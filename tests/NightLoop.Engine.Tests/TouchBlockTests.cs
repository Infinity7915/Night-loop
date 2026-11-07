using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Compiler;
using NightLoop.Engine.Input;
using NightLoop.Engine.Runtime;

namespace NightLoop.Engine.Tests;

internal sealed class FakeTouch : ITouchSender
{
    public List<string> Log { get; } = [];
    public string? ForegroundApp { get; set; }
    public HashSet<string> Installed { get; } = ["com.example.game"];

    public Task TapAsync(int x, int y, int holdMs, CancellationToken ct)
    {
        Log.Add($"tap {x},{y} {holdMs}");
        return Task.CompletedTask;
    }

    public Task SwipeAsync(int fromX, int fromY, int toX, int toY, int durationMs, CancellationToken ct)
    {
        Log.Add($"swipe {fromX},{fromY}->{toX},{toY} {durationMs}");
        return Task.CompletedTask;
    }

    public void PressButton(string button) => Log.Add($"button {button}");

    public bool OpenApp(string package)
    {
        Log.Add($"open {package}");
        if (!Installed.Contains(package)) return false;
        ForegroundApp = package;
        return true;
    }
}

public class TouchBlockTests
{
    private static async Task<MacroRun> RunAsync(MacroDoc doc, ITouchSender? touch)
    {
        doc.Platform = MacroPlatforms.Android;
        var run = new MacroRun(MacroCompiler.Compile(doc), new RunEnvironment { Input = new FakeInput(), Delay = new FakeDelay(), Touch = touch });
        run.Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        return run;
    }

    private static JsonObject Point(int x, int y) => new() { ["x"] = x, ["y"] = y };

    [Fact]
    public async Task Touch_blocks_reach_the_touch_sender()
    {
        var touch = new FakeTouch();
        var doc = new MacroBuilder()
            .Add(BlockTypes.Tap, new() { ["x"] = 10, ["y"] = 20, ["count"] = 2 })
            .Add(BlockTypes.LongPress, new() { ["x"] = 5, ["y"] = 6 })
            .Add(BlockTypes.Swipe, new() { ["from"] = Point(1, 2), ["to"] = Point(3, 4), ["durationMs"] = 500 })
            .Add(BlockTypes.DeviceButton, new() { ["button"] = "home" })
            .Build();
        var run = await RunAsync(doc, touch);
        Assert.Equal(RunState.Stopped, run.State);
        Assert.Equal(["tap 10,20 50", "tap 10,20 50", "tap 5,6 600", "swipe 1,2->3,4 500", "button home"], touch.Log);
    }

    [Fact]
    public async Task Open_app_waits_for_it_and_app_in_front_sees_it()
    {
        var touch = new FakeTouch();
        var doc = new MacroBuilder()
            .Add(BlockTypes.AppOpen, new() { ["package"] = "com.example.game" })
            .If(new JsonObject { ["kind"] = "appInFront", ["package"] = "com.example.game" })
            .Add(BlockTypes.DeviceButton, new() { ["button"] = "back" })
            .End()
            .Build();
        await RunAsync(doc, touch);
        Assert.Equal(["open com.example.game", "button back"], touch.Log);
    }

    [Fact]
    public async Task Missing_apps_and_slow_apps_fault()
    {
        var missing = await RunAsync(new MacroBuilder().Add(BlockTypes.AppOpen, new() { ["package"] = "com.not.here" }).Build(), new FakeTouch());
        Assert.Equal(RunState.Faulted, missing.State);

        var slow = await RunAsync(new MacroBuilder().Add(BlockTypes.WaitForApp, new() { ["package"] = "com.example.game", ["timeoutMs"] = 500 }).Build(), new FakeTouch());
        Assert.Equal(RunState.Faulted, slow.State);
    }

    [Fact]
    public async Task Touch_blocks_fault_without_a_touch_sender()
    {
        var run = await RunAsync(new MacroBuilder().Add(BlockTypes.Tap, new() { ["x"] = 1, ["y"] = 1 }).Build(), null);
        Assert.Equal(RunState.Faulted, run.State);
    }
}
