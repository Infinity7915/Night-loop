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

    /// <summary>window.waitFor: waits until the window is open and in front. Faults when the timeout passes.</summary>
    public static async Task WaitForAsync(Block b, RunContext ctx)
    {
        var exe = ctx.ProcessFor(b);
        var windows = ctx.Environment.Windows;
        if (exe is null || windows is null)
        {
            ctx.Environment.Log?.Invoke("Wait for window: no process or target game, so there's nothing to wait for.");
            return;
        }
        var timeout = b.GetInt("timeoutMs", 30_000);
        var waited = 0;
        ctx.Run.SetState(RunState.WaitingForWindow);
        try
        {
            while (!(windows.GetClientArea(exe) is not null && windows.IsForeground(exe)))
            {
                if (timeout > 0 && waited >= timeout)
                    throw new MacroFaultException($"{exe} didn't open (or come to the front) within {timeout / 1000.0:0.#} s.");
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
