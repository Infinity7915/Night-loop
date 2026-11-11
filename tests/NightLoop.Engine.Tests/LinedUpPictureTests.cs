using NightLoop.Engine.Blocks;
using NightLoop.Engine.Screen;
using Xunit;
using Xunit.Abstractions;

namespace NightLoop.Engine.Tests;

/// <summary>
/// Big pictures are shrunk when picked (phones). Their pixels start wherever the box started, so the matcher must line
/// the screen up with them: unlined, light text on a dark bar scored 0.86–0.95 on the very screen it was picked from,
/// below the 90% Confidence, and the macro waited forever.
/// </summary>
public class LinedUpPictureTests(ITestOutputHelper o)
{
    private const int W = 900, H = 700;

    /// <summary>A dark bar with light "letters" (thin strokes) on a light, slightly noisy screen.</summary>
    private static ScreenImage Screen(int barX, int barY, int barW, int barH)
    {
        var px = new byte[W * H * 4];
        var rnd = new Random(3);
        for (var i = 0; i < px.Length; i += 4)
        {
            var v = (byte)(232 + rnd.Next(6));
            px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
        }
        for (var y = 0; y < barH; y++)
        for (var x = 0; x < barW; x++)
        {
            // Letters: 2 px strokes every 9 px across and two bars down, like a line of text.
            var stroke = (x % 9 < 2 && y > barH / 5 && y < barH * 4 / 5) || (y % 23 < 2 && x % 31 < 20);
            var (r, g, b) = stroke ? (225, 230, 240) : (28, 30, 46);
            var i = ((barY + y) * W + barX + x) * 4;
            px[i] = (byte)b; px[i + 1] = (byte)g; px[i + 2] = (byte)r;
        }
        return new ScreenImage(0, 0, W, H, px);
    }

    private static ImageTemplate RoundTrip(ImageTemplate t)
    {
        Assert.True(ImageTemplate.TryDecode(t.Encode(), out var back));
        return back!;
    }

    /// <summary>What Night-loop for Android 0.8.5–0.8.6 saved: shrunk, with how much but not the exact box.</summary>
    private static ImageTemplate OldStyle(ScreenImage box)
    {
        var by = Math.Max((double)box.Width / ImageTemplate.MaxSide, (double)box.Height / ImageTemplate.MaxSide);
        var small = TemplateMatcher.Shrunk(box, by);
        return RoundTrip(ImageTemplate.FromImage(small, (double)box.Width / small.Width));
    }

    [Theory]
    [InlineData(141, 233, 290, 64)]
    [InlineData(142, 234, 290, 64)]
    [InlineData(143, 101, 437, 120)]
    [InlineData(57, 300, 610, 90)]
    public void A_big_picture_scores_100_on_the_screen_it_was_picked_from(int x, int y, int w, int h)
    {
        var screen = Screen(x, y, w, h);
        var box = screen.Crop(x, y, w, h);
        foreach (var (name, t) in new[] { ("new", RoundTrip(ImageTemplate.FromBox(box))), ("old", OldStyle(box)) })
        foreach (int? size in new int?[] { 0, null })
        {
            var m = TemplateMatcher.Find(screen, t, size, matchColor: true)!.Value;
            o.WriteLine($"{name} {w}x{h} at {x},{y} size {size?.ToString() ?? "auto"}: {m}");
            Assert.True(Vision.AlikeEnough(m.Score, 100), $"{name}: {m}");
            Assert.True(m.ColorOk, $"{name}: {m}");
            Assert.InRange(m.X, x + w / 2 - 1, x + w / 2 + 1);
            Assert.InRange(m.Y, y + h / 2 - 1, y + h / 2 + 1);
            Assert.InRange(t.Width * m.Scale, w - 1, w + 1);
        }
    }

    [Fact]
    public void A_big_picture_is_found_where_it_moved_and_not_where_it_isnt()
    {
        var picked = Screen(141, 233, 290, 64);
        var t = RoundTrip(ImageTemplate.FromBox(picked.Crop(141, 233, 290, 64)));
        var moved = Screen(377, 451, 290, 64);
        var m = TemplateMatcher.Find(moved, t, null, matchColor: true)!.Value;
        Assert.True(Vision.AlikeEnough(m.Score, 99) && m.ColorOk, m.ToString());
        Assert.InRange(m.X, 377 + 145 - 1, 377 + 145 + 1);
        Assert.InRange(m.Y, 451 + 32 - 1, 451 + 32 + 1);

        var empty = Screen(0, 0, 0, 0);
        var none = TemplateMatcher.Find(empty, t, null, matchColor: true);
        Assert.False(none is { } n && Vision.AlikeEnough(n.Score, 80) && n.ColorOk, none.ToString());
    }

    [Fact]
    public void The_box_size_is_saved_and_read_back()
    {
        var screen = Screen(10, 20, 437, 120);
        var t = RoundTrip(ImageTemplate.FromBox(screen.Crop(10, 20, 437, 120)));
        Assert.Equal(200, t.Width);
        Assert.Equal((437, 120), (t.BoxWidth, t.BoxHeight));
        Assert.Equal(437 / 200.0, t.CapturedScale, 3);

        // Small boxes aren't shrunk and need no size.
        var small = RoundTrip(ImageTemplate.FromBox(screen.Crop(10, 20, 150, 60)));
        Assert.Equal((150, 60, 0, 0, 1.0), (small.Width, small.Height, small.BoxWidth, small.BoxHeight, small.CapturedScale));
    }

    [Fact]
    public void A_box_too_big_to_shrink_is_refused()
    {
        var huge = new ScreenImage(0, 0, 4100, 10, new byte[4100 * 10 * 4]);
        Assert.Throws<ArgumentException>(() => ImageTemplate.FromBox(huge));
    }

    [Theory]
    [InlineData(1.0, 100, true)]
    [InlineData(0.996, 100, true)]
    [InlineData(0.994, 100, false)]
    [InlineData(0.895, 90, true)]
    [InlineData(0.894, 90, false)]
    public void Confidence_is_compared_in_whole_percents(double score, int pct, bool alike) =>
        Assert.Equal(alike, Vision.AlikeEnough(score, pct));
}
