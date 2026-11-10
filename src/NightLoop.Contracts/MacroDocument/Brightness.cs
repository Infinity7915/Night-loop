using System.Text.Json.Nodes;

namespace NightLoop.Contracts.MacroDocument;

/// <summary>
/// Picture blocks with Match colors on: how much dimmer or brighter than the captured picture a spot on screen may be,
/// in whole percents (<see cref="BlockTypes.BrightnessKey"/>). Blank is <see cref="DefaultPct"/> (about as captured,
/// with room for screen noise). Blocks from before the number had a switch (<see cref="BlockTypes.AverageColorsKey"/>):
/// on reads as <see cref="OldAveragePct"/>, the range it allowed.
/// </summary>
public static class Brightness
{
    public const int MinPct = 0, MaxPct = 100, DefaultPct = 6, OldAveragePct = 50;

    /// <summary>The range in percents from a block's or a condition's settings.</summary>
    public static int Read(JsonObject? p)
    {
        if (p is null) return DefaultPct;
        if (BlockParams.AsInt(p[BlockTypes.BrightnessKey]) is { } pct) return Math.Clamp(pct, MinPct, MaxPct);
        return (p[BlockTypes.AverageColorsKey] as JsonValue)?.TryGetValue(out bool on) == true && on ? OldAveragePct : DefaultPct;
    }

    /// <summary>What the number box shows: the saved number, the old switch's range, or blank for the default.</summary>
    public static string Shown(JsonObject? p) =>
        p is null ? ""
        : BlockParams.AsInt(p[BlockTypes.BrightnessKey]) is { } pct ? pct.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : (p[BlockTypes.AverageColorsKey] as JsonValue)?.TryGetValue(out bool on) == true && on ? OldAveragePct.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "";

    /// <summary>Saves the number box (blank = default) and drops the old switch it replaces.</summary>
    public static void Write(JsonObject p, int? pct)
    {
        p.Remove(BlockTypes.AverageColorsKey);
        if (pct is { } v) p[BlockTypes.BrightnessKey] = Math.Clamp(v, MinPct, MaxPct);
        else p.Remove(BlockTypes.BrightnessKey);
    }
}
