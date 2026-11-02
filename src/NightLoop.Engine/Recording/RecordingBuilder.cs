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
    /// <summary>
    /// When false, plain mouse moves don't get their own blocks: each click glides from the last spot instead.
    /// Drags and camera turns (hidden or locked cursor) are always kept.
    /// </summary>
    public bool RecordMouseMoves { get; init; }
    /// <summary>The stop hotkey. Its modifier presses at the very end are dropped so they don't get replayed.</summary>
    public Hotkey? StopHotkey { get; init; }
    public int MaxBlocks { get; init; } = MacroLimits.MaxBlocks;
    /// <summary>
    /// The most JSON the recorded blocks may add. Recording into a macro that already has blocks passes what's left,
    /// so the macro stays under the size limit.
    /// </summary>
    public int MaxBytes { get; init; } = MacroLimits.MaxDocumentBytes * 9 / 10;
    /// <summary>Paths are simplified until no point is further than this from the line the replay follows.</summary>
    public double PathTolerancePx { get; init; } = 1.5;
}

public sealed record RecordingResult(List<Block> Blocks, bool Truncated);

/// <summary>
/// Turns captured events into timeline blocks (schema section 7, "Recording"). Pure logic, no Windows calls.
/// A quick press and release becomes one input.key / input.click; anything overlapping becomes separate
/// down and up blocks, so combos like Ctrl+C and holding W while clicking replay exactly.
/// Mouse motion becomes one block per continuous movement, with its path, instead of hundreds of tiny moves.
/// Motion while the cursor is hidden or pinned (a game's camera) is saved as relative motion, which is what the
/// game reads.
/// </summary>
public static class RecordingBuilder
{
    /// <summary>Movement samples further apart than this belong to separate movements.</summary>
    private const int BurstGapMs = 150;
    /// <summary>A movement with this much raw motion but the cursor barely moving is a pinned-cursor camera turn.</summary>
    private const int LockedMinRawPx = 40;
    private const double LockedMaxCursorShare = 0.15;
    /// <summary>How far back before its first sample a movement is assumed to start.</summary>
    private const int LeadInMs = 32;

    private sealed record Action(long Start, long End, Func<string, Block> Make, MultiClick? Click = null);

    private sealed class MultiClick(MouseButton button, int x, int y, bool game, int? moveMs)
    {
        public MouseButton Button { get; } = button;
        public int X { get; } = x;
        public int Y { get; } = y;
        public bool Game { get; } = game;
        public int? MoveMs { get; } = moveMs;
        public int Count { get; set; } = 1;
        public int HoldMs { get; set; }
    }

    public static RecordingResult Build(IReadOnlyList<RawInputEvent> events, RecordingOptions? options = null)
    {
        options ??= new RecordingOptions();
        var cleaned = Clean(events, options);
        var tolerance = options.PathTolerancePx;
        while (true)
        {
            var result = Emit(ToActions(cleaned, options, tolerance), options);
            // Long recordings with lots of movement: simplify paths harder until the macro fits the size limit.
            if (Size(result.Blocks) <= options.MaxBytes) return result;
            if (tolerance > 64) return TrimToFit(result.Blocks, options.MaxBytes);
            tolerance *= 2;
        }
    }

    private static int Size(List<Block> blocks) => MacroJson.ByteSize(new MacroDoc { Name = "x", Blocks = blocks });

    /// <summary>Still too big with the simplest paths: keep as many blocks from the start as fit.</summary>
    private static RecordingResult TrimToFit(List<Block> blocks, int maxBytes)
    {
        int lo = 0, hi = blocks.Count;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Size(blocks[..mid]) <= maxBytes) lo = mid;
            else hi = mid - 1;
        }
        return new RecordingResult(blocks[..lo], Truncated: true);
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
                // Keys Night-loop has no name for (media keys, some OEM keys) can't be saved in a macro.
                case RawInputKind.KeyDown or RawInputKind.KeyUp when KeyCatalog.NameOf(e.VirtualKey) is null: continue;
                case RawInputKind.KeyDown when !keys.Add(e.VirtualKey): continue;
                case RawInputKind.KeyUp when !keys.Remove(e.VirtualKey): continue;
                case RawInputKind.MouseDown when !buttons.Add(e.Button): continue;
                case RawInputKind.MouseUp when !buttons.Remove(e.Button): continue;
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

    private static List<Action> ToActions(List<RawInputEvent> ev, RecordingOptions o, double tolerance)
    {
        var actions = new List<Action>();
        var consumed = new bool[ev.Count];
        var held = new HashSet<MouseButton>();
        // Where the cursor was at the last event with a position (null until the first one).
        (int X, int Y, bool Game)? cursor = null;
        // False until the first block that puts the cursor somewhere: before that, where it is on replay is unknown.
        var placed = false;
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
                    // Moves during the press that get their own blocks (kept moves, drags, camera turns) need the
                    // press split into down and up around them.
                    var keepMoves = o.RecordMouseMoves || held.Count > 0;
                    if (up >= 0 && clean && !HasRelativeMotion(ev, i, up) && !(keepMoves && HasMoves(ev, i, up)))
                    {
                        consumed[up] = true;
                        MarkMoves(ev, consumed, i, up);
                        UpdateCursor(ev, i, up, ref cursor);
                        var hold = (int)(ev[up].TimeMs - e.TimeMs);
                        actions.Add(new(e.TimeMs, ev[up].TimeMs, id => Key(id, e.VirtualKey, "press", hold)));
                        break;
                    }
                    actions.Add(new(e.TimeMs, e.TimeMs, id => Key(id, e.VirtualKey, "down", null)));
                    break;
                }
                case RawInputKind.KeyUp:
                    actions.Add(new(e.TimeMs, e.TimeMs, id => Key(id, e.VirtualKey, "up", null)));
                    break;

                case RawInputKind.MouseDown:
                {
                    // When moves get their own blocks the cursor is already there; otherwise glide like the recording did.
                    var prevEnd = actions.Count > 0 ? actions[^1].End : (long?)null;
                    int? approach = o.RecordMouseMoves || held.Count > 0 ? 0 : ApproachMs(ev, i, cursor, e, prevEnd);
                    if (!placed)
                    {
                        // The first click glides from wherever the cursor is when the replay starts.
                        var room = (int)Math.Max(0, e.TimeMs - (prevEnd ?? ev[0].TimeMs));
                        approach = Math.Max(approach ?? 0, Math.Min(FirstGlideMs, room));
                        placed = true;
                    }
                    held.Add(e.Button);
                    var up = FindRelease(ev, i, out var clean);
                    if (up >= 0 && clean && ev[up].RelativeToGame == e.RelativeToGame && !HasRelativeMotion(ev, i, up))
                    {
                        var u = ev[up];
                        var hold = (int)(u.TimeMs - e.TimeMs);
                        var moved = Math.Abs(u.X - e.X) > o.ClickSlopPx || Math.Abs(u.Y - e.Y) > o.ClickSlopPx;
                        if (moved)
                        {
                            consumed[up] = true;
                            MarkMoves(ev, consumed, i, up);
                            held.Remove(e.Button);
                            var path = Simplify(DragPath(ev, i, up), tolerance);
                            actions.Add(new(e.TimeMs, u.TimeMs, id => Drag(id, e, u, hold, approach, path)));
                            cursor = (u.X, u.Y, u.RelativeToGame);
                            break;
                        }
                        if (hold <= o.ClickMaxHoldMs)
                        {
                            consumed[up] = true;
                            MarkMoves(ev, consumed, i, up);
                            held.Remove(e.Button);
                            var mc = new MultiClick(e.Button, e.X, e.Y, e.RelativeToGame, approach) { HoldMs = hold };
                            if (actions.Count > 0 && actions[^1].Click is { } prev && prev.Button == e.Button && prev.Count < MacroLimits.ClicksMax
                                && e.TimeMs - actions[^1].End <= o.MultiClickMs && prev.Game == e.RelativeToGame
                                && Math.Abs(prev.X - e.X) <= o.ClickSlopPx && Math.Abs(prev.Y - e.Y) <= o.ClickSlopPx)
                            {
                                prev.Count++;
                                actions[^1] = actions[^1] with { End = u.TimeMs };
                            }
                            else actions.Add(new(e.TimeMs, u.TimeMs, id => Click(id, mc, "press"), mc));
                            cursor = (e.X, e.Y, e.RelativeToGame);
                            break;
                        }
                    }
                    var down = new MultiClick(e.Button, e.X, e.Y, e.RelativeToGame, approach);
                    actions.Add(new(e.TimeMs, e.TimeMs, id => Click(id, down, "down")));
                    cursor = (e.X, e.Y, e.RelativeToGame);
                    break;
                }
                case RawInputKind.MouseUp:
                {
                    held.Remove(e.Button);
                    // The moves before it were replayed as blocks, so the cursor is already there.
                    var upClick = new MultiClick(e.Button, e.X, e.Y, e.RelativeToGame, 0);
                    actions.Add(new(e.TimeMs, e.TimeMs, id => Click(id, upClick, "up")));
                    cursor = (e.X, e.Y, e.RelativeToGame);
                    break;
                }

                case RawInputKind.MouseMove:
                {
                    // One block per continuous movement.
                    var end = i;
                    while (end + 1 < ev.Count && !consumed[end + 1] && ev[end + 1].Kind == RawInputKind.MouseMove
                           && ev[end + 1].TimeMs - ev[end].TimeMs <= BurstGapMs && ev[end + 1].RelativeToGame == e.RelativeToGame)
                        end++;
                    for (var k = i + 1; k <= end; k++) consumed[k] = true;

                    var prevEnd = Math.Max(lastTime, actions.Count > 0 ? actions[^1].End : e.TimeMs);
                    var start = i == 0 ? e.TimeMs : Math.Max(prevEnd, e.TimeMs - LeadInMs);
                    var last = ev[end];
                    var relative = IsRelative(ev, i, end);

                    if (relative)
                    {
                        var full = RelativePath(ev, i, end, start);
                        var (dx, dy, _) = full[^1];
                        var path = Simplify(full, tolerance);
                        if (dx != 0 || dy != 0)
                        {
                            var duration = (int)(last.TimeMs - start);
                            actions.Add(new(start, last.TimeMs, id => RelativeMove(id, dx, dy, duration, path)));
                        }
                    }
                    else if (o.RecordMouseMoves || held.Count > 0)
                    {
                        var from = cursor is { } c && c.Game == e.RelativeToGame ? (c.X, c.Y) : (e.X, e.Y);
                        var points = new List<(int X, int Y, int T)> { (from.Item1, from.Item2, 0) };
                        for (var k = i; k <= end; k++) points.Add((ev[k].X, ev[k].Y, (int)(ev[k].TimeMs - start)));
                        // Skip jiggles (a hand resting on the mouse while holding a button).
                        var spread = points.Max(p => Math.Max(Math.Abs(p.X - from.Item1), Math.Abs(p.Y - from.Item2)));
                        if (spread > 2)
                        {
                            placed = true;
                            var path = Simplify(points, tolerance);
                            var duration = (int)(last.TimeMs - start);
                            actions.Add(new(start, last.TimeMs, id => Move(id, last, duration, path)));
                        }
                    }
                    cursor = (last.X, last.Y, last.RelativeToGame);
                    lastTime = last.TimeMs;
                    continue;
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
    /// How long the replay should take to glide to a click: the last movement before it, capped by the time since the
    /// previous action. 0 when the mouse didn't move; null when we don't know where it was.
    /// </summary>
    private static int? ApproachMs(List<RawInputEvent> ev, int index, (int X, int Y, bool Game)? cursor, RawInputEvent target, long? prevEnd)
    {
        var e = ev[index];
        var limit = (int)Math.Max(0, e.TimeMs - (prevEnd ?? ev[0].TimeMs));
        var j = index - 1;
        if (j < 0 || ev[j].Kind != RawInputKind.MouseMove)
            return cursor is null ? null : 0; // no movement since the last action: the cursor is already there

        var last = j;
        while (j - 1 >= 0 && ev[j - 1].Kind == RawInputKind.MouseMove && ev[j].TimeMs - ev[j - 1].TimeMs <= BurstGapMs) j--;
        if (IsRelative(ev, j, last)) return 0; // a camera turn: replayed as its own block, the cursor didn't travel

        // Only the starting position: where the cursor is when the replay starts is anyone's guess, so glide from there.
        if (j == 0 && last == 0) return Math.Min(FirstGlideMs, limit);

        var ms = Math.Max(MinGlideMs, (int)(ev[last].TimeMs - ev[j].TimeMs) + LeadInMs);
        return Math.Clamp(Math.Min(ms, limit), 0, MacroLimits.MoveMaxMs);
    }

    private const int MinGlideMs = 60;
    private const int FirstGlideMs = 300;

    private static bool IsRelative(List<RawInputEvent> ev, int from, int to)
    {
        double raw = 0, cursor = 0;
        var hidden = 0;
        for (var k = from; k <= to; k++)
        {
            raw += Math.Sqrt((double)ev[k].Dx * ev[k].Dx + (double)ev[k].Dy * ev[k].Dy);
            if (k > from) cursor += Math.Sqrt(Math.Pow(ev[k].X - ev[k - 1].X, 2) + Math.Pow(ev[k].Y - ev[k - 1].Y, 2));
            if (ev[k].Hidden) hidden++;
        }
        if (raw < 1) return false;
        return hidden * 2 > to - from + 1 || (raw >= LockedMinRawPx && cursor < raw * LockedMaxCursorShare);
    }

    private static bool HasRelativeMotion(List<RawInputEvent> ev, int from, int to)
    {
        var k = from + 1;
        while (k < to)
        {
            if (ev[k].Kind != RawInputKind.MouseMove) { k++; continue; }
            var end = k;
            while (end + 1 < to && ev[end + 1].Kind == RawInputKind.MouseMove) end++;
            if (IsRelative(ev, k, end)) return true;
            k = end + 1;
        }
        return false;
    }

    private static bool HasMoves(List<RawInputEvent> ev, int from, int to)
    {
        for (var k = from + 1; k < to; k++)
            if (ev[k].Kind == RawInputKind.MouseMove) return true;
        return false;
    }

    private static void UpdateCursor(List<RawInputEvent> ev, int from, int to, ref (int X, int Y, bool Game)? cursor)
    {
        for (var k = from + 1; k < to; k++)
            if (ev[k].Kind == RawInputKind.MouseMove) cursor = (ev[k].X, ev[k].Y, ev[k].RelativeToGame);
    }

    /// <summary>The drag's path from the press point through each sample (times from the press).</summary>
    private static List<(int X, int Y, int T)> DragPath(List<RawInputEvent> ev, int down, int up)
    {
        var d = ev[down];
        var points = new List<(int X, int Y, int T)> { (d.X, d.Y, 0) };
        for (var k = down + 1; k < up; k++)
            if (ev[k].Kind == RawInputKind.MouseMove) points.Add((ev[k].X, ev[k].Y, (int)(ev[k].TimeMs - d.TimeMs)));
        points.Add((ev[up].X, ev[up].Y, (int)(ev[up].TimeMs - d.TimeMs)));
        return points;
    }

    /// <summary>Cumulative raw motion from 0 at <paramref name="start"/>, one point per sample.</summary>
    private static List<(int X, int Y, int T)> RelativePath(List<RawInputEvent> ev, int from, int to, long start)
    {
        var points = new List<(int X, int Y, int T)> { (0, 0, 0) };
        int x = 0, y = 0;
        for (var k = from; k <= to; k++)
        {
            x += ev[k].Dx;
            y += ev[k].Dy;
            points.Add((x, y, (int)Math.Max(0, ev[k].TimeMs - start)));
        }
        return points;
    }

    /// <summary>
    /// Ramer–Douglas–Peucker on the points (time is kept per point), then thinned to the block limit. The last point
    /// is the block's end and isn't stored in the path.
    /// </summary>
    internal static List<(int X, int Y, int T)> Simplify(List<(int X, int Y, int T)> points, double tolerance)
    {
        if (points.Count <= 2) return points.Count == 0 ? [] : [points[0]];
        while (true)
        {
            var keep = new bool[points.Count];
            keep[0] = keep[^1] = true;
            Rdp(points, 0, points.Count - 1, tolerance, keep);
            var kept = points.Where((_, i) => keep[i]).ToList();
            if (kept.Count - 1 <= MacroLimits.PathMaxPoints) return kept.Take(kept.Count - 1).ToList();
            tolerance *= 1.5;
        }
    }

    private static void Rdp(List<(int X, int Y, int T)> p, int first, int last, double tolerance, bool[] keep)
    {
        if (last - first < 2) return;
        double ax = p[first].X, ay = p[first].Y, bx = p[last].X, by = p[last].Y;
        var len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        var worst = -1.0;
        var index = -1;
        for (var i = first + 1; i < last; i++)
        {
            var d = len < 1e-9
                ? Math.Sqrt(Math.Pow(p[i].X - ax, 2) + Math.Pow(p[i].Y - ay, 2))
                : Math.Abs((bx - ax) * (ay - p[i].Y) - (ax - p[i].X) * (by - ay)) / len;
            if (d > worst) { worst = d; index = i; }
        }
        if (worst <= tolerance) return;
        keep[index] = true;
        Rdp(p, first, index, tolerance, keep);
        Rdp(p, index, last, tolerance, keep);
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
        // Gaps too short for their own wait add up here, so many quick inputs in a row keep their real pace.
        long carried = 0;
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
            var block = a.Make("");
            // A click that glides there takes its glide time out of the wait before it, so timing stays the same.
            var lead = block.Type is BlockTypes.Click or BlockTypes.Drag ? block.GetInt("moveMs", 0) : 0;
            if (prevEnd is { } pe)
            {
                var gap = a.Start - pe - lead + carried;
                carried = 0;
                while (gap > o.MinWaitMs)
                {
                    var ms = (int)Math.Min(gap, MacroLimits.WaitMaxMs);
                    if (!Add(Block.Create(Id(), BlockTypes.Wait, new JsonObject { ["ms"] = ms }))) break;
                    gap -= ms;
                }
                if (gap > 0) carried = gap;
            }
            if (truncated || !Add(a.Make(Id()))) break;
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
        if (c.MoveMs is { } m) p["moveMs"] = m;
        if (action == "press")
        {
            p["clicks"] = c.Count;
            p["holdMs"] = c.HoldMs;
        }
        else p["action"] = action;
        return Block.Create(id, BlockTypes.Click, p);
    }

    private static Block Drag(string id, RawInputEvent from, RawInputEvent to, int durationMs, int? moveMs, List<(int X, int Y, int T)> path)
    {
        var p = new JsonObject
        {
            ["from"] = new JsonObject { ["x"] = from.X, ["y"] = from.Y },
            ["to"] = new JsonObject { ["x"] = to.X, ["y"] = to.Y },
            ["button"] = MouseButtons.ToName(from.Button),
            ["relativeTo"] = from.RelativeToGame ? "gameWindow" : "screen",
            ["durationMs"] = durationMs,
        };
        if (moveMs is { } m) p["moveMs"] = m;
        if (path.Count > 1) p["path"] = PathJson(path);
        return Block.Create(id, BlockTypes.Drag, p);
    }

    private static Block Move(string id, RawInputEvent end, int durationMs, List<(int X, int Y, int T)> path)
    {
        var p = new JsonObject
        {
            ["x"] = end.X,
            ["y"] = end.Y,
            ["relativeTo"] = end.RelativeToGame ? "gameWindow" : "screen",
            ["durationMs"] = Math.Max(0, durationMs),
        };
        if (path.Count > 1) p["path"] = PathJson(path);
        return Block.Create(id, BlockTypes.Move, p);
    }

    private static Block RelativeMove(string id, int dx, int dy, int durationMs, List<(int X, int Y, int T)> path)
    {
        var p = new JsonObject
        {
            ["mode"] = "relative",
            ["x"] = dx,
            ["y"] = dy,
            ["durationMs"] = Math.Max(0, durationMs),
        };
        if (path.Count > 1) p["path"] = PathJson(path);
        return Block.Create(id, BlockTypes.Move, p);
    }

    private static JsonArray PathJson(List<(int X, int Y, int T)> path) =>
        new(path.Select(p => (JsonNode)new JsonArray(p.X, p.Y, p.T)).ToArray());
}
