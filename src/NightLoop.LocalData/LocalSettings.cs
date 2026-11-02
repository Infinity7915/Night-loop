using System.Text.Json;
using System.Text.Json.Nodes;

namespace NightLoop.LocalData;

/// <summary>
/// settings.json: per-PC preferences. Safe to delete; defaults return. Only the keys this class knows are
/// read; anything else in the file is kept when it is saved.
/// </summary>
public sealed class LocalSettings(string path)
{
    /// <summary>Where the app looks for the server until a public one exists. Override with "serverUrl" or NIGHTLOOP_SERVER.</summary>
    public const string DefaultServerUrl = "http://localhost:5080/v1/";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public LocalSettings() : this(AppPaths.Settings) { }

    /// <summary>The API base URL, always ending in "/". NIGHTLOOP_SERVER wins over settings.json.</summary>
    public Uri ServerUrl
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("NIGHTLOOP_SERVER");
            if (string.IsNullOrWhiteSpace(raw)) raw = Read()?["serverUrl"]?.GetValue<string>();
            return Normalize(raw) ?? new Uri(DefaultServerUrl);
        }
    }

    /// <summary>Last email typed on the sign-in screen, so it's filled in next time.</summary>
    public string? LastEmail
    {
        get => Read()?["lastEmail"]?.GetValue<string>();
        set => Write("lastEmail", value);
    }

    internal static Uri? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (!text.EndsWith('/')) text += "/";
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;
    }

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

    private void Write(string key, string? value)
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
