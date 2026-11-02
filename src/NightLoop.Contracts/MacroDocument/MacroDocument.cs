using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NightLoop.Contracts.MacroDocument;

/// <summary>
/// One macro as stored in macros.saved_doc / draft_doc. The server validates it, the client's engine runs it.
/// </summary>
public sealed class MacroDoc
{
    public int Schema { get; set; } = MacroLimits.CurrentSchema;
    public string Name { get; set; } = "";
    public string? TargetGame { get; set; }
    public MacroOptions Options { get; set; } = new();
    public List<Block> Blocks { get; set; } = [];
}

public sealed class MacroOptions
{
    public string? HotkeyStart { get; set; }
    public string? HotkeyStop { get; set; }
    /// <summary>toggle, hold or once.</summary>
    public string TriggerMode { get; set; } = TriggerModes.Toggle;
    public bool RunAlongside { get; set; } = true;
    public RepeatOptions Repeat { get; set; } = new();
    public HumanizeOptions Humanize { get; set; } = new();
}

public static class TriggerModes
{
    public const string Toggle = "toggle";
    public const string Hold = "hold";
    public const string Once = "once";
}

public sealed class RepeatOptions
{
    /// <summary>forever, count or duration.</summary>
    public string Mode { get; set; } = RepeatModes.Count;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Count { get; set; } = 1;
    /// <summary>Seconds, used when Mode is duration.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Duration { get; set; }
}

public static class RepeatModes
{
    public const string Forever = "forever";
    public const string Count = "count";
    public const string Duration = "duration";
}

public sealed class HumanizeOptions
{
    public bool Enabled { get; set; }
    public int TimingJitterPct { get; set; }
    public int MousePx { get; set; }
}

/// <summary>
/// One step on the timeline. Params differ per type, so they stay as a JSON object; use the typed
/// getters in <see cref="BlockParams"/> to read them.
/// </summary>
public sealed class Block
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Breakpoint { get; set; }
    public JsonObject Params { get; set; } = [];

    public static Block Create(string id, string type, JsonObject? parameters = null) =>
        new() { Id = id, Type = type, Params = parameters ?? [] };
}
