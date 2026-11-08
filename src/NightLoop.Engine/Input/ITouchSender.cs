namespace NightLoop.Engine.Input;

/// <summary>
/// Touches the screen and presses Android's system buttons. The Android app implements it with an accessibility
/// service; Windows has none, so touch blocks fault there. Coordinates are real screen pixels.
/// </summary>
public interface ITouchSender
{
    /// <summary>Presses one spot for <paramref name="holdMs"/> and completes when the touch has ended.</summary>
    Task TapAsync(int x, int y, int holdMs, CancellationToken ct);
    /// <summary>Drags a finger in a straight line over <paramref name="durationMs"/>.</summary>
    Task SwipeAsync(int fromX, int fromY, int toX, int toY, int durationMs, CancellationToken ct);
    /// <summary>
    /// Drags a finger from one spot to another over <paramref name="durationMs"/>, then keeps it down at the end.
    /// While it's down, <paramref name="keepHolding"/> is asked again and again (the finger stays down while it
    /// runs); the finger lifts once it answers false.
    /// </summary>
    Task SwipeHoldAsync(int fromX, int fromY, int toX, int toY, int durationMs, Func<Task<bool>> keepHolding, CancellationToken ct);
    /// <summary>
    /// Puts a finger down at (x, y) and keeps it down, moving it wherever <paramref name="next"/> says, piece by piece
    /// (the finger stays down while it is asked). It lifts once <paramref name="next"/> answers null.
    /// </summary>
    Task SteerAsync(int x, int y, Func<Task<(int X, int Y)?>> next, CancellationToken ct);
    /// <summary>back, home, recents or notifications.</summary>
    void PressButton(string button);
    /// <summary>Starts an app by package name. False when it isn't installed.</summary>
    bool OpenApp(string package);
    /// <summary>The package of the app in front, when known.</summary>
    string? ForegroundApp { get; }
}
