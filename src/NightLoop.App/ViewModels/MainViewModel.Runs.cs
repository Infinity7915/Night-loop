using System.Runtime.InteropServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NightLoop.Client;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Compiler;
using NightLoop.Engine.Hotkeys;
using NightLoop.Engine.Runtime;
using NightLoop.LocalData;

namespace NightLoop.App.ViewModels;

/// <summary>
/// Running several macros at once (schema section 7, "Run alongside"): each saved macro's own start/stop key,
/// the on/off switches in the list, Hold trigger mode, and the Run macro block.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<Guid, MacroRun> _runsById = [];
    private readonly List<IDisposable> _macroHotkeys = [];
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
        if (_rows.FirstOrDefault(r => r.Id == id) is { IsAndroid: true } android)
        {
            Status = $"\"{android.Name}\" is an Android macro. Play it in Night-loop for Android.";
            android.SetRunning(false);
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
        GamesStore.CurrentName(targetGame) is { } name && _games.Games.FirstOrDefault(g => g.Game.Name == name) is { } option
            ? option.Game.Resolve(TargetGamesViewModel.IsRunning)
            : TargetExe;

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

    partial void OnCapturingRowChanged(SavedMacroRow? value) => OnCaptureChanged();

    /// <summary>Why a key can't be a macro's start key, or null when it's free. Macros may share a start key (it starts them together).</summary>
    internal string? HotkeyProblem(Hotkey h, Guid? forMacro)
    {
        var key = KeyText(h.ToString());
        if (h == Hotkey.Panic) return $"{key} is the stop-everything key.";
        if (h == PlayHotkey) return $"{key} is the editor's Play key. Pick another key.";
        if (ShortcutFor(h) is { } shortcut) return $"{key} is the editor's {shortcut.Label} shortcut. Pick another key, or change the shortcut in Settings › Macros.";
        return null;
    }

    /// <summary>
    /// Registers every macro's start key (the open macro uses the editor's current key). Several macros may share a
    /// key: Windows takes one registration per combo, so the key starts (or stops) all of them together.
    /// </summary>
    private void RefreshHotkeys()
    {
        DisposeMacroHotkeys();
        if (IsCapturingKey) return; // picking a key: OnCaptureChanged registers them again after
        var keys = _rows.Where(r => !r.IsAndroid).Select(r => (r.Id, Key: r.Id == _macroId ? EditorHotkey : r.Hotkey)).ToList();
        if (_macroId is null && !string.IsNullOrEmpty(EditorHotkey)) keys.Add((Guid.Empty, EditorHotkey));
        var byKey = keys
            .Select(k => (k.Id, Ok: Hotkey.TryParse(k.Key, out var parsed), Key: parsed))
            .Where(k => k.Ok && k.Key != PlayHotkey && k.Key != Hotkey.Panic && ShortcutFor(k.Key) is null)
            .GroupBy(k => k.Key, k => k.Id);
        // A start key that is also an editor shortcut (Settings › Macros) doesn't start the macro until one changes.
        foreach (var row in _rows)
        {
            if (ShortcutClash(row.Id == _macroId ? EditorHotkey : row.Hotkey) is { } clash) row.HotkeyError = clash;
            else if (row.HotkeyError.Contains("editor's", StringComparison.Ordinal)) row.HotkeyError = "";
        }
        if (ShortcutClash(EditorHotkey) is { } mine) EditorHotkeyError = mine;
        else if (EditorHotkeyError.Contains("editor's", StringComparison.Ordinal)) EditorHotkeyError = "";
        foreach (var group in byKey)
        {
            var ids = group.Distinct().ToList();
            var rows = _rows.Where(r => ids.Contains(r.Id)).ToList();
            try
            {
                _macroHotkeys.Add(_hotkeys.Register(group.Key, () => OnUi(() =>
                {
                    foreach (var macroId in ids)
                    {
                        if (macroId == Guid.Empty) TogglePlay();
                        else ToggleMacro(macroId);
                    }
                })));
                foreach (var row in rows) row.HotkeyError = "";
            }
            catch (HotkeyTakenException ex)
            {
                foreach (var row in rows) row.HotkeyError = ex.Message;
                if (ids.Contains(Guid.Empty) || (_macroId is { } open && ids.Contains(open))) EditorHotkeyError = ex.Message;
            }
        }
    }

    private void DisposeMacroHotkeys()
    {
        foreach (var r in _macroHotkeys) r.Dispose();
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
