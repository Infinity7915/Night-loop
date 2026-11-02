using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.Contracts.Tests;

public class MacroValidatorTests
{
    private const string SchemaExample = """
        {
          "schema": 1,
          "name": "Coin farm",
          "targetGame": "Roblox",
          "options": {
            "hotkeyStart": "F6",
            "hotkeyStop": "F7",
            "triggerMode": "toggle",
            "runAlongside": true,
            "repeat": { "mode": "forever" },
            "humanize": { "enabled": true, "timingJitterPct": 15, "mousePx": 2 }
          },
          "blocks": [
            { "id": "b1", "type": "window.resize", "enabled": true, "breakpoint": false,
              "params": { "width": 1280, "height": 720 } },
            { "id": "b2", "type": "logic.if", "enabled": true, "breakpoint": false,
              "params": { "condition": { "kind": "textVisible", "text": "+1 Coin", "region": [100, 80, 400, 160] } } },
            { "id": "b3", "type": "input.click", "enabled": true, "breakpoint": false,
              "params": { "button": "left", "x": 640, "y": 360, "relativeTo": "gameWindow" } },
            { "id": "b4", "type": "logic.end", "enabled": true, "breakpoint": false, "params": {} }
          ]
        }
        """;

    private static MacroDoc Doc(params Block[] blocks) => new() { Name = "Test", Blocks = [.. blocks] };

    private static Block B(string id, string type, JsonObject? p = null) => Block.Create(id, type, p);

    [Fact]
    public void Schema_example_from_the_plan_is_valid_and_round_trips()
    {
        var doc = MacroJson.Deserialize(SchemaExample);
        Assert.Empty(MacroValidator.Validate(doc));
        Assert.Equal(RepeatModes.Forever, doc.Options.Repeat.Mode);
        Assert.Equal(4, doc.Blocks.Count);
        Assert.Equal(640, doc.Blocks[2].GetInt("x"));

        var again = MacroJson.Deserialize(MacroJson.Serialize(doc));
        Assert.Equal(MacroJson.Serialize(doc), MacroJson.Serialize(again));
    }

    [Fact]
    public void Unknown_block_type_is_rejected()
    {
        var issues = MacroValidator.Validate(Doc(B("b1", "input.teleport")));
        Assert.Contains(issues, i => i.BlockId == "b1" && i.Message.Contains("Unknown block type"));
    }

    [Fact]
    public void Newer_schema_is_rejected()
    {
        var doc = Doc();
        doc.Schema = 99;
        Assert.NotEmpty(MacroValidator.Validate(doc));
    }

    [Fact]
    public void Duplicate_ids_are_rejected()
    {
        var wait = new JsonObject { ["ms"] = 10 };
        var issues = MacroValidator.Validate(Doc(B("b1", BlockTypes.Wait, wait), B("b1", BlockTypes.Wait, wait.DeepClone().AsObject())));
        Assert.Contains(issues, i => i.Message.Contains("unique"));
    }

    [Fact]
    public void Unclosed_if_and_stray_end_and_else_are_rejected()
    {
        var cond = new JsonObject { ["kind"] = "windowFocused" };
        Assert.NotEmpty(MacroValidator.Validate(Doc(B("b1", BlockTypes.If, new JsonObject { ["condition"] = cond }))));
        Assert.NotEmpty(MacroValidator.Validate(Doc(B("b1", BlockTypes.End))));
        Assert.NotEmpty(MacroValidator.Validate(Doc(B("b1", BlockTypes.Loop, new JsonObject { ["count"] = 2 }), B("b2", BlockTypes.Else), B("b3", BlockTypes.End))));
    }

    [Fact]
    public void Goto_must_point_at_a_label()
    {
        var issues = MacroValidator.Validate(Doc(B("b1", BlockTypes.Goto, new JsonObject { ["name"] = "nowhere" })));
        Assert.Contains(issues, i => i.Message.Contains("label"));
    }

    [Fact]
    public void Nesting_deeper_than_16_is_rejected()
    {
        var blocks = new List<Block>();
        for (var i = 0; i < 17; i++) blocks.Add(B($"o{i}", BlockTypes.Loop, new JsonObject { ["count"] = 1 }));
        for (var i = 0; i < 17; i++) blocks.Add(B($"e{i}", BlockTypes.End));
        Assert.Contains(MacroValidator.Validate(Doc([.. blocks])), i => i.Message.Contains("16"));
    }

    [Theory]
    [InlineData(BlockTypes.Wait, "ms", 0)]
    [InlineData(BlockTypes.Wait, "ms", 3_600_001)]
    [InlineData(BlockTypes.Loop, "count", 100_001)]
    [InlineData(BlockTypes.Scroll, "amount", 51)]
    public void Out_of_range_numbers_are_rejected(string type, string param, int value)
    {
        var blocks = new List<Block> { B("b1", type, new JsonObject { [param] = value }) };
        if (type == BlockTypes.Loop) blocks.Add(B("b2", BlockTypes.End));
        Assert.Contains(MacroValidator.Validate(Doc([.. blocks])), i => i.BlockId == "b1");
    }

    [Fact]
    public void Too_many_blocks_are_rejected()
    {
        var blocks = Enumerable.Range(1, MacroLimits.MaxBlocks + 1).Select(i => B($"b{i}", BlockTypes.Comment)).ToArray();
        Assert.Contains(MacroValidator.Validate(Doc(blocks)), i => i.Message.Contains("500"));
    }

    [Fact]
    public void Unknown_key_is_rejected()
    {
        var issues = MacroValidator.Validate(Doc(B("b1", BlockTypes.Key, new JsonObject { ["key"] = "Banana" })));
        Assert.Contains(issues, i => i.Message.Contains("Banana"));
    }

    [Theory]
    [InlineData("Ctrl+F6", HotkeyModifiers.Ctrl, 0x75)]
    [InlineData("F7", HotkeyModifiers.None, 0x76)]
    [InlineData("ctrl + alt + end", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x23)]
    public void Hotkeys_parse(string text, HotkeyModifiers mods, int vk)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(new Hotkey(mods, vk), hk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("F6+F7")]
    [InlineData("Hyper+Q")]
    public void Bad_hotkeys_do_not_parse(string text) => Assert.False(Hotkey.TryParse(text, out _));
}
