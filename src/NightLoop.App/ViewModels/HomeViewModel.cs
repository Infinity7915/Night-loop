using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightLoop.Updates;

namespace NightLoop.App.ViewModels;

/// <summary>
/// Home (schema section 8): greeting, stopwatch, active macros, updates, and the announcements text that lives
/// in <c>announcements.md</c> in the GitHub repository.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefetchAfter = TimeSpan.FromMinutes(1);

    private readonly ShellViewModel _shell;
    private readonly AnnouncementFeed _feed;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _announcementTimer;
    private DateTime _lastFetch = DateTime.MinValue;

    public HomeViewModel(ShellViewModel shell, AnnouncementFeed feed)
    {
        _shell = shell;
        _feed = feed;
        // Both timers start right away. The clock rolls the greeting over; the other picks up announcement edits.
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => UpdateClock(), Dispatcher.CurrentDispatcher);
        _announcementTimer = new DispatcherTimer(TimeSpan.FromMinutes(10), DispatcherPriority.Background, (_, _) => _ = LoadAnnouncementsAsync(), Dispatcher.CurrentDispatcher);
        UpdateClock();
        Show(_feed.LoadCached());
        _ = LoadAnnouncementsAsync();
    }

    public ShellViewModel Shell => _shell;
    public UpdatesViewModel Updates => _shell.Updates;
    public StopwatchViewModel Stopwatch { get; } = new();
    public ObservableCollection<AnnouncementBlock> Announcements { get; } = [];

    [ObservableProperty] private string _greeting = "";
    [ObservableProperty] private string _dateText = "";
    [ObservableProperty] private string _announcementMeta = "";
    [ObservableProperty] private bool _hasAnnouncements;
    [ObservableProperty] private bool _isRefreshing;

    /// <summary>Called when the Home tab is opened: fetch again if the text is over a minute old.</summary>
    public void OnShown()
    {
        if (DateTime.UtcNow - _lastFetch > RefetchAfter) _ = LoadAnnouncementsAsync();
    }

    /// <summary>The Refresh button: new announcements, and install any update (unless a macro is running).</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            await Task.WhenAll(LoadAnnouncementsAsync(), Updates.CheckAsync(restartWhenReady: true));
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private void OpenMacros() => _shell.IsMacrosTab = true;

    private async Task LoadAnnouncementsAsync()
    {
        _lastFetch = DateTime.UtcNow;
        Show(await _feed.LoadAsync());
    }

    private void Show(Announcement? a)
    {
        Announcements.Clear();
        if (a is null)
        {
            HasAnnouncements = false;
            AnnouncementMeta = "couldn't reach GitHub";
            return;
        }
        foreach (var block in a.Blocks) Announcements.Add(block);
        HasAnnouncements = !a.IsEmpty;
        AnnouncementMeta = a.FromCache ? $"offline · from {a.FetchedAt.ToLocalTime():g}" : $"updated {a.FetchedAt.ToLocalTime():t}";
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var part = now.Hour switch
        {
            >= 5 and < 12 => "morning",
            >= 12 and < 18 => "afternoon",
            _ => "evening",
        };
        Greeting = $"Good {part}, {_shell.Username}";
        DateText = $"{now:dddd, MMMM d} · {now:t}";
    }

    /// <summary>The username can change after Home is built.</summary>
    public void OnUsernameChanged() => UpdateClock();

    public void Dispose()
    {
        _clock.Stop();
        _announcementTimer.Stop();
        Stopwatch.Dispose();
    }
}

/// <summary>Home's stopwatch: Start / Stop / Clear, shown as hh:mm:ss.cc.</summary>
public sealed partial class StopwatchViewModel : ObservableObject, IDisposable
{
    private readonly Stopwatch _watch = new();
    private readonly DispatcherTimer _tick;

    public StopwatchViewModel()
    {
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) => Show(), Dispatcher.CurrentDispatcher);
        _tick.Stop();
        Show();
    }

    [ObservableProperty] private string _main = "";
    [ObservableProperty] private string _fraction = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        _watch.Start();
        _tick.Start();
        IsRunning = true;
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        _watch.Stop();
        _tick.Stop();
        IsRunning = false;
        Show();
    }

    [RelayCommand]
    private void Clear()
    {
        _watch.Reset();
        if (IsRunning) _watch.Start();
        Show();
    }

    private void Show()
    {
        var t = _watch.Elapsed;
        Main = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        Fraction = $".{t.Milliseconds / 10:00}";
    }

    public void Dispose() => _tick.Stop();
}
