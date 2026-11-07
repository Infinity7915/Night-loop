using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Input;
using NightLoop.Engine.Runtime;

namespace NightLoop.Engine.Blocks;

/// <summary>Android blocks: taps, swipes, system buttons and apps.</summary>
internal static class TouchBlocks
{
    private const int PollMs = 100;
    /// <summary>Gap between the taps of a double or triple tap.</summary>
    internal const int TapGapMs = 80;

    private static ITouchSender Touch(RunContext ctx) =>
        ctx.Environment.Touch ?? throw new MacroFaultException("Touch blocks only run in Night-loop for Android.");

    public static async Task TapAsync(Block b, RunContext ctx)
    {
        var touch = Touch(ctx);
        var count = Math.Clamp(b.GetInt("count", 1), MacroLimits.ClicksMin, MacroLimits.ClicksMax);
        var hold = b.GetInt("holdMs", 50);
        for (var i = 0; i < count; i++)
        {
            if (i > 0) await ctx.PauseExactAsync(TapGapMs);
            await touch.TapAsync(b.GetInt("x", 0), b.GetInt("y", 0), hold, ctx.Token);
        }
        ctx.DidWork = true;
    }

    public static async Task LongPressAsync(Block b, RunContext ctx)
    {
        await Touch(ctx).TapAsync(b.GetInt("x", 0), b.GetInt("y", 0), b.GetInt("holdMs", 600), ctx.Token);
        ctx.DidWork = true;
    }

    public static async Task SwipeAsync(Block b, RunContext ctx)
    {
        var from = b.GetPoint("from") ?? throw new MacroFaultException("Swipe needs a start spot.");
        var to = b.GetPoint("to") ?? throw new MacroFaultException("Swipe needs an end spot.");
        await Touch(ctx).SwipeAsync(from.X, from.Y, to.X, to.Y, b.GetInt("durationMs", 300), ctx.Token);
        ctx.DidWork = true;
    }

    public static Task ButtonAsync(Block b, RunContext ctx)
    {
        Touch(ctx).PressButton(b.GetString("button", "back"));
        ctx.DidWork = true;
        return Task.CompletedTask;
    }

    public static async Task OpenAppAsync(Block b, RunContext ctx)
    {
        var touch = Touch(ctx);
        var package = b.GetString("package", "");
        ctx.DidWork = true;
        if (!touch.OpenApp(package)) throw new MacroFaultException($"{package} isn't installed.");
        if (b.GetBool("wait") == false) return;
        await WaitForAppAsync(ctx, touch, package, b.GetInt("timeoutMs", 30_000));
    }

    public static Task WaitForAppAsync(Block b, RunContext ctx) =>
        WaitForAppAsync(ctx, Touch(ctx), b.GetString("package", ""), b.GetInt("timeoutMs", 0));

    private static async Task WaitForAppAsync(RunContext ctx, ITouchSender touch, string package, int timeoutMs)
    {
        var waited = 0;
        ctx.Run.SetState(RunState.WaitingForWindow);
        try
        {
            while (touch.ForegroundApp != package)
            {
                if (timeoutMs > 0 && waited >= timeoutMs)
                    throw new MacroFaultException($"{package} didn't come to the front within {timeoutMs / 1000.0:0.#} s.");
                await ctx.Environment.Delay.DelayAsync(PollMs, ctx.Token);
                waited += PollMs;
                ctx.DidWork = true;
            }
        }
        finally
        {
            ctx.Run.SetState(RunState.Running);
        }
    }
}
