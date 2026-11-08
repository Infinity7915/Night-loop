using NightLoop.Contracts.MacroDocument;

namespace NightLoop.Engine.Runtime;

/// <summary>
/// Settings › Macros' extra waits, read live by every run so a change applies to macros already running.
/// <list type="bullet">
/// <item><b>Wait after clicks</b>: after blocks that click (Click, Drag, Find &amp; click, Scan &amp; turn; on Android Tap,
/// Long press, Swipe), unless the
/// block sets its own "Wait after click".</item>
/// <item><b>Wait after every block</b>: after every other block. Clicking blocks only get it on top of their own wait
/// when that wait isn't 0, so a quick click → drag → click chain stays quick.</item>
/// </list>
/// </summary>
public sealed class BlockWaits
{
    private int _afterClicksMs;
    private int _afterBlocksMs;

    public int AfterClicksMs
    {
        get => Volatile.Read(ref _afterClicksMs);
        set => Volatile.Write(ref _afterClicksMs, Math.Clamp(value, 0, MacroLimits.WaitAfterMaxMs));
    }

    public int AfterBlocksMs
    {
        get => Volatile.Read(ref _afterBlocksMs);
        set => Volatile.Write(ref _afterBlocksMs, Math.Clamp(value, 0, MacroLimits.WaitAfterMaxMs));
    }

    /// <summary>How long to wait after <paramref name="block"/> finished (<paramref name="found"/>: it found its target).</summary>
    public int After(Block block, bool found)
    {
        if (block.Type is BlockTypes.Comment or BlockTypes.Description or BlockTypes.Label or BlockTypes.Stop) return 0;
        if (!Clicked(block, found)) return AfterBlocksMs;
        var click = WaitAfterClick(block, AfterClicksMs);
        return click > 0 ? click + AfterBlocksMs : 0;
    }

    /// <summary>The block's own "Wait after click", or <paramref name="fallback"/> (Settings) when it has none.</summary>
    public static int WaitAfterClick(Block block, int fallback) =>
        Math.Clamp(block.GetInt(BlockTypes.WaitAfterKey) ?? fallback, 0, MacroLimits.WaitAfterMaxMs);

    /// <summary>Whether this block clicked: Find &amp; click and Scan &amp; turn only click when they find something.</summary>
    private static bool Clicked(Block block, bool found) => block.Type switch
    {
        BlockTypes.Click or BlockTypes.Drag or BlockTypes.Tap or BlockTypes.LongPress or BlockTypes.Swipe or BlockTypes.SwipeHold => true,
        BlockTypes.FindClick => found,
        BlockTypes.ScanTurn => found && block.GetString("whenFound", "click") == "click",
        _ => false,
    };
}
