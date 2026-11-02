using System.Diagnostics;
using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Blocks;
using NightLoop.Engine.Compiler;
using NightLoop.Engine.Input;

namespace NightLoop.Engine.Runtime;

/// <summary>
/// One running macro on its own background task (schema section 7, "Runner").
/// Every way out of the run (stop, finish, fault) releases all keys and buttons it pressed.
/// </summary>
public sealed class MacroRun
{
    private readonly RunEnvironment _env;
    private readonly RunSettings _settings;
    private readonly TrackingInputSender _input;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private TaskCompletionSource? _resume;
    private bool _pauseRequested;
    private bool _stepMode;
    private Task? _task;

    public MacroRun(CompiledMacro macro, RunEnvironment environment, RunSettings? settings = null)
    {
        Macro = macro;
        _env = environment;
        _settings = settings ?? new RunSettings();
        _input = new TrackingInputSender(environment.Input);
    }

    public CompiledMacro Macro { get; }
    public string Name => Macro.Name;
    public RunState State { get; private set; } = RunState.Idle;
    /// <summary>Index of the block running now, for the timeline highlight. -1 when not running.</summary>
    public int CurrentIndex { get; private set; } = -1;
    public string? FaultMessage { get; private set; }
    public string? FaultBlockId { get; private set; }
    /// <summary>Passes finished so far.</summary>
    public int CompletedPasses { get; private set; }

    public event Action<MacroRun, RunState>? StateChanged;
    public event Action<MacroRun, int>? StepChanged;

    /// <summary>Completes when the run ends. Never throws; check <see cref="State"/>.</summary>
    public Task Completion => _task ?? Task.CompletedTask;

    public bool IsActive => State is RunState.Running or RunState.Paused or RunState.AtBreakpoint or RunState.WaitingForWindow;

    public void Start()
    {
        lock (_gate)
        {
            if (_task is not null) throw new InvalidOperationException("A run can only be started once.");
            SetState(RunState.Running);
            _task = Task.Run(RunAsync);
        }
    }

    public void Stop() => _cts.Cancel();

    public void Pause()
    {
        lock (_gate)
        {
            if (!IsActive) return;
            _pauseRequested = true;
            _resume ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _pauseRequested = false;
            _stepMode = false;
            _resume?.TrySetResult();
            _resume = null;
        }
    }

    /// <summary>At a breakpoint: carry on to the next breakpoint.</summary>
    public void Continue() => Resume();

    /// <summary>At a breakpoint or paused: run one block, then pause again.</summary>
    public void StepOnce()
    {
        lock (_gate)
        {
            _stepMode = true;
            _pauseRequested = false;
            _resume?.TrySetResult();
            _resume = null;
        }
    }

    internal void SetState(RunState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private async Task RunAsync()
    {
        var ctx = new RunContext(this, _env, _settings, _input, _cts.Token);
        try
        {
            var repeat = Macro.Options.Repeat;
            var once = Macro.Options.TriggerMode == TriggerModes.Once;
            var clock = Stopwatch.StartNew();
            var start = Math.Clamp(_settings.StartIndex, 0, Math.Max(0, Macro.Steps.Length - 1));

            while (true)
            {
                var stopped = await RunPassAsync(ctx, start);
                CompletedPasses++;
                start = 0;
                if (stopped || once) break;
                var more = repeat.Mode switch
                {
                    RepeatModes.Forever => true,
                    RepeatModes.Duration => clock.Elapsed.TotalSeconds < (repeat.Duration ?? 0),
                    _ => CompletedPasses < (repeat.Count ?? 1),
                };
                if (!more) break;
                if (Macro.Steps.Length == 0) break;
            }
            Finish(RunState.Stopped);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            Finish(RunState.Stopped);
        }
        catch (Exception ex)
        {
            FaultMessage = ex is MacroFaultException ? ex.Message : $"Unexpected error: {ex.Message}";
            FaultBlockId = CurrentIndex >= 0 && CurrentIndex < Macro.Steps.Length ? Macro.Steps[CurrentIndex].Block.Id : null;
            _env.Log?.Invoke($"{Name} faulted at {FaultBlockId}: {ex}");
            Finish(RunState.Faulted);
        }
    }

    private void Finish(RunState state)
    {
        _input.ReleaseAll();
        CurrentIndex = -1;
        StepChanged?.Invoke(this, -1);
        SetState(state);
    }

    /// <summary>Runs the program once from <paramref name="pc"/>. Returns true when a logic.stop ended the macro.</summary>
    private async Task<bool> RunPassAsync(RunContext ctx, int pc)
    {
        var steps = Macro.Steps;
        var loopCounters = new Dictionary<int, int>();
        var stepsWithoutWork = 0;

        while (pc >= 0 && pc < steps.Length)
        {
            ctx.Token.ThrowIfCancellationRequested();
            var step = steps[pc];
            await GateAsync(step);

            CurrentIndex = pc;
            StepChanged?.Invoke(this, pc);
            ctx.DidWork = false;
            var next = pc + 1;

            switch (step.Kind)
            {
                case StepKind.Action:
                    await BlockExecutor.ExecuteAsync(step.Block, ctx);
                    break;
                case StepKind.If:
                    if (!Evaluate(step.Block, ctx)) next = step.Jump;
                    break;
                case StepKind.Jump or StepKind.Goto:
                    next = step.Jump;
                    break;
                case StepKind.LoopStart:
                    loopCounters[pc] = step.Block.GetInt("count", 1);
                    break;
                case StepKind.LoopEnd:
                    var remaining = loopCounters.GetValueOrDefault(step.Jump, 1) - 1;
                    if (remaining > 0)
                    {
                        loopCounters[step.Jump] = remaining;
                        next = step.Jump + 1;
                    }
                    else loopCounters.Remove(step.Jump);
                    break;
                case StepKind.RepeatStart:
                    loopCounters.TryAdd(pc, 0);
                    break;
                case StepKind.RepeatEnd:
                    var opener = steps[step.Jump].Block;
                    var passes = loopCounters.GetValueOrDefault(step.Jump) + 1;
                    var max = opener.GetInt("maxIterations", MacroLimits.CountMax);
                    if (!Evaluate(opener, ctx) && passes < max)
                    {
                        loopCounters[step.Jump] = passes;
                        next = step.Jump + 1;
                    }
                    else loopCounters.Remove(step.Jump);
                    break;
                case StepKind.Stop:
                    return true;
            }

            stepsWithoutWork = ctx.DidWork ? 0 : stepsWithoutWork + 1;
            if (stepsWithoutWork >= _settings.RunawayStepLimit)
                throw new MacroFaultException("Loop with no wait: add a Wait block inside the loop.");

            pc = next;
        }
        return false;
    }

    /// <summary>Holds here while paused, at a breakpoint, or in step mode.</summary>
    private async Task GateAsync(Step step)
    {
        Task? wait = null;
        var state = RunState.Paused;
        lock (_gate)
        {
            var atBreakpoint = _settings.Debug && step.Block.Breakpoint && !_pauseRequested && _resume is null;
            if (_stepMode || atBreakpoint)
            {
                _stepMode = false;
                _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                state = RunState.AtBreakpoint;
            }
            if (_resume is not null) wait = _resume.Task;
        }
        if (wait is null) return;

        _input.ReleaseAll();
        CurrentIndex = step.Index;
        StepChanged?.Invoke(this, step.Index);
        SetState(state);
        await wait.WaitAsync(_cts.Token);
        SetState(RunState.Running);
    }

    private static bool Evaluate(Block block, RunContext ctx)
    {
        var condition = block.GetObject("condition") ?? throw new MacroFaultException("This block needs a condition.");
        return ctx.Environment.Conditions.Evaluate((JsonObject)condition, ctx);
    }
}
