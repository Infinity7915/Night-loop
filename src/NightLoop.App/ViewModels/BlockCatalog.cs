using System.Globalization;
using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.App.ViewModels;

/// <summary>How a block field is edited in the inspector.</summary>
public enum FieldKind
{
    /// <summary>One line of text (numbers, keys, colors, regions…).</summary>
    Text,
    /// <summary>Multi-line text that grows to fit (Description).</summary>
    MultiLine,
    /// <summary>Pick one of <see cref="FieldDef.Choices"/>.</summary>
    Choice,
    /// <summary>A saved macro's name, picked from the account's macros.</summary>
    MacroName,
    /// <summary>A Label in this macro (Go to label, On fail → Go to label).</summary>
    LabelName,
    /// <summary>A program: clicking the box opens File Explorer to pick it (or pick a running app).</summary>
    Program,
    /// <summary>A number of ms on a slider. Blank follows Settings › Macros (Wait after click).</summary>
    Slider,
}

/// <summary>One choice: the value stored in JSON and what the user sees.</summary>
public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One editable field. <see cref="Get"/> reads the block as text and <see cref="Set"/> writes it back, returning an error
/// message instead when the text isn't valid (the block then keeps its last good value).
/// </summary>
public sealed record FieldDef(
    string Key,
    string Label,
    FieldKind Kind,
    Func<Block, string> Get,
    Func<Block, string, string?> Set,
    IReadOnlyList<Choice>? Choices = null,
    string? Placeholder = null,
    string? Hint = null,
    int MaxLength = 200,
    Func<Block, bool>? ShowIf = null,
    bool AllMacros = false,
    bool Mono = true,
    bool FullPath = false,
    int SliderMax = 0);

/// <summary>A palette entry: one block type with its defaults, inspector fields and one-line summary.</summary>
public sealed record BlockDef(
    string Key,
    string Type,
    string Label,
    string Tag,
    string Category,
    string Description,
    Func<JsonObject> Defaults,
    IReadOnlyList<FieldDef> Fields,
    Func<Block, string> Summary,
    bool Hidden = false)
{
    /// <summary>The category's color, for the palette card's edge.</summary>
    public string Color => BlockCatalog.ColorOf(Category);
}

/// <summary>
/// The block catalog from the Macros mockup: 41 blocks in seven categories, with the mockup's labels, tags,
/// descriptions, fields and summaries. The palette, the inspector and the timeline all read from here. Hidden entries
/// only edit blocks from older macros.
/// </summary>
public static class BlockCatalog
{
    public static readonly IReadOnlyList<(string Name, string Color)> Categories =
    [
        ("Input", "#A78BFA"), ("Camera", "#F472B6"), ("Timing", "#9AA4B2"), ("Detect", "#F59E0B"),
        ("Logic", "#58A6FF"), ("Window", "#4CC9F0"), ("Utility", "#C9D1D9"),
    ];

    private static readonly Choice[] Buttons = [new("left", "Left"), new("right", "Right"), new("middle", "Middle")];
    private static readonly Choice[] KeyActions = [new("press", "Press"), new("down", "Hold down"), new("up", "Let go")];
    private static readonly Choice[] ClickActions = [new("press", "Click"), new("down", "Hold down"), new("up", "Let go")];
    private static readonly Choice[] WaitUntil = [new("front", "Open and in front"), new("open", "Open")];
    private static readonly Choice[] CloseHow =
        [new("close", "Close (like clicking X)"), new("closeThenEnd", "Close, then force if it's still open"), new("end", "Force close now (unsaved work is lost)")];
    private static readonly Choice[] IfRunning = [new("focus", "Bring it to the front"), new("new", "Open another one")];
    private static readonly Choice[] ClickCounts = [new("1", "1"), new("2", "2"), new("3", "3")];
    private static readonly Choice[] MoveModes = [new("absolute", "Absolute"), new("relative", "Relative")];
    private static readonly Choice[] UpDown = [new("up", "Up"), new("down", "Down")];
    private static readonly Choice[] ScanMethods =
        [new("rightDrag", "Mouse (right-drag)"), new("mouseMove", "Mouse (camera follows mouse)"), new("arrowKeys", "Arrow keys")];
    private static readonly Choice[] TurnMethods = [new("rightDrag", "Right-drag"), new("arrowKeys", "Arrow keys"), new("mouseMove", "Mouse move")];
    private static readonly Choice[] Directions = [new("right", "Right"), new("left", "Left"), new("up", "Up"), new("down", "Down")];
    private static readonly Choice[] RightLeft = [new("right", "Right"), new("left", "Left")];
    private static readonly Choice[] Zoom = [new("in", "In"), new("out", "Out")];
    private static readonly Choice[] PauseWhen = [new("now", "Right away"), new("afterBlock", "After its current block")];
    private static readonly Choice[] ResumeFrom = [new("paused", "Where it paused"), new("start", "The start")];
    private static readonly Choice[] Matches = [new("exact", "Exact"), new("contains", "Contains")];
    private static readonly Choice[] FindTargets = [new("text", "Text"), new("image", "Image"), new("pixel", "Pixel color")];
    private static readonly Choice[] WhenFound = [new("click", "Click it"), new("center", "Center camera on it"), new("continue", "Continue")];
    private static readonly Choice[] Monitors = [new("1", "Primary"), new("2", "Monitor 2"), new("3", "Monitor 3"), new("0", "Current")];
    private static readonly Choice[] Anchors =
    [
        new("center", "Center"), new("topLeft", "Top-left"), new("topRight", "Top-right"),
        new("bottomLeft", "Bottom-left"), new("bottomRight", "Bottom-right"), new("custom", "Custom"),
    ];
    private static readonly Choice[] NotifyKinds = [new("toast", "Notify"), new("sound", "Sound"), new("both", "Notify + sound")];
    private static readonly Choice[] Sounds = [new("default", "Default"), new("beep", "Beep"), new("exclamation", "Exclamation"), new("asterisk", "Asterisk"), new("question", "Question")];

    private const int Coord = 32_767;

    public static readonly IReadOnlyList<BlockDef> All =
    [
        // ---- Input (8) ----
        new("key", BlockTypes.Key, "Key", "KEY", "Input", "Press a key, or hold it down and let go later. Blocks between a Hold down and its Let go run while the key is held, like Ctrl + T.",
            () => new() { ["key"] = "E", ["action"] = "press" },
            [F.Key("key", "Key"), F.Pick("action", "Action", KeyActions, "press"),
             F.Int("holdMs", "Hold for (ms)", 0, MacroLimits.WaitMaxMs, optional: true, placeholder: "30",
                 hint: "How long the key stays down.") with { ShowIf = IsPress }],
            KeySummary),
        new("combo", BlockTypes.Combo, "Key combo", "CMB", "Input", "Press several keys together, like Ctrl + Shift + E.",
            () => new() { ["keys"] = "Ctrl + E" },
            [F.Combo("keys", "Keys")],
            b => b.GetString("keys", "")),
        new("holdkey", BlockTypes.HoldKey, "Hold key", "HLD", "Input", "Hold a key for a set time. Good for walking.",
            () => new() { ["key"] = "W", ["holdMs"] = 1000 },
            [F.Key("key", "Key"), F.Int("holdMs", "Hold for (ms)", 1, MacroLimits.WaitMaxMs)],
            b => $"{b.GetString("key", "?")} for {b.GetInt("holdMs", 0)} ms"),
        new("click", BlockTypes.Click, "Click", "CLK", "Input", "Click at a position, or where the mouse is.",
            () => new() { ["button"] = "left", ["clicks"] = 1, ["x"] = 960, ["y"] = 540, ["relativeTo"] = "gameWindow" },
            [F.Pick("button", "Button", Buttons, "left"), F.Pick("action", "Action", ClickActions, "press"),
             F.IntPick("clicks", "Clicks", ClickCounts, 1) with { ShowIf = IsPress },
             F.Int("holdMs", "Hold for (ms)", 0, MacroLimits.WaitMaxMs, optional: true, placeholder: "20",
                 hint: "How long the button stays down.") with { ShowIf = IsPress },
             F.Int("x", "X", -Coord, Coord, optional: true, placeholder: "Where the mouse is"),
             F.Int("y", "Y", -Coord, Coord, optional: true, placeholder: "Where the mouse is"),
             F.Int("moveMs", "Glide there (ms)", 0, MacroLimits.MoveMaxMs, optional: true, placeholder: "Automatic",
                 hint: "0 jumps straight there."),
             F.WaitAfterClick()],
            ClickSummary),
        new("move", BlockTypes.Move, "Mouse move", "MOV", "Input", "Move to a point, or by an offset.",
            () => new() { ["mode"] = "absolute", ["x"] = 0, ["y"] = 0, ["durationMs"] = 120, ["relativeTo"] = "gameWindow" },
            [F.Pick("mode", "Mode", MoveModes, "absolute"), F.Int("x", "X", -Coord, Coord), F.Int("y", "Y", -Coord, Coord),
             F.Int("durationMs", "Duration (ms)", 0, MacroLimits.WaitMaxMs)],
            b => $"{(b.GetString("mode") == "relative" ? "by " : "to ")}{b.GetInt("x", 0)}, {b.GetInt("y", 0)} · {b.GetInt("durationMs", 0)} ms"),
        new("drag", BlockTypes.Drag, "Drag", "DRG", "Input", "Hold a button and drag between two points.",
            () => new()
            {
                ["button"] = "left", ["from"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["to"] = new JsonObject { ["x"] = 100, ["y"] = 0 },
                ["durationMs"] = 300, ["relativeTo"] = "gameWindow",
            },
            [F.Pick("button", "Button", Buttons, "left"),
             F.Int("from.x", "From X", -Coord, Coord), F.Int("from.y", "From Y", -Coord, Coord),
             F.Int("to.x", "To X", -Coord, Coord), F.Int("to.y", "To Y", -Coord, Coord), F.WaitAfterClick()],
            b => b.GetPoint("from") is { } f && b.GetPoint("to") is { } t ? $"{f.X},{f.Y} → {t.X},{t.Y}" : ""),
        new("scroll", BlockTypes.Scroll, "Scroll", "SCR", "Input", "Scroll the mouse wheel up or down a number of ticks.",
            () => new() { ["direction"] = "down", ["amount"] = 3 },
            [F.ScrollDirection(), F.ScrollAmount()],
            b => $"{Label(UpDown, F.ScrollDir(b))} {F.Ticks(Math.Abs(b.GetInt("amount", 0)))}"),
        new("text", BlockTypes.Type, "Type text", "TXT", "Input", "Type a string, like a chat message.",
            () => new() { ["text"] = "gg", ["delayMs"] = 30 },
            [F.Text("text", "Text", MacroLimits.TypeTextMaxLength, mono: false), F.Int("delayMs", "Delay per key (ms)", 0, 10_000)],
            b => $"\"{b.GetString("text", "")}\""),

        // ---- Camera (3) ----
        new("camturn", BlockTypes.CameraTurn, "Turn camera", "CAM", "Camera", "Rotate the view by right-drag, arrow keys or mouse.",
            () => new() { ["method"] = "rightDrag", ["direction"] = "right", ["amountPx"] = 120 },
            [F.Pick("method", "Method", TurnMethods, "mouseMove"), F.Pick("direction", "Direction", Directions, "right"),
             F.Int("amountPx", "Amount (px)", 1, 100_000)],
            b => $"{Label(Directions, b.GetString("direction", "right"))} {b.GetInt("amountPx", 0)}px · {Label(TurnMethods, b.GetString("method", "mouseMove"))}"),
        new("camzoom", BlockTypes.CameraZoom, "Zoom camera", "ZM", "Camera", "Zoom in or out a number of steps.",
            () => new() { ["direction"] = "out", ["steps"] = 5 },
            [F.Pick("direction", "Zoom", Zoom, "out"), F.Int("steps", "Steps", 1, MacroLimits.ZoomStepsMax)],
            b => $"{Label(Zoom, b.GetString("direction", "out"))} ×{b.GetInt("steps", 1)}"),
        new("camcenter", BlockTypes.CameraCenter, "Center on target", "CTR", "Camera", "Turn until the last found match is in the middle.",
            () => new() { ["tolerancePx"] = 40 },
            [F.Int("tolerancePx", "Tolerance (px)", 1, 10_000)],
            b => $"within {b.GetInt("tolerancePx", 40)}px"),

        // ---- Timing (5) ----
        new("wait", BlockTypes.Wait, "Wait", "WAIT", "Timing", "Pause for a fixed time.",
            () => new() { ["ms"] = 500 },
            [F.Int("ms", "Duration (ms)", MacroLimits.WaitMinMs, MacroLimits.WaitMaxMs)],
            b => $"{b.GetInt("ms", 0)} ms"),
        new("randwait", BlockTypes.WaitRandom, "Random wait", "RND", "Timing", "Pause a random time between two values.",
            () => new() { ["minMs"] = 300, ["maxMs"] = 800 },
            [F.Int("minMs", "Min (ms)", MacroLimits.WaitMinMs, MacroLimits.WaitMaxMs), F.Int("maxMs", "Max (ms)", MacroLimits.WaitMinMs, MacroLimits.WaitMaxMs)],
            b => $"{b.GetInt("minMs", 0)}–{b.GetInt("maxMs", 0)} ms"),
        new("waitkey", BlockTypes.WaitForKey, "Wait for key", "WKY", "Timing", "Pause until you press a key.",
            () => new() { ["key"] = "F1" },
            [F.Key("key", "Key")],
            b => $"until {b.GetString("key", "?")}"),
        new("pausem", BlockTypes.Pause, "Pause macro", "PAUS", "Timing", "Pause another running macro, for example while this one handles a crash or reconnect.",
            () => new() { ["target"] = "*", ["when"] = "afterBlock", ["autoResume"] = true },
            [F.MacroName("target", "Macro", all: true), F.Pick("when", "Pause", PauseWhen, "afterBlock"),
             F.YesNo("autoResume", "Resume when this macro ends", fallback: false)],
            PauseSummary),
        new("resumem", BlockTypes.Resume, "Resume macro", "RSME", "Timing", "Unpause a macro so it carries on from where it stopped.",
            () => new() { ["target"] = "*", ["from"] = "paused" },
            [F.MacroName("target", "Macro", all: true), F.Pick("from", "Continue from", ResumeFrom, "paused")],
            b => $"{MacroLabel(b.GetString("target", ""))} · {Label(ResumeFrom, b.GetString("from", "paused")).ToLowerInvariant()}"),

        // ---- Detect (8) ----
        new("waittext", BlockTypes.WaitForText, "Wait for text", "OCR", "Detect", "Read the screen until the exact characters appear.",
            () => new() { ["text"] = "Quest complete", ["match"] = "exact", ["timeoutMs"] = 0, ["onFail"] = "continue" },
            [F.Text("text", "Text", MacroLimits.TypeTextMaxLength, mono: false), F.Pick("match", "Match", Matches, "exact"),
             F.YesNo("matchCase", "Case", fallback: false, yes: "Match case", no: "Ignore case"),
             F.Region(), F.Timeout(),
             F.OnFail(), F.FailLabel()],
            b => $"\"{b.GetString("text", "")}\" · {b.GetString("match", "exact")} · {F.MsOrInfinite(b, "timeoutMs", 10_000)}"),
        new("textgone", BlockTypes.WaitForTextGone, "Text gone", "!OCR", "Detect", "Wait until text is no longer on screen.",
            () => new() { ["text"] = "Loading", ["match"] = "contains", ["timeoutMs"] = 0, ["onFail"] = "continue" },
            [F.Text("text", "Text", MacroLimits.TypeTextMaxLength, mono: false), F.Pick("match", "Match", Matches, "exact"),
             F.YesNo("matchCase", "Case", fallback: false, yes: "Match case", no: "Ignore case"),
             F.Region(), F.Timeout(),
             F.OnFail(), F.FailLabel()],
            b => $"\"{b.GetString("text", "")}\" gone · {b.GetString("match", "exact")} · {F.MsOrInfinite(b, "timeoutMs", 10_000)}"),
        new("waitimage", BlockTypes.WaitForImage, "Wait for image", "IMG", "Detect", "Wait until a saved picture shows up on screen.",
            () => new() { ["confidencePct"] = 90, ["timeoutMs"] = 0, ["onFail"] = "continue" },
            [F.Image(), F.Int("confidencePct", "Confidence %", 1, 100), F.SizeTolerance(), F.MatchColor(), F.AverageColors(), F.Brightness(), F.Region(),
             F.Timeout(),
             F.OnFail(), F.FailLabel()],
            b => $"{ImageName(b)} · {b.GetInt("confidencePct", 90)}% · {F.MsOrInfinite(b, "timeoutMs", 10_000)}"),
        new("waitpixel", BlockTypes.DetectPixelColor, "Wait for pixel", "PIX", "Detect", "Wait until one point turns a color, like a health bar.",
            () => new() { ["x"] = 0, ["y"] = 0, ["color"] = "#FFFFFF", ["tolerance"] = 10, ["timeoutMs"] = 0, ["onFail"] = "continue", ["relativeTo"] = "gameWindow" },
            [F.Int("x", "X", -Coord, Coord), F.Int("y", "Y", -Coord, Coord), F.Color("color", "Color"),
             F.Int("tolerance", "Tolerance", 0, 255), F.Timeout(),
             F.OnFail(), F.FailLabel()],
            b => $"{b.GetInt("x", 0)}, {b.GetInt("y", 0)} = {b.GetString("color", "")} · {F.MsOrInfinite(b, "timeoutMs", 5000)}"),
        new("imagegone", BlockTypes.WaitForImageGone, "Image gone", "!IMG", "Detect", "Wait until a saved picture is no longer on screen.",
            () => new() { ["confidencePct"] = 90, ["timeoutMs"] = 0, ["onFail"] = "continue" },
            [F.Image(), F.Int("confidencePct", "Confidence %", 1, 100), F.SizeTolerance(), F.MatchColor(), F.AverageColors(), F.Brightness(), F.Region(),
             F.Timeout(),
             F.OnFail(), F.FailLabel()],
            b => $"{ImageName(b)} gone · {b.GetInt("confidencePct", 90)}% · {F.MsOrInfinite(b, "timeoutMs", 10_000)}"),
        new("pixelgone", BlockTypes.DetectPixelColorGone, "Pixel gone", "!PIX", "Detect", "Wait until one point is no longer a color, like a loading bar.",
            () => new() { ["x"] = 0, ["y"] = 0, ["color"] = "#FFFFFF", ["tolerance"] = 10, ["timeoutMs"] = 0, ["onFail"] = "continue", ["relativeTo"] = "gameWindow" },
            [F.Int("x", "X", -Coord, Coord), F.Int("y", "Y", -Coord, Coord), F.Color("color", "Color"),
             F.Int("tolerance", "Tolerance", 0, 255), F.Timeout(),
             F.OnFail(), F.FailLabel()],
            b => $"{b.GetInt("x", 0)}, {b.GetInt("y", 0)} ≠ {b.GetString("color", "")} · {F.MsOrInfinite(b, "timeoutMs", 5000)}"),
        new("findclick", BlockTypes.FindClick, "Find & click", "FND", "Detect", "Find text, an image or a color and click it.",
            () => new() { ["target"] = "text", ["value"] = "Claim", ["button"] = "left", ["timeoutMs"] = 5000, ["onFail"] = "continue" },
            [F.Pick("target", "Find", FindTargets, "text"), F.FindValue(), .. F.LookTuning(), F.Pick("button", "Button", Buttons, "left"),
             F.IntPick("clicks", "Clicks", ClickCounts, 1), F.WaitAfterClick(), F.Region(), F.AfterFound(), F.FoundLabel(), F.OnFail(), F.FailLabel()],
            b => $"{Label(FindTargets, b.GetString("target", "text"))} \"{b.GetString("value", "")}\" → {b.GetString("button", "left")} click"),
        new("scan", BlockTypes.ScanTurn, "Scan & turn", "SCN", "Detect", "Turn the camera step by step until a target is seen.",
            () => new() { ["target"] = "text", ["value"] = "Collect", ["method"] = "rightDrag", ["direction"] = "right", ["stepPx"] = 120, ["maxTurns"] = 12, ["whenFound"] = "click", ["button"] = "left", ["clicks"] = 1, ["onFail"] = "continue" },
            [F.Pick("target", "Look for", FindTargets, "text"), F.FindValue(), .. F.LookTuning(), F.Pick("method", "Turn with", ScanMethods, "rightDrag"), F.Pick("direction", "Turn", RightLeft, "right"),
             F.Int("stepPx", "Step (px)", 1, 10_000), F.Int("maxTurns", "Max turns", 1, MacroLimits.ScanTurnsMax),
             F.Pick("whenFound", "When found", WhenFound, "click"),
             F.Pick("button", "Button", Buttons, "left") with { ShowIf = b => b.GetString("whenFound", "click") == "click" },
             F.IntPick("clicks", "Clicks", ClickCounts, 1) with { ShowIf = b => b.GetString("whenFound", "click") == "click" },
             F.WaitAfterClick() with { ShowIf = b => b.GetString("whenFound", "click") == "click" },
             F.AfterFound(), F.FoundLabel(), F.OnFail(), F.FailLabel()],
            b => $"{Label(FindTargets, b.GetString("target", "text"))} \"{b.GetString("value", "")}\" · {b.GetString("direction", "right")} {b.GetInt("stepPx", 120)}px ×{b.GetInt("maxTurns", 12)}"),

        // ---- Logic (9) ----
        new("loop", BlockTypes.Loop, "Loop", "LOOP", "Logic", "Repeat the blocks inside a set number of times, or Infinite to never stop.",
            () => new() { ["count"] = 10 },
            [F.LoopCount()],
            b => b.GetBool("forever") == true ? F.Infinite : $"{b.GetInt("count", 1)} times"),
        new("until", BlockTypes.RepeatUntil, "Repeat until", "UNTL", "Logic", "Repeat the blocks inside until a condition is true.",
            () => new() { ["condition"] = new JsonObject { ["kind"] = "textVisible", ["text"] = "Done" }, ["maxIterations"] = 0 },
            [F.ConditionKind(), F.ConditionValue(), F.ConditionProgram(), F.ConditionRegion(), .. F.ConditionPictureTuning(),
             F.IntOrInfinite("maxIterations", "Max tries", MacroLimits.CountMax, "Infinite keeps going until the condition is true.")],
            b => Conditions.Describe(b.GetObject("condition")) + (b.GetInt("maxIterations") is > 0 and var max ? $" · max {max:N0}" : "")),
        new("ifb", BlockTypes.If, "If", "IF", "Logic", "Run the blocks inside only when a condition is true.",
            () => new() { ["condition"] = new JsonObject { ["kind"] = "textVisible", ["text"] = "Done" } },
            [F.ConditionKind(), F.ConditionValue(), F.ConditionProgram(), F.ConditionRegion(), .. F.ConditionPictureTuning()],
            b => Conditions.Describe(b.GetObject("condition"))),
        new("elseb", BlockTypes.Else, "Else", "ELSE", "Logic", "Runs when the If above was false.", () => [], [], _ => ""),
        new("end", BlockTypes.End, "End", "END", "Logic", "Closes the nearest Loop, Repeat until or If.", () => [], [], _ => ""),
        new("label", BlockTypes.Label, "Label", "LBL", "Logic", "A named spot that Go to label and On fail can jump to.",
            () => new() { ["name"] = "start" }, [F.Text("name", "Name", 40)], b => b.GetString("name", "")),
        new("gotob", BlockTypes.Goto, "Go to label", "GOTO", "Logic", "Jump to a label.",
            () => new() { ["name"] = "start" }, [F.LabelName("name", "Label")], b => $"→ {b.GetString("name", "")}"),
        new("call", BlockTypes.RunMacro, "Run macro", "CALL", "Logic", "Run another saved macro, then come back.",
            () => new() { ["macroName"] = "" }, [F.MacroName("macroName", "Macro", all: false)], b => b.GetString("macroName", "")),
        new("stop", BlockTypes.Stop, "Stop macro", "STOP", "Logic", "End the run here.", () => [], [], _ => ""),

        // ---- Window (6) ----
        new("open", BlockTypes.WindowOpen, "Open window", "OPEN", "Window", "Start a program and wait for its window. If it's already running, bring it to the front.",
            () => new() { ["path"] = "", ["ifRunning"] = "focus", ["wait"] = true, ["timeoutMs"] = 30_000 },
            [F.Program("path", "Program", fullPath: true),
             F.Text("args", "Arguments (optional)", MacroLimits.PathMaxLength, optional: true, placeholder: "e.g. -windowed"),
             F.Pick("ifRunning", "If it's already running", IfRunning, "focus"),
             F.YesNo("wait", "Wait for its window", fallback: true, yes: "Wait, then bring it to the front", no: "Don't wait"),
             F.Timeout() with { ShowIf = b => b.GetBool("wait") != false },
             F.Program("process", "Window to wait for (optional)", placeholder: "The program above",
                 hint: "Only for launchers that open a different program, like a game launcher.") with { ShowIf = b => b.GetBool("wait") != false }],
            b => b.GetString("path") is { Length: > 0 } path ? WindowFileName(path) + (b.GetString("args") is { Length: > 0 } a ? $" {a}" : "") : "pick a program"),
        new("close", BlockTypes.WindowClose, "Close window", "CLOSE", "Window", "Close a program, like clicking its X. It can also force it closed.",
            () => new() { ["how"] = "close", ["timeoutMs"] = 10_000 },
            [F.Process(), F.Pick("how", "How", CloseHow, "close"),
             F.Int("timeoutMs", "Wait for it to close (ms)", 0, MacroLimits.WaitMaxMs) with { ShowIf = b => b.GetString("how") != "end" }],
            b => $"{ProcessName(b)} · {Label(CloseHow, b.GetString("how", "close"))}"),
        new("focus", BlockTypes.WindowFocus, "Focus window", "WIN", "Window", "Bring a game or app to the front.",
            () => [], [F.Process()], b => ProcessName(b)),
        new("waitwin", BlockTypes.WaitForWindow, "Wait for window", "WWN", "Window", "Pause until a window opens, or opens and comes to the front.",
            () => new() { ["timeoutMs"] = 30_000 },
            [F.Process(), F.Pick("until", "Wait until it's", WaitUntil, "front"),
             F.Timeout()],
            b => $"{ProcessName(b)}{(b.GetString("until") == "open" ? " open" : " in front")} · {F.MsOrInfinite(b, "timeoutMs", 30_000)}"),
        new("resize", BlockTypes.WindowResize, "Resize window", "SIZE", "Window", "Set the game window to an exact size so clicks land in the same place every run.",
            () => new() { ["width"] = 1280, ["height"] = 720 },
            [F.Process(), F.Int("width", "Width (px)", 1, 16_384), F.Int("height", "Height (px)", 1, 16_384)],
            b => $"{b.GetInt("width", 0)}×{b.GetInt("height", 0)}"),
        new("position", BlockTypes.WindowPosition, "Position window", "POS", "Window", "Move the game window to the same spot on screen so clicks land in the right place.",
            () => new() { ["monitor"] = 1, ["anchor"] = "topLeft", ["x"] = 0, ["y"] = 0 },
            [F.Process(), F.IntPick("monitor", "Monitor", Monitors, 1), F.Pick("anchor", "Snap to", Anchors, "custom"),
             F.Int("x", "X (Custom only)", -Coord, Coord), F.Int("y", "Y (Custom only)", -Coord, Coord)],
            PositionSummary),

        // ---- Utility (4) ----
        new("notify", BlockTypes.Notify, "Notify", "NTF", "Utility", "Send a Windows notification or play a sound.",
            () => new() { ["kind"] = "toast", ["message"] = "Done" },
            [F.Pick("kind", "Type", NotifyKinds, "toast"), F.Text("message", "Message", 200, optional: true, mono: false)],
            b => $"{Label(NotifyKinds, b.GetString("kind", "toast"))} · \"{b.GetString("message", "")}\""),
        new("shot", BlockTypes.Screenshot, "Screenshot", "SHOT", "Utility", "Save a picture of the screen or a region.",
            () => new() { ["file"] = "night-loop.png" },
            [F.Region(), F.FileName()],
            b => b.GetString("file", "night-loop.png")),
        new("comment", BlockTypes.Comment, "Comment", "//", "Utility", "A note that does nothing when run.",
            () => new() { ["text"] = "Section" },
            [F.Text("text", "Note", MacroLimits.CommentMaxLength, optional: true, mono: false)],
            b => b.GetString("text", "")),
        new("desc", BlockTypes.Description, "Description", "DESC", "Utility", "Longer notes that grow to fit. Does nothing when run.",
            () => new() { ["text"] = "" },
            [F.Text("text", "Text", MacroLimits.DescriptionMaxLength, optional: true, multiLine: true)],
            _ => ""),

        // ---- Older blocks: editable, not in the palette ----
        new("sound", BlockTypes.PlaySound, "Play sound", "SND", "Utility", "Play a Windows sound.",
            () => new() { ["sound"] = "default" }, [F.Pick("sound", "Sound", Sounds, "default")],
            b => b.GetString("sound", "default"), Hidden: true),
        new("camtext", BlockTypes.CameraTurnUntilText, "Turn until text", "SCN", "Camera", "Replaced by Scan & turn. Swap it for a Scan & turn block.",
            () => [], [], b => $"\"{b.GetString("text", "")}\"", Hidden: true),
        new("camimage", BlockTypes.CameraTurnUntilImage, "Turn until image", "SCN", "Camera", "Replaced by Scan & turn. Swap it for a Scan & turn block.",
            () => [], [], _ => "", Hidden: true),
    ];

    private static readonly Dictionary<string, BlockDef> ByType = All
        .GroupBy(d => d.Type).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>The blocks shown in the palette (everything but older, hidden types).</summary>
    public static IEnumerable<BlockDef> Palette => All.Where(d => !d.Hidden);

    /// <summary>The definition used to edit and describe a block.</summary>
    public static BlockDef? For(string type) => ByType.GetValueOrDefault(type);

    public static string ColorOf(string category) =>
        Categories.FirstOrDefault(c => string.Equals(c.Name, category, StringComparison.OrdinalIgnoreCase)).Color ?? "#C9D1D9";

    /// <summary>"*" reads as "All other macros" (Pause and Resume).</summary>
    public static string MacroLabel(string name) => name == "*" ? "All other macros" : name.Length == 0 ? "?" : name;

    private static string Label(IEnumerable<Choice> choices, string value) =>
        choices.FirstOrDefault(c => c.Value == value)?.Label ?? value;

    private static string ImageName(Block b) =>
        b.GetString("image") is { Length: > 0 } name ? name : b.GetString("imageData") is { Length: > 0 } ? "picture" : "no picture yet";

    private static string ProcessName(Block b) => b.GetString("process") is { Length: > 0 } p ? p : "target game";

    private static bool IsPress(Block b) => b.GetString("action", "press") == "press";

    /// <summary>"C:\\Games\\Game.exe" → "Game.exe".</summary>
    public static string WindowFileName(string path)
    {
        var t = path.Trim().Trim('"').TrimEnd('\\', '/');
        var cut = t.LastIndexOfAny(['\\', '/']);
        return cut >= 0 ? t[(cut + 1)..] : t;
    }

    private static string KeySummary(Block b)
    {
        var key = b.GetString("key", "?");
        return b.GetString("action", "press") switch
        {
            "down" => $"Hold {key} down",
            "up" => $"Let go of {key}",
            _ => b.GetInt("holdMs") is { } h ? $"Press {key} · {h} ms" : $"Press {key}",
        };
    }

    private static string ClickSummary(Block b)
    {
        var button = Label(Buttons, b.GetString("button", "left"));
        var where = b.GetInt("x") is { } x && b.GetInt("y") is { } y ? $"at {x}, {y}" : "at the mouse";
        var text = b.GetString("action", "press") switch
        {
            "down" => $"Hold {button.ToLowerInvariant()} button {where}",
            "up" => $"Let go of {button.ToLowerInvariant()} button {where}",
            _ => $"{button} ×{b.GetInt("clicks", 1)} {where}{(b.GetInt("holdMs") is { } h and > 20 ? $" · {h} ms" : "")}",
        };
        // Only a wait set on the block itself; the Settings one isn't part of the macro.
        return b.GetInt(BlockTypes.WaitAfterKey) is { } wait ? $"{text} · then wait {wait} ms" : text;
    }

    private static string PauseSummary(Block b)
    {
        if (b.GetString("mode") == "resume") return $"{MacroLabel(b.GetString("target", ""))} · where it paused";
        var when = Label(PauseWhen, b.GetString("when", "afterBlock")).ToLowerInvariant();
        return $"{MacroLabel(b.GetString("target", ""))} · {when}{(b.GetBool("autoResume") == true ? " · auto-resume" : "")}";
    }

    private static string PositionSummary(Block b)
    {
        var anchor = b.GetString("anchor", "custom");
        var monitor = b.GetInt("monitor");
        var where = anchor == "custom" ? $"{b.GetInt("x", 0)}, {b.GetInt("y", 0)}" : Label(Anchors, anchor).ToLowerInvariant();
        return monitor is null ? where : $"{where} · {Label(Monitors, monitor.Value.ToString(CultureInfo.InvariantCulture)).ToLowerInvariant()}";
    }
}

/// <summary>Builders for the inspector fields.</summary>
internal static class F
{
    private static readonly Choice[] FoundActions = [new("next", "Go on to next block"), new("exitLoop", "Leave the loop"), new("goto", "Go to label"), new("stop", "Stop macro")];
    private static readonly Choice[] FailActions = [new("continue", "Continue"), new("retry", "Retry"), new("goto", "Go to label"), new("stop", "Stop macro")];
    private static readonly char[] BadFileChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    public static FieldDef Int(string path, string label, int min, int max, bool optional = false, string? placeholder = null, string? hint = null) =>
        new(path, label, FieldKind.Text,
            b => P.Text(P.Get(b, path)),
            (b, text) =>
            {
                var t = text.Trim().Replace(",", "", StringComparison.Ordinal);
                if (t.Length == 0)
                {
                    if (!optional) return "Enter a number.";
                    P.Remove(b, path);
                    return null;
                }
                if (!int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return "Enter a whole number.";
                if (n < min || n > max) return $"Use {min:N0} to {max:N0}.";
                P.Set(b, path, n);
                return null;
            },
            Placeholder: placeholder, Hint: hint, MaxLength: 12);

    /// <summary>
    /// Clicking blocks: how long to wait after the click. Blank (the default) follows Settings › Macros › Wait after
    /// clicks, so changing that slider changes every block that wasn't set by hand.
    /// </summary>
    public static FieldDef WaitAfterClick() =>
        new(BlockTypes.WaitAfterKey, "Wait after click (ms)", FieldKind.Slider,
            b => P.Text(P.Get(b, BlockTypes.WaitAfterKey)),
            (b, text) =>
            {
                var t = text.Trim();
                if (t.Length == 0)
                {
                    P.Remove(b, BlockTypes.WaitAfterKey);
                    return null;
                }
                if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > MacroLimits.WaitAfterMaxMs)
                    return $"Use 0 to {MacroLimits.WaitAfterMaxMs:N0}.";
                P.Set(b, BlockTypes.WaitAfterKey, n);
                return null;
            },
            Hint: "Until you move it, this follows Settings › Macros › Wait after clicks.", SliderMax: MacroLimits.WaitAfterMaxMs);

    public static FieldDef Text(string path, string label, int max, bool optional = false, bool multiLine = false, bool mono = true, string? placeholder = null) =>
        new(path, label, multiLine ? FieldKind.MultiLine : FieldKind.Text,
            b => P.Text(P.Get(b, path)),
            (b, text) =>
            {
                var t = multiLine ? text : text.Trim();
                if (t.Trim().Length == 0)
                {
                    if (!optional) return $"{label} can't be empty.";
                    P.Remove(b, path);
                    return null;
                }
                if (t.Length > max) return $"Use at most {max:N0} characters.";
                P.Set(b, path, t);
                return null;
            },
            Placeholder: placeholder, MaxLength: max, Mono: mono && !multiLine);

    public static FieldDef Key(string path, string label) =>
        new(path, label, FieldKind.Text,
            b => P.Text(P.Get(b, path)),
            (b, text) =>
            {
                var t = text.Trim();
                if (!KeyCatalog.TryGetVirtualKey(t, out var vk)) return "Not a key Night-loop knows. Try A, Space, F1, Shift or Enter.";
                P.Set(b, path, KeyCatalog.NameOf(vk) ?? t);
                return null;
            },
            MaxLength: 20);

    public static FieldDef Combo(string path, string label) =>
        new(path, label, FieldKind.Text,
            b => P.Text(P.Get(b, path)),
            (b, text) =>
            {
                if (!MacroValidator.IsKeyCombo(text)) return "Use keys joined by +, like Ctrl + Shift + E.";
                P.Set(b, path, string.Join(" + ", text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)));
                return null;
            },
            MaxLength: 60);

    public static FieldDef Pick(string path, string label, IReadOnlyList<Choice> choices, string fallback) =>
        new(path, label, FieldKind.Choice,
            b => P.Text(P.Get(b, path)) is { Length: > 0 } v ? v : fallback,
            (b, value) =>
            {
                P.Set(b, path, value);
                return null;
            },
            choices);

    public static FieldDef IntPick(string path, string label, IReadOnlyList<Choice> choices, int fallback) =>
        new(path, label, FieldKind.Choice,
            b => (BlockParams.AsInt(P.Get(b, path)) ?? fallback).ToString(CultureInfo.InvariantCulture),
            (b, value) =>
            {
                P.Set(b, path, int.Parse(value, CultureInfo.InvariantCulture));
                return null;
            },
            choices);

    public static FieldDef YesNo(string path, string label, bool fallback, string yes = "Yes", string no = "No") =>
        new(path, label, FieldKind.Choice,
            b => (b.GetBool(path) ?? fallback) ? "true" : "false",
            (b, value) =>
            {
                P.Set(b, path, value == "true");
                return null;
            },
            [new("true", yes), new("false", no)]);

    public static FieldDef Color(string path, string label) =>
        new("color", label, FieldKind.Text,
            b => P.Text(P.Get(b, path)),
            (b, text) =>
            {
                var t = text.Trim();
                if (!t.StartsWith('#')) t = "#" + t;
                if (!MacroValidator.IsColor(t)) return "Use a color like #FF0000, or Pick color from screen.";
                P.Set(b, path, t.ToUpperInvariant());
                return null;
            },
            Placeholder: "#RRGGBB", MaxLength: 7);

    /// <summary>"Full window" (or blank) for the whole game window, else x, y, width, height.</summary>
    public static FieldDef Region() =>
        new("region", "Region", FieldKind.Text,
            b => RegionText(b.Params["region"]),
            (b, text) =>
            {
                var t = text.Trim();
                if (t.Length == 0 || t.Equals("Full window", StringComparison.OrdinalIgnoreCase) || t.Equals("Full screen", StringComparison.OrdinalIgnoreCase))
                {
                    b.Params.Remove("region");
                    return null;
                }
                var parts = t.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length != 4 || !parts.All(p => int.TryParse(p, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)))
                    return "Use x, y, width, height (or Full window), or Select region on screen.";
                var n = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                if (n[2] < 1 || n[3] < 1) return "Width and height must be at least 1.";
                b.Params["region"] = new JsonArray(n[0], n[1], n[2], n[3]);
                return null;
            },
            Placeholder: "Full window", MaxLength: 40);

    public static string RegionText(JsonNode? region) =>
        MacroValidator.ReadRegion(region) is { } r ? $"{r.X}, {r.Y}, {r.Width}, {r.Height}" : "Full window";

    /// <summary>The program a Window block works on. Clicking the box opens File Explorer; blank means the target game.</summary>
    public static FieldDef Process() => Program("process", "Program", placeholder: "The target game");

    /// <summary>
    /// A program, picked in File Explorer (or from the running apps). Window blocks keep just the file name, like
    /// Game.exe; Open window keeps the whole path (<paramref name="fullPath"/>).
    /// </summary>
    public static FieldDef Program(string path, string label, bool fullPath = false, string? placeholder = null, string? hint = null) =>
        new(path, label, FieldKind.Program,
            b => b.GetString(path) ?? "",
            (b, text) =>
            {
                var t = text.Trim().Trim('"');
                if (t.Length == 0)
                {
                    if (fullPath) return "Pick the program to open.";
                    b.Params.Remove(path);
                    return null;
                }
                if (t.Length > MacroLimits.PathMaxLength) return $"Use at most {MacroLimits.PathMaxLength} characters.";
                b.Params[path] = fullPath ? t : BlockCatalog.WindowFileName(t);
                return null;
            },
            Placeholder: placeholder ?? "Click to pick a program", Hint: hint, MaxLength: MacroLimits.PathMaxLength, FullPath: fullPath);

    /// <summary>The picture's name; the picture itself comes from "Capture image from screen".</summary>
    public static FieldDef Image() =>
        new("image", "Image", FieldKind.Text,
            b => b.GetString("image") ?? "",
            (b, text) =>
            {
                var t = text.Trim();
                if (t.Length > 120) return "Use at most 120 characters.";
                if (t.Length == 0) b.Params.Remove("image");
                else b.Params["image"] = t;
                return null;
            },
            Placeholder: "Capture image from screen",
            Hint: null, MaxLength: 120);

    private const string SizeToleranceHint =
        "Leave blank (Auto) to find the picture at any size, from 30% to 160% of how you captured it, like a thing far " +
        "away in a game. A number limits it: 10 finds it from 90% to 110%, 0 only at the exact size.";

    /// <summary>Picture blocks: how much smaller or bigger the picture may look on screen.</summary>
    public static FieldDef SizeTolerance() =>
        Int("sizeTolerancePct", "Size tolerance %", 0, MacroLimits.SizeToleranceMaxPct, optional: true, placeholder: "Auto", hint: SizeToleranceHint)
            with { Get = b => AutoSize(b.GetInt("sizeTolerancePct")) };

    /// <summary>25 was 0.6.0's default and now means Auto, so it shows as blank.</summary>
    private static string AutoSize(int? stored) => stored is null or 25 ? "" : stored.Value.ToString(CultureInfo.InvariantCulture);

    private const string MatchColorHint =
        "On: the colors have to match too, so a same-shaped thing in another color is never clicked. Off: only the shape counts.";

    /// <summary>Picture blocks: the colors have to match as well as the shape. On unless turned off.</summary>
    public static FieldDef MatchColor() =>
        YesNo("matchColor", "Match colors", fallback: true, yes: "On", no: "Off") with { Hint = MatchColorHint };

    private const string AverageColorsHint =
        "On: the colors are averaged to the picture's brightness first, so any dimmer or brighter copy matches " +
        "(games whose lighting changes). Off: Brightness range % says how much dimmer or brighter it may be.";

    /// <summary>Picture blocks with Match colors on: average the colors instead of using the brightness range. Off unless turned on.</summary>
    public static FieldDef AverageColors() =>
        YesNo(BlockTypes.AverageColorsKey, "Dimmer or brighter", fallback: false, yes: "Average colors", no: "Use range")
            with { Hint = AverageColorsHint, ShowIf = b => b.GetBool("matchColor") != false };

    private const string BrightnessHint =
        "How much dimmer or brighter than the captured picture it may look on screen, for games whose lighting changes. " +
        "Blank is 3. 0 is as captured (screen noise is always allowed); 30 lets a copy 30% dimmer or brighter match.";

    /// <summary>Picture blocks with Match colors on: the brightness range in percents (blank = <see cref="Brightness.DefaultPct"/>).</summary>
    public static FieldDef Brightness() =>
        new FieldDef(BlockTypes.BrightnessKey, "Brightness range %", FieldKind.Text,
            b => NightLoop.Contracts.MacroDocument.Brightness.Shown(b.Params),
            (b, text) => SetBrightness(b.Params, text),
            Placeholder: NightLoop.Contracts.MacroDocument.Brightness.DefaultPct.ToString(CultureInfo.InvariantCulture), Hint: BrightnessHint, MaxLength: 12)
            with { ShowIf = b => b.GetBool("matchColor") != false && b.GetBool(BlockTypes.AverageColorsKey) != true };

    private static string? SetBrightness(JsonObject p, string text)
    {
        var t = text.Trim().TrimEnd('%').Trim();
        if (t.Length == 0)
        {
            NightLoop.Contracts.MacroDocument.Brightness.Write(p, null);
            return null;
        }
        if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return "Enter a whole number.";
        if (n is < NightLoop.Contracts.MacroDocument.Brightness.MinPct or > NightLoop.Contracts.MacroDocument.Brightness.MaxPct)
            return $"Use {NightLoop.Contracts.MacroDocument.Brightness.MinPct} to {NightLoop.Contracts.MacroDocument.Brightness.MaxPct}.";
        NightLoop.Contracts.MacroDocument.Brightness.Write(p, n);
        return null;
    }

    private const string ClickSpotHint =
        "Where to click once the picture is found: 0 is its left (or top) edge, 100 its right (or bottom), blank the " +
        "middle. Below 0 or above 100 is a spot beside the picture. Easiest: \"Set click spot in picture\" or \"Set click spot on screen\".";

    /// <summary>Find &amp; click / Scan &amp; turn: how close a picture or color has to be, shown for the kind being looked for.</summary>
    public static FieldDef[] LookTuning()
    {
        static bool Is(Block b, string kind) => b.GetString("target", "text") == kind;
        static bool Clicks(Block b) => b.Type != BlockTypes.ScanTurn || b.GetString("whenFound", "click") != "continue";
        return
        [
            Int("confidencePct", "Confidence %", 1, 100, optional: true, placeholder: "90",
                hint: "How alike the spot has to be. Lower finds it more easily but may click the wrong thing.") with { ShowIf = b => Is(b, "image") },
            SizeTolerance() with { ShowIf = b => Is(b, "image") },
            MatchColor() with { ShowIf = b => Is(b, "image") },
            AverageColors() with { ShowIf = b => Is(b, "image") && b.GetBool("matchColor") != false },
            Brightness() with { ShowIf = b => Is(b, "image") && b.GetBool("matchColor") != false && b.GetBool(BlockTypes.AverageColorsKey) != true },
            Int(BlockTypes.ClickXKey, "Click spot across %", ClickSpot.MinPct, ClickSpot.MaxPct, optional: true, placeholder: "50",
                hint: ClickSpotHint) with { ShowIf = b => Is(b, "image") && Clicks(b) },
            Int(BlockTypes.ClickYKey, "Click spot down %", ClickSpot.MinPct, ClickSpot.MaxPct, optional: true, placeholder: "50") with { ShowIf = b => Is(b, "image") && Clicks(b) },
            Int("tolerance", "Color tolerance", 0, 255, optional: true, placeholder: "10",
                hint: "How far each color channel may be from the one picked.") with { ShowIf = b => Is(b, "pixel") },
        ];
    }

    /// <summary>If / Repeat until with "Image visible": confidence and size tolerance, kept inside the condition.</summary>
    public static FieldDef[] ConditionPictureTuning()
    {
        static bool IsImage(Block b) => Conditions.Read(b.GetObject("condition")).Kind.TrimStart('!') == "imageVisible";
        static bool ColorsOn(Block b) => !(Conditions.Inner(b.GetObject("condition"))?["matchColor"] is JsonValue m && m.TryGetValue(out bool mc) && !mc);
        static bool Averages(Block b) => Conditions.Inner(b.GetObject("condition"))?[BlockTypes.AverageColorsKey] is JsonValue v && v.TryGetValue(out bool on) && on;
        return
        [
            ConditionInt("confidencePct", "Confidence %", 1, 100, "90", null) with { ShowIf = IsImage },
            new FieldDef("condition.matchColor", "Match colors", FieldKind.Choice,
                b => (Conditions.Inner(b.GetObject("condition"))?["matchColor"] is JsonValue v && v.TryGetValue(out bool on) && !on) ? "false" : "true",
                (b, value) =>
                {
                    var condition = b.GetObject("condition")?.DeepClone() as JsonObject ?? [];
                    if (Conditions.Inner(condition) is not { } inner) return null;
                    inner["matchColor"] = value == "true";
                    b.Params["condition"] = condition;
                    return null;
                },
                [new("true", "On"), new("false", "Off")], Hint: MatchColorHint) with { ShowIf = IsImage },
            new FieldDef("condition." + BlockTypes.AverageColorsKey, "Dimmer or brighter", FieldKind.Choice,
                b => Averages(b) ? "true" : "false",
                (b, value) =>
                {
                    var condition = b.GetObject("condition")?.DeepClone() as JsonObject ?? [];
                    if (Conditions.Inner(condition) is not { } inner) return null;
                    if (value == "true") inner[BlockTypes.AverageColorsKey] = true;
                    else inner.Remove(BlockTypes.AverageColorsKey);
                    b.Params["condition"] = condition;
                    return null;
                },
                [new("true", "Average colors"), new("false", "Use range")], Hint: AverageColorsHint)
                with { ShowIf = b => IsImage(b) && ColorsOn(b) },
            new FieldDef("condition." + BlockTypes.BrightnessKey, "Brightness range %", FieldKind.Text,
                b => NightLoop.Contracts.MacroDocument.Brightness.Shown(Conditions.Inner(b.GetObject("condition"))),
                (b, text) =>
                {
                    var condition = b.GetObject("condition")?.DeepClone() as JsonObject ?? [];
                    if (Conditions.Inner(condition) is not { } inner) return null;
                    if (SetBrightness(inner, text) is { } error) return error;
                    b.Params["condition"] = condition;
                    return null;
                },
                Placeholder: NightLoop.Contracts.MacroDocument.Brightness.DefaultPct.ToString(CultureInfo.InvariantCulture), Hint: BrightnessHint, MaxLength: 12)
                with { ShowIf = b => IsImage(b) && ColorsOn(b) && !Averages(b) },
            ConditionInt("sizeTolerancePct", "Size tolerance %", 0, MacroLimits.SizeToleranceMaxPct, "Auto", SizeToleranceHint)
                with { ShowIf = IsImage, Get = b => AutoSize(BlockParams.AsInt(Conditions.Inner(b.GetObject("condition"))?["sizeTolerancePct"])) },
        ];
    }

    private static FieldDef ConditionInt(string key, string label, int min, int max, string placeholder, string? hint) =>
        new("condition." + key, label, FieldKind.Text,
            b => P.Text(Conditions.Inner(b.GetObject("condition"))?[key]),
            (b, text) =>
            {
                var condition = b.GetObject("condition")?.DeepClone() as JsonObject ?? [];
                if (Conditions.Inner(condition) is not { } inner) return null;
                var t = text.Trim();
                if (t.Length == 0) inner.Remove(key);
                else if (!int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return "Enter a whole number.";
                else if (n < min || n > max) return $"Use {min:N0} to {max:N0}.";
                else inner[key] = n;
                b.Params["condition"] = condition;
                return null;
            },
            Placeholder: placeholder, Hint: hint, MaxLength: 12);

    /// <summary>Find &amp; click / Scan &amp; turn: the text, the picture's name or the color to look for.</summary>
    public static FieldDef FindValue() =>
        new("value", "Value", FieldKind.Text,
            b => b.GetString("value") ?? "",
            (b, text) =>
            {
                var t = text.Trim();
                var target = b.GetString("target", "text");
                if (target == "pixel")
                {
                    if (!t.StartsWith('#')) t = "#" + t;
                    if (!MacroValidator.IsColor(t)) return "Use a color like #FF0000, or Pick color from screen.";
                    b.Params["value"] = t.ToUpperInvariant();
                    return null;
                }
                if (t.Length == 0) return target == "image" ? "Name the picture, then Capture image from screen." : "Say what text to look for.";
                if (t.Length > MacroLimits.TypeTextMaxLength) return $"Use at most {MacroLimits.TypeTextMaxLength} characters.";
                b.Params["value"] = t;
                return null;
            },
            MaxLength: MacroLimits.TypeTextMaxLength);

    public static FieldDef OnFail() => Pick("onFail", "On fail", FailActions, "continue");

    public static FieldDef AfterFound() => Pick("afterFound", "After found", FoundActions, "next");

    public static FieldDef FoundLabel() =>
        LabelName("foundLabel", "Label to go to") with { ShowIf = b => b.GetString("afterFound") == "goto" };

    public static FieldDef FailLabel() =>
        LabelName("failLabel", "Label to go to") with { ShowIf = b => b.GetString("onFail") == "goto" };

    /// <summary>One of this macro's labels, picked from a list.</summary>
    public static FieldDef LabelName(string path, string label) =>
        new(path, label, FieldKind.LabelName,
            b => b.GetString(path) ?? "",
            (b, value) =>
            {
                if (value.Length == 0) return "Pick a label.";
                b.Params[path] = value;
                return null;
            },
            Hint: "The labels in this macro. Add a Label block to jump somewhere new.");

    public static FieldDef MacroName(string path, string label, bool all) =>
        new(path, label, FieldKind.MacroName,
            b => b.GetString(path) ?? "",
            (b, value) =>
            {
                if (value.Length == 0) return "Pick a macro.";
                b.Params[path] = value;
                return null;
            },
            AllMacros: all);

    public static FieldDef FileName() =>
        new("file", "File name", FieldKind.Text,
            b => b.GetString("file") ?? "",
            (b, text) =>
            {
                var t = text.Trim();
                if (t.Length == 0)
                {
                    b.Params.Remove("file");
                    return null;
                }
                if (t.Length > MacroLimits.FileNameMaxLength) return $"Use at most {MacroLimits.FileNameMaxLength} characters.";
                if (t.IndexOfAny(BadFileChars) >= 0) return "File names can't use / \\ : * ? \" < > |";
                b.Params["file"] = t;
                return null;
            },
            Placeholder: "night-loop.png", Hint: "Saved in Pictures\\Night-loop", MaxLength: MacroLimits.FileNameMaxLength);

    /// <summary>What every "no limit" setting shows and accepts: loops, max tries and timeouts.</summary>
    public const string Infinite = "Infinite";

    /// <summary>Infinite, plus the older words for it (Forever, ∞), in any case.</summary>
    public static bool IsInfinite(string text) =>
        text.Trim() is var t && (t.Equals(Infinite, StringComparison.OrdinalIgnoreCase) || t.Equals("inf", StringComparison.OrdinalIgnoreCase)
                                 || t.Equals("forever", StringComparison.OrdinalIgnoreCase) || t == "∞");

    /// <summary>A number where 0 means no limit. It shows as Infinite and takes Infinite (or 0) to mean it.</summary>
    public static FieldDef IntOrInfinite(string path, string label, int max, string? hint = null) =>
        new(path, label, FieldKind.Text,
            b => P.Get(b, path) is JsonValue v && BlockParams.AsInt(v) == 0 ? Infinite : P.Text(P.Get(b, path)),
            (b, text) =>
            {
                var t = text.Trim().Replace(",", "", StringComparison.Ordinal);
                if (IsInfinite(t)) t = "0";
                if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > max)
                    return $"Use 1 to {max:N0}, or {Infinite}.";
                P.Set(b, path, n);
                return null;
            },
            Hint: hint, MaxLength: 12);

    /// <summary>Wait blocks' timeout: Infinite waits for as long as it takes.</summary>
    public static FieldDef Timeout() =>
        IntOrInfinite("timeoutMs", "Timeout (ms)", MacroLimits.WaitMaxMs, $"{Infinite} waits for as long as it takes.");

    /// <summary>"5000 ms", or Infinite for 0.</summary>
    public static string MsOrInfinite(Block b, string key, int fallback) =>
        b.GetInt(key, fallback) is var ms && ms == 0 ? Infinite : $"{ms} ms";

    /// <summary>Loop: a number, or Infinite (stored as forever: true, like before).</summary>
    public static FieldDef LoopCount() =>
        new("count", "Times (number or Infinite)", FieldKind.Text,
            b => b.GetBool("forever") == true ? Infinite : (b.GetInt("count") ?? 1).ToString(CultureInfo.InvariantCulture),
            (b, text) =>
            {
                var t = text.Trim().Replace(",", "", StringComparison.Ordinal);
                if (IsInfinite(t) || t == "0")
                {
                    b.Params.Remove("count");
                    b.Params["forever"] = true;
                    return null;
                }
                if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < MacroLimits.CountMin || n > MacroLimits.CountMax)
                    return $"Use {MacroLimits.CountMin:N0} to {MacroLimits.CountMax:N0}, or {Infinite}.";
                b.Params.Remove("forever");
                b.Params["count"] = n;
                return null;
            },
            Hint: $"{Infinite} repeats until the macro stops.", MaxLength: 12);

    public static FieldDef ScrollDirection() =>
        new("direction", "Direction", FieldKind.Choice,
            ScrollDir,
            (b, value) =>
            {
                b.Params["amount"] = Math.Max(1, Math.Abs(b.GetInt("amount", 3)));
                b.Params["direction"] = value;
                return null;
            },
            [new("up", "Up"), new("down", "Down")]);

    public static FieldDef ScrollAmount() =>
        new("amount", "Ticks", FieldKind.Text,
            b => Math.Abs(b.GetInt("amount", 0)).ToString(CultureInfo.InvariantCulture),
            (b, text) =>
            {
                if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > MacroLimits.ScrollMax)
                    return $"Use 1 to {MacroLimits.ScrollMax}.";
                b.Params["direction"] = ScrollDir(b);
                b.Params["amount"] = n;
                return null;
            },
            Hint: "One tick is one click of the scroll wheel.", MaxLength: 3);

    public static string Ticks(int n) => n == 1 ? "1 tick" : $"{n} ticks";

    /// <summary>Older scroll blocks have a signed amount and no direction.</summary>
    public static string ScrollDir(Block b) => b.GetString("direction") ?? (b.GetInt("amount", 0) < 0 ? "down" : "up");

    public static FieldDef ConditionKind() =>
        new("condition.kind", "Condition", FieldKind.Choice,
            b => Conditions.Read(b.GetObject("condition")).Kind,
            (b, kind) =>
            {
                var (old, value) = Conditions.Read(b.GetObject("condition"));
                if (old == kind) return null;
                // Switching kinds keeps the value only when it still makes sense.
                var keep = Conditions.ValueKind(old) == Conditions.ValueKind(kind) ? value : Conditions.DefaultValue(kind);
                b.Params["condition"] = Conditions.Write(kind, keep, null);
                return null;
            },
            Conditions.Kinds);

    /// <summary>Window focused / Program running: the program, picked with File Explorer.</summary>
    public static FieldDef ConditionProgram() =>
        new FieldDef("condition.name", "Program", FieldKind.Program,
            b => Conditions.Read(b.GetObject("condition")).Value,
            (b, text) =>
            {
                var condition = b.GetObject("condition");
                var (kind, _) = Conditions.Read(condition);
                var name = text.Trim().Length == 0 ? "" : BlockCatalog.WindowFileName(text);
                if (Conditions.Check(kind, name) is { } error) return error;
                b.Params["condition"] = Conditions.Write(kind, name, condition);
                return null;
            },
            Placeholder: "Click to pick a program", MaxLength: MacroLimits.PathMaxLength)
        with { ShowIf = b => Conditions.IsProgramKind(Conditions.Read(b.GetObject("condition")).Kind) };

    /// <summary>Text / image visible: where to look (blank is the whole game window).</summary>
    public static FieldDef ConditionRegion() =>
        new FieldDef("condition.region", "Region", FieldKind.Text,
            b => RegionText(Conditions.Inner(b.GetObject("condition"))?["region"]),
            (b, text) =>
            {
                var probe = Block.Create("x", BlockTypes.Wait);
                if (Region().Set(probe, text) is { } error) return error;
                var condition = b.GetObject("condition")?.DeepClone() as JsonObject ?? [];
                if (Conditions.Inner(condition) is not { } inner) return null;
                if (probe.Params["region"] is { } r) inner["region"] = r.DeepClone();
                else inner.Remove("region");
                b.Params["condition"] = condition;
                return null;
            },
            Placeholder: "Full window", MaxLength: 40)
        with { ShowIf = b => Conditions.Read(b.GetObject("condition")).Kind.TrimStart('!') is "textVisible" or "imageVisible" };

    public static FieldDef ConditionValue() =>
        new FieldDef("condition.value", "Value", FieldKind.Text,
            b => Conditions.Read(b.GetObject("condition")).Value,
            (b, text) =>
            {
                var condition = b.GetObject("condition");
                var (kind, _) = Conditions.Read(condition);
                if (Conditions.Check(kind, text) is { } error) return error;
                b.Params["condition"] = Conditions.Write(kind, text, condition);
                return null;
            },
            MaxLength: 200)
        with { ShowIf = b => !Conditions.IsProgramKind(Conditions.Read(b.GetObject("condition")).Kind), Label = "Value" };
}

/// <summary>Reads and writes nested params like "from.x".</summary>
internal static class P
{
    public static JsonNode? Get(Block b, string path)
    {
        JsonNode? node = b.Params;
        foreach (var part in path.Split('.'))
            node = node is JsonObject o ? o[part] : null;
        return node;
    }

    public static void Set(Block b, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        var obj = b.Params;
        for (var i = 0; i < parts.Length - 1; i++)
            obj = obj[parts[i]] as JsonObject ?? (JsonObject)(obj[parts[i]] = new JsonObject());
        obj[parts[^1]] = value;
    }

    public static void Remove(Block b, string path)
    {
        var parts = path.Split('.');
        var obj = b.Params;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (obj[parts[i]] is not JsonObject child) return;
            obj = child;
        }
        obj.Remove(parts[^1]);
    }

    public static string Text(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue(out string? s) => s,
        JsonValue v when v.TryGetValue(out bool flag) => flag ? "true" : "false",
        JsonValue v => BlockParams.AsInt(v)?.ToString(CultureInfo.InvariantCulture) ?? v.ToJsonString(),
        _ => "",
    };
}

/// <summary>
/// The condition editor of If and Repeat until: one kind plus one value, as in the mockup, mapped to the JSON
/// condition objects ("not" wraps the negative kinds).
/// </summary>
public static class Conditions
{
    public static readonly Choice[] Kinds =
    [
        new("textVisible", "Text visible"),
        new("!textVisible", "Text not visible"),
        new("imageVisible", "Image visible"),
        new("!imageVisible", "Image not visible"),
        new("pixelColor", "Pixel matches"),
        new("!pixelColor", "Pixel doesn't match"),
        new("windowFocused", "Window focused"),
        new("!windowFocused", "Window not focused"),
        new("processRunning", "Program running"),
        new("!processRunning", "Program not running"),
        new("keyHeld", "Key held"),
        new("!keyHeld", "Key not held"),
    ];

    /// <summary>The condition itself, inside its "not" when it has one.</summary>
    public static JsonObject? Inner(JsonObject? c) =>
        c is not null && Str(c, "kind") == "not" ? c["condition"] as JsonObject : c;

    /// <summary>Kinds whose value is a program (picked with File Explorer).</summary>
    public static bool IsProgramKind(string kind) => kind.TrimStart('!') is "processRunning" or "windowFocused";

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
            "processRunning" or "windowFocused" => (kind, Str(c, "name") ?? ""),
            "textVisible" => (kind, Str(c, "text") ?? ""),
            "imageVisible" => (kind, Str(c, "image") ?? (Str(c, "imageData") is { Length: > 0 } ? "picture" : "")),
            "keyHeld" => (kind, Str(c, "key") ?? ""),
            "pixelColor" => (kind, $"{BlockParams.AsInt(c["x"]) ?? 0}, {BlockParams.AsInt(c["y"]) ?? 0}, {Str(c, "color") ?? "#FFFFFF"}"),
            _ => (kind, ""),
        };
    }

    /// <summary>Builds the condition object. Extra settings of <paramref name="old"/> (region, picture…) are kept.</summary>
    public static JsonObject Write(string kind, string value, JsonObject? old)
    {
        var negate = kind.StartsWith('!');
        var k = negate ? kind[1..] : kind;
        var inner = old is null ? null : Str(old, "kind") == "not" ? old["condition"] as JsonObject : old;
        var obj = inner is not null && Str(inner, "kind") == k ? (JsonObject)inner.DeepClone() : new JsonObject { ["kind"] = k };
        var v = value.Trim();
        switch (k)
        {
            case "processRunning":
                obj["name"] = v;
                break;
            case "windowFocused":
                if (v.Length > 0) obj["name"] = v; else obj.Remove("name");
                break;
            case "textVisible":
                obj["text"] = v;
                break;
            case "imageVisible":
                if (v.Length > 0) obj["image"] = v; else obj.Remove("image");
                break;
            case "keyHeld":
                obj["key"] = KeyCatalog.TryGetVirtualKey(v, out var vk) ? KeyCatalog.NameOf(vk) ?? v : v;
                break;
            case "pixelColor":
                if (ParsePixel(v) is { } p)
                {
                    obj["x"] = p.X;
                    obj["y"] = p.Y;
                    obj["color"] = p.Color;
                    obj["relativeTo"] ??= "gameWindow";
                }
                break;
        }
        return negate ? new JsonObject { ["kind"] = "not", ["condition"] = obj } : obj;
    }

    /// <summary>Why the value doesn't fit the kind, or null.</summary>
    public static string? Check(string kind, string value)
    {
        var v = value.Trim();
        return kind.TrimStart('!') switch
        {
            "processRunning" when v.Length == 0 => "Pick the program, like RobloxPlayerBeta.exe.",
            "textVisible" when v.Length == 0 => "Enter the text to look for.",
            "textVisible" when v.Length > MacroLimits.TypeTextMaxLength => $"Use at most {MacroLimits.TypeTextMaxLength} characters.",
            "keyHeld" when !KeyCatalog.IsKnown(v) => "Not a key Night-loop knows. Try Shift, Space or F1.",
            "pixelColor" when ParsePixel(v) is null => "Use x, y, #RRGGBB, or Pick color from screen.",
            _ => null,
        };
    }

    /// <summary>Kinds whose values mean the same thing (so switching between them keeps the value).</summary>
    public static string ValueKind(string kind) => kind.TrimStart('!') switch
    {
        "processRunning" or "windowFocused" => "process",
        var k => k,
    };

    public static string DefaultValue(string kind) => kind.TrimStart('!') switch
    {
        "textVisible" => "Done",
        "keyHeld" => "Shift",
        "pixelColor" => "0, 0, #FFFFFF",
        _ => "",
    };

    public static string Describe(JsonObject? c)
    {
        var (kind, value) = Read(c);
        var label = Kinds.FirstOrDefault(k => k.Value == kind)?.Label ?? kind;
        // A picture check without its picture stops the macro with an error, so say so on the row.
        if (kind.TrimStart('!') == "imageVisible" && Inner(c)?["imageData"] is not JsonValue) return $"{label} · no picture yet";
        return value.Length > 0 ? $"{label} {value}" : label;
    }

    private static (int X, int Y, string Color)? ParsePixel(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x)) return null;
        if (!int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var y)) return null;
        var color = parts[2].StartsWith('#') ? parts[2] : "#" + parts[2];
        return MacroValidator.IsColor(color) ? (x, y, color.ToUpperInvariant()) : null;
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
