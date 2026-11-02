namespace NightLoop.Updates.Tests;

public sealed class UpdateServiceTests
{
    private sealed class FakeUpdater : IUpdater
    {
        public bool IsInstalled { get; set; } = true;
        public string CurrentVersion => "0.1.0";
        public AvailableUpdate? Next { get; set; }
        public Exception? CheckFails { get; set; }
        public Exception? DownloadFails { get; set; }
        public TaskCompletionSource? DownloadGate { get; set; }
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public AvailableUpdate? Applied { get; private set; }

        public Task<AvailableUpdate?> CheckAsync(CancellationToken ct)
        {
            Checks++;
            return CheckFails is null ? Task.FromResult(Next) : Task.FromException<AvailableUpdate?>(CheckFails);
        }

        public async Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken ct)
        {
            Downloads++;
            progress(50);
            if (DownloadGate is not null) await DownloadGate.Task;
            if (DownloadFails is not null) throw DownloadFails;
            progress(100);
        }

        public void ApplyAndRestart(AvailableUpdate update) => Applied = update;
    }

    [Fact]
    public async Task Not_installed_never_checks()
    {
        var fake = new FakeUpdater { IsInstalled = false, Next = new("0.2.0", null, new()) };
        var service = new UpdateService(fake);

        Assert.Equal(UpdateStage.NotInstalled, service.State.Stage);
        Assert.False(await service.CheckAndDownloadAsync());
        Assert.Equal(0, fake.Checks);
    }

    [Fact]
    public async Task Up_to_date_when_nothing_newer()
    {
        var service = new UpdateService(new FakeUpdater());

        Assert.False(await service.CheckAndDownloadAsync());
        Assert.Equal(UpdateStage.UpToDate, service.State.Stage);
        Assert.NotNull(service.State.CheckedAt);
    }

    [Fact]
    public async Task Newer_release_is_downloaded_then_applied_on_restart()
    {
        var fake = new FakeUpdater { Next = new("0.2.0", "Faster replay", new()) };
        var service = new UpdateService(fake);
        var stages = new List<UpdateStage>();
        service.Changed += s => stages.Add(s.Stage);

        Assert.True(await service.CheckAndDownloadAsync());
        Assert.Equal(UpdateStage.ReadyToRestart, service.State.Stage);
        Assert.Equal("0.2.0", service.State.NewVersion);
        Assert.Equal("Faster replay", service.State.Notes);
        Assert.Contains(UpdateStage.Downloading, stages);

        service.RestartToApply();
        Assert.Equal("0.2.0", fake.Applied?.Version);
    }

    [Fact]
    public async Task Same_release_is_not_downloaded_twice()
    {
        var fake = new FakeUpdater { Next = new("0.2.0", null, new()) };
        var service = new UpdateService(fake);

        await service.CheckAndDownloadAsync();
        Assert.True(await service.CheckAndDownloadAsync());
        Assert.Equal(1, fake.Downloads);
        Assert.Equal(2, fake.Checks);
    }

    [Fact]
    public async Task Failure_keeps_current_version_and_next_check_retries()
    {
        var fake = new FakeUpdater { CheckFails = new HttpRequestException("offline") };
        var service = new UpdateService(fake);

        Assert.False(await service.CheckAndDownloadAsync());
        Assert.Equal(UpdateStage.Failed, service.State.Stage);
        Assert.Equal("offline", service.State.Error);

        service.RestartToApply();
        Assert.Null(fake.Applied);

        fake.CheckFails = null;
        fake.Next = new("0.2.0", null, new());
        Assert.True(await service.CheckAndDownloadAsync());
    }

    [Fact]
    public async Task Failed_check_after_download_stays_ready()
    {
        var fake = new FakeUpdater { Next = new("0.2.0", null, new()) };
        var service = new UpdateService(fake);
        await service.CheckAndDownloadAsync();

        fake.CheckFails = new HttpRequestException("offline");
        Assert.True(await service.CheckAndDownloadAsync());
        Assert.Equal(UpdateStage.ReadyToRestart, service.State.Stage);
    }

    [Fact]
    public async Task Overlapping_checks_share_one_run()
    {
        var fake = new FakeUpdater { Next = new("0.2.0", null, new()), DownloadGate = new() };
        var service = new UpdateService(fake);

        var first = service.CheckAndDownloadAsync();
        var second = service.CheckAndDownloadAsync();
        Assert.Same(first, second);

        fake.DownloadGate.SetResult();
        Assert.True(await first);
        Assert.Equal(1, fake.Checks);
    }
}
