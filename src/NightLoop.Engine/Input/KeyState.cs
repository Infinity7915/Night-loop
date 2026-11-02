using System.Runtime.Versioning;
using Windows.Win32;

namespace NightLoop.Engine.Input;

/// <summary>Reads whether a key or mouse button is held right now, anywhere in Windows.</summary>
[SupportedOSPlatform("windows10.0.17763")]
public static class KeyState
{
    /// <summary>True while held, or if it was tapped since the last check (so quick taps between checks count).</summary>
    public static bool IsDown(int virtualKey)
    {
        var state = PInvoke.GetAsyncKeyState(virtualKey);
        return (state & 0x8001) != 0;
    }
}
