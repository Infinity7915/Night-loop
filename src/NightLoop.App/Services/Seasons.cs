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

    private static readonly string[] EastAsia = ["CN", "HK", "TW", "MO", "SG", "MY", "KR", "VN"];
    private static readonly string[] StPatrickCountries = ["US", "IE", "GB", "CA", "AU", "NZ"];

    // Lunar New Year and Mid-Autumn move with the moon: their dates for the coming years.
    private static readonly Dictionary<int, (int M, int D)> LunarNewYear = new()
    {
        [2026] = (2, 17), [2027] = (2, 6), [2028] = (1, 26), [2029] = (2, 13), [2030] = (2, 3),
        [2031] = (1, 23), [2032] = (2, 11), [2033] = (1, 31), [2034] = (2, 19), [2035] = (2, 8),
    };
    private static readonly Dictionary<int, (int M, int D)> MidAutumn = new()
    {
        [2026] = (9, 25), [2027] = (9, 15), [2028] = (10, 3), [2029] = (9, 22), [2030] = (9, 12),
        [2031] = (10, 1), [2032] = (9, 19), [2033] = (9, 8), [2034] = (9, 27), [2035] = (9, 16),
    };

    /// <summary>
    /// The season for this date in this country (two-letter region code from the device's region setting, never
    /// GPS, and never saved or sent anywhere), or null on ordinary days.
    /// </summary>
    public static string? For(DateTime day, string? region)
    {
        var country = string.IsNullOrWhiteSpace(region) ? "US" : region.Trim().ToUpperInvariant();
        var eastAsia = EastAsia.Contains(country);
        var d = day.Date;
        var y = d.Year;
        bool In(DateTime from, DateTime to) => d >= from && d <= to;
        if (eastAsia && LunarNewYear.TryGetValue(y, out var lny))
        {
            var start = new DateTime(y, lny.M, lny.D);
            if (In(start.AddDays(-1), start.AddDays(6))) return "lunarnewyear";
        }
        if (eastAsia && MidAutumn.TryGetValue(y, out var ma))
        {
            var full = new DateTime(y, ma.M, ma.D);
            if (In(full.AddDays(-3), full.AddDays(1))) return "midautumn";
        }
        if (d == new DateTime(y, 12, 31) || In(new(y, 1, 1), new(y, 1, 2))) return "newyear";
        if (In(new(y, 12, 18), new(y, 12, 30))) return "christmas";
        if (In(new(y, 2, 8), new(y, 2, 14))) return "valentine";
        if (StPatrickCountries.Contains(country) && In(new(y, 3, 11), new(y, 3, 17))) return "stpatrick";
        var easter = EasterSunday(y);
        if (!eastAsia && In(easter.AddDays(-7), easter.AddDays(1))) return "easter";
        if (country == "US" && In(new(y, 6, 28), new(y, 7, 5))) return "july4";
        if (!eastAsia && In(new(y, 10, 24), new(y, 10, 31))) return "halloween";
        if (country is "US" or "CA")
        {
            // US: 4th Thursday of November. Canada: 2nd Monday of October. The whole week around it, Monday to Sunday.
            var t = country == "US" ? new DateTime(y, 11, 1) : new DateTime(y, 10, 1);
            var weekday = country == "US" ? DayOfWeek.Thursday : DayOfWeek.Monday;
            while (t.DayOfWeek != weekday) t = t.AddDays(1);
            t = t.AddDays(country == "US" ? 21 : 7);
            var monday = t.AddDays(-(((int)t.DayOfWeek + 6) % 7));
            if (In(monday, monday.AddDays(6))) return "thanksgiving";
        }
        return null;
    }

    /// <summary>Shows the season's icon (or the normal one) in the window, the taskbar and the tray.</summary>
    public static void Apply(bool enabled)
    {
        Current = enabled ? For(DateTime.Now, Region()) : null;
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

    private static string? Region()
    {
        try { return System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName; }
        catch (Exception) { return null; }
    }

    // Anonymous Gregorian algorithm.
    private static DateTime EasterSunday(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451, month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(y, month, day);
    }
}
