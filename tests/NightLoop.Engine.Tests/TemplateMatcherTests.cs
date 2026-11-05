using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Screen;

namespace NightLoop.Engine.Tests;

public class TemplateMatcherTests
{
    /// <summary>A made-up game sprite: soft blobs and stripes, drawn at any size so the same thing can be near or far.</summary>
    private static byte Sprite(double u, double v)
    {
        var a = Math.Exp(-((u - 0.3) * (u - 0.3) + (v - 0.35) * (v - 0.35)) * 30) * 200;
        var b = Math.Exp(-((u - 0.72) * (u - 0.72) + (v - 0.6) * (v - 0.6)) * 18) * 150;
        var c = (Math.Sin(u * 26) * Math.Cos(v * 22) + 1) * 45;
        return (byte)Math.Clamp(a + b + c + 10, 0, 255);
    }

    private static ScreenImage Draw(int width, int height, (int X, int Y, int Size)? sprite)
    {
        var px = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            // A busy background so a wrong spot can't score well by luck.
            var g = (byte)(60 + 40 * Math.Sin(x * 0.05) * Math.Sin(y * 0.07));
            if (sprite is { } s && x >= s.X && y >= s.Y && x < s.X + s.Size && y < s.Y + s.Size)
                g = Sprite((x - s.X + 0.5) / s.Size, (y - s.Y + 0.5) / s.Size);
            var i = (y * width + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = g;
            px[i + 3] = 255;
        }
        return new ScreenImage(0, 0, width, height, px);
    }

    private static ImageTemplate Captured(int size) => ImageTemplate.FromImage(Draw(size, size, (0, 0, size)));

    [Fact]
    public void Finds_the_captured_size_without_any_tolerance()
    {
        var m = TemplateMatcher.Find(Draw(400, 300, (210, 120, 48)), Captured(48));
        Assert.NotNull(m);
        Assert.True(m.Value.Score > 0.97, $"score {m.Value.Score}");
        Assert.InRange(m.Value.X, 210 + 24 - 1, 210 + 24 + 1);
        Assert.InRange(m.Value.Y, 120 + 24 - 1, 120 + 24 + 1);
    }

    [Theory]
    [InlineData(36)] // farther away: 75% of the captured size
    [InlineData(41)]
    [InlineData(58)] // closer: 120%
    public void Finds_it_smaller_or_bigger_with_a_size_tolerance(int shown)
    {
        var screen = Draw(400, 300, (150, 90, shown));
        var exact = TemplateMatcher.Find(screen, Captured(48))!.Value;
        var loose = TemplateMatcher.Find(screen, Captured(48), sizeTolerancePct: 30)!.Value;

        Assert.True(exact.Score < 0.9, $"without tolerance it shouldn't pass 90% (got {exact.Score:0.000})");
        Assert.True(loose.Score >= 0.9, $"with tolerance it should pass 90% (got {loose.Score:0.000})");
        Assert.InRange(loose.X, 150 + shown / 2 - 3, 150 + shown / 2 + 3);
        Assert.InRange(loose.Y, 90 + shown / 2 - 3, 90 + shown / 2 + 3);
        Assert.InRange(loose.Scale, shown / 48.0 - 0.05, shown / 48.0 + 0.05);
    }

    [Theory]
    [InlineData(20)] // far away: about 40% of the captured size
    [InlineData(31)]
    [InlineData(72)] // close up: 150%
    public void Automatic_size_finds_it_at_any_size(int shown)
    {
        var m = TemplateMatcher.Find(Draw(400, 300, (150, 90, shown)), Captured(48), sizeTolerancePct: null)!.Value;
        Assert.True(m.Score >= 0.9, $"score {m.Score:0.000} at scale {m.Scale:0.00}");
        Assert.InRange(m.X, 150 + shown / 2 - 3, 150 + shown / 2 + 3);
        Assert.InRange(m.Y, 90 + shown / 2 - 3, 90 + shown / 2 + 3);
    }

    [Fact]
    public void Blank_or_old_default_size_tolerance_means_automatic()
    {
        Assert.Null(Blocks.DetectBlocksLook.SizeTolerance(null));
        Assert.Null(Blocks.DetectBlocksLook.SizeTolerance(25));
        Assert.Equal(0, Blocks.DetectBlocksLook.SizeTolerance(0));
        Assert.Equal(10, Blocks.DetectBlocksLook.SizeTolerance(10));
    }

    [Fact]
    public void Not_there_stays_a_miss_with_a_size_tolerance()
    {
        var m = TemplateMatcher.Find(Draw(400, 300, null), Captured(48), sizeTolerancePct: null);
        Assert.True(m is null || m.Value.Score < 0.9, $"score {m?.Score}");
    }

    [Fact]
    public void Validator_checks_size_tolerance()
    {
        static MacroDoc With(int pct) => new MacroBuilder().Add(BlockTypes.WaitForImage, new() { ["sizeTolerancePct"] = pct }).Build();
        Assert.Empty(MacroValidator.Validate(With(0)));
        Assert.Empty(MacroValidator.Validate(With(MacroLimits.SizeToleranceMaxPct)));
        Assert.NotEmpty(MacroValidator.Validate(With(MacroLimits.SizeToleranceMaxPct + 1)));
    }
}

public class ColorMatchTests
{
    /// <summary>The same sprite shape drawn in a tint: same brightness pattern, different color.</summary>
    private static void DrawSprite(byte[] px, int width, int ox, int oy, int size, (double R, double G, double B) tint)
    {
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            double u = (x + 0.5) / size, v = (y + 0.5) / size;
            var g = Math.Exp(-((u - 0.4) * (u - 0.4) + (v - 0.45) * (v - 0.45)) * 12) * 180 + (Math.Sin(u * 20) * Math.Cos(v * 17) + 1) * 30 + 15;
            var i = ((oy + y) * width + ox + x) * 4;
            px[i] = (byte)Math.Clamp(g * tint.B, 0, 255);
            px[i + 1] = (byte)Math.Clamp(g * tint.G, 0, 255);
            px[i + 2] = (byte)Math.Clamp(g * tint.R, 0, 255);
            px[i + 3] = 255;
        }
    }

    private static readonly (double, double, double) Red = (1.3, 0.35, 0.3);
    private static readonly (double, double, double) Blue = (0.3, 0.4, 1.3);

    [Fact]
    public void Match_colors_skips_a_same_shaped_thing_in_another_color()
    {
        var shot = new byte[40 * 40 * 4];
        DrawSprite(shot, 40, 0, 0, 40, Red);
        var red = ImageTemplate.FromImage(new ScreenImage(0, 0, 40, 40, shot));
        Assert.True(ImageTemplate.TryDecode(red.Encode(), out var decoded));
        Assert.True(decoded!.HasColor);

        // A blue one on the left and the red one further right.
        var px = new byte[400 * 200 * 4];
        DrawSprite(px, 400, 40, 60, 40, Blue);
        DrawSprite(px, 400, 260, 80, 40, Red);
        var screen = new ScreenImage(0, 0, 400, 200, px);

        var m = TemplateMatcher.Find(screen, decoded, sizeTolerancePct: 0, matchColor: true)!.Value;
        Assert.True(m.ColorOk);
        Assert.InRange(m.X, 278, 282);
        Assert.InRange(m.Y, 98, 102);

        // Only the blue one on screen: shape found, colors don't match.
        var onlyBlue = new byte[400 * 200 * 4];
        DrawSprite(onlyBlue, 400, 40, 60, 40, Blue);
        var miss = TemplateMatcher.Find(new ScreenImage(0, 0, 400, 200, onlyBlue), decoded, sizeTolerancePct: 0, matchColor: true)!.Value;
        Assert.False(miss.ColorOk);
        var shapeOnly = TemplateMatcher.Find(new ScreenImage(0, 0, 400, 200, onlyBlue), decoded, sizeTolerancePct: 0, matchColor: false)!.Value;
        Assert.True(shapeOnly.ColorOk);
        Assert.InRange(shapeOnly.X, 58, 62);
    }

    [Fact]
    public void Colors_still_match_when_the_game_is_darker()
    {
        var shot = new byte[40 * 40 * 4];
        DrawSprite(shot, 40, 0, 0, 40, Red);
        var red = ImageTemplate.FromImage(new ScreenImage(0, 0, 40, 40, shot));
        var px = new byte[200 * 200 * 4];
        DrawSprite(px, 200, 80, 80, 40, (Red.Item1 * 0.75, Red.Item2 * 0.75, Red.Item3 * 0.75));
        var m = TemplateMatcher.Find(new ScreenImage(0, 0, 200, 200, px), red, sizeTolerancePct: 0, matchColor: true)!.Value;
        Assert.True(m.ColorOk);
    }

    [Fact]
    public void Old_gray_pictures_still_decode_and_skip_the_color_check()
    {
        // An nl1 picture (no colors) made by hand.
        using var raw = new MemoryStream();
        using (var z = new System.IO.Compression.DeflateStream(raw, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write([0, 2, 0, 2, 10, 200, 200, 10]);
        }
        Assert.True(ImageTemplate.TryDecode("nl1:" + Convert.ToBase64String(raw.ToArray()), out var t));
        Assert.False(t!.HasColor);
    }
}
