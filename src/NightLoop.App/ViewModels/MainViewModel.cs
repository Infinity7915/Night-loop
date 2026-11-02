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
using NightLoop.Engine.Recording;
using NightLoop.Engine.Runtime;
using NightLoop.Engine.Screen;
using NightLoop.Engine.Window;
using NightLoop.LocalData;

namespace NightLoop.App.ViewModels;

/// <summary>
/// The Macros screen (schema section 8). This file is the editor: the loaded macro, the block palette, the
/// timeline and the inspector, Record and Play. MainViewModel.Library.cs is the saved-macros list
/// with groups, and MainViewModel.Runs.cs runs several macros at once with their own hotkeys.
/// Macros live on the account: the first edit of a new macro creates it on the server (taking one of the 15 slots),
/// every later edit autosaves a draft, and Save makes it the saved copy.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Start / stop recording until it's changed in the loaded macro's options.</summary>
    public static readonly Hotkey DefaultRecordHotkey = new(HotkeyModifiers.None, 0x78); // F9
    public static readonly Hotkey PlayHotkey = new(HotkeyModifiers.None, 0x75);   // F6

    private static readonly FieldDef NoteField = F.Text("note", "Note (optional)", MacroLimits.CommentMaxLength,
        optional: true, mono: false, placeholder: "Shows under the block");

    private readonly RunManager _runs;
    private readonly Win32WindowService _windows = new();
    private readonly Win32ScreenCapture _screen = new();
    private readonly LocalSettings _settings;
    private readonly InputRecorder _recorder = new();
    private readonly HotkeyManager _hotkeys = new();
    private bool _disposed;
    private IDisposable? _recordKey;
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

    public MainViewModel(ApiClient api, TargetGamesViewModel games, LocalSettings? settings = null)
    {
        _api = api;
        _games = games;
        _settings = settings ?? new LocalSettings();
        _games.TargetChanged += OnTargetChanged;
        _runs = new RunManager(new Win32InputSender(), _windows)
        {
            Screen = _screen,
            Text = WindowsTextReader.TryCreate(),
            IsKeyDown = KeyState.IsDown,
            SaveScreenshot = Screenshots.Save,
        };
        _runs.RunStepChanged += (run, index) => OnUi(() => { if (run == _run) Highlight(index); });
        _runs.RunStateChanged += (run, state) => OnUi(() =>
        {
            OnRunState(run, state);
            OnPropertyChanged(nameof(ActiveRunCount));
            OnPropertyChanged(nameof(StopAllText));
            OnPropertyChanged(nameof(IsBusy));
        });
        _runs.Notify = message => OnUi(() => Status = message);
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal, (_, _) => HideToast(), Dispatcher.CurrentDispatcher);
        _toastTimer.Stop();
        _runs.PlaySound = PlayWindowsSound;
        _runs.RunMacro = RunMacroByNameAsync;

        Categories = BlockCatalog.Categories
            .Select(c => new PaletteCategory(c.Name, c.Color, BlockCatalog.Palette.Count(d => d.Category == c.Name)))
            .ToList();

        RecordHotkey = Hotkey.TryParse(_settings.RecordHotkey, out var recordKey) && RecordHotkeyRule(recordKey) is null
            ? recordKey
            : DefaultRecordHotkey;
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

    /// <summary>Starts and stops recording. Changed in the loaded macro's options; kept on this PC.</summary>
    public Hotkey RecordHotkey { get; private set; }
    public string RecordHotkeyText => KeyText(RecordHotkey.ToString());
    public string RecordToolTip => $"Start or stop recording ({RecordHotkeyText}). New blocks go under the highlighted block.";
    public string PanicHotkeyText => Hotkey.Panic.ToString();

    /// <summary>The loaded macro's own start key, or F6 (the editor's Play key) when it has none. Shown as "Ctrl + F5".</summary>
    public string PlayHotkeyText => KeyText(string.IsNullOrEmpty(EditorHotkey) ? PlayHotkey.ToString() : EditorHotkey);

    /// <summary>"Ctrl+F5" as the mockup shows it: "Ctrl + F5".</summary>
    public static string KeyText(string hotkey) => hotkey.Replace("+", " + ", StringComparison.Ordinal);

    [ObservableProperty] private string _macroName = "New macro";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isRecording;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayText))]
    private bool _isPlaying;
    /// <summary>The loaded macro is paused at a breakpoint; Play reads Continue.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayText))]
    private bool _isAtBreakpoint;
    [ObservableProperty] private bool _recordMouseMoves;
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
    [NotifyPropertyChangedFor(nameof(PlayHotkeyText), nameof(EditorHotkeyLabel))]
    private string _editorHotkey = "";
    [ObservableProperty] private string _editorTriggerMode = TriggerModes.Toggle;
    [ObservableProperty] private bool _editorRunAlongside = true;
    [ObservableProperty] private bool _isCapturingEditorHotkey;
    [ObservableProperty] private string _editorHotkeyError = "";
    [ObservableProperty] private bool _isCapturingRecordHotkey;
    [ObservableProperty] private string _recordHotkeyError = "";

    public string EditorHotkeyLabel => IsCapturingEditorHotkey
        ? "Press a key combo… (Esc cancels)"
        : string.IsNullOrEmpty(EditorHotkey) ? "None  ·  Change" : $"{KeyText(EditorHotkey)}  ·  Change";

    public string RecordHotkeyLabel => IsCapturingRecordHotkey ? "Press a key combo… (Esc cancels)" : $"{RecordHotkeyText}  ·  Change";

    /// <summary>Waiting for a key press to use as a start/stop key (the app's own keys are let go meanwhile).</summary>
    public bool IsCapturingKey => IsCapturingEditorHotkey || IsCapturingRecordHotkey || CapturingRow is not null;

    partial void OnIsCapturingEditorHotkeyChanged(bool value)
    {
        OnPropertyChanged(nameof(EditorHotkeyLabel));
        OnCaptureChanged();
    }

    partial void OnIsCapturingRecordHotkeyChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordHotkeyLabel));
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

    /// <summary>Recording or running: the app must not restart for an update by itself.</summary>
    public bool IsBusy => IsRecording || ActiveRunCount > 0;

    public string BlockSummary => $"{Blocks.Count} blocks · {Blocks.Count(b => !b.Enabled)} off";

    /// <summary>Set by the shell: opens Settings, where the target game is picked.</summary>
    public Action? OpenSettings { get; set; }

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
    partial void OnRecordMouseMovesChanged(bool value) => MarkChanged();
    partial void OnEditorHotkeyChanged(string value) => OnEditorOptionChanged();
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
        if (IsRecording || IsPlaying) return;
        if (_doc.Blocks.Count >= MacroLimits.MaxBlocks)
        {
            Status = $"A macro can have at most {MacroLimits.MaxBlocks} blocks.";
            return;
        }
        var at = SelectedBlock is null ? _doc.Blocks.Count : SelectedBlock.Index + 1;
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
        if (BlockTypes.IsOpener(def.Type) && _doc.Blocks.Count < MacroLimits.MaxBlocks)
            _doc.Blocks.Insert(at + 1, Block.Create(NextId(), BlockTypes.End));
        ShowBlocks(selectIndex: at);
        MarkChanged();
        if (BlockTypes.NeedsVision(block) && _runs.Text is null && block.Type is BlockTypes.WaitForText or BlockTypes.FindClick or BlockTypes.ScanTurn)
            Status = "Reading text needs Windows text recognition. Add a language with text recognition in Windows Settings › Time & language › Language.";
    }

    /// <summary>Turning the camera depends on the game: Roblox turns with a right-drag, most others with the mouse.</summary>
    private void ApplyTargetDefaults(Block block)
    {
        if (block.Type != BlockTypes.CameraTurn) return;
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
        if (IsRecording || IsPlaying) return;
        var (from, to) = SectionOf(row.Index);
        var count = to - from + 1;
        if (_doc.Blocks.Count + count > MacroLimits.MaxBlocks)
        {
            Status = $"A macro can have at most {MacroLimits.MaxBlocks} blocks.";
            return;
        }
        var copies = MacroJson.Deserialize(MacroJson.Serialize(new MacroDoc { Name = "x", Blocks = _doc.Blocks.GetRange(from, count) })).Blocks;
        for (var i = 0; i < copies.Count; i++)
        {
            copies[i].Id = NextId();
            _doc.Blocks.Insert(to + 1 + i, copies[i]);
        }
        ShowBlocks(selectIndex: to + 1);
        MarkChanged();
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

    private void Move(BlockRow row, int by)
    {
        var to = row.Index + by;
        if (IsRecording || IsPlaying || to < 0 || to >= _doc.Blocks.Count) return;
        (_doc.Blocks[row.Index], _doc.Blocks[to]) = (_doc.Blocks[to], _doc.Blocks[row.Index]);
        ShowBlocks(selectIndex: to);
        MarkChanged();
    }

    /// <summary>
    /// Deletes a block. Deleting a Loop, If or Repeat until (or its End) also deletes its End (or opener) and Else,
    /// and keeps the blocks that were inside, so the macro stays runnable.
    /// </summary>
    [RelayCommand]
    private void DeleteBlock(BlockRow row)
    {
        if (IsRecording || IsPlaying) return;
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

    /// <summary>Esc on the timeline: nothing highlighted, so Play starts at the top and Record adds at the end.</summary>
    public void ClearSelection() => SelectedBlock = null;

    /// <summary>Delete key on the timeline.</summary>
    public void DeleteSelected()
    {
        if (SelectedBlock is { } row) DeleteBlock(row);
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
        OnRowChanged(row);
        // A choice can show or hide other fields and tools (On fail → Go to label, Find → Image…).
        if (field.RebuildsInspector)
            Application.Current?.Dispatcher.BeginInvoke(() => { if (SelectedBlock == row) BuildInspector(); }, DispatcherPriority.Background);
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
            if (f.ShowIf?.Invoke(row.Block) != false) InspectorFields.Add(new BlockField(row, f, OnFieldChanged, names, labels));
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

    // ---------------- Record / Play ----------------

    /// <summary>
    /// Record, then press it (or the record key) again to stop. What was recorded goes under the block that was
    /// highlighted when recording started, or at the end when none was. Recording again adds more the same way.
    /// </summary>
    [RelayCommand]
    private void ToggleRecord()
    {
        if (IsPlaying) return;
        if (IsRecording)
        {
            StopRecording();
            return;
        }

        if (_doc.Blocks.Count >= MacroLimits.MaxBlocks)
        {
            Status = $"This macro already has {MacroLimits.MaxBlocks} blocks, the most one can have. Delete some to record more.";
            return;
        }
        _recordAfter = SelectedBlock?.Block;
        try
        {
            _recorder.Start(new RecorderSettings
            {
                TargetExe = TargetExe,
                StopHotkey = RecordHotkey,
                RecordMouseMoves = RecordMouseMoves,
            });
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
            return;
        }
        IsRecording = true;
        StepAside();
        Status = $"Recording… press {RecordHotkeyText} to stop. Keys and clicks on Night-loop itself aren't recorded.";
    }

    private void StopRecording()
    {
        IsRecording = false;
        SyncDoc();
        var result = _recorder.Stop(new RecordingOptions
        {
            RecordMouseMoves = RecordMouseMoves,
            StopHotkey = RecordHotkey,
            // Room left in this macro, so it stays under the limits however much is already in it.
            MaxBlocks = Math.Max(0, MacroLimits.MaxBlocks - _doc.Blocks.Count),
            MaxBytes = Math.Max(0, MacroLimits.MaxDocumentBytes * 9 / 10 - MacroJson.ByteSize(_doc)),
        });

        var at = _recordAfter is { } after && _doc.Blocks.IndexOf(after) is var i and >= 0 ? i + 1 : _doc.Blocks.Count;
        _recordAfter = null;
        var count = result.Blocks.Count;
        if (count == 0)
        {
            Status = result.Truncated
                ? "This macro is full, so nothing more could be recorded. Delete some blocks first."
                : "Nothing was recorded. Keys and clicks on Night-loop itself are skipped, so click into the app you want first.";
            return;
        }

        for (var k = 0; k < count; k++)
        {
            result.Blocks[k].Id = NextId();
            _doc.Blocks.Insert(at + k, result.Blocks[k]);
        }
        var last = at + count - 1;
        var atEnd = last == _doc.Blocks.Count - 1;
        // At the end: nothing stays highlighted, so Play runs it all and the next recording goes on the end too.
        // In the middle: the last new block is highlighted, so the next recording carries on right after it.
        ShowBlocks(selectIndex: atEnd ? null : last);
        RevealRequested?.Invoke(last);
        MarkChanged();
        var what = count == 1 ? "1 block" : $"{count} blocks";
        Status = result.Truncated
            ? $"Recorded {what}, then stopped at the size limit for one macro."
            : atEnd
                ? $"Recorded {what} at the end. Press Play ({PlayHotkeyText}) to replay from the top."
                : $"Recorded {what} after block {at:00}.";
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
        if (IsRecording) return;
        if (_run is { IsActive: true } running)
        {
            if (IsAtBreakpoint) running.Continue();
            else running.Stop();
            return;
        }
        StartEditorRun(new RunSettings { TargetExe = TargetExe, Debug = true, StartIndex = SelectedBlock?.Index ?? 0 });
    }

    /// <summary>
    /// Pressing Record or Play in Night-loop leaves Night-loop in front, where the first keys would land. Put the
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
        if (IsRecording) ToggleRecord();
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
        if (IsRecording || IsPlaying) return;
        if (_rows.Count >= _limit)
        {
            ShowLimit = true;
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
        if (IsRecording || IsPlaying) return;
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

    /// <summary>Called by the view with the key pressed while capturing. Escape cancels, Backspace/Delete clears.</summary>
    public void OnEditorHotkeyCaptured(Hotkey? hotkey, bool clear)
    {
        IsCapturingEditorHotkey = false;
        if (clear)
        {
            EditorHotkey = "";
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
    }

    // ---------------- Record key ----------------

    [RelayCommand]
    private void CaptureRecordHotkey()
    {
        if (IsRecording) return;
        ShowEditorOptions = true;
        RecordHotkeyError = "";
        IsCapturingRecordHotkey = true;
    }

    /// <summary>Called by the view with the key pressed while picking the record key. Escape cancels, Backspace/Delete puts F9 back.</summary>
    public void OnRecordHotkeyCaptured(Hotkey? hotkey, bool reset)
    {
        var key = reset ? DefaultRecordHotkey : hotkey;
        if (key is not { } h)
        {
            IsCapturingRecordHotkey = false;
            return;
        }
        if ((RecordHotkeyRule(h) ?? HotkeyProblem(h, forMacro: null, recordKey: true)) is { } problem)
        {
            RecordHotkeyError = problem;
            IsCapturingRecordHotkey = false;
            return;
        }
        RecordHotkeyError = "";
        RecordHotkey = h;
        _settings.RecordHotkey = h == DefaultRecordHotkey ? null : h.ToString();
        OnPropertyChanged(nameof(RecordHotkey));
        OnPropertyChanged(nameof(RecordHotkeyText));
        OnPropertyChanged(nameof(RecordHotkeyLabel));
        OnPropertyChanged(nameof(RecordToolTip));
        IsCapturingRecordHotkey = false; // registers the new key
        Status = $"Record now starts and stops with {RecordHotkeyText}.";
    }

    /// <summary>
    /// A record key has to be one you don't type with: a letter or Space on its own would start recording every
    /// time you typed it.
    /// </summary>
    internal static string? RecordHotkeyRule(Hotkey h)
    {
        if (h == Hotkey.Panic) return $"{KeyText(h.ToString())} is the stop-everything key.";
        if (h == PlayHotkey) return $"{KeyText(h.ToString())} is the editor's Play key. Pick another key.";
        var typing = h.VirtualKey is (>= 0x30 and <= 0x5A) or 0x20 or 0x0D or 0x09 or 0x08 or (>= 0xBA and <= 0xE2) or (>= 0x60 and <= 0x6F);
        var shiftOnly = h.Modifiers is HotkeyModifiers.None or HotkeyModifiers.Shift;
        return typing && shiftOnly ? "Pick a function key like F9, or add Ctrl or Alt, so typing never starts a recording." : null;
    }

    // ---------------- Plumbing ----------------

    private async Task OpenAsync(Guid id)
    {
        if (IsRecording || IsPlaying)
        {
            Status = "Stop recording or playing before opening another macro.";
            return;
        }
        try
        {
            await CloseCurrentAsync();
            var macro = await _api.GetMacroAsync(id);
            Attach(macro);
            ApplyDoc(macro.Current ?? NewDoc(), starter: false);
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
        _autosaver = new DraftAutosaver(_api, macro);
        _autosaver.StateChanged += state => OnUi(() => OnSyncState(state));
        OnSyncState(_autosaver.State);
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
            ShowBlocks(selectIndex: select);
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
            Blocks.Add(row);
            if (BlockTypes.IsOpener(b.Type) || b.Type == BlockTypes.Else) depth++;
        }
        OnPropertyChanged(nameof(BlockSummary));
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

    private void Highlight(int index)
    {
        foreach (var row in Blocks) row.IsCurrent = row.Index == index;
    }

    private void OnRunState(MacroRun run, RunState state)
    {
        OnLibraryRunState(run);
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
                Status = run.CompletedPasses == 1 ? "Done." : $"Done after {run.CompletedPasses} runs.";
                break;
            case RunState.Faulted:
                IsPlaying = false;
                IsAtBreakpoint = false;
                Status = $"Stopped at {run.FaultBlockId}: {run.FaultMessage}";
                if (Blocks.FirstOrDefault(b => b.Block.Id == run.FaultBlockId) is { } bad)
                {
                    bad.IsFault = true;
                    SelectedBlock = bad;
                }
                break;
        }
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

    /// <summary>The record key and the editor's Play key (F6).</summary>
    private void RegisterEditorKeys()
    {
        _recordKey?.Dispose();
        _playKey?.Dispose();
        _recordKey = TryRegister(RecordHotkey, () => OnUi(ToggleRecord));
        _playKey = TryRegister(PlayHotkey, () => OnUi(TogglePlay));
    }

    private void DisposeEditorKeys()
    {
        _recordKey?.Dispose();
        _playKey?.Dispose();
        _recordKey = _playKey = null;
    }

    /// <summary>
    /// While a key is being picked, Night-loop lets go of its own keys (all but stop-everything), so pressing one of
    /// them picks it instead of starting a recording or a macro.
    /// </summary>
    private void OnCaptureChanged()
    {
        OnPropertyChanged(nameof(IsCapturingKey));
        if (IsCapturingKey)
        {
            DisposeEditorKeys();
            DisposeMacroHotkeys();
        }
        else if (_recordKey is null && _playKey is null)
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
        _recorder.Dispose();
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
