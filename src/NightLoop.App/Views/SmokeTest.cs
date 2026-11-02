using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NightLoop.App.ViewModels;

namespace NightLoop.App.Views;

/// <summary>
/// <c>NightLoop.exe --smoke &lt;folder&gt;</c>: loads the theme and every screen's XAML, lays each out at window size
/// and saves it as a PNG. Catches broken XAML or missing resources on a CI machine without a server or a person.
/// Exit code 0 when every screen loaded.
/// </summary>
public static class SmokeTest
{
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
        var failures = 0;
        var loading = new LoadingViewModel(() => Task.CompletedTask, () => Task.CompletedTask);
        loading.Fail("Smoke test: the server isn't running.", offerUpdate: true);

        var screens = new (string Name, Func<FrameworkElement> Make)[]
        {
            ("loading", () => new LoadingView { DataContext = loading }),
            ("auth", () => new AuthView()),
            ("home", () => new HomeView()),
            ("macros", () => new MacrosView()),
            ("settings", () => new SettingsView()),
            ("window", MainWindowContent),
        };

        // Every screen in both palettes, with the accent each one is most often seen with.
        foreach (var (mode, accent) in new[] { (Themes.ThemeManager.Dark, Themes.ThemeManager.DefaultAccent), (Themes.ThemeManager.Light, Themes.ThemeManager.DefaultAccent) })
        {
            Themes.ThemeManager.Preview(mode, accent);
            failures += Draw(app, folder, mode, screens);
        }
        return failures == 0 ? 0 : 1;
    }

    private static int Draw(Application app, string folder, string mode, (string Name, Func<FrameworkElement> Make)[] screens)
    {
        var failures = 0;
        foreach (var (screen, make) in screens)
        {
            var name = $"{screen}-{mode}";
            try
            {
                var view = make();
                var host = new System.Windows.Controls.Border { Background = (Brush)app.FindResource("Bg"), Child = view };
                System.Windows.Documents.TextElement.SetForeground(host, (Brush)app.FindResource("Text"));
                host.Measure(new Size(1280, 800));
                host.Arrange(new Rect(0, 0, 1280, 800));
                host.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(folder, name + ".png"));
                png.Save(file);
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
