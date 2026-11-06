namespace NightLoop.Engine.Input;

/// <summary>
/// Wraps the shared sender for one macro run and remembers every key and button that run is holding,
/// so stop, pause, fault and sign-out can release them all (schema section 7, "Safety").
/// </summary>
public sealed class TrackingInputSender(IInputSender inner) : IInputSender
{
    private readonly object _gate = new();
    private readonly HashSet<int> _keys = [];
    private readonly HashSet<MouseButton> _buttons = [];

    public IReadOnlyCollection<int> HeldKeys { get { lock (_gate) return _keys.ToArray(); } }
    public IReadOnlyCollection<MouseButton> HeldButtons { get { lock (_gate) return _buttons.ToArray(); } }

    public void KeyDown(int virtualKey)
    {
        lock (_gate) _keys.Add(virtualKey);
        inner.KeyDown(virtualKey);
    }

    public void KeyUp(int virtualKey)
    {
        // Forget the key only once Windows took the release, so a failed one is tried again by ReleaseAll.
        inner.KeyUp(virtualKey);
        lock (_gate) _keys.Remove(virtualKey);
    }

    public void MouseDown(MouseButton button)
    {
        lock (_gate) _buttons.Add(button);
        inner.MouseDown(button);
    }

    public void MouseUp(MouseButton button)
    {
        inner.MouseUp(button);
        lock (_gate) _buttons.Remove(button);
    }

    public void MoveTo(int x, int y) => inner.MoveTo(x, y);
    public void MoveBy(int dx, int dy) => inner.MoveBy(dx, dy);
    public void Scroll(int notches) => inner.Scroll(notches);
    public void TypeChar(char c) => inner.TypeChar(c);
    public (int X, int Y) CursorPosition => inner.CursorPosition;

    /// <summary>Releases everything this run pressed. Safe to call any number of times.</summary>
    public void ReleaseAll()
    {
        int[] keys;
        MouseButton[] buttons;
        lock (_gate)
        {
            keys = [.. _keys];
            buttons = [.. _buttons];
            _keys.Clear();
            _buttons.Clear();
        }
        // One release failing must not leave the rest held down.
        foreach (var b in buttons) Try(() => inner.MouseUp(b));
        foreach (var k in keys) Try(() => inner.KeyUp(k));
    }

    private static void Try(Action release)
    {
        try { release(); }
        catch (InvalidOperationException) { }
    }
}
