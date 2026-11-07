using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace NightLoop.App.Services;

/// <summary>
/// Night-loop's icon in the Windows notification area (system tray). Closing the window can hide Night-loop
/// there so hotkeys and macros keep running; right-click › Exit quits for real. The Notify block's messages go out
/// through it as Windows notifications (Windows 10 and 11 show tray balloons as regular notifications in the
/// Action Center / notification center).
/// </summary>
/// <remarks>
/// Talks to Windows directly (Shell_NotifyIcon) instead of through WinForms' NotifyIcon, so Night-loop doesn't ship
/// or load all of WinForms for one icon.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const int IconId = 1;
    private const int MenuOpen = 1;
    private const int MenuExit = 2;

    private readonly Action _open;
    private readonly Action _exit;
    private readonly HwndSource _window;
    private readonly int _taskbarCreated;
    private IntPtr _hicon;
    private bool _added;

    /// <summary>The app's one tray icon, or null before the window opens (and in the CI smoke test).</summary>
    public static TrayIcon? Current { get; private set; }

    private TrayIcon(Action open, Action exit)
    {
        _open = open;
        _exit = exit;
        // A hidden window that only receives the icon's clicks. Not message-only: the menu needs a window that can
        // come to the front, or it wouldn't close when you click somewhere else.
        _window = new HwndSource(new HwndSourceParameters("Night-loop tray") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        _hicon = LoadIcon();
        Add();
    }

    /// <param name="open">Shows the window again (tray click, Open, a click on a notification).</param>
    /// <param name="exit">Quits Night-loop (tray menu › Exit).</param>
    public static TrayIcon Create(Action open, Action exit)
    {
        Current?.Dispose();
        return Current = new TrayIcon(open, exit);
    }

    /// <summary>Shows a Windows notification. Returns false when there's no tray icon to send it from.</summary>
    public static bool Notify(string title, string message)
    {
        if (Current is not { } tray) return false;
        var data = tray.Data(Native.NIF_INFO);
        data.szInfoTitle = Clip(title, 63);
        data.szInfo = Clip(message.Length == 0 ? " " : message, 255);
        data.dwInfoFlags = Native.NIIF_NONE;
        data.uTimeoutOrVersion = 5000;
        if (!Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data))
        {
            // Explorer may have restarted and dropped the icon: put it back and try once more.
            tray.Add();
            Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
        }
        return true;
    }

    private void Add()
    {
        var data = Data(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP);
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _hicon;
        data.szTip = "Night-loop";
        if (_added) Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
        _added = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
    }

    private Native.NOTIFYICONDATAW Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)lParam & 0xFFFF)
            {
                case Native.WM_LBUTTONUP:
                case Native.NIN_BALLOONUSERCLICK:
                    _open();
                    break;
                case Native.WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            // Explorer restarted (or the taskbar came back): icons have to be added again.
            _added = false;
            Add();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = Native.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            Native.AppendMenu(menu, Native.MF_STRING, MenuOpen, "Open Night-loop");
            Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);
            Native.AppendMenu(menu, Native.MF_STRING, MenuExit, "Exit");
            Native.GetCursorPos(out var at);
            // Without this the menu stays open after clicking elsewhere (documented TrackPopupMenu quirk).
            Native.SetForegroundWindow(_window.Handle);
            var picked = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN,
                at.X, at.Y, _window.Handle, IntPtr.Zero);
            Native.PostMessage(_window.Handle, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL, same quirk
            if (picked == MenuOpen) _open();
            else if (picked == MenuExit) _exit();
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    /// <summary>The app icon at the tray's size, from the .ico embedded in the app.</summary>
    private static IntPtr LoadIcon()
    {
        try
        {
            using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("NightLoop.icon.ico");
            if (stream is not null)
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                var icon = FromIco(copy.ToArray(), Native.GetSystemMetrics(Native.SM_CXSMICON), Native.GetSystemMetrics(Native.SM_CYSMICON));
                if (icon != IntPtr.Zero) return icon;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or Win32Exception)
        {
        }
        return Native.LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
    }

    /// <summary>Picks the smallest picture in an .ico file that is at least the wanted size, and makes an icon of it.</summary>
    internal static IntPtr FromIco(byte[] ico, int width, int height)
    {
        if (PickIcoEntry(ico, width) is not { } entry) return IntPtr.Zero;
        var (offset, length) = entry;
        var bits = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.Copy(ico, offset, bits, length);
            return Native.CreateIconFromResourceEx(bits, length, true, 0x00030000, width, height, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(bits);
        }
    }

    /// <summary>Where the best picture for <paramref name="size"/> sits in an .ico file, or null if it isn't one.</summary>
    internal static (int Offset, int Length)? PickIcoEntry(byte[] ico, int size)
    {
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 0) != 0 || BitConverter.ToUInt16(ico, 2) != 1) return null;
        var count = BitConverter.ToUInt16(ico, 4);
        (int Offset, int Length, int Side)? best = null;
        for (var i = 0; i < count; i++)
        {
            var at = 6 + i * 16;
            if (at + 16 > ico.Length) break;
            var side = ico[at] == 0 ? 256 : ico[at];
            var length = BitConverter.ToInt32(ico, at + 8);
            var offset = BitConverter.ToInt32(ico, at + 12);
            if (length <= 0 || offset < 0 || offset > ico.Length - length) continue;
            // Prefer the smallest that is big enough; otherwise the biggest there is.
            var better = best is not { } b
                || (side >= size && (b.Side < size || side < b.Side))
                || (side < size && b.Side < size && side > b.Side);
            if (better) best = (offset, length, side);
        }
        return best is { } pick ? (pick.Offset, pick.Length) : null;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_added)
        {
            var data = Data(0);
            Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
            _added = false;
        }
        if (_hicon != IntPtr.Zero)
        {
            Native.DestroyIcon(_hicon);
            _hicon = IntPtr.Zero;
        }
        _window.RemoveHook(WndProc);
        _window.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    private static class Native
    {
        public const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
        public const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
        public const int NIIF_NONE = 0;
        public const int NIN_BALLOONUSERCLICK = 0x0405;
        public const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
        public const int MF_STRING = 0x0, MF_SEPARATOR = 0x800;
        public const int TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100;
        public const int SM_CXSMICON = 49, SM_CYSMICON = 50;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATAW data);

        [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AppendMenu(IntPtr menu, int flags, nint id, string? text);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        public static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr hwnd, IntPtr tpm);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        public static extern IntPtr CreateIconFromResourceEx(IntPtr bits, int size, [MarshalAs(UnmanagedType.Bool)] bool icon, int version, int cx, int cy, int flags);

        [DllImport("user32.dll", EntryPoint = "LoadIconW")]
        public static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr icon);
    }
}
