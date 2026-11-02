using System.Text.RegularExpressions;

namespace NightLoop.Contracts.Api;

/// <summary>
/// Account field rules from schema sections 5 and 11. The server enforces them; the app checks them first
/// only to show friendly messages.
/// </summary>
public static partial class AccountRules
{
    public const int EmailMaxLength = 254;
    public const int PasswordMinLength = 12;
    public const int PasswordMaxLength = 128;
    public const int UsernameMinLength = 3;
    public const int UsernameMaxLength = 20;
    public const int MaxUsernameChanges = 3;
    public const int CodeLength = 6;
    public const int CodeLifetimeMinutes = 10;
    public const int CodeMaxAttempts = 5;
    public const int ResendCooldownSeconds = 60;
    public const int SessionDays = 30;
    public const int TrustDays = 30;
    public const int DeviceLabelMaxLength = 64;
    public const int GroupNameMaxLength = 40;
    public const int MaxGroups = 100;

    public static readonly IReadOnlySet<string> ReservedUsernames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "admin", "support", "nightloop", "night-loop", "moderator", "system" };

    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > EmailMaxLength) return false;
        return EmailPattern().IsMatch(email);
    }

    /// <summary>Returns null when the password is acceptable, else a reason. The breach check is server-only.</summary>
    public static string? PasswordProblem(string? password, string? email = null)
    {
        if (password is null || password.Length < PasswordMinLength)
            return $"Use at least {PasswordMinLength} characters.";
        if (password.Length > PasswordMaxLength)
            return $"Use at most {PasswordMaxLength} characters.";
        if (email is not null && string.Equals(password, email, StringComparison.OrdinalIgnoreCase))
            return "Your password can't be your email address.";
        return null;
    }

    /// <summary>Returns null when the username is acceptable, else (error code, reason).</summary>
    public static (string Code, string Message)? UsernameProblem(string? username)
    {
        if (username is null || username.Length < UsernameMinLength || username.Length > UsernameMaxLength || !UsernamePattern().IsMatch(username))
            return (ErrorCodes.InvalidUsername, $"Use {UsernameMinLength}–{UsernameMaxLength} letters, digits, _ or .");
        if (ReservedUsernames.Contains(username) || DefaultUsernamePattern().IsMatch(username))
            return (ErrorCodes.UsernameReserved, "That username is reserved.");
        return null;
    }

    /// <summary>
    /// Whether a two-factor change needs the current password. Making sign-in stricter (turning codes on, or asking
    /// on every start instead of only on new PCs) doesn't: only someone who already has the account open can do it,
    /// and it can only lock a thief out. Making it looser (codes off, or new PCs only) does.
    /// </summary>
    public static bool SecurityChangeNeedsPassword(bool wasEnabled, string wasMode, bool enabled, string mode)
    {
        if (!enabled) return wasEnabled; // turning codes off (or leaving them off)
        if (!wasEnabled) return false;   // turning codes on
        return wasMode == TwofaModes.EverySignin && mode == TwofaModes.NewDevices;
    }

    public static bool IsValidColor(string? color) => color is not null && ColorPattern().IsMatch(color);

    public static bool IsValidCode(string? code) => code is { Length: CodeLength } && code.All(char.IsAsciiDigit);

    // Deliberately simple: one @, something on each side, a dot in the domain, no spaces.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.]+$")]
    private static partial Regex UsernamePattern();

    // "user" followed by digits is how default names look (user#1043), so nobody can pick one.
    [GeneratedRegex(@"^user\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex DefaultUsernamePattern();

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();
}
