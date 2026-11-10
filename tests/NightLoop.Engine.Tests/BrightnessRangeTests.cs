using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Screen;
using Xunit;
using Xunit.Abstractions;

namespace NightLoop.Engine.Tests;

/// <summary>
/// Brightness range against textured pictures on a noisy background, at the captured size and found by automatic
/// size: a copy within the range is taken, one clearly outside it is not; the Dimmer or brighter switch takes them all.
/// </summary>
public class BrightnessRangeTests(ITestOutputHelper o)
{
    static void Sprite(byte[] px, int width, int ox, int oy, int size, (double R, double G, double B) tint, double f, Random? noise)
    {
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            double u = (x + 0.5) / size, v = (y + 0.5) / size;
            var g = Math.Exp(-((u - 0.4) * (u - 0.4) + (v - 0.45) * (v - 0.45)) * 12) * 180 + (Math.Sin(u * 20) * Math.Cos(v * 17) + 1) * 30 + 15;
            var i = ((oy + y) * width + ox + x) * 4;
            double n() => noise is null ? 0 : noise.Next(-6, 7);
            px[i] = (byte)Math.Clamp(g * tint.B * f + n(), 0, 255);
            px[i + 1] = (byte)Math.Clamp(g * tint.G * f + n(), 0, 255);
            px[i + 2] = (byte)Math.Clamp(g * tint.R * f + n(), 0, 255);
            px[i + 3] = 255;
        }
    }

    [Fact]
    public void Picks_the_copy_as_bright_as_captured_over_a_dimmer_one()
    {
        var tint = (1.3, 0.35, 0.3);
        var shot = new byte[40 * 40 * 4];
        Sprite(shot, 40, 0, 0, 40, tint, 1, null);
        ImageTemplate.TryDecode(ImageTemplate.FromImage(new ScreenImage(0, 0, 40, 40, shot)).Encode(), out var t);
        var px = new byte[400 * 200 * 4];
        for (var i = 3; i < px.Length; i += 4) px[i] = 255;
        Sprite(px, 400, 40, 60, 40, tint, 0.7, new Random(1));  // dim copy on the left
        Sprite(px, 400, 300, 60, 40, tint, 1.0, new Random(2)); // the real one on the right
        var m = TemplateMatcher.Find(new ScreenImage(0, 0, 400, 200, px), t!, sizeTolerancePct: null, matchColor: true)!.Value;
        Assert.True(m.ColorOk);
        Assert.InRange(m.X, 310, 330);
    }

    [Fact]
    public void Copies_within_the_range_match_and_others_dont()
    {
        var tints = new (double, double, double)[] { (1.3, 0.35, 0.3), (0.3, 0.4, 1.3), (0.6, 0.6, 0.6), (1.0, 0.9, 0.3) };
        var factors = new[] { 0.5, 0.6, 0.7, 0.8, 0.85, 0.9, 0.95, 1.0, 1.05, 1.1, 1.15, 1.2, 1.3, 1.5 };
        var ranges = new int?[] { 0, 3, 10, 20, 30, 50, null };
        var wrong = 0;
        foreach (var size in new[] { 40, 52 })
        foreach (var tint in tints)
        {
            var shot = new byte[40 * 40 * 4];
            Sprite(shot, 40, 0, 0, 40, tint, 1, null);
            ImageTemplate.TryDecode(ImageTemplate.FromImage(new ScreenImage(0, 0, 40, 40, shot)).Encode(), out var t);
            foreach (var r in ranges)
            {
                var line = $"size {size} tint {tint} range {r,2}: ";
                foreach (var f in factors)
                {
                    var px = new byte[300 * 200 * 4];
                    var rnd = new Random(7);
                    for (var i = 0; i < px.Length; i += 4) { px[i] = (byte)rnd.Next(40, 90); px[i + 1] = (byte)rnd.Next(60, 110); px[i + 2] = (byte)rnd.Next(30, 80); px[i + 3] = 255; }
                    Sprite(px, 300, 120, 70, size, tint, f, new Random(3));
                    var m = TemplateMatcher.Find(new ScreenImage(0, 0, 300, 200, px), t!, size == 40 ? 0 : null, matchColor: true, brightnessPct: r);
                    var rr = (r ?? 60) / 100.0;
                    var took = m is { } mm && mm.ColorOk && mm.Score >= 0.9;
                    var should = Math.Abs(f - 1) <= rr;
                    var shouldNot = r is not null && Math.Abs(f - 1) > rr + 0.08;
                    var bad = (took && shouldNot) || (!took && should && Math.Abs(f - 1) <= rr - 0.01);
                    if (bad) wrong++;
                    line += $"{f}:{(took ? "Y" : "-")}{(bad ? "!" : "")} ";
                }
                o.WriteLine(line);
            }
        }
        Assert.Equal(0, wrong);
    }
}

/// <summary>Phones shrink a picture bigger than 200 px to fit; it must still be found at its real size.</summary>
public class ShrunkPictureTests
{
    static byte[] Scene(int w, int h, int ox, int oy, int size)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = 245; px[i + 1] = 245; px[i + 2] = 245; px[i + 3] = 255; }
        for (var y = 0; y < size / 4; y++)
        for (var x = 0; x < size; x++)
        {
            var i = ((oy + y) * w + ox + x) * 4;
            var edge = y < 3 || y >= size / 4 - 3 || x < 3 || x >= size - 3;
            var icon = Math.Pow(x - size / 10.0, 2) + Math.Pow(y - size / 8.0, 2) < Math.Pow(size / 25.0, 2);
            var v = (byte)(edge || icon ? 90 : 225 - x * 60 / size);
            px[i] = v; px[i + 1] = (byte)(v + 5); px[i + 2] = v; px[i + 3] = 255;
        }
        return px;
    }

    static ScreenImage Shrink(ScreenImage image, out double scale)
    {
        scale = Math.Max((double)image.Width / ImageTemplate.MaxSide, (double)image.Height / ImageTemplate.MaxSide);
        int w = (int)(image.Width / scale), h = (int)(image.Height / scale);
        // Like the phone app: each pixel the average of the ones it covers.
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            int x0 = x * image.Width / w, x1 = Math.Max(x0 + 1, (x + 1) * image.Width / w);
            int y0 = y * image.Height / h, y1 = Math.Max(y0 + 1, (y + 1) * image.Height / h);
            for (var c = 0; c < 4; c++)
            {
                long sum = 0;
                for (var sy = y0; sy < y1; sy++) for (var sx = x0; sx < x1; sx++) sum += image.Bgra[(sy * image.Width + sx) * 4 + c];
                px[(y * w + x) * 4 + c] = (byte)(sum / ((x1 - x0) * (y1 - y0)));
            }
        }
        return new ScreenImage(0, 0, w, h, px);
    }

    [Fact]
    public void A_shrunk_picture_is_found_at_its_real_size_and_not_on_a_plain_screen()
    {
        const int W = 1080, H = 900, Size = 800;
        var screen = new ScreenImage(0, 0, W, H, Scene(W, H, 140, 300, Size));
        var crop = new byte[Size * (Size / 4) * 4];
        for (var y = 0; y < Size / 4; y++) Array.Copy(screen.Bgra, ((300 + y) * W + 140) * 4, crop, y * Size * 4, Size * 4);
        var small = Shrink(new ScreenImage(0, 0, Size, Size / 4, crop), out var scale);
        Assert.True(ImageTemplate.TryDecode(ImageTemplate.FromImage(small, scale).Encode(), out var t));
        Assert.InRange(t!.CapturedScale, scale - 0.01, scale + 0.01);

        var m = TemplateMatcher.Find(screen, t, sizeTolerancePct: 0, matchColor: true)!.Value;
        Assert.True(m.Score >= 0.9 && m.ColorOk, m.ToString());
        Assert.InRange(m.X, 140 + Size / 2 - 8, 140 + Size / 2 + 8);
        Assert.InRange(m.Y, 300 + Size / 8 - 8, 300 + Size / 8 + 8);
        Assert.InRange(t.Width * m.Scale, Size - 10, Size + 10);

        var plain = new byte[W * H * 4];
        for (var i = 0; i < plain.Length; i++) plain[i] = (byte)(i % 4 == 3 ? 255 : 245);
        var none = TemplateMatcher.Find(new ScreenImage(0, 0, W, H, plain), t, sizeTolerancePct: 0, matchColor: true);
        Assert.False(none is { } n && n.Score >= 0.9 && n.ColorOk);
    }

    [Fact]
    public void Pictures_without_a_size_note_read_as_not_shrunk()
    {
        var px = new byte[20 * 20 * 4];
        for (var i = 0; i < px.Length; i++) px[i] = (byte)(i * 7);
        Assert.True(ImageTemplate.TryDecode(ImageTemplate.FromImage(new ScreenImage(0, 0, 20, 20, px)).Encode(), out var t));
        Assert.Equal(1, t!.CapturedScale);
    }
}

public class PlainPictureTests
{
    private static ImageTemplate Make(Func<int, int, (byte R, byte G, byte B)> color, int w = 60, int h = 30)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (r, g, b) = color(x, y);
            var i = (y * w + x) * 4;
            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
        }
        Assert.True(ImageTemplate.TryDecode(ImageTemplate.FromImage(new ScreenImage(0, 0, w, h, px)).Encode(), out var t));
        return t!;
    }

    [Fact]
    public void A_white_picture_is_plain() => Assert.True(Make((_, _) => (250, 250, 250)).IsPlain);

    [Fact]
    public void A_white_picture_with_a_little_screen_noise_is_plain() =>
        Assert.True(Make((x, y) => ((byte)(248 + (x * 7 + y * 3) % 4), 250, 250)).IsPlain);

    [Fact]
    public void A_picture_with_a_small_icon_is_not_plain() =>
        Assert.False(Make((x, y) => x is > 24 and < 36 && y is > 8 and < 22 ? ((byte)40, (byte)40, (byte)40) : ((byte)250, (byte)250, (byte)250)).IsPlain);
}
