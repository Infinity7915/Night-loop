using NightLoop.Contracts.MacroDocument;

namespace NightLoop.App.ViewModels;

/// <summary>Chip tag, title, one-line summary and category for each block, as in the Macros mockup.</summary>
public static class BlockDescriber
{
    public static (string Tag, string Title, string Detail, string Category) Describe(Block b)
    {
        if (BlockCatalog.For(b.Type) is not { } def)
        {
            var last = b.Type.Split('.').Last().ToUpperInvariant();
            var first = b.Type.Split('.')[0];
            return (last[..Math.Min(4, last.Length)], b.Type, "", first.Length == 0 ? "" : char.ToUpperInvariant(first[0]) + first[1..]);
        }

        var title = def.Label;
        // Older macros used one Pause block with a mode for both.
        if (b.Type == BlockTypes.Pause && b.GetString("mode") == "resume") title = "Resume macro";
        string detail;
        try
        {
            detail = def.Summary(b);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            detail = "";
        }
        return (def.Tag, title, detail, def.Category);
    }
}
