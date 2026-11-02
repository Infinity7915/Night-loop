using System.Net.Http;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using NightLoop.Client;
using NightLoop.Contracts.Api;
using NightLoop.LocalData;

namespace NightLoop.App.ViewModels;

/// <summary>
/// The window's current screen: Loading → Sign in → Macros (schema section 8, "Startup and screens"). Owns the
/// API client and the account session, and sends the user back to sign-in when the server ends the session.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan MinLoading = TimeSpan.FromSeconds(1.5);

    private readonly LocalSettings _settings = new();
    private readonly HttpClient _http;

    public ShellViewModel()
    {
        var device = DeviceIdentity.ForThisPc();
        _http = new HttpClient { BaseAddress = _settings.ServerUrl, Timeout = TimeSpan.FromSeconds(15) };
        Api = new ApiClient(_http, device.InstallId, AppVersion);
        Session = new AccountSession(Api, OperatingSystem.IsWindows() ? new DpapiSessionStore() : new MemorySessionStore(), device);
        Session.SignedIn += _ => OnUi(ShowMacros);
        Session.SignedOut += e => OnUi(() => OnSignedOut(e));
        Session.UserChanged += user => OnUi(() => Username = user.Username);
        Session.OfflineChanged += offline => OnUi(() => IsOffline = offline);
        Session.OfflineGraceExpired += () => OnUi(() => Macros?.StopForOffline());

        _currentPage = Loading = new LoadingViewModel(StartAsync);
        _ = StartAsync();
    }

    public static string AppVersion { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0")
        .Split('+')[0];

    public ApiClient Api { get; }
    public AccountSession Session { get; }
    public LoadingViewModel Loading { get; }
    public string VersionText => $"v{AppVersion}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSidebar))]
    private object _currentPage;

    /// <summary>The sidebar (logo, username, sign out) shows only once signed in.</summary>
    public bool ShowSidebar => CurrentPage is MainViewModel;
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private bool _isOffline;

    public MainViewModel? Macros { get; private set; }

    /// <summary>Loading screen: check the server, then use the saved token or show sign-in.</summary>
    private async Task StartAsync()
    {
        CurrentPage = Loading;
        Loading.Begin();
        var minimum = Task.Delay(MinLoading); // never flicker
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
            Loading.Fail(ex.Message);
        }
        catch (ApiException ex)
        {
            await minimum;
            Loading.Fail(ex.Message);
        }
    }

    private void ShowAuth(string? notice)
    {
        CurrentPage = new AuthViewModel(Session, _settings, notice);
    }

    private void ShowMacros()
    {
        Username = Session.User?.Username ?? "";
        Macros?.Dispose();
        Macros = new MainViewModel(Api);
        OnPropertyChanged(nameof(Macros));
        CurrentPage = Macros;
        _ = Macros.RefreshAsync();
    }

    private void OnSignedOut(SessionEndedEventArgs e)
    {
        // Stop every macro and release held keys before anything else (schema: "The one-device rule").
        Macros?.Dispose();
        Macros = null;
        OnPropertyChanged(nameof(Macros));
        if (e.Reason == SessionEndReason.UpdateRequired)
        {
            CurrentPage = Loading;
            Loading.Fail(e.Message);
            return;
        }
        ShowAuth(e.Reason == SessionEndReason.SignedOut ? null : e.Message);
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

    public void Dispose()
    {
        Macros?.Dispose();
        Session.Dispose();
        _http.Dispose();
    }
}

public sealed partial class LoadingViewModel(Func<Task> retry) : ObservableObject
{
    [ObservableProperty] private bool _isBusy = true;
    [ObservableProperty] private string _error = "";

    public void Begin()
    {
        IsBusy = true;
        Error = "";
    }

    public void Fail(string message)
    {
        IsBusy = false;
        Error = message;
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task RetryAsync() => retry();
}
