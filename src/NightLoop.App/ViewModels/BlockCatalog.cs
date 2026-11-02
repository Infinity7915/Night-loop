using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.App.ViewModels;

/// <summary>How a block field is edited in the inspector.</summary>
public enum FieldKind
{
    /// <summary>Whole number. Optional ones may be left blank (removed from params).</summary>
    Int,
    Text,
    /// <summary>Multi-line text that grows to fit (utility.description).</summary>
    MultiLine,
    /// <summary>Pick one of <see cref="FieldDef.Choices"/>.</summary>
    Choice,
    /// <summary>A key name from KeyCatalog.</summary>
    Key,
    /// <summary>A saved macro's name (typed, with saved names suggested).</summary>
    MacroName,
}

/// <summary>One choice: the value stored in JSON and what the user sees.</summary>
public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One editable field. <see cref="Path"/> is a params key, or "from.x" style for nested points.
/// Condition fields use the pseudo paths "condition.kind" and "condition.value" (see <see cref="Conditions"/>).
/// </summary>
public sealed record FieldDef(
    string Path,
    string Label,
    FieldKind Kind,
    int Min = int.MinValue,
    int Max = int.MaxValue,
    IReadOnlyList<Choice>? Choices = null,
    bool Optional = false,
    int MaxLength = 200,
    string? Hint = null,
    bool IntChoice = false);

/// <summary>A palette entry: one block type (sometimes with preset params, like Pause vs Resume macro).</summary>
public sealed record BlockDef(
    string Key,
    string Type,
    string Label,
    string Tag,
    string Category,
    string Description,
    Func<JsonObject> Defaults,
    IReadOnlyList<FieldDef> Fields,
    bool NeedsVision = false)
{
    /// <summary>Shown on palette cards for blocks the engine can't run yet.</summary>
    public string Badge => NeedsVision ? "Coming soon" : "";
}

/// <summary>
/// The block catalog from schema section 6, with the labels and descriptions from the Macros mockup. The palette,
/// the inspector and the timeline all read from here.
/// </summary>
public static class BlockCatalog
{
    public static readonly IReadOnlyList<(string Name, string Color)> Categories =
    [
        ("Input", "#A78BFA"), ("Camera", "#F472B6"), ("Timing", "#9AA4B2"), ("Detect", "#F59E0B"),
        ("Logic", "#58A6FF"), ("Window", "#4CC9F0"), ("Utility", "#C9D1D9"),
    ];

    private static readonly Choice[] Buttons = [new("left", "Left"), new("right", "Right"), new("middle", "Middle"), new("x1", "Back (X1)"), new("x2", "Forward (X2)")];
    private static readonly Choice[] KeyActions = [new("press", "Press"), new("down", "Hold down"), new("up", "Release")];
    private static readonly Choice[] RelativeTo = [new("screen", "Screen"), new("gameWindow", "Game window")];
    private static readonly Choice[] Directions = [new("right", "Right"), new("left", "Left"), new("up", "Up"), new("down", "Down")];
    private static readonly Choice[] ClickCounts = [new("1", "Single"), new("2", "Double"), new("3", "Triple")];
    private static readonly Choice[] PauseModes = [new("pause", "Pause it"), new("resume", "Resume it")];
    private static readonly Choice[] Sounds = [new("default", "Default"), new("beep", "Beep"), new("exclamation", "Exclamation"), new("asterisk", "Asterisk"), new("question", "Question")];
    private static readonly Choice[] OnTimeout = [new("continue", "Continue"), new("stop", "Stop macro")];

    private static readonly FieldDef[] ConditionFields =
    [
        new("condition.kind", "Condition", FieldKind.Choice, Choices: Conditions.Kinds),
        new("condition.value", "Value", FieldKind.Text, Optional: true, MaxLength: 200,
            Hint: "Text to look for, or a process name like RobloxPlayerBeta.exe"),
    ];

    private static readonly FieldDef Ms = new("ms", "Duration (ms)", FieldKind.Int, MacroLimits.WaitMinMs, MacroLimits.WaitMaxMs);

    public static readonly IReadOnlyList<BlockDef> All =
    [
        // ---- Input ----
        new("key", BlockTypes.Key, "Key", "KEY", "Input", "Press, hold down or release one key.",
            () => new() { ["key"] = "E", ["action"] = "press", ["holdMs"] = 30 },
            [new("key", "Key", FieldKind.Key), new("action", "Action", FieldKind.Choice, Choices: KeyActions),
             new("holdMs", "Hold for (ms)", FieldKind.Int, 0, MacroLimits.WaitMaxMs, Optional: true, Hint: "Used by Press. Blank = 30 ms")]),
        new("click", BlockTypes.Click, "Click", "CLK", "Input", "Click at a position, or where the mouse is.",
            () => new() { ["button"] = "left", ["clicks"] = 1 },
            [new("button", "Button", FieldKind.Choice, Choices: Buttons),
             new("clicks", "Clicks", FieldKind.Choice, Choices: ClickCounts, IntChoice: true),
             new("x", "X", FieldKind.Int, -32_768, 32_767, Optional: true, Hint: "Leave X and Y blank to click where the mouse is"),
             new("y", "Y", FieldKind.Int, -32_768, 32_767, Optional: true),
             new("relativeTo", "Relative to", FieldKind.Choice, Choices: RelativeTo)]),
        new("move", BlockTypes.Move, "Mouse move", "MOVE", "Input", "Move the mouse to a point.",
            () => new() { ["x"] = 0, ["y"] = 0, ["relativeTo"] = "screen", ["durationMs"] = 120 },
            [new("x", "X", FieldKind.Int, -32_768, 32_767), new("y", "Y", FieldKind.Int, -32_768, 32_767),
             new("relativeTo", "Relative to", FieldKind.Choice, Choices: RelativeTo),
             new("durationMs", "Duration (ms)", FieldKind.Int, 0, MacroLimits.WaitMaxMs)]),
        new("drag", BlockTypes.Drag, "Drag", "DRAG", "Input", "Hold a button and drag between two points.",
            () => new() { ["button"] = "left", ["from"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["to"] = new JsonObject { ["x"] = 100, ["y"] = 0 }, ["relativeTo"] = "screen", ["durationMs"] = 300 },
            [new("button", "Button", FieldKind.Choice, Choices: Buttons),
             new("from.x", "From X", FieldKind.Int, -32_768, 32_767), new("from.y", "From Y", FieldKind.Int, -32_768, 32_767),
             new("to.x", "To X", FieldKind.Int, -32_768, 32_767), new("to.y", "To Y", FieldKind.Int, -32_768, 32_767),
             new("relativeTo", "Relative to", FieldKind.Choice, Choices: RelativeTo),
             new("durationMs", "Duration (ms)", FieldKind.Int, 0, MacroLimits.WaitMaxMs)]),
        new("scroll", BlockTypes.Scroll, "Scroll", "SCRL", "Input", "Scroll the wheel up or down.",
            () => new() { ["amount"] = -3 },
            [new("amount", "Notches", FieldKind.Int, MacroLimits.ScrollMin, MacroLimits.ScrollMax, Hint: "Positive scrolls up, negative scrolls down")]),
        new("type", BlockTypes.Type, "Type text", "TYPE", "Input", "Type a string, like a chat message.",
            () => new() { ["text"] = "gg", ["delayMs"] = 30 },
            [new("text", "Text", FieldKind.Text, MaxLength: MacroLimits.TypeTextMaxLength),
             new("delayMs", "Delay per key (ms)", FieldKind.Int, 0, 10_000)]),

        // ---- Camera ----
        new("camturn", BlockTypes.CameraTurn, "Turn camera", "CAM", "Camera", "Rotate the view with relative mouse movement.",
            () => new() { ["direction"] = "right", ["amountPx"] = 120, ["durationMs"] = 150 },
            [new("direction", "Direction", FieldKind.Choice, Choices: Directions),
             new("amountPx", "Amount (px)", FieldKind.Int, 1, 100_000),
             new("durationMs", "Duration (ms)", FieldKind.Int, 0, MacroLimits.WaitMaxMs)]),
        new("camtext", BlockTypes.CameraTurnUntilText, "Turn until text", "SCN", "Camera", "Turn the camera step by step until some text is on screen.",
            () => new() { ["direction"] = "right", ["text"] = "Collect", ["stepPx"] = 120, ["timeoutMs"] = 10_000 },
            [new("direction", "Direction", FieldKind.Choice, Choices: Directions), new("text", "Text", FieldKind.Text),
             new("stepPx", "Step (px)", FieldKind.Int, 1, 100_000), new("timeoutMs", "Timeout (ms)", FieldKind.Int, 1, MacroLimits.WaitMaxMs)],
            NeedsVision: true),
        new("camimage", BlockTypes.CameraTurnUntilImage, "Turn until image", "SCNI", "Camera", "Turn the camera until a saved picture is on screen.",
            () => new() { ["direction"] = "right", ["templateId"] = "", ["threshold"] = 0.85, ["stepPx"] = 120, ["timeoutMs"] = 10_000 },
            [new("direction", "Direction", FieldKind.Choice, Choices: Directions),
             new("stepPx", "Step (px)", FieldKind.Int, 1, 100_000), new("timeoutMs", "Timeout (ms)", FieldKind.Int, 1, MacroLimits.WaitMaxMs)],
            NeedsVision: true),

        // ---- Timing ----
        new("wait", BlockTypes.Wait, "Wait", "WAIT", "Timing", "Pause for a fixed time.",
            () => new() { ["ms"] = 500 }, [Ms]),
        new("randwait", BlockTypes.WaitRandom, "Random wait", "RND", "Timing", "Pause a random time between two values.",
            () => new() { ["minMs"] = 300, ["maxMs"] = 800 },
            [new("minMs", "Min (ms)", FieldKind.Int, MacroLimits.WaitMinMs, MacroLimits.WaitMaxMs),
             new("maxMs", "Max (ms)", FieldKind.Int, MacroLimits.WaitMinMs, MacroLimits.WaitMaxMs)]),
        new("pausem", BlockTypes.Pause, "Pause macro", "PAUS", "Timing", "Pause another running macro, for example while this one handles a crash or reconnect.",
            () => new() { ["target"] = "", ["mode"] = "pause" },
            [new("target", "Macro", FieldKind.MacroName, MaxLength: MacroLimits.NameMaxLength), new("mode", "Do", FieldKind.Choice, Choices: PauseModes)]),
        new("resumem", BlockTypes.Pause, "Resume macro", "RSME", "Timing", "Unpause a macro so it carries on from where it stopped.",
            () => new() { ["target"] = "", ["mode"] = "resume" },
            [new("target", "Macro", FieldKind.MacroName, MaxLength: MacroLimits.NameMaxLength), new("mode", "Do", FieldKind.Choice, Choices: PauseModes)]),

        // ---- Detect (needs screen reading) ----
        new("waittext", BlockTypes.WaitForText, "Wait for text", "OCR", "Detect", "Read the screen until the exact characters appear.",
            () => new() { ["text"] = "Quest complete", ["timeoutMs"] = 10_000, ["onTimeout"] = "continue" },
            [new("text", "Text", FieldKind.Text), new("timeoutMs", "Timeout (ms)", FieldKind.Int, 1, MacroLimits.WaitMaxMs),
             new("onTimeout", "On timeout", FieldKind.Choice, Choices: OnTimeout)],
            NeedsVision: true),
        new("waitimage", BlockTypes.WaitForImage, "Wait for image", "IMG", "Detect", "Wait until a saved picture shows up on screen.",
            () => new() { ["templateId"] = "", ["threshold"] = 0.85, ["timeoutMs"] = 10_000, ["onTimeout"] = "continue" },
            [new("timeoutMs", "Timeout (ms)", FieldKind.Int, 1, MacroLimits.WaitMaxMs), new("onTimeout", "On timeout", FieldKind.Choice, Choices: OnTimeout)],
            NeedsVision: true),
        new("pixel", BlockTypes.DetectPixelColor, "Pixel color", "PIX", "Detect", "Check one point's color, like a health bar.",
            () => new() { ["x"] = 0, ["y"] = 0, ["color"] = "#FFFFFF", ["tolerance"] = 10 },
            [new("x", "X", FieldKind.Int, -32_768, 32_767), new("y", "Y", FieldKind.Int, -32_768, 32_767),
             new("color", "Color (#RRGGBB)", FieldKind.Text, MaxLength: 7), new("tolerance", "Tolerance", FieldKind.Int, 0, 255)],
            NeedsVision: true),

        // ---- Logic ----
        new("if", BlockTypes.If, "If", "IF", "Logic", "Run the blocks inside only when a condition is true. Add an End after them.",
            () => new() { ["condition"] = new JsonObject { ["kind"] = "windowFocused" } }, ConditionFields),
        new("else", BlockTypes.Else, "Else", "ELSE", "Logic", "Runs when the If above was false.", () => [], []),
        new("end", BlockTypes.End, "End", "END", "Logic", "Closes the nearest Loop, Repeat until or If.", () => [], []),
        new("loop", BlockTypes.Loop, "Loop", "LOOP", "Logic", "Repeat the blocks inside a set number of times. Add an End after them.",
            () => new() { ["count"] = 10 }, [new("count", "Times", FieldKind.Int, MacroLimits.CountMin, MacroLimits.CountMax)]),
        new("until", BlockTypes.RepeatUntil, "Repeat until", "UNTL", "Logic", "Repeat the blocks inside until a condition is true. Add an End after them.",
            () => new() { ["condition"] = new JsonObject { ["kind"] = "not", ["condition"] = new JsonObject { ["kind"] = "windowFocused" } }, ["maxIterations"] = 100 },
            [.. ConditionFields, new("maxIterations", "Max tries", FieldKind.Int, MacroLimits.CountMin, MacroLimits.CountMax)]),
        new("label", BlockTypes.Label, "Label", "LBL", "Logic", "A named spot you can jump back to.",
            () => new() { ["name"] = "start" }, [new("name", "Name", FieldKind.Text, MaxLength: 40)]),
        new("goto", BlockTypes.Goto, "Go to label", "GOTO", "Logic", "Jump to a label.",
            () => new() { ["name"] = "start" }, [new("name", "Label", FieldKind.Text, MaxLength: 40)]),
        new("stop", BlockTypes.Stop, "Stop macro", "STOP", "Logic", "End the run here.", () => [], []),

        // ---- Window ----
        new("focus", BlockTypes.WindowFocus, "Focus window", "WIN", "Window", "Bring the target game to the front.", () => [], []),
        new("resize", BlockTypes.WindowResize, "Resize window", "SIZE", "Window", "Set the game window to an exact size so recorded positions line up every run.",
            () => new() { ["width"] = 1280, ["height"] = 720 },
            [new("width", "Width (px)", FieldKind.Int, 1, 16_384), new("height", "Height (px)", FieldKind.Int, 1, 16_384)]),
        new("position", BlockTypes.WindowPosition, "Position window", "POS", "Window", "Move the game window to the same spot on screen so clicks land in the right place.",
            () => new() { ["x"] = 0, ["y"] = 0 },
            [new("x", "X", FieldKind.Int, -32_768, 32_767), new("y", "Y", FieldKind.Int, -32_768, 32_767)]),

        // ---- Utility ----
        new("comment", BlockTypes.Comment, "Comment", "//", "Utility", "A note that does nothing when run.",
            () => new() { ["text"] = "Section" }, [new("text", "Note", FieldKind.Text, Optional: true, MaxLength: MacroLimits.CommentMaxLength)]),
        new("desc", BlockTypes.Description, "Description", "DESC", "Utility", "Longer notes that grow to fit. Does nothing when run.",
            () => new() { ["text"] = "" }, [new("text", "Text", FieldKind.MultiLine, Optional: true, MaxLength: MacroLimits.DescriptionMaxLength)]),
        new("notify", BlockTypes.Notify, "Notify", "NTF", "Utility", "Show a message in Night-loop.",
            () => new() { ["message"] = "Done" }, [new("message", "Message", FieldKind.Text)]),
        new("sound", BlockTypes.PlaySound, "Play sound", "SND", "Utility", "Play a Windows sound.",
            () => new() { ["sound"] = "default" }, [new("sound", "Sound", FieldKind.Choice, Choices: Sounds)]),
        new("call", BlockTypes.RunMacro, "Run macro", "CALL", "Utility", "Run another saved macro, then come back.",
            () => new() { ["macroName"] = "" }, [new("macroName", "Macro", FieldKind.MacroName, MaxLength: MacroLimits.NameMaxLength)]),
    ];

    private static readonly Dictionary<string, BlockDef> ByType = All
        .GroupBy(d => d.Type).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>The definition used to edit a block (the first palette entry of its type).</summary>
    public static BlockDef? For(string type) => ByType.GetValueOrDefault(type);

    public static string ColorOf(string category) =>
        Categories.FirstOrDefault(c => string.Equals(c.Name, category, StringComparison.OrdinalIgnoreCase)).Color ?? "#C9D1D9";
}

/// <summary>
/// The simple condition editor: one kind plus one value, mapped to the JSON condition objects of schema section 6
/// ("not" wraps the negative kinds).
/// </summary>
public static class Conditions
{
    public static readonly Choice[] Kinds =
    [
        new("windowFocused", "Game window focused"),
        new("!windowFocused", "Game window not focused"),
        new("processRunning", "Process running"),
        new("!processRunning", "Process not running"),
        new("textVisible", "Text visible (coming soon)"),
        new("!textVisible", "Text not visible (coming soon)"),
    ];

    /// <summary>Reads (kind, value) from a condition object. Unknown shapes come back as their raw kind.</summary>
    public static (string Kind, string Value) Read(JsonObject? c)
    {
        if (c is null) return ("windowFocused", "");
        var kind = Str(c, "kind") ?? "";
        if (kind == "not" && c["condition"] is JsonObject inner)
        {
            var (k, v) = Read(inner);
            return (k.StartsWith('!') ? k[1..] : "!" + k, v);
        }
        return kind switch
        {
            "processRunning" => (kind, Str(c, "name") ?? ""),
            "textVisible" => (kind, Str(c, "text") ?? ""),
            _ => (kind, ""),
        };
    }

    public static JsonObject Write(string kind, string value)
    {
        var negate = kind.StartsWith('!');
        var k = negate ? kind[1..] : kind;
        var obj = new JsonObject { ["kind"] = k };
        if (k == "processRunning") obj["name"] = value.Trim();
        if (k == "textVisible") obj["text"] = value;
        return negate ? new JsonObject { ["kind"] = "not", ["condition"] = obj } : obj;
    }

    public static string Describe(JsonObject? c)
    {
        var (kind, value) = Read(c);
        var label = Kinds.FirstOrDefault(k => k.Value == kind)?.Label.Replace(" (coming soon)", "") ?? kind;
        return value.Length > 0 ? $"{label} \"{value}\"" : label;
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
