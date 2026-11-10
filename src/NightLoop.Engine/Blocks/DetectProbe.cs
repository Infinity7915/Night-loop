using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Compiler;
using NightLoop.Engine.Input;
using NightLoop.Engine.Runtime;
using NightLoop.Engine.Screen;

namespace NightLoop.Engine.Blocks;

/// <summary>What one test look found. Point is on the screen, when there is one.</summary>
public sealed record ProbeResult(bool Found, string Message, (int X, int Y)? Point = null);

/// <summary>A click spot picked on the screen: shares of the found picture (outside 0–1 = beside it), or why not.</summary>
public sealed record ClickSpotPick(bool Ok, string Message, double X = 0.5, double Y = 0.5);

/// <summary>
/// "Test on screen now" in the block editor: looks once for what a Detect block or an If / Repeat until condition is
/// set up to find, without clicking, turning or waiting. No input is sent.
/// </summary>
public static class DetectProbe
{
    /// <summary>Blocks the editor offers the test for.</summary>
    public static bool CanTest(Block block) => block.Type is BlockTypes.WaitForText or BlockTypes.WaitForTextGone or BlockTypes.WaitForImage or BlockTypes.WaitForImageGone
        or BlockTypes.DetectPixelColor or BlockTypes.DetectPixelColorGone or BlockTypes.FindClick or BlockTypes.ScanTurn or BlockTypes.If or BlockTypes.RepeatUntil;

    public static async Task<ProbeResult> LookOnceAsync(Block block, RunEnvironment environment, string? targetExe, CancellationToken ct = default)
    {
        var settings = new RunSettings { TargetExe = targetExe };
        var run = new MacroRun(MacroCompiler.Compile(new MacroDoc { Name = "Test" }), environment, settings);
        var ctx = new RunContext(run, environment, settings, new TrackingInputSender(NoInput.Instance), ct);
        try
        {
            return block.Type switch
            {
                BlockTypes.If or BlockTypes.RepeatUntil => await ConditionAsync(block, ctx),
                BlockTypes.DetectPixelColor or BlockTypes.DetectPixelColorGone => Pixel(block, ctx),
                BlockTypes.WaitForText or BlockTypes.WaitForTextGone => await FindAsync(ctx, DetectBlocksLook.Text(block)),
                BlockTypes.WaitForImage or BlockTypes.WaitForImageGone => await FindAsync(ctx, DetectBlocksLook.Picture(block)),
                BlockTypes.FindClick or BlockTypes.ScanTurn => await FindAsync(ctx, DetectBlocksLook.Target(block)),
                _ => new ProbeResult(false, "This block doesn't look at the screen."),
            };
        }
        catch (MacroFaultException ex)
        {
            return new ProbeResult(false, ex.Message);
        }
    }

    /// <summary>
    /// "Set click spot on screen": finds the block's picture in <paramref name="shot"/> and turns the screen point
    /// that was picked into the block's click spot, so it can be anywhere on screen, not only inside the picture. The
    /// spot keeps its place next to the picture (and scales with it) wherever the picture is found later.
    /// </summary>
    public static ClickSpotPick SpotFromScreen(Block b, ScreenImage shot, int x, int y)
    {
        var what = b.Type is BlockTypes.FindClick or BlockTypes.ScanTurn ? DetectBlocksLook.Target(b) : DetectBlocksLook.Picture(b);
        if (what.ImageData is not { Length: > 0 } data || !ImageTemplate.TryDecode(data, out var t) || t is null)
            return new(false, "Capture the picture first.");
        var m = TemplateMatcher.Find(shot, t, what.SizeTolerancePct, what.MatchColor, what.BrightnessPct);
        if (m is not { } found || found.Score * 100 < what.ConfidencePct || !found.ColorOk)
            return new(false, "The picture isn't on screen right now. Show it on screen, then pick the spot again.");
        var w = Math.Max(1, (int)Math.Round(t.Width * found.Scale));
        var h = Math.Max(1, (int)Math.Round(t.Height * found.Scale));
        var (left, top) = (found.X - w / 2, found.Y - h / 2);
        var sx = w > 1 ? (x - left) / (double)(w - 1) : 0.5;
        var sy = h > 1 ? (y - top) / (double)(h - 1) : 0.5;
        if (sx * 100 < ClickSpot.MinPct || sx * 100 > ClickSpot.MaxPct || sy * 100 < ClickSpot.MinPct || sy * 100 > ClickSpot.MaxPct)
            return new(false, "That spot is too far from the picture (at most 10 picture widths away). Pick a spot closer to it.");
        return new(true, "", sx, sy);
    }

    private static async Task<ProbeResult> FindAsync(RunContext ctx, LookFor what)
    {
        if (what.Kind != "image" && what.Value.Trim().Length == 0)
            return new ProbeResult(false, what.Kind == "pixel" ? "Pick a color first." : "Type the text to look for first.");
        if (what.Kind == "image") return Picture(ctx, what);
        var found = await Vision.FindAsync(ctx, what);
        var thing = what.Kind switch { "image" => "the picture", "pixel" => what.Value, _ => $"\"{what.Value}\"" };
        return found is { } p
            ? new ProbeResult(true, $"Found {thing} at {p.X}, {p.Y}.", p)
            : new ProbeResult(false, $"Didn't find {thing} on screen right now.");
    }

    /// <summary>Says how close the best spot came and at what size, so a miss shows what to change.</summary>
    private static ProbeResult Picture(RunContext ctx, LookFor what)
    {
        if (Vision.BestImageMatch(ctx, what) is not { } m) return new ProbeResult(false, "The picture is bigger than the area to look in.");
        var score = (int)Math.Floor(m.Score * 100);
        var at = Vision.PointIn(m, what);
        var size = Math.Abs(m.Scale - 1) < 0.005 ? "" : $" at {Math.Round(m.Scale * 100)}% of its size";
        if (!m.ColorOk && score >= what.ConfidencePct)
            return new ProbeResult(false, $"Found the shape{size} ({score}% alike), but not in the picture's colors. Match colors is on, so it won't click a different-colored one.", at);
        if (score >= what.ConfidencePct)
            return new ProbeResult(true, $"Found the picture at {at.X}, {at.Y}{size} ({score}% alike).", at);
        var hint = what.SizeTolerancePct is not null
            ? " If it looks smaller or bigger in the game than when you captured it, clear Size tolerance so it's automatic."
            : " Try a lower Confidence, or capture a smaller, more distinctive part.";
        return new ProbeResult(false, $"Didn't find the picture. The closest spot was {score}% alike{size} (needs {what.ConfidencePct}%).{hint}", at);
    }

    private static ProbeResult Pixel(Block b, RunContext ctx)
    {
        if (!Rgb.TryParse(b.GetString("color"), out var color)) return new ProbeResult(false, "Pick a color like #FF0000 first.");
        var p = ctx.ToScreen(b.GetInt("x", 0), b.GetInt("y", 0), b.GetString("relativeTo"), jitter: false);
        var px = ctx.Capture(new ScreenRect(p.X, p.Y, 1, 1))[0, 0];
        var tolerance = b.GetInt("tolerance", 10);
        return px.IsNear(color, tolerance)
            ? new ProbeResult(true, $"The pixel at {p.X}, {p.Y} matches ({px}).", (p.X, p.Y))
            : new ProbeResult(false, $"The pixel at {p.X}, {p.Y} is {px}, not {color.ToString()}.", (p.X, p.Y));
    }

    private static async Task<ProbeResult> ConditionAsync(Block b, RunContext ctx)
    {
        if (b.GetObject("condition") is not JsonObject condition) return new ProbeResult(false, "Pick a condition first.");
        var yes = await ctx.Environment.Conditions.EvaluateAsync(condition, ctx);
        return new ProbeResult(yes, yes ? "Right now this is true." : "Right now this is false.");
    }

    /// <summary>Sends nothing: a test look must never press or click.</summary>
    private sealed class NoInput : IInputSender
    {
        public static readonly NoInput Instance = new();
        public void KeyDown(int virtualKey) { }
        public void KeyUp(int virtualKey) { }
        public void MouseDown(MouseButton button) { }
        public void MouseUp(MouseButton button) { }
        public void MoveTo(int x, int y) { }
        public void MoveBy(int dx, int dy) { }
        public void Scroll(int notches) { }
        public void TypeChar(char c) { }
        public (int X, int Y) CursorPosition => (0, 0);
    }
}
