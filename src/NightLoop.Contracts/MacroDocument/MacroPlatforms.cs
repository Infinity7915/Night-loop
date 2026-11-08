namespace NightLoop.Contracts.MacroDocument;

/// <summary>
/// Which app a macro is made for. A macro keeps its platform for life: the PC app only plays PC macros and the
/// Android app only plays Android macros. Stored in <see cref="MacroDoc.Platform"/>; missing means PC, so every
/// macro made before Android existed stays a PC macro.
/// </summary>
public static class MacroPlatforms
{
    public const string Pc = "pc";
    public const string Android = "android";

    public static bool IsKnown(string? platform) => platform is null or Pc or Android;

    /// <summary>The platform a document belongs to: null and "pc" are both PC.</summary>
    public static string Of(MacroDoc doc) => doc.Platform ?? Pc;

    /// <summary>Blocks that only make sense on a phone: touch, Android buttons and apps.</summary>
    public static readonly IReadOnlySet<string> AndroidOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        BlockTypes.Tap, BlockTypes.LongPress, BlockTypes.Swipe, BlockTypes.DeviceButton, BlockTypes.AppOpen, BlockTypes.WaitForApp,
    };

    /// <summary>
    /// Blocks both apps run. Everything else (mouse, camera, windows, screen reading) is PC only for now; screen
    /// reading comes to Android in a later version and joins this list then. On Android, Key, Hold key and Type reach
    /// the text box that has focus (Android 13+), through Night-loop's accessibility service.
    /// </summary>
    public static readonly IReadOnlySet<string> Shared = new HashSet<string>(StringComparer.Ordinal)
    {
        BlockTypes.Wait, BlockTypes.WaitRandom, BlockTypes.Pause, BlockTypes.Resume,
        BlockTypes.If, BlockTypes.Else, BlockTypes.End, BlockTypes.Loop, BlockTypes.RepeatUntil,
        BlockTypes.Label, BlockTypes.Goto, BlockTypes.Stop,
        BlockTypes.Comment, BlockTypes.Description, BlockTypes.Notify, BlockTypes.PlaySound, BlockTypes.RunMacro,
        BlockTypes.Key, BlockTypes.HoldKey, BlockTypes.Type,
    };

    /// <summary>Whether a block type can be used in a macro for this platform.</summary>
    public static bool Allows(string platform, string blockType) =>
        platform == Android ? AndroidOnly.Contains(blockType) || Shared.Contains(blockType) : !AndroidOnly.Contains(blockType);

    /// <summary>If / Repeat until condition kinds per platform. "appInFront" is Android only.</summary>
    public static bool AllowsCondition(string platform, string kind) =>
        platform == Android ? kind is "appInFront" or "not" or "all" or "any" : kind != "appInFront";

    public static string Label(string platform) => platform == Android ? "Android" : "PC";
}
