using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Input;

namespace NightLoop.Engine.Recording;

public sealed class RecordingOptions
{
    /// <summary>Gaps longer than this become timing.wait blocks (schema: 50 ms).</summary>
    public int MinWaitMs { get; init; } = 50;
    /// <summary>A press and release closer than this many pixels is a click; further apart is a drag.</summary>
    public int ClickSlopPx { get; init; } = 4;
    /// <summary>Holding a button longer than this without moving records separate down and up blocks.</summary>
    public int ClickMaxHoldMs { get; init; } = 300;
    /// <summary>Clicks this close together in time and place merge into a double or triple click.</summary>
    public int MultiClickMs { get; init; } = 500;
    /// <summary>Wheel ticks this close together merge into one scroll block.</summary>
    public int ScrollMergeMs { get; init; } = 100;
    /// <summary>When false, plain mouse moves are dropped (clicks and drags still carry their positions).</summary>
    public bool RecordMouseMoves { get; init; }
    /// <summary>The stop hotkey. Its modifier presses at the very end are dropped so they don't get replayed.</summary>
    public Hotkey? StopHotkey { get; init; }
    public int MaxBlocks { get; init; } = MacroLimits.MaxBlocks;
}

public sealed record RecordingResult(List<Block> Blocks, bool Truncated);

/// <summary>
/// Turns captured events into timeline blocks (schema section 7, "Recording"). Pure logic, no Windows calls.
/// A quick press and release becomes one input.key / input.click; anything overlapping becomes separate
/// down and up blocks, so combos like Ctrl+C and holding W while clicking replay exactly.
/// </summary>
public static class RecordingBuilder
{
    private sealed record Action(long Start, long End, Func<string, Block> Make, MultiClick? Click = null);

    private sealed class MultiClick(MouseButton button, int x, int y, bool game)
    {
        public MouseButton Button { get; } = button;
        public int X { get; } = x;
        public int Y { get; } = y;
        public bool Game { get; } = game;
        public int Count { get; set; } = 1;
        public int HoldMs { get; set; }
    }

    public static RecordingResult Build(IReadOnlyList<RawInputEvent> events, RecordingOptions? options = null)
    {
        options ??= new RecordingOptions();
        var cleaned = Clean(events, options);
        var actions = ToActions(cleaned, options);
        return Emit(actions, options);
    }

    /// <summary>Drops key auto-repeat, releases that were pressed before recording started, and the stop hotkey's modifiers.</summary>
    private static List<RawInputEvent> Clean(IReadOnlyList<RawInputEvent> events, RecordingOptions options)
    {
        var keys = new HashSet<int>();
        var buttons = new HashSet<MouseButton>();
        var result = new List<RawInputEvent>(events.Count);

        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case RawInputKind.KeyDown when !keys.Add(e.VirtualKey): continue;
                case RawInputKind.KeyUp when !keys.Remove(e.VirtualKey): continue;
                case RawInputKind.MouseDown when !buttons.Add(e.Button): continue;
                case RawInputKind.MouseUp when !buttons.Remove(e.Button): continue;
                case RawInputKind.MouseMove when !options.RecordMouseMoves && buttons.Count == 0: continue;
            }
            result.Add(e);
        }

        if (options.StopHotkey is { } hk)
        {
            // Modifiers still held at the end were pressed for the stop hotkey.
            for (var i = result.Count - 1; i >= 0; i--)
            {
                var e = result[i];
                if (e.Kind == RawInputKind.MouseMove) continue;
                if (e.Kind != RawInputKind.KeyDown || !keys.Contains(e.VirtualKey) || !IsModifierOf(hk, e.VirtualKey)) break;
                keys.Remove(e.VirtualKey);
                result.RemoveAt(i);
            }
        }
        return result;
    }

    private static bool IsModifierOf(Hotkey hk, int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => hk.Modifiers.HasFlag(HotkeyModifiers.Shift),
        0x11 or 0xA2 or 0xA3 => hk.Modifiers.HasFlag(HotkeyModifiers.Ctrl),
        0x12 or 0xA4 or 0xA5 => hk.Modifiers.HasFlag(HotkeyModifiers.Alt),
        0x5B or 0x5C => hk.Modifiers.HasFlag(HotkeyModifiers.Win),
        _ => false,
    };

    private static List<Action> ToActions(List<RawInputEvent> ev, RecordingOptions o)
    {
        var actions = new List<Action>();
        var consumed = new bool[ev.Count];
        long lastTime = ev.Count > 0 ? ev[0].TimeMs : 0;

        for (var i = 0; i < ev.Count; i++)
        {
            if (consumed[i]) continue;
            var e = ev[i];

            switch (e.Kind)
            {
                case RawInputKind.KeyDown:
                {
                    var up = FindRelease(ev, i, out var clean);
                    if (up >= 0 && clean)
                    {
                        consumed[up] = true;
                        MarkMoves(ev, consumed, i, up);
                        var hold = (int)(ev[up].TimeMs - e.TimeMs);
                        actions.Add(new(e.TimeMs, ev[up].TimeMs, id => Key(id, e.VirtualKey, "press", hold)));
                    }
                    else actions.Add(new(e.TimeMs, e.TimeMs, id => Key(id, e.VirtualKey, "down", null)));
                    break;
                }
                case RawInputKind.KeyUp:
                    actions.Add(new(e.TimeMs, e.TimeMs, id => Key(id, e.VirtualKey, "up", null)));
                    break;

                case RawInputKind.MouseDown:
                {
                    var up = FindRelease(ev, i, out var clean);
                    if (up >= 0 && clean && ev[up].RelativeToGame == e.RelativeToGame)
                    {
                        var u = ev[up];
                        var hold = (int)(u.TimeMs - e.TimeMs);
                        var moved = Math.Abs(u.X - e.X) > o.ClickSlopPx || Math.Abs(u.Y - e.Y) > o.ClickSlopPx;
                        if (moved)
                        {
                            consumed[up] = true;
                            MarkMoves(ev, consumed, i, up);
                            actions.Add(new(e.TimeMs, u.TimeMs, id => Drag(id, e, u, hold)));
                            break;
                        }
                        if (hold <= o.ClickMaxHoldMs)
                        {
                            consumed[up] = true;
                            MarkMoves(ev, consumed, i, up);
                            var mc = new MultiClick(e.Button, e.X, e.Y, e.RelativeToGame) { HoldMs = hold };
                            if (actions.Count > 0 && actions[^1].Click is { } prev && prev.Button == e.Button && prev.Count < MacroLimits.ClicksMax
                                && e.TimeMs - actions[^1].End <= o.MultiClickMs && prev.Game == e.RelativeToGame
                                && Math.Abs(prev.X - e.X) <= o.ClickSlopPx && Math.Abs(prev.Y - e.Y) <= o.ClickSlopPx)
                            {
                                prev.Count++;
                                actions[^1] = actions[^1] with { End = u.TimeMs };
                            }
                            else actions.Add(new(e.TimeMs, u.TimeMs, id => Click(id, mc, "press"), mc));
                            break;
                        }
                    }
                    var down = new MultiClick(e.Button, e.X, e.Y, e.RelativeToGame);
                    actions.Add(new(e.TimeMs, e.TimeMs, id => Click(id, down, "down")));
                    break;
                }
                case RawInputKind.MouseUp:
                {
                    var upClick = new MultiClick(e.Button, e.X, e.Y, e.RelativeToGame);
                    actions.Add(new(e.TimeMs, e.TimeMs, id => Click(id, upClick, "up")));
                    break;
                }

                case RawInputKind.MouseMove:
                {
                    var start = Math.Max(lastTime, actions.Count > 0 ? actions[^1].End : e.TimeMs);
                    var duration = (int)(e.TimeMs - start);
                    actions.Add(new(start, e.TimeMs, id => Move(id, e, duration)));
                    break;
                }

                case RawInputKind.Wheel:
                {
                    var delta = e.WheelDelta;
                    var end = e.TimeMs;
                    for (var j = i + 1; j < ev.Count; j++)
                    {
                        if (ev[j].Kind == RawInputKind.MouseMove) continue;
                        if (ev[j].Kind != RawInputKind.Wheel || ev[j].TimeMs - end > o.ScrollMergeMs) break;
                        delta += ev[j].WheelDelta;
                        end = ev[j].TimeMs;
                        consumed[j] = true;
                    }
                    var notches = (int)Math.Round(delta / 120.0);
                    while (notches != 0)
                    {
                        var chunk = Math.Clamp(notches, MacroLimits.ScrollMin, MacroLimits.ScrollMax);
                        notches -= chunk;
                        actions.Add(new(e.TimeMs, end, id => Block.Create(id, BlockTypes.Scroll, new JsonObject { ["amount"] = chunk })));
                    }
                    break;
                }
            }
            lastTime = e.TimeMs;
        }
        return actions;
    }

    /// <summary>
    /// Finds the release that matches a press. "Clean" means nothing but mouse moves happened in between,
    /// so the pair can be one block.
    /// </summary>
    private static int FindRelease(List<RawInputEvent> ev, int downIndex, out bool clean)
    {
        var d = ev[downIndex];
        clean = true;
        for (var j = downIndex + 1; j < ev.Count; j++)
        {
            var e = ev[j];
            var isRelease = d.Kind == RawInputKind.KeyDown
                ? e.Kind == RawInputKind.KeyUp && e.VirtualKey == d.VirtualKey
                : e.Kind == RawInputKind.MouseUp && e.Button == d.Button;
            if (isRelease) return j;
            if (e.Kind != RawInputKind.MouseMove) clean = false;
        }
        clean = false;
        return -1;
    }

    private static void MarkMoves(List<RawInputEvent> ev, bool[] consumed, int from, int to)
    {
        for (var k = from + 1; k < to; k++)
            if (ev[k].Kind == RawInputKind.MouseMove) consumed[k] = true;
    }

    private static RecordingResult Emit(List<Action> actions, RecordingOptions o)
    {
        var blocks = new List<Block>();
        var truncated = false;
        long? prevEnd = null;
        var next = 1;
        string Id() => $"b{next++}";

        bool Add(Block b)
        {
            if (blocks.Count >= o.MaxBlocks) { truncated = true; return false; }
            blocks.Add(b);
            return true;
        }

        foreach (var a in actions)
        {
            if (prevEnd is { } pe)
            {
                var gap = a.Start - pe;
                while (gap > o.MinWaitMs)
                {
                    var ms = (int)Math.Min(gap, MacroLimits.WaitMaxMs);
                    if (!Add(Block.Create(Id(), BlockTypes.Wait, new JsonObject { ["ms"] = ms }))) break;
                    gap -= ms;
                }
            }
            if (!Add(a.Make(Id()))) break;
            prevEnd = Math.Max(prevEnd ?? a.End, a.End);
        }
        return new RecordingResult(blocks, truncated);
    }

    private static Block Key(string id, int vk, string action, int? holdMs)
    {
        var p = new JsonObject { ["key"] = KeyCatalog.NameOf(vk) ?? $"0x{vk:X2}", ["action"] = action };
        if (holdMs is { } h) p["holdMs"] = h;
        return Block.Create(id, BlockTypes.Key, p);
    }

    private static Block Click(string id, MultiClick c, string action)
    {
        var p = new JsonObject
        {
            ["button"] = MouseButtons.ToName(c.Button),
            ["x"] = c.X,
            ["y"] = c.Y,
            ["relativeTo"] = c.Game ? "gameWindow" : "screen",
        };
        if (action == "press")
        {
            p["clicks"] = c.Count;
            p["holdMs"] = c.HoldMs;
        }
        else p["action"] = action;
        return Block.Create(id, BlockTypes.Click, p);
    }

    private static Block Drag(string id, RawInputEvent from, RawInputEvent to, int durationMs) =>
        Block.Create(id, BlockTypes.Drag, new JsonObject
        {
            ["from"] = new JsonObject { ["x"] = from.X, ["y"] = from.Y },
            ["to"] = new JsonObject { ["x"] = to.X, ["y"] = to.Y },
            ["button"] = MouseButtons.ToName(from.Button),
            ["relativeTo"] = from.RelativeToGame ? "gameWindow" : "screen",
            ["durationMs"] = durationMs,
        });

    private static Block Move(string id, RawInputEvent e, int durationMs) =>
        Block.Create(id, BlockTypes.Move, new JsonObject
        {
            ["x"] = e.X,
            ["y"] = e.Y,
            ["relativeTo"] = e.RelativeToGame ? "gameWindow" : "screen",
            ["durationMs"] = Math.Max(0, durationMs),
        });
}
