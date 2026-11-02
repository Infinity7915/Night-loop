using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.Engine.Tests;

/// <summary>Small helper to write test macros in a few lines.</summary>
internal sealed class MacroBuilder
{
    private readonly MacroDoc _doc = new() { Name = "Test" };
    private int _next = 1;

    public MacroBuilder Add(string type, JsonObject? p = null, bool enabled = true, bool breakpoint = false)
    {
        var b = Block.Create($"b{_next++}", type, p);
        b.Enabled = enabled;
        b.Breakpoint = breakpoint;
        _doc.Blocks.Add(b);
        return this;
    }

    public MacroBuilder Press(string key, int holdMs = 30) => Add(BlockTypes.Key, new() { ["key"] = key, ["holdMs"] = holdMs });
    public MacroBuilder Down(string key) => Add(BlockTypes.Key, new() { ["key"] = key, ["action"] = "down" });
    public MacroBuilder Up(string key) => Add(BlockTypes.Key, new() { ["key"] = key, ["action"] = "up" });
    public MacroBuilder Wait(int ms) => Add(BlockTypes.Wait, new() { ["ms"] = ms });
    public MacroBuilder Loop(int count) => Add(BlockTypes.Loop, new() { ["count"] = count });
    public MacroBuilder If(JsonObject condition) => Add(BlockTypes.If, new() { ["condition"] = condition });
    public MacroBuilder Else() => Add(BlockTypes.Else);
    public MacroBuilder End() => Add(BlockTypes.End);
    public MacroBuilder Label(string name) => Add(BlockTypes.Label, new() { ["name"] = name });
    public MacroBuilder Goto(string name) => Add(BlockTypes.Goto, new() { ["name"] = name });

    public MacroBuilder Repeat(int count)
    {
        _doc.Options.Repeat = new RepeatOptions { Mode = RepeatModes.Count, Count = count };
        return this;
    }

    public MacroBuilder Forever()
    {
        _doc.Options.Repeat = new RepeatOptions { Mode = RepeatModes.Forever, Count = null };
        return this;
    }

    public MacroDoc Build() => _doc;

    public static JsonObject Focused() => new() { ["kind"] = "windowFocused" };
    public static JsonObject Not(JsonObject c) => new() { ["kind"] = "not", ["condition"] = c };
}
