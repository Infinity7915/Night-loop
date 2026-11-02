using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Input;

namespace NightLoop.Engine.Runtime;

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
    public CancellationToken Token { get; }
    public HumanizeOptions Humanize => Run.Macro.Options.Humanize;

    /// <summary>Set by blocks that wait or send input; the runaway guard resets on it.</summary>
    internal bool DidWork { get; set; }

    /// <summary>Waits, adding humanize jitter when it's on.</summary>
    public Task WaitAsync(int ms, bool jitter = true)
    {
        DidWork = true;
        if (jitter && Humanize.Enabled && Humanize.TimingJitterPct > 0 && ms > 0)
        {
            var spread = ms * Humanize.TimingJitterPct / 100.0;
            ms = Math.Max(0, (int)Math.Round(ms + (Environment.Random.NextDouble() * 2 - 1) * spread));
        }
        return Environment.Delay.DelayAsync(ms, Token);
    }

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
}
