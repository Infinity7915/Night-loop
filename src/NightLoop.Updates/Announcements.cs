using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace NightLoop.Updates;

public enum AnnouncementBlockKind { Heading, Bullet, Paragraph }

public sealed record AnnouncementBlock(AnnouncementBlockKind Kind, string Text);

/// <param name="FetchedAt">When this text came from GitHub (for cached text, when it was cached).</param>
/// <param name="FromCache">True when GitHub couldn't be reached and this is the last copy seen.</param>
public sealed record Announcement(IReadOnlyList<AnnouncementBlock> Blocks, DateTimeOffset FetchedAt, bool FromCache)
{
    public bool IsEmpty => Blocks.Count == 0;
}

/// <summary>
/// The Home screen's announcements: <c>announcements.md</c> on the repository's main branch. Edit that file on
/// GitHub and every app shows the new text the next time Home loads or is refreshed. The last copy is kept on
/// this PC so it still shows offline.
/// </summary>
public sealed partial class AnnouncementFeed(HttpClient http, string cachePath, TimeProvider? time = null)
{
    /// <summary>Anything longer is cut, so a pasted log can't bury the Home screen.</summary>
    public const int MaxChars = 8000;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The newest text from GitHub, else the cached copy, else null.</summary>
    public async Task<Announcement?> LoadAsync(CancellationToken ct = default)
    {
        var text = await TryGetAsync(GitHubRepo.AnnouncementsApiUrl, "application/vnd.github.raw", ct).ConfigureAwait(false)
                   ?? await TryGetAsync(GitHubRepo.AnnouncementsRawUrl, "text/plain", ct).ConfigureAwait(false);
        if (text is null) return LoadCached();

        Save(text);
        return new Announcement(Parse(text), _time.GetUtcNow(), FromCache: false);
    }

    public Announcement? LoadCached()
    {
        try
        {
            if (!File.Exists(cachePath)) return null;
            return new Announcement(Parse(File.ReadAllText(cachePath)), new DateTimeOffset(File.GetLastWriteTimeUtc(cachePath)), FromCache: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<string?> TryGetAsync(Uri url, string accept, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Night-loop", null));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            // 404 means the file was deleted: no announcements, not an error.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return "";
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private void Save(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, text, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only the offline copy is lost.
        }
    }

    /// <summary>
    /// A small, forgiving slice of Markdown: <c># Heading</c>, <c>- bullet</c>, and paragraphs separated by a
    /// blank line. <c>&lt;!-- comments --&gt;</c> are hidden, and <c>**bold**</c>/<c>`code`</c> marks are dropped.
    /// </summary>
    public static IReadOnlyList<AnnouncementBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];
        var text = CommentRegex().Replace(markdown, "");
        if (text.Length > MaxChars) text = text[..MaxChars] + "…";

        var blocks = new List<AnnouncementBlock>();
        var paragraph = new StringBuilder();
        void Flush()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new AnnouncementBlock(AnnouncementBlockKind.Paragraph, paragraph.ToString()));
            paragraph.Clear();
        }

        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                Flush();
            }
            else if (line.StartsWith('#'))
            {
                Flush();
                var heading = Clean(line.TrimStart('#'));
                if (heading.Length > 0) blocks.Add(new AnnouncementBlock(AnnouncementBlockKind.Heading, heading));
            }
            else if (line.Length > 1 && line[0] is '-' or '*' or '+' && line[1] == ' ')
            {
                Flush();
                blocks.Add(new AnnouncementBlock(AnnouncementBlockKind.Bullet, Clean(line[2..])));
            }
            else if (line.Trim('-', '*', '_', ' ').Length == 0)
            {
                Flush(); // a --- divider line
            }
            else
            {
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(Clean(line));
            }
        }
        Flush();
        return blocks;
    }

    private static string Clean(string s) => MarkRegex().Replace(s, "").Trim();

    [GeneratedRegex("<!--.*?(-->|$)", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"\*\*|__|`")]
    private static partial Regex MarkRegex();
}
