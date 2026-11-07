using CommunityToolkit.Mvvm.Input;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.App.ViewModels;

/// <summary>
/// Folding Loop, If and Repeat until blocks on the timeline: the arrow on the row hides the blocks inside (and the
/// End) under it. Only the editor's view: it isn't saved with the macro, nothing starts folded, and playback runs
/// the hidden blocks as usual.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Ids of the folded blocks in the open macro. Kept by id, so undo, redo and moves keep them folded.</summary>
    private readonly HashSet<string> _folded = new(StringComparer.Ordinal);

    /// <summary>For each block: the row that shows it (itself, or the outermost folded block it sits in).</summary>
    private int[] _shownAs = [];

    /// <summary>The arrow on a Loop / If / Repeat until row: folds or unfolds the blocks inside.</summary>
    [RelayCommand]
    private void ToggleCollapse(BlockRow row)
    {
        if (!row.CanCollapse) return;
        if (!_folded.Remove(row.Block.Id)) _folded.Add(row.Block.Id);
        ApplyCollapse();
        // The highlighted block went out of sight: highlight the folded row instead, so Play and Add still work
        // from where you were.
        if (SelectedBlock is { IsHidden: true }) SelectedBlock = row;
    }

    /// <summary>Another macro (or a fresh one) is in the editor: everything shows.</summary>
    private void ResetCollapse() => _folded.Clear();

    /// <summary>Hides the rows inside folded blocks and marks which rows can fold. Runs after the rows are rebuilt.</summary>
    private void ApplyCollapse()
    {
        var structure = MacroStructure.Analyze(_doc.Blocks);
        var count = Math.Min(Blocks.Count, structure.Partner.Length);
        _shownAs = new int[count];
        var hideUntil = -1;
        var owner = -1;
        for (var i = 0; i < count; i++)
        {
            var row = Blocks[i];
            var hidden = i <= hideUntil;
            var end = structure.Partner[i];
            row.CanCollapse = BlockTypes.IsOpener(row.Block.Type) && end > i;
            var folded = row.CanCollapse && _folded.Contains(row.Block.Id);
            row.IsCollapsed = folded;
            row.CoversTo = folded ? end : i;
            var inside = end - i - 1;
            row.FoldedText = !folded ? "" : inside switch { 0 => "empty", 1 => "1 block inside", _ => $"{inside} blocks inside" };
            row.IsHidden = hidden;
            _shownAs[i] = hidden ? owner : i;
            if (folded && !hidden)
            {
                hideUntil = end;
                owner = i;
            }
        }
    }

    /// <summary>The row a block shows on: itself, or the folded row it's hidden under.</summary>
    private int ShownRow(int index) => index >= 0 && index < _shownAs.Length ? _shownAs[index] : index;

    /// <summary>Unfolds whatever hides this block, so it can be highlighted and seen.</summary>
    private void Unfold(int index)
    {
        var changed = false;
        while (ShownRow(index) is var shown && shown != index && shown >= 0)
        {
            _folded.Remove(Blocks[shown].Block.Id);
            ApplyCollapse();
            changed = true;
        }
        if (changed) RevealRequested?.Invoke(index);
    }

    /// <summary>
    /// The blocks that show as one row at this index in the macro as it is now: a folded block's whole section
    /// (opener to End), or just this block. Worked out from the blocks, so it stays right while blocks are moved.
    /// </summary>
    private (int From, int To) ShownUnit(int index)
    {
        if (_folded.Count == 0 || index < 0 || index >= _doc.Blocks.Count) return (index, index);
        var structure = MacroStructure.Analyze(_doc.Blocks);
        // The first folded opener (from the top) whose section holds the index is the outermost one.
        for (var i = 0; i <= index; i++)
        {
            var b = _doc.Blocks[i];
            if (BlockTypes.IsOpener(b.Type) && structure.Partner[i] is var end && end > i && end >= index && _folded.Contains(b.Id))
                return (i, end);
        }
        return (index, index);
    }

    /// <summary>Where Add block and Paste put new blocks: under the highlighted row, after its End when it's folded.</summary>
    private int InsertAt => SelectedBlock is null ? _doc.Blocks.Count : ShownUnit(SelectedBlock.Index).To + 1;
}
