using System.Text.Json;
using System.Text.Json.Nodes;

namespace NightLoop.LocalData;

/// <summary>
/// settings.json: per-PC preferences. Safe to delete; defaults return. Only the keys this class knows are
/// read; anything else in the file is kept when it is saved.
/// </summary>
public sealed class LocalSettings(string path)
{
    /// <summary>The public Night-loop server. Override with "serverUrl" or NIGHTLOOP_SERVER (e.g. a local dev server).</summary>
    public const string DefaultServerUrl = "https://api.night-loop.win/v1/";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public LocalSettings() : this(AppPaths.Settings) { }

    /// <summary>The API base URL, always ending in "/". NIGHTLOOP_SERVER wins over settings.json.</summary>
    public Uri ServerUrl
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("NIGHTLOOP_SERVER");
            if (string.IsNullOrWhiteSpace(raw)) raw = ReadString("serverUrl");
            return Normalize(raw) ?? new Uri(DefaultServerUrl);
        }
    }

    /// <summary>Last email typed on the sign-in screen, so it's filled in next time.</summary>
    public string? LastEmail
    {
        get => ReadString("lastEmail");
        set => Write("lastEmail", value);
    }

    /// <summary>Theme mode: "dark" (default), "light" or "system".</summary>
    public string Theme
    {
        get => ReadString("theme") switch { "light" => "light", "system" => "system", _ => "dark" };
        set => Write("theme", value);
    }

    /// <summary>Accent color as #RRGGBB, or null for the default.</summary>
    public string? Accent
    {
        get => ReadString("accent") is { } a && IsHexColor(a) ? a.ToUpperInvariant() : null;
        set => Write("accent", value is not null && IsHexColor(value) ? value.ToUpperInvariant() : null);
    }

    /// <summary>Name of the target game picked in Settings ("N/A" or missing = no target).</summary>
    public string? ActiveGame
    {
        get => ReadString("activeGame");
        set => Write("activeGame", value);
    }

    /// <summary>New macros start with a short ready-made block sequence (Settings › Macros). On unless turned off.</summary>
    public bool StarterSequence
    {
        get => Read()?["starterSequence"] is not JsonValue v || !v.TryGetValue(out bool on) || on;
        set => WriteNode("starterSequence", value);
    }

    /// <summary>Keep Night-loop above every other window (Settings › Appearance). Off unless turned on.</summary>
    public bool AlwaysOnTop
    {
        get => Read()?["alwaysOnTop"] is JsonValue v && v.TryGetValue(out bool on) && on;
        set => WriteNode("alwaysOnTop", value);
    }

    /// <summary>The start / stop recording key, like "F9" or "Ctrl+Shift+R". Null means the default (F9).</summary>
    public string? RecordHotkey
    {
        get => ReadString("recordHotkey");
        set => Write("recordHotkey", value);
    }

    public static bool IsHexColor(string s) =>
        s.Length == 7 && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);

    internal static Uri? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (!text.EndsWith('/')) text += "/";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        // Passwords and tokens only travel over HTTPS. Plain HTTP is allowed for a server on this PC (development).
        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ? uri : null;
    }

    private string? ReadString(string key) =>
        Read()?[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private JsonObject? Read()
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private void Write(string key, string? value) => WriteNode(key, value);

    private void WriteNode(string key, JsonNode? value)
    {
        try
        {
            var obj = Read() ?? [];
            obj[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, obj.ToJsonString(Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A preference that can't be saved isn't worth interrupting anyone over.
        }
    }
}
