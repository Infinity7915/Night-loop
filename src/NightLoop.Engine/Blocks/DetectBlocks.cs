using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Input;
using NightLoop.Engine.Runtime;
using NightLoop.Engine.Screen;

namespace NightLoop.Engine.Blocks;

/// <summary>
/// Blocks that look at the screen: Wait for text / image / pixel, Find &amp; click, Scan &amp; turn and Center on target.
/// Each returns false when it didn't find what it was looking for, and the runner applies the block's "On fail".
/// </summary>
internal static class DetectBlocks
{
    private const int ScanSettleMs = 150;
    private const int CenterMaxSteps = 25;
    private const int CenterMaxMovePx = 400;

    /// <summary>Waits until the point has the color, or with <paramref name="gone"/> until it no longer has it.</summary>
    public static async Task<bool> WaitForPixelAsync(Block b, RunContext ctx, bool gone = false)
    {
        if (!Rgb.TryParse(b.GetString("color"), out var color)) throw new MacroFaultException("Pick a color like #FF0000.");
        var p = ctx.ToScreen(b.GetInt("x", 0), b.GetInt("y", 0), b.GetString("relativeTo"), jitter: false);
        var tolerance = b.GetInt("tolerance", 10);
        var timeout = b.GetInt("timeoutMs", 5000);
        var waited = 0;
        while (true)
        {
            var px = ctx.Capture(new ScreenRect(p.X, p.Y, 1, 1))[0, 0];
            if (px.IsNear(color, tolerance) != gone) return true;
            if (timeout > 0 && waited >= timeout) return false;
            await ctx.PauseExactAsync(50);
            waited += 50;
        }
    }

    public static async Task<bool> WaitForTextAsync(Block b, RunContext ctx) =>
        await Vision.WaitForAsync(ctx, Text(b), b.GetInt("timeoutMs", 10_000)) is not null;

    public static async Task<bool> WaitForImageAsync(Block b, RunContext ctx) =>
        await Vision.WaitForAsync(ctx, Picture(b), b.GetInt("timeoutMs", 10_000)) is not null;

    public static Task<bool> WaitForImageGoneAsync(Block b, RunContext ctx) =>
        Vision.WaitForGoneAsync(ctx, Picture(b), b.GetInt("timeoutMs", 10_000));

    public static async Task<bool> FindClickAsync(Block b, RunContext ctx)
    {
        var what = Target(b);
        var found = await Vision.WaitForAsync(ctx, what, b.GetInt("timeoutMs", 0));
        if (found is not { } p) return false;
        ctx.LastFound = new FoundTarget(what.Kind, what.Value, what.ImageData, what.Tolerance, what.Region, p, what.ConfidencePct, what.SizeTolerancePct);
        await ClickFoundAsync(b, ctx, p);
        return true;
    }

    /// <summary>Glides to what was found and clicks it the block's number of times (1–3).</summary>
    private static Task ClickFoundAsync(Block b, RunContext ctx, (int X, int Y) p)
    {
        MouseButtons.TryParse(b.GetString("button"), out var button);
        var clicks = Math.Clamp(b.GetInt("clicks", 1), MacroLimits.ClicksMin, MacroLimits.ClicksMax);
        return ctx.InputAsync(async () =>
        {
            await InputBlocks.GlideForClickAsync(ctx, ctx.ToScreen(p.X, p.Y, null), null);
            await InputBlocks.PressAsync(ctx, button, clicks);
        });
    }

    /// <summary>Clicks where the cursor already is, without moving the mouse (moving it would turn the camera).</summary>
    private static Task ClickInPlaceAsync(Block b, RunContext ctx)
    {
        MouseButtons.TryParse(b.GetString("button"), out var button);
        var clicks = Math.Clamp(b.GetInt("clicks", 1), MacroLimits.ClicksMin, MacroLimits.ClicksMax);
        return ctx.InputAsync(() => InputBlocks.PressAsync(ctx, button, clicks));
    }

    /// <summary>Looks, turns one step, looks again… until found or out of turns.</summary>
    public static async Task<bool> ScanTurnAsync(Block b, RunContext ctx)
    {
        var what = Target(b);
        var step = b.GetInt("stepPx", 120) * (b.GetString("direction") == "left" ? -1 : 1);
        var tries = b.GetInt("maxTurns", 12);
        var method = TurnMethod(b, ctx);

        for (var i = 0; i <= tries; i++)
        {
            if (await Vision.FindAsync(ctx, what) is { } p)
            {
                // Turning has stopped. Look once more after the view settles, so the click lands where the target is now.
                await ctx.PauseExactAsync(ScanSettleMs);
                if (await Vision.FindAsync(ctx, what) is { } settled) p = settled;
                ctx.LastFound = new FoundTarget(what.Kind, what.Value, what.ImageData, what.Tolerance, what.Region, p, what.ConfidencePct, what.SizeTolerancePct);
                switch (b.GetString("whenFound", "click"))
                {
                    case "click" when method == "mouseMove":
                        // The camera follows the mouse here, so gliding to the target would turn the view away from
                        // it. Turn the target into the middle instead, then click without moving the mouse.
                        await CenterAsync(ctx, ctx.LastFound, b.GetInt("tolerancePx", 40), method);
                        await ClickInPlaceAsync(b, ctx);
                        break;
                    case "click":
                        await ClickFoundAsync(b, ctx, p);
                        break;
                    case "center":
                        await CenterAsync(ctx, ctx.LastFound, b.GetInt("tolerancePx", 40), method);
                        break;
                }
                return true;
            }
            if (i == tries) break;
            await ctx.InputAsync(() => InputBlocks.TurnAsync(ctx, method, step, 0, InputBlocks.DefaultTurnMs(step)));
            await ctx.PauseExactAsync(ScanSettleMs);
        }
        return false;
    }

    /// <summary>camera.center: turns until the last thing found is in the middle of the window.</summary>
    public static async Task CameraCenterAsync(Block b, RunContext ctx)
    {
        if (ctx.LastFound is not { } last)
        {
            ctx.Environment.Log?.Invoke("Center on target: nothing was found yet. Put a Find & click or Scan & turn before it.");
            return;
        }
        await CenterAsync(ctx, last, b.GetInt("tolerancePx", 40), TurnMethod(b, ctx));
    }

    /// <summary>
    /// Turns toward the target, measuring how far it moved on screen for each pixel of turn, so it settles in a few
    /// steps whatever the game's mouse sensitivity is.
    /// </summary>
    private static async Task CenterAsync(RunContext ctx, FoundTarget target, int tolerance, string method)
    {
        var what = new LookFor(target.Kind, target.Value, target.ImageData, target.Tolerance, target.ConfidencePct, Region: null, SizeTolerancePct: target.SizeTolerancePct);
        var view = ctx.AreaFor(null).Center;
        var point = target.Point;
        double ratioX = 1, ratioY = 1;

        for (var i = 0; i < CenterMaxSteps; i++)
        {
            var dx = point.X - view.X;
            var dy = point.Y - view.Y;
            if (Math.Abs(dx) <= tolerance && Math.Abs(dy) <= tolerance) return;

            var sendX = (int)Math.Clamp(dx / ratioX * 0.8, -CenterMaxMovePx, CenterMaxMovePx);
            var sendY = (int)Math.Clamp(dy / ratioY * 0.8, -CenterMaxMovePx, CenterMaxMovePx);
            await ctx.InputAsync(() => InputBlocks.TurnAsync(ctx, method, sendX, sendY, InputBlocks.DefaultTurnMs(Math.Max(Math.Abs(sendX), Math.Abs(sendY)))));
            await ctx.PauseExactAsync(ScanSettleMs);

            if (await Vision.FindAsync(ctx, what) is not { } next)
            {
                ctx.Environment.Log?.Invoke("Center on target: lost sight of the target while turning.");
                return;
            }
            // How many screen pixels the target moved per pixel of turn (it moves the opposite way to the turn).
            if (sendX != 0 && Math.Abs(point.X - next.X) > 2) ratioX = Math.Clamp((point.X - next.X) / (double)sendX, 0.05, 20);
            if (sendY != 0 && Math.Abs(point.Y - next.Y) > 2) ratioY = Math.Clamp((point.Y - next.Y) / (double)sendY, 0.05, 20);
            point = next;
            ctx.LastFound = target with { Point = next };
        }
    }

    /// <summary>The block's turn method, else right-drag for Roblox (its camera needs it) and mouse move for other games.</summary>
    private static string TurnMethod(Block b, RunContext ctx) =>
        b.GetString("method")
        ?? (ctx.Settings.TargetExe?.Contains("Roblox", StringComparison.OrdinalIgnoreCase) == true ? "rightDrag" : "mouseMove");

    private static LookFor Text(Block b) => DetectBlocksLook.Text(b);
    private static LookFor Picture(Block b) => DetectBlocksLook.Picture(b);
    private static LookFor Target(Block b) => DetectBlocksLook.Target(b);
}

/// <summary>What each detect block looks for, shared with the editor's test look.</summary>
internal static class DetectBlocksLook
{
    public static LookFor Text(Block b) => new("text", b.GetString("text", ""),
        Contains: b.GetString("match") == "contains", MatchCase: b.GetBool("matchCase") == true, Region: b.Params["region"]);

    public static LookFor Picture(Block b) => new("image", b.GetString("image", ""), ImageData: b.GetString("imageData"),
        ConfidencePct: b.GetInt("confidencePct", 90), Region: b.Params["region"], SizeTolerancePct: b.GetInt("sizeTolerancePct", 0));

    public static LookFor Target(Block b)
    {
        var kind = b.GetString("target", "text");
        return new LookFor(kind, b.GetString("value", ""), ImageData: b.GetString("imageData"),
            Tolerance: b.GetInt("tolerance", 10), ConfidencePct: b.GetInt("confidencePct", 90),
            Contains: true, MatchCase: false, Region: b.Params["region"], SizeTolerancePct: b.GetInt("sizeTolerancePct", 0));
    }
}
