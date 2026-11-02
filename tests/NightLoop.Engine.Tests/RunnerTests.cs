using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Compiler;
using NightLoop.Engine.Runtime;

namespace NightLoop.Engine.Tests;

public class RunnerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static async Task<(MacroRun Run, FakeInput Input, FakeDelay Delay)> RunAsync(
        MacroDoc doc, RunSettings? settings = null, FakeWindows? windows = null, FakeDelay? delay = null)
    {
        var input = new FakeInput();
        delay ??= new FakeDelay();
        var run = new MacroRun(MacroCompiler.Compile(doc), new RunEnvironment { Input = input, Delay = delay, Windows = windows }, settings);
        run.Start();
        await run.Completion.WaitAsync(Timeout);
        return (run, input, delay);
    }

    [Fact]
    public async Task Key_press_sends_down_then_up_and_waits_hold_time()
    {
        var (run, input, delay) = await RunAsync(new MacroBuilder().Press("A", holdMs: 80).Wait(200).Build());

        Assert.Equal(RunState.Stopped, run.State);
        Assert.Equal(["down A", "up A"], input.Log);
        Assert.Equal(280, delay.TotalMs);
    }

    [Fact]
    public async Task Loop_runs_body_count_times()
    {
        var (_, input, _) = await RunAsync(new MacroBuilder().Loop(3).Press("A").End().Build());
        Assert.Equal(3, input.Log.Count(l => l == "down A"));
    }

    [Fact]
    public async Task Nested_loops_multiply()
    {
        var (_, input, _) = await RunAsync(new MacroBuilder().Loop(3).Loop(4).Press("A").End().End().Build());
        Assert.Equal(12, input.Log.Count(l => l == "down A"));
    }

    [Fact]
    public async Task If_else_takes_the_right_branch()
    {
        var doc = new MacroBuilder().If(MacroBuilder.Not(MacroBuilder.Focused())).Press("A").Else().Press("B").End().Build();
        var (_, input, _) = await RunAsync(doc, new RunSettings { TargetExe = "game.exe" }, new FakeWindows { Foreground = true });
        Assert.Equal(["down B", "up B"], input.Log);
    }

    [Fact]
    public async Task Repeat_option_runs_whole_macro_again()
    {
        var (run, input, _) = await RunAsync(new MacroBuilder().Press("A").Repeat(4).Build());
        Assert.Equal(4, input.Log.Count(l => l == "down A"));
        Assert.Equal(4, run.CompletedPasses);
    }

    [Fact]
    public async Task Stop_block_ends_the_macro_even_when_repeating_forever()
    {
        var (run, input, _) = await RunAsync(new MacroBuilder().Press("A").Add(BlockTypes.Stop).Press("B").Forever().Build());
        Assert.Equal(RunState.Stopped, run.State);
        Assert.Equal(["down A", "up A"], input.Log);
    }

    [Fact]
    public async Task Held_keys_are_released_when_the_run_ends()
    {
        var (_, input, _) = await RunAsync(new MacroBuilder().Down("W").Wait(100).Build());
        Assert.Equal(["down W", "up W"], input.Log);
    }

    [Fact]
    public async Task Stopping_mid_run_releases_held_keys()
    {
        var delay = new FakeDelay();
        var input = new FakeInput();
        var run = new MacroRun(MacroCompiler.Compile(new MacroBuilder().Down("W").Wait(1000).Forever().Build()),
            new RunEnvironment { Input = input, Delay = delay });
        run.Start();
        while (delay.TotalMs < 3000) await Task.Delay(1);
        run.Stop();
        await run.Completion.WaitAsync(Timeout);

        Assert.Equal(RunState.Stopped, run.State);
        Assert.Equal("up W", input.Log[^1]);
    }

    [Fact]
    public async Task Goto_loop_without_wait_faults_instead_of_hanging()
    {
        var (run, _, _) = await RunAsync(new MacroBuilder().Label("x").Goto("x").Build());
        Assert.Equal(RunState.Faulted, run.State);
        Assert.Contains("no wait", run.FaultMessage);
    }

    [Fact]
    public async Task Click_relative_to_game_window_is_offset_by_its_position()
    {
        var doc = new MacroBuilder().Add(BlockTypes.Click, new JsonObject { ["x"] = 10, ["y"] = 20, ["relativeTo"] = "gameWindow" }).Build();
        var (_, input, _) = await RunAsync(doc, new RunSettings { TargetExe = "game.exe" }, new FakeWindows());
        var log = input.Log;
        Assert.Equal(["move 110,220", "mdown Left", "mup Left"], log[^3..]);
        // It glided there in small steps instead of jumping.
        Assert.True(log.Count(l => l.StartsWith("move", StringComparison.Ordinal)) > 5);
    }

    [Fact]
    public async Task Input_waits_while_the_game_is_not_in_front()
    {
        var windows = new FakeWindows { Foreground = false };
        var delay = new FakeDelay();
        delay.OnDelay = () =>
        {
            if (Volatile.Read(ref windows.ForegroundChecks) > 5) windows.Foreground = true;
            return Task.CompletedTask;
        };
        var states = new List<RunState>();
        var input = new FakeInput();
        var run = new MacroRun(MacroCompiler.Compile(new MacroBuilder().Press("A").Build()),
            new RunEnvironment { Input = input, Delay = delay, Windows = windows }, new RunSettings { TargetExe = "game.exe" });
        run.StateChanged += (_, s) => { lock (states) states.Add(s); };
        run.Start();
        await run.Completion.WaitAsync(Timeout);

        Assert.Contains(RunState.WaitingForWindow, states);
        Assert.Equal(["down A", "up A"], input.Log);
    }

    [Fact]
    public async Task Pause_and_resume()
    {
        var delay = new FakeDelay();
        var input = new FakeInput();
        var run = new MacroRun(MacroCompiler.Compile(new MacroBuilder().Press("A").Wait(10).Forever().Build()),
            new RunEnvironment { Input = input, Delay = delay });
        run.Start();
        while (delay.TotalMs < 100) await Task.Delay(1);

        run.Pause();
        await WaitFor(() => run.State == RunState.Paused);
        var countWhilePaused = input.Log.Count;
        await Task.Delay(50);
        Assert.Equal(countWhilePaused, input.Log.Count);

        run.Resume();
        await WaitFor(() => input.Log.Count > countWhilePaused);
        run.Stop();
        await run.Completion.WaitAsync(Timeout);
        Assert.Equal(RunState.Stopped, run.State);
    }

    [Fact]
    public async Task Breakpoint_pauses_in_debug_and_step_runs_one_block()
    {
        var delay = new FakeDelay();
        var input = new FakeInput();
        var doc = new MacroBuilder().Press("A").Add(BlockTypes.Key, new() { ["key"] = "B" }, breakpoint: true).Press("C").Build();
        var run = new MacroRun(MacroCompiler.Compile(doc), new RunEnvironment { Input = input, Delay = delay }, new RunSettings { Debug = true });
        run.Start();

        await WaitFor(() => run.State == RunState.AtBreakpoint);
        Assert.Equal(1, run.CurrentIndex);
        Assert.Equal(["down A", "up A"], input.Log);

        run.StepOnce();
        await WaitFor(() => run.State == RunState.AtBreakpoint && run.CurrentIndex == 2);
        Assert.Equal(["down A", "up A", "down B", "up B"], input.Log);

        run.Continue();
        await run.Completion.WaitAsync(Timeout);
        Assert.Equal("up C", input.Log[^1]);
    }

    [Fact]
    public async Task Start_paused_holds_before_the_first_block_then_steps_one_at_a_time()
    {
        var input = new FakeInput();
        var doc = new MacroBuilder().Press("A").Press("B").Build();
        var run = new MacroRun(MacroCompiler.Compile(doc), new RunEnvironment { Input = input, Delay = new FakeDelay() },
            new RunSettings { StartPaused = true });
        run.Start();

        await WaitFor(() => run.State == RunState.AtBreakpoint);
        Assert.Equal(0, run.CurrentIndex);
        Assert.Empty(input.Log);

        run.StepOnce();
        await WaitFor(() => run.State == RunState.AtBreakpoint && run.CurrentIndex == 1);
        Assert.Equal(["down A", "up A"], input.Log);

        run.Continue();
        await run.Completion.WaitAsync(Timeout);
        Assert.Equal("up B", input.Log[^1]);
    }

    [Fact]
    public async Task Starting_inside_a_hold_holds_the_key_first()
    {
        // Ctrl down, T, Ctrl up: playing from T still opens a tab.
        var doc = new MacroBuilder().Down("LeftCtrl").Press("T").Up("LeftCtrl").Build();
        var (_, input, _) = await RunAsync(doc, new RunSettings { StartIndex = 1 });
        Assert.Equal(["down LeftCtrl", "down T", "up T", "up LeftCtrl"], input.Log);
    }

    [Fact]
    public async Task Starting_after_a_hold_ends_holds_nothing()
    {
        var doc = new MacroBuilder().Down("LeftCtrl").Up("LeftCtrl").Press("T").Build();
        var (_, input, _) = await RunAsync(doc, new RunSettings { StartIndex = 2 });
        Assert.Equal(["down T", "up T"], input.Log);
    }

    [Fact]
    public async Task A_breakpoint_lets_go_of_held_keys_and_presses_them_again_on_continue()
    {
        var input = new FakeInput();
        var doc = new MacroBuilder().Down("LeftCtrl").Add(BlockTypes.Key, new() { ["key"] = "T" }, breakpoint: true).Up("LeftCtrl").Build();
        var run = new MacroRun(MacroCompiler.Compile(doc), new RunEnvironment { Input = input, Delay = new FakeDelay() }, new RunSettings { Debug = true });
        run.Start();
        await WaitFor(() => run.State == RunState.AtBreakpoint);
        Assert.Equal(["down LeftCtrl", "up LeftCtrl"], input.Log);

        run.Continue();
        await run.Completion.WaitAsync(Timeout);
        Assert.Equal(["down LeftCtrl", "up LeftCtrl", "down LeftCtrl", "down T", "up T", "up LeftCtrl"], input.Log);
    }

    [Fact]
    public async Task Open_window_starts_the_program_then_brings_its_window_to_the_front()
    {
        var windows = new FakeWindows { Running = false };
        var doc = new MacroBuilder().Add(BlockTypes.WindowOpen, new() { ["path"] = @"C:\Games\Game.exe", ["args"] = "-windowed" }).Build();
        var (run, _, _) = await RunAsync(doc, windows: windows);
        Assert.Equal(RunState.Stopped, run.State);
        Assert.Equal([@"launch C:\Games\Game.exe -windowed", "focus Game.exe"], windows.Calls);
    }

    [Fact]
    public async Task Open_window_focuses_the_program_when_it_is_already_running()
    {
        var windows = new FakeWindows { Running = true };
        var doc = new MacroBuilder().Add(BlockTypes.WindowOpen, new() { ["path"] = @"C:\Games\Game.exe" }).Build();
        await RunAsync(doc, windows: windows);
        Assert.Equal(["focus Game.exe"], windows.Calls);
    }

    [Fact]
    public async Task Open_window_can_open_another_copy()
    {
        var windows = new FakeWindows { Running = true };
        var doc = new MacroBuilder().Add(BlockTypes.WindowOpen, new() { ["path"] = @"C:\Tools\notepad.exe", ["ifRunning"] = "new", ["wait"] = false }).Build();
        await RunAsync(doc, windows: windows);
        Assert.Equal([@"launch C:\Tools\notepad.exe"], windows.Calls);
    }

    [Fact]
    public async Task Open_window_faults_when_no_window_shows_up_in_time()
    {
        var windows = new FakeWindows { Running = false, OnLaunch = _ => null };
        var doc = new MacroBuilder().Add(BlockTypes.WindowOpen, new() { ["path"] = @"C:\Games\Game.exe", ["timeoutMs"] = 1000 }).Build();
        var (run, _, delay) = await RunAsync(doc, windows: windows);
        Assert.Equal(RunState.Faulted, run.State);
        Assert.Equal("Game.exe didn't open a window within 1 s.", run.FaultMessage);
        Assert.True(delay.TotalMs >= 1000);
    }

    [Fact]
    public async Task Open_window_faults_with_the_reason_when_windows_cannot_start_it()
    {
        var windows = new FakeWindows { Running = false, OnLaunch = p => throw new InvalidOperationException($"Game.exe wasn't found at {p}.") };
        var doc = new MacroBuilder().Add(BlockTypes.WindowOpen, new() { ["path"] = @"C:\Games\Game.exe" }).Build();
        var (run, _, _) = await RunAsync(doc, windows: windows);
        Assert.Equal(RunState.Faulted, run.State);
        Assert.Equal(@"Game.exe wasn't found at C:\Games\Game.exe.", run.FaultMessage);
    }

    [Fact]
    public async Task Wait_for_window_until_open_does_not_need_it_in_front()
    {
        var windows = new FakeWindows { Running = true, Foreground = false };
        var doc = new MacroBuilder().Add(BlockTypes.WaitForWindow, new() { ["process"] = "chat.exe", ["until"] = "open", ["timeoutMs"] = 500 }).Build();
        var (run, _, _) = await RunAsync(doc, windows: windows);
        Assert.Equal(RunState.Stopped, run.State);
    }

    [Fact]
    public void Open_window_blocks_validate()
    {
        var ok = new MacroBuilder().Add(BlockTypes.WindowOpen, new() { ["path"] = @"C:\Games\Game.exe", ["ifRunning"] = "new" }).Build();
        Assert.Empty(MacroValidator.Validate(ok));
        var missing = new MacroBuilder().Add(BlockTypes.WindowOpen, new()).Build();
        Assert.Contains(MacroValidator.Validate(missing), e => e.Message == "Pick the program to open.");
    }

    [Fact]
    public async Task Run_from_starts_at_the_chosen_block()
    {
        var (_, input, _) = await RunAsync(new MacroBuilder().Press("A").Press("B").Build(), new RunSettings { StartIndex = 1 });
        Assert.Equal(["down B", "up B"], input.Log);
    }

    [Fact]
    public async Task Vision_conditions_fault_with_a_clear_message_for_now()
    {
        var doc = new MacroBuilder().If(new JsonObject { ["kind"] = "textVisible", ["text"] = "hi" }).Press("A").End().Build();
        var (run, _, _) = await RunAsync(doc);
        Assert.Equal(RunState.Faulted, run.State);
        Assert.Equal("b1", run.FaultBlockId);
    }

    [Fact]
    public async Task Run_manager_stops_others_when_a_macro_does_not_run_alongside()
    {
        var delay = new FakeDelay();
        var manager = new RunManager(new FakeInput(), delay: delay);
        var first = manager.Start(MacroCompiler.Compile(new MacroBuilder().Wait(10).Forever().Build()));
        var soloDoc = new MacroBuilder().Wait(10).Forever().Build();
        soloDoc.Options.RunAlongside = false;
        var solo = manager.Start(MacroCompiler.Compile(soloDoc));

        await first.Completion.WaitAsync(Timeout);
        Assert.Equal(RunState.Stopped, first.State);
        Assert.True(solo.IsActive);

        await manager.StopAllAsync().WaitAsync(Timeout);
        Assert.False(solo.IsActive);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(1);
        }
    }
}
