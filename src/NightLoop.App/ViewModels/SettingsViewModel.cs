using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NightLoop.App.Themes;

namespace NightLoop.App.ViewModels;

/// <summary>Settings (schema section 8): appearance, target game, account and updates.</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _gameTimer;

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Account = new AccountViewModel(shell.Session, shell.SignOutAsync);
        Accents = ThemeManager.AllAccents.Select(hex => new AccentOption(hex)).ToList();
        Themes = ThemePalettes.All.Select(t => new ThemeOption(t)).ToList();
        ThemeManager.Changed += OnThemeChanged;
        // Detected / Not running labels stay current while Settings is open.
        _gameTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background,
            (_, _) => Games.RefreshStatus(), Dispatcher.CurrentDispatcher);
        _gameTimer.Stop();
    }

    public ShellViewModel Shell => _shell;
    public UpdatesViewModel Updates => _shell.Updates;
    public TargetGamesViewModel Games => _shell.Games;
    public AccountViewModel Account { get; }
    public IReadOnlyList<AccentOption> Accents { get; }
    public IReadOnlyList<ThemeOption> Themes { get; }

    /// <summary>Dark / Light / System only changes the Night theme; the others bring their own.</summary>
    public bool ModeApplies => ThemeManager.Palette.Id == ThemePalettes.NightId;

    /// <summary>New macros start with the ready-made blocks (focus, resize, a loop, a notification). Kept on this PC.</summary>
    public bool StarterSequence
    {
        get => _shell.Local.StarterSequence;
        set
        {
            if (_shell.Local.StarterSequence == value) return;
            _shell.Local.StarterSequence = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The block editor slides along beside a long timeline as the page scrolls. Kept on this PC.</summary>
    public bool EditorFollowsScroll
    {
        get => _shell.Local.EditorFollowsScroll;
        set
        {
            if (_shell.Local.EditorFollowsScroll == value) return;
            _shell.Local.EditorFollowsScroll = value;
            if (_shell.Macros is { } macros) macros.EditorFollowsScroll = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Closing the window hides Night-loop to the tray instead of quitting. Kept on this PC.</summary>
    public bool CloseToTray
    {
        get => _shell.Local.CloseToTray;
        set
        {
            if (_shell.Local.CloseToTray == value) return;
            _shell.Local.CloseToTray = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Keeps the window above all others. Kept on this PC.</summary>
    public bool AlwaysOnTop
    {
        get => _shell.AlwaysOnTop;
        set
        {
            if (_shell.AlwaysOnTop == value) return;
            _shell.AlwaysOnTop = value;
            OnPropertyChanged();
        }
    }

    public string ThemeMode
    {
        get => ThemeManager.Mode;
        set => ThemeManager.SetMode(value);
    }

    public string Accent
    {
        get => ThemeManager.Accent;
        set => ThemeManager.SetAccent(value);
    }

    public void OnShown()
    {
        Games.RefreshStatus();
        _gameTimer.Start();
    }

    public void OnHidden() => _gameTimer.Stop();

    private void OnThemeChanged()
    {
        OnPropertyChanged(nameof(ThemeMode));
        OnPropertyChanged(nameof(Accent));
        OnPropertyChanged(nameof(ModeApplies));
        foreach (var a in Accents) a.Refresh();
        foreach (var t in Themes) t.Refresh();
    }

    public void Dispose()
    {
        _gameTimer.Stop();
        ThemeManager.Changed -= OnThemeChanged;
    }
}

/// <summary>One accent swatch.</summary>
public sealed class AccentOption(string hex) : ObservableObject
{
    public string Hex { get; } = hex;

    public bool IsSelected
    {
        get => string.Equals(ThemeManager.Accent, Hex, StringComparison.OrdinalIgnoreCase);
        set { if (value) ThemeManager.SetAccent(Hex); }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
}

/// <summary>One color theme button: a little preview of its page and accent, and its name.</summary>
public sealed class ThemeOption(ThemePalette palette) : ObservableObject
{
    public string Name => palette.Name;
    public string Preview => palette.Preview;
    public string AccentHex => palette.Accent;

    public bool IsSelected
    {
        get => ThemeManager.Palette.Id == palette.Id;
        set { if (value) ThemeManager.SetPalette(palette.Id); }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
}
