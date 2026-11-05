using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NightLoop.Engine.Input;

/// <summary>
/// Sends input with SendInput. Keys go out as scan codes (KEYEVENTF_SCANCODE) because many games ignore
/// virtual-key events. Every event carries <see cref="InjectedMarker"/> so the recorder can ignore our own replay.
/// One instance is shared by all runs; a lock keeps each call's events together.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed class Win32InputSender : IInputSender
{
    /// <summary>"NLOO". Put in dwExtraInfo of everything we inject.</summary>
    public const nuint InjectedMarker = 0x4E4C4F4F;

    private readonly object _lock = new();

    public void KeyDown(int virtualKey) => SendKey(virtualKey, up: false);
    public void KeyUp(int virtualKey) => SendKey(virtualKey, up: true);

    public void MouseDown(MouseButton button) => SendButton(button, up: false);
    public void MouseUp(MouseButton button) => SendButton(button, up: true);

    public void MoveTo(int x, int y)
    {
        // Absolute moves use 0–65535 across the whole virtual desktop (all monitors).
        var left = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        var top = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        var width = Math.Max(2, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN));
        var height = Math.Max(2, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));
        // Aim at the middle of the pixel: the 0–65535 scale doesn't divide evenly into pixels, and aiming at an edge
        // lands one pixel short on some screen sizes.
        var nx = (int)Math.Clamp(((long)(x - left) * 65536 + 32768) / width, 0, 65535);
        var ny = (int)Math.Clamp(((long)(y - top) * 65536 + 32768) / height, 0, 65535);
        SendMouse(MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK, nx, ny, 0);
        // Still a pixel off (unusual monitor layouts): put the cursor exactly where the recording had it.
        if (PInvoke.GetCursorPos(out var now) && (now.X != x || now.Y != y)
            && x >= left && y >= top && x < left + width && y < top + height)
            PInvoke.SetCursorPos(x, y);
    }

    public void MoveBy(int dx, int dy) => SendMouse(MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE, dx, dy, 0);

    public void Scroll(int notches) =>
        SendMouse(MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(notches * 120)));

    public void TypeChar(char c)
    {
        Span<INPUT> inputs = stackalloc INPUT[2];
        for (var i = 0; i < 2; i++)
        {
            inputs[i].type = INPUT_TYPE.INPUT_KEYBOARD;
            inputs[i].Anonymous.ki.wScan = c;
            inputs[i].Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE | (i == 1 ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0);
            inputs[i].Anonymous.ki.dwExtraInfo = InjectedMarker;
        }
        Send(inputs);
    }

    public (int X, int Y) CursorPosition => PInvoke.GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    private void SendKey(int vk, bool up)
    {
        var scan = PInvoke.MapVirtualKey((uint)vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC_EX);
        Span<INPUT> input = stackalloc INPUT[1];
        input[0].type = INPUT_TYPE.INPUT_KEYBOARD;
        ref var ki = ref input[0].Anonymous.ki;
        ki.dwExtraInfo = InjectedMarker;
        var flags = up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0;
        if (scan == 0)
        {
            // No scan code for this key on this layout: fall back to a virtual-key event.
            ki.wVk = (VIRTUAL_KEY)vk;
        }
        else
        {
            ki.wScan = (ushort)(scan & 0xFF);
            flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_SCANCODE;
            if ((scan & 0xFF00) is 0xE000 or 0xE100) flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY;
        }
        ki.dwFlags = flags;
        Send(input);
    }

    private void SendButton(MouseButton button, bool up)
    {
        var (flags, data) = (button, up) switch
        {
            (MouseButton.Left, false) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN, 0u),
            (MouseButton.Left, true) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP, 0u),
            (MouseButton.Right, false) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN, 0u),
            (MouseButton.Right, true) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP, 0u),
            (MouseButton.Middle, false) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEDOWN, 0u),
            (MouseButton.Middle, true) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEUP, 0u),
            (MouseButton.X1, false) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_XDOWN, 1u),
            (MouseButton.X1, true) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_XUP, 1u),
            (MouseButton.X2, false) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_XDOWN, 2u),
            _ => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_XUP, 2u),
        };
        SendMouse(flags, 0, 0, data);
    }

    private void SendMouse(MOUSE_EVENT_FLAGS flags, int dx, int dy, uint data)
    {
        Span<INPUT> input = stackalloc INPUT[1];
        input[0].type = INPUT_TYPE.INPUT_MOUSE;
        ref var mi = ref input[0].Anonymous.mi;
        mi.dx = dx;
        mi.dy = dy;
        mi.mouseData = data;
        mi.dwFlags = flags;
        mi.dwExtraInfo = InjectedMarker;
        Send(input);
    }

    private unsafe void Send(Span<INPUT> inputs)
    {
        lock (_lock)
        {
            var sent = PInvoke.SendInput(inputs, sizeof(INPUT));
            if (sent != inputs.Length)
                throw new InvalidOperationException("Windows blocked the input (another app may be running as administrator).");
        }
    }
}
