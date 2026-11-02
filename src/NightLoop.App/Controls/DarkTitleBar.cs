using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace NightLoop.App.Controls;

/// <summary>
/// Makes the Windows title bar dark and the same purple as the window, instead of the white or accent-colored
/// bar Windows draws by default. Dark mode works on Windows 10 20H1+; the exact color needs Windows 11.
/// Older versions ignore it.
/// </summary>
public static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20; // DWMWA_USE_IMMERSIVE_DARK_MODE
    private const int CaptionColor = 35;         // DWMWA_CAPTION_COLOR
    private const int TextColor = 36;            // DWMWA_TEXT_COLOR

    public static void Apply(Window window, Color caption, Color text)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var on = 1;
        _ = DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref on, sizeof(int));
        var captionRef = ToColorRef(caption);
        _ = DwmSetWindowAttribute(hwnd, CaptionColor, ref captionRef, sizeof(int));
        var textRef = ToColorRef(text);
        _ = DwmSetWindowAttribute(hwnd, TextColor, ref textRef, sizeof(int));
    }

    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
