namespace NightLoop.Contracts.MacroDocument;

/// <summary>
/// Key names used in input.key blocks and hotkeys, mapped to Windows virtual-key codes.
/// The engine turns the virtual key into a scan code at send time, because many games ignore virtual-key events.
/// </summary>
public static class KeyCatalog
{
    private static readonly Dictionary<string, int> NameToVk = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> VkToName = new();

    static KeyCatalog()
    {
        for (var c = 'A'; c <= 'Z'; c++) Add(c.ToString(), c);
        for (var d = 0; d <= 9; d++) Add(d.ToString(), '0' + d);
        for (var f = 1; f <= 24; f++) Add($"F{f}", 0x70 + f - 1);
        for (var n = 0; n <= 9; n++) Add($"NumPad{n}", 0x60 + n);

        Add("Backspace", 0x08); Add("Tab", 0x09); Add("Enter", 0x0D); Add("Pause", 0x13);
        Add("CapsLock", 0x14); Add("Escape", 0x1B); Add("Space", 0x20);
        Add("PageUp", 0x21); Add("PageDown", 0x22); Add("End", 0x23); Add("Home", 0x24);
        Add("Left", 0x25); Add("Up", 0x26); Add("Right", 0x27); Add("Down", 0x28);
        Add("PrintScreen", 0x2C); Add("Insert", 0x2D); Add("Delete", 0x2E);
        Add("LeftWin", 0x5B); Add("RightWin", 0x5C); Add("Apps", 0x5D);
        Add("Multiply", 0x6A); Add("Add", 0x6B); Add("Subtract", 0x6D); Add("Decimal", 0x6E); Add("Divide", 0x6F);
        Add("NumLock", 0x90); Add("ScrollLock", 0x91);
        Add("LeftShift", 0xA0); Add("RightShift", 0xA1);
        Add("LeftCtrl", 0xA2); Add("RightCtrl", 0xA3);
        Add("LeftAlt", 0xA4); Add("RightAlt", 0xA5);
        Add("Semicolon", 0xBA); Add("Equals", 0xBB); Add("Comma", 0xBC); Add("Minus", 0xBD);
        Add("Period", 0xBE); Add("Slash", 0xBF); Add("Backtick", 0xC0);
        Add("LeftBracket", 0xDB); Add("Backslash", 0xDC); Add("RightBracket", 0xDD); Add("Quote", 0xDE);

        // Generic modifiers. Older recorded macros use the left/right names; these are for blocks and hotkeys.
        AddAlias("Shift", 0x10); AddAlias("Ctrl", 0x11); AddAlias("Control", 0x11); AddAlias("Alt", 0x12);
        AddAlias("Win", 0x5B); AddAlias("Esc", 0x1B); AddAlias("Return", 0x0D); AddAlias("Del", 0x2E); AddAlias("Ins", 0x2D);
    }

    private static void Add(string name, int vk)
    {
        NameToVk[name] = vk;
        VkToName.TryAdd(vk, name);
    }

    private static void AddAlias(string name, int vk)
    {
        NameToVk[name] = vk;
        VkToName.TryAdd(vk, name);
    }

    public static bool TryGetVirtualKey(string name, out int vk) => NameToVk.TryGetValue(name.Trim(), out vk);

    public static bool IsKnown(string name) => NameToVk.ContainsKey(name.Trim());

    /// <summary>The canonical name for a virtual key, or null if Night-loop doesn't know it.</summary>
    public static string? NameOf(int vk) => VkToName.TryGetValue(vk, out var name) ? name : null;

    public static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);
}
