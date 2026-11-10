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
