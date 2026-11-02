using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace NightLoop.App.Services;

/// <summary>Brings the window that was in front before Night-loop back to the front, like Alt+Tab would.</summary>
public static class WindowSwitcher
{
    private const uint GW_HWNDNEXT = 2;
    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_CLOAKED = 14;

    /// <summary>Activates the next real app window below <paramref name="window"/>. Returns false when there is none.</summary>
    public static bool ActivateWindowBehind(Window window)
    {
        var own = new WindowInteropHelper(window).Handle;
        if (own == IntPtr.Zero) return false;
        GetWindowThreadProcessId(own, out var ownPid);
        // Each window lower in the z-order, nearest first; stop after a few hundred in case the list loops.
        var h = own;
        for (var i = 0; i < 500; i++)
        {
            h = GetWindow(h, GW_HWNDNEXT);
            if (h == IntPtr.Zero) return false;
            if (!IsAppWindow(h, ownPid)) continue;
            return SetForegroundWindow(h);
        }
        return false;
    }

    private static bool IsAppWindow(IntPtr h, uint ownPid)
    {
        if (!IsWindowVisible(h) || IsIconic(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
        if (GetWindowTextLength(h) == 0) return false;
        GetWindowThreadProcessId(h, out var pid);
        if (pid == ownPid) return false;
        var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        if ((ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return false;
        // Windows 10/11 keep some hidden-but-"visible" windows (other desktops, suspended Store apps).
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var cls = new StringBuilder(64);
        GetClassName(h, cls, cls.Capacity);
        return cls.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd");
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
