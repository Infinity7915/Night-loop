using System.Globalization;
using System.Text.Json.Nodes;

namespace NightLoop.Contracts.MacroDocument;

/// <summary>
/// Picture blocks with Match colors on. With the "Dimmer or brighter" switch (<see cref="BlockTypes.AverageColorsKey"/>)
/// on, the screen's colors are averaged to the picture's brightness, so any dimmer or brighter copy matches. Off: a
/// copy may be at most <see cref="BlockTypes.BrightnessKey"/> percent dimmer or brighter (blank = <see cref="DefaultPct"/>).
/// </summary>
public static class Brightness
{
    public const int MinPct = 0, MaxPct = 100, DefaultPct = 3;

    /// <summary>The range in percents, or null when the switch says to average the colors instead.</summary>
    public static int? Read(JsonObject? p)
    {
        if (p is null) return DefaultPct;
        if ((p[BlockTypes.AverageColorsKey] as JsonValue)?.TryGetValue(out bool on) == true && on) return null;
        return BlockParams.AsInt(p[BlockTypes.BrightnessKey]) is { } pct ? Math.Clamp(pct, MinPct, MaxPct) : DefaultPct;
    }

    /// <summary>What the number box shows: the saved number, or blank for the default.</summary>
    public static string Shown(JsonObject? p) =>
        p is not null && BlockParams.AsInt(p[BlockTypes.BrightnessKey]) is { } pct ? pct.ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>Saves the number box (blank = default).</summary>
    public static void Write(JsonObject p, int? pct)
    {
        if (pct is { } v) p[BlockTypes.BrightnessKey] = Math.Clamp(v, MinPct, MaxPct);
        else p.Remove(BlockTypes.BrightnessKey);
    }
}
