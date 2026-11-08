using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace NightLoop.App.Services;

/// <summary>
/// Settings › Appearance › Seasonal themes (off until turned on): around holidays the Night-loop icon in the window,
/// the taskbar and the tray dresses up for the season. The .exe's own icon (desktop shortcut) can't change.
/// </summary>
public static class Seasons
{
    /// <summary>The season being shown, or null for the normal icon.</summary>
    public static string? Current { get; private set; }

    /// <summary>The season for this date, or null on ordinary days.</summary>
    public static string? For(DateTime day)
    {
        var d = day.Date;
        var y = d.Year;
        bool In(DateTime from, DateTime to) => d >= from && d <= to;
        if (d == new DateTime(y, 12, 31) || In(new(y, 1, 1), new(y, 1, 2))) return "newyear";
        if (In(new(y, 12, 18), new(y, 12, 30))) return "christmas";
        if (In(new(y, 2, 8), new(y, 2, 14))) return "valentine";
        if (In(new(y, 3, 11), new(y, 3, 17))) return "stpatrick";
        var easter = EasterSunday(y);
        if (In(easter.AddDays(-7), easter.AddDays(1))) return "easter";
        if (In(new(y, 6, 28), new(y, 7, 5))) return "july4";
        if (In(new(y, 10, 24), new(y, 10, 31))) return "halloween";
        var thanksgiving = new DateTime(y, 11, 1);
        while (thanksgiving.DayOfWeek != DayOfWeek.Thursday) thanksgiving = thanksgiving.AddDays(1);
        thanksgiving = thanksgiving.AddDays(21);
        if (In(thanksgiving.AddDays(-3), thanksgiving.AddDays(3))) return "thanksgiving"; // Monday to Sunday
        return null;
    }

    /// <summary>Shows the season's icon (or the normal one) in the window, the taskbar and the tray.</summary>
    public static void Apply(bool enabled)
    {
        Current = enabled ? For(DateTime.Now) : null;
        if (Application.Current is { } app)
        {
            var path = Current is null ? "Assets/icon.png" : $"Assets/Seasons/{Current}.png";
            try
            {
                var image = new BitmapImage(new Uri($"pack://application:,,,/{path}"));
                image.Freeze();
                app.Resources["AppIcon"] = image;
            }
            catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException)
            {
            }
        }
        TrayIcon.Current?.RefreshIcon();
    }

    /// <summary>The embedded .ico for the tray.</summary>
    public static string IcoResource => Current is null ? "NightLoop.icon.ico" : $"NightLoop.season.{Current}.ico";

    // Anonymous Gregorian algorithm.
    private static DateTime EasterSunday(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451, month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(y, month, day);
    }
}
