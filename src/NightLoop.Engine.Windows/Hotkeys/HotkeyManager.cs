using System.Runtime.Versioning;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Native;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NightLoop.Engine.Hotkeys;

/// <summary>Thrown when another program already owns a hotkey.</summary>
public sealed class HotkeyTakenException(Hotkey hotkey)
    : Exception($"{hotkey} is already used by another program.")
{
    public Hotkey Hotkey { get; } = hotkey;
}

/// <summary>
/// System-wide hotkeys through RegisterHotKey (start/stop keys and the Ctrl+Alt+End panic key).
/// Callbacks run on a thread-pool thread, never on the hotkey thread.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed class HotkeyManager : IDisposable
{
    private readonly Dictionary<int, (Hotkey Key, Action Callback)> _registered = [];
    private readonly MessageLoopThread _loop;
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyManager()
    {
        _loop = new MessageLoopThread("Night-loop hotkeys", OnMessage);
    }

    /// <summary>Registers a hotkey. Dispose the result to unregister it.</summary>
    /// <exception cref="HotkeyTakenException">Another program already uses this combo.</exception>
    public IDisposable Register(Hotkey hotkey, Action callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = _loop.Invoke(() =>
        {
            var newId = _nextId++;
            var mods = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= HOT_KEY_MODIFIERS.MOD_ALT;
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) mods |= HOT_KEY_MODIFIERS.MOD_CONTROL;
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= HOT_KEY_MODIFIERS.MOD_SHIFT;
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Win)) mods |= HOT_KEY_MODIFIERS.MOD_WIN;
            if (!PInvoke.RegisterHotKey(default, newId, mods, (uint)hotkey.VirtualKey))
                return -1;
            _registered[newId] = (hotkey, callback);
            return newId;
        });
        if (id < 0) throw new HotkeyTakenException(hotkey);
        return new Registration(this, id);
    }

    private void Unregister(int id)
    {
        if (_disposed) return; // everything was unregistered already
        _loop.Invoke(() =>
        {
            if (_registered.Remove(id)) PInvoke.UnregisterHotKey(default, id);
        });
    }

    private void OnMessage(MSG msg)
    {
        if (msg.message != PInvoke.WM_HOTKEY) return;
        if (_registered.TryGetValue((int)msg.wParam.Value, out var entry))
            ThreadPool.QueueUserWorkItem(_ => entry.Callback());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loop.Invoke(() =>
        {
            foreach (var id in _registered.Keys) PInvoke.UnregisterHotKey(default, id);
            _registered.Clear();
        });
        _loop.Dispose();
    }

    private sealed class Registration(HotkeyManager owner, int id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Unregister(id);
        }
    }
}
