using NightLoop.Contracts.Api;

namespace NightLoop.Client.Tests;

public sealed class AccountDataTextTests
{
    private static readonly AccountDataResponse Data = new(
        "cryo@example.com", "Cryo", 1000, UsageSeconds: 12_345, MacroCount: 7, PasswordSet: true,
        PasswordChangedAt: new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero),
        CreatedAt: new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero),
        ExportedAt: new DateTimeOffset(2026, 11, 5, 3, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Lists_email_username_time_and_macro_count_but_never_a_password()
    {
        var text = AccountDataText.Format(Data);
        Assert.Contains("cryo@example.com", text);
        Assert.Contains("Cryo (user #1000)", text);
        Assert.Contains("3 h 25 min (12345 seconds)", text);
        Assert.Contains("Macros on account:      7", text);
        Assert.Contains("Password:               set (hidden)", text);
        Assert.Contains("one-way Argon2id hash", text);
    }

    [Theory]
    [InlineData(0, "under a minute")]
    [InlineData(59, "under a minute")]
    [InlineData(60, "1 min")]
    [InlineData(3_600, "1 h 0 min")]
    [InlineData(90_000, "25 h 0 min")]
    public void Durations_read_naturally(long seconds, string expected) =>
        Assert.Equal(expected, AccountDataText.Duration(seconds));

    [Fact]
    public void File_name_is_safe_for_windows()
    {
        var name = AccountDataText.FileName(Data with { Username = "a/b:c*" }, Data.ExportedAt);
        Assert.StartsWith("Night-loop data - a_b_c_ - ", name);
        Assert.EndsWith(".txt", name);
    }
}
