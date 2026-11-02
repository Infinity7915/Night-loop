using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Runtime;

namespace NightLoop.Engine.Blocks;

/// <summary>Runs one "action" step. Control-flow blocks are handled by the runner itself.</summary>
internal static class BlockExecutor
{
    public static async Task ExecuteAsync(Block b, RunContext ctx)
    {
        if (BlockTypes.IsInput(b.Type))
        {
            await WaitForTargetAsync(ctx);
            await ctx.Environment.InputLock.WaitAsync(ctx.Token);
            try { await ExecuteInputAsync(b, ctx); }
            finally { ctx.Environment.InputLock.Release(); }
            return;
        }

        switch (b.Type)
        {
            case BlockTypes.Wait:
                await ctx.WaitAsync(b.GetInt("ms", 0));
                break;
            case BlockTypes.WaitRandom:
                var min = b.GetInt("minMs", 0);
                var max = b.GetInt("maxMs", min);
                await ctx.WaitAsync(ctx.Environment.Random.Next(min, max + 1), jitter: false);
                break;
            case BlockTypes.Pause:
                var target = b.GetString("target", "");
                var other = ctx.Environment.FindRun?.Invoke(target);
                if (other is null) ctx.Environment.Log?.Invoke($"Pause: no running macro named \"{target}\".");
                else if (b.GetString("mode") == "resume") other.Resume();
                else other.Pause();
                break;
            case BlockTypes.WindowResize:
                WithWindow(ctx, (w, exe) => w.Resize(exe, b.GetInt("width", 0), b.GetInt("height", 0)), "resize");
                break;
            case BlockTypes.WindowPosition:
                WithWindow(ctx, (w, exe) => w.MoveTo(exe, b.GetInt("x", 0), b.GetInt("y", 0)), "move");
                break;
            case BlockTypes.WindowFocus:
                WithWindow(ctx, (w, exe) => w.Focus(exe), "focus");
                break;
            case BlockTypes.Notify:
                ctx.Environment.Notify?.Invoke(b.GetString("message", ""));
                break;
            case BlockTypes.PlaySound:
                ctx.Environment.PlaySound?.Invoke(b.GetString("sound", "default"));
                break;
            case BlockTypes.RunMacro:
                var run = ctx.Environment.RunMacro ?? throw new MacroFaultException("Running another macro isn't available here.");
                await run(b.GetString("macroName", ""), ctx.Token);
                ctx.DidWork = true;
                break;
            case BlockTypes.WaitForText or BlockTypes.WaitForImage:
                throw new MacroFaultException("Waiting for text or images needs screen reading, which isn't built yet.");
            default:
                throw new MacroFaultException($"\"{b.Type}\" can't run yet.");
        }
    }

    private static Task ExecuteInputAsync(Block b, RunContext ctx) => b.Type switch
    {
        BlockTypes.Key => InputBlocks.KeyAsync(b, ctx),
        BlockTypes.Click => InputBlocks.ClickAsync(b, ctx),
        BlockTypes.Move => InputBlocks.MoveAsync(b, ctx),
        BlockTypes.Drag => InputBlocks.DragAsync(b, ctx),
        BlockTypes.Scroll => InputBlocks.ScrollAsync(b, ctx),
        BlockTypes.Type => InputBlocks.TypeAsync(b, ctx),
        BlockTypes.CameraTurn => InputBlocks.CameraTurnAsync(b, ctx),
        _ => throw new MacroFaultException("Turning until text or an image appears needs screen reading, which isn't built yet."),
    };

    /// <summary>Target lock: never type into another app. Waits while the game isn't in front.</summary>
    private static async Task WaitForTargetAsync(RunContext ctx)
    {
        if (ctx.Settings.TargetExe is not { } exe || ctx.Environment.Windows is not { } windows) return;
        if (windows.IsForeground(exe)) return;

        ctx.Input.ReleaseAll();
        ctx.Run.SetState(RunState.WaitingForWindow);
        while (!windows.IsForeground(exe))
            await ctx.Environment.Delay.DelayAsync(ctx.Settings.WindowPollMs, ctx.Token);
        ctx.Run.SetState(RunState.Running);
    }

    private static void WithWindow(RunContext ctx, Func<Window.IWindowService, string, bool> action, string verb)
    {
        if (ctx.Settings.TargetExe is not { } exe || ctx.Environment.Windows is not { } windows)
        {
            ctx.Environment.Log?.Invoke($"Skipped window {verb}: no target game.");
            return;
        }
        if (!action(windows, exe))
            ctx.Environment.Log?.Invoke($"Couldn't {verb} the {exe} window (is it fullscreen or closed?).");
    }
}
