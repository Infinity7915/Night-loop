namespace NightLoop.Updates;

/// <summary>A release newer than the running app.</summary>
/// <param name="Version">Its version, for example "0.2.0".</param>
/// <param name="Notes">The GitHub Release text, if any.</param>
/// <param name="Handle">The updater's own object for this release.</param>
public sealed record AvailableUpdate(string Version, string? Notes, object Handle);

/// <summary>Finds, downloads and installs releases. <see cref="VelopackUpdater"/> in the app, a fake in tests.</summary>
public interface IUpdater
{
    /// <summary>False when running from a build folder (dotnet run, Visual Studio). Only installed copies can update.</summary>
    bool IsInstalled { get; }

    string CurrentVersion { get; }

    /// <summary>A newer release, or null when this is the latest.</summary>
    Task<AvailableUpdate?> CheckAsync(CancellationToken ct);

    /// <summary>Downloads only the changed parts when it can. Progress is 0 to 100.</summary>
    Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken ct);

    /// <summary>Closes the app, installs the downloaded release and opens it again. Does not return.</summary>
    void ApplyAndRestart(AvailableUpdate update);
}
