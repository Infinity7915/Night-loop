namespace NightLoop.Contracts.MacroDocument;

/// <summary>
/// Picture blocks: the spot to click (or tap) once the picture is found, saved as whole percents of the picture's
/// width and height from its top-left corner (<see cref="BlockTypes.ClickXKey"/>, <see cref="BlockTypes.ClickYKey"/>).
/// 0–100 is inside the picture; below 0 or above 100 is a spot on screen beside it (picked on the screen), which
/// moves and scales with the picture. Blank is the middle, which is how every block clicked before the spot could be set.
/// </summary>
public static class ClickSpot
{
    /// <summary>Lowest and highest percent: up to 10 picture widths (or heights) beside the picture.</summary>
    public const int MinPct = -1000, MaxPct = 1100;

    /// <summary>The spot as shares across and down (0–1 inside the picture, outside that beside it).</summary>
    public static (double X, double Y) Read(Block b)
    {
        static double Share(int? v) => v is { } p ? Math.Clamp(p, MinPct, MaxPct) / 100.0 : 0.5;
        return (Share(b.GetInt(BlockTypes.ClickXKey)), Share(b.GetInt(BlockTypes.ClickYKey)));
    }

    /// <summary>Saves the spot as whole percents; the middle is left blank, as blocks had it before there was a spot.</summary>
    public static (int X, int Y) Write(Block b, double x, double y)
    {
        var px = (int)Math.Round(Math.Clamp(x * 100, MinPct, MaxPct));
        var py = (int)Math.Round(Math.Clamp(y * 100, MinPct, MaxPct));
        if (px == 50 && py == 50)
        {
            b.Params.Remove(BlockTypes.ClickXKey);
            b.Params.Remove(BlockTypes.ClickYKey);
        }
        else
        {
            b.Params[BlockTypes.ClickXKey] = px;
            b.Params[BlockTypes.ClickYKey] = py;
        }
        return (px, py);
    }

    /// <summary>"Clicks the middle of the picture", "Taps 20% across, 80% down", or a spot beside it.</summary>
    public static string Describe(double x, double y, string verb = "Clicks")
    {
        if (Math.Abs(x - 0.5) < 0.005 && Math.Abs(y - 0.5) < 0.005) return $"{verb} the middle of the picture";
        var where = $"{Math.Round(x * 100)}% across, {Math.Round(y * 100)}% down";
        return x is >= 0 and <= 1 && y is >= 0 and <= 1 ? $"{verb} {where}" : $"{verb} a spot beside the picture ({where} of it)";
    }
}
