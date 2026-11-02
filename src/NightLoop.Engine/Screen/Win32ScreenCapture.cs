using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NightLoop.Engine.Screen;

/// <summary>
/// Screen capture with GDI BitBlt. Fast enough for pixel checks, text reading and screenshots. Games in exclusive
/// fullscreen can come back black; borderless or windowed mode works.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed class Win32ScreenCapture : IScreenCapture
{
    public (int Left, int Top, int Width, int Height) Desktop => (
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));

    public unsafe ScreenImage Capture(int left, int top, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var pixels = new byte[width * height * 4];

        var screen = PInvoke.GetDC(default);
        var memory = PInvoke.CreateCompatibleDC(screen);
        var bitmap = PInvoke.CreateCompatibleBitmap(screen, width, height);
        var old = PInvoke.SelectObject(memory, bitmap);
        try
        {
            PInvoke.BitBlt(memory, 0, 0, width, height, screen, left, top, ROP_CODE.SRCCOPY);
            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER),
                    biWidth = width,
                    biHeight = -height, // top-down rows
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                },
            };
            fixed (byte* p = pixels)
                PInvoke.GetDIBits(memory, bitmap, 0, (uint)height, p, &info, DIB_USAGE.DIB_RGB_COLORS);
        }
        finally
        {
            PInvoke.SelectObject(memory, old);
            PInvoke.DeleteObject(bitmap);
            PInvoke.DeleteDC(memory);
            _ = PInvoke.ReleaseDC(default, screen);
        }
        return new ScreenImage(left, top, width, height, pixels);
    }
}
