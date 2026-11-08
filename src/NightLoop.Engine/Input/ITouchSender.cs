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
    /// <summary>back, home, recents or notifications.</summary>
    void PressButton(string button);
    /// <summary>Starts an app by package name. False when it isn't installed.</summary>
    bool OpenApp(string package);
    /// <summary>The package of the app in front, when known.</summary>
    string? ForegroundApp { get; }
}
