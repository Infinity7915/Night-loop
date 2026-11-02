using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Input;
using NightLoop.Engine.Runtime;

namespace NightLoop.Engine.Blocks;

/// <summary>input.* and camera.turn blocks.</summary>
internal static class InputBlocks
{
    private const int DefaultKeyHoldMs = 30;
    private const int DefaultClickHoldMs = 20;
    private const int MultiClickGapMs = 60;
    private const int MoveStepMs = 10;

    public static async Task KeyAsync(Block b, RunContext ctx)
    {
        var name = b.GetString("key")!;
        if (!KeyCatalog.TryGetVirtualKey(name, out var vk)) throw new MacroFaultException($"Unknown key \"{name}\".");
        ctx.DidWork = true;

        switch (b.GetString("action", "press"))
        {
            case "down": ctx.Input.KeyDown(vk); break;
            case "up": ctx.Input.KeyUp(vk); break;
            default:
                ctx.Input.KeyDown(vk);
                try { await ctx.WaitAsync(b.GetInt("holdMs", DefaultKeyHoldMs)); }
                finally { ctx.Input.KeyUp(vk); }
                break;
        }
    }

    public static async Task ClickAsync(Block b, RunContext ctx)
    {
        MouseButtons.TryParse(b.GetString("button"), out var button);
        ctx.DidWork = true;

        if (b.GetInt("x") is { } x && b.GetInt("y") is { } y)
        {
            var p = ctx.ToScreen(x, y, b.GetString("relativeTo"));
            ctx.Input.MoveTo(p.X, p.Y);
        }

        switch (b.GetString("action", "press"))
        {
            case "down": ctx.Input.MouseDown(button); return;
            case "up": ctx.Input.MouseUp(button); return;
        }

        var clicks = Math.Clamp(b.GetInt("clicks", 1), 1, 3);
        var hold = b.GetInt("holdMs", DefaultClickHoldMs);
        for (var i = 0; i < clicks; i++)
        {
            if (i > 0) await ctx.WaitAsync(MultiClickGapMs, jitter: false);
            ctx.Input.MouseDown(button);
            try { await ctx.WaitAsync(hold, jitter: false); }
            finally { ctx.Input.MouseUp(button); }
        }
    }

    public static Task MoveAsync(Block b, RunContext ctx)
    {
        var to = ctx.ToScreen(b.GetInt("x", 0), b.GetInt("y", 0), b.GetString("relativeTo"));
        return GlideAsync(ctx, to, b.GetInt("durationMs", 0));
    }

    public static async Task DragAsync(Block b, RunContext ctx)
    {
        MouseButtons.TryParse(b.GetString("button"), out var button);
        var relativeTo = b.GetString("relativeTo");
        var from = b.GetPoint("from")!.Value;
        var to = b.GetPoint("to")!.Value;
        var start = ctx.ToScreen(from.X, from.Y, relativeTo);
        var end = ctx.ToScreen(to.X, to.Y, relativeTo);

        ctx.Input.MoveTo(start.X, start.Y);
        ctx.Input.MouseDown(button);
        try { await GlideAsync(ctx, end, b.GetInt("durationMs", 300)); }
        finally { ctx.Input.MouseUp(button); }
    }

    public static Task ScrollAsync(Block b, RunContext ctx)
    {
        ctx.DidWork = true;
        ctx.Input.Scroll(b.GetInt("amount", 0));
        return Task.CompletedTask;
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

    /// <summary>Relative mouse movement spread over the duration, which is what games read for the camera.</summary>
    public static async Task CameraTurnAsync(Block b, RunContext ctx)
    {
        var amount = b.GetInt("amountPx", 0);
        var (dx, dy) = b.GetString("direction") switch
        {
            "left" => (-amount, 0),
            "up" => (0, -amount),
            "down" => (0, amount),
            _ => (amount, 0),
        };
        var duration = b.GetInt("durationMs", 0);
        var steps = Math.Max(1, duration / MoveStepMs);
        int sentX = 0, sentY = 0;
        for (var i = 1; i <= steps; i++)
        {
            var targetX = dx * i / steps;
            var targetY = dy * i / steps;
            ctx.Input.MoveBy(targetX - sentX, targetY - sentY);
            sentX = targetX;
            sentY = targetY;
            ctx.DidWork = true;
            if (i < steps) await ctx.WaitAsync(MoveStepMs, jitter: false);
        }
    }

    /// <summary>Moves the cursor to a point along an ease-in-out curve.</summary>
    private static async Task GlideAsync(RunContext ctx, (int X, int Y) to, int durationMs)
    {
        ctx.DidWork = true;
        if (durationMs <= MoveStepMs)
        {
            ctx.Input.MoveTo(to.X, to.Y);
            if (durationMs > 0) await ctx.WaitAsync(durationMs, jitter: false);
            return;
        }

        var from = ctx.Input.CursorPosition;
        var steps = durationMs / MoveStepMs;
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var eased = t * t * (3 - 2 * t);
            ctx.Input.MoveTo(
                (int)Math.Round(from.X + (to.X - from.X) * eased),
                (int)Math.Round(from.Y + (to.Y - from.Y) * eased));
            await ctx.WaitAsync(MoveStepMs, jitter: false);
        }
    }
}
