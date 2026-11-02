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

    public const string CameraTurn = "camera.turn";
    public const string CameraTurnUntilText = "camera.turnUntilText";
    public const string CameraTurnUntilImage = "camera.turnUntilImage";

    public const string Wait = "timing.wait";
    public const string WaitRandom = "timing.waitRandom";
    public const string WaitForText = "timing.waitForText";
    public const string WaitForImage = "timing.waitForImage";
    public const string Pause = "timing.pause";

    public const string DetectText = "detect.text";
    public const string DetectImage = "detect.image";
    public const string DetectPixelColor = "detect.pixelColor";

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

    public const string Comment = "utility.comment";
    public const string Description = "utility.description";
    public const string Notify = "utility.notify";
    public const string PlaySound = "utility.playSound";
    public const string RunMacro = "utility.runMacro";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Key, Click, Move, Drag, Scroll, Type,
        CameraTurn, CameraTurnUntilText, CameraTurnUntilImage,
        Wait, WaitRandom, WaitForText, WaitForImage, Pause,
        DetectText, DetectImage, DetectPixelColor,
        If, Else, End, Loop, RepeatUntil, Label, Goto, Stop,
        WindowResize, WindowPosition, WindowFocus,
        Comment, Description, Notify, PlaySound, RunMacro,
    };

    /// <summary>Blocks that open a section closed by logic.end.</summary>
    public static bool IsOpener(string type) => type is If or Loop or RepeatUntil;

    /// <summary>Blocks that send keyboard or mouse input (used by the target lock and the runaway guard).</summary>
    public static bool IsInput(string type) => type.StartsWith("input.", StringComparison.Ordinal) || type.StartsWith("camera.", StringComparison.Ordinal);
}
