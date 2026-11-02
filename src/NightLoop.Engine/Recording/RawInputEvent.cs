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
/// (game-window relative when it happened inside the target game, else screen). Moves also carry the raw mouse
/// motion since the last move (<see cref="Dx"/>, <see cref="Dy"/>) and whether the cursor was hidden: games that
/// hide or lock the cursor (camera turning) only see that raw motion.
/// </summary>
public readonly record struct RawInputEvent(
    RawInputKind Kind,
    long TimeMs,
    int VirtualKey = 0,
    MouseButton Button = MouseButton.Left,
    int X = 0,
    int Y = 0,
    bool RelativeToGame = false,
    int WheelDelta = 0,
    int Dx = 0,
    int Dy = 0,
    bool Hidden = false)
{
    public static RawInputEvent KeyDown(long t, int vk) => new(RawInputKind.KeyDown, t, VirtualKey: vk);
    public static RawInputEvent KeyUp(long t, int vk) => new(RawInputKind.KeyUp, t, VirtualKey: vk);
    public static RawInputEvent MouseDown(long t, MouseButton b, int x, int y, bool game = false) => new(RawInputKind.MouseDown, t, Button: b, X: x, Y: y, RelativeToGame: game);
    public static RawInputEvent MouseUp(long t, MouseButton b, int x, int y, bool game = false) => new(RawInputKind.MouseUp, t, Button: b, X: x, Y: y, RelativeToGame: game);
    public static RawInputEvent Move(long t, int x, int y, bool game = false, int dx = 0, int dy = 0, bool hidden = false) =>
        new(RawInputKind.MouseMove, t, X: x, Y: y, RelativeToGame: game, Dx: dx, Dy: dy, Hidden: hidden);
    public static RawInputEvent Wheel(long t, int delta, int x = 0, int y = 0) => new(RawInputKind.Wheel, t, X: x, Y: y, WheelDelta: delta);
}
