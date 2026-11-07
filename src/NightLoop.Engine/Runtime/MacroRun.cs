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
    private readonly List<MacroRun> _resumeWhenDone = [];
    private TaskCompletionSource? _resume;
    private CancellationTokenSource? _blockCts;
    private bool _pauseRequested;
    private bool _interrupted;
    private bool _restartRequested;
    private bool _stepMode;
    private Task? _task;

    public MacroRun(CompiledMacro macro, RunEnvironment environment, RunSettings? settings = null)
    {
        Macro = macro;
        _env = environment;
        _settings = settings ?? new RunSettings();
        _input = new TrackingInputSender(environment.Input);
        _stepMode = _settings.StartPaused;
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

    /// <summary>
    /// Pauses before the next block. <paramref name="immediate"/> also interrupts the block running now (a long wait,
    /// a held key); it runs again from its start on resume.
    /// </summary>
    public void Pause(bool immediate = false)
    {
        CancellationTokenSource? interrupt = null;
        lock (_gate)
        {
            if (!IsActive) return;
            _pauseRequested = true;
            _resume ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (immediate && _blockCts is { } cts)
            {
                _interrupted = true;
                interrupt = cts;
            }
        }
        try { interrupt?.Cancel(); }
        catch (ObjectDisposedException) { /* the block just finished */ }
    }

    /// <summary>Carries on from where it paused, or from the first block when <paramref name="fromStart"/> is set.</summary>
    public void Resume(bool fromStart = false)
    {
        lock (_gate)
        {
            if (fromStart && IsActive) _restartRequested = true;
            _pauseRequested = false;
            _stepMode = false;
            _resume?.TrySetResult();
            _resume = null;
        }
    }

    /// <summary>Pause macro's "Resume when this macro ends": resumes <paramref name="other"/> when this run ends.</summary>
    internal void ResumeWhenDone(MacroRun other)
    {
        lock (_gate)
        {
            if (!_resumeWhenDone.Contains(other)) _resumeWhenDone.Add(other);
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
            // Starting in the middle of a hold (Ctrl down … Ctrl up): hold what the blocks before it left held.
            var holdFirst = start > 0 ? InputBlocks.HeldAfter(Macro.Steps.Take(start).Where(s => s.Kind == StepKind.Action).Select(s => s.Block)) : default;

            while (true)
            {
                var end = await RunPassAsync(ctx, start, holdFirst);
                start = 0;
                holdFirst = default;
                if (end == PassEnd.Restart) continue;
                CompletedPasses++;
                if (end == PassEnd.Stopped || once) break;
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

        MacroRun[] paused;
        lock (_gate)
        {
            paused = [.. _resumeWhenDone];
            _resumeWhenDone.Clear();
        }
        foreach (var other in paused)
            if (other.State == RunState.Paused) other.Resume();
    }

    private enum PassEnd { Finished, Stopped, Restart }

    /// <summary>Runs the program once from <paramref name="pc"/>.</summary>
    private async Task<PassEnd> RunPassAsync(RunContext ctx, int pc, InputBlocks.Held holdFirst = default)
    {
        var steps = Macro.Steps;
        var loopCounters = new Dictionary<int, int>();
        var stepsWithoutWork = 0;

        while (pc >= 0 && pc < steps.Length)
        {
            _cts.Token.ThrowIfCancellationRequested();
            var step = steps[pc];
            await GateAsync(step);
            if (TakeRestart()) return PassEnd.Restart;
            if (holdFirst.Any)
            {
                Press(holdFirst);
                holdFirst = default;
            }

            CurrentIndex = pc;
            StepChanged?.Invoke(this, pc);
            ctx.DidWork = false;
            var next = pc + 1;

            switch (step.Kind)
            {
                case StepKind.Action:
                    var ended = await RunBlockAsync(step.Block, ctx);
                    // Settings › Macros: wait after clicks / after every block.
                    if (ended != BlockEnd.Interrupted && _env.Waits is { } waits && waits.After(step.Block, ended == BlockEnd.Done) is > 0 and var extra)
                        await ctx.PauseExactAsync(extra);
                    switch (ended)
                    {
                        case BlockEnd.Interrupted:
                            next = pc; // paused right away: run this block again on resume
                            ctx.DidWork = true;
                            break;
                        case BlockEnd.Done when BlockTypes.HasAfterFound(step.Block.Type):
                            switch (step.Block.GetString("afterFound", "next"))
                            {
                                case "exitLoop":
                                    if (EnclosingLoopEnd(steps, pc) is not { } loopEnd)
                                    {
                                        _env.Log?.Invoke($"{Name}: \"{step.Block.Type}\" found its target outside a loop, so the macro stopped (After found: leave loop).");
                                        return PassEnd.Stopped;
                                    }
                                    loopCounters.Remove(steps[loopEnd].Jump);
                                    next = loopEnd + 1;
                                    break;
                                case "goto" when step.Block.GetString("foundLabel") is { } label && Macro.Labels.TryGetValue(label, out var target):
                                    next = target;
                                    break;
                                case "stop":
                                    _env.Log?.Invoke($"{Name}: \"{step.Block.Type}\" found its target, so the macro stopped (After found: stop).");
                                    return PassEnd.Stopped;
                            }
                            break;
                        case BlockEnd.Failed:
                            switch (step.Block.GetString("onFail", "continue"))
                            {
                                case "retry":
                                    await ctx.PauseExactAsync(RetryDelayMs);
                                    next = pc;
                                    break;
                                case "goto" when step.Block.GetString("failLabel") is { } label && Macro.Labels.TryGetValue(label, out var target):
                                    next = target;
                                    break;
                                case "stop":
                                    _env.Log?.Invoke($"{Name}: \"{step.Block.Type}\" didn't find its target, so the macro stopped (On fail: stop).");
                                    return PassEnd.Stopped;
                            }
                            break;
                    }
                    break;
                case StepKind.If:
                    if (!await EvaluateAsync(step.Block, ctx)) next = step.Jump;
                    break;
                case StepKind.Jump or StepKind.Goto:
                    next = step.Jump;
                    break;
                case StepKind.LoopStart:
                    loopCounters[pc] = step.Block.GetBool("forever") == true ? Forever : step.Block.GetInt("count", 1);
                    break;
                case StepKind.LoopEnd:
                    var counter = loopCounters.GetValueOrDefault(step.Jump, 1);
                    if (counter == Forever)
                    {
                        next = step.Jump + 1;
                        break;
                    }
                    var remaining = counter - 1;
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
                    if (!await EvaluateAsync(opener, ctx) && (max <= 0 || passes < max))
                    {
                        loopCounters[step.Jump] = passes;
                        next = step.Jump + 1;
                    }
                    else loopCounters.Remove(step.Jump);
                    break;
                case StepKind.Stop:
                    return PassEnd.Stopped;
            }

            stepsWithoutWork = ctx.DidWork ? 0 : stepsWithoutWork + 1;
            if (stepsWithoutWork >= _settings.RunawayStepLimit)
                throw new MacroFaultException("Loop with no wait: add a Wait block inside the loop.");

            pc = next;
        }
        return PassEnd.Finished;
    }

    /// <summary>The End of the innermost Loop or Repeat until around <paramref name="pc"/>, or null when it isn't in one.</summary>
    private static int? EnclosingLoopEnd(System.Collections.Immutable.ImmutableArray<Step> steps, int pc)
    {
        // Loops nest, so the first loop end after pc that opened before pc is the innermost one around it.
        for (var i = pc + 1; i < steps.Length; i++)
            if (steps[i].Kind is StepKind.LoopEnd or StepKind.RepeatEnd && steps[i].Jump < pc) return i;
        return null;
    }

    private const int Forever = -1;
    /// <summary>"On fail: retry" waits this long before looking again.</summary>
    private const int RetryDelayMs = 250;

    private enum BlockEnd { Done, Failed, Interrupted }

    /// <summary>Runs one action block with its own cancellation, so a "right away" pause can cut it short.</summary>
    private async Task<BlockEnd> RunBlockAsync(Block block, RunContext ctx)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        lock (_gate) _blockCts = cts;
        ctx.Token = cts.Token;
        try
        {
            return await BlockExecutor.ExecuteAsync(block, ctx) ? BlockEnd.Done : BlockEnd.Failed;
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested && TakeInterrupt())
        {
            _input.ReleaseAll();
            return BlockEnd.Interrupted;
        }
        finally
        {
            lock (_gate) _blockCts = null;
            ctx.Token = _cts.Token;
        }
    }

    private bool TakeInterrupt()
    {
        lock (_gate)
        {
            var was = _interrupted;
            _interrupted = false;
            return was;
        }
    }

    private bool TakeRestart()
    {
        lock (_gate)
        {
            var was = _restartRequested;
            _restartRequested = false;
            return was;
        }
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

        // Let go while paused so nothing stays stuck down, and press it all again before carrying on.
        var held = new InputBlocks.Held([.. _input.HeldKeys], [.. _input.HeldButtons]);
        _input.ReleaseAll();
        CurrentIndex = step.Index;
        StepChanged?.Invoke(this, step.Index);
        SetState(state);
        await wait.WaitAsync(_cts.Token);
        SetState(RunState.Running);
        Press(held);
    }

    private void Press(InputBlocks.Held held)
    {
        foreach (var vk in held.Keys ?? []) _input.KeyDown(vk);
        foreach (var button in held.Buttons ?? []) _input.MouseDown(button);
    }

    private static Task<bool> EvaluateAsync(Block block, RunContext ctx)
    {
        var condition = block.GetObject("condition") ?? throw new MacroFaultException("This block needs a condition.");
        return ctx.Environment.Conditions.EvaluateAsync(condition, ctx);
    }
}
