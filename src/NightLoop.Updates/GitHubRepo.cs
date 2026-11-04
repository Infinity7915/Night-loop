namespace NightLoop.Updates;

/// <summary>
/// The public GitHub repository that ships Night-loop. Releases there are the app's updates, and
/// <see cref="AnnouncementsFile"/> on the main branch is the Home screen's announcements text.
/// Neither needs the Night-loop server.
/// </summary>
public static class GitHubRepo
{
    public const string Owner = "Infinity7915";
    public const string Name = "Night-loop";
    public const string Branch = "main";
    public const string AnnouncementsFile = "announcements.md";

    public static string Url => $"https://github.com/{Owner}/{Name}";
    public static string ReleasesUrl => $"{Url}/releases";

    /// <summary>The Terms of Service file, next to the announcements file on the main branch.</summary>
    public const string TermsFile = "terms-of-service.md";

    /// <summary>The official Terms of Service page, linked from the create-account screen.</summary>
    public static string TermsUrl => $"{Url}/blob/{Branch}/{TermsFile}";

    /// <summary>The contents API answers with the latest commit right away (60 requests an hour per IP).</summary>
    public static Uri AnnouncementsApiUrl =>
        new($"https://api.github.com/repos/{Owner}/{Name}/contents/{AnnouncementsFile}?ref={Branch}");

    /// <summary>Fallback when the API is rate limited. GitHub caches this for up to about 5 minutes.</summary>
    public static Uri AnnouncementsRawUrl =>
        new($"https://raw.githubusercontent.com/{Owner}/{Name}/{Branch}/{AnnouncementsFile}");
}
