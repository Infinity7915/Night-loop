using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NightLoop.LocalData;

namespace NightLoop.Client;

/// <summary>What stays on disk between launches: the session token and the per-account "trust this PC" tokens.</summary>
public sealed class StoredSession
{
    public string? Token { get; set; }
    /// <summary>Trust tokens by lower-case email, so a trusted PC skips the code even after signing out.</summary>
    public Dictionary<string, string> TrustTokens { get; set; } = [];
}

public interface ISessionStore
{
    StoredSession Load();
    void Save(StoredSession session);
}

/// <summary>session.bin, encrypted with Windows DPAPI for the current user (schema section 8).</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSessionStore(string? path = null) : ISessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Night-loop session v1");
    private readonly string _path = path ?? AppPaths.Session;

    public StoredSession Load()
    {
        try
        {
            if (!File.Exists(_path)) return new StoredSession();
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredSession>(plain) ?? new StoredSession();
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable (another Windows user, corrupted): start signed out.
            return new StoredSession();
        }
    }

    public void Save(StoredSession session)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path, cipher);
    }
}

/// <summary>For tests and non-Windows runs: nothing is written to disk.</summary>
public sealed class MemorySessionStore : ISessionStore
{
    private string _json = "{}";

    public StoredSession Load() => JsonSerializer.Deserialize<StoredSession>(_json) ?? new StoredSession();

    public void Save(StoredSession session) => _json = JsonSerializer.Serialize(session);
}
