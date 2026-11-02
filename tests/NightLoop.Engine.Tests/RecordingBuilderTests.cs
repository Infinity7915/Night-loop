using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Compiler;
using NightLoop.Engine.Input;
using NightLoop.Engine.Recording;
using NightLoop.Engine.Runtime;
using E = NightLoop.Engine.Recording.RawInputEvent;

namespace NightLoop.Engine.Tests;

public class RecordingBuilderTests
{
    private const int A = 0x41, C = 0x43, T = 0x54, W = 0x57, LCtrl = 0xA2, F9 = 0x78;

    private static List<Block> Build(RecordingOptions? o, params E[] events) => RecordingBuilder.Build(events, o).Blocks;
    private static List<Block> Build(params E[] events) => Build(null, events);

    private static string Describe(Block b) => b.Type switch
    {
        BlockTypes.Key => $"key {b.GetString("key")} {b.GetString("action")}{(b.GetInt("holdMs") is { } h ? $" {h}" : "")}",
        BlockTypes.Wait => $"wait {b.GetInt("ms")}",
        BlockTypes.Click => $"click {b.GetString("button")} {b.GetString("action") ?? $"x{b.GetInt("clicks")}"} {b.GetInt("x")},{b.GetInt("y")} {b.GetString("relativeTo")}",
        BlockTypes.Drag => $"drag {b.GetPoint("from")} {b.GetPoint("to")} {b.GetInt("durationMs")}",
        BlockTypes.Scroll => $"scroll {b.GetInt("amount")}",
        BlockTypes.Move => $"move {b.GetInt("x")},{b.GetInt("y")} {b.GetInt("durationMs")}",
        _ => b.Type,
    };

    private static List<string> Describe(IEnumerable<Block> blocks) => blocks.Select(Describe).ToList();

    [Fact]
    public void Quick_press_becomes_one_key_block_with_hold_time()
    {
        Assert.Equal(["key A press 90"], Describe(Build(E.KeyDown(1000, A), E.KeyUp(1090, A))));
    }

    [Fact]
    public void Gaps_over_50ms_become_waits_and_shorter_gaps_carry_into_the_next_wait()
    {
        var blocks = Build(
            E.KeyDown(0, A), E.KeyUp(50, A),
            E.KeyDown(80, A), E.KeyUp(130, A),     // 30 ms gap: no wait of its own
            E.KeyDown(530, A), E.KeyUp(580, A));   // 400 ms gap, plus the 30 ms carried over
        Assert.Equal(["key A press 50", "key A press 50", "wait 430", "key A press 50"], Describe(blocks));
    }

    [Fact]
    public void Many_short_gaps_add_up_so_the_replay_keeps_its_pace()
    {
        var blocks = Build(
            E.KeyDown(0, A), E.KeyUp(40, A),
            E.KeyDown(80, A), E.KeyUp(120, A),     // 40 ms
            E.KeyDown(160, A), E.KeyUp(200, A));   // 40 ms: 80 ms carried
        Assert.Equal(["key A press 40", "key A press 40", "wait 80", "key A press 40"], Describe(blocks));
    }

    [Fact]
    public void Ctrl_held_past_the_letter_replays_in_the_same_order()
    {
        // Ctrl down, T pressed and released quickly, Ctrl let go a little later (opening a browser tab).
        var blocks = Build(E.KeyDown(0, LCtrl), E.KeyDown(30, T), E.KeyUp(90, T), E.KeyUp(130, LCtrl));
        Assert.Equal(["key LeftCtrl down", "key T press 60", "wait 70", "key LeftCtrl up"], Describe(blocks));
    }

    [Fact]
    public void Keys_without_a_name_are_left_out_so_the_macro_stays_valid()
    {
        var blocks = Build(E.KeyDown(0, 0xFF), E.KeyUp(40, 0xFF), E.KeyDown(500, A), E.KeyUp(550, A));
        Assert.Equal(["key A press 50"], Describe(blocks));
    }

    [Fact]
    public void A_byte_budget_cuts_the_recording_short()
    {
        var events = new List<RawInputEvent>();
        for (var i = 0; i < 200; i++) { events.Add(E.KeyDown(i * 200, A)); events.Add(E.KeyUp(i * 200 + 50, A)); }
        var result = RecordingBuilder.Build(events, new RecordingOptions { MaxBytes = 2_000 });
        Assert.True(result.Truncated);
        Assert.NotEmpty(result.Blocks);
        Assert.True(MacroJson.ByteSize(new MacroDoc { Name = "x", Blocks = result.Blocks }) <= 2_000);
    }

    [Fact]
    public void Overlapping_keys_record_as_down_and_up_so_combos_replay()
    {
        var blocks = Build(E.KeyDown(0, LCtrl), E.KeyDown(100, C), E.KeyUp(160, C), E.KeyUp(300, LCtrl));
        Assert.Equal(["key LeftCtrl down", "wait 100", "key C press 60", "wait 140", "key LeftCtrl up"], Describe(blocks));
    }

    [Fact]
    public void Key_auto_repeat_is_dropped()
    {
        var blocks = Build(E.KeyDown(0, W), E.KeyDown(30, W), E.KeyDown(60, W), E.KeyUp(900, W));
        Assert.Equal(["key W press 900"], Describe(blocks));
    }

    [Fact]
    public void Releases_from_before_recording_started_are_dropped()
    {
        var blocks = Build(E.MouseUp(0, MouseButton.Left, 5, 5), E.KeyUp(10, A), E.KeyDown(20, A), E.KeyUp(60, A));
        Assert.Equal(["key A press 40"], Describe(blocks));
    }

    [Fact]
    public void Stop_hotkey_modifiers_at_the_end_are_dropped()
    {
        var opts = new RecordingOptions { StopHotkey = new Hotkey(HotkeyModifiers.Ctrl, F9) };
        var blocks = Build(opts, E.KeyDown(0, A), E.KeyUp(40, A), E.KeyDown(500, LCtrl));
        Assert.Equal(["key A press 40"], Describe(blocks));
    }

    [Fact]
    public void Click_double_click_and_triple_click()
    {
        Assert.Equal(["click left x1 10,20 screen"],
            Describe(Build(E.MouseDown(0, MouseButton.Left, 10, 20), E.MouseUp(60, MouseButton.Left, 11, 20))));

        Assert.Equal(["click left x2 10,20 screen"], Describe(Build(
            E.MouseDown(0, MouseButton.Left, 10, 20), E.MouseUp(60, MouseButton.Left, 10, 20),
            E.MouseDown(200, MouseButton.Left, 10, 21), E.MouseUp(250, MouseButton.Left, 10, 21))));

        Assert.Equal(["click left x3 10,20 screen", "wait 60", "click left x1 10,20 screen"], Describe(Build(
            E.MouseDown(0, MouseButton.Left, 10, 20), E.MouseUp(40, MouseButton.Left, 10, 20),
            E.MouseDown(100, MouseButton.Left, 10, 20), E.MouseUp(140, MouseButton.Left, 10, 20),
            E.MouseDown(200, MouseButton.Left, 10, 20), E.MouseUp(240, MouseButton.Left, 10, 20),
            E.MouseDown(300, MouseButton.Left, 10, 20), E.MouseUp(340, MouseButton.Left, 10, 20))));
    }

    [Fact]
    public void Clicks_far_apart_stay_separate()
    {
        var blocks = Build(
            E.MouseDown(0, MouseButton.Left, 10, 20), E.MouseUp(40, MouseButton.Left, 10, 20),
            E.MouseDown(100, MouseButton.Left, 300, 300), E.MouseUp(140, MouseButton.Left, 300, 300));
        Assert.Equal(["click left x1 10,20 screen", "wait 60", "click left x1 300,300 screen"], Describe(blocks));
    }

    [Fact]
    public void Press_move_release_becomes_a_drag()
    {
        var blocks = Build(
            E.MouseDown(0, MouseButton.Left, 10, 10), E.Move(50, 60, 60), E.Move(100, 120, 90), E.MouseUp(400, MouseButton.Left, 200, 100));
        Assert.Equal(["drag (10, 10) (200, 100) 400"], Describe(blocks));
    }

    [Fact]
    public void Long_hold_without_moving_records_down_and_up()
    {
        var blocks = Build(E.MouseDown(0, MouseButton.Right, 5, 5), E.MouseUp(2000, MouseButton.Right, 5, 5));
        Assert.Equal(["click right down 5,5 screen", "wait 2000", "click right up 5,5 screen"], Describe(blocks));
    }

    [Fact]
    public void Holding_a_key_while_clicking_keeps_both()
    {
        var blocks = Build(
            E.KeyDown(0, W), E.MouseDown(100, MouseButton.Left, 1, 1), E.MouseUp(150, MouseButton.Left, 1, 1), E.KeyUp(1000, W));
        // The first click glides in for the 100 ms the wait used to take, so the timing is the same.
        Assert.Equal(["key W down", "click left x1 1,1 screen", "wait 850", "key W up"], Describe(blocks));
        Assert.Equal(100, blocks[1].GetInt("moveMs"));
    }

    [Fact]
    public void Wheel_ticks_merge_into_one_scroll()
    {
        var blocks = Build(E.Wheel(0, 120), E.Wheel(40, 120), E.Wheel(80, 120), E.Wheel(1000, -240));
        Assert.Equal(["scroll 3", "wait 920", "scroll -2"], Describe(blocks));
    }

    [Fact]
    public void Mouse_moves_are_kept_only_when_asked()
    {
        var events = new[] { E.Move(0, 1, 1), E.Move(30, 5, 5), E.Move(60, 9, 9) };
        Assert.Empty(Build(events));
        var blocks = Build(new RecordingOptions { RecordMouseMoves = true }, events);
        // One block for the whole movement (with its path), not one per sample.
        Assert.Equal(["move 9,9 60"], Describe(blocks));
    }

    [Fact]
    public void Clicks_in_the_game_window_are_saved_relative_to_it()
    {
        var blocks = Build(E.MouseDown(0, MouseButton.Left, 40, 50, game: true), E.MouseUp(30, MouseButton.Left, 40, 50, game: true));
        Assert.Equal(["click left x1 40,50 gameWindow"], Describe(blocks));
    }

    [Fact]
    public void Recording_stops_at_the_block_limit()
    {
        var events = new List<E>();
        for (var i = 0; i < 400; i++)
        {
            events.Add(E.KeyDown(i * 1000, A));
            events.Add(E.KeyUp(i * 1000 + 10, A));
        }
        var result = RecordingBuilder.Build(events);
        Assert.True(result.Truncated);
        Assert.Equal(MacroLimits.MaxBlocks, result.Blocks.Count);
    }

    [Fact]
    public async Task Recorded_macro_validates_and_replays_the_same_input()
    {
        var events = new[]
        {
            E.Move(0, 10, 20), // the recorder notes where the cursor starts
            E.KeyDown(0, LCtrl), E.KeyDown(100, C), E.KeyUp(160, C), E.KeyUp(300, LCtrl),
            E.MouseDown(800, MouseButton.Left, 10, 20), E.MouseUp(850, MouseButton.Left, 10, 20),
            E.KeyDown(1000, W), E.KeyUp(2000, W),
            E.Wheel(2500, -120),
        };
        var doc = new MacroDoc { Name = "Recorded", Blocks = RecordingBuilder.Build(events).Blocks };
        Assert.Empty(MacroValidator.Validate(doc));

        var input = new FakeInput();
        var delay = new FakeDelay();
        var run = new MacroRun(MacroCompiler.Compile(doc), new RunEnvironment { Input = input, Delay = delay });
        run.Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RunState.Stopped, run.State);
        // The first click glides from wherever the cursor is (moves elided here) and lands exactly on the spot.
        var log = input.Log;
        Assert.Equal(
            ["down LeftCtrl", "down C", "up C", "up LeftCtrl", "move 10,20", "mdown Left", "mup Left", "down W", "up W", "scroll -1"],
            log.Where((l, i) => !l.StartsWith("move", StringComparison.Ordinal) || log[i + 1] == "mdown Left").ToList());
        Assert.True(log.Count(l => l.StartsWith("move", StringComparison.Ordinal)) > 3, "the cursor should glide, not jump");
        // Replayed time matches the recording: 2500 ms of events (click hold and multi-click gaps included).
        Assert.InRange(delay.TotalMs, 2450, 2550);
    }
}
