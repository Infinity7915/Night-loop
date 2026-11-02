using System.IO;
using System.Windows;
using System.Windows.Threading;
using NightLoop.LocalData;

namespace NightLoop.App;

public partial class App : Application
{
    /// <summary>Repeats of the same error within this window show no second message box.</summary>
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);
    private static string? _lastError;
    private static DateTime _lastShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    /// <summary>
    /// Writes the whole error to logs\errors.log (so a bug report has the details) and shows the short message
    /// once. The same error repeating, for example on every frame of a window resize, doesn't stack message boxes.
    /// </summary>
    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Log(e.Exception);
        var message = e.Exception.Message;
        var now = DateTime.UtcNow;
        if (message == _lastError && now - _lastShown < RepeatWindow) return;
        _lastError = message;
        _lastShown = now;
        MessageBox.Show($"{message}\n\nDetails were saved to {Path.Combine(AppPaths.Logs, "errors.log")}.", "Night-loop",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            var path = Path.Combine(AppPaths.Logs, "errors.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path); // keep it small
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{ViewModels.ShellViewModel.AppVersion}\n{ex}\n\n");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            // Logging must never cause a second error.
        }
    }
}
