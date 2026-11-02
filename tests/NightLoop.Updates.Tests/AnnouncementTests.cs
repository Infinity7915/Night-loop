using System.Net;

namespace NightLoop.Updates.Tests;

public sealed class AnnouncementTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nl-ann-" + Guid.NewGuid().ToString("N"));
    private string CachePath => Path.Combine(_dir, "announcements.md");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Text(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s) };

    [Fact]
    public void Parses_headings_bullets_and_paragraphs()
    {
        var blocks = AnnouncementFeed.Parse("""
            <!-- Edit this file on GitHub.
                 This comment is hidden. -->
            # Version 0.2 is out

            Replay is **faster**
            and smoother.

            - New `Home` page
            * Smooth tabs
            ---
            """);

        Assert.Equal(
            [
                new(AnnouncementBlockKind.Heading, "Version 0.2 is out"),
                new(AnnouncementBlockKind.Paragraph, "Replay is faster and smoother."),
                new(AnnouncementBlockKind.Bullet, "New Home page"),
                new(AnnouncementBlockKind.Bullet, "Smooth tabs"),
            ],
            blocks);
    }

    [Fact]
    public void Only_comments_means_no_announcements() =>
        Assert.Empty(AnnouncementFeed.Parse("<!-- nothing yet -->\n\n"));

    [Fact]
    public void Very_long_text_is_cut()
    {
        var blocks = AnnouncementFeed.Parse(new string('a', AnnouncementFeed.MaxChars + 500));
        Assert.Equal(AnnouncementFeed.MaxChars + 1, blocks.Single().Text.Length);
    }

    [Fact]
    public async Task Loads_from_the_api_and_caches_it()
    {
        var handler = new Handler(_ => Text("Hello everyone"));
        var feed = new AnnouncementFeed(new HttpClient(handler), CachePath);

        var result = await feed.LoadAsync();

        Assert.False(result!.FromCache);
        Assert.Equal("Hello everyone", result.Blocks.Single().Text);
        Assert.Equal(GitHubRepo.AnnouncementsApiUrl, handler.Seen.Single());
        Assert.Equal("Hello everyone", File.ReadAllText(CachePath));
    }

    [Fact]
    public async Task Falls_back_to_raw_when_the_api_is_rate_limited()
    {
        var handler = new Handler(r => r.RequestUri!.Host == "api.github.com" ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Text("From raw"));
        var feed = new AnnouncementFeed(new HttpClient(handler), CachePath);

        var result = await feed.LoadAsync();

        Assert.Equal("From raw", result!.Blocks.Single().Text);
        Assert.Equal(2, handler.Seen.Count);
    }

    [Fact]
    public async Task Offline_shows_the_last_copy()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath, "Old news");
        var feed = new AnnouncementFeed(new HttpClient(new Handler(_ => throw new HttpRequestException("offline"))), CachePath);

        var result = await feed.LoadAsync();

        Assert.True(result!.FromCache);
        Assert.Equal("Old news", result.Blocks.Single().Text);
    }

    [Fact]
    public async Task Deleted_file_clears_announcements()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath, "Old news");
        var feed = new AnnouncementFeed(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), CachePath);

        var result = await feed.LoadAsync();

        Assert.True(result!.IsEmpty);
        Assert.False(result.FromCache);
    }

    [Fact]
    public async Task Nothing_cached_and_offline_is_null()
    {
        var feed = new AnnouncementFeed(new HttpClient(new Handler(_ => throw new HttpRequestException("offline"))), CachePath);
        Assert.Null(await feed.LoadAsync());
    }
}
