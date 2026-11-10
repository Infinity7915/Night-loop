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
    /// <summary>The opposite of Wait for image: wait until the picture is no longer on screen. Same params.</summary>
    public const string WaitForImageGone = "timing.waitForImageGone";
    /// <summary>Wait until text is no longer on screen.</summary>
    public const string WaitForTextGone = "timing.waitForTextGone";
    public const string Pause = "timing.pause";
    /// <summary>Unpause another macro. Params: target, from (paused/start).</summary>
    public const string Resume = "timing.resume";
    /// <summary>Pause until a key is pressed. Params: key, timeoutMs (optional).</summary>
    public const string WaitForKey = "timing.waitForKey";

    public const string DetectText = "detect.text";
    public const string DetectImage = "detect.image";
    public const string DetectPixelColor = "detect.pixelColor";
    /// <summary>The opposite of Wait for pixel: wait until the point no longer has the color. Same params.</summary>
    public const string DetectPixelColorGone = "detect.pixelColorGone";
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
    /// <summary>Pause until a window opens (and comes to the front). Params: process (optional), until (front/open), timeoutMs.</summary>
    public const string WaitForWindow = "window.waitFor";
    /// <summary>
    /// Start a program (or open a file or shortcut). Params: path, args (optional), ifRunning (focus/new),
    /// wait (wait for its window, default true), timeoutMs.
    /// </summary>
    public const string WindowOpen = "window.open";
    /// <summary>
    /// Close a program. Params: process (optional, else the target game), how (close / closeThenEnd / end),
    /// timeoutMs (how long to wait for it to close).
    /// </summary>
    public const string WindowClose = "window.close";

    public const string Comment = "utility.comment";
    public const string Description = "utility.description";
    public const string Notify = "utility.notify";
    public const string PlaySound = "utility.playSound";
    public const string RunMacro = "utility.runMacro";
    /// <summary>Save a picture of the window or screen. Params: region, file.</summary>
    public const string Screenshot = "utility.screenshot";

    // ---- Android only (see MacroPlatforms) ----

    /// <summary>Tap the screen. Params: x, y (screen pixels), count (1–3 taps, default 1), holdMs (press length, default 50).</summary>
    public const string Tap = "touch.tap";
    /// <summary>Press and hold. Params: x, y, holdMs (default 600).</summary>
    public const string LongPress = "touch.longPress";
    /// <summary>Swipe in a straight line. Params: from {x, y}, to {x, y}, durationMs (default 300).</summary>
    public const string Swipe = "touch.swipe";
    /// <summary>
    /// Swipe, then keep the finger down at the end (a joystick: the player keeps walking). Params: from, to,
    /// durationMs (swipe time, default 300), holdMs (how long to hold, default 1000, used when there's no check),
    /// condition (optional: hold until this check is true, e.g. text or a picture shows up or goes away),
    /// timeoutMs (with a check: let go after this long anyway; 0 or missing = Infinite).
    /// </summary>
    public const string SwipeHold = "touch.swipeHold";
    /// <summary>
    /// Swipe again and again (turning the camera) until text or a picture is on screen. Params: from, to, durationMs,
    /// target (text / image), value (the text), imageData…, maxTurns (default 12), whenFound (tap / none), onFail.
    /// </summary>
    public const string SwipeScan = "touch.swipeScan";
    /// <summary>
    /// Hold the joystick and walk toward a picture (or text), steering at it, until it's near the middle of the
    /// screen or a check is true. Params: from (joystick middle), to (pushed forward), target, value, imageData…,
    /// tolerancePx (how near the middle counts as there, default 200), condition (optional, stop when true instead),
    /// timeoutMs (default 30 s, 0 = Infinite), whenFound (tap / none), onFail.
    /// </summary>
    public const string WalkTo = "touch.walkTo";

    /// <summary>"whenFound" on Wait for text / picture / color: "tap" (Android) or "click" (PC) taps where it was found.</summary>
    public static bool HasTapWhenFound(string type) => type is WaitForText or WaitForImage or DetectPixelColor or SwipeScan or WalkTo;
    /// <summary>Press an Android system button. Params: button (back, home, recents, notifications).</summary>
    public const string DeviceButton = "device.button";
    /// <summary>Open an app. Params: package (e.g. com.roblox.client), wait (wait until it's in front, default true), timeoutMs.</summary>
    public const string AppOpen = "app.open";
    /// <summary>Wait until an app is in front. Params: package, timeoutMs (0 or missing = Infinite).</summary>
    public const string WaitForApp = "app.waitFor";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Key, Click, Move, Drag, Scroll, Type, Combo, HoldKey,
        CameraTurn, CameraTurnUntilText, CameraTurnUntilImage, CameraZoom, CameraCenter,
        Wait, WaitRandom, WaitForText, WaitForTextGone, WaitForImage, WaitForImageGone, Pause, Resume, WaitForKey,
        DetectText, DetectImage, DetectPixelColor, DetectPixelColorGone, FindClick, ScanTurn,
        If, Else, End, Loop, RepeatUntil, Label, Goto, Stop,
        WindowResize, WindowPosition, WindowFocus, WaitForWindow, WindowOpen, WindowClose,
        Comment, Description, Notify, PlaySound, RunMacro, Screenshot,
        Tap, LongPress, Swipe, SwipeHold, SwipeScan, WalkTo, DeviceButton, AppOpen, WaitForApp,
    };

    /// <summary>Blocks that act on what they found and have an "After found" choice (go on, leave the loop, go to a label, stop).</summary>
    public static bool HasAfterFound(string type) => type is FindClick or ScanTurn;

    /// <summary>Blocks that click, with their own "Wait after click" (blank follows Settings › Macros).</summary>
    public static bool HasWaitAfterClick(string type) => type is Click or Drag or FindClick or ScanTurn;

    /// <summary>The "Wait after click" setting on clicking blocks, in ms.</summary>
    public const string WaitAfterKey = "waitAfterMs";

    /// <summary>Find &amp; click / Scan &amp; turn with a picture: where inside the found picture to click, 0–100 % across.</summary>
    public const string ClickXKey = "clickXPct";

    /// <summary>
    /// Picture blocks with Match colors on: true lets a dimmer or brighter copy of the picture match (the screen's
    /// colors are averaged to the picture's brightness first). Missing or false: the colors must match as captured.
    /// </summary>
    public const string AverageColorsKey = "averageColors";

    /// <summary>
    /// Picture blocks with Match colors and Dimmer or brighter off: how much dimmer or brighter a copy may be, 0–100 %
    /// (see <see cref="Brightness"/>).
    /// </summary>
    public const string BrightnessKey = "brightnessPct";
    /// <summary>…and 0–100 % down. Both blank = the middle.</summary>
    public const string ClickYKey = "clickYPct";

    /// <summary>Blocks with an "On fail" choice (continue, retry, go to label, stop).</summary>
    public static bool HasOnFail(string type) => type is SwipeScan or WalkTo or WaitForText or WaitForTextGone or WaitForImage or WaitForImageGone or DetectPixelColor or DetectPixelColorGone or FindClick or ScanTurn;

    /// <summary>Blocks that open a section closed by logic.end.</summary>
    public static bool IsOpener(string type) => type is If or Loop or RepeatUntil;

    /// <summary>Blocks that send keyboard or mouse input (used by the target lock and the runaway guard).</summary>
    public static bool IsInput(string type) =>
        type.StartsWith("input.", StringComparison.Ordinal) || type.StartsWith("camera.", StringComparison.Ordinal)
        || type.StartsWith("touch.", StringComparison.Ordinal) || type is DeviceButton
        || type is FindClick or ScanTurn;

    /// <summary>Blocks that read the screen for text or images. Pixel colors don't count: they need no text reading.</summary>
    public static bool NeedsVision(Block block) => block.Type switch
    {
        WaitForText or WaitForTextGone or WaitForImage or WaitForImageGone or DetectText or DetectImage or CameraTurnUntilText or CameraTurnUntilImage => true,
        FindClick or ScanTurn or SwipeScan or WalkTo => block.GetString("target", "text") != "pixel",
        _ => false,
    };
}
