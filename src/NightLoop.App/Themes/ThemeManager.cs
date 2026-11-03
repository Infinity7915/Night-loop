using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using NightLoop.App.Controls;
using NightLoop.LocalData;

namespace NightLoop.App.Themes;

/// <summary>
/// Theme mode (dark, light, system) and accent color (schema section 8, Settings → Appearance). Swaps the palette
/// dictionary in App.xaml and rebuilds the accent brushes; every view uses DynamicResource, so it all repaints
/// at once. Saved in settings.json, so it only applies to this PC.
/// </summary>
public static class ThemeManager
{
    public const string Dark = "dark";
    public const string Light = "light";
    public const string System = "system";

    /// <summary>The four accents from the mockups. Purple is the default.</summary>
    public static readonly IReadOnlyList<string> Accents = ["#A78BFA", "#4CC9F0", "#F59E0B", "#34D399"];
    public static string DefaultAccent => Accents[0];

    private static readonly Uri DarkUri = new("pack://application:,,,/NightLoop;component/Themes/Dark.xaml");
    private static readonly Uri LightUri = new("pack://application:,,,/NightLoop;component/Themes/Light.xaml");

    private static LocalSettings? _settings;
    private static bool _listening;

    public static string Mode { get; private set; } = Dark;
    public static string Accent { get; private set; } = DefaultAccent;

    /// <summary>The color theme (Night, Rose, Ocean...). Only Night follows the mode; the others set their own.</summary>
    public static ThemePalette Palette { get; private set; } = ThemePalettes.Default;

    /// <summary>The theme accents first after the mockup four, so every theme's own color can be picked again.</summary>
    public static IReadOnlyList<string> AllAccents { get; } =
        Accents.Concat(ThemePalettes.All.Select(t => t.Accent)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True when the light palette is showing (Light, or System while Windows uses light apps).</summary>
    public static bool IsLight { get; private set; }

    public static event Action? Changed;

    /// <summary>Applies the saved theme. Call once at startup, before the first window shows.</summary>
    public static void Load(LocalSettings settings)
    {
        _settings = settings;
        Mode = settings.Theme;
        Accent = settings.Accent ?? DefaultAccent;
        Palette = ThemePalettes.For(settings.Palette);
        Apply();
    }

    /// <summary>Shows a theme without saving it (the CI smoke test draws every screen in both modes).</summary>
    public static void Preview(string mode, string accent, string palette = ThemePalettes.NightId)
    {
        _settings = null;
        Mode = mode;
        Accent = accent;
        Palette = ThemePalettes.For(palette);
        Apply();
    }

    public static void SetMode(string mode)
    {
        if (mode is not (Dark or Light or System) || mode == Mode) return;
        Mode = mode;
        if (_settings is not null) _settings.Theme = mode;
        Apply();
    }

    public static void SetAccent(string hex)
    {
        if (!LocalSettings.IsHexColor(hex) || string.Equals(hex, Accent, StringComparison.OrdinalIgnoreCase)) return;
        Accent = hex.ToUpperInvariant();
        if (_settings is not null) _settings.Accent = Accent;
        Apply();
    }

    /// <summary>Picks a color theme and its accent (the accent can still be changed after).</summary>
    public static void SetPalette(string id)
    {
        var palette = ThemePalettes.For(id);
        if (palette == Palette) return;
        Palette = palette;
        Accent = palette.Accent.ToUpperInvariant();
        if (_settings is not null)
        {
            _settings.Palette = palette.Id;
            _settings.Accent = Accent;
        }
        Apply();
    }

    private static void Apply()
    {
        if (Application.Current is not { } app) return;
        IsLight = Palette.Id == ThemePalettes.NightId
            ? Mode == Light || (Mode == System && WindowsUsesLightApps())
            : Palette.Light;
        ListenForWindowsChanges();

        // The palette is always the first merged dictionary (see App.xaml).
        var dictionaries = app.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = IsLight ? LightUri : DarkUri };
        foreach (var (key, hex) in Palette.Colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            palette[key] = brush;
        }
        if (dictionaries.Count > 0) dictionaries[0] = palette;
        else dictionaries.Add(palette);

        var accent = (Color)ColorConverter.ConvertFromString(Accent);
        var bg = ((SolidColorBrush)palette["Bg"]).Color;
        var panel = ((SolidColorBrush)palette["Panel"]).Color;
        SetBrush(app, "Accent", accent);
        SetBrush(app, "AccentText", Luminance(accent) > 0.35 ? Color.FromRgb(0x14, 0x0B, 0x2E) : Colors.White);
        // Links and accent text on the page background: darker in light mode so they stay readable.
        SetBrush(app, "AccentLink", IsLight ? Mix(accent, Colors.Black, 0.38) : accent);
        SetBrush(app, "AccentSoft", Mix(bg, accent, IsLight ? 0.10 : 0.10));
        SetBrush(app, "AccentSoftBorder", Mix(panel, accent, IsLight ? 0.35 : 0.30));

        foreach (Window w in app.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    /// <summary>Colors the Windows title bar to match the sidebar.</summary>
    public static void ApplyTitleBar(Window window)
    {
        if (Application.Current is not { } app) return;
        var sidebar = ((SolidColorBrush)app.FindResource("Sidebar")).Color;
        var text = ((SolidColorBrush)app.FindResource("Text")).Color;
        DarkTitleBar.Apply(window, sidebar, text, dark: !IsLight);
    }

    private static void SetBrush(Application app, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        app.Resources[key] = brush;
    }

    private static bool WindowsUsesLightApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch (Exception ex) when (ex is global::System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static void ListenForWindowsChanges()
    {
        if (_listening || Mode != System) return;
        _listening = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General || Mode != System) return;
            Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}
