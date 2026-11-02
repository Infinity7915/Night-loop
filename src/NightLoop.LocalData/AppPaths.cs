namespace NightLoop.LocalData;

/// <summary>%LOCALAPPDATA%\NightLoop\ and the files in it (schema section 8, "Local files").</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NightLoop");

    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Games => Path.Combine(Root, "games.json");
    public static string Logs => Path.Combine(Root, "logs");
    /// <summary>Session and trust tokens, encrypted with DPAPI for the current Windows user.</summary>
    public static string Session => Path.Combine(Root, "session.bin");
    /// <summary>Random GUID made on first run; identifies this install to the server.</summary>
    public static string InstallId => Path.Combine(Root, "install.id");
    /// <summary>Last announcements text fetched from GitHub, so Home still shows it offline.</summary>
    public static string Announcements => Path.Combine(Root, "announcements.md");
}
