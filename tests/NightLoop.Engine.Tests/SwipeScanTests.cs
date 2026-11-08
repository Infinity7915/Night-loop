using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Compiler;
using NightLoop.Engine.Runtime;
using NightLoop.Engine.Screen;

namespace NightLoop.Engine.Tests;

public class SwipeScanTests
{
    private static ImageTemplate Pattern()
    {
        var px = new byte[24 * 24 * 4];
        var rng = new Random(5);
        for (var i = 0; i < 24 * 24; i++) { px[i * 4] = (byte)rng.Next(256); px[i * 4 + 1] = (byte)rng.Next(256); px[i * 4 + 2] = (byte)rng.Next(256); px[i * 4 + 3] = 255; }
        return ImageTemplate.FromImage(new ScreenImage(0, 0, 24, 24, px));
    }

    private static JsonObject Point(int x, int y) => new() { ["x"] = x, ["y"] = y };

    private static async Task<MacroRun> RunAsync(MacroDoc doc, FakeTouch touch, FakeScreen screen)
    {
        doc.Platform = MacroPlatforms.Android;
        Assert.Empty(MacroValidator.Validate(doc));
        var run = new MacroRun(MacroCompiler.Compile(doc), new RunEnvironment { Input = new FakeInput(), Delay = new FakeDelay(), Touch = touch, Screen = screen });
        run.Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        return run;
    }

    [Fact]
    public async Task Swipe_and_scan_swipes_until_the_picture_shows_then_taps_it()
    {
        var t = Pattern();
        var touch = new FakeTouch();
        var screen = new FakeScreen(400, 400);
        var doc = new MacroBuilder()
            .Add(BlockTypes.SwipeScan, new()
            {
                ["from"] = Point(300, 200), ["to"] = Point(100, 200), ["imageData"] = t.Encode(), ["matchColor"] = false, ["whenFound"] = "tap",
            })
            .Build();
        var swipes = 0;
        screen.OnCapture = s => { if (touch.Log.Count(l => l.StartsWith("swipe")) >= 2 && Interlocked.Increment(ref swipes) == 1) s.Show(150, 150, t); };
        var run = await RunAsync(doc, touch, screen);
        Assert.True(run.State == RunState.Stopped, run.FaultMessage);
        Assert.Equal(2, touch.Log.Count(l => l.StartsWith("swipe")));
        Assert.StartsWith("tap 16", touch.Log[^1]); // the picture's middle (150 + 12 = 162)
    }

    [Fact]
    public async Task Walk_to_steers_at_the_picture_and_stops_when_it_is_near_the_middle()
    {
        var t = Pattern();
        var touch = new FakeTouch();
        var screen = new FakeScreen(400, 400);
        screen.Show(300, 50, t); // up and to the right
        var moves = 0;
        screen.OnCapture = s => { if (Interlocked.Increment(ref moves) == 4) { s.Clear(); s.Show(190, 190, t); } };
        var doc = new MacroBuilder()
            .Add(BlockTypes.WalkTo, new()
            {
                ["from"] = Point(80, 320), ["to"] = Point(80, 240), ["imageData"] = t.Encode(), ["matchColor"] = false, ["tolerancePx"] = 40,
            })
            .Build();
        var run = await RunAsync(doc, touch, screen);
        Assert.True(run.State == RunState.Stopped, run.FaultMessage);
        Assert.Equal("down 80,320", touch.Log[0]);
        var first = touch.Log[1].Split(' ', ',');
        Assert.True(int.Parse(first[1]) > 80 && int.Parse(first[2]) < 320, touch.Log[1]); // pushed up and right
        Assert.Equal("up", touch.Log[^1]);
    }
}
