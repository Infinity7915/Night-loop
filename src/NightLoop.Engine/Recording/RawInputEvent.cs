using NightLoop.Engine.Input;

namespace NightLoop.Engine.Recording;

public enum RawInputKind
{
    KeyDown,
    KeyUp,
    MouseDown,
    MouseUp,
    MouseMove,
    Wheel,
}

/// <summary>
/// One input event captured by the hooks, already converted to the coordinates it will be saved in
/// (game-window relative when it happened inside the target game, else screen).
/// </summary>
public readonly record struct RawInputEvent(
    RawInputKind Kind,
    long TimeMs,
    int VirtualKey = 0,
    MouseButton Button = MouseButton.Left,
    int X = 0,
    int Y = 0,
    bool RelativeToGame = false,
    int WheelDelta = 0)
{
    public static RawInputEvent KeyDown(long t, int vk) => new(RawInputKind.KeyDown, t, VirtualKey: vk);
    public static RawInputEvent KeyUp(long t, int vk) => new(RawInputKind.KeyUp, t, VirtualKey: vk);
    public static RawInputEvent MouseDown(long t, MouseButton b, int x, int y, bool game = false) => new(RawInputKind.MouseDown, t, Button: b, X: x, Y: y, RelativeToGame: game);
    public static RawInputEvent MouseUp(long t, MouseButton b, int x, int y, bool game = false) => new(RawInputKind.MouseUp, t, Button: b, X: x, Y: y, RelativeToGame: game);
    public static RawInputEvent Move(long t, int x, int y, bool game = false) => new(RawInputKind.MouseMove, t, X: x, Y: y, RelativeToGame: game);
    public static RawInputEvent Wheel(long t, int delta, int x = 0, int y = 0) => new(RawInputKind.Wheel, t, X: x, Y: y, WheelDelta: delta);
}
