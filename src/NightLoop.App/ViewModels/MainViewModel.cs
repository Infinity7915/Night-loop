using System.Collections.ObjectModel;
using System.Media;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightLoop.App.Services;
using NightLoop.Client;
using NightLoop.Contracts;
using NightLoop.Contracts.Api;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Compiler;
using NightLoop.Engine.Hotkeys;
using NightLoop.Engine.Input;
using NightLoop.Engine.Runtime;
using NightLoop.Engine.Screen;
using NightLoop.Engine.Window;
using NightLoop.LocalData;

namespace NightLoop.App.ViewModels;

/// <summary>
/// The Macros screen (schema section 8). This file is the editor: the loaded macro, the block palette, the
/// timeline and the inspector, and Play. MainViewModel.Library.cs is the saved-macros list
/// with groups, and MainViewModel.Runs.cs runs several macros at once with their own hotkeys.
/// Macros live on the account: the first edit of a new macro creates it on the server (taking one of the 15 slots),
/// every later edit autosaves a draft, and Save makes it the saved copy.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public static readonly Hotkey PlayHotkey = new(HotkeyModifiers.None, 0x75);   // F6

    private static readonly FieldDef NoteField = F.Text("note", "Note (optional)", MacroLimits.CommentMaxLength,
        optional: true, mono: false, placeholder: "Shows under the block");

    private readonly RunManager _runs;
    private readonly Win32WindowService _windows = new();
    private readonly Win32ScreenCapture _screen = new();
    private readonly LocalSettings _settings;
    private readonly HotkeyManager _hotkeys = new();
    private bool _disposed;
    private IDisposable? _playKey;
    private IDisposable? _panicKey;
    private readonly ApiClient _api;
    private readonly TargetGamesViewModel _games;
    private MacroDoc _doc = new() { Name = "New macro" };
    private MacroRun? _run;
    private DraftAutosaver? _autosaver;
    private Guid? _macroId;
    private bool _loading;
    private bool _creating;
    /// <summary>Between switching to another macro and showing its blocks and name.</summary>
    private bool _opening;

    public MainViewModel(ApiClient api, TargetGamesViewModel games, LocalSettings? settings = null)
    {
        _api = api;
        _games = games;
        _settings = settings ?? new LocalSettings();
        _editorFollowsScroll = _settings.EditorFollowsScroll;
        _followPlayingBlock = _settings.FollowPlayingBlock;
        Waits = new BlockWaits { AfterClicksMs = _settings.WaitAfterClicksMs, AfterBlocksMs = _settings.WaitAfterBlocksMs };
        _games.TargetChanged += OnTargetChanged;
        _runs = new RunManager(new Win32InputSender(), _windows)
        {
            Screen = _screen,
            Text = WindowsTextReader.Create(), // starts Windows text recognition only when a macro first reads text
            IsKeyDown = KeyState.IsDown,
            SaveScreenshot = Screenshots.Save,
            Waits = Waits,
        };
        _runs.RunStepChanged += (run, index) => OnUi(() => { if (run == _run) Highlight(index); });
        _runs.RunStateChanged += (run, state) => OnUi(() =>
        {
            OnRunState(run, state);
            OnPropertyChanged(nameof(ActiveRunCount));
            OnPropertyChanged(nameof(StopAllText));
            OnPropertyChanged(nameof(IsBusy));
        });
        // The Notify block: a Windows notification from the tray icon; inside Night-loop only if there's no tray icon.
        _runs.Notify = message => OnUi(() =>
        {
            if (!Services.TrayIcon.Notify("Night-loop", message)) Status = message;
        });
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal, (_, _) => HideToast(), Dispatcher.CurrentDispatcher);
        _toastTimer.Stop();
        _runs.PlaySound = PlayWindowsSound;
        _runs.RunMacro = RunMacroByNameAsync;

        Categories = BlockCatalog.Categories
            .Select(c => new PaletteCategory(c.Name, c.Color, BlockCatalog.Palette.Count(d => d.Category == c.Name)))
            .ToList();

        _panicKey = TryRegister(Hotkey.Panic, () => OnUi(StopAll));
        RegisterEditorKeys();

        // A new macro starts with the starter sequence (when it's on in Settings).
        ApplyDoc(NewDoc(), starter: true);
    }

    /// <summary>The target game picked in Settings.</summary>
    public TargetGamesViewModel Games => _games;
    private GameTarget SelectedGame => _games.Target;
    /// <summary>The target game's program (the running one, for games with two), or null for N/A.</summary>
    private string? TargetExe => SelectedGame.Resolve(TargetGamesViewModel.IsRunning);

    public ObservableCollection<BlockRow> Blocks { get; } = [];
    public ObservableCollection<BlockField> InspectorFields { get; } = [];
    public ObservableCollection<InspectorTool> InspectorTools { get; } = [];
    public IReadOnlyList<PaletteCategory> Categories { get; }
    public ObservableCollection<BlockDef> PaletteItems { get; } = [];

    public string PanicHotkeyText => Hotkey.Panic.ToString();

    /// <summary>The loaded macro's own start key, or F6 (the editor's Play key) when it has none. Shown as "Ctrl + F5".</summary>
    public string PlayHotkeyText => KeyText(string.IsNullOrEmpty(EditorHotkey) ? PlayHotkey.ToString() : EditorHotkey);

    /// <summary>"Ctrl+F5" as the mockup shows it: "Ctrl + F5".</summary>
    public static string KeyText(string hotkey) => hotkey.Replace("+", " + ", StringComparison.Ordinal);

    [ObservableProperty] private string _macroName = "New macro";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayText))]
    private bool _isPlaying;
    /// <summary>The loaded macro is paused at a breakpoint; Play reads Continue.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayText))]
    private bool _isAtBreakpoint;
    /// <summary>Settings › Macros › Block editor follows scrolling. The view reads it on every scroll.</summary>
    [ObservableProperty] private bool _editorFollowsScroll = true;
    /// <summary>Settings › Macros › Follow the playing block. The view scrolls to each block as it plays.</summary>
    [ObservableProperty] private bool _followPlayingBlock = true;
    [ObservableProperty] private int _repeatCount = 1;
    [ObservableProperty] private bool _repeatForever;
    [ObservableProperty] private bool _humanize;
    [ObservableProperty] private int _timingJitterPct = 15;
    [ObservableProperty] private int _mouseJitterPx = 3;
    [ObservableProperty] private int _speedPct = 100;
    /// <summary>The latest message. It shows as a toast at the bottom of the page for a few seconds.</summary>
    [ObservableProperty] private string _status = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncKind), nameof(SaveText))]
    private string _syncText = "Not on your account yet";

    /// <summary>Colors the sync pill: saved (green), draft (amber), saving (accent), muted or error.</summary>
    public string SyncKind => SyncText switch
    {
        "Saved to account" => "saved",
        "Draft · autosaved" => "draft",
        "Saving…" => "saving",
        "Not on your account yet" => "muted",
        _ => "error",
    };

    /// <summary>The inspector's save button.</summary>
    public string SaveText => SyncText switch { "Saved to account" => "Saved to account", "Saving…" => "Saving…", _ => "Save to account" };
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(PlayText), nameof(PlayToolTip), nameof(InspectorTitle), nameof(InspectorDescription), nameof(InspectorEyebrow), nameof(SelectedNumber))]
    private BlockRow? _selectedBlock;
    [ObservableProperty] private PaletteCategory? _openCategory;

    // Loaded macro options (gear button): start key, trigger mode, run alongside.
    [ObservableProperty] private bool _showEditorOptions;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayHotkeyText), nameof(EditorHotkeyLabel), nameof(EditorHasHotkey))]
    private string _editorHotkey = "";
    [ObservableProperty] private string _editorTriggerMode = TriggerModes.Toggle;
    [ObservableProperty] private bool _editorRunAlongside = true;
    [ObservableProperty] private bool _isCapturingEditorHotkey;
    [ObservableProperty] private string _editorHotkeyError = "";

    /// <summary>The loaded macro has a start / stop key (the None button is only useful then).</summary>
    public bool EditorHasHotkey => !string.IsNullOrEmpty(EditorHotkey);

    public string EditorHotkeyLabel => IsCapturingEditorHotkey
        ? "Press a key combo… (Esc cancels)"
        : string.IsNullOrEmpty(EditorHotkey) ? "None  ·  Change" : $"{KeyText(EditorHotkey)}  ·  Change";

    /// <summary>Waiting for a key press to use as a start/stop key (the app's own keys are let go meanwhile).</summary>
    public bool IsCapturingKey => IsCapturingEditorHotkey || CapturingRow is not null || CapturingShortcut is not null;

    partial void OnIsCapturingEditorHotkeyChanged(bool value)
    {
        OnPropertyChanged(nameof(EditorHotkeyLabel));
        OnCaptureChanged();
    }

    public bool HasSelection => SelectedBlock is not null;
    public string SelectedNumber => SelectedBlock?.Number ?? "—";
    public string InspectorEyebrow => $"EDIT BLOCK {SelectedNumber}";
    public string InspectorTitle => SelectedBlock?.Title ?? "Nothing selected";
    public string InspectorDescription => SelectedBlock is null
        ? "Pick a block on the left to edit it."
        : BlockCatalog.For(SelectedBlock.Block.Type)?.Description ?? "";

    /// <summary>Play starts at the highlighted block: "Play from 4". With the first block (or none) highlighted it's "Play".</summary>
    public string PlayText => IsAtBreakpoint ? "Continue" : IsPlaying ? "Stop" : SelectedBlock is { Index: > 0 } sel ? $"Play from {sel.Index + 1}" : "Play";

    public string PlayToolTip => SelectedBlock is { Index: > 0 }
        ? $"Play from block {SelectedBlock.Index + 1}, the highlighted one. Press Esc on the timeline to play from the top."
        : "Play the macro from the top.";

    /// <summary>The loaded macro's run state pill: "Running" or "Off".</summary>
    public string RunStateText => IsPlaying ? "Running" : "Off";
    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(RunStateText));

    /// <summary>Macros running right now (Home's "Active macros" card).</summary>
    public int ActiveRunCount => _runs.ActiveRuns.Count;
    public string StopAllText => $"Stop all · {ActiveRunCount} running";

    /// <summary>Running: the app must not restart for an update by itself.</summary>
    public bool IsBusy => ActiveRunCount > 0;

    public string BlockSummary => $"{Blocks.Count} blocks · {Blocks.Count(b => !b.Enabled)} off";

    /// <summary>Blocks ticked on the timeline. Kept by block, so they stay ticked when the rows are rebuilt.</summary>
    private readonly HashSet<Block> _checked = new(ReferenceEqualityComparer.Instance);
    /// <summary>The last block ticked without Shift: Shift+click ticks everything from it to the clicked one.</summary>
    private Block? _checkAnchor;

    public int CheckedCount => _checked.Count;
    public bool HasChecked => _checked.Count > 0;
    public string CheckedText => _checked.Count == 1 ? "1 selected" : $"{_checked.Count} selected";

    /// <summary>Set by the shell: opens Settings, where the target game is picked.</summary>
    public Action? OpenSettings { get; set; }
    /// <summary>Blocks and macros left (today and on the account). Null in previews and tests: then only the server checks.</summary>
    public LimitsViewModel? Limits { get; set; }

    /// <summary>Checks the daily and account block limits before placing blocks; says why not in the toast.</summary>
    private bool CanPlace(int count)
    {
        if (Limits is null || Limits.CanPlace(count, out var why)) return true;
        Status = why;
        return false;
    }

    private void OnTargetChanged(GameTarget value)
    {
        if (_loading) return;
        // An untouched starter macro is rebuilt for the new game (window blocks come and go) instead of saved.
        if (_pristine && _macroId is null)
        {
            ApplyDoc(NewDoc(), starter: true);
            return;
        }
        MarkChanged();
    }

    // Every editor field that ends up in the document autosaves.
    partial void OnMacroNameChanged(string value) => MarkChanged();
    partial void OnRepeatCountChanged(int value) => MarkChanged();
    partial void OnRepeatForeverChanged(bool value) => MarkChanged();
    partial void OnHumanizeChanged(bool value) => MarkChanged();
    partial void OnTimingJitterPctChanged(int value) => MarkChanged();
    partial void OnMouseJitterPxChanged(int value) => MarkChanged();
    partial void OnSpeedPctChanged(int value) => MarkChanged();
    partial void OnEditorHotkeyChanged(string value)
    {
        // A macro already on the account saves its key straight away (SaveEditorHotkeyAsync), into both the saved
        // copy and the draft, so changing only the key doesn't leave unsaved changes behind.
        if (_macroId is not null && !_loading)
        {
            RefreshHotkeys();
            return;
        }
        OnEditorOptionChanged();
    }
    partial void OnEditorTriggerModeChanged(string value) => OnEditorOptionChanged();
    partial void OnEditorRunAlongsideChanged(bool value) => OnEditorOptionChanged();

    private void OnEditorOptionChanged()
    {
        if (_loading) return;
        MarkChanged();
        RefreshHotkeys();
    }

    partial void OnSelectedBlockChanged(BlockRow? oldValue, BlockRow? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        // Highlighting a block inside a folded one (a fault, an undo) unfolds it, so you can see what's highlighted.
        if (newValue is { IsHidden: true }) Unfold(newValue.Index);
        BuildInspector();
    }

    [RelayCommand]
    private void ChangeTarget() => OpenSettings?.Invoke();

    [RelayCommand]
    private void ToggleEditorOptions() => ShowEditorOptions = !ShowEditorOptions;

    // ---------------- Palette ----------------

    /// <summary>Opens a category's blocks; clicking the open category again hides them (schema section 8).</summary>
    [RelayCommand]
    private void ToggleCategory(PaletteCategory category)
    {
        var open = OpenCategory == category ? null : category;
        foreach (var c in Categories) c.IsOpen = c == open;
        OpenCategory = open;
        PaletteItems.Clear();
        if (open is null) return;
        foreach (var d in BlockCatalog.Palette.Where(d => d.Category == open.Name)) PaletteItems.Add(d);
    }

    /// <summary>Adds a block after the selected one (or at the end). Openers get their End added too.</summary>
    [RelayCommand]
    private void AddBlock(BlockDef def)
    {
        if (IsPlaying) return;
        var placing = BlockTypes.IsOpener(def.Type) ? 2 : 1;
        if (!CanPlace(placing)) return;
        var at = InsertAt;
        var block = Block.Create(NextId(), def.Type, def.Defaults());
        ApplyTargetDefaults(block);
        if (block.Type == BlockTypes.WindowOpen)
        {
            // Open window is only useful with a program, so File Explorer opens straight away.
            if (Programs.Browse(null, anyFile: true) is not { } file) return;
            block.Params["path"] = Programs.Portable(file);
        }
        if (block.Type == BlockTypes.RunMacro && LibraryNames().FirstOrDefault(n => n != MacroName) is { } other)
            block.Params["macroName"] = other;
        _doc.Blocks.Insert(at, block);
        if (BlockTypes.IsOpener(def.Type))
            _doc.Blocks.Insert(at + 1, Block.Create(NextId(), BlockTypes.End));
        ShowBlocks(selectIndex: at);
        MarkChanged();
        Limits?.Placed(placing);
        if (BlockTypes.NeedsVision(block) && _runs.Text is null && block.Type is BlockTypes.WaitForText or BlockTypes.FindClick or BlockTypes.ScanTurn)
            Status = "Reading text needs Windows text recognition. Add a language with text recognition in Windows Settings › Time & language › Language.";
    }

    /// <summary>Turning the camera depends on the game: Roblox turns with a right-drag, most others with the mouse.</summary>
    private void ApplyTargetDefaults(Block block)
    {
        if (block.Type is not (BlockTypes.CameraTurn or BlockTypes.ScanTurn)) return;
        var roblox = SelectedGame.Exe?.Contains("Roblox", StringComparison.OrdinalIgnoreCase) == true;
        block.Params["method"] = SelectedGame.Exe is null || roblox ? "rightDrag" : "mouseMove";
    }

    // ---------------- Timeline row actions ----------------

    [RelayCommand]
    private void SelectBlock(BlockRow row) => SelectedBlock = row;

    /// <summary>Duplicates a block. On a Loop, If or Repeat until (or its Else / End) it copies the whole section.</summary>
    [RelayCommand]
    private void DuplicateBlock(BlockRow row)
    {
        if (IsPlaying) return;
        var (from, to) = SectionOf(row.Index);
        var count = to - from + 1;
        if (!CanPlace(count)) return;
        var copies = MacroJson.Deserialize(MacroJson.Serialize(new MacroDoc { Name = "x", Blocks = _doc.Blocks.GetRange(from, count) })).Blocks;
        for (var i = 0; i < copies.Count; i++)
        {
            copies[i].Id = NextId();
            _doc.Blocks.Insert(to + 1 + i, copies[i]);
        }
        ShowBlocks(selectIndex: to + 1);
        MarkChanged();
        Limits?.Placed(count);
        if (count > 1) Status = $"Copied the whole {BlockCatalog.For(_doc.Blocks[from].Type)?.Label ?? "section"} ({count} blocks).";
    }

    /// <summary>The section a block belongs to: an opener (or its Else / End) spans opener..End, anything else is itself.</summary>
    private (int From, int To) SectionOf(int index)
    {
        var b = _doc.Blocks[index];
        if (!(BlockTypes.IsOpener(b.Type) || b.Type is BlockTypes.Else or BlockTypes.End)) return (index, index);
        var structure = MacroStructure.Analyze(_doc.Blocks);
        var partner = structure.Partner[index];
        if (partner < 0) return (index, index);
        if (b.Type == BlockTypes.Else) partner = structure.Partner[partner]; // else → end → opener
        return (Math.Min(index, partner), Math.Max(index, partner));
    }

    [RelayCommand]
    private void MoveBlockUp(BlockRow row) => Move(row, -1);

    [RelayCommand]
    private void MoveBlockDown(BlockRow row) => Move(row, +1);

    /// <summary>
    /// Swaps a row with the one above (or below). A folded block moves with everything inside it, and moving past a
    /// folded block jumps over the whole of it, so nothing slips in or out of a section you can't see.
    /// </summary>
    private void Move(BlockRow row, int by)
    {
        if (IsPlaying) return;
        var (from, to) = ShownUnit(row.Index);
        var (nFrom, nTo) = by < 0 ? ShownUnit(from - 1) : ShownUnit(to + 1);
        if (nFrom < 0 || nTo >= _doc.Blocks.Count) return;
        var blocks = _doc.Blocks;
        var moving = blocks.GetRange(from, to - from + 1);
        var first = Math.Min(from, nFrom);
        blocks.RemoveRange(from, moving.Count);
        // Below the moved row the neighbor shifted up by its size; above, it stays where it was.
        var insertAt = by < 0 ? nFrom : nTo - moving.Count + 1;
        blocks.InsertRange(insertAt, moving);
        ShowBlocks(selectIndex: by < 0 ? first : insertAt);
        MarkChanged();
    }

    /// <summary>
    /// Deletes a block. Deleting a Loop, If or Repeat until (or its End) also deletes its End (or opener) and Else,
    /// and keeps the blocks that were inside, so the macro stays runnable.
    /// </summary>
    [RelayCommand]
    private void DeleteBlock(BlockRow row)
    {
        if (IsPlaying) return;
        var index = row.Index;
        var remove = new SortedSet<int> { index };
        var type = _doc.Blocks[index].Type;
        if (BlockTypes.IsOpener(type) || type == BlockTypes.End)
        {
            var structure = MacroStructure.Analyze(_doc.Blocks);
            var partner = structure.Partner[index];
            if (partner >= 0)
            {
                remove.Add(partner);
                var opener = Math.Min(index, partner);
                if (structure.ElseOf[opener] is >= 0 and var e) remove.Add(e);
            }
        }
        foreach (var i in remove.Reverse()) _doc.Blocks.RemoveAt(i);
        ShowBlocks(selectIndex: _doc.Blocks.Count == 0 ? null : Math.Min(remove.Min, _doc.Blocks.Count - 1));
        MarkChanged();
        if (remove.Count > 1) Status = "Deleted it with its End. The blocks inside are kept.";
    }

    [RelayCommand]
    private void ToggleBreakpoint(BlockRow row) => row.Breakpoint = !row.Breakpoint;

    /// <summary>Esc on the timeline: nothing highlighted, so Play starts at the top and Add block adds at the end.</summary>
    public void ClearSelection() => SelectedBlock = null;

    /// <summary>The Delete key: the ticked blocks when some are ticked, else the highlighted one.</summary>
    public void DeleteSelected()
    {
        if (HasChecked) DeleteChecked();
        else if (SelectedBlock is { } row) DeleteBlock(row);
    }

    // ---------------- Ticked blocks (move and delete several at once) ----------------

    /// <summary>
    /// Ticks or unticks a row. With Shift, ticks every row from the last one ticked to this one (both included),
    /// so two clicks select a whole run of blocks.
    /// </summary>
    public void CheckRow(BlockRow row, bool shift)
    {
        if (shift && _checkAnchor is { } anchor && Blocks.FirstOrDefault(r => ReferenceEquals(r.Block, anchor)) is { } from)
        {
            var (lo, hi) = (Math.Min(from.Index, row.Index), Math.Max(from.Index, row.Index));
            for (var i = lo; i <= hi; i++) SetChecked(Blocks[i], true);
        }
        else
        {
            SetChecked(row, !row.IsChecked);
            _checkAnchor = row.IsChecked ? row.Block : null;
        }
        NotifyChecked();
    }

    [RelayCommand]
    private void CheckAll()
    {
        foreach (var row in Blocks) SetChecked(row, true);
        NotifyChecked();
    }

    /// <summary>True when every block on the timeline is ticked, so the Select all button unticks them instead.</summary>
    public bool AllChecked => Blocks.Count > 0 && Blocks.All(r => r.IsChecked);

    public string CheckAllToolTip => AllChecked ? "Deselect all blocks (Esc)" : "Select all blocks (Ctrl+A)";

    /// <summary>The one Select all button: ticks every block, or unticks them all when they are already ticked.</summary>
    [RelayCommand]
    private void ToggleCheckAll()
    {
        if (AllChecked) ClearChecked();
        else CheckAll();
    }

    [RelayCommand]
    public void ClearChecked()
    {
        foreach (var row in Blocks) row.IsChecked = false;
        _checked.Clear();
        _checkAnchor = null;
        NotifyChecked();
    }

    private void SetChecked(BlockRow row, bool on)
    {
        row.IsChecked = on;
        if (on) _checked.Add(row.Block);
        else _checked.Remove(row.Block);
    }

    private void NotifyChecked()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(HasChecked));
        OnPropertyChanged(nameof(CheckedText));
        OnPropertyChanged(nameof(AllChecked));
        OnPropertyChanged(nameof(CheckAllToolTip));
    }

    /// <summary>
    /// The ticked blocks as indexes. A ticked Loop, If or Repeat until (or its Else / End) brings its whole section,
    /// so moving it keeps its blocks inside and the macro stays runnable.
    /// </summary>
    private SortedSet<int> CheckedSections()
    {
        var set = new SortedSet<int>();
        for (var i = 0; i < _doc.Blocks.Count; i++)
        {
            if (!_checked.Contains(_doc.Blocks[i])) continue;
            var (from, to) = SectionOf(i);
            for (var k = from; k <= to; k++) set.Add(k);
        }
        return set;
    }

    [RelayCommand]
    private void MoveCheckedUp() => MoveChecked(-1);

    [RelayCommand]
    private void MoveCheckedDown() => MoveChecked(+1);

    /// <summary>
    /// Moves the ticked blocks one step. Each run of ticked blocks swaps places with the block just above (or below)
    /// it, so a gap between ticked blocks stays, and a run at the top (or bottom) stays put.
    /// </summary>
    private void MoveChecked(int by)
    {
        if (IsPlaying || !HasChecked) return;
        var picked = CheckedSections();
        var blocks = _doc.Blocks;
        var moved = false;
        if (by < 0)
        {
            for (var i = 1; i < blocks.Count; i++)
            {
                if (!picked.Contains(i) || picked.Contains(i - 1)) continue;
                var end = i;
                while (end + 1 < blocks.Count && picked.Contains(end + 1)) end++;
                // The row above, or all of a folded block above.
                var (aFrom, aTo) = ShownUnit(i - 1);
                if (aTo != i - 1 || picked.GetViewBetween(aFrom, aTo).Count > 0) continue;
                var n = aTo - aFrom + 1;
                var above = blocks.GetRange(aFrom, n);
                blocks.RemoveRange(aFrom, n);
                blocks.InsertRange(end - n + 1, above);
                for (var k = i; k <= end; k++) { picked.Remove(k); picked.Add(k - n); }
                moved = true;
                i = end;
            }
        }
        else
        {
            for (var i = blocks.Count - 2; i >= 0; i--)
            {
                if (!picked.Contains(i) || picked.Contains(i + 1)) continue;
                var start = i;
                while (start - 1 >= 0 && picked.Contains(start - 1)) start--;
                // The row below, or all of a folded block below.
                var (bFrom, bTo) = ShownUnit(i + 1);
                if (bFrom != i + 1 || picked.GetViewBetween(bFrom, bTo).Count > 0) continue;
                var n = bTo - bFrom + 1;
                var below = blocks.GetRange(bFrom, n);
                blocks.RemoveRange(bFrom, n);
                blocks.InsertRange(start, below);
                for (var k = i; k >= start; k--) { picked.Remove(k); picked.Add(k + n); }
                moved = true;
                i = start;
            }
        }
        if (!moved)
        {
            Status = by < 0 ? "The selected blocks are already at the top." : "The selected blocks are already at the bottom.";
            return;
        }
        var keep = SelectedBlock?.Block;
        ShowBlocks(selectIndex: keep is null ? null : _doc.Blocks.IndexOf(keep));
        MarkChanged();
    }

    /// <summary>
    /// Deletes the ticked blocks. A ticked Loop, If or Repeat until (or its End) also takes its End (or opener) and
    /// Else, like deleting one; blocks inside that aren't ticked are kept.
    /// </summary>
    [RelayCommand]
    private void DeleteChecked()
    {
        if (IsPlaying || !HasChecked) return;
        var structure = MacroStructure.Analyze(_doc.Blocks);
        var remove = new SortedSet<int>();
        for (var i = 0; i < _doc.Blocks.Count; i++)
        {
            if (!_checked.Contains(_doc.Blocks[i])) continue;
            remove.Add(i);
            var type = _doc.Blocks[i].Type;
            if (!(BlockTypes.IsOpener(type) || type == BlockTypes.End) || structure.Partner[i] is not (>= 0 and var partner)) continue;
            remove.Add(partner);
            if (structure.ElseOf[Math.Min(i, partner)] is >= 0 and var e) remove.Add(e);
        }
        var first = remove.Min;
        foreach (var i in remove.Reverse()) _doc.Blocks.RemoveAt(i);
        _checked.Clear();
        _checkAnchor = null;
        ShowBlocks(selectIndex: _doc.Blocks.Count == 0 ? null : Math.Min(first, _doc.Blocks.Count - 1));
        MarkChanged();
        Status = remove.Count == 1 ? "Deleted 1 block." : $"Deleted {remove.Count} blocks.";
    }

    private void OnRowChanged(BlockRow row)
    {
        if (_loading) return;
        OnPropertyChanged(nameof(BlockSummary));
        OnPropertyChanged(nameof(InspectorTitle));
        MarkChanged();
    }

    private void OnFieldChanged(BlockRow row, BlockField field)
    {
        // Typing into one field is one change to undo, not one per key.
        _undoGroup = $"{row.Block.Id}:{field.Def.Key}";
        try { OnRowChanged(row); }
        finally { _undoGroup = null; }
        // Typed click spot: move the dot on the picture preview to match.
        if (field.Def.Key is BlockTypes.ClickXKey or BlockTypes.ClickYKey) ShowImagePreview(row.Block);
        // A choice can show or hide other fields and tools (On fail → Go to label, Find → Image…).
        if (field.RebuildsInspector)
            Application.Current?.Dispatcher.BeginInvoke(() => { if (SelectedBlock == row) BuildInspector(); }, DispatcherPriority.Background);
    }

    /// <summary>Settings › Macros' extra waits, shared with every run (they read it live).</summary>
    public BlockWaits Waits { get; }

    /// <summary>The Settings wait sliders moved: runs use the new values right away, and the open block shows them.</summary>
    public void SetWaits(int afterClicksMs, int afterBlocksMs)
    {
        Waits.AfterClicksMs = afterClicksMs;
        Waits.AfterBlocksMs = afterBlocksMs;
        foreach (var field in InspectorFields) field.RefreshSlider();
    }

    private void BuildInspector()
    {
        InspectorFields.Clear();
        InspectorTools.Clear();
        ShowImagePreview(SelectedBlock?.Block);
        if (SelectedBlock is not { } row) return;
        var def = BlockCatalog.For(row.Block.Type);
        var names = LibraryNames();
        var labels = _doc.Blocks.Where(b => b.Type == BlockTypes.Label).Select(b => b.GetString("name") ?? "").Where(n => n.Length > 0).ToList();
        foreach (var f in def?.Fields ?? [])
            if (f.ShowIf?.Invoke(row.Block) != false) InspectorFields.Add(new BlockField(row, f, OnFieldChanged, names, labels, () => Waits.AfterClicksMs));
        foreach (var tool in ToolsFor(row.Block)) InspectorTools.Add(tool);
        if (row.Block.Type is not (BlockTypes.Comment or BlockTypes.Description))
            InspectorFields.Add(new BlockField(row, NoteField, OnFieldChanged));
    }

    private string NextId()
    {
        var used = new HashSet<string>(_doc.Blocks.Select(b => b.Id), StringComparer.Ordinal);
        for (var i = _doc.Blocks.Count + 1; ; i++)
            if (!used.Contains($"b{i}")) return $"b{i}";
    }

    // ---------------- Play ----------------

    /// <summary>" inside the If" (or Loop, ...) when a block put at this index lands inside one, else "".</summary>
    private string InsideText(int index)
    {
        var structure = MacroStructure.Analyze(_doc.Blocks);
        for (var i = Math.Min(index, _doc.Blocks.Count) - 1; i >= 0; i--)
        {
            var b = _doc.Blocks[i];
            if (BlockTypes.IsOpener(b.Type) && structure.Partner[i] is var end && (end < 0 || end >= index))
                return $" inside the {BlockCatalog.For(b.Type)?.Label ?? b.Type} (block {i + 1:00})";
        }
        return "";
    }

    /// <summary>Asks the view to scroll a timeline row into sight.</summary>
    public event Action<int>? RevealRequested;

    /// <summary>
    /// Play, or Stop while playing, or Continue at a breakpoint. Starts at the highlighted block (block 01 when none
    /// is), holding any key a "down" block before it left held.
    /// </summary>
    [RelayCommand]
    private void TogglePlay()
    {
        if (_run is { IsActive: true } running)
        {
            if (IsAtBreakpoint) running.Continue();
            else running.Stop();
            return;
        }
        StartEditorRun(new RunSettings { TargetExe = TargetExe, Debug = true, StartIndex = SelectedBlock?.Index ?? 0 });
    }

    /// <summary>
    /// Pressing Play in Night-loop leaves Night-loop in front, where the first keys would land. Put the
    /// target game in front instead, or with no target, the window that was in front before Night-loop.
    /// </summary>
    private void StepAside()
    {
        if (Application.Current?.MainWindow is not { IsActive: true } window) return;
        if (TargetExe is { } exe)
        {
            if (_windows.IsProcessRunning(exe)) _windows.Focus(exe);
            return;
        }
        WindowSwitcher.ActivateWindowBehind(window);
    }

    private void StartEditorRun(RunSettings settings)
    {
        SyncDoc();
        foreach (var r in Blocks) r.IsFault = false;
        CompiledMacro compiled;
        try
        {
            compiled = MacroCompiler.Compile(_doc);
        }
        catch (MacroCompileException ex)
        {
            var issue = ex.Issues.Count > 0 ? ex.Issues[0] : null;
            Status = issue?.Message ?? ex.Message;
            if (issue?.BlockId is { } id && Blocks.FirstOrDefault(b => b.Block.Id == id) is { } bad)
            {
                bad.IsFault = true;
                SelectedBlock = bad;
            }
            return;
        }

        try
        {
            _run = _runs.Start(compiled, settings);
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
            return;
        }
        if (_macroId is { } macroId) TrackRun(macroId, _run);
        IsPlaying = true;
        IsAtBreakpoint = false;
        StepAside();
        Status = settings.StartIndex > 0
            ? $"Playing from block {settings.StartIndex + 1:00}… press {PlayHotkeyText} to stop, {KeyText(Hotkey.Panic.ToString())} stops everything."
            : $"Playing… press {PlayHotkeyText} to stop, {KeyText(Hotkey.Panic.ToString())} stops everything.";
    }

    [RelayCommand]
    private void StopAll()
    {
        _ = _runs.StopAllAsync();
    }

    // ---------------- New / Save / Delete ----------------

    /// <summary>
    /// Starts a new macro (with the starter sequence when it's on). It reaches the account, and takes one of the
    /// slots, on its first edit.
    /// </summary>
    [RelayCommand]
    private async Task NewMacroAsync()
    {
        if (IsPlaying) return;
        if (_rows.Count >= _limit)
        {
            ShowLimit = true;
            return;
        }
        if (Limits is { MacrosLeft: 0, Usage: { } usage })
        {
            Status = $"You've made {usage.NewMacrosPerDay} new macros today, the most for one day. More at {usage.ResetsAt.ToLocalTime():t}.";
            return;
        }
        await CloseCurrentAsync();
        ApplyDoc(NewDoc(), starter: true);
        SyncText = "Not on your account yet";
        RefreshRows();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        SyncDoc();
        if (_autosaver is null)
        {
            await CreateOnServerAsync();
            if (_autosaver is null) return;
        }
        try
        {
            await _autosaver.SaveAsync(_doc);
            Status = $"Saved \"{_doc.Name}\" to your account.";
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException or InvalidOperationException)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (IsPlaying) return;
        if (_macroId is not { } id)
        {
            ApplyDoc(NewDoc(), starter: false);
            return;
        }
        await DeleteMacroAsync(id, MacroName);
    }

    /// <summary>Sends any unsaved edit now (before signing out or closing).</summary>
    public async Task FlushAsync()
    {
        if (_autosaver is null) return;
        try
        {
            await _autosaver.FlushAsync();
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException or OperationCanceledException)
        {
            // Best effort; the draft on the server is at most one debounce behind.
        }
    }

    /// <summary>The server has been unreachable for 5 minutes: macros stop until it answers again.</summary>
    public void StopForOffline()
    {
        StopAll();
        Status = "Lost contact with the server for 5 minutes, so macros were stopped.";
    }

    // ---------------- Editor hotkey capture ----------------

    [RelayCommand]
    private void CaptureEditorHotkey()
    {
        EditorHotkeyError = "";
        IsCapturingEditorHotkey = true;
    }

    /// <summary>The None button: no start / stop key, so the macro only runs from Play (or Run macro).</summary>
    [RelayCommand]
    private void ClearEditorHotkey() => OnEditorHotkeyCaptured(null, clear: true);

    /// <summary>Called by the view with the key pressed while capturing. Escape cancels, Backspace/Delete clears.</summary>
    public void OnEditorHotkeyCaptured(Hotkey? hotkey, bool clear)
    {
        IsCapturingEditorHotkey = false;
        if (clear)
        {
            EditorHotkey = "";
            _ = SaveEditorHotkeyAsync();
            return;
        }
        if (hotkey is not { } h) return;
        if (HotkeyProblem(h, _macroId) is { } problem)
        {
            EditorHotkeyError = problem;
            return;
        }
        EditorHotkeyError = "";
        EditorHotkey = h.ToString();
        _ = SaveEditorHotkeyAsync();
    }

    /// <summary>
    /// A new start key is saved to the account straight away (into the saved copy too), so there's no need to
    /// press Save just for the key. A macro that isn't on the account yet gets it when it's created.
    /// </summary>
    private async Task SaveEditorHotkeyAsync()
    {
        if (_macroId is not { } id) return;
        var key = EditorHotkey;
        try
        {
            await FlushAsync();
            var updated = await _api.UpdateMacroAsync(id, new UpdateMacroRequest(HotkeyStart: key, HotkeyStop: key));
            if (_macroId == id) _autosaver?.Adopt(updated);
            Status = key.Length == 0 ? "Start key removed and saved." : $"Start key {KeyText(key)} saved.";
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            Status = $"Couldn't save the start key: {ex.Message}";
        }
    }

    // ---------------- Plumbing ----------------

    private async Task OpenAsync(Guid id)
    {
        if (IsPlaying)
        {
            Status = "Stop playing before opening another macro.";
            return;
        }
        try
        {
            await CloseCurrentAsync();
            var macro = await _api.GetMacroAsync(id);
            _opening = true;
            try
            {
                Attach(macro);
                ApplyDoc(macro.Current ?? NewDoc(), starter: false);
            }
            finally
            {
                _opening = false;
            }
            RefreshRows();
            Status = macro.DraftDoc is null ? $"Opened \"{macro.Name}\"." : $"Opened \"{macro.Name}\" with unsaved changes.";
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            Status = ex.Message;
        }
    }

    /// <summary>First edit of a macro that isn't on the account yet: create it there as a draft.</summary>
    private async Task CreateOnServerAsync()
    {
        if (_macroId is not null || _creating) return;
        _creating = true;
        try
        {
            SyncDoc();
            var macro = await _api.CreateMacroAsync(_doc.Name, _doc);
            Attach(macro);
            await RefreshAsync();
            // Edits made while the create was in flight.
            SyncDoc();
            _autosaver?.Changed(_doc);
        }
        catch (ApiException ex) when (ex.Code == ErrorCodes.MacroLimit)
        {
            SyncText = "Not saved: account full";
            Status = ex.Message;
        }
        catch (ApiException ex) when (ex.Code == ErrorCodes.BlockLimit)
        {
            SyncText = "Not saved: no blocks left";
            Status = ex.Message;
            _ = Limits?.RefreshAsync();
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            SyncText = "Not saved";
            Status = ex.Message;
        }
        finally
        {
            _creating = false;
        }
    }

    private void Attach(MacroDto macro)
    {
        _macroId = macro.Id;
        var saver = new DraftAutosaver(_api, macro);
        _autosaver = saver;
        // A closed macro's saver can still report (its last save finishing): ignore it, or it would mark the newly
        // opened macro's row with the old macro's name.
        saver.StateChanged += state => OnUi(() =>
        {
            if (ReferenceEquals(saver, _autosaver)) OnSyncState(state);
        });
        // The counts are the account's, so even a closed macro's last save brings fresh ones.
        saver.UsageReported += usage => OnUi(() => Limits?.Apply(usage));
        OnSyncState(saver.State);
    }

    private async Task CloseCurrentAsync()
    {
        await FlushAsync();
        await DisposeAutosaverAsync();
        _macroId = null;
    }

    private async Task DisposeAutosaverAsync()
    {
        if (_autosaver is null) return;
        var saver = _autosaver;
        _autosaver = null;
        await saver.DisposeAsync();
    }

    private void MarkChanged()
    {
        if (_loading) return;
        RememberForUndo();
        _pristine = false;
        if (_autosaver is not null)
        {
            SyncDoc();
            _autosaver.Changed(_doc);
        }
        else if (_doc.Blocks.Count > 0)
        {
            _ = CreateOnServerAsync();
        }
    }

    private void OnSyncState(SyncState state)
    {
        SyncText = state switch
        {
            SyncState.Saved => "Saved to account",
            SyncState.Pending or SyncState.Saving => "Saving…",
            SyncState.Draft => "Draft · autosaved",
            SyncState.Offline => "Offline — changes not saved",
            SyncState.Conflict => "Changed on another PC",
            SyncState.Error => "Not saved: " + _autosaver?.Message,
            _ => "",
        };
        if (state == SyncState.Conflict) ResolveConflict();
        if (state == SyncState.Error && _autosaver?.Message is { Length: > 0 } why)
        {
            Status = why;
            _ = Limits?.RefreshAsync();
        }
        if (state is SyncState.Saved or SyncState.Draft) MarkEditorRow(state == SyncState.Draft);
    }

    /// <summary>Edited from another place: offer Keep mine or Load server copy (schema section 6).</summary>
    private void ResolveConflict()
    {
        if (_autosaver is null) return;
        var answer = MessageBox.Show(
            "This macro was changed on another PC since you opened it.\n\nYes: keep your version (overwrites the other one).\nNo: load the server copy (drops your recent changes).",
            "Night-loop", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            _autosaver.KeepMine();
            return;
        }
        var copy = _autosaver.LoadServerCopy();
        ApplyDoc(copy.Current ?? NewDoc(), starter: false);
        Status = "Loaded the server copy.";
    }

    /// <summary>
    /// Shows a document in the editor without triggering autosave. <paramref name="starter"/>: a new macro, which
    /// gets the starter sequence when it's on in Settings.
    /// </summary>
    private void ApplyDoc(MacroDoc doc, bool starter = false)
    {
        _loading = true;
        int? select = null;
        try
        {
            if (starter && _settings.StarterSequence) select = AddStarterBlocks(doc);
            _pristine = starter;
            _doc = doc;
            MacroName = doc.Name;
            RepeatForever = doc.Options.Repeat.Mode == RepeatModes.Forever;
            RepeatCount = doc.Options.Repeat.Count ?? 1;
            Humanize = doc.Options.Humanize.Enabled;
            TimingJitterPct = doc.Options.Humanize.TimingJitterPct;
            MouseJitterPx = doc.Options.Humanize.MousePx;
            SpeedPct = doc.Options.SpeedPct is >= MacroLimits.SpeedMinPct and <= MacroLimits.SpeedMaxPct ? doc.Options.SpeedPct : 100;
            EditorHotkey = doc.Options.HotkeyStart ?? "";
            EditorTriggerMode = doc.Options.TriggerMode;
            EditorRunAlongside = doc.Options.RunAlongside;
            EditorHotkeyError = "";
            // A macro remembers the game it was made for; opening it switches the target to that game.
            if (doc.TargetGame is not null) _games.SelectByName(doc.TargetGame);
            ResetCollapse();
            ShowBlocks(selectIndex: select);
            ResetUndo();
        }
        finally
        {
            _loading = false;
        }
        RefreshHotkeys();
    }

    private void SyncDoc()
    {
        _doc.Name = string.IsNullOrWhiteSpace(MacroName) ? "New macro" : MacroName.Trim();
        _doc.TargetGame = SelectedGame.Exe is null ? null : SelectedGame.Name;
        _doc.Options.Repeat = RepeatForever
            ? new RepeatOptions { Mode = RepeatModes.Forever, Count = null }
            : new RepeatOptions { Mode = RepeatModes.Count, Count = Math.Clamp(RepeatCount, MacroLimits.CountMin, MacroLimits.CountMax) };
        _doc.Options.Humanize = new HumanizeOptions
        {
            Enabled = Humanize,
            TimingJitterPct = Math.Clamp(TimingJitterPct, 0, MacroLimits.TimingJitterMaxPct),
            MousePx = Math.Clamp(MouseJitterPx, 0, MacroLimits.HumanizeMouseMaxPx),
        };
        _doc.Options.SpeedPct = Math.Clamp(SpeedPct, MacroLimits.SpeedMinPct, MacroLimits.SpeedMaxPct);
        _doc.Options.HotkeyStart = string.IsNullOrEmpty(EditorHotkey) ? null : EditorHotkey;
        _doc.Options.HotkeyStop = _doc.Options.HotkeyStart;
        _doc.Options.TriggerMode = EditorTriggerMode;
        _doc.Options.RunAlongside = EditorRunAlongside;
    }

    /// <summary>
    /// Rebuilds the timeline rows. Rows inside a Loop / If / Repeat until are indented, and so are rows between a
    /// key or button's Hold down and its Let go, which also shows how long it's held. -1 keeps the current selection.
    /// </summary>
    private void ShowBlocks(int? selectIndex = -1)
    {
        var keep = selectIndex == -1 ? SelectedBlock?.Index : selectIndex;
        SelectedBlock = null;
        Blocks.Clear();
        var depth = 0;
        var held = new List<string>(); // keys and buttons held down at this point
        for (var i = 0; i < _doc.Blocks.Count; i++)
        {
            var b = _doc.Blocks[i];
            if (b.Type is BlockTypes.End or BlockTypes.Else) depth = Math.Max(0, depth - 1);
            var hold = HoldName(b);
            var action = b.GetString("action");
            if (hold is not null && action == "up") held.Remove(hold);
            var row = new BlockRow(i, b, depth + Math.Min(held.Count, 3), OnRowChanged);
            if (hold is not null && action == "down" && b.Enabled)
            {
                if (!held.Contains(hold)) held.Add(hold);
                row.HeldFor = HeldTime(i, hold) is { } ms ? $"held {ms:N0} ms" : "not let go";
            }
            row.IsChecked = _checked.Contains(b);
            Blocks.Add(row);
            if (BlockTypes.IsOpener(b.Type) || b.Type == BlockTypes.Else) depth++;
        }
        // Blocks that were deleted, or a whole other macro loaded, drop out of the ticked set.
        var shown = new HashSet<Block>(_doc.Blocks, ReferenceEqualityComparer.Instance);
        _checked.RemoveWhere(b => !shown.Contains(b));
        if (_checkAnchor is not null && !shown.Contains(_checkAnchor)) _checkAnchor = null;
        NotifyChecked();
        OnPropertyChanged(nameof(BlockSummary));
        ApplyCollapse();
        SelectedBlock = keep is { } k && k >= 0 && k < Blocks.Count ? Blocks[k] : null;
    }

    /// <summary>"key:LeftCtrl" or "button:left" for Key and Click blocks that hold down or let go; else null.</summary>
    private static string? HoldName(Block b) => b.Type switch
    {
        BlockTypes.Key => $"key:{b.GetString("key")}",
        BlockTypes.Click => $"button:{b.GetString("button", "left")}",
        _ => null,
    };

    /// <summary>
    /// Roughly how long a Hold down lasts before its Let go: the waits and presses in between. Null when it's never
    /// let go in this macro.
    /// </summary>
    private int? HeldTime(int downIndex, string hold)
    {
        var total = 0L;
        for (var i = downIndex + 1; i < _doc.Blocks.Count; i++)
        {
            var b = _doc.Blocks[i];
            if (!b.Enabled) continue;
            if (HoldName(b) == hold && b.GetString("action") == "up") return (int)Math.Min(total, int.MaxValue);
            total += b.Type switch
            {
                BlockTypes.Wait => b.GetInt("ms", 0),
                BlockTypes.WaitRandom => (b.GetInt("minMs", 0) + b.GetInt("maxMs", 0)) / 2,
                BlockTypes.Key when b.GetString("action", "press") == "press" => b.GetInt("holdMs", 30),
                BlockTypes.Click when b.GetString("action", "press") == "press" => b.GetInt("holdMs", 20) + b.GetInt("moveMs", 0),
                BlockTypes.HoldKey or BlockTypes.Combo => b.GetInt("holdMs", 40),
                BlockTypes.Move or BlockTypes.Drag => b.GetInt("durationMs", 0),
                _ => 0,
            };
        }
        return null;
    }

    /// <summary>
    /// Marks the playing block. One hidden inside a folded block lights up the folded row instead (it stays folded),
    /// and that's the row Follow the playing block keeps in view.
    /// </summary>
    private void Highlight(int index)
    {
        var shown = ShownRow(index);
        foreach (var row in Blocks) row.IsCurrent = row.Index == shown;
        if (shown >= 0 && shown < Blocks.Count && FollowPlayingBlock) PlayingBlockChanged?.Invoke(shown);
    }

    /// <summary>A block of the loaded macro started playing; the view centers it (Settings › Follow the playing block).</summary>
    public event Action<int>? PlayingBlockChanged;

    private void OnRunState(MacroRun run, RunState state)
    {
        OnLibraryRunState(run);
        if (state == RunState.Faulted) ReportFault(run);
        if (run != _run) return;
        switch (state)
        {
            case RunState.WaitingForWindow:
                Status = $"Waiting for {SelectedGame.Name} to be in front…";
                break;
            case RunState.AtBreakpoint:
                IsAtBreakpoint = true;
                Status = $"Paused at the breakpoint on block {run.CurrentIndex + 1:00}. Press Continue ({PlayHotkeyText}) to carry on.";
                break;
            case RunState.Running:
                if (!IsAtBreakpoint) break;
                IsAtBreakpoint = false;
                Status = $"Playing… press {PlayHotkeyText} to stop, {KeyText(Hotkey.Panic.ToString())} stops everything.";
                break;
            case RunState.Stopped:
                IsPlaying = false;
                IsAtBreakpoint = false;
                // The run's row highlight looks like a selection, so it mustn't stay once the run is over.
                Highlight(-1);
                Status = run.CompletedPasses == 1 ? "Done." : $"Done after {run.CompletedPasses} runs.";
                break;
            case RunState.Faulted:
                IsPlaying = false;
                IsAtBreakpoint = false;
                Highlight(-1);
                if (Blocks.FirstOrDefault(b => b.Block.Id == run.FaultBlockId) is { } bad)
                {
                    bad.IsFault = true;
                    SelectedBlock = bad;
                }
                break;
        }
    }

    /// <summary>
    /// A macro stopped on an error. Said in the status bar and as a Windows notification, for every run (also ones
    /// started by their key from Saved macros), because Night-loop is usually hidden behind the game by then and a
    /// macro that silently ends looks like a bug in the loop or Go to.
    /// </summary>
    private void ReportFault(MacroRun run)
    {
        var index = -1;
        for (var i = 0; i < run.Macro.Steps.Length; i++)
            if (run.Macro.Steps[i].Block.Id == run.FaultBlockId) { index = i; break; }
        var where = index >= 0 ? $"block {index + 1:00}" : "a block";
        Status = $"\"{run.Name}\" stopped at {where}: {run.FaultMessage}";
        TrayIcon.Notify($"{run.Name} stopped at {where}", run.FaultMessage ?? "");
    }

    private IDisposable? TryRegister(Hotkey hotkey, Action action)
    {
        try
        {
            return _hotkeys.Register(hotkey, action);
        }
        catch (HotkeyTakenException ex)
        {
            Status = ex.Message;
            return null;
        }
    }

    /// <summary>The editor's Play key (F6).</summary>
    private void RegisterEditorKeys()
    {
        _playKey?.Dispose();
        _playKey = TryRegister(PlayHotkey, () => OnUi(TogglePlay));
    }

    private void DisposeEditorKeys()
    {
        _playKey?.Dispose();
        _playKey = null;
    }

    /// <summary>
    /// While a key is being picked, Night-loop lets go of its own keys (all but stop-everything), so pressing one of
    /// them picks it instead of starting a macro.
    /// </summary>
    private void OnCaptureChanged()
    {
        OnPropertyChanged(nameof(IsCapturingKey));
        if (IsCapturingKey)
        {
            DisposeEditorKeys();
            DisposeMacroHotkeys();
        }
        else if (_playKey is null)
        {
            RegisterEditorKeys();
            RefreshHotkeys();
        }
    }

    private static void PlayWindowsSound(string sound)
    {
        var s = sound switch
        {
            "beep" => SystemSounds.Beep,
            "exclamation" => SystemSounds.Exclamation,
            "question" => SystemSounds.Question,
            _ => SystemSounds.Asterisk,
        };
        s.Play();
    }

    private static void OnUi(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    private static MacroDoc NewDoc() => new()
    {
        Name = "New macro",
        Options = new MacroOptions { Humanize = new HumanizeOptions { Enabled = true, TimingJitterPct = 15, MousePx = 3 } },
    };

    public void Dispose()
    {
        if (_disposed) return; // a second Dispose would wait on hotkey and hook threads that are gone
        _disposed = true;
        _toastTimer.Stop();
        _games.TargetChanged -= OnTargetChanged;
        _ = DisposeAutosaverAsync();
        _ = _runs.StopAllAsync();
        DisposeMacroHotkeys();
        DisposeEditorKeys();
        _panicKey?.Dispose();
        _hotkeys.Dispose();
    }
}

/// <summary>A category chip above the palette.</summary>
public sealed partial class PaletteCategory(string name, string color, int count) : ObservableObject
{
    public string Name { get; } = name;
    public string Color { get; } = color;
    public int Count { get; } = count;
    [ObservableProperty] private bool _isOpen;
}
