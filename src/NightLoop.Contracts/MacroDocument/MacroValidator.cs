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
    private static readonly HashSet<string> ConditionKinds = ["textVisible", "imageVisible", "pixelColor", "windowFocused", "processRunning", "keyHeld", "not", "all", "any"];
    private static readonly HashSet<string> MoveModes = ["absolute", "relative"];
    private static readonly HashSet<string> FailActions = ["continue", "retry", "goto", "stop"];
    private static readonly HashSet<string> FindTargets = ["text", "image", "pixel"];
    private static readonly HashSet<string> TurnMethods = ["rightDrag", "arrowKeys", "mouseMove"];
    private static readonly HashSet<string> Anchors = ["center", "topLeft", "topRight", "bottomLeft", "bottomRight", "custom"];
    private static readonly HashSet<string> NotifyKinds = ["toast", "sound", "both"];
    private static readonly HashSet<string> TextMatches = ["exact", "contains"];
    private static readonly HashSet<string> PauseWhen = ["now", "afterBlock"];
    private static readonly HashSet<string> ResumeFrom = ["paused", "start"];

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

        var structure = MacroStructure.Analyze(doc.Blocks);
        issues.AddRange(structure.Issues);
        foreach (var block in doc.Blocks)
        {
            if (!BlockTypes.HasOnFail(block.Type) || block.GetString("onFail") != "goto") continue;
            var label = block.GetString("failLabel");
            if (string.IsNullOrWhiteSpace(label) || !structure.Labels.ContainsKey(label))
                issues.Add(new(block.Id, $"On fail goes to label \"{label}\", but there's no Label block with that name."));
        }
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
        if (o.SpeedPct is < MacroLimits.SpeedMinPct or > MacroLimits.SpeedMaxPct)
            issues.Add(new(null, "Speed must be 10–1,000%."));
    }

    /// <summary>"Ctrl+Shift+E" or "Ctrl + E": every part must be a key Night-loop knows.</summary>
    public static bool IsKeyCombo(string? keys)
    {
        if (string.IsNullOrWhiteSpace(keys)) return false;
        var parts = keys.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length is > 0 and <= 6 && parts.All(KeyCatalog.IsKnown);
    }

    /// <summary>A screen area: [x, y, width, height] (schema section 6). Missing means the whole game window (or screen).</summary>
    public static bool IsRegion(JsonNode? node) => node is null || ReadRegion(node) is not null;

    /// <summary>Reads a region as [x, y, width, height]; {x, y, width, height} is accepted too.</summary>
    public static (int X, int Y, int Width, int Height)? ReadRegion(JsonNode? node)
    {
        int? x, y, w, h;
        switch (node)
        {
            case JsonArray { Count: 4 } a:
                (x, y, w, h) = (BlockParams.AsInt(a[0]), BlockParams.AsInt(a[1]), BlockParams.AsInt(a[2]), BlockParams.AsInt(a[3]));
                break;
            case JsonObject o:
                (x, y, w, h) = (BlockParams.AsInt(o["x"]), BlockParams.AsInt(o["y"]), BlockParams.AsInt(o["width"]), BlockParams.AsInt(o["height"]));
                break;
            default:
                return null;
        }
        if (x is not (>= -32_768 and <= 32_767) || y is not (>= -32_768 and <= 32_767)) return null;
        if (w is not (>= 1 and <= 16_384) || h is not (>= 1 and <= 16_384)) return null;
        return (x.Value, y.Value, w.Value, h.Value);
    }

    /// <summary>
    /// A recorded mouse path: up to <see cref="MacroLimits.PathMaxPoints"/> [x, y, ms] points, ms rising from 0 to
    /// at most <paramref name="durationMs"/>.
    /// </summary>
    public static bool IsPath(JsonNode? node, int durationMs)
    {
        if (node is null) return true;
        if (node is not JsonArray a || a.Count is 0 or > MacroLimits.PathMaxPoints) return false;
        var last = 0;
        foreach (var item in a)
        {
            if (item is not JsonArray p || p.Count != 3) return false;
            int? Int(int i) => BlockParams.AsInt(p[i]);
            if (Int(0) is not (>= -100_000 and <= 100_000) || Int(1) is not (>= -100_000 and <= 100_000)) return false;
            if (Int(2) is not { } t || t < last || t > durationMs) return false;
            last = t;
        }
        return true;
    }

    /// <summary>"#RRGGBB".</summary>
    public static bool IsColor(string? text) =>
        text is { Length: 7 } && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit);

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
        bool OptBool(string name) => !b.Params.ContainsKey(name) || b.GetBool(name) is not null;
        void Region() => Need(IsRegion(b.Params["region"]), "Region must be x, y, width and height (or left as the full window).");
        void Process() => Need(Text("process", MacroLimits.PathMaxLength, required: false), "Process name is too long.");
        void Image() => Need(Text("imageData", MacroLimits.ImageDataMaxLength, required: false) && Text("image", 120, required: false),
            "The saved picture is too large. Capture a smaller area.");
        void OnFail() => Need(OneOf("onFail", FailActions, "continue"), "On fail must be continue, retry, go to label or stop.");

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
                Need(OptIntIn("moveMs", 0, MacroLimits.MoveMaxMs), "Move time must be 0–10,000 ms.");
                break;
            case BlockTypes.Move:
                Need(b.GetInt("x") is not null && b.GetInt("y") is not null, "Move needs x and y.");
                Need(OneOf("mode", MoveModes, "absolute"), "Move mode must be absolute or relative.");
                Need(OneOf("relativeTo", RelativeTo, "screen"), "relativeTo must be screen or gameWindow.");
                Need(OptIntIn("durationMs", 0, MacroLimits.WaitMaxMs), "Duration must be 0 ms – 1 hour.");
                Need(IsPath(b.Params["path"], b.GetInt("durationMs", 0)), "The recorded mouse path is damaged. Record it again.");
                break;
            case BlockTypes.Drag:
                Need(b.GetPoint("from") is not null && b.GetPoint("to") is not null, "Drag needs from and to points.");
                Need(OneOf("button", Buttons, "left"), "Button must be left, right, middle, x1 or x2.");
                Need(OneOf("relativeTo", RelativeTo, "screen"), "relativeTo must be screen or gameWindow.");
                Need(OptIntIn("durationMs", 0, MacroLimits.WaitMaxMs), "Duration must be 0 ms – 1 hour.");
                Need(OptIntIn("moveMs", 0, MacroLimits.MoveMaxMs), "Move time must be 0–10,000 ms.");
                Need(IsPath(b.Params["path"], b.GetInt("durationMs", 300)), "The recorded drag path is damaged. Record it again.");
                break;
            case BlockTypes.Scroll:
                if (b.Params.ContainsKey("direction"))
                {
                    Need(OneOf("direction", ["up", "down"]), "Scroll direction must be up or down.");
                    Need(IntIn("amount", 1, MacroLimits.ScrollMax), "Notches must be 1–50.");
                }
                else Need(IntIn("amount", MacroLimits.ScrollMin, MacroLimits.ScrollMax), "Scroll amount must be -50 to 50.");
                break;
            case BlockTypes.Type:
                Need(Text("text", MacroLimits.TypeTextMaxLength), "Type text must be 1–200 characters.");
                Need(OptIntIn("delayMs", 0, 10_000), "Delay between characters must be 0–10,000 ms.");
                break;
            case BlockTypes.CameraTurn:
                Need(OneOf("direction", Directions), "Direction must be left, right, up or down.");
                Need(IntIn("amountPx", 1, 100_000), "Amount must be 1–100,000 px.");
                Need(OneOf("method", TurnMethods, "mouseMove"), "Method must be right-drag, arrow keys or mouse move.");
                Need(OptIntIn("durationMs", 0, MacroLimits.WaitMaxMs), "Duration must be 0 ms – 1 hour.");
                break;
            case BlockTypes.Combo:
                Need(IsKeyCombo(b.GetString("keys")), $"\"{b.GetString("keys")}\" isn't a key combo Night-loop knows, e.g. Ctrl+Shift+E.");
                Need(OptIntIn("holdMs", 0, MacroLimits.WaitMaxMs), "Hold time must be 0 ms – 1 hour.");
                break;
            case BlockTypes.HoldKey:
                Need(b.GetString("key") is { } hk && KeyCatalog.IsKnown(hk), $"Unknown key \"{b.GetString("key")}\".");
                Need(IntIn("holdMs", 1, MacroLimits.WaitMaxMs), "Hold time must be 1 ms – 1 hour.");
                break;
            case BlockTypes.CameraZoom:
                Need(OneOf("direction", ["in", "out"]), "Zoom must be in or out.");
                Need(IntIn("steps", 1, MacroLimits.ZoomStepsMax), "Zoom steps must be 1–50.");
                break;
            case BlockTypes.CameraCenter:
                Need(OptIntIn("tolerancePx", 1, 10_000), "Tolerance must be 1–10,000 px.");
                Need(OneOf("method", TurnMethods, "mouseMove"), "Method must be right-drag, arrow keys or mouse move.");
                break;
            case BlockTypes.WaitForKey:
                Need(b.GetString("key") is { } wk && KeyCatalog.IsKnown(wk), $"Unknown key \"{b.GetString("key")}\".");
                Need(OptIntIn("timeoutMs", 0, MacroLimits.WaitMaxMs), "Timeout must be 0 ms – 1 hour.");
                break;
            case BlockTypes.WaitForText:
                Need(Text("text", MacroLimits.TypeTextMaxLength), "Wait for text needs the text to look for (1–200 characters).");
                Need(OneOf("match", TextMatches, "exact"), "Match must be exact or contains.");
                Need(OptBool("matchCase"), "Case must be on or off.");
                Need(OptIntIn("timeoutMs", 0, MacroLimits.WaitMaxMs), "Timeout must be 0 ms – 1 hour.");
                Region();
                OnFail();
                break;
            case BlockTypes.WaitForImage:
                Need(OptIntIn("timeoutMs", 0, MacroLimits.WaitMaxMs), "Timeout must be 0 ms – 1 hour.");
                Need(OptIntIn("confidencePct", 1, 100), "Confidence must be 1–100%.");
                Image();
                Region();
                OnFail();
                break;
            case BlockTypes.DetectPixelColor:
                Need(b.GetInt("x") is not null && b.GetInt("y") is not null, "Wait for pixel needs x and y.");
                Need(IsColor(b.GetString("color")), "Color must look like #RRGGBB.");
                Need(OptIntIn("tolerance", 0, 255), "Tolerance must be 0–255.");
                Need(OptIntIn("timeoutMs", 0, MacroLimits.WaitMaxMs), "Timeout must be 0 ms – 1 hour.");
                OnFail();
                Need(OneOf("relativeTo", RelativeTo, "screen"), "relativeTo must be screen or gameWindow.");
                break;
            case BlockTypes.FindClick:
                Need(OneOf("target", FindTargets, "text"), "Find must be text, image or pixel.");
                Need(Text("value", MacroLimits.TypeTextMaxLength), "Say what to find (1–200 characters).");
                Need(OneOf("button", Buttons, "left"), "Button must be left, right, middle, x1 or x2.");
                Need(OptIntIn("tolerance", 0, 255), "Tolerance must be 0–255.");
                Need(OptIntIn("timeoutMs", 0, MacroLimits.WaitMaxMs), "Timeout must be 0 ms – 1 hour.");
                Image();
                Region();
                OnFail();
                if (b.GetString("target") == "pixel") Need(IsColor(b.GetString("value")), "For a pixel color, the value must look like #RRGGBB.");
                break;
            case BlockTypes.ScanTurn:
                Need(OneOf("target", FindTargets, "text"), "Look for must be text, image or pixel.");
                Need(Text("value", MacroLimits.TypeTextMaxLength), "Say what to look for (1–200 characters).");
                Need(OneOf("direction", ["left", "right"], "right"), "Turn must be left or right.");
                Need(OptIntIn("stepPx", 1, 10_000), "Step must be 1–10,000 px.");
                Need(OptIntIn("maxTurns", 1, MacroLimits.ScanTurnsMax), "Max turns must be 1–360.");
                Need(OneOf("whenFound", ["click", "center", "continue"], "click"), "When found must be click, center or continue.");
                Need(OneOf("method", TurnMethods, "mouseMove"), "Method must be right-drag, arrow keys or mouse move.");
                Need(OptIntIn("tolerance", 0, 255), "Tolerance must be 0–255.");
                Image();
                Region();
                OnFail();
                if (b.GetString("target") == "pixel") Need(IsColor(b.GetString("value")), "For a pixel color, the value must look like #RRGGBB.");
                break;
            case BlockTypes.WaitForWindow:
                Process();
                Need(OneOf("until", ["front", "open"], "front"), "Wait until must be \"in front\" or \"open\".");
                Need(OptIntIn("timeoutMs", 0, MacroLimits.WaitMaxMs), "Timeout must be 0 ms – 1 hour.");
                break;
            case BlockTypes.WindowOpen:
                Need(Text("path", MacroLimits.PathMaxLength), "Pick the program to open.");
                Need(Text("args", MacroLimits.PathMaxLength, required: false), "Arguments must be at most 260 characters.");
                Need(OneOf("ifRunning", ["focus", "new"], "focus"), "If it's already running must be focus or open another.");
                Need(OptBool("wait"), "Wait for its window must be yes or no.");
                Need(OptIntIn("timeoutMs", 0, MacroLimits.WaitMaxMs), "Timeout must be 0 ms – 1 hour.");
                break;
            case BlockTypes.Screenshot:
                Region();
                Need(Text("file", MacroLimits.FileNameMaxLength, required: false)
                     && (b.GetString("file") ?? "").IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0,
                    "File name must be up to 80 characters, without / \\ : * ? \" < > |.");
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
                Need(OneOf("mode", ["pause", "resume"], "pause"), "Mode must be pause or resume.");
                Need(OneOf("when", PauseWhen, "afterBlock"), "Pause must be right away or after its current block.");
                Need(OptBool("autoResume"), "Resume when this macro ends must be yes or no.");
                break;
            case BlockTypes.Resume:
                Need(Text("target", MacroLimits.NameMaxLength), "Resume needs the name of the macro to resume.");
                Need(OneOf("from", ResumeFrom, "paused"), "Continue from must be where it paused or the start.");
                break;
            case BlockTypes.If or BlockTypes.RepeatUntil:
                ValidateCondition(b.GetObject("condition"), b.Id, issues, 0);
                if (b.Type == BlockTypes.RepeatUntil)
                    Need(OptIntIn("maxIterations", 0, MacroLimits.CountMax), "Max tries must be 0 (no limit) to 100,000.");
                break;
            case BlockTypes.Loop:
                Need(b.GetBool("forever") == true || IntIn("count", MacroLimits.CountMin, MacroLimits.CountMax),
                    "Loop count must be 1–100,000, or Forever.");
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
                Process();
                Need(IntIn("width", 1, 16_384) && IntIn("height", 1, 16_384), "Width and height must be 1–16,384.");
                break;
            case BlockTypes.WindowPosition:
                Process();
                Need(OneOf("anchor", Anchors, "custom"), "Snap to must be center, a corner or custom.");
                if (b.GetString("anchor", "custom") == "custom")
                    Need(IntIn("x", -32_768, 32_767) && IntIn("y", -32_768, 32_767), "Window position needs x and y.");
                Need(OptIntIn("monitor", 0, 16), "Monitor must be 0 (current) to 16.");
                break;
            case BlockTypes.WindowFocus:
                Process();
                break;
            case BlockTypes.Notify:
                Need(OneOf("kind", NotifyKinds, "toast"), "Type must be toast, sound or both.");
                Need(Text("message", 200, required: b.GetString("kind", "toast") != "sound"), "Notification text must be 1–200 characters.");
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
            issues.Add(new(blockId, "Condition kind must be textVisible, imageVisible, pixelColor, windowFocused, processRunning, keyHeld, not, all or any."));
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
            case "keyHeld":
                if ((c["key"] as JsonValue)?.TryGetValue(out string? key) != true || key is null || !KeyCatalog.IsKnown(key))
                    issues.Add(new(blockId, "Key held needs a key Night-loop knows."));
                break;
            case "pixelColor":
                if ((c["color"] as JsonValue)?.TryGetValue(out string? color) != true || !IsColor(color))
                    issues.Add(new(blockId, "Pixel matches needs a color like #RRGGBB."));
                if (BlockParams.AsInt(c["x"]) is null || BlockParams.AsInt(c["y"]) is null)
                    issues.Add(new(blockId, "Pixel matches needs the point to check, like 960, 540, #FF0000."));
                break;
            case "textVisible":
                if ((c["text"] as JsonValue)?.TryGetValue(out string? text) != true || string.IsNullOrWhiteSpace(text) || text.Length > MacroLimits.TypeTextMaxLength)
                    issues.Add(new(blockId, "Text visible needs the text to look for (1–200 characters)."));
                if (!IsRegion(c["region"]))
                    issues.Add(new(blockId, "Region must have x, y, width and height."));
                break;
            case "imageVisible":
                if ((c["imageData"] as JsonValue)?.TryGetValue(out string? data) == true && data.Length > MacroLimits.ImageDataMaxLength)
                    issues.Add(new(blockId, "The saved picture is too large. Capture a smaller area."));
                break;
        }
    }
}
