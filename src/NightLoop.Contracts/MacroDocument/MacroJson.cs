using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightLoop.Contracts.MacroDocument;

/// <summary>Reads and writes macro documents with the API's JSON conventions (camelCase, UTF-8).</summary>
public static class MacroJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    private static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string Serialize(MacroDoc doc, bool indented = false) =>
        JsonSerializer.Serialize(doc, indented ? Indented : Options);

    public static int ByteSize(MacroDoc doc) => Encoding.UTF8.GetByteCount(Serialize(doc));

    /// <summary>Parses a document. Throws <see cref="JsonException"/> on malformed JSON.</summary>
    public static MacroDoc Deserialize(string json) =>
        JsonSerializer.Deserialize<MacroDoc>(json, Options) ?? throw new JsonException("Macro document is null.");
}
