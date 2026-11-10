using NightLoop.Contracts.MacroDocument;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using NightLoop.Engine.Runtime;
using NightLoop.Engine.Screen;

namespace NightLoop.Engine.Blocks;

/// <summary>
/// What to look for on screen: some text, a saved picture or a pixel color. For a picture, <see cref="ClickX"/> and
/// <see cref="ClickY"/> say which spot of it counts as "where it is" (0–1 across and down, 0.5 = the middle): Find &amp;
/// click clicks there and Center on target turns there.
/// </summary>
internal sealed record LookFor(
    string Kind,
    string Value,
    string? ImageData = null,
    int Tolerance = 10,
    int ConfidencePct = 90,
    bool Contains = true,
    bool MatchCase = false,
    JsonNode? Region = null,
    int? SizeTolerancePct = 0,
    bool MatchColor = false,
    double ClickX = 0.5,
    double ClickY = 0.5,
    int? BrightnessPct = Brightness.DefaultPct);

/// <summary>Finds text, pictures and colors on screen for the detect blocks and conditions.</summary>
internal static class Vision
{
    private static readonly ConditionalWeakTable<string, ImageTemplate> s_templates = new();

    /// <summary>One look. Returns the screen point at the middle of what was found.</summary>
    public static async Task<(int X, int Y)?> FindAsync(RunContext ctx, LookFor what)
    {
        var area = ctx.AreaFor(what.Region);
        var image = ctx.Capture(area);
        switch (what.Kind)
        {
            case "pixel":
                if (!Rgb.TryParse(what.Value, out var color)) throw new MacroFaultException($"\"{what.Value}\" isn't a color like #FF0000.");
                return image.Find(color, what.Tolerance);

            case "image":
                var template = Template(what.ImageData);
                var match = TemplateMatcher.Find(image, template, what.SizeTolerancePct, what.MatchColor, what.BrightnessPct);
                return match is { } m && m.ColorOk && m.Score * 100 >= what.ConfidencePct ? PointIn(m, template, what) : null;

            default:
                var reader = ctx.Environment.Text
                    ?? throw new MacroFaultException("Reading text needs Windows text recognition. Add a language with text recognition in Windows Settings > Time & language > Language.");
                var lines = await reader.ReadAsync(image, ctx.Token);
                return TextSearch.Find(lines, what.Value, what.Contains, what.MatchCase)?.Center;
        }
    }

    /// <summary>
    /// The block's chosen spot inside a found picture. The picture may be found smaller or bigger than captured, so
    /// the spot is scaled with it and stays on the same part of the picture.
    /// </summary>
    public static (int X, int Y) PointIn(TemplateMatcher.Match m, ImageTemplate template, LookFor what)
    {
        if (Math.Abs(what.ClickX - 0.5) < 1e-9 && Math.Abs(what.ClickY - 0.5) < 1e-9) return (m.X, m.Y);
        // The found area, sized the way the matcher sized it; m is its middle.
        var w = Math.Max(1, (int)Math.Round(template.Width * m.Scale));
        var h = Math.Max(1, (int)Math.Round(template.Height * m.Scale));
        var (left, top) = (m.X - w / 2, m.Y - h / 2);
        // Outside 0–1 is a spot beside the picture (picked on screen); it keeps the same place relative to it.
        return (left + (int)Math.Round(what.ClickX * (w - 1)), top + (int)Math.Round(what.ClickY * (h - 1)));
    }

    /// <summary>Same, for the editor's test look.</summary>
    public static (int X, int Y) PointIn(TemplateMatcher.Match m, LookFor what) => PointIn(m, Template(what.ImageData), what);

    /// <summary>The best spot for a picture right now, however poor, for the editor's test look to explain a miss.</summary>
    public static TemplateMatcher.Match? BestImageMatch(RunContext ctx, LookFor what) =>
        TemplateMatcher.Find(ctx.Capture(ctx.AreaFor(what.Region)), Template(what.ImageData), what.SizeTolerancePct, what.MatchColor, what.BrightnessPct);

    /// <summary>Two looks this close together count as the same, steady spot.</summary>
    public const int SteadyPx = 16;
    /// <summary>Most extra looks <see cref="ConfirmAsync"/> takes while the target is still sliding (the camera easing to a stop).</summary>
    private const int ConfirmLooks = 3;

    /// <summary>
    /// Makes sure something seen once is really there before a block clicks or turns to it. One look can be fooled:
    /// a frame caught while the camera is still moving, a moment of a look-alike, or text read wrong once. This looks
    /// again after <paramref name="settleMs"/> and only says yes when it is still there, in about the same place.
    /// While it keeps sliding, it waits and looks again (up to a few times), then uses the last place seen.
    /// Returns null when a later look doesn't find it.
    /// </summary>
    public static async Task<(int X, int Y)?> ConfirmAsync(RunContext ctx, LookFor what, (int X, int Y) first, int settleMs)
    {
        var last = first;
        for (var i = 0; i < ConfirmLooks; i++)
        {
            await ctx.PauseExactAsync(settleMs);
            if (await FindAsync(ctx, what) is not { } next) return null;
            if (Math.Abs(next.X - last.X) <= SteadyPx && Math.Abs(next.Y - last.Y) <= SteadyPx) return next;
            last = next;
        }
        return last;
    }

    /// <summary>
    /// Looks again and again until found or the timeout passes (0 = no timeout). Returns null on timeout. With
    /// <paramref name="confirm"/>, a find only counts once a second look agrees (see <see cref="ConfirmAsync"/>).
    /// </summary>
    public static async Task<(int X, int Y)?> WaitForAsync(RunContext ctx, LookFor what, int timeoutMs, bool confirm = false)
    {
        // Text reading is slow-ish (tens of ms), pictures and pixels are quick.
        var pollMs = what.Kind switch { "pixel" => 50, "image" => 120, _ => 200 };
        var waited = 0;
        while (true)
        {
            if (await FindAsync(ctx, what) is { } p)
            {
                if (!confirm) return p;
                if (await ConfirmAsync(ctx, what, p, pollMs) is { } sure) return sure;
                waited += pollMs;
            }
            if (timeoutMs > 0 && waited >= timeoutMs) return null;
            var step = timeoutMs > 0 ? Math.Min(pollMs, timeoutMs - waited) : pollMs;
            await ctx.PauseExactAsync(Math.Max(1, step));
            waited += Math.Max(1, step);
        }
    }

    /// <summary>Looks again and again until it is no longer found or the timeout passes. False on timeout.</summary>
    public static async Task<bool> WaitForGoneAsync(RunContext ctx, LookFor what, int timeoutMs)
    {
        var pollMs = what.Kind switch { "pixel" => 50, "image" => 120, _ => 200 };
        var waited = 0;
        while (true)
        {
            if (await FindAsync(ctx, what) is null) return true;
            if (timeoutMs > 0 && waited >= timeoutMs) return false;
            var step = timeoutMs > 0 ? Math.Min(pollMs, timeoutMs - waited) : pollMs;
            await ctx.PauseExactAsync(Math.Max(1, step));
            waited += Math.Max(1, step);
        }
    }

    /// <summary>Whether a saved picture has its colors (pictures captured before 0.6.2 are gray only).</summary>
    public static bool HasColor(string? data) => !string.IsNullOrEmpty(data) && ImageTemplate.TryDecode(data, out var t) && t!.HasColor;

    private static ImageTemplate Template(string? data)
    {
        if (string.IsNullOrEmpty(data)) throw new MacroFaultException("Pick a picture first: use \"Capture image from screen\" on this block.");
        if (s_templates.TryGetValue(data, out var cached)) return cached;
        if (!ImageTemplate.TryDecode(data, out var t) || t is null)
            throw new MacroFaultException("The saved picture is damaged. Capture it again.");
        s_templates.AddOrUpdate(data, t);
        return t;
    }
}
