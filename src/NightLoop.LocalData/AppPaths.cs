namespace NightLoop.LocalData;

/// <summary>%LOCALAPPDATA%\NightLoop\ and the files in it (schema section 8, "Local files").</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NightLoop");

    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Games => Path.Combine(Root, "games.json");
    public static string Logs => Path.Combine(Root, "logs");
}
