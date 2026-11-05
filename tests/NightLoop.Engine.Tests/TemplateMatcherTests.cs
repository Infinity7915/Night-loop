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
