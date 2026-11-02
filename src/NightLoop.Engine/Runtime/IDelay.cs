using System.Diagnostics;

namespace NightLoop.Engine.Runtime;

/// <summary>Waits. Tests swap in a fake so runs finish instantly.</summary>
public interface IDelay
{
    Task DelayAsync(int milliseconds, CancellationToken cancellationToken);
}

/// <summary>
/// Task.Delay for the bulk of a wait, then a short spin for the last stretch. Windows timers tick about
/// every 15 ms, so this keeps short waits (and recorded timing) accurate to about a millisecond.
/// </summary>
public sealed class PreciseDelay : IDelay
{
    public static readonly PreciseDelay Instance = new();
    private const int SpinThresholdMs = 16;

    public async Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        if (milliseconds <= 0) return;
        var deadline = Stopwatch.GetTimestamp() + milliseconds * Stopwatch.Frequency / 1000;

        if (milliseconds > SpinThresholdMs)
            await Task.Delay(milliseconds - SpinThresholdMs, cancellationToken).ConfigureAwait(false);

        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Yield();
        }
    }
}
