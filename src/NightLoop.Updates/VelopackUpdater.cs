using Velopack;
using Velopack.Sources;

namespace NightLoop.Updates;

/// <summary>Velopack against the repository's GitHub Releases (schema section 9). Stable releases only.</summary>
public sealed class VelopackUpdater : IUpdater
{
    private readonly UpdateManager _manager = new(new GithubSource(GitHubRepo.Url, accessToken: null, prerelease: false));
    private readonly string _fallbackVersion;

    /// <param name="fallbackVersion">Shown when the app isn't installed and Velopack has no version for it.</param>
    public VelopackUpdater(string fallbackVersion) => _fallbackVersion = fallbackVersion;

    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? _fallbackVersion;

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = await _manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        if (info is null) return null;
        var target = info.TargetFullRelease;
        return new AvailableUpdate(target.Version.ToString(), target.NotesMarkdown, info);
    }

    public Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken ct) =>
        _manager.DownloadUpdatesAsync((UpdateInfo)update.Handle, progress, ct);

    public void ApplyAndRestart(AvailableUpdate update) =>
        _manager.ApplyUpdatesAndRestart(((UpdateInfo)update.Handle).TargetFullRelease);
}
