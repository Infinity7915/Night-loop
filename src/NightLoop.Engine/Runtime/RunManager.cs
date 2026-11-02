using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Compiler;
using NightLoop.Engine.Input;
using NightLoop.Engine.Screen;
using NightLoop.Engine.Window;

namespace NightLoop.Engine.Runtime;

/// <summary>
/// Owns every running macro. Enforces run-alongside and the 8-at-once cap, shares one input lock,
/// and stops everything for the panic key or sign-out.
/// </summary>
public sealed class RunManager(IInputSender input, IWindowService? windows = null, IDelay? delay = null)
{
    private readonly object _gate = new();
    private readonly List<MacroRun> _runs = [];
    private readonly SemaphoreSlim _inputLock = new(1, 1);

    public event Action<MacroRun, RunState>? RunStateChanged;
    public event Action<MacroRun, int>? RunStepChanged;
    public Action<string>? Log { get; set; }
    public Action<string>? Notify { get; set; }
    /// <summary>utility.playSound.</summary>
    public Action<string>? PlaySound { get; set; }
    /// <summary>utility.runMacro: the app finds the macro by name, runs it and completes when it ends.</summary>
    public Func<string, CancellationToken, Task>? RunMacro { get; set; }
    /// <summary>Screen capture for pixel, picture and text blocks and screenshots.</summary>
    public IScreenCapture? Screen { get; set; }
    /// <summary>Text recognition for the text blocks.</summary>
    public ITextReader? Text { get; set; }
    /// <summary>Key state for Wait for key and "Key held".</summary>
    public Func<int, bool>? IsKeyDown { get; set; }
    /// <summary>utility.screenshot.</summary>
    public Action<ScreenImage, string>? SaveScreenshot { get; set; }

    public IReadOnlyList<MacroRun> ActiveRuns
    {
        get { lock (_gate) return _runs.Where(r => r.IsActive).ToArray(); }
    }

    public MacroRun? Find(string name)
    {
        lock (_gate) return _runs.LastOrDefault(r => r.IsActive && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Starts a macro. If it or any running macro doesn't allow running alongside, the others stop first.</summary>
    public MacroRun Start(CompiledMacro macro, RunSettings? settings = null)
    {
        var env = new RunEnvironment
        {
            Input = input,
            Windows = windows,
            Delay = delay ?? PreciseDelay.Instance,
            InputLock = _inputLock,
            FindRun = Find,
            ActiveRuns = () => ActiveRuns,
            Log = Log,
            Notify = Notify,
            PlaySound = PlaySound,
            RunMacro = RunMacro,
            Screen = Screen,
            Text = Text,
            IsKeyDown = IsKeyDown,
            SaveScreenshot = SaveScreenshot,
        };
        var run = new MacroRun(macro, env, settings);
        run.StateChanged += (r, s) => RunStateChanged?.Invoke(r, s);
        run.StepChanged += (r, i) => RunStepChanged?.Invoke(r, i);

        MacroRun[] toStop;
        lock (_gate)
        {
            _runs.RemoveAll(r => !r.IsActive);
            var active = _runs.ToArray();
            toStop = !macro.Options.RunAlongside || active.Any(r => !r.Macro.Options.RunAlongside) ? active : [];
            if (active.Length - toStop.Length >= MacroLimits.MaxRunsAtOnce)
                throw new InvalidOperationException($"At most {MacroLimits.MaxRunsAtOnce} macros can run at once.");
            _runs.Add(run);
        }
        foreach (var r in toStop) r.Stop();
        run.Start();
        return run;
    }

    /// <summary>The panic key: stops every macro at once. Each run releases its own keys as it ends.</summary>
    public Task StopAllAsync()
    {
        MacroRun[] runs;
        lock (_gate) runs = [.. _runs];
        foreach (var r in runs) r.Stop();
        return Task.WhenAll(runs.Select(r => r.Completion));
    }
}
