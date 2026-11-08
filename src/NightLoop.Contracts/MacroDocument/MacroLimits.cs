namespace NightLoop.Contracts.MacroDocument;

/// <summary>Every macro limit in one place (schema section 11). Client and server both read these.</summary>
public static class MacroLimits
{
    public const int CurrentSchema = 1;
    public const int MacrosPerUser = 15;
    public const int NameMaxLength = 60;
    public const int MaxDocumentBytes = 200 * 1024;
    /// <summary>Blocks across every macro on an account. No cap per macro beyond the size limit (MaxDocumentBytes).</summary>
    public const int BlocksPerUser = 2000;
    /// <summary>New blocks an account may place per day (UTC).</summary>
    public const int BlocksPerDay = 200;
    /// <summary>New macros an account may create per day (UTC), on top of the MacrosPerUser cap.</summary>
    public const int NewMacrosPerDay = 30;
    /// <summary>The app warns (in the window, not in Windows) once this many or fewer blocks or macros are left.</summary>
    public const int WarnWhenLeft = 10;
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
    /// <summary>Wait after click (on a block) and Settings' wait sliders go up to this.</summary>
    public const int WaitAfterMaxMs = 2_000;
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
    /// <summary>Mouse paths (only in macros recorded before recording was removed) keep at most this many points per block.</summary>
    public const int PathMaxPoints = 64;
    /// <summary>A small picture saved inside an image block (base64 text), about 40 KB.</summary>
    public const int ImageDataMaxLength = 56_000;
    /// <summary>Picture blocks: how much smaller or bigger the picture may look on screen (60 = 40%–160% of its size).</summary>
    public const int SizeToleranceMaxPct = 60;
    /// <summary>Program paths, process names and arguments (Windows' classic MAX_PATH).</summary>
    public const int PathMaxLength = 260;
    /// <summary>Gliding the mouse to a click point takes at most this long.</summary>
    public const int MoveMaxMs = 10_000;
    /// <summary>Block ids (the app makes short ones like "b12").</summary>
    public const int BlockIdMaxLength = 64;
    /// <summary>The most text any one block setting can hold, apart from pictures, descriptions and conditions.</summary>
    public const int ParamMaxLength = 4096;
    /// <summary>Touch blocks: screen coordinates are 0–20,000 px.</summary>
    public const int ScreenMaxPx = 20_000;
    /// <summary>Touch blocks: longest press or swipe.</summary>
    public const int TouchHoldMaxMs = 60_000;

    /// <summary>
    /// Blocks in <paramref name="after"/> whose id isn't in <paramref name="before"/>: what an edit placed. Moving,
    /// editing or deleting blocks places nothing. The app gives every new block a fresh id.
    /// </summary>
    public static int NewBlocks(MacroDoc? before, MacroDoc after)
    {
        if (before is null) return after.Blocks.Count;
        var old = new HashSet<string>(before.Blocks.Select(b => b.Id), StringComparer.Ordinal);
        return after.Blocks.Count(b => !old.Contains(b.Id));
    }

    /// <summary>When the daily counts start again: the next midnight UTC.</summary>
    public static DateTimeOffset NextDailyReset(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1);
    }
}
