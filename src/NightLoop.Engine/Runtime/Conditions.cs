using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Blocks;
using NightLoop.Engine.Screen;

namespace NightLoop.Engine.Runtime;

/// <summary>Evaluates logic.if / logic.repeatUntil conditions.</summary>
public interface IConditionEvaluator
{
    Task<bool> EvaluateAsync(JsonObject condition, RunContext context);
}

/// <summary>
/// Every condition kind: windowFocused, processRunning, keyHeld, pixelColor, textVisible, imageVisible, not, all, any.
/// The screen ones take one look (no waiting); use Wait for text / image / pixel to wait.
/// </summary>
public class BasicConditionEvaluator : IConditionEvaluator
{
    public async Task<bool> EvaluateAsync(JsonObject condition, RunContext context)
    {
        var kind = Str(condition, "kind");
        switch (kind)
        {
            case "not":
                return !await EvaluateAsync(condition["condition"] as JsonObject ?? throw Bad("not needs a condition"), context);
            case "all":
                foreach (var c in Children(condition))
                    if (!await EvaluateAsync(c, context)) return false;
                return true;
            case "any":
                foreach (var c in Children(condition))
                    if (await EvaluateAsync(c, context)) return true;
                return false;
            case "windowFocused":
                var exe = Str(condition, "name") is { Length: > 0 } n ? n : context.Settings.TargetExe;
                return exe is null || (context.Environment.Windows?.IsForeground(exe) ?? true);
            case "processRunning":
                var name = Str(condition, "name");
                return name is not null && (context.Environment.Windows?.IsProcessRunning(name) ?? false);
            case "keyHeld":
                var key = Str(condition, "key");
                if (key is null || !KeyCatalog.TryGetVirtualKey(key, out var vk)) throw Bad($"Unknown key \"{key}\".");
                var isDown = context.Environment.IsKeyDown ?? throw Bad("Checking keys isn't available here.");
                return isDown(vk);
            case "pixelColor":
                if (!Rgb.TryParse(Str(condition, "color"), out var color)) throw Bad("Pixel matches needs a color like #FF0000.");
                var p = context.ToScreen(BlockParams.AsInt(condition["x"]) ?? 0, BlockParams.AsInt(condition["y"]) ?? 0, Str(condition, "relativeTo"), jitter: false);
                var px = context.Capture(new ScreenRect(p.X, p.Y, 1, 1))[0, 0];
                return px.IsNear(color, BlockParams.AsInt(condition["tolerance"]) ?? 10);
            case "textVisible":
                return await Vision.FindAsync(context, new LookFor("text", Str(condition, "text") ?? "",
                    Contains: Str(condition, "match") != "exact", MatchCase: false, Region: condition["region"])) is not null;
            case "imageVisible":
                return await Vision.FindAsync(context, new LookFor("image", Str(condition, "image") ?? "", ImageData: Str(condition, "imageData"),
                    ConfidencePct: BlockParams.AsInt(condition["confidencePct"]) ?? 90, Region: condition["region"],
                    SizeTolerancePct: BlockParams.AsInt(condition["sizeTolerancePct"]) ?? 0)) is not null;
            default:
                throw Bad($"\"{kind}\" conditions can't be checked in this version of Night-loop.");
        }
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static IEnumerable<JsonObject> Children(JsonObject c) =>
        (c["conditions"] as JsonArray ?? throw Bad("all/any needs conditions")).OfType<JsonObject>();

    private static MacroFaultException Bad(string message) => new(message);
}
