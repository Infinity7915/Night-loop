using NightLoop.Contracts.MacroDocument;

namespace NightLoop.App.ViewModels;

/// <summary>Short chip tag, title and one-line detail for each block type, as in the design mockups.</summary>
public static class BlockDescriber
{
    public static (string Tag, string Title, string Detail, string Category) Describe(Block b)
    {
        var def = BlockCatalog.For(b.Type);
        var category = def?.Category ?? Capitalize(b.Type.Split('.')[0]);
        var tag = def?.Tag ?? Fallback(b.Type);
        var title = def?.Label ?? b.Type;
        var detail = b.Type switch
        {
            BlockTypes.Key => b.GetString("action", "press") switch
            {
                "down" => $"Hold down {b.GetString("key", "?")}",
                "up" => $"Release {b.GetString("key", "?")}",
                _ => $"Press {b.GetString("key", "?")} for {b.GetInt("holdMs", 30)} ms",
            },
            BlockTypes.Click => $"{ClickTitle(b)} {Where(b)}",
            BlockTypes.Move => $"{Where(b)} · {b.GetInt("durationMs", 0)} ms",
            BlockTypes.Drag => DragDetail(b),
            BlockTypes.Scroll => b.GetInt("amount", 0) >= 0 ? $"up {b.GetInt("amount", 0)}" : $"down {-b.GetInt("amount", 0)}",
            BlockTypes.Type => $"\"{b.GetString("text", "")}\"",
            BlockTypes.CameraTurn => $"{b.GetString("direction", "right")} {b.GetInt("amountPx", 0)}px",
            BlockTypes.CameraTurnUntilText => $"{b.GetString("direction", "right")} until \"{b.GetString("text", "")}\"",
            BlockTypes.CameraTurnUntilImage => $"{b.GetString("direction", "right")} until image",
            BlockTypes.Wait => $"{b.GetInt("ms", 0)} ms",
            BlockTypes.WaitRandom => $"{b.GetInt("minMs", 0)}–{b.GetInt("maxMs", 0)} ms",
            BlockTypes.WaitForText => $"\"{b.GetString("text", "")}\" · {b.GetInt("timeoutMs", 0)} ms",
            BlockTypes.WaitForImage => $"{b.GetInt("timeoutMs", 0)} ms",
            BlockTypes.DetectPixelColor => $"{b.GetInt("x", 0)}, {b.GetInt("y", 0)} = {b.GetString("color", "")}",
            BlockTypes.Pause => $"{b.GetString("target", "?")}",
            BlockTypes.If or BlockTypes.RepeatUntil => Conditions.Describe(b.GetObject("condition")),
            BlockTypes.Loop => $"{b.GetInt("count", 1)} times",
            BlockTypes.Label => b.GetString("name", ""),
            BlockTypes.Goto => $"→ {b.GetString("name", "")}",
            BlockTypes.WindowResize => $"{b.GetInt("width", 0)}×{b.GetInt("height", 0)}",
            BlockTypes.WindowPosition => $"{b.GetInt("x", 0)}, {b.GetInt("y", 0)}",
            BlockTypes.Comment => b.GetString("text", ""),
            BlockTypes.Description => b.GetString("text", ""),
            BlockTypes.Notify => $"\"{b.GetString("message", "")}\"",
            BlockTypes.PlaySound => b.GetString("sound", "default"),
            BlockTypes.RunMacro => b.GetString("macroName", ""),
            _ => "",
        };
        if (b.Type == BlockTypes.Pause) title = b.GetString("mode") == "resume" ? "Resume macro" : "Pause macro";
        return (tag, title, detail, category);
    }

    private static string ClickTitle(Block b)
    {
        var button = b.GetString("button", "left");
        return b.GetString("action", "press") switch
        {
            "down" => $"{button} down",
            "up" => $"{button} up",
            _ => b.GetInt("clicks", 1) switch { 2 => $"{button} ×2", 3 => $"{button} ×3", _ => button },
        };
    }

    private static string Where(Block b) =>
        b.GetInt("x") is { } x && b.GetInt("y") is { } y
            ? $"at {x}, {y}{(b.GetString("relativeTo") == "gameWindow" ? " in game" : "")}"
            : "at cursor";

    private static string DragDetail(Block b)
    {
        var f = b.GetPoint("from");
        var t = b.GetPoint("to");
        return f is null || t is null ? "" : $"{f.Value.X}, {f.Value.Y} → {t.Value.X}, {t.Value.Y} · {b.GetInt("durationMs", 0)} ms";
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Fallback(string type)
    {
        var last = type.Split('.').Last().ToUpperInvariant();
        return last[..Math.Min(4, last.Length)];
    }
}
