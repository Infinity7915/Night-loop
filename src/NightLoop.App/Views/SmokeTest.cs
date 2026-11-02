using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NightLoop.App.ViewModels;

namespace NightLoop.App.Views;

/// <summary>
/// <c>NightLoop.exe --smoke &lt;folder&gt;</c>: loads the theme and every screen's XAML, lays each out at window size
/// and saves it as a PNG. Catches broken XAML or missing resources on a CI machine without a server or a person.
/// Exit code 0 when every screen loaded without errors, including broken bindings (a misspelt property shows
/// nothing on screen and would otherwise go unnoticed).
/// </summary>
public static class SmokeTest
{
    /// <summary>Collects WPF's binding error messages ("BindingExpression path error" and the like).</summary>
    private sealed class BindingErrors : TraceListener
    {
        private readonly StringBuilder _line = new();
        public List<string> Lines { get; } = [];
        public override void Write(string? message) => _line.Append(message);
        public override void WriteLine(string? message)
        {
            _line.Append(message);
            Lines.Add(_line.ToString());
            _line.Clear();
        }
    }

    private static readonly BindingErrors Errors = new();

    /// <summary>The real window's content (sidebar + loading page), moved out so it can be drawn without showing a window.</summary>
    private static FrameworkElement MainWindowContent()
    {
        var window = new MainWindow();
        // The window itself must carry the dark theme; a white window slipped through once.
        if (window.Background != Application.Current.FindResource("Bg"))
            throw new InvalidOperationException($"MainWindow background is {window.Background}, not the theme's Bg.");
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        content.DataContext = window.DataContext;
        if (content is System.Windows.Controls.Panel panel) panel.Background = window.Background;
        foreach (var key in window.Resources.Keys) content.Resources[key] = window.Resources[key];
        return content;
    }

    public static int Run(Application app, string folder)
    {
        Directory.CreateDirectory(folder);
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(Errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var failures = 0;
        var loading = new LoadingViewModel(() => Task.CompletedTask, () => Task.CompletedTask);
        loading.Fail("Smoke test: the server isn't running.", offerUpdate: true);

        // The Macros screen gets sample blocks and saved macros so the pictures show a filled editor.
        MainViewModel? macros = null;
        MainViewModel SampleMacros()
        {
            if (macros is not null) return macros;
            var dir = Path.Combine(Path.GetTempPath(), "nightloop-smoke");
            Directory.CreateDirectory(dir);
            var api = new Client.ApiClient(new System.Net.Http.HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/v1/") }, Guid.Empty, "smoke");
            var games = new TargetGamesViewModel(new LocalData.GamesStore(Path.Combine(dir, "games.json")),
                new LocalData.LocalSettings(Path.Combine(dir, "settings.json")));
            macros = new MainViewModel(api, games);
            macros.ShowSample();
            return macros;
        }

        // Settings needs the app shell behind it (account, updates, games). It stays offline here.
        SettingsViewModel? settings = null;
        SettingsViewModel SampleSettings() => settings ??= new SettingsViewModel(new ShellViewModel());

        // Pages render at the width they get in the window: 1060 at the default 1280 window (minus the 220 sidebar),
        // 740 at the smallest window. The narrow ones catch rows that overlap or get cut off when the window shrinks.
        var screens = new (string Name, Func<FrameworkElement> Make, int Width, int Height)[]
        {
            ("loading", () => new LoadingView { DataContext = loading }, 1280, 800),
            ("auth", () => new AuthView(), 1280, 800),
            ("home", () => new HomeView(), 1060, 800),
            ("macros", () => new MacrosView { DataContext = SampleMacros() }, 1060, 800),
            // The whole Macros page, down to the saved macros list.
            ("macros-full", () => new MacrosView { DataContext = SampleMacros() }, 1060, 2300),
            ("macros-narrow", () => new MacrosView { DataContext = SampleMacros() }, 740, 3000),
            ("macros-wide", () => new MacrosView { DataContext = SampleMacros() }, 1700, 2000),
            ("settings", () => new SettingsView { DataContext = SampleSettings() }, 1060, 1700),
            ("settings-narrow", () => new SettingsView { DataContext = SampleSettings() }, 740, 1900),
            ("window", MainWindowContent, 1280, 800),
        };

        // Every screen in both palettes, with the accent each one is most often seen with.
        foreach (var (mode, accent) in new[] { (Themes.ThemeManager.Dark, Themes.ThemeManager.DefaultAccent), (Themes.ThemeManager.Light, Themes.ThemeManager.DefaultAccent) })
        {
            Themes.ThemeManager.Preview(mode, accent);
            failures += Draw(app, folder, mode, screens);
        }
        return failures == 0 ? 0 : 1;
    }

    private static int Draw(Application app, string folder, string mode, (string Name, Func<FrameworkElement> Make, int Width, int Height)[] screens)
    {
        var failures = 0;
        foreach (var (screen, make, width, height) in screens)
        {
            var name = $"{screen}-{mode}";
            try
            {
                var errorsBefore = Errors.Lines.Count;
                var view = make();
                var host = new System.Windows.Controls.Border { Background = (Brush)app.FindResource("Bg"), Child = view };
                System.Windows.Documents.TextElement.SetForeground(host, (Brush)app.FindResource("Text"));
                // Drag the width through every size first, like resizing the window, so layout errors show up here.
                foreach (var w in new[] { 740, 960, 1300, 1900, 1100, 800, width })
                {
                    host.Measure(new Size(w, height));
                    host.Arrange(new Rect(0, 0, w, height));
                    host.UpdateLayout();
                }
                // Let queued work run (bindings that resolve late, such as ElementName ones in templates), then lay out again.
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                host.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(folder, name + ".png"));
                png.Save(file);
                var bindingErrors = Errors.Lines.Skip(errorsBefore).Distinct().ToList();
                if (bindingErrors.Count > 0)
                    throw new InvalidOperationException($"{bindingErrors.Count} binding error(s):\n" + string.Join("\n", bindingErrors));
                Console.WriteLine($"ok   {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {ex}");
                File.WriteAllText(Path.Combine(folder, name + ".error.txt"), ex.ToString());
            }
        }
        return failures;
    }
}
