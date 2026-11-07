using CommunityToolkit.Mvvm.ComponentModel;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.App.ViewModels;

/// <summary>One line on the timeline. Changing Enabled or Breakpoint edits the block and autosaves.</summary>
public sealed partial class BlockRow : ObservableObject
{
    private readonly Action<BlockRow> _changed;

    public BlockRow(int index, Block block, int depth, Action<BlockRow> changed)
    {
        Index = index;
        CoversTo = index;
        Block = block;
        Depth = depth;
        _changed = changed;
        Refresh();
    }

    public int Index { get; }
    public string Number => (Index + 1).ToString("00");
    public Block Block { get; }
    /// <summary>How many If / Loop / Repeat until blocks this one sits inside (indents the row).</summary>
    public int Depth { get; }
    /// <summary>22 px per level, with a guide line in each (as in the mockup).</summary>
    public double Indent => Depth * 22;
    public bool IsDescription => Block.Type == BlockTypes.Description;
    /// <summary>Loop / If / Repeat until / Else / End: their tag chip gets a tinted background.</summary>
    public bool IsStructural => BlockTypes.IsOpener(Block.Type) || Block.Type is BlockTypes.Else or BlockTypes.End;

    [ObservableProperty] private string _tag = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InlineDetail))]
    private string _detail = "";
    /// <summary>The detail shown after the title. Descriptions show theirs on its own lines instead.</summary>
    public string InlineDetail
    {
        get
        {
            if (IsDescription) return "";
            var text = HeldFor.Length > 0 ? $"{Detail} · {HeldFor}" : Detail;
            return FoldedText.Length > 0 ? (text.Length > 0 ? $"{text} · {FoldedText}" : FoldedText) : text;
        }
    }

    /// <summary>A Loop / If / Repeat until with its End, so the blocks inside can be folded away.</summary>
    [ObservableProperty] private bool _canCollapse;
    /// <summary>Folded: the blocks inside (and the End) are hidden under this row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CollapseToolTip))]
    private bool _isCollapsed;
    /// <summary>Inside a folded Loop / If / Repeat until, so the row isn't shown.</summary>
    [ObservableProperty] private bool _isHidden;
    /// <summary>On a folded row: "5 blocks inside".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InlineDetail))]
    private string _foldedText = "";
    /// <summary>The last block this row stands for: its End when folded, else itself.</summary>
    public int CoversTo { get; set; }
    public string CollapseToolTip => IsCollapsed ? "Expand: show the blocks inside" : "Collapse: hide the blocks inside";

    /// <summary>On a Hold down: how long until its Let go ("held 130 ms"). Set by the timeline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InlineDetail))]
    private string _heldFor = "";
    [ObservableProperty] private string _note = "";
    /// <summary>A Description block's text, shown under its title.</summary>
    [ObservableProperty] private string _descriptionText = "";
    /// <summary>Input, Camera, Timing, Detect, Logic, Window or Utility. Picks the chip color.</summary>
    [ObservableProperty] private string _category = "";
    [ObservableProperty] private string _color = "#C9D1D9";
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isSelected;
    /// <summary>Ticked on the timeline, so the bar's Move up, Move down and Delete act on it with the others.</summary>
    [ObservableProperty] private bool _isChecked;
    /// <summary>The run stopped here with an error.</summary>
    [ObservableProperty] private bool _isFault;

    public bool Enabled
    {
        get => Block.Enabled;
        set
        {
            if (Block.Enabled == value) return;
            Block.Enabled = value;
            OnPropertyChanged();
            _changed(this);
        }
    }

    public bool Breakpoint
    {
        get => Block.Breakpoint;
        set
        {
            if (Block.Breakpoint == value) return;
            Block.Breakpoint = value;
            OnPropertyChanged();
            _changed(this);
        }
    }

    /// <summary>Re-reads the block after an edit in the inspector.</summary>
    public void Refresh()
    {
        string category;
        (Tag, Title, Detail, category) = BlockDescriber.Describe(Block);
        Category = category;
        Color = BlockCatalog.ColorOf(category);
        Note = Block.GetString("note") is { Length: > 0 } n ? "// " + n : "";
        DescriptionText = IsDescription ? (Block.GetString("text") is { Length: > 0 } t ? t : "Empty description") : "";
    }
}
