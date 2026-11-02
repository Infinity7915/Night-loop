using System.Runtime.InteropServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NightLoop.Client;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Compiler;
using NightLoop.Engine.Hotkeys;
using NightLoop.Engine.Runtime;

namespace NightLoop.App.ViewModels;

/// <summary>
/// Running several macros at once (schema section 7, "Run alongside"): each saved macro's own start/stop key,
/// the on/off switches in the list, Hold trigger mode, and the Run macro block.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<Guid, MacroRun> _runsById = [];
    private readonly Dictionary<Guid, IDisposable> _macroHotkeys = [];
    private readonly Dictionary<Guid, DispatcherTimer> _holdWatchers = [];
    private IReadOnlyDictionary<string, Guid> _nameToId = new Dictionary<string, Guid>();

    /// <summary>The saved-macro row waiting for a key press (its start key is being changed).</summary>
    [ObservableProperty] private SavedMacroRow? _capturingRow;

    /// <summary>A macro's key or switch: start it, or stop it if it's running.</summary>
    internal void ToggleMacro(Guid id)
    {
        if (id == _macroId)
        {
            TogglePlay();
            RefreshRows();
            return;
        }
        if (_runsById.TryGetValue(id, out var running) && running.IsActive)
        {
            running.Stop();
            return;
        }
        _ = StartSavedAsync(id);
    }

    private async Task StartSavedAsync(Guid id)
    {
        var row = _rows.FirstOrDefault(r => r.Id == id);
        try
        {
            var macro = await _api.GetMacroAsync(id);
            var doc = macro.SavedDoc ?? macro.DraftDoc;
            if (doc is null) return;
            var compiled = MacroCompiler.Compile(doc);
            var run = _runs.Start(compiled, new RunSettings { TargetExe = ExeFor(doc.TargetGame) });
            TrackRun(id, run);
            if (doc.Options.TriggerMode == TriggerModes.Hold && Hotkey.TryParse(doc.Options.HotkeyStart, out var key))
                WatchHold(id, run, key);
            Status = $"Started \"{doc.Name}\".";
        }
        catch (MacroCompileException ex)
        {
            Status = $"\"{row?.Name}\" can't run: {(ex.Issues.Count > 0 ? ex.Issues[0].Message : ex.Message)}";
            row?.SetRunning(false);
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException or InvalidOperationException)
        {
            Status = ex.Message;
            row?.SetRunning(false);
        }
    }

    /// <summary>The macro's own game when it names one we know, otherwise the target picked in Settings.</summary>
    private string? ExeFor(string? targetGame) =>
        targetGame is not null && _games.Games.FirstOrDefault(g => g.Game.Name == targetGame) is { } option
            ? option.Game.Exe
            : SelectedGame.Exe;

    private void TrackRun(Guid id, MacroRun run)
    {
        _runsById[id] = run;
        _rows.FirstOrDefault(r => r.Id == id)?.SetRunning(true);
    }

    private void OnLibraryRunState(MacroRun run)
    {
        foreach (var (id, r) in _runsById.ToList())
        {
            if (r != run || run.IsActive) continue;
            _runsById.Remove(id);
            if (_holdWatchers.Remove(id, out var timer)) timer.Stop();
            _rows.FirstOrDefault(x => x.Id == id)?.SetRunning(false);
        }
    }

    /// <summary>Hold mode: the macro runs only while its key is held down.</summary>
    private void WatchHold(Guid id, MacroRun run, Hotkey key)
    {
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Normal, (s, _) =>
        {
            if ((GetAsyncKeyState(key.VirtualKey) & 0x8000) != 0) return;
            ((DispatcherTimer)s!).Stop();
            run.Stop();
        }, Dispatcher.CurrentDispatcher);
        _holdWatchers[id] = timer;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>Why a key can't be a macro's start key, or null when it's free.</summary>
    internal string? HotkeyProblem(Hotkey h, Guid? forMacro)
    {
        if (h == RecordHotkey) return $"{h} is the Record key.";
        if (h == Hotkey.Panic) return $"{h} is the stop-everything key.";
        if (h == PlayHotkey) return $"{h} is the editor's Play key. Pick another key.";
        var clash = _rows.FirstOrDefault(r => r.Id != forMacro && Hotkey.TryParse(r.Hotkey, out var other) && other == h);
        if (clash is not null) return $"{h} already starts \"{clash.Name}\".";
        if (forMacro != _macroId && _macroId is not null && Hotkey.TryParse(EditorHotkey, out var editorKey) && editorKey == h)
            return $"{h} already starts \"{MacroName}\".";
        return null;
    }

    /// <summary>Registers every macro's start key (the open macro uses the editor's current key).</summary>
    private void RefreshHotkeys()
    {
        DisposeMacroHotkeys();
        var keys = _rows.Select(r => (r.Id, Key: r.Id == _macroId ? EditorHotkey : r.Hotkey)).ToList();
        if (_macroId is null && !string.IsNullOrEmpty(EditorHotkey)) keys.Add((Guid.Empty, EditorHotkey));
        foreach (var (id, text) in keys)
        {
            if (!Hotkey.TryParse(text, out var key) || key == RecordHotkey || key == PlayHotkey || key == Hotkey.Panic) continue;
            var row = _rows.FirstOrDefault(r => r.Id == id);
            try
            {
                var macroId = id;
                _macroHotkeys[id] = _hotkeys.Register(key, () => OnUi(() =>
                {
                    if (macroId == Guid.Empty) TogglePlay();
                    else ToggleMacro(macroId);
                }));
                if (row is not null) row.HotkeyError = "";
            }
            catch (HotkeyTakenException ex)
            {
                if (row is not null) row.HotkeyError = ex.Message;
                else EditorHotkeyError = ex.Message;
            }
            catch (ArgumentException)
            {
                // Same key listed twice (shouldn't happen; HotkeyProblem prevents it).
            }
        }
    }

    private void DisposeMacroHotkeys()
    {
        foreach (var r in _macroHotkeys.Values) r.Dispose();
        _macroHotkeys.Clear();
    }

    /// <summary>utility.runMacro: runs a saved macro by name alongside this one and waits for it to end.</summary>
    private async Task RunMacroByNameAsync(string name, CancellationToken ct)
    {
        if (!_nameToId.TryGetValue(name, out var id))
            throw new MacroFaultException($"There's no saved macro called \"{name}\".");
        var macro = await _api.GetMacroAsync(id, ct);
        var doc = macro.SavedDoc ?? macro.DraftDoc ?? throw new MacroFaultException($"\"{name}\" is empty.");
        doc.Options.RunAlongside = true; // never stop the macro that called it
        doc.Options.Repeat = new RepeatOptions { Mode = RepeatModes.Count, Count = 1 };
        var run = _runs.Start(MacroCompiler.Compile(doc), new RunSettings { TargetExe = ExeFor(doc.TargetGame) });
        await using (ct.Register(run.Stop))
            await run.Completion;
        if (run.State == RunState.Faulted)
            throw new MacroFaultException($"\"{name}\" stopped with an error: {run.FaultMessage}");
    }
}
