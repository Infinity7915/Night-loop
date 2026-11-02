using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Input;
using NightLoop.Engine.Native;
using NightLoop.Engine.Window;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NightLoop.Engine.Recording;

public sealed class RecorderSettings
{
    /// <summary>Exe name of the target game. Clicks inside its window are saved relative to it.</summary>
    public string? TargetExe { get; init; }
    /// <summary>Pressing this stops recording. It is not recorded itself.</summary>
    public Hotkey? StopHotkey { get; init; }
    public bool RecordMouseMoves { get; init; }
    /// <summary>Keep at most one mouse position every this many milliseconds (raw motion in between is added up).</summary>
    public int MoveSampleMs { get; init; } = 16;
    /// <summary>Skip clicks and keys aimed at Night-loop's own windows (like the Record button).</summary>
    public bool IgnoreOwnWindows { get; init; } = true;
}

/// <summary>
/// Records keyboard and mouse with low-level hooks (WH_KEYBOARD_LL, WH_MOUSE_LL), plus Raw Input for the mouse's
/// own motion: games that hide or lock the cursor to turn the camera only see that motion, and it's what replay
/// sends back. The hook callbacks only copy the event and return, so the rest of Windows never waits on us.
/// Input Night-loop injects itself is ignored, so replaying while recording doesn't loop.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed class InputRecorder : IDisposable
{
    private static InputRecorder? s_active;

    private readonly object _lock = new();
    private readonly List<RawInputEvent> _events = [];
    private readonly HashSet<int> _held = [];
    private readonly Stopwatch _clock = new();
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private RecorderSettings _settings = new();
    private MessageLoopThread? _loop;
    private HHOOK _keyboardHook;
    private HHOOK _mouseHook;
    private Timer? _targetRefresh;
    private HWND _target;
    private long _lastMoveMs = long.MinValue;
    private int _swallowKeyUp = -1;
    private HWND _rawWindow;
    private int _rawDx;
    private int _rawDy;
    private Point _lastPoint;

    /// <summary>Raised on a thread-pool thread when the stop hotkey is pressed. Call <see cref="Stop"/> from it.</summary>
    public event Action? StopHotkeyPressed;

    public bool IsRecording { get; private set; }

    public int EventCount { get { lock (_lock) return _events.Count; } }

    public unsafe void Start(RecorderSettings settings)
    {
        if (IsRecording) throw new InvalidOperationException("Already recording.");
        if (Interlocked.CompareExchange(ref s_active, this, null) is not null)
            throw new InvalidOperationException("Another recording is already running.");

        _settings = settings;
        lock (_lock)
        {
            _events.Clear();
            _held.Clear();
        }
        _swallowKeyUp = -1;
        _lastMoveMs = long.MinValue;
        _rawDx = _rawDy = 0;
        RefreshTarget();
        _targetRefresh = new Timer(_ => RefreshTarget(), null, 1000, 1000);

        _loop = new MessageLoopThread("Night-loop recorder", OnLoopMessage);
        _loop.Invoke(() =>
        {
            var module = PInvoke.GetModuleHandle((PCWSTR)null);
            var hinstance = new HINSTANCE(module.Value);
            _keyboardHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &KeyboardProc, hinstance, 0);
            _mouseHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, &MouseProc, hinstance, 0);
            StartRawInput(hinstance);
        });

        if (_keyboardHook.IsNull || _mouseHook.IsNull)
        {
            StopHooks();
            throw new InvalidOperationException("Windows refused the input hooks, so recording can't start.");
        }
        _clock.Restart();
        // Where the cursor starts, so the first click glides from the right place on replay.
        if (PInvoke.GetCursorPos(out var start))
        {
            _lastPoint = start;
            var p = Locate(start);
            Add(RawInputEvent.Move(0, p.X, p.Y, p.Game, hidden: IsCursorHidden()));
        }
        IsRecording = true;
    }

    /// <summary>Stops recording and turns what was captured into blocks.</summary>
    public RecordingResult Stop(RecordingOptions? options = null)
    {
        if (!IsRecording) return new RecordingResult([], false);
        StopHooks();
        IsRecording = false;

        List<RawInputEvent> events;
        lock (_lock) events = [.. _events];
        options ??= new RecordingOptions { RecordMouseMoves = _settings.RecordMouseMoves, StopHotkey = _settings.StopHotkey };
        return RecordingBuilder.Build(events, options);
    }

    private void StopHooks()
    {
        _targetRefresh?.Dispose();
        _targetRefresh = null;
        _loop?.Invoke(() =>
        {
            if (!_keyboardHook.IsNull) PInvoke.UnhookWindowsHookEx(_keyboardHook);
            if (!_mouseHook.IsNull) PInvoke.UnhookWindowsHookEx(_mouseHook);
            _keyboardHook = default;
            _mouseHook = default;
            StopRawInput();
        });
        _loop?.Dispose();
        _loop = null;
        Interlocked.CompareExchange(ref s_active, null, this);
    }

    private void RefreshTarget() =>
        _target = _settings.TargetExe is { } exe ? Win32WindowService.FindMainWindow(exe) : HWND.Null;

    private void Add(RawInputEvent e)
    {
        lock (_lock)
        {
            if (_events.Count < 200_000) _events.Add(e);
        }
    }

    private (int X, int Y, bool Game) Locate(Point pt)
    {
        if (!_target.IsNull && Win32WindowService.ClientAreaOf(_target) is { } area && area.Contains(pt.X, pt.Y))
            return (pt.X - area.Left, pt.Y - area.Top, true);
        return (pt.X, pt.Y, false);
    }

    private bool IsOwnWindowAt(Point pt) =>
        _settings.IgnoreOwnWindows && Win32WindowService.ProcessIdOf(PInvoke.WindowFromPoint(pt)) == _ownPid;

    private bool IsOwnWindowInFront() =>
        _settings.IgnoreOwnWindows && Win32WindowService.ProcessIdOf(PInvoke.GetForegroundWindow()) == _ownPid;

    private void OnKey(uint message, uint vk)
    {
        var down = message is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;
        var t = _clock.ElapsedMilliseconds;
        var key = (int)vk;

        if (!down && key == _swallowKeyUp)
        {
            _swallowKeyUp = -1;
            _held.Remove(key);
            return;
        }
        if (down) _held.Add(key); else _held.Remove(key);

        if (down && _settings.StopHotkey is { } hk && key == hk.VirtualKey && ModifiersMatch(hk.Modifiers))
        {
            _swallowKeyUp = key;
            var handler = StopHotkeyPressed;
            if (handler is not null) ThreadPool.QueueUserWorkItem(_ => handler());
            return;
        }
        if (IsOwnWindowInFront()) return;

        Add(down ? RawInputEvent.KeyDown(t, key) : RawInputEvent.KeyUp(t, key));
    }

    private bool ModifiersMatch(HotkeyModifiers mods)
    {
        bool Any(params int[] vks) => vks.Any(_held.Contains);
        return Any(0xA2, 0xA3) == mods.HasFlag(HotkeyModifiers.Ctrl)
            && Any(0xA4, 0xA5) == mods.HasFlag(HotkeyModifiers.Alt)
            && Any(0xA0, 0xA1) == mods.HasFlag(HotkeyModifiers.Shift)
            && Any(0x5B, 0x5C) == mods.HasFlag(HotkeyModifiers.Win);
    }

    private void OnMouse(uint message, Point pt, uint mouseData)
    {
        var t = _clock.ElapsedMilliseconds;
        switch (message)
        {
            case PInvoke.WM_MOUSEMOVE:
                _lastPoint = pt;
                if (t - _lastMoveMs < _settings.MoveSampleMs) return;
                AddMove(t, pt);
                return;
            case PInvoke.WM_MOUSEWHEEL:
                if (IsOwnWindowAt(pt)) return;
                FlushMove(t, pt);
                var w = Locate(pt);
                Add(RawInputEvent.Wheel(t, (short)(mouseData >> 16), w.X, w.Y));
                return;
        }

        (MouseButton Button, bool Down)? button = message switch
        {
            PInvoke.WM_LBUTTONDOWN => (MouseButton.Left, true),
            PInvoke.WM_LBUTTONUP => (MouseButton.Left, false),
            PInvoke.WM_RBUTTONDOWN => (MouseButton.Right, true),
            PInvoke.WM_RBUTTONUP => (MouseButton.Right, false),
            PInvoke.WM_MBUTTONDOWN => (MouseButton.Middle, true),
            PInvoke.WM_MBUTTONUP => (MouseButton.Middle, false),
            PInvoke.WM_XBUTTONDOWN => ((mouseData >> 16) == 2 ? MouseButton.X2 : MouseButton.X1, true),
            PInvoke.WM_XBUTTONUP => ((mouseData >> 16) == 2 ? MouseButton.X2 : MouseButton.X1, false),
            _ => null,
        };
        if (button is null || IsOwnWindowAt(pt)) return;

        FlushMove(t, pt);
        var p = Locate(pt);
        Add(button.Value.Down
            ? RawInputEvent.MouseDown(t, button.Value.Button, p.X, p.Y, p.Game)
            : RawInputEvent.MouseUp(t, button.Value.Button, p.X, p.Y, p.Game));
    }

    private void AddMove(long t, Point pt)
    {
        _lastMoveMs = t;
        var m = Locate(pt);
        Add(RawInputEvent.Move(t, m.X, m.Y, m.Game, _rawDx, _rawDy, IsCursorHidden()));
        _rawDx = _rawDy = 0;
    }

    /// <summary>Before a click or wheel: save motion that hasn't been sampled yet, so it lands before the click.</summary>
    private void FlushMove(long t, Point pt)
    {
        if (_rawDx != 0 || _rawDy != 0 || pt != _lastPoint) AddMove(t, pt);
        _lastPoint = pt;
    }

    private static unsafe bool IsCursorHidden()
    {
        var info = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
        return PInvoke.GetCursorInfo(ref info) && (info.flags & CURSORINFO_FLAGS.CURSOR_SHOWING) == 0;
    }

    // ---- Raw Input: the mouse's own motion, even when a game hides or pins the cursor ----

    private unsafe void StartRawInput(HINSTANCE hinstance)
    {
        // A message-only window receives WM_INPUT; INPUTSINK keeps it coming while games are in front.
        _rawWindow = PInvoke.CreateWindowEx(0, "STATIC", "Night-loop recorder", 0, 0, 0, 0, 0, new HWND(-3), null, null, null);
        if (_rawWindow.IsNull) return;
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = 0x01, // generic desktop
            usUsage = 0x02,     // mouse
            dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK,
            hwndTarget = _rawWindow,
        };
        if (!PInvoke.RegisterRawInputDevices(new Span<RAWINPUTDEVICE>(&device, 1), (uint)sizeof(RAWINPUTDEVICE)))
        {
            PInvoke.DestroyWindow(_rawWindow);
            _rawWindow = default;
        }
    }

    private unsafe void StopRawInput()
    {
        if (_rawWindow.IsNull) return;
        var device = new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE };
        PInvoke.RegisterRawInputDevices(new Span<RAWINPUTDEVICE>(&device, 1), (uint)sizeof(RAWINPUTDEVICE));
        PInvoke.DestroyWindow(_rawWindow);
        _rawWindow = default;
    }

    private unsafe void OnLoopMessage(MSG msg)
    {
        if (msg.message != PInvoke.WM_INPUT || !IsRecording) return;
        RAWINPUT raw;
        var size = (uint)sizeof(RAWINPUT);
        var read = PInvoke.GetRawInputData(new HRAWINPUT(msg.lParam.Value), RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, &raw, ref size, (uint)sizeof(RAWINPUTHEADER));
        if (read == unchecked((uint)-1) || raw.header.dwType != (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEMOUSE) return;
        // Injected input (ours or another tool's) has no device. Absolute devices (pens, remote desktop) have no motion to add up.
        if (raw.header.hDevice.IsNull || ((ushort)raw.data.mouse.usFlags & 0x01) != 0) return;
        _rawDx += raw.data.mouse.lLastX;
        _rawDy += raw.data.mouse.lLastY;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT KeyboardProc(int nCode, WPARAM wParam, LPARAM lParam)
    {
        var self = s_active;
        if (nCode >= 0 && self is not null)
        {
            try
            {
                var k = (KBDLLHOOKSTRUCT*)lParam.Value;
                if (k->dwExtraInfo != Win32InputSender.InjectedMarker)
                    self.OnKey((uint)wParam.Value, k->vkCode);
            }
            catch
            {
                // Never let an exception cross into Windows.
            }
        }
        return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT MouseProc(int nCode, WPARAM wParam, LPARAM lParam)
    {
        var self = s_active;
        if (nCode >= 0 && self is not null)
        {
            try
            {
                var m = (MSLLHOOKSTRUCT*)lParam.Value;
                if (m->dwExtraInfo != Win32InputSender.InjectedMarker)
                    self.OnMouse((uint)wParam.Value, m->pt, m->mouseData);
            }
            catch
            {
                // Never let an exception cross into Windows.
            }
        }
        return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (IsRecording)
        {
            StopHooks();
            IsRecording = false;
        }
    }
}
