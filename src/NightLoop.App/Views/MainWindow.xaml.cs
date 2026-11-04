using System.ComponentModel;
using System.Windows;
using NightLoop.App.Services;
using NightLoop.App.Themes;
using NightLoop.App.ViewModels;

namespace NightLoop.App.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// The window size the layout was designed for (the sidebar plus a full Macros page with the block editor beside
    /// the timeline). A smaller window shrinks everything in it by the same amount instead of cutting things off or
    /// moving the block editor away.
    /// </summary>
    internal const double DesignWidth = 1240;
    internal const double DesignHeight = 720;
    /// <summary>Never shrink below this; text would get too small to read.</summary>
    internal const double MinScale = 0.5;

    private readonly ShellViewModel _shell = new();
    private bool _exiting;
    private bool _toldAboutTray;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _shell;
        FitToScreen();
        SizeChanged += (_, _) => UpdateScale();
        // Signing out of Windows or shutting down must never be held up by the hide-to-tray close.
        if (Application.Current is { } app) app.SessionEnding += (_, _) => _exiting = true;
    }

    /// <summary>How much the UI is shrunk for a window of this size: 1 at the design size and up.</summary>
    internal static double ScaleFor(double width, double height) =>
        Math.Clamp(Math.Min(width / DesignWidth, height / DesignHeight), MinScale, 1.0);

    /// <summary>
    /// The first window size comes from the screen it opens on, so a small laptop screen gets a window that fits
    /// (and a UI shrunk to match) while a big monitor gets the full-size layout. Sizes are in WPF's device-independent
    /// pixels, which already include Windows' display scaling.
    /// </summary>
    private void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        if (area.Width <= 0 || area.Height <= 0) return;
        Width = Math.Max(MinWidth, Math.Min(1280, area.Width * 0.9));
        Height = Math.Max(MinHeight, Math.Min(800, area.Height * 0.9));
        UpdateScale();
    }

    private void UpdateScale()
    {
        // ActualWidth is 0 before the window is shown; the requested size stands in until then.
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        var scale = ScaleFor(w, h);
        if (Math.Abs(UiScale.ScaleX - scale) < 0.001) return;
        UiScale.ScaleX = UiScale.ScaleY = scale;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyTitleBar(this);
        TrayIcon.Create(ShowFromTray, ExitFromTray);
    }

    /// <summary>Back from the tray: show, un-minimise and bring to the front.</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Settings › Appearance › "Keep running in the tray": the X hides the window and Night-loop keeps going.
        if (!_exiting && TrayIcon.Current is not null && _shell.Local.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                TrayIcon.Notify("Night-loop is still running",
                    "Your hotkeys keep working. Right-click the Night-loop icon in the taskbar corner and pick Exit to quit.");
            }
            return;
        }

        // Send the last edit before closing. Off the UI thread so the awaits inside can't deadlock on it.
        if (_shell.Macros is { } macros)
            Task.Run(macros.FlushAsync).Wait(TimeSpan.FromSeconds(3));
        _shell.Dispose();
        TrayIcon.Current?.Dispose();
        base.OnClosing(e);
    }

    /// <summary>Quitting the main window quits Night-loop, even if a picker or other small window is still open.</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current?.Shutdown();
    }
}
