using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace NightLoop.App.Services;

/// <summary>A program with a window, for "Pick a running app".</summary>
public sealed record RunningProgram(string Title, string Exe, string? Path)
{
    public override string ToString() => $"{Title} ({Exe})";
}

/// <summary>Picking programs for Window blocks: File Explorer, the running apps, and paths that work on any PC.</summary>
public static class Programs
{
    /// <summary>
    /// Opens File Explorer to pick a program. <paramref name="anyFile"/> also allows shortcuts, scripts and other
    /// files (Open window can open those). Returns the full path, or null when cancelled.
    /// </summary>
    public static string? Browse(string? current, bool anyFile)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = anyFile ? "Pick the program to open" : "Pick the program",
            Filter = anyFile
                ? "Programs and shortcuts|*.exe;*.lnk;*.bat;*.cmd;*.url|Programs (*.exe)|*.exe|All files|*.*"
                : "Programs (*.exe)|*.exe",
            CheckFileExists = true,
            DereferenceLinks = true, // a Start menu shortcut gives the program it points at
        };
        var expanded = string.IsNullOrWhiteSpace(current) ? null : Environment.ExpandEnvironmentVariables(current.Trim('"'));
        var folder = expanded is not null && System.IO.Path.IsPathRooted(expanded) ? System.IO.Path.GetDirectoryName(expanded) : null;
        dialog.InitialDirectory = folder is not null && Directory.Exists(folder)
            ? folder
            : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var owner = Application.Current?.MainWindow;
        var ok = owner is { IsVisible: true } ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? dialog.FileName : null;
    }

    /// <summary>
    /// Swaps the start of a path for %LOCALAPPDATA%, %APPDATA%, %USERPROFILE% or %ProgramFiles% when it's in one of
    /// those, so the macro still finds the program on another PC or Windows account.
    /// </summary>
    public static string Portable(string path)
    {
        (string Var, Environment.SpecialFolder Folder)[] folders =
        [
            ("%LOCALAPPDATA%", Environment.SpecialFolder.LocalApplicationData),
            ("%APPDATA%", Environment.SpecialFolder.ApplicationData),
            ("%ProgramFiles(x86)%", Environment.SpecialFolder.ProgramFilesX86),
            ("%ProgramFiles%", Environment.SpecialFolder.ProgramFiles),
            ("%USERPROFILE%", Environment.SpecialFolder.UserProfile),
        ];
        foreach (var (name, folder) in folders)
        {
            var dir = Environment.GetFolderPath(folder).TrimEnd('\\');
            if (dir.Length > 3 && path.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) return name + path[dir.Length..];
        }
        return path;
    }

    /// <summary>Programs with a window right now, by window title. Night-loop itself is left out.</summary>
    public static List<RunningProgram> Running()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<RunningProgram>();
        var own = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == own || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                var exe = p.ProcessName + ".exe";
                if (!seen.Add(exe)) continue;
                var title = p.MainWindowTitle.Length > 40 ? p.MainWindowTitle[..40] + "…" : p.MainWindowTitle;
                list.Add(new RunningProgram(title, exe, PathOf(p.Id)));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // It ended or is protected.
            }
            finally
            {
                p.Dispose();
            }
        }
        return [.. list.OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The program file of a running process, when Windows lets us read it (not for apps run as admin).</summary>
    private static string? PathOf(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
