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

        // The Macros screen gets sample blocks and saved macros so the pictures show a filled editor. Each variant
        // has the inspector on a different block: a window block, a picture with its preview, and Open window.
        var samples = new Dictionary<string, MainViewModel>();
        MainViewModel SampleMacros(string select = "position")
        {
            if (samples.TryGetValue(select, out var existing)) return existing;
            // Only one editor holds F6 and F9 at a time (the app never has two); free them for the new one.
            foreach (var old in samples.Values) old.Dispose();
            var dir = Path.Combine(Path.GetTempPath(), "nightloop-smoke");
            Directory.CreateDirectory(dir);
            var api = new Client.ApiClient(new System.Net.Http.HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/v1/") }, Guid.Empty, "smoke");
            var games = new TargetGamesViewModel(new LocalData.GamesStore(Path.Combine(dir, "games.json")),
                new LocalData.LocalSettings(Path.Combine(dir, "settings.json")));
            var macros = new MainViewModel(api, games, new LocalData.LocalSettings(Path.Combine(dir, "settings.json")));
            var parts = select.Split('#');
            macros.ShowSample(parts[0]);
            // "#..." opens one more part of the page for the picture.
            switch (parts.ElementAtOrDefault(1))
            {
                case "saved": macros.Sections.SelectMany(g => g.Rows).First().ShowOptions = true; break;
                case "group": macros.NewGroupCommand.Execute(null); break;
                case "options": macros.ShowEditorOptions = true; break;
            }
            return samples[select] = macros;
        }

        // Settings needs the app shell behind it (account, updates, games). It stays offline here.
        ShellViewModel? shell = null;
        ShellViewModel Shell() => shell ??= new ShellViewModel();
        SettingsViewModel? settings = null;
        SettingsViewModel SampleSettings() => settings ??= new SettingsViewModel(Shell());
        // The create-account screen, with its terms line.
        AuthViewModel SignUp() => new(Shell().Session, Shell().Local, null) { Mode = AuthMode.SignUp };
        AuthViewModel AuthIn(AuthMode mode) => new(Shell().Session, Shell().Local, null) { Mode = mode };
        // Settings with one of the account boxes open.
        SettingsViewModel SettingsWith(Action<AccountViewModel> open)
        {
            var vm = new SettingsViewModel(Shell());
            open(vm.Account);
            return vm;
        }

        // Pages render at the width they get in the window: 1060 at the default 1280 window (minus the 220 sidebar),
        // 740 at the smallest window. The narrow ones catch rows that overlap or get cut off when the window shrinks.
        var screens = new (string Name, Func<FrameworkElement> Make, int Width, int Height)[]
        {
            ("loading", () => new LoadingView { DataContext = loading }, 1280, 800),
            ("auth", () => new AuthView(), 1280, 800),
            ("auth-signup", () => new AuthView { DataContext = SignUp() }, 1280, 800),
            ("home", () => new HomeView(), 1060, 800),
            ("macros", () => new MacrosView { DataContext = SampleMacros() }, 1060, 800),
            // The whole Macros page, down to the saved macros list.
            ("macros-full", () => new MacrosView { DataContext = SampleMacros() }, 1060, 2300),
            ("macros-narrow", () => new MacrosView { DataContext = SampleMacros() }, 740, 3000),
            ("macros-wide", () => new MacrosView { DataContext = SampleMacros() }, 1700, 2000),
            // The loaded macro's options (record key) and a picture block with its preview.
            ("macros-picture", () => new MacrosView { DataContext = SampleMacros("image") }, 1060, 2100),
            ("macros-open-window", () => new MacrosView { DataContext = SampleMacros("open") }, 1060, 1300),
            ("macros-close-window", () => new MacrosView { DataContext = SampleMacros("close") }, 1060, 2100),
            ("macros-scan-turn", () => new MacrosView { DataContext = SampleMacros("scan") }, 1060, 2100),
            // Scan & turn with a picture: the click spot dot on the preview and its two % fields.
            ("macros-click-spot", () => new MacrosView { DataContext = SampleMacros("scanpic") }, 1060, 3300),
            // The window "Set click spot in picture" opens.
            ("click-spot-picker", () => Controls.ClickSpotPicker.ContentForSmoke(
                (System.Windows.Media.Imaging.BitmapSource)SampleMacros("scanpic").InspectorImage!, 0.3, 0.25), 600, 480),
            // A Click block with its Wait after click slider.
            ("macros-click", () => new MacrosView { DataContext = SampleMacros("click") }, 1060, 2100),
            ("auth-code", () => new AuthView { DataContext = AuthIn(AuthMode.Code) }, 1280, 800),
            ("auth-forgot", () => new AuthView { DataContext = AuthIn(AuthMode.Forgot) }, 1280, 800),
            ("auth-reset", () => new AuthView { DataContext = AuthIn(AuthMode.Reset) }, 1280, 800),
            // Each part of the Macros page opened: the loaded macro's options, a saved macro's options, a new group.
            ("macros-options", () => new MacrosView { DataContext = SampleMacros("position#options") }, 1060, 1500),
            ("macros-saved-options", () => new MacrosView { DataContext = SampleMacros("position#saved") }, 1060, 2700),
            ("macros-new-group", () => new MacrosView { DataContext = SampleMacros("position#group") }, 1060, 2300),
            ("settings", () => new SettingsView { DataContext = SampleSettings() }, 1060, 1700),
            ("settings-full", () => new SettingsView { DataContext = SampleSettings() }, 1060, 2600),
            ("settings-username", () => new SettingsView { DataContext = SettingsWith(a => a.ChangeUsernameCommand.Execute(null)) }, 1060, 2600),
            ("settings-email", () => new SettingsView { DataContext = SettingsWith(a => a.ChangeEmailCommand.Execute(null)) }, 1060, 2600),
            ("settings-password", () => new SettingsView { DataContext = SettingsWith(a => a.ChangePasswordCommand.Execute(null)) }, 1060, 2600),
            ("settings-delete", () => new SettingsView { DataContext = SettingsWith(a => a.DeleteAccountCommand.Execute(null)) }, 1060, 2600),
            ("settings-narrow", () => new SettingsView { DataContext = SampleSettings() }, 740, 1900),
            ("window", MainWindowContent, 1280, 800),
        };

        // Every screen in both palettes, with the accent each one is most often seen with.
        foreach (var (mode, accent) in new[] { (Themes.ThemeManager.Dark, Themes.ThemeManager.DefaultAccent), (Themes.ThemeManager.Light, Themes.ThemeManager.DefaultAccent) })
        {
            Themes.ThemeManager.Preview(mode, accent);
            failures += Draw(app, folder, mode, screens);
        }
        // Each extra color theme on the screens it changes most.
        var themed = screens.Where(s => s.Name is "macros" or "settings" or "window").ToArray();
        foreach (var palette in Themes.ThemePalettes.All.Skip(1))
        {
            Themes.ThemeManager.Preview(Themes.ThemeManager.Dark, palette.Accent, palette.Id);
            failures += Draw(app, folder, palette.Id, themed);
        }
        CheckMinimizeFocus(folder);
        failures += CheckWindowsParts(folder);
        return failures == 0 ? 0 : 1;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// Report only (never fails the run): after minimizing for Record, is Night-loop still the foreground window?
    /// It must not be, or the keys typed next go to Night-loop. Compares WPF's WindowState = Minimized with the
    /// SW_MINIMIZE that Record uses, with another window behind to take focus. Written to minimize-check.txt.
    /// </summary>
    private static void CheckMinimizeFocus(string folder)
    {
        var lines = new List<string>();
        try
        {
            void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            void Wait() { for (var i = 0; i < 10; i++) { Pump(); Thread.Sleep(30); } }
            var behind = new Window { Title = "Behind", Width = 300, Height = 200, Left = 50, Top = 50, ShowActivated = true };
            behind.Show();
            var main = new Window { Title = "Night-loop check", Width = 400, Height = 300, Left = 100, Top = 100 };
            main.Show();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(main).Handle;
            foreach (var (name, minimize) in new (string, Action)[]
                     {
                         ("WindowState = Minimized", () => main.WindowState = WindowState.Minimized),
                         ("ShowWindow(SW_MINIMIZE)", () => ShowWindow(hwnd, 6)),
                     })
            {
                main.WindowState = WindowState.Normal;
                main.Activate();
                SetForegroundWindow(hwnd);
                Wait();
                var before = GetForegroundWindow() == hwnd;
                minimize();
                Wait();
                lines.Add($"{name}: in front before={before}, still in front after minimize={GetForegroundWindow() == hwnd}");
            }
            main.Close();
            behind.Close();
        }
        catch (Exception ex)
        {
            lines.Add("check failed: " + ex.Message);
        }
        File.WriteAllLines(Path.Combine(folder, "minimize-check.txt"), lines);
    }

    /// <summary>
    /// The parts that talk to Windows directly: the tray icon (Shell_NotifyIcon, no WinForms) and text recognition,
    /// whose Windows SDK part is trimmed in published builds. Missing code there shows up as a load error here, so the
    /// release (which runs this on the published build) fails instead of shipping it. A PC without a text recognition
    /// language only gets a note. Results go to windows-parts.txt.
    /// </summary>
    private static int CheckWindowsParts(string folder)
    {
        var lines = new List<string>();
        var failures = 0;
        try
        {
            using var tray = Services.TrayIcon.Create(() => { }, () => { });
            lines.Add($"tray icon: ok, notification sent={Services.TrayIcon.Notify("Night-loop smoke test", "Checking the tray icon.")}");
        }
        catch (Exception ex)
        {
            failures++;
            lines.Add("tray icon: FAILED " + ex);
        }
        try
        {
            var reader = Services.WindowsTextReader.Create();
            if (!reader.IsAvailable) lines.Add("text reading: no text recognition language on this PC (not a failure)");
            else
            {
                var text = new System.Windows.Controls.TextBlock
                {
                    Text = "Collect 25 coins", FontSize = 36, Foreground = Brushes.Black, Background = Brushes.White, Padding = new Thickness(20),
                };
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                text.Arrange(new Rect(text.DesiredSize));
                var (w, h) = ((int)Math.Ceiling(text.DesiredSize.Width), (int)Math.Ceiling(text.DesiredSize.Height));
                var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(text);
                var pixels = new byte[w * h * 4];
                bitmap.CopyPixels(pixels, w * 4, 0);
                var image = new Engine.Screen.ScreenImage(0, 0, w, h, pixels);
                var found = Task.Run(() => reader.ReadAsync(image, CancellationToken.None)).GetAwaiter().GetResult();
                var read = string.Join(" | ", found.Select(l => l.Text));
                lines.Add($"text reading: ok, read \"{read}\"");
            }
        }
        catch (Exception ex)
        {
            failures++;
            lines.Add("text reading: FAILED " + ex);
        }
        File.WriteAllLines(Path.Combine(folder, "windows-parts.txt"), lines);
        foreach (var line in lines) Console.WriteLine(line);
        if (failures > 0) File.WriteAllText(Path.Combine(folder, "windows-parts.error.txt"), string.Join(Environment.NewLine, lines));
        return failures;
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
