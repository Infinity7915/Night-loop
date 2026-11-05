namespace NightLoop.Engine.Window;

/// <summary>A window's client area in screen pixels.</summary>
public readonly record struct ClientArea(int Left, int Top, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= Left && y >= Top && x < Left + Width && y < Top + Height;
}

/// <summary>Finds and controls the target game's window. Processes are matched by exe name, e.g. RobloxPlayerBeta.exe.</summary>
public interface IWindowService
{
    /// <summary>True when the window in front belongs to a process with this exe name.</summary>
    bool IsForeground(string exeName);

    bool IsProcessRunning(string exeName);

    /// <summary>The target's main window client area, or null if the game isn't running.</summary>
    ClientArea? GetClientArea(string exeName);

    bool Resize(string exeName, int width, int height);
    bool MoveTo(string exeName, int x, int y);
    bool Focus(string exeName);

    /// <summary>The whole window (frame included) in screen pixels, or null if it isn't open.</summary>
    ClientArea? GetWindowBounds(string exeName) => null;

    /// <summary>Each monitor's work area (without the taskbar), primary monitor first.</summary>
    IReadOnlyList<ClientArea> GetMonitors() => [];

    /// <summary>
    /// Starts a program (or opens a file or shortcut, like double-clicking it). Returns the exe name of the process it
    /// started, when Windows says, so its window can be waited for. Throws <see cref="InvalidOperationException"/>
    /// with a readable message when it can't be started.
    /// </summary>
    string? Launch(string path, string? arguments) => throw new InvalidOperationException("Opening programs isn't available here.");

    /// <summary>Asks every window of the program to close, like clicking its X. Returns how many were asked.</summary>
    int CloseWindows(string exeName) => throw new InvalidOperationException("Closing programs isn't available here.");

    /// <summary>Ends the program at once, like End task in Task Manager (unsaved work is lost). Returns how many ended.</summary>
    int EndProcesses(string exeName) => throw new InvalidOperationException("Closing programs isn't available here.");
}
