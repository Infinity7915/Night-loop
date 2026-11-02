using NightLoop.LocalData;

namespace NightLoop.Client.Tests;

public sealed class LocalFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nl-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Install_id_is_created_once_and_reused()
    {
        var path = Path.Combine(_dir, "install.id");
        var first = DeviceIdentity.LoadOrCreateInstallId(path);
        Assert.Equal(first, DeviceIdentity.LoadOrCreateInstallId(path));
        Assert.NotEqual(Guid.Empty, first);
    }

    [Fact]
    public void Fingerprint_is_hex_sha256_and_label_is_short()
    {
        var id = DeviceIdentity.ForThisPc(Path.Combine(_dir, "install.id"));
        Assert.Matches("^[0-9a-f]{64}$", id.Fingerprint);
        Assert.InRange(id.Label.Length, 1, 64);
    }

    [Fact]
    public void Server_url_comes_from_settings_and_gets_a_trailing_slash()
    {
        var path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """{ "theme": "dark", "serverUrl": "https://api.example.com/v1" }""");
        var settings = new LocalSettings(path);
        Assert.Equal("https://api.example.com/v1/", settings.ServerUrl.ToString());

        settings.LastEmail = "a@b.co";
        Assert.Contains("\"theme\"", File.ReadAllText(path)); // other keys survive
        Assert.Equal("a@b.co", new LocalSettings(path).LastEmail);
    }

    [Fact]
    public void Missing_settings_fall_back_to_the_default_server()
    {
        var settings = new LocalSettings(Path.Combine(_dir, "nope.json"));
        Assert.Equal(LocalSettings.DefaultServerUrl, settings.ServerUrl.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
