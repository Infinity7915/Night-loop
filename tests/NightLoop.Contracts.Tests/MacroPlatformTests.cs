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
