namespace NightLoop.Contracts.MacroDocument;

/// <summary>
/// Find &amp; click / Scan &amp; turn with a picture: the spot inside the found picture to click, saved as whole
/// percents across and down (<see cref="BlockTypes.ClickXKey"/>, <see cref="BlockTypes.ClickYKey"/>). Blank is the
/// middle, which is how every block clicked before the spot could be set.
/// </summary>
public static class ClickSpot
{
    /// <summary>The spot as 0–1 across and down.</summary>
    public static (double X, double Y) Read(Block b)
    {
        static double Share(int? v) => v is { } p ? Math.Clamp(p, 0, 100) / 100.0 : 0.5;
        return (Share(b.GetInt(BlockTypes.ClickXKey)), Share(b.GetInt(BlockTypes.ClickYKey)));
    }
}
