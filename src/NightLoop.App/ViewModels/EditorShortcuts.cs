using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.App.ViewModels;

/// <summary>What an editor shortcut does to the blocks.</summary>
public enum EditAction { Undo, Redo, Copy, Cut, Paste, Delete }

/// <summary>
/// One editor shortcut in Settings › Macros, like Undo = Ctrl+Z. Click it, then press the new key; Backspace puts the
/// default back and Esc cancels. Saved on this PC.
/// </summary>
public sealed partial class EditorShortcut : ObservableObject
{
    private readonly MainViewModel _owner;

    internal EditorShortcut(MainViewModel owner, EditAction action, string label, string description, Hotkey fallback, Hotkey key)
    {
        _owner = owner;
        Action = action;
        Label = label;
        Description = description;
        Default = fallback;
        _key = key;
    }

    public EditAction Action { get; }
    public string Label { get; }
    public string Description { get; }
    public Hotkey Default { get; }

    /// <summary>Name saved in settings.json.</summary>
    public string Id => Action.ToString().ToLowerInvariant();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyLabel), nameof(KeyText))]
    private Hotkey _key;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyLabel))]
    private bool _isCapturing;

    [ObservableProperty] private string _error = "";

    public string KeyText => MainViewModel.KeyText(Key.ToString());
    public string KeyLabel => IsCapturing ? "Press a key… (Esc cancels)" : $"{KeyText}  ·  Change";

    [RelayCommand]
    private void Capture() => _owner.StartShortcutCapture(this);
}

public sealed partial class MainViewModel
{
    private static readonly (EditAction Action, string Label, string Description, Hotkey Default)[] ShortcutDefs =
    [
        (EditAction.Undo, "Undo", "Brings back the blocks as they were before the last change, again and again until there's nothing left to undo.", new(HotkeyModifiers.Ctrl, 0x5A)),
        (EditAction.Redo, "Redo", "Does an undone change again.", new(HotkeyModifiers.Ctrl, 0x59)),
        (EditAction.Copy, "Copy", "Copies the ticked blocks, or the highlighted one.", new(HotkeyModifiers.Ctrl, 0x43)),
        (EditAction.Cut, "Cut", "Copies the ticked blocks, then deletes them.", new(HotkeyModifiers.Ctrl, 0x58)),
        (EditAction.Paste, "Paste", "Puts the copied blocks under the highlighted block.", new(HotkeyModifiers.Ctrl, 0x56)),
        (EditAction.Delete, "Delete", "Deletes the ticked blocks, or the highlighted one.", new(HotkeyModifiers.None, 0x2E)),
    ];

    private static readonly Hotkey SelectAllKey = new(HotkeyModifiers.Ctrl, 0x41);
    private const int EscapeKey = 0x1B;
    private const int BackspaceKey = 0x08;

    private List<EditorShortcut>? _shortcuts;

    /// <summary>Settings › Macros › Editor shortcuts.</summary>
    public IReadOnlyList<EditorShortcut> EditorShortcuts => _shortcuts ??= LoadShortcuts();

    [ObservableProperty] private EditorShortcut? _capturingShortcut;

    partial void OnCapturingShortcutChanged(EditorShortcut? value) => OnCaptureChanged();

    private List<EditorShortcut> LoadShortcuts()
    {
        var saved = _settings.EditorShortcuts;
        var list = new List<EditorShortcut>();
        foreach (var (action, label, description, fallback) in ShortcutDefs)
        {
            var id = action.ToString().ToLowerInvariant();
            var key = saved.TryGetValue(id, out var text) && Hotkey.TryParse(text, out var parsed)
                      && list.All(s => s.Key != parsed) && ReservedProblem(parsed) is null
                ? parsed
                : fallback;
            // A saved key can push another action off its default; then that one has none that clashes.
            list.Add(new EditorShortcut(this, action, label, description, fallback, key));
        }
        return list;
    }

    /// <summary>The editor action a key combo does, if any.</summary>
    public EditAction? ShortcutAction(Hotkey h) => EditorShortcuts.FirstOrDefault(s => s.Key == h)?.Action;

    /// <summary>The editor shortcut a key combo belongs to, for "this key is taken" messages.</summary>
    internal EditorShortcut? ShortcutFor(Hotkey h) => EditorShortcuts.FirstOrDefault(s => s.Key == h);

    /// <summary>Keys an editor shortcut can never be: Night-loop's own keys.</summary>
    private string? ReservedProblem(Hotkey h)
    {
        var key = KeyText(h.ToString());
        if (h == Hotkey.Panic) return $"{key} is the stop-everything key.";
        if (h == PlayHotkey) return $"{key} is the editor's Play key.";
        if (h == SelectAllKey) return $"{key} selects every block.";
        if (h.VirtualKey == EscapeKey) return "Esc clears the selection, so it can't be a shortcut.";
        return null;
    }

    internal void StartShortcutCapture(EditorShortcut shortcut)
    {
        if (CapturingShortcut is { } old) old.IsCapturing = false;
        foreach (var s in EditorShortcuts) s.Error = "";
        shortcut.IsCapturing = true;
        CapturingShortcut = shortcut;
    }

    /// <summary>Settings closed or another key picked: stop waiting for a key.</summary>
    public void CancelShortcutCapture()
    {
        if (CapturingShortcut is not { } s) return;
        s.IsCapturing = false;
        CapturingShortcut = null;
    }

    /// <summary>
    /// Called by Settings with the key pressed while picking a shortcut. Esc cancels, Backspace puts the default
    /// back. A key that starts a macro is allowed, but that macro won't start with it until one of them changes.
    /// </summary>
    public void OnShortcutCaptured(Hotkey pressed)
    {
        if (CapturingShortcut is not { } s) return;
        s.IsCapturing = false;
        CapturingShortcut = null; // registers the macro keys again
        if (pressed.VirtualKey == EscapeKey && pressed.Modifiers == HotkeyModifiers.None) return;
        var h = pressed.VirtualKey == BackspaceKey && pressed.Modifiers == HotkeyModifiers.None ? s.Default : pressed;
        if (ReservedProblem(h) is { } reserved)
        {
            s.Error = reserved;
            return;
        }
        if (EditorShortcuts.FirstOrDefault(o => o != s && o.Key == h) is { } other)
        {
            s.Error = $"{KeyText(h.ToString())} is already {other.Label}. Change that one first.";
            return;
        }
        s.Key = h;
        _settings.EditorShortcuts = EditorShortcuts
            .Where(o => o.Key != o.Default)
            .ToDictionary(o => o.Id, o => o.Key.ToString(), StringComparer.Ordinal);
        var macros = MacrosUsing(h);
        s.Error = macros.Count == 0 ? "" :
            $"{KeyText(h.ToString())} also starts {string.Join(", ", macros.Select(n => $"\"{n}\""))}. It won't start "
            + (macros.Count == 1 ? "it" : "them") + " any more until you give " + (macros.Count == 1 ? "that macro" : "those macros") + " another key or change this shortcut.";
        RefreshHotkeys();
        Status = $"{s.Label} is now {KeyText(h.ToString())}.";
    }

    /// <summary>Names of macros whose start key is this combo.</summary>
    private List<string> MacrosUsing(Hotkey h)
    {
        var names = _rows.Where(r => r.Id != _macroId && Hotkey.TryParse(r.Hotkey, out var k) && k == h).Select(r => r.Name).ToList();
        if (Hotkey.TryParse(EditorHotkey, out var mine) && mine == h) names.Insert(0, MacroName);
        return names;
    }

    /// <summary>Message for a macro whose start key is an editor shortcut, or null.</summary>
    internal string? ShortcutClash(string? hotkey) =>
        Hotkey.TryParse(hotkey, out var h) && ShortcutFor(h) is { } s
            ? $"{KeyText(h.ToString())} is the editor's {s.Label} shortcut, so it won't start this macro. Pick another key, or change the shortcut in Settings › Macros."
            : null;

    /// <summary>Runs the shortcut's action on the blocks.</summary>
    public void RunShortcut(EditAction action)
    {
        switch (action)
        {
            case EditAction.Undo: Undo(); break;
            case EditAction.Redo: Redo(); break;
            case EditAction.Copy: CopyBlocks(); break;
            case EditAction.Cut: CutBlocks(); break;
            case EditAction.Paste: PasteBlocks(); break;
            case EditAction.Delete: DeleteSelected(); break;
        }
    }
}
