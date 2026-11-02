using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightLoop.LocalData;

/// <summary>
/// A game Night-loop can lock onto. Exe is null for "N/A" (no target lock). A few games ship as two programs
/// (Minecraft: Java and Bedrock); <paramref name="AltExe"/> is the second one. <paramref name="Hint"/> replaces the
/// usual "Matches the … process" line in Settings.
/// </summary>
public sealed record GameTarget(string Name, string? Exe, string? AltExe = null, string? Hint = null)
{
    /// <summary>Every program that counts as this game.</summary>
    [JsonIgnore]
    public IEnumerable<string> Exes => new[] { Exe, AltExe }.OfType<string>();

    /// <summary>The program to send input to: the first one that is running, else the main one.</summary>
    public string? Resolve(Func<string, bool> isRunning) => Exes.FirstOrDefault(isRunning) ?? Exe;
}

/// <summary>Built-in games plus the ones the user added in games.json.</summary>
public sealed class GamesStore(string path)
{
    public const int MaxUserGames = 50;

    public static readonly IReadOnlyList<GameTarget> BuiltIn =
    [
        new("N/A", null),
        new("Roblox", "RobloxPlayerBeta.exe"),
        new("Fortnite", "FortniteClient-Win64-Shipping.exe"),
        new("Minecraft", "javaw.exe", "Minecraft.Windows.exe", "Java: javaw.exe · Bedrock: Minecraft.Windows.exe"),
    ];

    /// <summary>Names older versions used, mapped to today's built-in game.</summary>
    public static string? CurrentName(string? name) =>
        name is "Minecraft (Java)" or "Minecraft (Bedrock)" ? "Minecraft" : name;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public GamesStore() : this(AppPaths.Games) { }

    /// <summary>Built-ins first, then the user's games. A missing or broken file just means no user games.</summary>
    public IReadOnlyList<GameTarget> Load()
    {
        var all = new List<GameTarget>(BuiltIn);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<GameTarget>>(File.ReadAllText(path), Json) is { } user)
                all.AddRange(user.Where(IsValid).Take(MaxUserGames));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Safe to ignore: defaults return.
        }
        return all;
    }

    public void SaveUserGames(IEnumerable<GameTarget> games)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(games.Where(IsValid).Take(MaxUserGames).ToList(), Json));
    }

    public static bool IsValid(GameTarget g) =>
        g.Name.Length is >= 1 and <= 40 && g.Exe is { Length: > 4 } exe && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
}
