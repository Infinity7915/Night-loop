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
    private const int CenterFirstMovePx = 150;

    /// <summary>Waits until the point has the color, or with <paramref name="gone"/> until it no longer has it.</summary>
    public static async Task<bool> WaitForPixelAsync(Block b, RunContext ctx, bool gone = false)
    {
        if (!Rgb.TryParse(b.GetString("color"), out var color)) throw new MacroFaultException("Pick a color like #FF0000.");
        var p = ctx.ToScreen(b.GetInt("x", 0), b.GetInt("y", 0), b.GetString("relativeTo"), jitter: false);
        var tolerance = b.GetInt("tolerance", 10);
        var timeout = b.GetInt("timeoutMs", 5000);
        var waited = 0;
        var hits = 0;
        while (true)
        {
            var px = ctx.Capture(new ScreenRect(p.X, p.Y, 1, 1))[0, 0];
            // Two looks in a row must agree, so a flicker (or a frame from before the screen changed) doesn't count.
            hits = px.IsNear(color, tolerance) != gone ? hits + 1 : 0;
            if (hits >= 2) return gone || await AfterFoundAsync(b, ctx, p);
            if (timeout > 0 && waited >= timeout) return false;
            await ctx.PauseExactAsync(50);
            waited += 50;
        }
    }

    // A find only counts once a second look agrees (confirm), so a stale frame or a flicker can't end the wait early.
    public static async Task<bool> WaitForTextAsync(Block b, RunContext ctx) =>
        await Vision.WaitForAsync(ctx, Text(b), b.GetInt("timeoutMs", 10_000), confirm: true) is { } p && await AfterFoundAsync(b, ctx, p);

    public static async Task<bool> WaitForImageAsync(Block b, RunContext ctx) =>
        await Vision.WaitForAsync(ctx, Picture(b), b.GetInt("timeoutMs", 10_000), confirm: true) is { } p && await AfterFoundAsync(b, ctx, p);

    /// <summary>"Tap when found": taps (Android) or clicks (PC) where the block found its target. Always true.</summary>
    private static async Task<bool> AfterFoundAsync(Block b, RunContext ctx, (int X, int Y) p)
    {
        if (b.GetString("whenFound", "none") is not ("tap" or "click")) return true;
        if (ctx.Environment.Touch is { } touch)
        {
            await touch.TapAsync(p.X, p.Y, 50, ctx.Token);
            ctx.DidWork = true;
        }
        else
        {
            await ctx.InputAsync(async () =>
            {
                await InputBlocks.GlideForClickAsync(ctx, ctx.ToScreen(p.X, p.Y, null), null);
                await InputBlocks.PressAsync(ctx, MouseButton.Left, 1);
            });
        }
        return true;
    }

    /// <summary>touch.swipeScan: look; not there yet → swipe (turns the camera) and look again, up to maxTurns swipes.</summary>
    public static async Task<bool> SwipeScanAsync(Block b, RunContext ctx)
    {
        var touch = TouchBlocks.Touch(ctx);
        var from = b.GetPoint("from") ?? throw new MacroFaultException("Swipe and scan needs a start spot.");
        var to = b.GetPoint("to") ?? throw new MacroFaultException("Swipe and scan needs an end spot.");
        var what = Look(b);
        var tries = b.GetInt("maxTurns", 12);
        for (var i = 0; i <= tries; i++)
        {
            if (await Vision.FindAsync(ctx, what) is { } seen && await Vision.ConfirmAsync(ctx, what, seen, ScanSettleMs) is { } p)
            {
                ctx.LastFound = new FoundTarget(what.Kind, what.Value, what.ImageData, what.Tolerance, what.Region, p, what.ConfidencePct, what.SizeTolerancePct, what.MatchColor, what.ClickX, what.ClickY, what.BrightnessPct);
                return await AfterFoundAsync(b, ctx, p);
            }
            if (i == tries) break;
            await touch.SwipeAsync(from.X, from.Y, to.X, to.Y, b.GetInt("durationMs", 300), ctx.Token);
            ctx.DidWork = true;
            await ctx.PauseExactAsync(250); // let the camera stop moving before looking
        }
        return false;
    }

    /// <summary>
    /// touch.walkTo: holds the joystick and walks at the target, steering toward wherever it is on screen (above the
    /// middle = straight ahead, left = ahead and left…), until it is within tolerancePx of the middle of the screen,
    /// or the block's check is true. False when the time runs out first.
    /// </summary>
    public static async Task<bool> WalkToAsync(Block b, RunContext ctx)
    {
        var touch = TouchBlocks.Touch(ctx);
        var from = b.GetPoint("from") ?? throw new MacroFaultException("Walk to needs the joystick's middle.");
        var to = b.GetPoint("to") ?? throw new MacroFaultException("Walk to needs a spot to push the joystick to.");
        var what = Look(b);
        var condition = b.GetObject("condition");
        var near = b.GetInt("tolerancePx", 200);
        var timeout = b.GetInt("timeoutMs", 30_000);
        var radius = Math.Max(10, Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2)));
        var forward = Math.Atan2(to.Y - from.Y, to.X - from.X);
        var screen = ctx.Environment.Screen?.Desktop ?? (0, 0, 0, 0);
        var (midX, midY) = (screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
        var angle = forward;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        (int X, int Y)? arrivedAt = null;
        var timedOut = false;

        await touch.SteerAsync(from.X, from.Y, async () =>
        {
            ctx.Token.ThrowIfCancellationRequested();
            if (timeout > 0 && clock.ElapsedMilliseconds >= timeout)
            {
                timedOut = true;
                return null;
            }
            var seen = await Vision.FindAsync(ctx, what);
            if (condition is not null && await ctx.Environment.Conditions.EvaluateAsync(condition, ctx))
            {
                arrivedAt = seen ?? (midX, midY);
                return null;
            }
            if (seen is { } p)
            {
                var (dx, dy) = (p.X - midX, p.Y - midY);
                if (condition is null && Math.Sqrt(dx * dx + dy * dy) <= near)
                {
                    arrivedAt = p;
                    return null;
                }
                // Straight above the middle = forward; turn the push by the target's angle off that line.
                angle = forward + Math.Atan2(dx, -dy);
            }
            // Not seen right now: keep walking the way we were going.
            return ((int)Math.Round(from.X + Math.Cos(angle) * radius), (int)Math.Round(from.Y + Math.Sin(angle) * radius));
        }, ctx.Token);
        ctx.DidWork = true;
        if (timedOut || arrivedAt is not { } at) return false;
        return await AfterFoundAsync(b, ctx, at);
    }

    /// <summary>What Swipe and scan / Walk to look for: a picture (default) or text.</summary>
    internal static LookFor Look(Block b) => b.GetString("target", "image") == "text"
        ? DetectBlocksLook.Text(b) with { Value = b.GetString("value", "") , Contains = b.GetString("match", "contains") == "contains" }
        : DetectBlocksLook.Picture(b);

    public static Task<bool> WaitForTextGoneAsync(Block b, RunContext ctx) =>
        Vision.WaitForGoneAsync(ctx, Text(b), b.GetInt("timeoutMs", 10_000));

    public static Task<bool> WaitForImageGoneAsync(Block b, RunContext ctx) =>
        Vision.WaitForGoneAsync(ctx, Picture(b), b.GetInt("timeoutMs", 10_000));

    public static async Task<bool> FindClickAsync(Block b, RunContext ctx)
    {
        var what = Target(b);
        var found = await Vision.WaitForAsync(ctx, what, b.GetInt("timeoutMs", 0), confirm: true);
        if (found is not { } p) return false;
        ctx.LastFound = new FoundTarget(what.Kind, what.Value, what.ImageData, what.Tolerance, what.Region, p, what.ConfidencePct, what.SizeTolerancePct, what.MatchColor, what.ClickX, what.ClickY, what.BrightnessPct);
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
            // Turning has stopped. Only act on it when a second look after the view settles still sees it in the same
            // place: a single look can catch the camera still moving or a look-alike for a moment, and clicking that
            // clicks before the target is really there.
            if (await Vision.FindAsync(ctx, what) is { } seen && await Vision.ConfirmAsync(ctx, what, seen, ScanSettleMs) is { } p)
            {
                ctx.LastFound = new FoundTarget(what.Kind, what.Value, what.ImageData, what.Tolerance, what.Region, p, what.ConfidencePct, what.SizeTolerancePct, what.MatchColor, what.ClickX, what.ClickY, what.BrightnessPct);
                switch (b.GetString("whenFound", "click"))
                {
                    case "click" when method == "mouseMove":
                        // The camera follows the mouse here, so gliding to the target would turn the view away from
                        // it. Turn the target into the middle (sideways only), then click without moving the mouse.
                        var tolerance = b.GetInt("tolerancePx", 40);
                        var (ok, at, ratio) = await CenterAsync(ctx, ctx.LastFound, tolerance, method, tilt: false);
                        // Lost it while turning to it: don't click blind, keep scanning.
                        if (!ok) continue;
                        await ClickAimedAsync(b, ctx, at, ratio, tolerance, method);
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
    /// Puts the target in the middle of the screen in separate steps, never turning both ways at once: first it turns
    /// sideways until the target is centered left to right, and only then (when <paramref name="tilt"/> is on and the
    /// method isn't arrow keys) tilts
    /// up or down until it is centered top to bottom. Each step measures how far the target moved on screen for each
    /// pixel of turn, so it settles in a few turns whatever the game's mouse sensitivity is. Returns where the target
    /// is now and the measured sideways screen pixels per pixel of turn. Ok is false when it lost sight of the target.
    /// </summary>
    private static async Task<(bool Ok, (int X, int Y) Point, double Ratio)> CenterAsync(RunContext ctx, FoundTarget target, int tolerance, string method, bool tilt = true)
    {
        var what = new LookFor(target.Kind, target.Value, target.ImageData, target.Tolerance, target.ConfidencePct, Region: null, SizeTolerancePct: target.SizeTolerancePct, MatchColor: target.MatchColor,
            ClickX: target.ClickX, ClickY: target.ClickY, BrightnessPct: target.BrightnessPct);
        var view = ctx.AreaFor(null).Center;
        var (ok, point, ratio) = await CenterAxisAsync(ctx, what, target, target.Point, view, tolerance, method, vertical: false, guess: null);
        // Sideways sensitivity is a good first guess for up and down. Arrow keys only turn sideways: in most games
        // (Roblox too) Up and Down walk instead of tilting the view.
        if (ok && tilt && method != "arrowKeys")
            (ok, point, _) = await CenterAxisAsync(ctx, what, target, point, view, tolerance, method, vertical: true, guess: ratio);
        return (ok, point, ratio ?? 1);
    }

    /// <summary>One direction of <see cref="CenterAsync"/>: only sideways turns, or only up and down. Ok is false when the target was lost.</summary>
    private static async Task<(bool Ok, (int X, int Y) Point, double? Ratio)> CenterAxisAsync(RunContext ctx, LookFor what, FoundTarget target,
        (int X, int Y) point, (int X, int Y) view, int tolerance, string method, bool vertical, double? guess)
    {
        double? ratio = null;
        for (var i = 0; i < CenterMaxSteps; i++)
        {
            var off = vertical ? point.Y - view.Y : point.X - view.X;
            if (Math.Abs(off) <= tolerance) break;

            // Until this direction's sensitivity is measured, turn carefully: part of the way on a guess, or a small
            // safe turn to measure it.
            var send = ratio is { } r
                ? (int)Math.Clamp(off / r * 0.85, -CenterMaxMovePx, CenterMaxMovePx)
                : guess is { } g
                    ? (int)Math.Clamp(off / g * 0.6, -CenterFirstMovePx, CenterFirstMovePx)
                    : (int)Math.Clamp(off * 0.4, -CenterFirstMovePx, CenterFirstMovePx);
            if (send == 0) send = Math.Sign(off);
            // A short arrow tap is held for a minimum time, so it turns at least that much.
            var turned = method == "arrowKeys" ? Math.Sign(send) * Math.Max(Math.Abs(send), InputBlocks.ArrowMinPx) : send;
            var (sx, sy) = vertical ? (0, send) : (send, 0);
            await ctx.InputAsync(() => InputBlocks.TurnAsync(ctx, method, sx, sy, InputBlocks.DefaultTurnMs(send)));
            await ctx.PauseExactAsync(ScanSettleMs);

            if (await Vision.FindAsync(ctx, what) is not { } next)
            {
                ctx.Environment.Log?.Invoke("Center on target: lost sight of the target while turning.");
                return (false, point, ratio);
            }
            // How many screen pixels the target moved per pixel of turn (it moves the opposite way to the turn).
            // A reading that went the wrong way (the target itself moved) is ignored.
            var moved = vertical ? point.Y - next.Y : point.X - next.X;
            if (Math.Abs(moved) > 2 && Math.Sign(moved) == Math.Sign(send))
                ratio = Math.Clamp(moved / (double)turned, 0.05, 20);
            point = next;
            ctx.LastFound = target with { Point = next };
        }
        return (true, point, ratio);
    }

    /// <summary>
    /// Clicks a centered target in a game whose camera follows the mouse. When the target is above or below the
    /// middle, it tilts just enough to aim, clicks, and tilts back by the same amount, so the view ends up level
    /// where it was.
    /// </summary>
    private static async Task ClickAimedAsync(Block b, RunContext ctx, (int X, int Y) at, double ratio, int tolerance, string method)
    {
        var dy = at.Y - ctx.AreaFor(null).Center.Y;
        if (Math.Abs(dy) <= tolerance)
        {
            await ClickInPlaceAsync(b, ctx);
            return;
        }
        var tilt = (int)Math.Clamp(dy / ratio, -CenterMaxMovePx, CenterMaxMovePx);
        await ctx.InputAsync(() => InputBlocks.TurnAsync(ctx, method, 0, tilt, InputBlocks.DefaultTurnMs(tilt)));
        try { await ClickInPlaceAsync(b, ctx); }
        finally { await ctx.InputAsync(() => InputBlocks.TurnAsync(ctx, method, 0, -tilt, InputBlocks.DefaultTurnMs(tilt))); }
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
    /// <summary>
    /// A block's size tolerance: blank is automatic (any size from 30% to 160%). 25 was the default in 0.6.0 and
    /// counts as automatic too, so those blocks find small, far-away targets as well.
    /// </summary>
    public static int? SizeTolerance(int? stored) => stored is null or 25 ? null : stored;

    public static LookFor Text(Block b) => new("text", b.GetString("text", ""),
        Contains: b.GetString("match") == "contains", MatchCase: b.GetBool("matchCase") == true, Region: b.Params["region"]);

    public static LookFor Picture(Block b)
    {
        var (cx, cy) = ClickSpot.Read(b);
        return new("image", b.GetString("image", ""), ImageData: b.GetString("imageData"),
            ConfidencePct: b.GetInt("confidencePct", 90), Region: b.Params["region"], SizeTolerancePct: SizeTolerance(b.GetInt("sizeTolerancePct")), MatchColor: b.GetBool("matchColor") != false,
            ClickX: cx, ClickY: cy, BrightnessPct: Brightness.Read(b.Params));
    }

    public static LookFor Target(Block b)
    {
        var kind = b.GetString("target", "text");
        var (cx, cy) = ClickSpot.Read(b);
        return new LookFor(kind, b.GetString("value", ""), ImageData: b.GetString("imageData"),
            Tolerance: b.GetInt("tolerance", 10), ConfidencePct: b.GetInt("confidencePct", 90),
            Contains: true, MatchCase: false, Region: b.Params["region"], SizeTolerancePct: SizeTolerance(b.GetInt("sizeTolerancePct")), MatchColor: b.GetBool("matchColor") != false,
            ClickX: kind == "image" ? cx : 0.5, ClickY: kind == "image" ? cy : 0.5, BrightnessPct: Brightness.Read(b.Params));
    }
}
