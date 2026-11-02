using System.Collections.ObjectModel;
using System.IO;
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
/// The record and replay screen. Macros live on the account: the first edit of a new macro creates it on the
/// server (taking one of the 15 slots), every later edit autosaves a draft, and Save makes it the saved copy.
/// Import and Export still work with JSON files.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public static readonly Hotkey RecordHotkey = new(HotkeyModifiers.None, 0x78); // F9
    public static readonly Hotkey PlayHotkey = new(HotkeyModifiers.None, 0x75);   // F6

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
        _runs.RunStepChanged += (_, index) => OnUi(() => Highlight(index));
        _runs.RunStateChanged += (run, state) => OnUi(() =>
        {
            OnRunState(run, state);
            OnPropertyChanged(nameof(ActiveRunCount));
            OnPropertyChanged(nameof(IsBusy));
        });
        _runs.Notify = message => OnUi(() => Status = message);

        TryRegister(RecordHotkey, () => OnUi(ToggleRecord));
        TryRegister(PlayHotkey, () => OnUi(TogglePlay));
        TryRegister(Hotkey.Panic, () => OnUi(StopAll));
    }

    /// <summary>The target game picked in Settings.</summary>
    public TargetGamesViewModel Games => _games;
    private GameTarget SelectedGame => _games.Target;
    public ObservableCollection<BlockRow> Blocks { get; } = [];
    public ObservableCollection<SavedMacroRow> SavedMacros { get; } = [];

    public string RecordHotkeyText => RecordHotkey.ToString();
    public string PlayHotkeyText => PlayHotkey.ToString();
    public string PanicHotkeyText => Hotkey.Panic.ToString();

    [ObservableProperty] private string _macroName = "New macro";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isRecording;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _recordMouseMoves;
    [ObservableProperty] private int _repeatCount = 1;
    [ObservableProperty] private bool _repeatForever;
    [ObservableProperty] private bool _humanize;
    [ObservableProperty] private int _timingJitterPct = 10;
    [ObservableProperty] private int _mouseJitterPx = 2;
    [ObservableProperty] private string _status = "Press Record (or F9), do your thing, then press F9 again.";
    [ObservableProperty] private string _syncText = "Not on your account yet";
    [ObservableProperty] private string _usedText = "";
    [ObservableProperty] private SavedMacroRow? _selectedSaved;

    /// <summary>Macros running right now (Home's "Active macros" card).</summary>
    public int ActiveRunCount => _runs.ActiveRuns.Count;

    /// <summary>Recording or running: the app must not restart for an update by itself.</summary>
    public bool IsBusy => IsRecording || ActiveRunCount > 0;

    public string BlockSummary => $"{Blocks.Count} blocks · {Blocks.Count(b => !b.Enabled)} off";

    private void OnTargetChanged(GameTarget value) => MarkChanged();

    // Every editor field that ends up in the document autosaves.
    partial void OnMacroNameChanged(string value) => MarkChanged();
    partial void OnRepeatCountChanged(int value) => MarkChanged();
    partial void OnRepeatForeverChanged(bool value) => MarkChanged();
    partial void OnHumanizeChanged(bool value) => MarkChanged();
    partial void OnTimingJitterPctChanged(int value) => MarkChanged();
    partial void OnMouseJitterPxChanged(int value) => MarkChanged();

    partial void OnSelectedSavedChanged(SavedMacroRow? value)
    {
        if (!_loading && value is not null && value.Id != _macroId) _ = OpenAsync(value.Id);
    }

    [RelayCommand]
    private void ToggleRecord()
    {
        if (IsPlaying) return;
        if (IsRecording)
        {
            var result = _recorder.Stop();
            IsRecording = false;
            _doc.Blocks = result.Blocks;
            ShowBlocks();
            MarkChanged();
            Status = result.Truncated
                ? $"Recording stopped at the {MacroLimits.MaxBlocks}-block limit."
                : $"Recorded {result.Blocks.Count} blocks. Press Play (F6) to replay.";
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
        if (_run is { IsActive: true })
        {
            _run.Stop();
            return;
        }

        SyncDoc();
        CompiledMacro compiled;
        try
        {
            compiled = MacroCompiler.Compile(_doc);
        }
        catch (MacroCompileException ex)
        {
            Status = ex.Issues.Count > 0 ? ex.Issues[0].ToString() : ex.Message;
            return;
        }

        _run = _runs.Start(compiled, new RunSettings { TargetExe = SelectedGame.Exe });
        IsPlaying = true;
        Status = $"Playing… press {PlayHotkey} to stop, {Hotkey.Panic} stops everything.";
    }

    /// <summary>Set by the shell: opens Settings, where the target game is picked.</summary>
    public Action? OpenSettings { get; set; }

    [RelayCommand]
    private void ChangeTarget() => OpenSettings?.Invoke();

    [RelayCommand]
    private void StopAll()
    {
        if (IsRecording) ToggleRecord();
        _ = _runs.StopAllAsync();
    }

    /// <summary>Starts a blank macro. It reaches the account (and takes a slot) on its first edit.</summary>
    [RelayCommand]
    private async Task NewMacroAsync()
    {
        if (IsRecording || IsPlaying) return;
        await CloseCurrentAsync();
        ApplyDoc(NewDoc());
        SelectedSaved = null;
        SyncText = "Not on your account yet";
        Status = "New macro. Record something and it saves to your account.";
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
        if (IsRecording || IsPlaying || _macroId is not { } id) return;
        var answer = MessageBox.Show($"Delete \"{MacroName}\" from your account? This can't be undone.", "Night-loop",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            await DisposeAutosaverAsync();
            await _api.DeleteMacroAsync(id);
            _macroId = null;
            ApplyDoc(NewDoc());
            SyncText = "Not on your account yet";
            Status = "Deleted.";
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            Status = ex.Message;
        }
    }

    /// <summary>Reloads the saved-macros list and the n / 15 meter.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var list = await _api.ListMacrosAsync();
            SavedMacros.Clear();
            foreach (var m in list.Macros) SavedMacros.Add(new SavedMacroRow(m));
            UsedText = $"{list.Macros.Count} / {list.Limit} used";
            // Re-select the open macro without reopening it.
            _loading = true;
            SelectedSaved = _macroId is { } id ? SavedMacros.FirstOrDefault(r => r.Id == id) : null;
            _loading = false;
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            Status = ex.Message;
        }
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
            SelectedSaved = null;
            Status = $"Loaded {doc.Blocks.Count} blocks.";
            await CreateOnServerAsync();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            Status = $"Couldn't open that file: {ex.Message}";
        }
    }

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
            // A macro remembers the game it was made for; opening it switches the target to that game.
            if (doc.TargetGame is not null) _games.SelectByName(doc.TargetGame);
            ShowBlocks();
        }
        finally
        {
            _loading = false;
        }
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
        _doc.Options.HotkeyStart = PlayHotkey.ToString();
        _doc.Options.HotkeyStop = PlayHotkey.ToString();
    }

    private void ShowBlocks()
    {
        Blocks.Clear();
        for (var i = 0; i < _doc.Blocks.Count; i++) Blocks.Add(new BlockRow(i, _doc.Blocks[i]));
        OnPropertyChanged(nameof(BlockSummary));
    }

    private void Highlight(int index)
    {
        foreach (var row in Blocks) row.IsCurrent = row.Index == index;
    }

    private void OnRunState(MacroRun run, RunState state)
    {
        if (run != _run) return;
        switch (state)
        {
            case RunState.WaitingForWindow:
                Status = $"Waiting for {SelectedGame.Name} to be in front…";
                break;
            case RunState.Running:
                Status = $"Playing… press {PlayHotkey} to stop, {Hotkey.Panic} stops everything.";
                break;
            case RunState.Stopped:
                IsPlaying = false;
                Status = $"Done after {run.CompletedPasses} pass(es).";
                break;
            case RunState.Faulted:
                IsPlaying = false;
                Status = $"Stopped at {run.FaultBlockId}: {run.FaultMessage}";
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

    private static void OnUi(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    private static MacroDoc NewDoc() => new() { Name = "New macro" };

    public void Dispose()
    {
        _games.TargetChanged -= OnTargetChanged;
        _ = DisposeAutosaverAsync();
        _recorder.Dispose();
        _ = _runs.StopAllAsync();
        foreach (var r in _hotkeyRegistrations) r.Dispose();
        _hotkeys.Dispose();
    }
}

/// <summary>One line in the Saved macros list.</summary>
public sealed class SavedMacroRow(MacroSummaryDto m)
{
    public Guid Id { get; } = m.Id;
    public string Name { get; } = m.Name;
    public string Detail { get; } = $"{m.BlockCount} blocks{(m.TargetGame is null ? "" : " · " + m.TargetGame)}{(m.HasDraft ? (m.HasSaved ? " · unsaved changes" : " · draft") : "")}";
}
