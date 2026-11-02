using NightLoop.Contracts.MacroDocument;

namespace NightLoop.App.ViewModels;

/// <summary>Short chip tag, title and one-line detail for each block type, as in the design mockups.</summary>
public static class BlockDescriber
{
    public static (string Tag, string Title, string Detail, string Category) Describe(Block b)
    {
        var category = b.Type.Split('.')[0];
        return b.Type switch
        {
            BlockTypes.Key => b.GetString("action", "press") switch
            {
                "down" => ("KEY", "Key down", b.GetString("key", "?"), category),
                "up" => ("KEY", "Key up", b.GetString("key", "?"), category),
                _ => ("KEY", "Press key", $"{b.GetString("key", "?")} for {b.GetInt("holdMs", 30)} ms", category),
            },
            BlockTypes.Click => ("CLK", ClickTitle(b), Where(b), category),
            BlockTypes.Move => ("MOVE", "Move mouse", $"{Where(b)} · {b.GetInt("durationMs", 0)} ms", category),
            BlockTypes.Drag => ("DRAG", "Drag", DragDetail(b), category),
            BlockTypes.Scroll => ("SCRL", "Scroll", b.GetInt("amount", 0) > 0 ? $"up {b.GetInt("amount", 0)}" : $"down {-b.GetInt("amount", 0)}", category),
            BlockTypes.Type => ("TYPE", "Type text", $"\"{b.GetString("text", "")}\"", category),
            BlockTypes.CameraTurn => ("CAM", "Turn camera", $"{b.GetString("direction", "right")} {b.GetInt("amountPx", 0)}px", category),
            BlockTypes.Wait => ("WAIT", "Wait", $"{b.GetInt("ms", 0)} ms", category),
            BlockTypes.WaitRandom => ("RND", "Random wait", $"{b.GetInt("minMs", 0)}-{b.GetInt("maxMs", 0)} ms", category),
            BlockTypes.If => ("IF", "If", Condition(b), category),
            BlockTypes.Else => ("ELSE", "Else", "", category),
            BlockTypes.End => ("END", "End", "", category),
            BlockTypes.Loop => ("LOOP", "Loop", $"{b.GetInt("count", 1)} times", category),
            BlockTypes.RepeatUntil => ("RPT", "Repeat until", Condition(b), category),
            BlockTypes.Label => ("LBL", "Label", b.GetString("name", ""), category),
            BlockTypes.Goto => ("GOTO", "Go to", b.GetString("name", ""), category),
            BlockTypes.Stop => ("STOP", "Stop macro", "", category),
            BlockTypes.WindowFocus => ("WIN", "Focus window", "", category),
            BlockTypes.WindowResize => ("SIZE", "Resize window", $"{b.GetInt("width", 0)}×{b.GetInt("height", 0)}", category),
            BlockTypes.WindowPosition => ("POS", "Position window", $"{b.GetInt("x", 0)}, {b.GetInt("y", 0)}", category),
            BlockTypes.Comment => ("NOTE", "Comment", b.GetString("text", ""), category),
            BlockTypes.Description => ("DESC", "Description", b.GetString("text", ""), category),
            BlockTypes.Notify => ("NTF", "Notify", $"\"{b.GetString("message", "")}\"", category),
            _ => (b.Type.Split('.').Last().ToUpperInvariant()[..Math.Min(4, b.Type.Split('.').Last().Length)], b.Type, "", category),
        };
    }

    private static string ClickTitle(Block b)
    {
        var button = b.GetString("button", "left");
        var name = char.ToUpperInvariant(button[0]) + button[1..];
        return b.GetString("action", "press") switch
        {
            "down" => $"{name} button down",
            "up" => $"{name} button up",
            _ => b.GetInt("clicks", 1) switch { 2 => $"Double {button} click", 3 => $"Triple {button} click", _ => $"{name} click" },
        };
    }

    private static string Where(Block b) =>
        b.GetInt("x") is { } x && b.GetInt("y") is { } y
            ? $"{x}, {y}{(b.GetString("relativeTo") == "gameWindow" ? " in game" : "")}"
            : "at cursor";

    private static string DragDetail(Block b)
    {
        var f = b.GetPoint("from");
        var t = b.GetPoint("to");
        return f is null || t is null ? "" : $"{f.Value.X}, {f.Value.Y} → {t.Value.X}, {t.Value.Y} · {b.GetInt("durationMs", 0)} ms";
    }

    private static string Condition(Block b) =>
        b.GetObject("condition")?["kind"]?.ToString() ?? "";
}
