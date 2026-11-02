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
    public double Indent => Depth * 20;
    public bool IsDescription => Block.Type == BlockTypes.Description;

    [ObservableProperty] private string _tag = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InlineDetail))]
    private string _detail = "";
    /// <summary>The detail shown after the title. Descriptions show theirs on its own lines instead.</summary>
    public string InlineDetail => IsDescription ? "" : Detail;
    [ObservableProperty] private string _note = "";
    /// <summary>Input, Camera, Timing, Detect, Logic, Window or Utility. Picks the chip color.</summary>
    [ObservableProperty] private string _category = "";
    [ObservableProperty] private string _color = "#C9D1D9";
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isSelected;
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
    }
}
