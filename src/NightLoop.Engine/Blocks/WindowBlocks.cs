using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Runtime;
using NightLoop.Engine.Window;

namespace NightLoop.Engine.Blocks;

/// <summary>window.* blocks. Each uses the block's own process, else the macro's target game.</summary>
internal static class WindowBlocks
{
    private const int PollMs = 250;

    public static void Focus(Block b, RunContext ctx) =>
        With(b, ctx, "bring to the front", (w, exe) => w.Focus(exe));

    public static void Resize(Block b, RunContext ctx) =>
        With(b, ctx, "resize", (w, exe) => w.Resize(exe, b.GetInt("width", 0), b.GetInt("height", 0)));

    /// <summary>Snaps the window to a corner or the middle of a monitor, or to x, y (on that monitor when one is picked).</summary>
    public static void Position(Block b, RunContext ctx) => With(b, ctx, "move", (w, exe) =>
    {
        var anchor = b.GetString("anchor", "custom");
        var monitorNumber = b.GetInt("monitor");
        if (anchor == "custom" && monitorNumber is null)
            return w.MoveTo(exe, b.GetInt("x", 0), b.GetInt("y", 0)); // older macros: plain screen coordinates

        var bounds = w.GetWindowBounds(exe);
        var monitors = w.GetMonitors();
        if (bounds is not { } win || monitors.Count == 0) return false;

        // 0 = the monitor the window is on now, 1 = primary, 2 = monitor 2…
        var (cx, cy) = (win.Left + win.Width / 2, win.Top + win.Height / 2);
        var area = monitorNumber is null or 0
            ? monitors.FirstOrDefault(m => m.Contains(cx, cy), monitors[0])
            : monitors[Math.Min(monitorNumber.Value, monitors.Count) - 1];

        var (x, y) = anchor switch
        {
            "center" => (area.Left + (area.Width - win.Width) / 2, area.Top + (area.Height - win.Height) / 2),
            "topLeft" => (area.Left, area.Top),
            "topRight" => (area.Left + area.Width - win.Width, area.Top),
            "bottomLeft" => (area.Left, area.Top + area.Height - win.Height),
            "bottomRight" => (area.Left + area.Width - win.Width, area.Top + area.Height - win.Height),
            _ => (area.Left + b.GetInt("x", 0), area.Top + b.GetInt("y", 0)),
        };
        return w.MoveTo(exe, x, y);
    });

    /// <summary>
    /// window.waitFor: waits until the window is open (until "open") or open and in front (until "front", the default).
    /// Faults when the timeout passes.
    /// </summary>
    public static async Task WaitForAsync(Block b, RunContext ctx)
    {
        var exe = ctx.ProcessFor(b);
        var windows = ctx.Environment.Windows;
        if (exe is null || windows is null)
        {
            ctx.Environment.Log?.Invoke("Wait for window: no process or target game, so there's nothing to wait for.");
            return;
        }
        var front = b.GetString("until", "front") != "open";
        await WaitForWindowAsync(ctx, windows, exe, front, b.GetInt("timeoutMs", 30_000),
            s => front ? $"{exe} didn't open (or come to the front) within {s}." : $"{exe} didn't open a window within {s}.");
    }

    /// <summary>
    /// window.open: starts a program (or focuses it when it's already running), then waits for its window and brings
    /// it to the front. The path may use variables like %LOCALAPPDATA%, so a macro works on every PC.
    /// </summary>
    public static async Task OpenAsync(Block b, RunContext ctx)
    {
        var windows = ctx.Environment.Windows ?? throw new MacroFaultException("Opening programs isn't available here.");
        var path = (b.GetString("path") ?? "").Trim();
        if (path.Length == 0) throw new MacroFaultException("Pick the program to open.");
        var name = FileName(path);
        // The window to wait for: the one picked on the block, else the program itself.
        var waitFor = b.GetString("process") is { Length: > 0 } p ? p.Trim() : name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : null;
        ctx.DidWork = true;

        if (b.GetString("ifRunning", "focus") == "focus" && waitFor is not null && windows.IsProcessRunning(waitFor) && windows.GetClientArea(waitFor) is not null)
        {
            windows.Focus(waitFor);
            return;
        }

        string? started;
        try
        {
            started = windows.Launch(path, b.GetString("args"));
        }
        catch (InvalidOperationException ex)
        {
            throw new MacroFaultException(ex.Message);
        }
        if (b.GetBool("wait") == false) return;
        waitFor ??= started;
        if (waitFor is null)
        {
            ctx.Environment.Log?.Invoke($"Opened {name}. Night-loop can't tell which window it opens, so it didn't wait for one.");
            return;
        }
        await WaitForWindowAsync(ctx, windows, waitFor, front: false, b.GetInt("timeoutMs", 30_000),
            s => $"{name} didn't open a window within {s}.");
        windows.Focus(waitFor);
    }

    /// <summary>Programs a macro may never close: Night-loop itself and the parts of Windows that keep the desktop running.</summary>
    internal static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "NightLoop.exe", "explorer.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "lsass.exe", "services.exe",
        "smss.exe", "svchost.exe", "dwm.exe", "System", "Registry", "fontdrvhost.exe", "sihost.exe",
    };

    /// <summary>
    /// window.close: closes the program like clicking its X ("close"), then, for "closeThenEnd", ends it if it's still
    /// open after the timeout. "end" ends it straight away, like End task in Task Manager.
    /// </summary>
    public static async Task CloseAsync(Block b, RunContext ctx)
    {
        var windows = ctx.Environment.Windows ?? throw new MacroFaultException("Closing programs isn't available here.");
        var exe = ctx.ProcessFor(b);
        if (exe is null)
        {
            ctx.Environment.Log?.Invoke("Close window: pick a program, or a target game for this macro, to close.");
            return;
        }
        var name = FileName(exe);
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        if (Protected.Contains(name) || Protected.Contains(name[..^4]))
            throw new MacroFaultException($"Night-loop won't close {name}: Windows (or Night-loop) needs it to keep running.");
        ctx.DidWork = true;
        if (!windows.IsProcessRunning(exe))
        {
            ctx.Environment.Log?.Invoke($"Close window: {name} isn't running.");
            return;
        }

        var how = b.GetString("how", "close");
        var timeoutMs = b.GetInt("timeoutMs", 10_000);
        try
        {
            if (how != "end")
            {
                windows.CloseWindows(exe);
                if (await WaitForExitAsync(ctx, windows, exe, timeoutMs)) return;
                if (how == "close")
                {
                    ctx.Environment.Log?.Invoke($"Close window: {name} is still open (it may be asking to save). Pick \"Close, then force\" to end it anyway.");
                    return;
                }
            }
            windows.EndProcesses(exe);
        }
        catch (InvalidOperationException ex)
        {
            throw new MacroFaultException(ex.Message);
        }
        if (!await WaitForExitAsync(ctx, windows, exe, ForceWaitMs))
            ctx.Environment.Log?.Invoke($"Close window: Windows wouldn't let Night-loop end {name} (it may be running as administrator).");
    }

    /// <summary>How long to wait for a forced close to finish.</summary>
    private const int ForceWaitMs = 5_000;

    /// <summary>True once the program has closed; false when the timeout passes first (0 = don't wait).</summary>
    private static async Task<bool> WaitForExitAsync(RunContext ctx, IWindowService windows, string exe, int timeoutMs)
    {
        for (var waited = 0; windows.IsProcessRunning(exe); waited += PollMs)
        {
            if (waited >= timeoutMs) return false;
            await ctx.Environment.Delay.DelayAsync(PollMs, ctx.Token);
        }
        return true;
    }

    private static async Task WaitForWindowAsync(RunContext ctx, IWindowService windows, string exe, bool front, int timeoutMs,
        Func<string, string> timeoutMessage)
    {
        var waited = 0;
        ctx.Run.SetState(RunState.WaitingForWindow);
        try
        {
            while (!(windows.GetClientArea(exe) is not null && (!front || windows.IsForeground(exe))))
            {
                if (timeoutMs > 0 && waited >= timeoutMs)
                    throw new MacroFaultException(timeoutMessage($"{timeoutMs / 1000.0:0.#} s"));
                await ctx.Environment.Delay.DelayAsync(PollMs, ctx.Token);
                waited += PollMs;
                ctx.DidWork = true;
            }
        }
        finally
        {
            ctx.Run.SetState(RunState.Running);
        }
    }

    /// <summary>"C:\Games\Game.exe" → "Game.exe" (works for Windows paths on any OS, for the tests).</summary>
    internal static string FileName(string path)
    {
        var trimmed = path.Trim().Trim('"').TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }

    private static void With(Block b, RunContext ctx, string verb, Func<IWindowService, string, bool> action)
    {
        var exe = ctx.ProcessFor(b);
        if (exe is null || ctx.Environment.Windows is not { } windows)
        {
            ctx.Environment.Log?.Invoke($"Skipped: pick a process or a target game for this macro to {verb} its window.");
            return;
        }
        if (!windows.IsProcessRunning(exe))
        {
            ctx.Environment.Log?.Invoke($"Couldn't {verb} {exe}: it isn't running.");
            return;
        }
        if (!action(windows, exe))
            ctx.Environment.Log?.Invoke($"Couldn't {verb} the {exe} window (is it fullscreen or minimized?).");
    }
}
