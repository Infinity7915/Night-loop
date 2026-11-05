using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Compiler;
using NightLoop.Engine.Recording;
using NightLoop.Engine.Runtime;
using NightLoop.Engine.Screen;
using E = NightLoop.Engine.Recording.RawInputEvent;

namespace NightLoop.Engine.Tests;

/// <summary>Smooth mouse playback, the newer blocks, screen reading and "On fail".</summary>
public class PlaybackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private sealed record Result(MacroRun Run, FakeInput Input, FakeDelay Delay);

    private static async Task<Result> RunAsync(MacroDoc doc, RunSettings? settings = null, FakeWindows? windows = null,
        FakeScreen? screen = null, FakeText? text = null, Func<int, bool>? keys = null, FakeDelay? delay = null,
        Action<ScreenImage, string>? save = null)
    {
        Assert.Empty(MacroValidator.Validate(doc));
        var input = new FakeInput();
        delay ??= new FakeDelay();
        var env = new RunEnvironment
        {
            Input = input, Delay = delay, Windows = windows, Screen = screen, Text = text, IsKeyDown = keys,
            SaveScreenshot = save, Random = new Random(1),
        };
        var run = new MacroRun(MacroCompiler.Compile(doc), env, settings);
        run.Start();
        await run.Completion.WaitAsync(Timeout);
        return new Result(run, input, delay);
    }

    private static List<(int X, int Y)> Moves(FakeInput input) => input.Log
        .Where(l => l.StartsWith("move ", StringComparison.Ordinal))
        .Select(l => l[5..].Split(','))
        .Select(p => (int.Parse(p[0]), int.Parse(p[1])))
        .ToList();

    [Fact]
    public async Task Click_glides_along_a_smooth_path_and_lands_exactly()
    {
        var doc = new MacroBuilder().Add(BlockTypes.Click, new() { ["x"] = 800, ["y"] = 400 }).Build();
        var r = await RunAsync(doc);

        var moves = Moves(r.Input);
        Assert.True(moves.Count >= 10, $"only {moves.Count} moves");
        Assert.Equal((800, 400), moves[^1]);
        // Each step is small: no teleporting.
        for (var i = 1; i < moves.Count; i++)
            Assert.True(Math.Abs(moves[i].X - moves[i - 1].X) < 150 && Math.Abs(moves[i].Y - moves[i - 1].Y) < 150);
        Assert.Equal(["mdown Left", "mup Left"], r.Input.Log[^2..]);
    }

    [Fact]
    public async Task Move_time_zero_jumps()
    {
        var doc = new MacroBuilder().Add(BlockTypes.Click, new() { ["x"] = 800, ["y"] = 400, ["moveMs"] = 0 }).Build();
        var r = await RunAsync(doc);
        Assert.Equal(["move 800,400", "mdown Left", "mup Left"], r.Input.Log);
    }

    [Fact]
    public async Task Drag_presses_moves_along_its_path_then_releases()
    {
        var doc = new MacroBuilder().Add(BlockTypes.Drag, new()
        {
            ["from"] = new JsonObject { ["x"] = 100, ["y"] = 100 },
            ["to"] = new JsonObject { ["x"] = 300, ["y"] = 100 },
            ["durationMs"] = 400,
            ["moveMs"] = 0,
            ["path"] = new JsonArray(new JsonArray(100, 100, 0), new JsonArray(200, 200, 200)),
        }).Build();
        var r = await RunAsync(doc);

        var log = r.Input.Log;
        var down = log.IndexOf("mdown Left");
        var up = log.IndexOf("mup Left");
        Assert.True(down > 0 && up > down);
        var during = log.Skip(down + 1).Take(up - down - 1).Select(l => l[5..].Split(',')).Select(p => (X: int.Parse(p[0]), Y: int.Parse(p[1]))).ToList();
        Assert.Contains(during, p => p.Y > 180); // it went through the bend at 200,200
        Assert.Equal((300, 100), during[^1]);
    }

    [Fact]
    public async Task Relative_move_sends_raw_motion_that_adds_up_exactly()
    {
        var doc = new MacroBuilder().Add(BlockTypes.Move, new()
        {
            ["mode"] = "relative", ["x"] = 333, ["y"] = -47, ["durationMs"] = 250,
            ["path"] = new JsonArray(new JsonArray(0, 0, 0), new JsonArray(100, -40, 100)),
        }).Build();
        var r = await RunAsync(doc);

        var rel = r.Input.Log.Where(l => l.StartsWith("rel ", StringComparison.Ordinal)).Select(l => l[4..].Split(',')).ToList();
        Assert.True(rel.Count > 10);
        Assert.Equal(333, rel.Sum(p => int.Parse(p[0])));
        Assert.Equal(-47, rel.Sum(p => int.Parse(p[1])));
        Assert.DoesNotContain(r.Input.Log, l => l.StartsWith("move ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Speed_200_percent_halves_waits_but_not_key_holds()
    {
        var doc = new MacroBuilder().Press("A", holdMs: 100).Wait(1000).Build();
        doc.Options.SpeedPct = 200;
        var r = await RunAsync(doc);
        Assert.Equal(600, r.Delay.TotalMs);
    }

    [Fact]
    public async Task Turn_camera_by_right_drag_holds_the_right_button_around_relative_motion()
    {
        var doc = new MacroBuilder().Add(BlockTypes.CameraTurn, new() { ["direction"] = "left", ["amountPx"] = 120, ["method"] = "rightDrag" }).Build();
        var r = await RunAsync(doc);
        var log = r.Input.Log;
        Assert.Equal("mdown Right", log[0]);
        Assert.Equal("mup Right", log[^1]);
        Assert.Equal(-120, log.Where(l => l.StartsWith("rel ", StringComparison.Ordinal)).Sum(l => int.Parse(l[4..].Split(',')[0])));
    }

    [Fact]
    public async Task Turn_camera_with_arrow_keys_holds_the_arrow()
    {
        var doc = new MacroBuilder().Add(BlockTypes.CameraTurn, new() { ["direction"] = "right", ["amountPx"] = 100, ["method"] = "arrowKeys" }).Build();
        var r = await RunAsync(doc);
        Assert.Equal(["down Right", "up Right"], r.Input.Log);
        Assert.Equal(200, r.Delay.TotalMs);
    }

    [Fact]
    public async Task Combo_presses_in_order_and_releases_in_reverse()
    {
        var doc = new MacroBuilder().Add(BlockTypes.Combo, new() { ["keys"] = "Ctrl + Shift + E" }).Build();
        var r = await RunAsync(doc);
        Assert.Equal(["down LeftCtrl", "down LeftShift", "down E", "up E", "up LeftShift", "up LeftCtrl"], r.Input.Log);
    }

    [Fact]
    public async Task Hold_key_and_zoom_and_scroll_direction()
    {
        var doc = new MacroBuilder()
            .Add(BlockTypes.HoldKey, new() { ["key"] = "W", ["holdMs"] = 1200 })
            .Add(BlockTypes.CameraZoom, new() { ["direction"] = "out", ["steps"] = 3 })
            .Add(BlockTypes.Scroll, new() { ["direction"] = "down", ["amount"] = 2 })
            .Build();
        var r = await RunAsync(doc);
        Assert.Equal(["down W", "up W", "scroll -1", "scroll -1", "scroll -1", "scroll -1", "scroll -1"], r.Input.Log);
    }

    [Fact]
    public async Task Loop_forever_runs_until_a_stop_block()
    {
        var doc = new MacroBuilder()
            .Add(BlockTypes.Loop, new() { ["forever"] = true })
            .Press("A")
            .If(new JsonObject { ["kind"] = "keyHeld", ["key"] = "F1" }).Add(BlockTypes.Stop).End()
            .End()
            .Build();
        var presses = 0;
        var r = await RunAsync(doc, keys: _ => Interlocked.Increment(ref presses) >= 5);
        Assert.Equal(RunState.Stopped, r.Run.State);
        Assert.Equal(5, r.Input.Log.Count(l => l == "down A"));
    }

    [Fact]
    public async Task Wait_for_key_waits_until_pressed_and_can_time_out()
    {
        var checks = 0;
        var doc = new MacroBuilder().Add(BlockTypes.WaitForKey, new() { ["key"] = "F1" }).Press("A").Build();
        var r = await RunAsync(doc, keys: _ => Interlocked.Increment(ref checks) > 10);
        Assert.Equal(["down A", "up A"], r.Input.Log);
        Assert.True(checks > 10);

        var timeout = new MacroBuilder()
            .Add(BlockTypes.WaitForKey, new() { ["key"] = "F1", ["timeoutMs"] = 300 })
            .Press("A").Build();
        var t = await RunAsync(timeout, keys: _ => false);
        Assert.Equal(["down A", "up A"], t.Input.Log);
    }

    [Fact]
    public async Task Wait_for_pixel_waits_for_the_color()
    {
        var screen = new FakeScreen(200, 200);
        screen.OnCapture = s =>
        {
            if (Volatile.Read(ref s.Captures) == 4) s.Paint(50, 60, 1, 1, new Rgb(255, 0, 0));
        };
        var doc = new MacroBuilder()
            .Add(BlockTypes.DetectPixelColor, new() { ["x"] = 50, ["y"] = 60, ["color"] = "#FF0000", ["tolerance"] = 5, ["timeoutMs"] = 0 })
            .Press("A").Build();
        var r = await RunAsync(doc, screen: screen);
        Assert.Equal(RunState.Stopped, r.Run.State);
        Assert.Equal(4, screen.Captures);
        Assert.Equal(["down A", "up A"], r.Input.Log);
    }

    [Fact]
    public async Task Pixel_gone_waits_until_the_color_leaves()
    {
        var screen = new FakeScreen(200, 200);
        screen.Paint(50, 60, 1, 1, new Rgb(255, 0, 0));
        screen.OnCapture = s =>
        {
            if (Volatile.Read(ref s.Captures) == 3) s.Clear();
        };
        var doc = new MacroBuilder()
            .Add(BlockTypes.DetectPixelColorGone, new() { ["x"] = 50, ["y"] = 60, ["color"] = "#FF0000", ["tolerance"] = 5, ["timeoutMs"] = 0 })
            .Press("A").Build();
        Assert.Empty(MacroValidator.Validate(doc));
        var r = await RunAsync(doc, screen: screen);
        Assert.Equal(RunState.Stopped, r.Run.State);
        Assert.Equal(3, screen.Captures);
        Assert.Equal(["down A", "up A"], r.Input.Log);
    }

    [Fact]
    public async Task Image_gone_waits_until_the_picture_leaves_and_fails_on_timeout()
    {
        var pattern = new byte[24 * 24 * 4];
        var rng = new Random(11);
        for (var i = 0; i < 24 * 24; i++) pattern[i * 4] = pattern[i * 4 + 1] = pattern[i * 4 + 2] = (byte)rng.Next(256);
        var template = ImageTemplate.FromImage(new ScreenImage(0, 0, 24, 24, pattern));

        var screen = new FakeScreen(320, 240);
        screen.Show(150, 90, template);
        screen.OnCapture = s =>
        {
            if (Volatile.Read(ref s.Captures) == 2) s.Clear();
        };
        var gone = new MacroBuilder()
            .Add(BlockTypes.WaitForImageGone, new() { ["imageData"] = template.Encode(), ["timeoutMs"] = 0 })
            .Press("A").Build();
        Assert.Empty(MacroValidator.Validate(gone));
        var r = await RunAsync(gone, screen: screen);
        Assert.Equal(["down A", "up A"], r.Input.Log);

        // Still there when the time runs out: On fail "stop" ends the macro.
        var stays = new FakeScreen(320, 240);
        stays.Show(150, 90, template);
        var timeout = new MacroBuilder()
            .Add(BlockTypes.WaitForImageGone, new() { ["imageData"] = template.Encode(), ["timeoutMs"] = 300, ["onFail"] = "stop" })
            .Press("B").Build();
        var t = await RunAsync(timeout, screen: stays);
        Assert.Empty(t.Input.Log);
    }

    [Fact]
    public async Task On_fail_go_to_label_jumps_and_stop_ends_the_macro()
    {
        var screen = new FakeScreen(100, 100);
        var doc = new MacroBuilder()
            .Add(BlockTypes.DetectPixelColor, new() { ["x"] = 1, ["y"] = 1, ["color"] = "#FF0000", ["timeoutMs"] = 100, ["onFail"] = "goto", ["failLabel"] = "missing" })
            .Press("A")
            .Label("missing")
            .Press("B")
            .Add(BlockTypes.DetectPixelColor, new() { ["x"] = 1, ["y"] = 1, ["color"] = "#FF0000", ["timeoutMs"] = 100, ["onFail"] = "stop" })
            .Press("C")
            .Build();
        var r = await RunAsync(doc, screen: screen);
        Assert.Equal(RunState.Stopped, r.Run.State);
        Assert.Equal(["down B", "up B"], r.Input.Log);
    }

    [Fact]
    public void On_fail_goto_needs_a_real_label()
    {
        var doc = new MacroBuilder()
            .Add(BlockTypes.DetectPixelColor, new() { ["x"] = 1, ["y"] = 1, ["color"] = "#FF0000", ["onFail"] = "goto", ["failLabel"] = "nowhere" })
            .Build();
        Assert.Contains(MacroValidator.Validate(doc), i => i.Message.Contains("nowhere"));
    }

    [Fact]
    public async Task Find_and_click_a_color_clicks_the_middle_of_it_in_the_game_window()
    {
        var windows = new FakeWindows { Area = new(100, 200, 400, 300) };
        var screen = new FakeScreen(800, 600);
        screen.Paint(300, 300, 20, 10, new Rgb(0, 200, 0));
        var doc = new MacroBuilder()
            .Add(BlockTypes.FindClick, new() { ["target"] = "pixel", ["value"] = "#00C800", ["tolerance"] = 8 })
            .Build();
        var r = await RunAsync(doc, new RunSettings { TargetExe = "game.exe" }, windows, screen);

        Assert.Equal(RunState.Stopped, r.Run.State);
        Assert.Equal((309, 304), Moves(r.Input)[^1]);
        Assert.Equal(["mdown Left", "mup Left"], r.Input.Log[^2..]);
    }

    [Fact]
    public async Task Find_and_click_text_uses_the_text_reader()
    {
        var text = new FakeText();
        text.Say("Daily reward: Claim now", 200, 300);
        var doc = new MacroBuilder().Add(BlockTypes.FindClick, new() { ["target"] = "text", ["value"] = "claim" }).Build();
        var r = await RunAsync(doc, screen: new FakeScreen(640, 480), text: text);

        // "Claim" starts after "Daily" and "reward:" (10 px per letter, 6 px gaps) and is 50 px wide, 16 px tall.
        Assert.Equal((200 + 50 + 6 + 70 + 6 + 25, 308), Moves(r.Input)[^1]);
    }

    [Fact]
    public async Task Wait_for_text_times_out_and_continues()
    {
        var doc = new MacroBuilder()
            .Add(BlockTypes.WaitForText, new() { ["text"] = "+1 Coin", ["match"] = "contains", ["timeoutMs"] = 1000 })
            .Press("A").Build();
        var text = new FakeText();
        var r = await RunAsync(doc, screen: new FakeScreen(320, 240), text: text);
        Assert.Equal(["down A", "up A"], r.Input.Log);
        Assert.True(text.Reads >= 5);
    }

    [Fact]
    public async Task Text_conditions_fault_clearly_without_text_reading()
    {
        var doc = new MacroBuilder().If(new JsonObject { ["kind"] = "textVisible", ["text"] = "hi" }).Press("A").End().Build();
        var r = await RunAsync(doc, screen: new FakeScreen(100, 100));
        Assert.Equal(RunState.Faulted, r.Run.State);
        Assert.Contains("text recognition", r.Run.FaultMessage);
    }

    [Fact]
    public async Task Scan_and_turn_turns_until_the_target_shows_then_clicks_it()
    {
        var screen = new FakeScreen(400, 300);
        var input = new FakeInput();
        var turns = 0;
        screen.OnCapture = s =>
        {
            if (turns >= 3) s.Paint(197, 147, 6, 6, new Rgb(255, 255, 0)); // lands in the middle of the view
        };
        var doc = new MacroBuilder().Add(BlockTypes.ScanTurn, new()
        {
            ["target"] = "pixel", ["value"] = "#FFFF00", ["direction"] = "right", ["stepPx"] = 90, ["maxTurns"] = 10, ["method"] = "mouseMove",
        }).Build();
        var delay = new FakeDelay();
        delay.OnDelay = () =>
        {
            turns = input.Log.Count(l => l.StartsWith("rel ", StringComparison.Ordinal)) > 0
                ? input.Log.Where(l => l.StartsWith("rel ", StringComparison.Ordinal)).Sum(l => int.Parse(l[4..].Split(',')[0])) / 90
                : 0;
            return Task.CompletedTask;
        };
        var run = new MacroRun(MacroCompiler.Compile(doc), new RunEnvironment { Input = input, Delay = delay, Screen = screen });
        run.Start();
        await run.Completion.WaitAsync(Timeout);

        Assert.Equal(RunState.Stopped, run.State);
        Assert.Equal(270, input.Log.Where(l => l.StartsWith("rel ", StringComparison.Ordinal)).Sum(l => int.Parse(l[4..].Split(',')[0])));
        Assert.Equal(["mdown Left", "mup Left"], input.Log[^2..]);
    }

    [Fact]
    public async Task Scan_and_turn_with_the_camera_on_the_mouse_never_moves_the_mouse_to_click()
    {
        // Target off-center: it turns it into the middle (relative moves) and clicks there, with no absolute glide.
        var screen = new FakeScreen(400, 300);
        screen.Paint(250, 147, 6, 6, new Rgb(255, 255, 0));
        var input = new FakeInput();
        var moved = 0;
        screen.OnCapture = s =>
        {
            var sent = input.Log.Where(l => l.StartsWith("rel ", StringComparison.Ordinal)).Sum(l => int.Parse(l[4..].Split(',')[0]));
            if (sent == moved) return;
            moved = sent;
            s.Clear();
            s.Paint(250 - sent, 147, 6, 6, new Rgb(255, 255, 0));
        };
        var doc = new MacroBuilder().Add(BlockTypes.ScanTurn, new()
        {
            ["target"] = "pixel", ["value"] = "#FFFF00", ["method"] = "mouseMove", ["clicks"] = 2,
        }).Build();
        var run = new MacroRun(MacroCompiler.Compile(doc), new RunEnvironment { Input = input, Delay = new FakeDelay(), Screen = screen });
        run.Start();
        await run.Completion.WaitAsync(Timeout);

        Assert.Equal(RunState.Stopped, run.State);
        Assert.DoesNotContain(input.Log, l => l.StartsWith("move ", StringComparison.Ordinal));
        Assert.True(moved > 0);
        Assert.Equal(2, input.Log.Count(l => l == "mdown Left"));
    }

    [Fact]
    public async Task Scan_and_turn_with_right_drag_glides_to_the_target_and_clicks_three_times()
    {
        var screen = new FakeScreen(400, 300);
        screen.Paint(300, 60, 6, 6, new Rgb(255, 255, 0));
        var doc = new MacroBuilder().Add(BlockTypes.ScanTurn, new()
        {
            ["target"] = "pixel", ["value"] = "#FFFF00", ["method"] = "rightDrag", ["clicks"] = 3, ["button"] = "right",
        }).Press("A").Build();
        var r = await RunAsync(doc, screen: screen);
        Assert.Equal((302, 62), Moves(r.Input)[^1]);
        Assert.Equal(3, r.Input.Log.Count(l => l == "mdown Right"));
        Assert.Equal(["down A", "up A"], r.Input.Log[^2..]);
    }

    [Fact]
    public async Task Find_and_click_clicks_up_to_three_times()
    {
        var doc = new MacroBuilder()
            .Add(BlockTypes.FindClick, new() { ["target"] = "pixel", ["value"] = "#00C800", ["tolerance"] = 8, ["clicks"] = 2 }).Build();
        var r = await RunAsync(doc, screen: GreenScreen());
        Assert.Equal(2, Clicks(r.Input));
        var bad = new MacroBuilder().Add(BlockTypes.FindClick, new() { ["target"] = "pixel", ["value"] = "#00C800", ["clicks"] = 4 }).Build();
        Assert.Contains(MacroValidator.Validate(bad), i => i.Message.Contains("Clicks"));
    }

    private static JsonObject GreenFind(string afterFound) => new()
    {
        ["target"] = "pixel", ["value"] = "#00C800", ["tolerance"] = 8, ["afterFound"] = afterFound,
    };

    private static FakeScreen GreenScreen()
    {
        var screen = new FakeScreen(400, 300);
        screen.Paint(100, 100, 6, 6, new Rgb(0, 200, 0));
        return screen;
    }

    private static int Clicks(FakeInput input) => input.Log.Count(l => l == "mdown Left");

    [Fact]
    public async Task After_found_stop_ends_a_forever_loop_after_one_click()
    {
        var doc = new MacroBuilder()
            .Add(BlockTypes.Loop, new() { ["forever"] = true })
            .Add(BlockTypes.FindClick, GreenFind("stop")).Wait(50)
            .End().Forever().Build();
        var r = await RunAsync(doc, screen: GreenScreen());
        Assert.Equal(RunState.Stopped, r.Run.State);
        Assert.Equal(1, Clicks(r.Input));
    }

    [Fact]
    public async Task After_found_leave_loop_goes_on_after_the_loop()
    {
        var doc = new MacroBuilder()
            .Add(BlockTypes.Loop, new() { ["forever"] = true })
            .Add(BlockTypes.Loop, new() { ["count"] = 2 }).Wait(10).End()
            .Add(BlockTypes.ScanTurn, new()
            {
                ["target"] = "pixel", ["value"] = "#00C800", ["tolerance"] = 8, ["method"] = "mouseMove", ["afterFound"] = "exitLoop",
            })
            .Wait(50)
            .End()
            .Press("A").Build();
        var r = await RunAsync(doc, screen: GreenScreen());
        Assert.Equal(RunState.Stopped, r.Run.State);
        Assert.Equal(1, Clicks(r.Input));
        Assert.Equal(["down A", "up A"], r.Input.Log[^2..]);
    }

    [Fact]
    public async Task After_found_go_to_label_jumps_there()
    {
        var doc = new MacroBuilder()
            .Add(BlockTypes.FindClick, GreenFind("goto").Also("foundLabel", "done"))
            .Press("B")
            .Label("done").Press("A").Build();
        var r = await RunAsync(doc, screen: GreenScreen());
        Assert.DoesNotContain("down B", r.Input.Log);
        Assert.Equal(["down A", "up A"], r.Input.Log[^2..]);
    }

    [Fact]
    public async Task After_found_defaults_to_the_next_block()
    {
        var doc = new MacroBuilder()
            .Add(BlockTypes.FindClick, new() { ["target"] = "pixel", ["value"] = "#00C800", ["tolerance"] = 8 })
            .Press("A").Build();
        var r = await RunAsync(doc, screen: GreenScreen());
        Assert.Equal(1, Clicks(r.Input));
        Assert.Equal(["down A", "up A"], r.Input.Log[^2..]);
    }

    [Fact]
    public void After_found_goto_needs_a_real_label()
    {
        var doc = new MacroBuilder().Add(BlockTypes.FindClick, GreenFind("goto").Also("foundLabel", "nowhere")).Build();
        Assert.Contains(MacroValidator.Validate(doc), i => i.Message.Contains("After found") && i.Message.Contains("nowhere"));
        var bad = new MacroBuilder().Add(BlockTypes.FindClick, GreenFind("again")).Build();
        Assert.Contains(MacroValidator.Validate(bad), i => i.Message.Contains("After found"));
    }

    [Fact]
    public async Task Picture_saved_in_the_block_is_found_on_screen()
    {
        var pattern = new byte[24 * 24 * 4];
        var rng = new Random(7);
        for (var i = 0; i < 24 * 24; i++) pattern[i * 4] = pattern[i * 4 + 1] = pattern[i * 4 + 2] = (byte)rng.Next(256);
        var template = ImageTemplate.FromImage(new ScreenImage(0, 0, 24, 24, pattern));
        Assert.True(ImageTemplate.TryDecode(template.Encode(), out var decoded));
        Assert.Equal(template.Gray, decoded!.Gray);

        var screen = new FakeScreen(320, 240);
        screen.Show(150, 90, template);
        var doc = new MacroBuilder()
            .Add(BlockTypes.FindClick, new() { ["target"] = "image", ["value"] = "chest.png", ["imageData"] = template.Encode() })
            .Build();
        var r = await RunAsync(doc, screen: screen);
        Assert.Equal((162, 102), Moves(r.Input)[^1]);
    }

    [Fact]
    public async Task Test_look_finds_the_picture_without_clicking()
    {
        var pattern = new byte[24 * 24 * 4];
        var rng = new Random(7);
        for (var i = 0; i < 24 * 24; i++) pattern[i * 4] = pattern[i * 4 + 1] = pattern[i * 4 + 2] = (byte)rng.Next(256);
        var template = ImageTemplate.FromImage(new ScreenImage(0, 0, 24, 24, pattern));
        var screen = new FakeScreen(320, 240);
        screen.Show(150, 90, template);
        var input = new FakeInput();
        var env = new RunEnvironment { Input = input, Delay = new FakeDelay(), Screen = screen };
        var block = Block.Create("b1", BlockTypes.FindClick, new() { ["target"] = "image", ["imageData"] = template.Encode() });

        var found = await Blocks.DetectProbe.LookOnceAsync(block, env, null);
        Assert.True(found.Found);
        Assert.Equal((162, 102), found.Point);
        Assert.Empty(input.Log);

        screen.Clear();
        var missing = await Blocks.DetectProbe.LookOnceAsync(block, env, null);
        Assert.False(missing.Found);
        Assert.Equal("Didn't find the picture on screen right now.", missing.Message);
    }

    [Fact]
    public async Task Test_look_checks_a_condition_and_explains_problems()
    {
        var screen = new FakeScreen(200, 200);
        screen.Paint(10, 10, 5, 5, new Rgb(255, 0, 0));
        var env = new RunEnvironment { Input = new FakeInput(), Delay = new FakeDelay(), Screen = screen };
        var red = Block.Create("b1", BlockTypes.If, new()
        {
            ["condition"] = new JsonObject { ["kind"] = "pixelColor", ["x"] = 12, ["y"] = 12, ["color"] = "#FF0000" },
        });
        Assert.True((await Blocks.DetectProbe.LookOnceAsync(red, env, null)).Found);

        var noPicture = Block.Create("b2", BlockTypes.WaitForImage, new());
        var r = await Blocks.DetectProbe.LookOnceAsync(noPicture, env, null);
        Assert.False(r.Found);
        Assert.StartsWith("Pick a picture first", r.Message);
    }

    [Fact]
    public async Task Pause_right_away_interrupts_and_resume_from_start_restarts()
    {
        var input = new FakeInput();
        var worker = new MacroRun(MacroCompiler.Compile(new MacroBuilder().Press("A").Wait(100_000).Press("B").Build()),
            new RunEnvironment { Input = input, Delay = new LongWaitsBlock() });
        worker.Start();
        await WaitFor(() => input.Log.Count >= 2);

        worker.Pause(immediate: true);
        await WaitFor(() => worker.State == RunState.Paused);
        worker.Resume(fromStart: true);
        await WaitFor(() => input.Log.Count(l => l == "down A") == 2);
        worker.Stop();
        await worker.Completion.WaitAsync(Timeout);
        Assert.Equal(0, worker.CompletedPasses);
    }

    [Fact]
    public async Task Position_window_snaps_to_a_monitor_corner()
    {
        var windows = new FakeWindows { Bounds = new(10, 10, 800, 600) };
        var doc = new MacroBuilder()
            .Add(BlockTypes.WindowPosition, new() { ["anchor"] = "bottomRight", ["monitor"] = 2 })
            .Add(BlockTypes.WindowPosition, new() { ["anchor"] = "center", ["monitor"] = 0, ["process"] = "other.exe" })
            .Build();
        await RunAsync(doc, new RunSettings { TargetExe = "game.exe" }, windows);
        Assert.Equal(["moveto game.exe 3680,800", "moveto other.exe 560,220"], windows.Calls);
    }

    [Fact]
    public async Task Screenshot_saves_the_game_window()
    {
        var windows = new FakeWindows { Area = new(10, 20, 64, 48) };
        ScreenImage? saved = null;
        string? file = null;
        var doc = new MacroBuilder().Add(BlockTypes.Screenshot, new() { ["file"] = "farm.png" }).Build();
        await RunAsync(doc, new RunSettings { TargetExe = "game.exe" }, windows, new FakeScreen(200, 200), save: (img, f) => { saved = img; file = f; });
        Assert.Equal("farm.png", file);
        Assert.Equal((10, 20, 64, 48), (saved!.Left, saved.Top, saved.Width, saved.Height));
    }

    [Fact]
    public void Recording_a_camera_turn_with_a_hidden_cursor_saves_relative_motion()
    {
        // Right button down, the game hides and pins the cursor, the mouse moves 300 px right, button up.
        var events = new List<E> { E.Move(0, 500, 400), E.MouseDown(100, Input.MouseButton.Right, 500, 400) };
        for (var t = 1; t <= 20; t++) events.Add(E.Move(100 + t * 16, 500, 400, dx: 15, dy: t % 2, hidden: true));
        events.Add(E.MouseUp(500, Input.MouseButton.Right, 500, 400));

        var blocks = RecordingBuilder.Build(events).Blocks;
        var doc = new MacroDoc { Name = "Turn", Blocks = blocks };
        Assert.Empty(MacroValidator.Validate(doc));

        var move = Assert.Single(blocks, b => b.Type == BlockTypes.Move);
        Assert.Equal("relative", move.GetString("mode"));
        Assert.Equal(300, move.GetInt("x"));
        Assert.Equal(10, move.GetInt("y"));
        Assert.Contains(blocks, b => b.Type == BlockTypes.Click && b.GetString("action") == "down" && b.GetString("button") == "right");
        Assert.Contains(blocks, b => b.Type == BlockTypes.Click && b.GetString("action") == "up");
    }

    [Fact]
    public async Task Recorded_drag_keeps_its_path_and_clicks_glide_without_changing_the_timing()
    {
        var events = new List<E> { E.Move(0, 0, 0) };
        // Wander to 400,300 over 300 ms, wait, click; then a curved drag.
        for (var t = 1; t <= 15; t++) events.Add(E.Move(1000 + t * 20, t * 400 / 15, t * 300 / 15));
        events.Add(E.MouseDown(2000, Input.MouseButton.Left, 400, 300));
        events.Add(E.MouseUp(2040, Input.MouseButton.Left, 400, 300));
        events.Add(E.MouseDown(3000, Input.MouseButton.Left, 400, 300));
        for (var t = 1; t <= 20; t++)
        {
            var a = t / 20.0 * Math.PI;
            events.Add(E.Move(3000 + t * 20, 400 + (int)(200 * (1 - Math.Cos(a))), 300 - (int)(150 * Math.Sin(a))));
        }
        events.Add(E.MouseUp(3420, Input.MouseButton.Left, 800, 300));

        var blocks = RecordingBuilder.Build(events).Blocks;
        var doc = new MacroDoc { Name = "Drag", Blocks = blocks };
        Assert.Empty(MacroValidator.Validate(doc));
        var drag = Assert.Single(blocks, b => b.Type == BlockTypes.Drag);
        Assert.True(drag.Params["path"] is JsonArray { Count: > 3 }, "the curve should be kept");
        Assert.Equal(BlockTypes.Click, blocks.First(b => b.Type != BlockTypes.Wait).Type);

        var r = await RunAsync(doc);
        var moves = Moves(r.Input);
        Assert.Contains(moves, m => m.Y < 200); // went over the top of the arc
        Assert.Equal((800, 300), moves[^1]);
        // Same length as the recording from its first action (1.42 s) plus the first click's 312 ms glide in,
        // give or take the drag's settle pauses.
        Assert.InRange(r.Delay.TotalMs, 1700, 1850);
    }

    /// <summary>Short waits return at once; waits of 10 s or more last until cancelled.</summary>
    private sealed class LongWaitsBlock : IDelay
    {
        public Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
            milliseconds >= 10_000 ? Task.Delay(Timeout, cancellationToken) : Task.CompletedTask;
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

internal static class JsonObjectTestExtensions
{
    public static JsonObject Also(this JsonObject o, string key, string value)
    {
        o[key] = value;
        return o;
    }
}
