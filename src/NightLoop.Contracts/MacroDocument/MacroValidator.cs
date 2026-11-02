using System.Text.Json.Nodes;

namespace NightLoop.Contracts.MacroDocument;

/// <summary>
/// Checks a macro document against every rule in schema sections 6 and 11.
/// The server runs this on every write; the client runs it first to show friendly messages.
/// </summary>
public static class MacroValidator
{
    private static readonly HashSet<string> Buttons = ["left", "right", "middle", "x1", "x2"];
    private static readonly HashSet<string> KeyActions = ["press", "down", "up"];
    private static readonly HashSet<string> RelativeTo = ["screen", "gameWindow"];
    private static readonly HashSet<string> Directions = ["left", "right", "up", "down"];
    private static readonly HashSet<string> ConditionKinds = ["textVisible", "imageVisible", "pixelColor", "windowFocused", "processRunning", "not", "all", "any"];

    public static IReadOnlyList<MacroIssue> Validate(MacroDoc doc)
    {
        var issues = new List<MacroIssue>();

        if (doc.Schema < 1 || doc.Schema > MacroLimits.CurrentSchema)
            issues.Add(new(null, $"Schema version {doc.Schema} isn't supported (newest known is {MacroLimits.CurrentSchema})."));
        if (string.IsNullOrWhiteSpace(doc.Name) || doc.Name.Length > MacroLimits.NameMaxLength)
            issues.Add(new(null, $"Name must be 1–{MacroLimits.NameMaxLength} characters."));
        if (doc.Blocks.Count > MacroLimits.MaxBlocks)
            issues.Add(new(null, $"A macro can have at most {MacroLimits.MaxBlocks} blocks."));
        if (MacroJson.ByteSize(doc) > MacroLimits.MaxDocumentBytes)
            issues.Add(new(null, "The macro is larger than 200 KB."));

        ValidateOptions(doc.Options, issues);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in doc.Blocks)
        {
            if (string.IsNullOrWhiteSpace(block.Id) || !ids.Add(block.Id))
                issues.Add(new(block.Id, "Block ids must be present and unique."));
            if (!BlockTypes.All.Contains(block.Type))
            {
                issues.Add(new(block.Id, $"Unknown block type \"{block.Type}\"."));
                continue;
            }
            ValidateParams(block, issues);
        }

        issues.AddRange(MacroStructure.Analyze(doc.Blocks).Issues);
        return issues;
    }

    private static void ValidateOptions(MacroOptions o, List<MacroIssue> issues)
    {
        if (o.TriggerMode is not (TriggerModes.Toggle or TriggerModes.Hold or TriggerModes.Once))
            issues.Add(new(null, "Trigger mode must be toggle, hold or once."));
        if (o.HotkeyStart is not null && !Hotkey.TryParse(o.HotkeyStart, out _))
            issues.Add(new(null, $"Start hotkey \"{o.HotkeyStart}\" isn't a key combo Night-loop knows."));
        if (o.HotkeyStop is not null && !Hotkey.TryParse(o.HotkeyStop, out _))
            issues.Add(new(null, $"Stop hotkey \"{o.HotkeyStop}\" isn't a key combo Night-loop knows."));

        switch (o.Repeat.Mode)
        {
            case RepeatModes.Forever: break;
            case RepeatModes.Count when o.Repeat.Count is >= MacroLimits.CountMin and <= MacroLimits.CountMax: break;
            case RepeatModes.Duration when o.Repeat.Duration is >= MacroLimits.DurationMinSeconds and <= MacroLimits.DurationMaxSeconds: break;
            default: issues.Add(new(null, "Repeat must be forever, a count of 1–100,000 or a duration of 1–86,400 seconds.")); break;
        }

        if (o.Humanize.TimingJitterPct is < 0 or > MacroLimits.TimingJitterMaxPct)
            issues.Add(new(null, "Timing jitter must be 0–50%."));
        if (o.Humanize.MousePx is < 0 or > MacroLimits.HumanizeMouseMaxPx)
            issues.Add(new(null, "Mouse jitter must be 0–10 px."));
    }

    private static void ValidateParams(Block b, List<MacroIssue> issues)
    {
        void Need(bool ok, string message)
        {
            if (!ok) issues.Add(new(b.Id, message));
        }

        bool IntIn(string name, int min, int max) => b.GetInt(name) is { } v && v >= min && v <= max;
        bool OptIntIn(string name, int min, int max) => !b.Params.ContainsKey(name) || IntIn(name, min, max);
        bool OneOf(string name, HashSet<string> allowed, string? fallback = null) => (b.GetString(name) ?? fallback) is { } s && allowed.Contains(s);
        bool Text(string name, int max, bool required = true) => b.GetString(name) is { } s ? s.Length <= max && (!required || s.Length > 0) : !required;

        switch (b.Type)
        {
            case BlockTypes.Key:
                Need(b.GetString("key") is { } k && KeyCatalog.IsKnown(k), $"Unknown key \"{b.GetString("key")}\".");
                Need(OneOf("action", KeyActions, "press"), "Key action must be press, down or up.");
                Need(OptIntIn("holdMs", 0, MacroLimits.WaitMaxMs), "Hold time must be 0 ms – 1 hour.");
                break;
            case BlockTypes.Click:
                Need(OneOf("button", Buttons, "left"), "Button must be left, right, middle, x1 or x2.");
                Need(OneOf("action", KeyActions, "press"), "Click action must be press, down or up.");
                Need(OptIntIn("clicks", MacroLimits.ClicksMin, MacroLimits.ClicksMax), "Clicks must be 1–3.");
                Need(OptIntIn("holdMs", 0, MacroLimits.WaitMaxMs), "Hold time must be 0 ms – 1 hour.");
                Need(OneOf("relativeTo", RelativeTo, "screen"), "relativeTo must be screen or gameWindow.");
                Need(b.Params.ContainsKey("x") == b.Params.ContainsKey("y"), "Give both x and y, or neither to click where the mouse is.");
                break;
            case BlockTypes.Move:
                Need(b.GetInt("x") is not null && b.GetInt("y") is not null, "Move needs x and y.");
                Need(OneOf("relativeTo", RelativeTo, "screen"), "relativeTo must be screen or gameWindow.");
                Need(OptIntIn("durationMs", 0, MacroLimits.WaitMaxMs), "Duration must be 0 ms – 1 hour.");
                break;
            case BlockTypes.Drag:
                Need(b.GetPoint("from") is not null && b.GetPoint("to") is not null, "Drag needs from and to points.");
                Need(OneOf("button", Buttons, "left"), "Button must be left, right, middle, x1 or x2.");
                Need(OneOf("relativeTo", RelativeTo, "screen"), "relativeTo must be screen or gameWindow.");
                Need(OptIntIn("durationMs", 0, MacroLimits.WaitMaxMs), "Duration must be 0 ms – 1 hour.");
                break;
            case BlockTypes.Scroll:
                Need(IntIn("amount", MacroLimits.ScrollMin, MacroLimits.ScrollMax), "Scroll amount must be -50 to 50.");
                break;
            case BlockTypes.Type:
                Need(Text("text", MacroLimits.TypeTextMaxLength), "Type text must be 1–200 characters.");
                Need(OptIntIn("delayMs", 0, 10_000), "Delay between characters must be 0–10,000 ms.");
                break;
            case BlockTypes.CameraTurn:
                Need(OneOf("direction", Directions), "Direction must be left, right, up or down.");
                Need(IntIn("amountPx", 1, 100_000), "Amount must be 1–100,000 px.");
                break;
            case BlockTypes.Wait:
                Need(IntIn("ms", MacroLimits.WaitMinMs, MacroLimits.WaitMaxMs), "Wait must be 1 ms – 1 hour.");
                break;
            case BlockTypes.WaitRandom:
                Need(IntIn("minMs", MacroLimits.WaitMinMs, MacroLimits.WaitMaxMs) && IntIn("maxMs", MacroLimits.WaitMinMs, MacroLimits.WaitMaxMs)
                     && b.GetInt("minMs") <= b.GetInt("maxMs"), "Random wait needs 1 ms ≤ min ≤ max ≤ 1 hour.");
                break;
            case BlockTypes.Pause:
                Need(Text("target", MacroLimits.NameMaxLength), "Pause needs the name of the macro to pause.");
                Need(OneOf("mode", ["pause", "resume"]), "Mode must be pause or resume.");
                break;
            case BlockTypes.If or BlockTypes.RepeatUntil:
                ValidateCondition(b.GetObject("condition"), b.Id, issues, 0);
                if (b.Type == BlockTypes.RepeatUntil)
                    Need(OptIntIn("maxIterations", MacroLimits.CountMin, MacroLimits.CountMax), "Max iterations must be 1–100,000.");
                break;
            case BlockTypes.Loop:
                Need(IntIn("count", MacroLimits.CountMin, MacroLimits.CountMax), "Loop count must be 1–100,000.");
                break;
            case BlockTypes.Label or BlockTypes.Goto:
                Need(Text("name", 40), "Label name must be 1–40 characters.");
                break;
            case BlockTypes.Comment:
                Need(Text("text", MacroLimits.CommentMaxLength, required: false), "Comment must be at most 200 characters.");
                break;
            case BlockTypes.Description:
                Need(Text("text", MacroLimits.DescriptionMaxLength, required: false), "Description must be at most 5,000 characters.");
                break;
            case BlockTypes.WindowResize:
                Need(IntIn("width", 1, 16_384) && IntIn("height", 1, 16_384), "Width and height must be 1–16,384.");
                break;
            case BlockTypes.WindowPosition:
                Need(IntIn("x", -32_768, 32_767) && IntIn("y", -32_768, 32_767), "Window position needs x and y.");
                break;
            case BlockTypes.Notify:
                Need(Text("message", 200), "Notification text must be 1–200 characters.");
                break;
            case BlockTypes.RunMacro:
                Need(Text("macroName", MacroLimits.NameMaxLength), "Run macro needs a macro name.");
                break;
        }
    }

    private static void ValidateCondition(JsonObject? c, string blockId, List<MacroIssue> issues, int depth)
    {
        var kind = (c?["kind"] as JsonValue)?.TryGetValue(out string? k) == true ? k : null;
        if (c is null || kind is null || !ConditionKinds.Contains(kind))
        {
            issues.Add(new(blockId, "Condition kind must be textVisible, imageVisible, pixelColor, windowFocused, processRunning, not, all or any."));
            return;
        }
        if (depth > 4)
        {
            issues.Add(new(blockId, "Condition is nested too deeply."));
            return;
        }
        switch (kind)
        {
            case "not":
                ValidateCondition(c["condition"] as JsonObject, blockId, issues, depth + 1);
                break;
            case "all" or "any":
                if (c["conditions"] is not JsonArray children || children.Count is 0 or > MacroLimits.ConditionGroupMaxChildren)
                {
                    issues.Add(new(blockId, "All/any groups hold 1–5 conditions."));
                    break;
                }
                foreach (var child in children)
                    ValidateCondition(child as JsonObject, blockId, issues, depth + 1);
                break;
            case "processRunning":
                if ((c["name"] as JsonValue)?.TryGetValue(out string? process) != true || string.IsNullOrWhiteSpace(process))
                    issues.Add(new(blockId, "Process running needs a process name."));
                break;
        }
    }
}
