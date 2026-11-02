using NightLoop.Contracts.Api;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.Contracts.Tests;

public class AccountRulesTests
{
    private const string Every = TwofaModes.EverySignin;
    private const string NewPcs = TwofaModes.NewDevices;

    [Theory]
    // Stricter: no password.
    [InlineData(true, NewPcs, true, Every, false)]
    [InlineData(false, NewPcs, true, NewPcs, false)]
    [InlineData(false, NewPcs, true, Every, false)]
    [InlineData(true, Every, true, Every, false)]
    // Looser: password.
    [InlineData(true, Every, true, NewPcs, true)]
    [InlineData(true, NewPcs, false, NewPcs, true)]
    [InlineData(true, Every, false, Every, true)]
    public void Only_looser_two_factor_changes_need_the_password(bool wasOn, string wasMode, bool on, string mode, bool needs) =>
        Assert.Equal(needs, AccountRules.SecurityChangeNeedsPassword(wasOn, wasMode, on, mode));
}

public class WindowOpenValidationTests
{
    private static MacroDoc Doc(System.Text.Json.Nodes.JsonObject p) =>
        new() { Name = "Test", Blocks = [Block.Create("b1", BlockTypes.WindowOpen, p)] };

    [Fact]
    public void Open_window_needs_a_program()
    {
        var issues = MacroValidator.Validate(Doc(new() { ["path"] = "" }));
        Assert.Contains(issues, i => i.Message.Contains("program", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Open_window_with_a_path_is_valid()
    {
        var issues = MacroValidator.Validate(Doc(new()
        {
            ["path"] = @"%LOCALAPPDATA%\Discord\Update.exe", ["args"] = "--processStart Discord.exe",
            ["ifRunning"] = "focus", ["wait"] = true, ["timeoutMs"] = 30_000,
        }));
        Assert.Empty(issues);
    }
}
