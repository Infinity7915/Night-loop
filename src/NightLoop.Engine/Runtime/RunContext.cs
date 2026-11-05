using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Input;
using NightLoop.Engine.Screen;
using NightLoop.Engine.Window;

namespace NightLoop.Engine.Runtime;

/// <summary>What the last Find &amp; click or Scan &amp; turn found, so Center on target can find it again.</summary>
public sealed record FoundTarget(string Kind, string Value, string? ImageData, int Tolerance, JsonNode? Region, (int X, int Y) Point,
    int ConfidencePct = 90, int? SizeTolerancePct = 0);

/// <summary>What block executors see while a run is in progress.</summary>
public sealed class RunContext
{
    internal RunContext(MacroRun run, RunEnvironment env, RunSettings settings, TrackingInputSender input, CancellationToken token)
    {
        Run = run;
        Environment = env;
        Settings = settings;
        Input = input;
        Token = token;
    }

    public MacroRun Run { get; }
    public RunEnvironment Environment { get; }
    public RunSettings Settings { get; }
    public TrackingInputSender Input { get; }
    /// <summary>Cancelled when the run stops, or when a "right away" pause interrupts the current block.</summary>
    public CancellationToken Token { get; internal set; }
    public HumanizeOptions Humanize => Run.Macro.Options.Humanize;

    /// <summary>Playback speed, 10–1000%.</summary>
    public int SpeedPct => Math.Clamp(Run.Macro.Options.SpeedPct, MacroLimits.SpeedMinPct, MacroLimits.SpeedMaxPct);

    /// <summary>The last target found on screen (for camera.center).</summary>
    public FoundTarget? LastFound { get; internal set; }

    /// <summary>Set by blocks that wait or send input; the runaway guard resets on it.</summary>
    internal bool DidWork { get; set; }

    /// <summary>
    /// Scales a wait or movement time by the playback speed (200% halves it). Key and click hold times aren't scaled,
    /// so games still register every press.
    /// </summary>
    public int Scale(int ms) => ms <= 0 ? ms : Math.Max(1, (int)Math.Round(ms * 100.0 / SpeedPct));

    /// <summary>Waits, adding humanize jitter when it's on, then applying the playback speed.</summary>
    public Task WaitAsync(int ms, bool jitter = true, bool scale = true)
    {
        DidWork = true;
        if (jitter && Humanize.Enabled && Humanize.TimingJitterPct > 0 && ms > 0)
        {
            var spread = ms * Humanize.TimingJitterPct / 100.0;
            ms = Math.Max(0, (int)Math.Round(ms + (Environment.Random.NextDouble() * 2 - 1) * spread));
        }
        return Environment.Delay.DelayAsync(scale ? Scale(ms) : ms, Token);
    }

    /// <summary>A short wait that never changes with speed or jitter (key holds, settle time between input events).</summary>
    public Task PauseExactAsync(int ms) => WaitAsync(ms, jitter: false, scale: false);

    /// <summary>
    /// Paces the steps of one movement so it takes exactly <paramref name="totalMs"/>: each step waits until its
    /// own moment on a real clock instead of "8 ms after the last one", so time spent sending input doesn't add up
    /// and replay keeps the recorded speed.
    /// </summary>
    public MotionPacer Pace(int totalMs, int steps) => new(this, totalMs, Math.Max(1, steps));

    /// <summary>The block's own process (Focus window and friends), else the macro's target game.</summary>
    public string? ProcessFor(Block b) =>
        b.GetString("process") is { Length: > 0 } p ? p.Trim() : Settings.TargetExe;

    /// <summary>The target game's client area, or null when there's no target or it isn't open.</summary>
    public ClientArea? GameArea =>
        Settings.TargetExe is { } exe && Environment.Windows is { } w ? w.GetClientArea(exe) : null;

    /// <summary>Converts a block point to screen pixels, applying gameWindow offset and humanize mouse jitter.</summary>
    public (int X, int Y) ToScreen(int x, int y, string? relativeTo, bool jitter = true)
    {
        if (relativeTo == "gameWindow" && Settings.TargetExe is { } exe && Environment.Windows is { } windows)
        {
            var area = windows.GetClientArea(exe) ?? throw new MacroFaultException($"{exe} isn't running.");
            x += area.Left;
            y += area.Top;
        }
        if (jitter && Humanize.Enabled && Humanize.MousePx > 0)
        {
            x += Environment.Random.Next(-Humanize.MousePx, Humanize.MousePx + 1);
            y += Environment.Random.Next(-Humanize.MousePx, Humanize.MousePx + 1);
        }
        return (x, y);
    }

    /// <summary>
    /// The screen area a detect block looks at. A region is relative to the game window when the macro has a target
    /// game, else to the screen. No region means the whole game window (or every monitor).
    /// </summary>
    public ScreenRect AreaFor(JsonNode? region)
    {
        var screen = Environment.Screen ?? throw NoScreen();
        var game = GameArea;
        if (MacroValidator.ReadRegion(region) is { } r)
            return game is { } g ? new ScreenRect(g.Left + r.X, g.Top + r.Y, r.Width, r.Height) : new ScreenRect(r.X, r.Y, r.Width, r.Height);
        if (game is { } a) return new ScreenRect(a.Left, a.Top, a.Width, a.Height);
        if (Settings.TargetExe is { } exe) throw new MacroFaultException($"{exe} isn't running, so there's nothing to look at.");
        var d = screen.Desktop;
        return new ScreenRect(d.Left, d.Top, d.Width, d.Height);
    }

    public ScreenImage Capture(ScreenRect area)
    {
        var screen = Environment.Screen ?? throw NoScreen();
        return screen.Capture(area.Left, area.Top, Math.Max(1, area.Width), Math.Max(1, area.Height));
    }

    /// <summary>
    /// Runs input for blocks that mostly look at the screen (Find &amp; click, Scan &amp; turn), so other macros can
    /// type while this one is reading. Waits for the game window first, like every input block.
    /// </summary>
    public async Task InputAsync(Func<Task> send)
    {
        await WaitForTargetAsync();
        await Environment.InputLock.WaitAsync(Token);
        try { await send(); }
        finally { Environment.InputLock.Release(); }
    }

    /// <summary>Target lock: never type into another app. Waits while the game isn't in front.</summary>
    internal async Task WaitForTargetAsync()
    {
        if (Settings.TargetExe is not { } exe || Environment.Windows is not { } windows) return;
        if (windows.IsForeground(exe)) return;

        // Let go so nothing stays stuck down in the other app, and press it again once the game is back.
        var keys = Input.HeldKeys;
        var buttons = Input.HeldButtons;
        Input.ReleaseAll();
        Run.SetState(RunState.WaitingForWindow);
        while (!windows.IsForeground(exe))
            await Environment.Delay.DelayAsync(Settings.WindowPollMs, Token);
        Run.SetState(RunState.Running);
        foreach (var vk in keys) Input.KeyDown(vk);
        foreach (var button in buttons) Input.MouseDown(button);
    }

    internal static MacroFaultException NoScreen() => new("Reading the screen isn't available here.");

}

/// <summary>See <see cref="RunContext.Pace"/>.</summary>
public sealed class MotionPacer
{
    private readonly RunContext _ctx;
    private readonly double _stepMs;
    private readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();
    private readonly bool _realClock;
    private int _step;

    internal MotionPacer(RunContext ctx, int totalMs, int steps)
    {
        _ctx = ctx;
        _stepMs = Math.Max(0, totalMs) / (double)steps;
        // Tests use a fake delay that returns at once; wall-clock catch-up would make no sense there.
        _realClock = ctx.Environment.Delay is PreciseDelay;
    }

    /// <summary>Waits until the next step's moment.</summary>
    public Task NextAsync()
    {
        _step++;
        var due = _stepMs * _step;
        if (!_realClock)
            return _ctx.PauseExactAsync((int)Math.Round(due) - (int)Math.Round(_stepMs * (_step - 1)));
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
        return _ctx.PauseExactAsync((int)Math.Max(0, Math.Round(due - elapsed)));
    }
}
