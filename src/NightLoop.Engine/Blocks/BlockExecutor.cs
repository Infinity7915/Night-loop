using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Runtime;

namespace NightLoop.Engine.Blocks;

/// <summary>
/// Runs one "action" step. Control-flow blocks are handled by the runner itself. Returns false when a detect block
/// didn't find its target, so the runner can apply the block's "On fail".
/// </summary>
internal static class BlockExecutor
{
    public static async Task<bool> ExecuteAsync(Block b, RunContext ctx)
    {
        switch (b.Type)
        {
            // Mostly screen reading: these take the input lock only while they click or turn.
            case BlockTypes.FindClick:
                return await DetectBlocks.FindClickAsync(b, ctx);
            case BlockTypes.ScanTurn:
                return await DetectBlocks.ScanTurnAsync(b, ctx);
            case BlockTypes.CameraCenter:
                await DetectBlocks.CameraCenterAsync(b, ctx);
                return true;
        }

        if (BlockTypes.IsInput(b.Type))
        {
            await ctx.InputAsync(() => ExecuteInputAsync(b, ctx));
            return true;
        }

        switch (b.Type)
        {
            case BlockTypes.Wait:
                await ctx.WaitAsync(b.GetInt("ms", 0));
                return true;
            case BlockTypes.WaitRandom:
                var min = b.GetInt("minMs", 0);
                var max = b.GetInt("maxMs", min);
                await ctx.WaitAsync(ctx.Environment.Random.Next(min, max + 1), jitter: false);
                return true;
            case BlockTypes.WaitForKey:
                return await WaitForKeyAsync(b, ctx);
            case BlockTypes.Pause:
                PauseOthers(b, ctx);
                return true;
            case BlockTypes.Resume:
                foreach (var other in Targets(b, ctx)) other.Resume(fromStart: b.GetString("from") == "start");
                return true;

            case BlockTypes.WaitForText:
                return await DetectBlocks.WaitForTextAsync(b, ctx);
            case BlockTypes.WaitForImage:
                return await DetectBlocks.WaitForImageAsync(b, ctx);
            case BlockTypes.DetectPixelColor:
                return await DetectBlocks.WaitForPixelAsync(b, ctx);

            case BlockTypes.WindowFocus:
                WindowBlocks.Focus(b, ctx);
                return true;
            case BlockTypes.WindowResize:
                WindowBlocks.Resize(b, ctx);
                return true;
            case BlockTypes.WindowPosition:
                WindowBlocks.Position(b, ctx);
                return true;
            case BlockTypes.WaitForWindow:
                await WindowBlocks.WaitForAsync(b, ctx);
                return true;

            case BlockTypes.Notify:
                var kind = b.GetString("kind", "toast");
                if (kind is "toast" or "both") ctx.Environment.Notify?.Invoke(b.GetString("message", ""));
                if (kind is "sound" or "both") ctx.Environment.PlaySound?.Invoke("default");
                return true;
            case BlockTypes.PlaySound:
                ctx.Environment.PlaySound?.Invoke(b.GetString("sound", "default"));
                return true;
            case BlockTypes.Screenshot:
                var save = ctx.Environment.SaveScreenshot ?? throw new MacroFaultException("Screenshots aren't available here.");
                var image = ctx.Capture(ctx.AreaFor(b.Params["region"]));
                save(image, b.GetString("file") is { Length: > 0 } f ? f : "night-loop.png");
                return true;
            case BlockTypes.RunMacro:
                var run = ctx.Environment.RunMacro ?? throw new MacroFaultException("Running another macro isn't available here.");
                await run(b.GetString("macroName", ""), ctx.Token);
                ctx.DidWork = true;
                return true;

            case BlockTypes.CameraTurnUntilText or BlockTypes.CameraTurnUntilImage:
                throw new MacroFaultException("This older block was replaced by Scan & turn. Swap it for a Scan & turn block.");
            default:
                throw new MacroFaultException($"\"{b.Type}\" can't run in this version of Night-loop. Update the app.");
        }
    }

    private static Task ExecuteInputAsync(Block b, RunContext ctx) => b.Type switch
    {
        BlockTypes.Key => InputBlocks.KeyAsync(b, ctx),
        BlockTypes.Combo => InputBlocks.ComboAsync(b, ctx),
        BlockTypes.HoldKey => InputBlocks.HoldKeyAsync(b, ctx),
        BlockTypes.Click => InputBlocks.ClickAsync(b, ctx),
        BlockTypes.Move => InputBlocks.MoveAsync(b, ctx),
        BlockTypes.Drag => InputBlocks.DragAsync(b, ctx),
        BlockTypes.Scroll => InputBlocks.ScrollAsync(b, ctx),
        BlockTypes.Type => InputBlocks.TypeAsync(b, ctx),
        BlockTypes.CameraTurn => InputBlocks.CameraTurnAsync(b, ctx),
        BlockTypes.CameraZoom => InputBlocks.ZoomAsync(b, ctx),
        _ => throw new MacroFaultException("This older block was replaced by Scan & turn. Swap it for a Scan & turn block."),
    };

    /// <summary>timing.waitForKey: true once the key is down, false when the timeout (if any) passes first.</summary>
    private static async Task<bool> WaitForKeyAsync(Block b, RunContext ctx)
    {
        var isDown = ctx.Environment.IsKeyDown ?? throw new MacroFaultException("Waiting for a key isn't available here.");
        var name = b.GetString("key");
        if (name is null || !KeyCatalog.TryGetVirtualKey(name, out var vk)) throw new MacroFaultException($"Unknown key \"{name}\".");
        var timeout = b.GetInt("timeoutMs", 0);
        var waited = 0;
        while (!isDown(vk))
        {
            if (timeout > 0 && waited >= timeout) return false;
            await ctx.PauseExactAsync(15);
            waited += 15;
        }
        return true;
    }

    private static void PauseOthers(Block b, RunContext ctx)
    {
        if (b.GetString("mode") == "resume")
        {
            // Older macros used one block with a mode for both.
            foreach (var other in Targets(b, ctx)) other.Resume();
            return;
        }
        var now = b.GetString("when") == "now";
        foreach (var other in Targets(b, ctx))
        {
            other.Pause(immediate: now);
            if (b.GetBool("autoResume") == true) ctx.Run.ResumeWhenDone(other);
        }
    }

    /// <summary>The macros a Pause or Resume block means. "*" is every other running macro.</summary>
    private static IEnumerable<MacroRun> Targets(Block b, RunContext ctx)
    {
        var target = b.GetString("target", "");
        if (target == "*")
            return (ctx.Environment.ActiveRuns?.Invoke() ?? []).Where(r => !ReferenceEquals(r, ctx.Run));
        var other = ctx.Environment.FindRun?.Invoke(target);
        if (other is null || ReferenceEquals(other, ctx.Run))
        {
            ctx.Environment.Log?.Invoke($"No other running macro is named \"{target}\".");
            return [];
        }
        return [other];
    }
}
