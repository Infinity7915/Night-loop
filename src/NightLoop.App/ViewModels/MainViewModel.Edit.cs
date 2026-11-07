using NightLoop.Contracts.MacroDocument;

namespace NightLoop.App.ViewModels;

/// <summary>
/// Editor shortcuts (keys changeable in Settings › Macros): undo and redo block changes, copy, cut and paste
/// blocks. Copied blocks stay inside Night-loop (never on the Windows clipboard), so macros can't leave the app.
/// </summary>
public sealed partial class MainViewModel
{
    private const int UndoLimit = 200;
    /// <summary>Typing into the same field within this long counts as one change to undo.</summary>
    private static readonly TimeSpan UndoMergeWindow = TimeSpan.FromSeconds(1.5);

    /// <summary>The timeline at one moment: its blocks as JSON, plus which block was highlighted and which were ticked.</summary>
    private sealed record EditorState(string Blocks, string? SelectedId, string[] CheckedIds);

    private readonly Stack<EditorState> _undo = new();
    private readonly Stack<EditorState> _redo = new();
    private EditorState? _current;
    private string? _undoGroup;
    private string? _lastUndoGroup;
    private DateTime _lastUndoAt;

    /// <summary>Blocks copied with Ctrl+C, shared by every macro opened in this window.</summary>
    private static string? s_copied;
    private static int s_copiedCount;

    public bool CanUndo => _undo.Count > 0;

    /// <summary>The key picked for an editor action, like "Ctrl + Z".</summary>
    private string KeyOf(EditAction action) => EditorShortcuts.First(s => s.Action == action).KeyText;
    public bool CanRedo => _redo.Count > 0;

    private EditorState Capture() => new(
        MacroJson.Serialize(new MacroDoc { Name = "x", Blocks = _doc.Blocks }),
        SelectedBlock?.Block.Id,
        [.. _checked.Select(b => b.Id)]);

    /// <summary>A new macro (or another one) is in the editor: nothing to undo yet.</summary>
    private void ResetUndo()
    {
        _undo.Clear();
        _redo.Clear();
        _current = Capture();
        _lastUndoGroup = null;
    }

    /// <summary>Called on every edit. Saves the timeline as it was before, unless the blocks didn't change.</summary>
    private void RememberForUndo()
    {
        var now = Capture();
        if (_current is null)
        {
            _current = now;
            return;
        }
        if (now.Blocks == _current.Blocks) return;
        var merge = _undoGroup is not null && _undoGroup == _lastUndoGroup && DateTime.UtcNow - _lastUndoAt < UndoMergeWindow && _undo.Count > 0;
        if (!merge)
        {
            _undo.Push(_current);
            if (_undo.Count > UndoLimit)
            {
                var keep = _undo.Take(UndoLimit).Reverse().ToList();
                _undo.Clear();
                foreach (var s in keep) _undo.Push(s);
            }
        }
        _redo.Clear();
        _current = now;
        _lastUndoGroup = _undoGroup;
        _lastUndoAt = DateTime.UtcNow;
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    /// <summary>Ctrl+Z: puts back the blocks as they were before the last change (deleted blocks come back).</summary>
    public void Undo()
    {
        if (IsPlaying) return;
        if (_undo.Count == 0)
        {
            Status = "Nothing to undo.";
            return;
        }
        _redo.Push(Capture());
        Restore(_undo.Pop());
        Status = $"Undone. {KeyOf(EditAction.Redo)} puts it back.";
    }

    /// <summary>Ctrl+Y or Ctrl+Shift+Z: does the undone change again.</summary>
    public void Redo()
    {
        if (IsPlaying) return;
        if (_redo.Count == 0)
        {
            Status = "Nothing to redo.";
            return;
        }
        _undo.Push(Capture());
        Restore(_redo.Pop());
        Status = "Redone.";
    }

    private void Restore(EditorState state)
    {
        var blocks = MacroJson.Deserialize(state.Blocks).Blocks;
        _doc.Blocks.Clear();
        _doc.Blocks.AddRange(blocks);
        var ticked = new HashSet<string>(state.CheckedIds, StringComparer.Ordinal);
        _checked.Clear();
        foreach (var b in _doc.Blocks.Where(b => ticked.Contains(b.Id))) _checked.Add(b);
        _checkAnchor = null;
        var select = state.SelectedId is { } id ? _doc.Blocks.FindIndex(b => b.Id == id) : -2;
        ShowBlocks(selectIndex: select >= 0 ? select : null);
        _current = Capture();
        _lastUndoGroup = null;
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        MarkChanged();
    }

    /// <summary>
    /// Ctrl+C: copies the ticked blocks, or the highlighted one when none are ticked. A Loop, If or Repeat until
    /// brings the blocks inside and its End, so pasting it gives a block that runs.
    /// </summary>
    public bool CopyBlocks()
    {
        var picked = HasChecked ? CheckedSections() : SelectedBlock is { } row ? Range(SectionOf(row.Index)) : [];
        if (picked.Count == 0)
        {
            Status = "Tick the blocks to copy first (or click one).";
            return false;
        }
        var blocks = picked.Select(i => _doc.Blocks[i]).ToList();
        s_copied = MacroJson.Serialize(new MacroDoc { Name = "x", Blocks = blocks });
        s_copiedCount = blocks.Count;
        var paste = KeyOf(EditAction.Paste);
        Status = blocks.Count == 1 ? $"Copied 1 block. {paste} pastes it under the highlighted block." : $"Copied {blocks.Count} blocks. {paste} pastes them under the highlighted block.";
        return true;

        static SortedSet<int> Range((int From, int To) r) => new(Enumerable.Range(r.From, r.To - r.From + 1));
    }

    /// <summary>Ctrl+X: copies, then deletes what was copied.</summary>
    public void CutBlocks()
    {
        if (IsPlaying) return;
        if (!CopyBlocks()) return;
        var count = s_copiedCount;
        DeleteSelected();
        Status = count == 1 ? $"Cut 1 block. {KeyOf(EditAction.Paste)} pastes it." : $"Cut {count} blocks. {KeyOf(EditAction.Paste)} pastes them.";
    }

    /// <summary>Ctrl+V: puts the copied blocks under the highlighted block (or at the end when none is highlighted).</summary>
    public void PasteBlocks()
    {
        if (IsPlaying) return;
        if (s_copied is null)
        {
            Status = $"Nothing copied yet. Tick some blocks and press {KeyOf(EditAction.Copy)}.";
            return;
        }
        var copies = MacroJson.Deserialize(s_copied).Blocks;
        if (!CanPlace(copies.Count)) return;
        var at = InsertAt;
        for (var i = 0; i < copies.Count; i++)
        {
            copies[i].Id = NextId();
            _doc.Blocks.Insert(at + i, copies[i]);
        }
        ShowBlocks(selectIndex: at + copies.Count - 1);
        MarkChanged();
        RevealRequested?.Invoke(at + copies.Count - 1);
        Limits?.Placed(copies.Count);
        Status = copies.Count == 1 ? "Pasted 1 block." : $"Pasted {copies.Count} blocks.";
    }
}
