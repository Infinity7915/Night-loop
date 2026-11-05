using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Input;
using NightLoop.Engine.Runtime;

namespace NightLoop.Engine.Blocks;

/// <summary>input.* and camera.turn / camera.zoom blocks.</summary>
internal static class InputBlocks
{
    private const int DefaultKeyHoldMs = 30;
    private const int DefaultClickHoldMs = 20;
    private const int DefaultComboHoldMs = 40;
    private const int ComboGapMs = 15;
    private const int MultiClickGapMs = 60;
    private const int WheelGapMs = 25;
    private const int ZoomGapMs = 40;
    /// <summary>Time between cursor updates while gliding. 8 ms is smoother than a 120 Hz screen.</summary>
    internal const int MoveStepMs = 8;
    /// <summary>Arrow-key camera turns hold the key this long per pixel of "amount".</summary>
    internal const double ArrowMsPerPx = 2.0;
    /// <summary>Gives games a moment to see the button go down before the mouse moves (drags, right-drag turns).</summary>
    private const int SettleMs = 25;
    /// <summary>Rest on the target after gliding there, before clicking.</summary>
    private const int HoverMs = 15;

    public static async Task KeyAsync(Block b, RunContext ctx)
    {
        var vk = Vk(b.GetString("key"));
        ctx.DidWork = true;

        switch (b.GetString("action", "press"))
        {
            case "down": ctx.Input.KeyDown(vk); break;
            case "up": ctx.Input.KeyUp(vk); break;
            default:
                ctx.Input.KeyDown(vk);
                try { await ctx.WaitAsync(b.GetInt("holdMs", DefaultKeyHoldMs), scale: false); }
                finally { ctx.Input.KeyUp(vk); }
                break;
        }
    }

    /// <summary>Presses every key in order, holds, then lets go in reverse (Ctrl + Shift + E).</summary>
    public static async Task ComboAsync(Block b, RunContext ctx)
    {
        var keys = (b.GetString("keys") ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Vk).Select(vk => vk switch { 0x10 => 0xA0, 0x11 => 0xA2, 0x12 => 0xA4, _ => vk }).ToArray(); // "Ctrl" = left Ctrl
        ctx.DidWork = true;
        var pressed = new List<int>();
        try
        {
            foreach (var vk in keys)
            {
                if (pressed.Count > 0) await ctx.PauseExactAsync(ComboGapMs);
                ctx.Input.KeyDown(vk);
                pressed.Add(vk);
            }
            await ctx.WaitAsync(b.GetInt("holdMs", DefaultComboHoldMs), scale: false);
        }
        finally
        {
            for (var i = pressed.Count - 1; i >= 0; i--) ctx.Input.KeyUp(pressed[i]);
        }
    }

    public static async Task HoldKeyAsync(Block b, RunContext ctx)
    {
        var vk = Vk(b.GetString("key"));
        ctx.DidWork = true;
        ctx.Input.KeyDown(vk);
        try { await ctx.WaitAsync(b.GetInt("holdMs", 1000), scale: false); }
        finally { ctx.Input.KeyUp(vk); }
    }

    public static async Task ClickAsync(Block b, RunContext ctx)
    {
        MouseButtons.TryParse(b.GetString("button"), out var button);
        ctx.DidWork = true;

        if (b.GetInt("x") is { } x && b.GetInt("y") is { } y)
            await GlideForClickAsync(ctx, ctx.ToScreen(x, y, b.GetString("relativeTo")), b.GetInt("moveMs"));

        switch (b.GetString("action", "press"))
        {
            case "down": ctx.Input.MouseDown(button); return;
            case "up": ctx.Input.MouseUp(button); return;
        }
        await PressAsync(ctx, button, Math.Clamp(b.GetInt("clicks", 1), 1, 3), b.GetInt("holdMs", DefaultClickHoldMs));
    }

    /// <summary>
    /// Glides to a click point and rests a moment before the press, so the game sees the cursor arrive (hover) first.
    /// The rest is part of <paramref name="moveMs"/>, so recorded timing stays the same.
    /// </summary>
    internal static async Task GlideForClickAsync(RunContext ctx, (int X, int Y) to, int? moveMs)
    {
        var (cx, cy) = ctx.Input.CursorPosition;
        if (Math.Abs(cx - to.X) < 2 && Math.Abs(cy - to.Y) < 2)
        {
            ctx.Input.MoveTo(to.X, to.Y);
            if (moveMs is > 0) await ctx.WaitAsync(moveMs.Value, jitter: false);
            return;
        }
        var glide = moveMs ?? AutoGlideMs(Math.Sqrt(Math.Pow(to.X - cx, 2) + Math.Pow(to.Y - cy, 2))) + HoverMs;
        await GlideToAsync(ctx, to, Math.Max(0, glide - HoverMs));
        await ctx.WaitAsync(Math.Min(HoverMs, glide), jitter: false);
    }

    /// <summary>Clicks where the cursor is now.</summary>
    internal static async Task PressAsync(RunContext ctx, MouseButton button, int clicks = 1, int holdMs = DefaultClickHoldMs)
    {
        for (var i = 0; i < clicks; i++)
        {
            if (i > 0) await ctx.PauseExactAsync(MultiClickGapMs);
            ctx.Input.MouseDown(button);
            try { await ctx.PauseExactAsync(holdMs); }
            finally { ctx.Input.MouseUp(button); }
        }
        ctx.DidWork = true;
    }

    public static async Task MoveAsync(Block b, RunContext ctx)
    {
        var x = b.GetInt("x", 0);
        var y = b.GetInt("y", 0);
        var duration = b.GetInt("durationMs", 0);
        var path = ReadPath(b.Params["path"]);

        if (b.GetString("mode") == "relative")
        {
            await RelativeAsync(ctx, path ?? [(0, 0, 0), (x, y, duration)], x, y, duration);
            return;
        }

        var relativeTo = b.GetString("relativeTo");
        var to = ctx.ToScreen(x, y, relativeTo);
        if (path is { Count: > 1 })
        {
            var screen = path.Select(p => { var s = ctx.ToScreen(p.X, p.Y, relativeTo, jitter: false); return (s.X, s.Y, p.T); }).ToList();
            await FollowAsync(ctx, screen, to, duration);
        }
        else await GlideToAsync(ctx, to, duration);
    }

    public static async Task DragAsync(Block b, RunContext ctx)
    {
        MouseButtons.TryParse(b.GetString("button"), out var button);
        var relativeTo = b.GetString("relativeTo");
        var from = b.GetPoint("from")!.Value;
        var to = b.GetPoint("to")!.Value;
        var start = ctx.ToScreen(from.X, from.Y, relativeTo);
        var end = ctx.ToScreen(to.X, to.Y, relativeTo);
        var duration = b.GetInt("durationMs", 300);
        var path = ReadPath(b.Params["path"]);

        await GlideForClickAsync(ctx, start, b.GetInt("moveMs"));
        ctx.Input.MouseDown(button);
        try
        {
            await ctx.PauseExactAsync(SettleMs);
            if (path is { Count: > 1 })
            {
                var screen = path.Select(p => { var s = ctx.ToScreen(p.X, p.Y, relativeTo, jitter: false); return (s.X, s.Y, p.T); }).ToList();
                await FollowAsync(ctx, screen, end, duration);
            }
            else await GlideToAsync(ctx, end, duration, curve: false);
            await ctx.PauseExactAsync(SettleMs);
        }
        finally { ctx.Input.MouseUp(button); }
    }

    /// <summary>One notch at a time with a short gap: games often ignore one big wheel jump.</summary>
    public static async Task ScrollAsync(Block b, RunContext ctx)
    {
        var amount = b.GetInt("amount", 0);
        var notches = b.GetString("direction") switch
        {
            "up" => Math.Abs(amount),
            "down" => -Math.Abs(amount),
            _ => amount,
        };
        await WheelAsync(ctx, notches, WheelGapMs);
    }

    public static async Task TypeAsync(Block b, RunContext ctx)
    {
        var delay = b.GetInt("delayMs", 10);
        var first = true;
        foreach (var c in b.GetString("text", ""))
        {
            if (!first && delay > 0) await ctx.WaitAsync(delay);
            first = false;
            ctx.DidWork = true;
            ctx.Input.TypeChar(c);
        }
    }

    /// <summary>Turns the camera by right-dragging, holding an arrow key, or plain relative mouse movement.</summary>
    public static Task CameraTurnAsync(Block b, RunContext ctx)
    {
        var amount = b.GetInt("amountPx", 0);
        var (dx, dy) = b.GetString("direction") switch
        {
            "left" => (-amount, 0),
            "up" => (0, -amount),
            "down" => (0, amount),
            _ => (amount, 0),
        };
        var method = b.GetString("method") ?? "mouseMove";
        var duration = b.GetInt("durationMs") ?? DefaultTurnMs(amount);
        return TurnAsync(ctx, method, dx, dy, duration);
    }

    internal static int DefaultTurnMs(int amountPx) => Math.Clamp(Math.Abs(amountPx), 80, 2000);

    internal static async Task TurnAsync(RunContext ctx, string method, int dx, int dy, int durationMs)
    {
        ctx.DidWork = true;
        switch (method)
        {
            case "arrowKeys":
            {
                // Hold Left/Right for the horizontal part, then Up/Down for the vertical part.
                if (dx != 0) await HoldArrowAsync(ctx, dx > 0 ? 0x27 : 0x25, dx);
                if (dy != 0) await HoldArrowAsync(ctx, dy > 0 ? 0x28 : 0x26, dy);
                break;
            }
            case "rightDrag":
                ctx.Input.MouseDown(MouseButton.Right);
                try
                {
                    await ctx.PauseExactAsync(SettleMs);
                    await RelativeAsync(ctx, [(0, 0, 0), (dx, dy, durationMs)], dx, dy, durationMs);
                    await ctx.PauseExactAsync(SettleMs);
                }
                finally { ctx.Input.MouseUp(MouseButton.Right); }
                break;
            default:
                await RelativeAsync(ctx, [(0, 0, 0), (dx, dy, durationMs)], dx, dy, durationMs);
                break;
        }
    }

    private static async Task HoldArrowAsync(RunContext ctx, int vk, int amount)
    {
        ctx.Input.KeyDown(vk);
        try { await ctx.WaitAsync((int)Math.Round(Math.Abs(amount) * ArrowMsPerPx), jitter: false); }
        finally { ctx.Input.KeyUp(vk); }
    }

    public static Task ZoomAsync(Block b, RunContext ctx)
    {
        var steps = Math.Max(1, b.GetInt("steps", 1));
        return WheelAsync(ctx, b.GetString("direction") == "in" ? steps : -steps, ZoomGapMs);
    }

    private static async Task WheelAsync(RunContext ctx, int notches, int gapMs)
    {
        ctx.DidWork = true;
        var sign = Math.Sign(notches);
        for (var i = 0; i < Math.Abs(notches); i++)
        {
            if (i > 0) await ctx.WaitAsync(gapMs, jitter: false);
            ctx.Input.Scroll(sign);
        }
    }

    /// <summary>
    /// Glides the cursor to a point instead of jumping, so hover effects fire and it looks like a hand moved it.
    /// <paramref name="ms"/> null picks a natural time from the distance; 0 jumps.
    /// </summary>
    internal static async Task GlideToAsync(RunContext ctx, (int X, int Y) to, int? ms, bool curve = true)
    {
        ctx.DidWork = true;
        var from = ctx.Input.CursorPosition;
        var dist = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
        var duration = ms ?? AutoGlideMs(dist);
        var scaled = ctx.Scale(duration);
        if (dist < 2 || scaled <= MoveStepMs)
        {
            ctx.Input.MoveTo(to.X, to.Y);
            if (scaled > 0) await ctx.PauseExactAsync(scaled);
            return;
        }

        // A slight bend (humanize only), so glides aren't ruler-straight.
        double bendX = 0, bendY = 0;
        if (curve && ctx.Humanize.Enabled && dist > 40)
        {
            var bend = (ctx.Environment.Random.NextDouble() * 2 - 1) * dist * 0.08;
            bendX = -(to.Y - from.Y) / dist * bend;
            bendY = (to.X - from.X) / dist * bend;
        }

        var steps = scaled / MoveStepMs;
        var pace = ctx.Pace(scaled, steps);
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var e = t * t * (3 - 2 * t);
            var arc = 4 * t * (1 - t);
            if (i == steps) ctx.Input.MoveTo(to.X, to.Y);
            else ctx.Input.MoveTo(
                (int)Math.Round(from.X + (to.X - from.X) * e + bendX * arc),
                (int)Math.Round(from.Y + (to.Y - from.Y) * e + bendY * arc));
            await pace.NextAsync();
        }
    }

    /// <summary>About how long a person takes: 80 ms for a short hop up to 450 ms across a big screen.</summary>
    internal static int AutoGlideMs(double distancePx) => distancePx < 2 ? 0 : (int)Math.Clamp(80 + distancePx * 0.22, 80, 450);

    /// <summary>Follows a recorded path (screen points with times), ending exactly at <paramref name="end"/>.</summary>
    private static async Task FollowAsync(RunContext ctx, IReadOnlyList<(int X, int Y, int T)> path, (int X, int Y) end, int durationMs)
    {
        ctx.DidWork = true;
        // Get to the start of the path first if the cursor is somewhere else (a jump when it's only a hop away).
        var (cx, cy) = ctx.Input.CursorPosition;
        var away = Math.Max(Math.Abs(cx - path[0].X), Math.Abs(cy - path[0].Y));
        await GlideToAsync(ctx, (path[0].X, path[0].Y), away < 40 ? 0 : null);
        var total = Math.Max(durationMs, path[^1].T);
        var scaled = ctx.Scale(total);
        var steps = Math.Max(1, scaled / MoveStepMs);
        var pace = ctx.Pace(scaled, steps);
        var points = new List<(int X, int Y, int T)>(path) { (end.X, end.Y, total) };
        var seg = 0;
        for (var i = 1; i <= steps; i++)
        {
            var t = total * (double)i / steps;
            while (seg < points.Count - 2 && points[seg + 1].T < t) seg++;
            var a = points[seg];
            var b = points[seg + 1];
            var f = b.T > a.T ? Math.Clamp((t - a.T) / (b.T - a.T), 0, 1) : 1;
            ctx.Input.MoveTo((int)Math.Round(a.X + (b.X - a.X) * f), (int)Math.Round(a.Y + (b.Y - a.Y) * f));
            await pace.NextAsync();
        }
        ctx.Input.MoveTo(end.X, end.Y);
    }

    /// <summary>
    /// Relative mouse movement along cumulative offsets [dx, dy, ms]: what games read for the camera when the cursor is
    /// hidden or locked. Ends with exactly (<paramref name="totalX"/>, <paramref name="totalY"/>) sent.
    /// </summary>
    internal static async Task RelativeAsync(RunContext ctx, IReadOnlyList<(int X, int Y, int T)> path, int totalX, int totalY, int durationMs)
    {
        ctx.DidWork = true;
        var points = path.ToList();
        if (points.Count == 0 || points[0].T != 0) points.Insert(0, (0, 0, 0));
        var total = Math.Max(durationMs, points[^1].T);
        if (points[^1].X != totalX || points[^1].Y != totalY || points[^1].T != total) points.Add((totalX, totalY, total));

        var scaled = ctx.Scale(total);
        var steps = Math.Max(1, scaled / MoveStepMs);
        var pace = ctx.Pace(scaled, steps);
        int sentX = 0, sentY = 0, seg = 0;
        for (var i = 1; i <= steps; i++)
        {
            int tx, ty;
            if (i == steps) (tx, ty) = (totalX, totalY);
            else
            {
                var t = total * (double)i / steps;
                while (seg < points.Count - 2 && points[seg + 1].T < t) seg++;
                var a = points[seg];
                var b = points[seg + 1];
                var f = b.T > a.T ? Math.Clamp((t - a.T) / (b.T - a.T), 0, 1) : 1;
                tx = (int)Math.Round(a.X + (b.X - a.X) * f);
                ty = (int)Math.Round(a.Y + (b.Y - a.Y) * f);
            }
            if (tx != sentX || ty != sentY) ctx.Input.MoveBy(tx - sentX, ty - sentY);
            sentX = tx;
            sentY = ty;
            await pace.NextAsync();
        }
    }

    internal static List<(int X, int Y, int T)>? ReadPath(JsonNode? node)
    {
        if (node is not JsonArray a) return null;
        var list = new List<(int X, int Y, int T)>(a.Count);
        foreach (var item in a)
        {
            if (item is JsonArray p && p.Count == 3 && BlockParams.AsInt(p[0]) is { } px && BlockParams.AsInt(p[1]) is { } py
                && BlockParams.AsInt(p[2]) is { } pt)
                list.Add((px, py, pt));
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>Keys and mouse buttons held down.</summary>
    internal readonly record struct Held(IReadOnlyList<int>? Keys, IReadOnlyList<MouseButton>? Buttons)
    {
        public bool Any => Keys is { Count: > 0 } || Buttons is { Count: > 0 };
    }

    /// <summary>What's still held after these blocks run: keys and buttons with a "down" block and no "up" after it.</summary>
    internal static Held HeldAfter(IEnumerable<Block> blocks)
    {
        var keys = new List<int>();
        var buttons = new List<MouseButton>();
        foreach (var b in blocks)
        {
            if (!b.Enabled) continue;
            var action = b.GetString("action");
            if (action is not ("down" or "up")) continue;
            if (b.Type == BlockTypes.Key && KeyCatalog.TryGetVirtualKey(b.GetString("key") ?? "", out var vk))
            {
                keys.Remove(vk);
                if (action == "down") keys.Add(vk);
            }
            else if (b.Type == BlockTypes.Click && MouseButtons.TryParse(b.GetString("button"), out var button))
            {
                buttons.Remove(button);
                if (action == "down") buttons.Add(button);
            }
        }
        return new Held(keys, buttons);
    }

    private static int Vk(string? name) =>
        name is not null && KeyCatalog.TryGetVirtualKey(name, out var vk) ? vk : throw new MacroFaultException($"Unknown key \"{name}\".");
}
