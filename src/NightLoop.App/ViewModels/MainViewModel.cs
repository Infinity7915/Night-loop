using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
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
/// The record and replay screen. Macros live in memory and can be exported or imported as JSON files
/// until account sync (milestone M3/M4) stores them on the server.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public static readonly Hotkey RecordHotkey = new(HotkeyModifiers.None, 0x78); // F9
    public static readonly Hotkey PlayHotkey = new(HotkeyModifiers.None, 0x75);   // F6

    private readonly RunManager _runs;
    private readonly InputRecorder _recorder = new();
    private readonly HotkeyManager _hotkeys = new();
    private readonly List<IDisposable> _hotkeyRegistrations = [];
    private MacroDoc _doc = NewDoc();
    private MacroRun? _run;

    public MainViewModel()
    {
        Games = new ObservableCollection<GameTarget>(new GamesStore().Load());
        _selectedGame = Games[0];
        _runs = new RunManager(new Win32InputSender(), new Win32WindowService());
        _runs.RunStepChanged += (_, index) => OnUi(() => Highlight(index));
        _runs.RunStateChanged += (run, state) => OnUi(() => OnRunState(run, state));
        _runs.Notify = message => OnUi(() => Status = message);

        TryRegister(RecordHotkey, () => OnUi(ToggleRecord));
        TryRegister(PlayHotkey, () => OnUi(TogglePlay));
        TryRegister(Hotkey.Panic, () => OnUi(StopAll));
    }

    public ObservableCollection<GameTarget> Games { get; }
    public ObservableCollection<BlockRow> Blocks { get; } = [];

    public string RecordHotkeyText => RecordHotkey.ToString();
    public string PlayHotkeyText => PlayHotkey.ToString();
    public string PanicHotkeyText => Hotkey.Panic.ToString();

    [ObservableProperty] private GameTarget _selectedGame;
    [ObservableProperty] private string _macroName = "New macro";
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _recordMouseMoves;
    [ObservableProperty] private int _repeatCount = 1;
    [ObservableProperty] private bool _repeatForever;
    [ObservableProperty] private bool _humanize;
    [ObservableProperty] private int _timingJitterPct = 10;
    [ObservableProperty] private int _mouseJitterPx = 2;
    [ObservableProperty] private string _status = "Press Record (or F9), do your thing, then press F9 again.";

    public string TargetBanner => SelectedGame.Exe is null
        ? "No target game: input goes to whatever window is in front."
        : $"Input goes to {SelectedGame.Name} only, and pauses when it isn't focused.";

    public string BlockSummary => $"{Blocks.Count} blocks · {Blocks.Count(b => !b.Enabled)} off";

    partial void OnSelectedGameChanged(GameTarget value) => OnPropertyChanged(nameof(TargetBanner));

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

    [RelayCommand]
    private void StopAll()
    {
        if (IsRecording) ToggleRecord();
        _ = _runs.StopAllAsync();
    }

    [RelayCommand]
    private void Clear()
    {
        if (IsRecording || IsPlaying) return;
        _doc = NewDoc();
        MacroName = _doc.Name;
        ShowBlocks();
        Status = "Cleared.";
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
    private void Import()
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
            _doc = doc;
            MacroName = doc.Name;
            RepeatForever = doc.Options.Repeat.Mode == RepeatModes.Forever;
            RepeatCount = doc.Options.Repeat.Count ?? 1;
            Humanize = doc.Options.Humanize.Enabled;
            TimingJitterPct = doc.Options.Humanize.TimingJitterPct;
            MouseJitterPx = doc.Options.Humanize.MousePx;
            SelectedGame = Games.FirstOrDefault(g => g.Name == doc.TargetGame) ?? Games[0];
            ShowBlocks();
            Status = $"Loaded {doc.Blocks.Count} blocks.";
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            Status = $"Couldn't open that file: {ex.Message}";
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
        _recorder.Dispose();
        _ = _runs.StopAllAsync();
        foreach (var r in _hotkeyRegistrations) r.Dispose();
        _hotkeys.Dispose();
    }
}
