namespace NightLoop.Contracts.MacroDocument;

/// <summary>The block catalog (schema section 6). The type string is what goes in the JSON.</summary>
public static class BlockTypes
{
    public const string Key = "input.key";
    public const string Click = "input.click";
    public const string Move = "input.move";
    public const string Drag = "input.drag";
    public const string Scroll = "input.scroll";
    public const string Type = "input.type";
    /// <summary>Several keys together, like Ctrl+Shift+E. Params: keys.</summary>
    public const string Combo = "input.combo";
    /// <summary>Hold one key for a set time. Params: key, holdMs.</summary>
    public const string HoldKey = "input.holdKey";

    public const string CameraTurn = "camera.turn";
    public const string CameraTurnUntilText = "camera.turnUntilText";
    public const string CameraTurnUntilImage = "camera.turnUntilImage";
    /// <summary>Zoom with the mouse wheel. Params: direction (in/out), steps.</summary>
    public const string CameraZoom = "camera.zoom";
    /// <summary>Turn until the last found match is in the middle of the window. Params: tolerancePx.</summary>
    public const string CameraCenter = "camera.center";

    public const string Wait = "timing.wait";
    public const string WaitRandom = "timing.waitRandom";
    public const string WaitForText = "timing.waitForText";
    public const string WaitForImage = "timing.waitForImage";
    public const string Pause = "timing.pause";
    /// <summary>Unpause another macro. Params: target, from (paused/start).</summary>
    public const string Resume = "timing.resume";
    /// <summary>Pause until a key is pressed. Params: key, timeoutMs (optional).</summary>
    public const string WaitForKey = "timing.waitForKey";

    public const string DetectText = "detect.text";
    public const string DetectImage = "detect.image";
    public const string DetectPixelColor = "detect.pixelColor";
    /// <summary>Find text, an image or a color and click it. Params: target, value, button, region, onFail.</summary>
    public const string FindClick = "detect.findClick";
    /// <summary>Turn the camera step by step until a target is seen. Params: target, value, direction, stepPx, maxTurns, whenFound, onFail.</summary>
    public const string ScanTurn = "detect.scanTurn";

    public const string If = "logic.if";
    public const string Else = "logic.else";
    public const string End = "logic.end";
    public const string Loop = "logic.loop";
    public const string RepeatUntil = "logic.repeatUntil";
    public const string Label = "logic.label";
    public const string Goto = "logic.goto";
    public const string Stop = "logic.stop";

    public const string WindowResize = "window.resize";
    public const string WindowPosition = "window.position";
    public const string WindowFocus = "window.focus";
    /// <summary>Pause until a window opens and comes to the front. Params: process (optional), timeoutMs.</summary>
    public const string WaitForWindow = "window.waitFor";

    public const string Comment = "utility.comment";
    public const string Description = "utility.description";
    public const string Notify = "utility.notify";
    public const string PlaySound = "utility.playSound";
    public const string RunMacro = "utility.runMacro";
    /// <summary>Save a picture of the window or screen. Params: region, file.</summary>
    public const string Screenshot = "utility.screenshot";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Key, Click, Move, Drag, Scroll, Type, Combo, HoldKey,
        CameraTurn, CameraTurnUntilText, CameraTurnUntilImage, CameraZoom, CameraCenter,
        Wait, WaitRandom, WaitForText, WaitForImage, Pause, Resume, WaitForKey,
        DetectText, DetectImage, DetectPixelColor, FindClick, ScanTurn,
        If, Else, End, Loop, RepeatUntil, Label, Goto, Stop,
        WindowResize, WindowPosition, WindowFocus, WaitForWindow,
        Comment, Description, Notify, PlaySound, RunMacro, Screenshot,
    };

    /// <summary>Blocks with an "On fail" choice (continue, retry, go to label, stop).</summary>
    public static bool HasOnFail(string type) => type is WaitForText or WaitForImage or DetectPixelColor or FindClick or ScanTurn;

    /// <summary>Blocks that open a section closed by logic.end.</summary>
    public static bool IsOpener(string type) => type is If or Loop or RepeatUntil;

    /// <summary>Blocks that send keyboard or mouse input (used by the target lock and the runaway guard).</summary>
    public static bool IsInput(string type) =>
        type.StartsWith("input.", StringComparison.Ordinal) || type.StartsWith("camera.", StringComparison.Ordinal)
        || type is FindClick or ScanTurn;

    /// <summary>Blocks that read the screen for text or images. Pixel colors don't count: they need no text reading.</summary>
    public static bool NeedsVision(Block block) => block.Type switch
    {
        WaitForText or WaitForImage or DetectText or DetectImage or CameraTurnUntilText or CameraTurnUntilImage => true,
        FindClick or ScanTurn => block.GetString("target", "text") != "pixel",
        _ => false,
    };
}
