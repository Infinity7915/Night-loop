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
using Windows.Win32.UI.WindowsAndMessaging;

namespace NightLoop.Engine.Recording;

public sealed class RecorderSettings
{
    /// <summary>Exe name of the target game. Clicks inside its window are saved relative to it.</summary>
    public string? TargetExe { get; init; }
    /// <summary>Pressing this stops recording. It is not recorded itself.</summary>
    public Hotkey? StopHotkey { get; init; }
    public bool RecordMouseMoves { get; init; }
    /// <summary>When recording moves, keep at most one every this many milliseconds.</summary>
    public int MoveSampleMs { get; init; } = 30;
    /// <summary>Skip clicks and keys aimed at Night-loop's own windows (like the Record button).</summary>
    public bool IgnoreOwnWindows { get; init; } = true;
}

/// <summary>
/// Records keyboard and mouse with low-level hooks (WH_KEYBOARD_LL, WH_MOUSE_LL). The hook callbacks only
/// copy the event and return, so the rest of Windows never waits on us. Input Night-loop injects itself
/// is ignored, so replaying while recording doesn't loop.
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
        RefreshTarget();
        _targetRefresh = new Timer(_ => RefreshTarget(), null, 1000, 1000);

        _loop = new MessageLoopThread("Night-loop recorder", _ => { });
        _loop.Invoke(() =>
        {
            var module = PInvoke.GetModuleHandle((PCWSTR)null);
            var hinstance = new HINSTANCE(module.Value);
            _keyboardHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &KeyboardProc, hinstance, 0);
            _mouseHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, &MouseProc, hinstance, 0);
        });

        if (_keyboardHook.IsNull || _mouseHook.IsNull)
        {
            StopHooks();
            throw new InvalidOperationException("Windows refused the input hooks, so recording can't start.");
        }
        _clock.Restart();
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
                if (t - _lastMoveMs < _settings.MoveSampleMs) return;
                _lastMoveMs = t;
                var m = Locate(pt);
                Add(RawInputEvent.Move(t, m.X, m.Y, m.Game));
                return;
            case PInvoke.WM_MOUSEWHEEL:
                if (IsOwnWindowAt(pt)) return;
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

        var p = Locate(pt);
        Add(button.Value.Down
            ? RawInputEvent.MouseDown(t, button.Value.Button, p.X, p.Y, p.Game)
            : RawInputEvent.MouseUp(t, button.Value.Button, p.X, p.Y, p.Game));
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
