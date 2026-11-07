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
}
