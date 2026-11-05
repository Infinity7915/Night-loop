using System.Globalization;
using System.Text;
using NightLoop.Contracts.Api;

namespace NightLoop.Client;

/// <summary>Turns the account's data summary into the plain text file "Download my data" saves.</summary>
public static class AccountDataText
{
    public static string FileName(AccountDataResponse data, DateTimeOffset now)
    {
        // Windows' rules, spelled out so they hold wherever this runs.
        const string bad = "<>:\"/\\|?*";
        var safe = new string(data.Username.Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (safe.Length == 0) safe = "account";
        return $"Night-loop data - {safe} - {now.ToLocalTime():yyyy-MM-dd}.txt";
    }

    public static string Format(AccountDataResponse data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Night-loop account data");
        sb.AppendLine("=======================");
        Line(sb, "Downloaded:", $"{Date(data.ExportedAt)}");
        sb.AppendLine();
        Line(sb, "Email:", $"{data.Email}");
        Line(sb, "Password:", $"{(data.PasswordSet ? "set (hidden)" : "not set")}");
        if (data.PasswordChangedAt is { } changed)
            Line(sb, "Password changed:", $"{Date(changed)}");
        Line(sb, "Username:", $"{data.Username} (user #{data.UserNumber})");
        Line(sb, "Account created:", $"{Date(data.CreatedAt)}");
        Line(sb, "Time using Night-loop:", $"{Duration(data.UsageSeconds)} ({data.UsageSeconds} seconds)");
        Line(sb, "Macros on account:", $"{data.MacroCount}");
        sb.AppendLine();
        sb.AppendLine("Why the password is hidden: Night-loop never stores your password. The server keeps only a");
        sb.AppendLine("one-way Argon2id hash of it, which can check a password but can't be turned back into one,");
        sb.AppendLine("so nobody (not even Night-loop) can read it. If you've forgotten it, use \"Forgot password\".");
        sb.AppendLine();
        sb.AppendLine("Macros: this file lists how many macros your account holds, not the macros themselves.");
        sb.AppendLine("Macros made with Night-loop stay on the Night-loop service and can't be exported or imported");
        sb.AppendLine("(see the Terms of Service, section 5).");
        return sb.ToString();
    }

    private static void Line(StringBuilder sb, string label, string value) => sb.Append(label.PadRight(24)).AppendLine(value);

    private static string Date(DateTimeOffset when) =>
        when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (your local time)";

    /// <summary>"3 h 25 min", "12 min", "under a minute".</summary>
    public static string Duration(long seconds)
    {
        if (seconds < 60) return "under a minute";
        var t = TimeSpan.FromSeconds(seconds);
        var hours = (long)t.TotalHours;
        return hours > 0 ? $"{hours} h {t.Minutes} min" : $"{t.Minutes} min";
    }
}
