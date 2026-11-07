using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Compiler;
using NightLoop.Engine.Runtime;

namespace NightLoop.Engine.Tests;

public class BlockWaitsTests
{
    private static async Task<long> WaitedAsync(MacroDoc doc, BlockWaits? waits)
    {
        var delay = new FakeDelay();
        var run = new MacroRun(MacroCompiler.Compile(doc), new RunEnvironment { Input = new FakeInput(), Delay = delay, Waits = waits });
        run.Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunState.Stopped, run.State);
        return delay.TotalMs;
    }

    private static MacroBuilder Click(MacroBuilder b, int? waitAfterMs = null)
    {
        var p = new System.Text.Json.Nodes.JsonObject { ["button"] = "left", ["holdMs"] = 20 };
        if (waitAfterMs is { } w) p[BlockTypes.WaitAfterKey] = w;
        return b.Add(BlockTypes.Click, p);
    }

    [Fact]
    public async Task Both_sliders_at_zero_change_nothing()
    {
        var doc = Click(new MacroBuilder().Press("A")).Wait(100).Build();
        Assert.Equal(await WaitedAsync(doc, null), await WaitedAsync(doc, new BlockWaits()));
    }

    [Fact]
    public async Task Wait_after_every_block_skips_clicks_while_their_wait_is_zero()
    {
        // Key, Click, Wait: the key and the wait get 50 each, the click (wait after click 0) gets nothing.
        var doc = Click(new MacroBuilder().Press("A")).Wait(100).Build();
        var plain = await WaitedAsync(doc, null);
        Assert.Equal(plain + 100, await WaitedAsync(doc, new BlockWaits { AfterBlocksMs = 50 }));
    }

    [Fact]
    public async Task Wait_after_clicks_follows_settings_unless_the_block_sets_its_own()
    {
        var doc = Click(Click(new MacroBuilder()), waitAfterMs: 30).Build();
        var plain = await WaitedAsync(doc, null);
        // First click follows Settings (200), second has its own (30).
        Assert.Equal(plain + 230, await WaitedAsync(doc, new BlockWaits { AfterClicksMs = 200 }));
    }

    [Fact]
    public async Task Clicks_with_a_wait_also_get_the_every_block_wait()
    {
        var doc = Click(Click(new MacroBuilder()), waitAfterMs: 0).Build();
        var plain = await WaitedAsync(doc, null);
        // First click: 100 + 40. Second click set to 0 by hand: nothing at all.
        Assert.Equal(plain + 140, await WaitedAsync(doc, new BlockWaits { AfterClicksMs = 100, AfterBlocksMs = 40 }));
    }

    [Fact]
    public void Comments_labels_and_failed_finds_are_handled()
    {
        var waits = new BlockWaits { AfterClicksMs = 100, AfterBlocksMs = 40 };
        Assert.Equal(0, waits.After(Block.Create("c", BlockTypes.Comment), found: true));
        Assert.Equal(0, waits.After(Block.Create("l", BlockTypes.Label, new() { ["name"] = "x" }), found: true));
        var find = Block.Create("f", BlockTypes.FindClick, new() { ["target"] = "text", ["value"] = "Claim" });
        Assert.Equal(140, waits.After(find, found: true));
        Assert.Equal(40, waits.After(find, found: false)); // didn't click: an ordinary block
        var scanOnly = Block.Create("s", BlockTypes.ScanTurn, new() { ["whenFound"] = "center" });
        Assert.Equal(40, waits.After(scanOnly, found: true));
    }

    [Fact]
    public void Values_are_kept_in_range()
    {
        var waits = new BlockWaits { AfterClicksMs = -5, AfterBlocksMs = 999_999 };
        Assert.Equal(0, waits.AfterClicksMs);
        Assert.Equal(MacroLimits.WaitAfterMaxMs, waits.AfterBlocksMs);
    }
}
