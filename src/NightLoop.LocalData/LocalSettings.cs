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

    /// <summary>Color theme id ("night", "rose", "ocean"...), or null for the default. Unknown ids fall back to it.</summary>
    public string? Palette
    {
        get => ReadString("palette") is { Length: > 0 and <= 32 } p && p.All(c => c is >= 'a' and <= 'z') ? p : null;
        set => Write("palette", value);
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

    /// <summary>Settings › Macros › Wait after clicks (ms): the default for clicking blocks without their own. 0 unless set.</summary>
    public int WaitAfterClicksMs
    {
        get => ReadMs("waitAfterClicksMs");
        set => WriteNode("waitAfterClicksMs", Math.Max(0, value));
    }

    /// <summary>Settings › Macros › Wait after every block (ms). 0 unless set.</summary>
    public int WaitAfterBlocksMs
    {
        get => ReadMs("waitAfterBlocksMs");
        set => WriteNode("waitAfterBlocksMs", Math.Max(0, value));
    }

    private int ReadMs(string key) => Read()?[key] is JsonValue v && v.TryGetValue(out int ms) && ms > 0 ? ms : 0;

    /// <summary>Keep Night-loop above every other window (Settings › Appearance). Off unless turned on.</summary>
    public bool AlwaysOnTop
    {
        get => Read()?["alwaysOnTop"] is JsonValue v && v.TryGetValue(out bool on) && on;
        set => WriteNode("alwaysOnTop", value);
    }

    /// <summary>
    /// Settings › Macros: with many blocks, the block editor slides down beside the timeline as the page scrolls,
    /// so it stays in sight. On unless turned off.
    /// </summary>
    public bool EditorFollowsScroll
    {
        get => Read()?["editorFollowsScroll"] is not JsonValue v || !v.TryGetValue(out bool on) || on;
        set => WriteNode("editorFollowsScroll", value);
    }

    /// <summary>
    /// Settings › Macros: while a macro plays, the page scrolls so the block being played sits in the middle of the
    /// screen. On unless turned off.
    /// </summary>
    public bool FollowPlayingBlock
    {
        get => Read()?["followPlayingBlock"] is not JsonValue v || !v.TryGetValue(out bool on) || on;
        set => WriteNode("followPlayingBlock", value);
    }

    /// <summary>Settings › Appearance: holiday icons around Christmas, Halloween and other holidays. Off unless turned on.</summary>
    public bool SeasonalThemes
    {
        get => Read()?["seasonalThemes"] is JsonValue v && v.TryGetValue(out bool on) && on;
        set => WriteNode("seasonalThemes", value);
    }

    /// <summary>
    /// Settings › Macros: the on-screen pickers (click position, color, area, picture) leave the screen moving and
    /// draw a crosshair, the position and the outline on top, instead of freezing a screenshot. Off unless turned on.
    /// </summary>
    public bool LivePicking
    {
        get => Read()?["livePicking"] is JsonValue v && v.TryGetValue(out bool on) && on;
        set => WriteNode("livePicking", value);
    }

    /// <summary>
    /// Settings › Appearance: closing the window hides Night-loop to the notification area (system tray) so
    /// hotkeys and macros keep working. Right-click the tray icon › Exit quits. On unless turned off.
    /// </summary>
    public bool CloseToTray
    {
        get => Read()?["closeToTray"] is not JsonValue v || !v.TryGetValue(out bool on) || on;
        set => WriteNode("closeToTray", value);
    }

    /// <summary>
    /// Settings › Appearance: reopen the window where it was, at the size it was, when Night-loop was last closed.
    /// On unless turned off.
    /// </summary>
    public bool RememberWindow
    {
        get => Read()?["rememberWindow"] is not JsonValue v || !v.TryGetValue(out bool on) || on;
        set => WriteNode("rememberWindow", value);
    }

    /// <summary>Where the window was when Night-loop was last closed (device-independent pixels), or null.</summary>
    public WindowBounds? WindowBounds
    {
        get => Read()?["window"] is JsonObject o
               && o["left"] is JsonValue l && l.TryGetValue(out double left)
               && o["top"] is JsonValue t && t.TryGetValue(out double top)
               && o["width"] is JsonValue w && w.TryGetValue(out double width)
               && o["height"] is JsonValue h && h.TryGetValue(out double height)
            ? new WindowBounds(left, top, width, height, o["maximized"] is JsonValue m && m.TryGetValue(out bool max) && max)
            : null;
        set => WriteNode("window", value is not { } b ? null : new JsonObject
        {
            ["left"] = Math.Round(b.Left, 1),
            ["top"] = Math.Round(b.Top, 1),
            ["width"] = Math.Round(b.Width, 1),
            ["height"] = Math.Round(b.Height, 1),
            ["maximized"] = b.Maximized,
        });
    }

    /// <summary>
    /// Settings › Macros › Editor shortcuts: keys picked for undo, copy, paste and so on, by action name, like
    /// { "undo": "Ctrl+Z" }. Actions left out use their default.
    /// </summary>
    public IReadOnlyDictionary<string, string> EditorShortcuts
    {
        get => Read()?["editorShortcuts"] is JsonObject o
            ? o.Where(p => p.Value is JsonValue v && v.TryGetValue(out string? _))
               .ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        set
        {
            var o = new JsonObject();
            foreach (var (k, v) in value) o[k] = v;
            WriteNode("editorShortcuts", value.Count == 0 ? null : o);
        }
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

/// <summary>A saved window position and size (device-independent pixels; the size is the normal, unmaximized one).</summary>
public sealed record WindowBounds(double Left, double Top, double Width, double Height, bool Maximized)
{
    /// <summary>
    /// Whether the window can go back there: a sane size, and enough of its title bar on the screens that exist now
    /// to grab it (a monitor may have been unplugged since).
    /// </summary>
    public bool FitsOn(double screenLeft, double screenTop, double screenWidth, double screenHeight, double minWidth, double minHeight)
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top) || !double.IsFinite(Width) || !double.IsFinite(Height)) return false;
        if (Width < minWidth || Height < minHeight || screenWidth <= 0 || screenHeight <= 0) return false;
        if (Width > screenWidth * 1.5 || Height > screenHeight * 1.5) return false;
        const double grab = 80; // pixels of title bar that must be on screen
        var visibleX = Math.Min(Left + Width, screenLeft + screenWidth) - Math.Max(Left, screenLeft);
        var titleOnScreen = Top >= screenTop - 4 && Top + 30 <= screenTop + screenHeight;
        return visibleX >= grab && titleOnScreen;
    }
}
