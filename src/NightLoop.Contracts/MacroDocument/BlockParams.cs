using System.Text.Json;
using System.Text.Json.Nodes;

namespace NightLoop.Contracts.MacroDocument;

/// <summary>Typed, forgiving readers for a block's params object.</summary>
public static class BlockParams
{
    public static string? GetString(this Block block, string name) =>
        block.Params.TryGetPropertyValue(name, out var node) && node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public static string GetString(this Block block, string name, string fallback) => block.GetString(name) ?? fallback;

    public static int? GetInt(this Block block, string name)
    {
        if (!block.Params.TryGetPropertyValue(name, out var node) || node is not JsonValue v)
            return null;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        if (v.TryGetValue(out double d) && d is >= int.MinValue and <= int.MaxValue && Math.Abs(d % 1) < double.Epsilon) return (int)d;
        if (v.TryGetValue(out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var ei)) return ei;
        return null;
    }

    public static int GetInt(this Block block, string name, int fallback) => block.GetInt(name) ?? fallback;

    public static double? GetDouble(this Block block, string name)
    {
        if (!block.Params.TryGetPropertyValue(name, out var node) || node is not JsonValue v)
            return null;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out JsonElement e) && e.ValueKind == JsonValueKind.Number) return e.GetDouble();
        return null;
    }

    public static JsonObject? GetObject(this Block block, string name) =>
        block.Params.TryGetPropertyValue(name, out var node) ? node as JsonObject : null;

    /// <summary>Reads a point param written as { "x": 1, "y": 2 }.</summary>
    public static (int X, int Y)? GetPoint(this Block block, string name)
    {
        var obj = block.GetObject(name);
        if (obj is null) return null;
        var tmp = new Block { Params = obj };
        var x = tmp.GetInt("x");
        var y = tmp.GetInt("y");
        return x is null || y is null ? null : (x.Value, y.Value);
    }
}
