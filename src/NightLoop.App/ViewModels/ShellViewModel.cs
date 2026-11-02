using System.ComponentModel;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NightLoop.Client;
using NightLoop.Contracts.Api;
using NightLoop.LocalData;
using NightLoop.Updates;

namespace NightLoop.App.ViewModels;

/// <summary>
/// The window's current screen: Loading → Sign in → Home / Macros / Settings (schema section 8, "Startup and
/// screens"). Owns the API client, the account session and the updater, and sends the user back to sign-in when
/// the server ends the session.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan MinLoading = TimeSpan.FromSeconds(1.5);
    /// <summary>Launch never waits longer than this to hear back from GitHub. A download, once started, may take longer.</summary>
    private static readonly TimeSpan LaunchCheckTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan LaunchDownloadTimeout = TimeSpan.FromMinutes(3);

    private readonly LocalSettings _settings = new();
    private readonly HttpClient _http;
    private readonly HttpClient _github = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly UpdateService _updateService;
    private readonly DispatcherTimer _updateTimer;

    public ShellViewModel()
    {
        var device = DeviceIdentity.ForThisPc();
        _http = new HttpClient { BaseAddress = _settings.ServerUrl, Timeout = TimeSpan.FromSeconds(15) };
        Api = new ApiClient(_http, device.InstallId, AppVersion);
        Session = new AccountSession(Api, OperatingSystem.IsWindows() ? new DpapiSessionStore() : new MemorySessionStore(), device);
        Session.SignedIn += _ => OnUi(ShowSignedIn);
        Session.SignedOut += e => OnUi(() => OnSignedOut(e));
        Session.UserChanged += user => OnUi(() => Username = user.Username);
        Session.OfflineChanged += offline => OnUi(() => IsOffline = offline);
        Session.OfflineGraceExpired += () => OnUi(() => Macros?.StopForOffline());

        _updateService = new UpdateService(new VelopackUpdater(AppVersion));
        Updates = new UpdatesViewModel(_updateService, () => Macros?.IsBusy == true, PrepareForRestartAsync);
        // Schema section 9: check again every 4 hours while open. Downloads quietly; Restart stays the user's call.
        _updateTimer = new DispatcherTimer(TimeSpan.FromHours(4), DispatcherPriority.Background,
            (_, _) => _ = Updates.CheckAsync(restartWhenReady: false), Dispatcher.CurrentDispatcher);

        _currentPage = Loading = new LoadingViewModel(StartAsync, UpdateFromLoadingAsync);
        _ = StartAsync();
    }

    public static string AppVersion { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0")
        .Split('+')[0];

    public ApiClient Api { get; }
    public AccountSession Session { get; }
    public LoadingViewModel Loading { get; }
    public UpdatesViewModel Updates { get; }
    public string VersionText => $"v{AppVersion}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSidebar), nameof(IsHomeTab), nameof(IsMacrosTab), nameof(IsSettingsTab))]
    private object _currentPage;

    /// <summary>The sidebar (tabs, username, sign out) shows only once signed in.</summary>
    public bool ShowSidebar => CurrentPage is HomeViewModel or MainViewModel or SettingsViewModel;

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private bool _isOffline;

    public MainViewModel? Macros { get; private set; }
    public HomeViewModel? Home { get; private set; }
    public SettingsViewModel? Settings { get; private set; }

    // Sidebar tabs. The setters are for the tab buttons; switching away never stops a running macro.
    public bool IsHomeTab
    {
        get => CurrentPage is HomeViewModel;
        set { if (value && Home is not null) { CurrentPage = Home; Home.OnShown(); } }
    }

    public bool IsMacrosTab
    {
        get => CurrentPage is MainViewModel;
        set { if (value && Macros is not null) CurrentPage = Macros; }
    }

    public bool IsSettingsTab
    {
        get => CurrentPage is SettingsViewModel;
        set { if (value && Settings is not null) CurrentPage = Settings; }
    }

    partial void OnUsernameChanged(string value) => Home?.OnUsernameChanged();

    /// <summary>
    /// Loading screen: install any new release first (from GitHub, so it works even if the server is down), then
    /// check the server and use the saved token or show sign-in. Retry runs this again.
    /// </summary>
    private async Task StartAsync()
    {
        CurrentPage = Loading;
        Loading.Begin("Checking for updates…");
        var minimum = Task.Delay(MinLoading); // never flicker
        await InstallUpdateOnLaunchAsync();

        Loading.Begin("Connecting to your account…");
        try
        {
            await Api.HealthAsync();
            var restored = await Session.TryRestoreAsync();
            await minimum;
            if (!restored) ShowAuth(null);
        }
        catch (ServerUnreachableException ex)
        {
            await minimum;
            Loading.Fail($"{ex.Message}\nServer: {_settings.ServerUrl}");
        }
        catch (ApiException ex) when (ex.Code == Contracts.ErrorCodes.UpdateRequired)
        {
            await minimum;
            Loading.Fail(ex.Message, offerUpdate: true);
        }
        catch (ApiException ex)
        {
            await minimum;
            Loading.Fail(ex.Message);
        }
    }

    private async Task InstallUpdateOnLaunchAsync()
    {
        if (_updateService.State.Stage == UpdateStage.NotInstalled) return;
        using var cts = new CancellationTokenSource(LaunchCheckTimeout);
        void OnChanged(UpdateState s)
        {
            if (s.Stage == UpdateStage.Downloading)
            {
                cts.CancelAfter(LaunchDownloadTimeout); // found one: give the download time to finish
                OnUi(() => Loading.Begin($"Updating to v{s.NewVersion}… {s.Progress}%"));
            }
        }
        _updateService.Changed += OnChanged;
        try
        {
            // Nothing is running yet, so a downloaded update restarts straight into the new version.
            await Updates.CheckAsync(restartWhenReady: true, cts.Token);
        }
        finally
        {
            _updateService.Changed -= OnChanged;
        }
    }

    /// <summary>The loading screen's Update now button, shown when the server says this version is too old.</summary>
    private async Task UpdateFromLoadingAsync()
    {
        if (_updateService.State.Stage == UpdateStage.NotInstalled)
        {
            Updates.OpenReleasesCommand.Execute(null);
            return;
        }
        var message = Loading.Error;
        Loading.Begin("Checking for updates…");
        await InstallUpdateOnLaunchAsync();
        // Still here: nothing newer was found or the download failed.
        Loading.Fail($"{message}\n\n{Updates.Title}. {Updates.Detail}", offerUpdate: true);
    }

    private void ShowAuth(string? notice)
    {
        CurrentPage = new AuthViewModel(Session, _settings, notice);
    }

    private void ShowSignedIn()
    {
        Username = Session.User?.Username ?? "";
        DisposePages();
        Macros = new MainViewModel(Api);
        Macros.PropertyChanged += OnMacrosChanged;
        Home = new HomeViewModel(this, new AnnouncementFeed(_github, AppPaths.Announcements));
        Settings = new SettingsViewModel(this);
        OnPropertyChanged(nameof(Macros));
        CurrentPage = Home;
        _ = Macros.RefreshAsync();
    }

    private void OnMacrosChanged(object? sender, PropertyChangedEventArgs e)
    {
        // "Update ready" wording depends on whether a macro is running.
        if (e.PropertyName == nameof(MainViewModel.IsBusy)) Updates.Refresh();
    }

    private void OnSignedOut(SessionEndedEventArgs e)
    {
        // Stop every macro and release held keys before anything else (schema: "The one-device rule").
        DisposePages();
        if (e.Reason == SessionEndReason.UpdateRequired)
        {
            CurrentPage = Loading;
            Loading.Fail(e.Message, offerUpdate: true);
            return;
        }
        ShowAuth(e.Reason == SessionEndReason.SignedOut ? null : e.Message);
    }

    private void DisposePages()
    {
        if (Macros is not null)
        {
            Macros.PropertyChanged -= OnMacrosChanged;
            Macros.Dispose();
        }
        Home?.Dispose();
        Macros = null;
        Home = null;
        Settings = null;
        OnPropertyChanged(nameof(Macros));
    }

    /// <summary>Before an update restarts the app: send the last edit, stop macros and release every key.</summary>
    private async Task PrepareForRestartAsync()
    {
        if (Macros is { } macros)
        {
            try
            {
                await macros.FlushAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) when (ex is TimeoutException or ApiException or ServerUnreachableException)
            {
                // The draft autosaves every couple of seconds anyway; don't block the update on it.
            }
        }
        Dispose();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task SignOutAsync()
    {
        if (Macros is not null) await Macros.FlushAsync();
        await Session.SignOutAsync();
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _updateTimer.Stop();
        DisposePages();
        Session.Dispose();
        _http.Dispose();
        _github.Dispose();
    }
}

public sealed partial class LoadingViewModel(Func<Task> retry, Func<Task> runUpdater) : ObservableObject
{
    [ObservableProperty] private bool _isBusy = true;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _offerUpdate;

    public void Begin(string status)
    {
        IsBusy = true;
        Status = status;
        Error = "";
        OfferUpdate = false;
    }

    /// <param name="offerUpdate">The server needs a newer app: show an Update now button.</param>
    public void Fail(string message, bool offerUpdate = false)
    {
        IsBusy = false;
        Error = message;
        OfferUpdate = offerUpdate;
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task RetryAsync() => retry();

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task UpdateNowAsync() => runUpdater();
}
