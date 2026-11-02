using System.ComponentModel;
using System.Windows;
using NightLoop.App.Themes;
using NightLoop.App.ViewModels;

namespace NightLoop.App.Views;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _shell;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyTitleBar(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Send the last edit before closing. Off the UI thread so the awaits inside can't deadlock on it.
        if (_shell.Macros is { } macros)
            Task.Run(macros.FlushAsync).Wait(TimeSpan.FromSeconds(3));
        _shell.Dispose();
        base.OnClosing(e);
    }
}
