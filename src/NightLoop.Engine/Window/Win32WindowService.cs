using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NightLoop.Engine.Window;

/// <summary>Finds game windows by exe name and moves, sizes and focuses them.</summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed class Win32WindowService : IWindowService
{
    public bool IsForeground(string exeName)
    {
        var hwnd = PInvoke.GetForegroundWindow();
        return !hwnd.IsNull && ProcessMatches(hwnd, exeName);
    }

    public bool IsProcessRunning(string exeName)
    {
        var procs = Process.GetProcessesByName(StripExe(exeName));
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    public ClientArea? GetClientArea(string exeName)
    {
        var hwnd = FindMainWindow(exeName);
        return hwnd.IsNull ? null : ClientAreaOf(hwnd);
    }

    public bool Resize(string exeName, int width, int height)
    {
        var hwnd = FindMainWindow(exeName);
        return !hwnd.IsNull && PInvoke.SetWindowPos(hwnd, HWND.Null, 0, 0, width, height,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    public bool MoveTo(string exeName, int x, int y)
    {
        var hwnd = FindMainWindow(exeName);
        return !hwnd.IsNull && PInvoke.SetWindowPos(hwnd, HWND.Null, x, y, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    public bool Focus(string exeName)
    {
        var hwnd = FindMainWindow(exeName);
        if (hwnd.IsNull) return false;
        if (PInvoke.IsIconic(hwnd)) PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
        PInvoke.AllowSetForegroundWindow(unchecked((uint)-1)); // ASFW_ANY
        return PInvoke.SetForegroundWindow(hwnd);
    }

    public string? Launch(string path, string? arguments)
    {
        var full = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        var start = new ProcessStartInfo(full) { UseShellExecute = true, Arguments = arguments ?? "" };
        // Programs often look for their files next to themselves.
        if (Path.GetDirectoryName(full) is { Length: > 0 } dir && Directory.Exists(dir)) start.WorkingDirectory = dir;
        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(ex.NativeErrorCode switch
            {
                2 or 3 => $"{Path.GetFileName(full)} wasn't found at {full}.",
                1223 => $"Opening {Path.GetFileName(full)} was cancelled.",
                _ => $"Windows couldn't open {Path.GetFileName(full)}: {ex.Message}",
            });
        }
        using (process)
        {
            var fallback = full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(full) : null;
            if (process is null) return fallback;
            try
            {
                return ExeNameOf((uint)process.Id) ?? fallback;
            }
            catch (InvalidOperationException)
            {
                return fallback; // it already exited (a launcher that handed off to the real program)
            }
        }
    }

    public ClientArea? GetWindowBounds(string exeName)
    {
        var hwnd = FindMainWindow(exeName);
        if (hwnd.IsNull || !PInvoke.GetWindowRect(hwnd, out var r)) return null;
        return new ClientArea(r.left, r.top, r.right - r.left, r.bottom - r.top);
    }

    public unsafe IReadOnlyList<ClientArea> GetMonitors()
    {
        var list = new List<(ClientArea Area, bool Primary)>();
        var handle = GCHandle.Alloc(list);
        try
        {
            PInvoke.EnumDisplayMonitors(default, (RECT*)null, &OnMonitor, (LPARAM)GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Area.Left).Select(m => m.Area).ToList();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe BOOL OnMonitor(HMONITOR monitor, HDC dc, RECT* rect, LPARAM data)
    {
        var list = (List<(ClientArea Area, bool Primary)>)GCHandle.FromIntPtr(data).Target!;
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (PInvoke.GetMonitorInfo(monitor, ref info))
        {
            var w = info.rcWork;
            list.Add((new ClientArea(w.left, w.top, w.right - w.left, w.bottom - w.top), (info.dwFlags & 1) != 0)); // MONITORINFOF_PRIMARY
        }
        return true;
    }

    /// <summary>The foreground window when it belongs to the game (most accurate), else the process's main window.</summary>
    internal static HWND FindMainWindow(string exeName)
    {
        var fg = PInvoke.GetForegroundWindow();
        if (!fg.IsNull && ProcessMatches(fg, exeName)) return fg;

        foreach (var p in Process.GetProcessesByName(StripExe(exeName)))
        {
            using (p)
            {
                if (p.MainWindowHandle != IntPtr.Zero) return new HWND(p.MainWindowHandle);
            }
        }
        return HWND.Null;
    }

    internal static unsafe ClientArea? ClientAreaOf(HWND hwnd)
    {
        if (!PInvoke.GetClientRect(hwnd, out var rect)) return null;
        var origin = new Point(0, 0);
        if (!PInvoke.ClientToScreen(hwnd, ref origin)) return null;
        return new ClientArea(origin.X, origin.Y, rect.right - rect.left, rect.bottom - rect.top);
    }

    internal static unsafe bool ProcessMatches(HWND hwnd, string exeName)
    {
        uint pid;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        return pid != 0 && string.Equals(ExeNameOf(pid), exeName, StringComparison.OrdinalIgnoreCase);
    }

    internal static unsafe uint ProcessIdOf(HWND hwnd)
    {
        uint pid;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        return pid;
    }

    /// <summary>Exe file name of a process, e.g. RobloxPlayerBeta.exe. Works without admin rights.</summary>
    internal static unsafe string? ExeNameOf(uint pid)
    {
        using var handle = PInvoke.OpenProcess_SafeHandle(
            global::Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsInvalid) return null;

        var buffer = stackalloc char[1024];
        uint size = 1024;
        if (!PInvoke.QueryFullProcessImageName(handle, 0, new PWSTR(buffer), ref size)) return null;
        return Path.GetFileName(new string(buffer, 0, (int)size));
    }

    private static string StripExe(string exe) =>
        exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
}
