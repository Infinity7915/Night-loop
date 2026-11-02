namespace NightLoop.Updates;

public enum UpdateStage
{
    /// <summary>Running from a build folder: updates only work in the installed app.</summary>
    NotInstalled,
    /// <summary>Not checked yet.</summary>
    Idle,
    Checking,
    UpToDate,
    Downloading,
    /// <summary>Downloaded. Restarting the app installs it.</summary>
    ReadyToRestart,
    /// <summary>The check or download failed. The current version keeps working; the next check tries again.</summary>
    Failed,
}

/// <summary>Everything the Home and Settings screens show about updates.</summary>
public sealed record UpdateState(
    UpdateStage Stage,
    string CurrentVersion,
    string? NewVersion = null,
    string? Notes = null,
    int Progress = 0,
    DateTimeOffset? CheckedAt = null,
    string? Error = null);

/// <summary>
/// Checks for a newer release, downloads it in the background and restarts into it when asked (schema section 9,
/// "On the client"). One check runs at a time; asking again while one runs joins it.
/// </summary>
public sealed class UpdateService
{
    private readonly IUpdater _updater;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Task<bool>? _running;
    private AvailableUpdate? _downloaded;

    public UpdateService(IUpdater updater, TimeProvider? time = null)
    {
        _updater = updater;
        _time = time ?? TimeProvider.System;
        State = new UpdateState(updater.IsInstalled ? UpdateStage.Idle : UpdateStage.NotInstalled, updater.CurrentVersion);
    }

    public UpdateState State { get; private set; }

    /// <summary>Raised on whatever thread the work is on.</summary>
    public event Action<UpdateState>? Changed;

    public bool IsReadyToRestart => _downloaded is not null;

    /// <summary>
    /// Looks for a newer release and downloads it. True when one is downloaded and a restart will install it.
    /// Never throws for network problems: those end in <see cref="UpdateStage.Failed"/>.
    /// </summary>
    public Task<bool> CheckAndDownloadAsync(CancellationToken ct = default)
    {
        if (!_updater.IsInstalled) return Task.FromResult(false);
        lock (_gate)
        {
            if (_running is { IsCompleted: false }) return _running;
            return _running = RunAsync(ct);
        }
    }

    private async Task<bool> RunAsync(CancellationToken ct)
    {
        await Task.Yield();
        var downloaded = _downloaded;
        Set(State with { Stage = UpdateStage.Checking, Error = null });
        try
        {
            var update = await _updater.CheckAsync(ct).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            if (update is null)
            {
                _downloaded = null;
                Set(State with { Stage = UpdateStage.UpToDate, NewVersion = null, Notes = null, Progress = 0, CheckedAt = now });
                return false;
            }

            if (downloaded is not null && downloaded.Version == update.Version)
            {
                Set(State with { Stage = UpdateStage.ReadyToRestart, CheckedAt = now });
                return true;
            }

            Set(State with { Stage = UpdateStage.Downloading, NewVersion = update.Version, Notes = update.Notes, Progress = 0, CheckedAt = now });
            await _updater.DownloadAsync(update, p => Set(State with { Progress = Math.Clamp(p, 0, 100) }), ct).ConfigureAwait(false);
            _downloaded = update;
            Set(State with { Stage = UpdateStage.ReadyToRestart, Progress = 100 });
            return true;
        }
        catch (OperationCanceledException)
        {
            Set(State with { Stage = downloaded is null ? UpdateStage.Idle : UpdateStage.ReadyToRestart });
            return downloaded is not null;
        }
        catch (Exception ex)
        {
            // Offline, GitHub rate limit, a broken download... the installed version keeps working either way.
            Set(State with { Stage = downloaded is null ? UpdateStage.Failed : UpdateStage.ReadyToRestart, Error = ex.Message, CheckedAt = _time.GetUtcNow() });
            return downloaded is not null;
        }
    }

    /// <summary>Installs the downloaded release and restarts. Does nothing when nothing is downloaded.</summary>
    public void RestartToApply()
    {
        if (_downloaded is { } update) _updater.ApplyAndRestart(update);
    }

    private void Set(UpdateState state)
    {
        State = state;
        Changed?.Invoke(state);
    }
}
