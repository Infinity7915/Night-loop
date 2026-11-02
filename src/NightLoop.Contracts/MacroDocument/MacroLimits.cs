namespace NightLoop.Contracts.MacroDocument;

/// <summary>Every macro limit in one place (schema section 11). Client and server both read these.</summary>
public static class MacroLimits
{
    public const int CurrentSchema = 1;
    public const int MacrosPerUser = 15;
    public const int NameMaxLength = 60;
    public const int MaxDocumentBytes = 200 * 1024;
    public const int MaxBlocks = 500;
    public const int MaxNestingDepth = 16;
    public const int TypeTextMaxLength = 200;
    public const int CommentMaxLength = 200;
    public const int DescriptionMaxLength = 5000;
    public const int WaitMinMs = 1;
    public const int WaitMaxMs = 3_600_000;
    public const int CountMin = 1;
    public const int CountMax = 100_000;
    public const int DurationMinSeconds = 1;
    public const int DurationMaxSeconds = 86_400;
    public const int ScrollMin = -50;
    public const int ScrollMax = 50;
    public const int ClicksMin = 1;
    public const int ClicksMax = 3;
    public const int TimingJitterMaxPct = 50;
    public const int HumanizeMouseMaxPx = 10;
    public const int ConditionGroupMaxChildren = 5;
    public const int MaxRunsAtOnce = 8;
    public const int SpeedMinPct = 10;
    public const int SpeedMaxPct = 1000;
    public const int ZoomStepsMax = 50;
    public const int ScanTurnsMax = 360;
    /// <summary>File names for the screenshot block (saved under Pictures\Night-loop).</summary>
    public const int FileNameMaxLength = 80;
    /// <summary>Recorded mouse paths keep at most this many points per block.</summary>
    public const int PathMaxPoints = 64;
    /// <summary>A small picture saved inside an image block (base64 text), about 40 KB.</summary>
    public const int ImageDataMaxLength = 56_000;
    /// <summary>Program paths, process names and arguments (Windows' classic MAX_PATH).</summary>
    public const int PathMaxLength = 260;
    /// <summary>Gliding the mouse to a click point takes at most this long.</summary>
    public const int MoveMaxMs = 10_000;
}
