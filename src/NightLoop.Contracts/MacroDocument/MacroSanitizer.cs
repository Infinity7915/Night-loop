using System.Text.Json.Nodes;

namespace NightLoop.Contracts.MacroDocument;

/// <summary>
/// Removes block settings Night-loop doesn't use before a macro is checked and stored. The app is open source, so a
/// changed copy could tuck any data it likes into a block's settings; the server keeps only the settings the app
/// really reads. Settings older versions wrote and nothing reads any more go too, which changes nothing about how a
/// macro runs.
/// </summary>
public static class MacroSanitizer
{
    /// <summary>Every block setting the app reads or writes, on any block type.</summary>
    public static readonly HashSet<string> BlockKeys = new(StringComparer.Ordinal)
    {
        "action", "afterFound", "amount", "amountPx", "anchor", "args", "autoResume", "button", "clickXPct", "clickYPct", "clicks", "color",
        "condition", "confidencePct", "count", "delayMs", "direction", "durationMs", "failLabel", "file", "forever",
        "foundLabel", "from", "height", "holdMs", "how", "ifRunning", "image", "imageData", "key", "keys", "kind", "macroName",
        "match", "matchCase", "matchColor", "maxIterations", "maxMs", "maxTurns", "message", "method", "minMs", "mode",
        "monitor", "moveMs", "ms", "name", "note", "onFail", "path", "process", "region", "relativeTo",
        "sizeTolerancePct", "sound", "stepPx", "steps", "target", "text", "timeoutMs", "to", "tolerance", "tolerancePx",
        "until", "value", "wait", "waitAfterMs", "when", "whenFound", "width", "x", "y",
    };

    /// <summary>Every setting an If / Repeat until condition can have.</summary>
    public static readonly HashSet<string> ConditionKeys = new(StringComparer.Ordinal)
    {
        "kind", "condition", "conditions", "name", "key", "color", "x", "y", "relativeTo", "tolerance", "text", "match",
        "region", "image", "imageData", "confidencePct", "sizeTolerancePct", "matchColor",
    };

    /// <summary>Drops unknown settings from every block and condition, in place. Returns how many were dropped.</summary>
    public static int Clean(MacroDoc doc)
    {
        var dropped = 0;
        foreach (var block in doc.Blocks)
        {
            block.Params ??= [];
            dropped += Keep(block.Params, BlockKeys);
            if (block.Params["condition"] is JsonObject c) dropped += CleanCondition(c, 0);
        }
        return dropped;
    }

    private static int CleanCondition(JsonObject c, int depth)
    {
        var dropped = Keep(c, ConditionKeys);
        if (depth > 8) return dropped; // the validator refuses anything this deep anyway
        if (c["condition"] is JsonObject inner) dropped += CleanCondition(inner, depth + 1);
        if (c["conditions"] is JsonArray children)
            foreach (var child in children.OfType<JsonObject>())
                dropped += CleanCondition(child, depth + 1);
        return dropped;
    }

    private static int Keep(JsonObject o, HashSet<string> allowed)
    {
        var unknown = o.Select(p => p.Key).Where(k => !allowed.Contains(k)).ToList();
        foreach (var k in unknown) o.Remove(k);
        return unknown.Count;
    }
}
