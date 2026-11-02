namespace NightLoop.Engine.Input;

public enum MouseButton
{
    Left,
    Right,
    Middle,
    X1,
    X2,
}

/// <summary>
/// Sends keyboard and mouse input to Windows. The real one uses SendInput; tests use a fake that records calls.
/// Coordinates are physical screen pixels (the app is per-monitor DPI aware).
/// </summary>
public interface IInputSender
{
    void KeyDown(int virtualKey);
    void KeyUp(int virtualKey);
    void MouseDown(MouseButton button);
    void MouseUp(MouseButton button);
    /// <summary>Moves the cursor to an absolute screen point.</summary>
    void MoveTo(int x, int y);
    /// <summary>Relative mouse move. Games read this for camera rotation.</summary>
    void MoveBy(int dx, int dy);
    /// <summary>Wheel notches. Positive scrolls up (away from the user).</summary>
    void Scroll(int notches);
    /// <summary>Types one character as Unicode input, independent of keyboard layout.</summary>
    void TypeChar(char c);
    (int X, int Y) CursorPosition { get; }
}

public static class MouseButtons
{
    public static bool TryParse(string? name, out MouseButton button)
    {
        switch (name ?? "left")
        {
            case "left": button = MouseButton.Left; return true;
            case "right": button = MouseButton.Right; return true;
            case "middle": button = MouseButton.Middle; return true;
            case "x1": button = MouseButton.X1; return true;
            case "x2": button = MouseButton.X2; return true;
            default: button = MouseButton.Left; return false;
        }
    }

    public static string ToName(MouseButton button) => button switch
    {
        MouseButton.Right => "right",
        MouseButton.Middle => "middle",
        MouseButton.X1 => "x1",
        MouseButton.X2 => "x2",
        _ => "left",
    };
}
