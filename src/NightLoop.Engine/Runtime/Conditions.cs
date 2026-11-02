using System.Text.Json.Nodes;

namespace NightLoop.Engine.Runtime;

/// <summary>Evaluates logic.if / logic.repeatUntil conditions.</summary>
public interface IConditionEvaluator
{
    bool Evaluate(JsonObject condition, RunContext context);
}

/// <summary>
/// Handles the conditions that need no screen reading: windowFocused, processRunning, not, all, any.
/// textVisible, imageVisible and pixelColor need the Vision module (milestone M6) and fault the run for now.
/// </summary>
public class BasicConditionEvaluator : IConditionEvaluator
{
    public bool Evaluate(JsonObject condition, RunContext context)
    {
        var kind = (condition["kind"] as JsonValue)?.TryGetValue(out string? k) == true ? k : null;
        switch (kind)
        {
            case "not":
                return !Evaluate(condition["condition"] as JsonObject ?? throw Bad("not needs a condition"), context);
            case "all":
                return Children(condition).All(c => Evaluate(c, context));
            case "any":
                return Children(condition).Any(c => Evaluate(c, context));
            case "windowFocused":
                return context.Settings.TargetExe is null
                    || (context.Environment.Windows?.IsForeground(context.Settings.TargetExe) ?? true);
            case "processRunning":
                var name = (condition["name"] as JsonValue)?.TryGetValue(out string? n) == true ? n : null;
                return name is not null && (context.Environment.Windows?.IsProcessRunning(name) ?? false);
            default:
                return EvaluateVision(kind, condition, context);
        }
    }

    /// <summary>Override when the Vision module exists.</summary>
    protected virtual bool EvaluateVision(string? kind, JsonObject condition, RunContext context) =>
        throw new MacroFaultException($"\"{kind}\" conditions need screen reading, which isn't built yet.");

    private static IEnumerable<JsonObject> Children(JsonObject c) =>
        (c["conditions"] as JsonArray ?? throw Bad("all/any needs conditions")).OfType<JsonObject>();

    private static MacroFaultException Bad(string message) => new(message);
}
