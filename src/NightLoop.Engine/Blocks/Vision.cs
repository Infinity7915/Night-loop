using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using NightLoop.Engine.Runtime;
using NightLoop.Engine.Screen;

namespace NightLoop.Engine.Blocks;

/// <summary>What to look for on screen: some text, a saved picture or a pixel color.</summary>
internal sealed record LookFor(
    string Kind,
    string Value,
    string? ImageData = null,
    int Tolerance = 10,
    int ConfidencePct = 90,
    bool Contains = true,
    bool MatchCase = false,
    JsonNode? Region = null);

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
                var match = TemplateMatcher.Find(image, template);
                return match is { } m && m.Score * 100 >= what.ConfidencePct ? (m.X, m.Y) : null;

            default:
                var reader = ctx.Environment.Text
                    ?? throw new MacroFaultException("Reading text needs Windows text recognition. Add a language with text recognition in Windows Settings > Time & language > Language.");
                var lines = await reader.ReadAsync(image, ctx.Token);
                return TextSearch.Find(lines, what.Value, what.Contains, what.MatchCase)?.Center;
        }
    }

    /// <summary>Looks again and again until found or the timeout passes (0 = no timeout). Returns null on timeout.</summary>
    public static async Task<(int X, int Y)?> WaitForAsync(RunContext ctx, LookFor what, int timeoutMs)
    {
        // Text reading is slow-ish (tens of ms), pictures and pixels are quick.
        var pollMs = what.Kind switch { "pixel" => 50, "image" => 120, _ => 200 };
        var waited = 0;
        while (true)
        {
            if (await FindAsync(ctx, what) is { } p) return p;
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
