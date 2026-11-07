using NightLoop.Contracts.MacroDocument;

namespace NightLoop.Contracts.Tests;

public class MacroPlatformTests
{
    private static MacroDoc Doc(string? platform) => new()
    {
        Name = "Test",
        Platform = platform,
        Blocks = [Block.Create("b1", BlockTypes.Wait, new() { ["ms"] = 100 })],
    };

    [Fact]
    public void Pc_macros_keep_their_old_json()
    {
        var json = MacroJson.Serialize(Doc(null));
        Assert.DoesNotContain("platform", json);
        Assert.Equal(MacroPlatforms.Pc, MacroPlatforms.Of(MacroJson.Deserialize(json)));
    }

    [Fact]
    public void Android_macros_round_trip()
    {
        var back = MacroJson.Deserialize(MacroJson.Serialize(Doc(MacroPlatforms.Android)));
        Assert.Equal(MacroPlatforms.Android, back.Platform);
        Assert.Empty(MacroValidator.Validate(back));
    }

    [Fact]
    public void Pc_is_stored_as_missing()
    {
        var doc = Doc(MacroPlatforms.Pc);
        MacroSanitizer.Clean(doc);
        Assert.Null(doc.Platform);
    }

    [Theory]
    [InlineData("ios")]
    [InlineData("Android")]
    [InlineData("")]
    public void Unknown_platforms_are_refused(string platform) =>
        Assert.NotEmpty(MacroValidator.Validate(Doc(platform)));
}

public class PlatformBlockTests
{
    private static MacroDoc Doc(string? platform, params Block[] blocks) => new() { Name = "Test", Platform = platform, Blocks = [.. blocks] };

    private static Block Tap() => Block.Create("t", BlockTypes.Tap, new() { ["x"] = 10, ["y"] = 20 });
    private static Block Key() => Block.Create("k", BlockTypes.Key, new() { ["key"] = "A" });

    [Fact]
    public void Android_macros_take_touch_blocks_but_not_keys()
    {
        Assert.Empty(MacroValidator.Validate(Doc(MacroPlatforms.Android, Tap())));
        Assert.NotEmpty(MacroValidator.Validate(Doc(MacroPlatforms.Android, Key())));
    }

    [Fact]
    public void Pc_macros_take_keys_but_not_touch_blocks()
    {
        Assert.Empty(MacroValidator.Validate(Doc(null, Key())));
        Assert.NotEmpty(MacroValidator.Validate(Doc(null, Tap())));
    }

    [Fact]
    public void Every_block_type_belongs_somewhere()
    {
        foreach (var type in BlockTypes.All)
            Assert.True(MacroPlatforms.Allows(MacroPlatforms.Pc, type) || MacroPlatforms.Allows(MacroPlatforms.Android, type), type);
    }

    [Fact]
    public void App_in_front_is_an_android_condition()
    {
        Block If() => Block.Create("i", BlockTypes.If, new() { ["condition"] = new System.Text.Json.Nodes.JsonObject
        {
            ["kind"] = "not",
            ["condition"] = new System.Text.Json.Nodes.JsonObject { ["kind"] = "appInFront", ["package"] = "com.roblox.client" },
        } });
        Block End() => Block.Create("e", BlockTypes.End);
        Assert.Empty(MacroValidator.Validate(Doc(MacroPlatforms.Android, If(), End())));
        Assert.NotEmpty(MacroValidator.Validate(Doc(null, If(), End())));
    }

    [Theory]
    [InlineData("com.roblox.client", true)]
    [InlineData("", false)]
    [InlineData("com/evil", false)]
    public void Open_app_needs_a_package_name(string package, bool ok)
    {
        var doc = Doc(MacroPlatforms.Android, Block.Create("a", BlockTypes.AppOpen, new() { ["package"] = package }));
        Assert.Equal(ok, MacroValidator.Validate(doc).Count == 0);
    }

    [Fact]
    public void The_server_keeps_the_package_setting()
    {
        var doc = Doc(MacroPlatforms.Android, Block.Create("a", BlockTypes.AppOpen, new() { ["package"] = "com.roblox.client" }));
        MacroSanitizer.Clean(doc);
        Assert.Equal("com.roblox.client", doc.Blocks[0].GetString("package"));
    }
}
