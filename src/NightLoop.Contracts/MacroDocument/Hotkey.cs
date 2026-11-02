namespace NightLoop.Contracts.MacroDocument;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A key combo such as "Ctrl+F6" or "F7".</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = HotkeyModifiers.None;
        int? vk = null;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotkeyModifiers.Ctrl; continue;
                case "alt": mods |= HotkeyModifiers.Alt; continue;
                case "shift": mods |= HotkeyModifiers.Shift; continue;
                case "win": mods |= HotkeyModifiers.Win; continue;
            }
            if (vk is not null || !KeyCatalog.TryGetVirtualKey(part, out var code)) return false;
            vk = code;
        }
        if (vk is null) return false;
        hotkey = new Hotkey(mods, vk.Value);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyCatalog.NameOf(VirtualKey) ?? $"0x{VirtualKey:X2}");
        return string.Join('+', parts);
    }

    /// <summary>Stops every running macro at once (schema section 7).</summary>
    public static readonly Hotkey Panic = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x23);
}
