using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightLoop.Updates;

namespace NightLoop.App.ViewModels;

/// <summary>
/// What the Home "Updates" card and Settings → Updates show, and the Check now / Restart buttons. Updates come
/// from GitHub Releases, so they work even when the Night-loop server is down.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly UpdateService _service;
    private readonly Func<bool> _isBusy;
    private readonly Func<Task> _prepareForRestart;

    /// <param name="isBusy">True while a macro runs or records: then the app never restarts by itself.</param>
    /// <param name="prepareForRestart">Saves drafts and stops macros before the app closes.</param>
    public UpdatesViewModel(UpdateService service, Func<bool> isBusy, Func<Task> prepareForRestart)
    {
        _service = service;
        _isBusy = isBusy;
        _prepareForRestart = prepareForRestart;
        _service.Changed += _ => OnUi(Refresh);
        Refresh();
    }

    public UpdateStage Stage => _service.State.Stage;
    public string CurrentVersion => _service.State.CurrentVersion;
    public string VersionText => $"v{CurrentVersion}";

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private int _progress;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private bool _canRestart;
    [ObservableProperty] private string _notes = "";

    /// <summary>
    /// Checks GitHub and downloads a newer release. With <paramref name="restartWhenReady"/> the app then restarts
    /// into it right away, unless a macro is running or recording (then the Restart button waits for the user).
    /// </summary>
    public async Task CheckAsync(bool restartWhenReady, CancellationToken ct = default)
    {
        var ready = await _service.CheckAndDownloadAsync(ct);
        if (ready && restartWhenReady && !_isBusy()) await RestartAsync();
        else Refresh();
    }

    [RelayCommand]
    private Task CheckNowAsync() => CheckAsync(restartWhenReady: true);

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (!_service.IsReadyToRestart) return;
        Title = "Installing update…";
        Detail = "Night-loop will open again in a moment.";
        await _prepareForRestart();
        try
        {
            _service.RestartToApply();
        }
        catch (Exception ex)
        {
            // Macros are already stopped at this point, so ask for a manual restart rather than carry on.
            Title = "Couldn't install the update";
            Detail = $"{ex.Message} Close and reopen Night-loop to finish updating.";
        }
    }

    [RelayCommand]
    private static void OpenReleases() =>
        Process.Start(new ProcessStartInfo(GitHubRepo.ReleasesUrl) { UseShellExecute = true });

    /// <summary>Re-reads the state (also when a macro starts or stops, since that changes the wording).</summary>
    public void Refresh()
    {
        var s = _service.State;
        Progress = s.Progress;
        IsWorking = s.Stage is UpdateStage.Checking or UpdateStage.Downloading;
        CanRestart = s.Stage == UpdateStage.ReadyToRestart;
        Notes = s.Stage is UpdateStage.Downloading or UpdateStage.ReadyToRestart ? s.Notes?.Trim() ?? "" : "";
        var checkedAt = s.CheckedAt is { } at ? $"Checked {at.ToLocalTime():t}" : "Checked at launch";
        (Title, Detail) = s.Stage switch
        {
            UpdateStage.NotInstalled => ("Updates off", $"{VersionText} · running from a build folder. Install from GitHub Releases to get updates."),
            UpdateStage.Idle => ("Not checked yet", $"{VersionText} · checks GitHub Releases on launch"),
            UpdateStage.Checking => ("Checking…", $"{VersionText} · looking at GitHub Releases"),
            UpdateStage.UpToDate => ("Up to date", $"{checkedAt} · {VersionText}"),
            UpdateStage.Downloading => ($"Downloading v{s.NewVersion}", $"{s.Progress}% · only the changed parts are downloaded"),
            UpdateStage.ReadyToRestart => ($"Update ready: v{s.NewVersion}",
                _isBusy() ? "A macro is running, so it waits for you. Press Restart when you're done."
                          : "Press Restart to install it. Takes a few seconds."),
            UpdateStage.Failed => ("Couldn't check for updates", $"{s.Error} · {VersionText} keeps working, it tries again later"),
            _ => ("", ""),
        };
        OnPropertyChanged(nameof(Stage));
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
