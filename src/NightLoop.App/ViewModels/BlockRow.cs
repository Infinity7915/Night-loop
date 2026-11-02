using CommunityToolkit.Mvvm.ComponentModel;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.App.ViewModels;

/// <summary>One line on the timeline.</summary>
public sealed partial class BlockRow : ObservableObject
{
    public BlockRow(int index, Block block)
    {
        Index = index;
        Block = block;
        (Tag, Title, Detail, Category) = BlockDescriber.Describe(block);
    }

    public int Index { get; }
    public string Number => (Index + 1).ToString("00");
    public Block Block { get; }
    public string Tag { get; }
    public string Title { get; }
    public string Detail { get; }
    /// <summary>input, timing, logic, window, utility, camera or detect. Picks the chip color.</summary>
    public string Category { get; }
    public bool Enabled => Block.Enabled;

    [ObservableProperty]
    private bool _isCurrent;
}
