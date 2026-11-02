using System.Collections.ObjectModel;
using System.IO;
using System.Media;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NightLoop.Client;
using NightLoop.Contracts;
using NightLoop.Contracts.Api;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Compiler;
using NightLoop.Engine.Hotkeys;
using NightLoop.Engine.Input;
using NightLoop.Engine.Recording;
using NightLoop.Engine.Runtime;
using NightLoop.Engine.Window;
using NightLoop.LocalData;

namespace NightLoop.App.ViewModels;

/// <summary>
/// The Macros screen (schema section 8). This file is the editor: the loaded macro, the block palette, the
/// timeline and the inspector, Record / Step / Run from / Play. MainViewModel.Library.cs is the saved-macros list
/// with groups, and MainViewModel.Runs.cs runs several macros at once with their own hotkeys.
/// Macros live on the account: the first edit of a new macro creates it on the server (taking one of the 15 slots),
/// every later edit autosaves a draft, and Save makes it the saved copy.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public static readonly Hotkey RecordHotkey = new(HotkeyModifiers.None, 0x78); // F9
    public static readonly Hotkey PlayHotkey = new(HotkeyModifiers.None, 0x75);   // F6

    private static readonly FieldDef NoteField = new("note", "Note (optional)", FieldKind.Text, Optional: true,
        MaxLength: MacroLimits.CommentMaxLength, Hint: "Shows under the block");

    private readonly RunManager _runs;
    private readonly InputRecorder _recorder = new();
    private readonly HotkeyManager _hotkeys = new();
    private readonly List<IDisposable> _hotkeyRegistrations = [];
    private readonly ApiClient _api;
    private readonly TargetGamesViewModel _games;
    private MacroDoc _doc = NewDoc();
    private MacroRun? _run;
    private DraftAutosaver? _autosaver;
    private Guid? _macroId;
    private bool _loading;
    private bool _creating;

    public MainViewModel(ApiClient api, TargetGamesViewModel games)
    {
        _api = api;
        _games = games;
        _games.TargetChanged += OnTargetChanged;
        _runs = new RunManager(new Win32InputSender(), new Win32WindowService());
        _runs.RunStepChanged += (run, index) => OnUi(() => { if (run == _run) Highlight(index); });
        _runs.RunStateChanged += (run, state) => OnUi(() =>
        {
            OnRunState(run, state);
            OnPropertyChanged(nameof(ActiveRunCount));
            OnPropertyChanged(nameof(StopAllText));
            OnPropertyChanged(nameof(IsBusy));
        });
        _runs.Notify = message => OnUi(() => Status = message);
        _runs.PlaySound = PlayWindowsSound;
        _runs.RunMacro = RunMacroByNameAsync;

        Categories = BlockCatalog.Categories
            .Select(c => new PaletteCategory(c.Name, c.Color, BlockCatalog.All.Count(d => d.Category == c.Name)))
            .ToList();

        TryRegister(RecordHotkey, () => OnUi(ToggleRecord));
        TryRegister(PlayHotkey, () => OnUi(TogglePlay));
        TryRegister(Hotkey.Panic, () => OnUi(StopAll));
    }

    /// <summary>The target game picked in Settings.</summary>
    public TargetGamesViewModel Games => _games;
    private GameTarget SelectedGame => _games.Target;

    public ObservableCollection<BlockRow> Blocks { get; } = [];
    public ObservableCollection<BlockField> InspectorFields { get; } = [];
    public IReadOnlyList<PaletteCategory> Categories { get; }
    public ObservableCollection<BlockDef> PaletteItems { get; } = [];

    public string RecordHotkeyText => RecordHotkey.ToString();
    public string PanicHotkeyText => Hotkey.Panic.ToString();

    /// <summary>The loaded macro's own start key, or F6 (the editor's Play key) when it has none.</summary>
    public string PlayHotkeyText => string.IsNullOrEmpty(EditorHotkey) ? PlayHotkey.ToString() : EditorHotkey;

    [ObservableProperty] private string _macroName = "New macro";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isRecording;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayText))]
    private bool _isPlaying;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayText))]
    private bool _isStepping;
    [ObservableProperty] private bool _recordMouseMoves;
    [ObservableProperty] private int _repeatCount = 1;
    [ObservableProperty] private bool _repeatForever;
    [ObservableProperty] private bool _humanize;
    [ObservableProperty] private int _timingJitterPct = 10;
    [ObservableProperty] private int _mouseJitterPx = 2;
    [ObservableProperty] private string _status = "Press Record (or F9), or add blocks from the palette.";
    [ObservableProperty] private string _syncText = "Not on your account yet";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(RunFromText), nameof(InspectorTitle), nameof(InspectorDescription), nameof(InspectorEyebrow))]
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

    public string EditorHotkeyLabel => IsCapturingEditorHotkey ? "Press a key…" : string.IsNullOrEmpty(EditorHotkey) ? "None" : EditorHotkey;

    partial void OnIsCapturingEditorHotkeyChanged(bool value) => OnPropertyChanged(nameof(EditorHotkeyLabel));

    public bool HasSelection => SelectedBlock is not null;
    public string RunFromText => SelectedBlock is null ? "Run from…" : $"Run from {SelectedBlock.Number}";
    public string InspectorEyebrow => SelectedBlock is null ? "EDIT BLOCK" : $"EDIT BLOCK {SelectedBlock.Number}";
    public string InspectorTitle => SelectedBlock?.Title ?? "No block selected";
    public string InspectorDescription => SelectedBlock is null
        ? "Click a block on the timeline to change it, or add one from the palette."
        : BlockCatalog.For(SelectedBlock.Block.Type)?.Description ?? "";

    public string PlayText => IsStepping ? "Continue" : IsPlaying ? "Stop" : "Play";

    /// <summary>Macros running right now (Home's "Active macros" card).</summary>
    public int ActiveRunCount => _runs.ActiveRuns.Count;
    public string StopAllText => $"Stop all · {ActiveRunCount} running";

    /// <summary>Recording or running: the app must not restart for an update by itself.</summary>
    public bool IsBusy => IsRecording || ActiveRunCount > 0;

    public string BlockSummary => $"{Blocks.Count} blocks · {Blocks.Count(b => !b.Enabled)} off";

    /// <summary>Set by the shell: opens Settings, where the target game is picked.</summary>
    public Action? OpenSettings { get; set; }

    private void OnTargetChanged(GameTarget value) => MarkChanged();

    // Every editor field that ends up in the document autosaves.
    partial void OnMacroNameChanged(string value) => MarkChanged();
    partial void OnRepeatCountChanged(int value) => MarkChanged();
    partial void OnRepeatForeverChanged(bool value) => MarkChanged();
    partial void OnHumanizeChanged(bool value) => MarkChanged();
    partial void OnTimingJitterPctChanged(int value) => MarkChanged();
    partial void OnMouseJitterPxChanged(int value) => MarkChanged();
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
        foreach (var d in BlockCatalog.All.Where(d => d.Category == open.Name)) PaletteItems.Add(d);
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
        _doc.Blocks.Insert(at, block);
        if (BlockTypes.IsOpener(def.Type) && _doc.Blocks.Count < MacroLimits.MaxBlocks)
            _doc.Blocks.Insert(at + 1, Block.Create(NextId(), BlockTypes.End));
        ShowBlocks(selectIndex: at);
        MarkChanged();
        Status = def.NeedsVision
            ? $"Added {def.Label}. It needs screen reading, which is coming in a later update, so it can't run yet."
            : $"Added {def.Label}.";
    }

    // ---------------- Timeline row actions ----------------

    [RelayCommand]
    private void SelectBlock(BlockRow row) => SelectedBlock = row;

    [RelayCommand]
    private void DuplicateBlock(BlockRow row)
    {
        if (IsRecording || IsPlaying || _doc.Blocks.Count >= MacroLimits.MaxBlocks) return;
        var copy = MacroJson.Deserialize(MacroJson.Serialize(new MacroDoc { Name = "x", Blocks = [row.Block] })).Blocks[0];
        copy.Id = NextId();
        _doc.Blocks.Insert(row.Index + 1, copy);
        ShowBlocks(selectIndex: row.Index + 1);
        MarkChanged();
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

    [RelayCommand]
    private void DeleteBlock(BlockRow row)
    {
        if (IsRecording || IsPlaying) return;
        _doc.Blocks.RemoveAt(row.Index);
        ShowBlocks(selectIndex: _doc.Blocks.Count == 0 ? null : Math.Min(row.Index, _doc.Blocks.Count - 1));
        MarkChanged();
    }

    [RelayCommand]
    private void ToggleBreakpoint(BlockRow row) => row.Breakpoint = !row.Breakpoint;

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

    private void BuildInspector()
    {
        InspectorFields.Clear();
        if (SelectedBlock is not { } row) return;
        var def = BlockCatalog.For(row.Block.Type);
        var names = LibraryNames();
        foreach (var f in def?.Fields ?? []) InspectorFields.Add(new BlockField(row, f, OnRowChanged, names));
        if (row.Block.Type is not (BlockTypes.Comment or BlockTypes.Description))
            InspectorFields.Add(new BlockField(row, NoteField, OnRowChanged));
    }

    private string NextId()
    {
        var used = new HashSet<string>(_doc.Blocks.Select(b => b.Id), StringComparer.Ordinal);
        for (var i = _doc.Blocks.Count + 1; ; i++)
            if (!used.Contains($"b{i}")) return $"b{i}";
    }

    // ---------------- Record / Step / Run from / Play ----------------

    [RelayCommand]
    private void ToggleRecord()
    {
        if (IsPlaying) return;
        if (IsRecording)
        {
            var result = _recorder.Stop();
            IsRecording = false;
            // Recording adds to the end, so blocks added by hand stay.
            var room = MacroLimits.MaxBlocks - _doc.Blocks.Count;
            foreach (var b in result.Blocks.Take(room))
            {
                b.Id = NextId();
                _doc.Blocks.Add(b);
            }
            ShowBlocks();
            MarkChanged();
            Status = result.Truncated || result.Blocks.Count > room
                ? $"Recording stopped at the {MacroLimits.MaxBlocks}-block limit."
                : $"Recorded {result.Blocks.Count} blocks. Press Play ({PlayHotkeyText}) to replay.";
            return;
        }

        _recorder.Start(new RecorderSettings
        {
            TargetExe = SelectedGame.Exe,
            StopHotkey = RecordHotkey,
            RecordMouseMoves = RecordMouseMoves,
        });
        IsRecording = true;
        Status = $"Recording… press {RecordHotkey} to stop.";
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (IsRecording) return;
        if (_run is { IsActive: true } running)
        {
            if (IsStepping) running.Continue();
            else running.Stop();
            return;
        }
        StartEditorRun(new RunSettings { TargetExe = SelectedGame.Exe, Debug = true });
    }

    /// <summary>Step: start paused at the selected (or first) block, then run one block per press.</summary>
    [RelayCommand]
    private void Step()
    {
        if (IsRecording) return;
        if (_run is { IsActive: true } running)
        {
            running.StepOnce();
            return;
        }
        StartEditorRun(new RunSettings
        {
            TargetExe = SelectedGame.Exe,
            Debug = true,
            StartPaused = true,
            StartIndex = SelectedBlock?.Index ?? 0,
        });
    }

    [RelayCommand]
    private void RunFrom()
    {
        if (IsRecording || SelectedBlock is null) return;
        if (_run is { IsActive: true }) return;
        StartEditorRun(new RunSettings { TargetExe = SelectedGame.Exe, Debug = true, StartIndex = SelectedBlock.Index });
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
        IsStepping = settings.StartPaused;
        Status = settings.StartPaused
            ? "Stepping: press Step to run the highlighted block, Continue to run the rest."
            : $"Playing… press {PlayHotkeyText} to stop, {Hotkey.Panic} stops everything.";
    }

    [RelayCommand]
    private void StopAll()
    {
        if (IsRecording) ToggleRecord();
        _ = _runs.StopAllAsync();
    }

    // ---------------- New / Save / Delete / Import / Export ----------------

    /// <summary>Starts a blank macro. It reaches the account (and takes a slot) on its first edit.</summary>
    [RelayCommand]
    private async Task NewMacroAsync()
    {
        if (IsRecording || IsPlaying) return;
        await CloseCurrentAsync();
        ApplyDoc(NewDoc());
        SyncText = "Not on your account yet";
        RefreshRows();
        Status = "New macro. Add blocks or record something and it saves to your account.";
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
            ApplyDoc(NewDoc());
            Status = "Cleared.";
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

    [RelayCommand]
    private void Export()
    {
        SyncDoc();
        var dialog = new SaveFileDialog { Filter = "Night-loop macro (*.json)|*.json", FileName = $"{MacroName}.json" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, MacroJson.Serialize(_doc, indented: true));
        Status = $"Saved {Path.GetFileName(dialog.FileName)}.";
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (IsRecording || IsPlaying) return;
        var dialog = new OpenFileDialog { Filter = "Night-loop macro (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var doc = MacroJson.Deserialize(File.ReadAllText(dialog.FileName));
            var issues = MacroValidator.Validate(doc);
            if (issues.Count > 0)
            {
                Status = $"That macro has problems: {issues[0]}";
                return;
            }
            // An imported file becomes a new macro on the account.
            await CloseCurrentAsync();
            ApplyDoc(doc);
            Status = $"Loaded {doc.Blocks.Count} blocks.";
            await CreateOnServerAsync();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            Status = $"Couldn't open that file: {ex.Message}";
        }
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
            ApplyDoc(macro.Current ?? NewDoc());
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
            SyncState.Pending or SyncState.Saving => "Saving draft…",
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
        ApplyDoc(copy.Current ?? NewDoc());
        Status = "Loaded the server copy.";
    }

    /// <summary>Shows a document in the editor without triggering autosave.</summary>
    private void ApplyDoc(MacroDoc doc)
    {
        _loading = true;
        try
        {
            _doc = doc;
            MacroName = doc.Name;
            RepeatForever = doc.Options.Repeat.Mode == RepeatModes.Forever;
            RepeatCount = doc.Options.Repeat.Count ?? 1;
            Humanize = doc.Options.Humanize.Enabled;
            TimingJitterPct = doc.Options.Humanize.TimingJitterPct;
            MouseJitterPx = doc.Options.Humanize.MousePx;
            EditorHotkey = doc.Options.HotkeyStart ?? "";
            EditorTriggerMode = doc.Options.TriggerMode;
            EditorRunAlongside = doc.Options.RunAlongside;
            EditorHotkeyError = "";
            // A macro remembers the game it was made for; opening it switches the target to that game.
            if (doc.TargetGame is not null) _games.SelectByName(doc.TargetGame);
            ShowBlocks(selectIndex: null);
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
        _doc.Options.HotkeyStart = string.IsNullOrEmpty(EditorHotkey) ? null : EditorHotkey;
        _doc.Options.HotkeyStop = _doc.Options.HotkeyStart;
        _doc.Options.TriggerMode = EditorTriggerMode;
        _doc.Options.RunAlongside = EditorRunAlongside;
    }

    /// <summary>Rebuilds the timeline rows (with nesting depth). -1 keeps the current selection.</summary>
    private void ShowBlocks(int? selectIndex = -1)
    {
        var keep = selectIndex == -1 ? SelectedBlock?.Index : selectIndex;
        SelectedBlock = null;
        Blocks.Clear();
        var depth = 0;
        for (var i = 0; i < _doc.Blocks.Count; i++)
        {
            var b = _doc.Blocks[i];
            if (b.Type is BlockTypes.End or BlockTypes.Else) depth = Math.Max(0, depth - 1);
            Blocks.Add(new BlockRow(i, b, depth, OnRowChanged));
            if (BlockTypes.IsOpener(b.Type) || b.Type == BlockTypes.Else) depth++;
        }
        OnPropertyChanged(nameof(BlockSummary));
        SelectedBlock = keep is { } k && k >= 0 && k < Blocks.Count ? Blocks[k] : null;
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
                IsStepping = true;
                Status = $"Paused before block {run.CurrentIndex + 1:00}. Step runs it, Continue runs the rest.";
                break;
            case RunState.Running:
                IsStepping = false;
                Status = $"Playing… press {PlayHotkeyText} to stop, {Hotkey.Panic} stops everything.";
                break;
            case RunState.Stopped:
                IsPlaying = false;
                IsStepping = false;
                Status = $"Done after {run.CompletedPasses} pass(es).";
                break;
            case RunState.Faulted:
                IsPlaying = false;
                IsStepping = false;
                Status = $"Stopped at {run.FaultBlockId}: {run.FaultMessage}";
                if (Blocks.FirstOrDefault(b => b.Block.Id == run.FaultBlockId) is { } bad)
                {
                    bad.IsFault = true;
                    SelectedBlock = bad;
                }
                break;
        }
    }

    private void TryRegister(Hotkey hotkey, Action action)
    {
        try
        {
            _hotkeyRegistrations.Add(_hotkeys.Register(hotkey, action));
        }
        catch (HotkeyTakenException ex)
        {
            Status = ex.Message;
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

    private static MacroDoc NewDoc() => new() { Name = "New macro" };

    public void Dispose()
    {
        _games.TargetChanged -= OnTargetChanged;
        _ = DisposeAutosaverAsync();
        _recorder.Dispose();
        _ = _runs.StopAllAsync();
        DisposeMacroHotkeys();
        foreach (var r in _hotkeyRegistrations) r.Dispose();
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
