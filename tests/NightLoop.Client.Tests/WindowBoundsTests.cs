using NightLoop.LocalData;

namespace NightLoop.Client.Tests;

public sealed class WindowBoundsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nl-win-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Remember_window_is_on_by_default_and_bounds_round_trip()
    {
        var settings = new LocalSettings(Path.Combine(_dir, "settings.json"));
        Assert.True(settings.RememberWindow);
        Assert.Null(settings.WindowBounds);

        settings.WindowBounds = new WindowBounds(100, 50, 1100, 700, Maximized: true);
        var again = new LocalSettings(Path.Combine(_dir, "settings.json"));
        Assert.Equal(new WindowBounds(100, 50, 1100, 700, true), again.WindowBounds);

        again.RememberWindow = false;
        again.WindowBounds = null;
        Assert.False(new LocalSettings(Path.Combine(_dir, "settings.json")).RememberWindow);
        Assert.Null(new LocalSettings(Path.Combine(_dir, "settings.json")).WindowBounds);
    }

    [Theory]
    [InlineData(100, 50, 1100, 700, true)]       // on the screen
    [InlineData(1800, 50, 1100, 700, true)]      // mostly off to the right, title bar still grabbable
    [InlineData(1900, 50, 1100, 700, false)]     // only 20 px left on screen
    [InlineData(2500, 50, 1100, 700, false)]     // on a monitor that's gone
    [InlineData(100, -400, 1100, 700, false)]    // title bar above the screen
    [InlineData(100, 50, 200, 150, false)]       // smaller than the window allows
    public void Saved_spot_is_used_only_when_it_fits_the_screens(double left, double top, double width, double height, bool fits) =>
        Assert.Equal(fits, new WindowBounds(left, top, width, height, false).FitsOn(0, 0, 1920, 1040, 560, 400));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
