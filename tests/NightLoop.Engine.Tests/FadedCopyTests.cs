using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Runtime;
using NightLoop.Engine.Screen;

namespace NightLoop.Engine.Tests;

/// <summary>
/// A grayed-out copy of a colored picture (a card you can't afford, a button that is switched off) has the same shape
/// and the same light and dark, so it scores 100% alike. Match colors has to tell them apart by the color that is gone.
/// </summary>
public class FadedCopyTests
{
    // Two directions of color that leave a pixel exactly as light as it was (for the gray the matcher compares
    // shapes in), so a colored picture and its gray copy have the very same shape.
    private static readonly (double R, double G, double B) Warm = (0.8897, -0.4567, 0);
    private static readonly (double R, double G, double B) Cool = (0.0774, 0.1508, -0.9855);

    /// <summary>
    /// A made-up card: light and dark detail all over, in sixteen patches of different colors. <paramref name="color"/>
    /// is how strong the colors are (0 = a gray picture). The colors even out over the picture, so its gray copy is
    /// exactly as bright as it is in red, green and blue: brightness can't tell the two apart.
    /// </summary>
    private static (byte R, byte G, byte B) Card(double u, double v, double color, double light = 128)
    {
        var level = light + 42 * Math.Sin(u * 9 + 1) * Math.Cos(v * 7) + 13 * Math.Sin(u * 31) * Math.Sin(v * 29);
        var patch = (int)Math.Min(3, u * 4) + 4 * (int)Math.Min(3, v * 4);
        var hue = 2 * Math.PI * (patch * 5 % 16) / 16;
        double a = Math.Cos(hue) * color, b = Math.Sin(hue) * color;
        return (Byte(level + a * Warm.R + b * Cool.R), Byte(level + a * Warm.G + b * Cool.G), Byte(level + a * Warm.B + b * Cool.B));
    }

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    /// <summary>The gray the matcher sees a pixel as.</summary>
    private static int Gray(byte r, byte g, byte b) => (b * 29 + g * 150 + r * 77) >> 8;

    private static ScreenImage Screen(int width, int height)
    {
        var px = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            // A dull, busy game background.
            var g = (byte)(70 + 30 * Math.Sin(x * 0.05) * Math.Sin(y * 0.07));
            var i = (y * width + x) * 4;
            px[i] = (byte)(g - 8);
            px[i + 1] = (byte)(g + 14);
            px[i + 2] = g;
            px[i + 3] = 255;
        }
        return new ScreenImage(0, 0, width, height, px);
    }

    /// <summary>
    /// Draws the card. <paramref name="gray"/> is how far its colors are drained toward gray (1 = fully grayed out),
    /// <paramref name="dim"/> how bright it is (1 = as it was).
    /// </summary>
    private static void Draw(ScreenImage on, int ox, int oy, int w, int h, double color, double gray = 0, double dim = 1, double light = 128,
        (double R, double G, double B)? tint = null)
    {
        var px = on.Bgra;
        var (tr, tg, tb) = tint ?? (1, 1, 1);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (r, g, b) = Card((x + 0.5) / w, (y + 0.5) / h, color, light);
            var level = Gray(r, g, b);
            var i = ((oy + y) * on.Width + ox + x) * 4;
            px[i] = Byte((b + (level - b) * gray) * dim * tb);
            px[i + 1] = Byte((g + (level - g) * gray) * dim * tg);
            px[i + 2] = Byte((r + (level - r) * gray) * dim * tr);
        }
    }

    private static ImageTemplate Picked(int w, int h, double color, double gray = 0, double light = 128)
    {
        var shot = new ScreenImage(0, 0, w, h, new byte[w * h * 4]);
        Draw(shot, 0, 0, w, h, color, gray, light: light);
        Assert.True(ImageTemplate.TryDecode(ImageTemplate.FromBox(shot).Encode(), out var saved));
        return saved!;
    }

    private static bool Taken(TemplateMatcher.Match? m) => m is { } v && v.ColorOk && v.Score >= 0.9;

    public static TheoryData<int?> BothWays => new() { Brightness.DefaultPct, null };

    [Theory]
    [MemberData(nameof(BothWays))]
    public void A_grayed_out_copy_is_not_the_colored_picture(int? brightnessPct)
    {
        var card = Picked(64, 64, color: 40);

        var colored = Screen(320, 220);
        Draw(colored, 130, 70, 64, 64, color: 40);
        Assert.True(Taken(TemplateMatcher.Find(colored, card, 0, matchColor: true, brightnessPct)));

        var grayed = Screen(320, 220);
        Draw(grayed, 130, 70, 64, 64, color: 40, gray: 1);
        var m = TemplateMatcher.Find(grayed, card, 0, matchColor: true, brightnessPct)!.Value;
        // The very same shape (it used to be taken at 100%), but not the picture's colors.
        Assert.True(m.Score > 0.99, $"score {m.Score:0.000}");
        Assert.False(m.ColorOk);
        // Automatic size too.
        Assert.False(Taken(TemplateMatcher.Find(grayed, card, null, matchColor: true, brightnessPct)));
        // With Match colors off only the shape counts, as before.
        Assert.True(Taken(TemplateMatcher.Find(grayed, card, 0, matchColor: false, brightnessPct)));
    }

    [Theory]
    [MemberData(nameof(BothWays))]
    public void Grayed_out_and_dimmed_is_not_the_colored_picture(int? brightnessPct)
    {
        var card = Picked(64, 64, color: 40);
        var screen = Screen(320, 220);
        Draw(screen, 130, 70, 64, 64, color: 40, gray: 0.85, dim: 0.75);
        Assert.False(Taken(TemplateMatcher.Find(screen, card, 0, matchColor: true, brightnessPct)));
    }

    [Theory]
    [MemberData(nameof(BothWays))]
    public void The_colored_one_is_picked_next_to_its_grayed_out_copies(int? brightnessPct)
    {
        var card = Picked(64, 64, color: 40);
        var screen = Screen(420, 220);
        Draw(screen, 30, 70, 64, 64, color: 40, gray: 1);
        Draw(screen, 290, 90, 64, 64, color: 40);
        Draw(screen, 160, 40, 64, 64, color: 40, gray: 1);
        var m = TemplateMatcher.Find(screen, card, 0, matchColor: true, brightnessPct)!.Value;
        Assert.True(Taken(m));
        Assert.InRange(m.X, 290 + 32 - 1, 290 + 32 + 1);
        Assert.InRange(m.Y, 90 + 32 - 1, 90 + 32 + 1);
    }

    [Fact]
    public void A_little_washed_out_still_matches_and_mostly_gray_does_not()
    {
        var card = Picked(64, 64, color: 40);
        bool Takes(double gray, int? brightnessPct)
        {
            var screen = Screen(320, 220);
            Draw(screen, 130, 70, 64, 64, color: 40, gray: gray);
            return Taken(TemplateMatcher.Find(screen, card, 0, matchColor: true, brightnessPct));
        }
        Assert.True(Takes(0.15, Brightness.DefaultPct));
        Assert.False(Takes(0.6, Brightness.DefaultPct));
        // Average colors allows for lighting, which washes colors out more.
        Assert.True(Takes(0.3, null));
        Assert.False(Takes(0.8, null));
        // So does a wide brightness range.
        Assert.True(Takes(0.4, 30));
        Assert.False(Takes(0.8, 30));
    }

    [Fact]
    public void The_gray_picture_is_not_its_colored_copy()
    {
        // Picked while it was grayed out (to wait for "can't afford it"): the colored one isn't it.
        var grayCard = Picked(64, 64, color: 40, gray: 1);
        var colored = Screen(320, 220);
        Draw(colored, 130, 70, 64, 64, color: 40);
        Assert.False(Taken(TemplateMatcher.Find(colored, grayCard, 0, matchColor: true)));
        var grayed = Screen(320, 220);
        Draw(grayed, 130, 70, 64, 64, color: 40, gray: 1);
        Assert.True(Taken(TemplateMatcher.Find(grayed, grayCard, 0, matchColor: true)));
    }

    [Theory]
    [MemberData(nameof(BothWays))]
    public void A_picture_with_hardly_any_color_is_left_alone(int? brightnessPct)
    {
        // White text, a gray icon: there is no color to lose, so its gray copy is the same thing.
        var faint = Picked(64, 64, color: 3);
        var screen = Screen(320, 220);
        Draw(screen, 130, 70, 64, 64, color: 3, gray: 1);
        Assert.True(Taken(TemplateMatcher.Find(screen, faint, 0, matchColor: true, brightnessPct)));
    }

    [Fact]
    public void A_dimmer_copy_keeps_its_colors_but_a_dimmer_gray_one_has_none()
    {
        var card = Picked(64, 64, color: 40);
        var dim = Screen(320, 220);
        Draw(dim, 130, 70, 64, 64, color: 40, dim: 0.6);
        Assert.True(Taken(TemplateMatcher.Find(dim, card, 0, matchColor: true, brightnessPct: null)));
        var dimGray = Screen(320, 220);
        Draw(dimGray, 130, 70, 64, 64, color: 40, gray: 1, dim: 0.6);
        Assert.False(Taken(TemplateMatcher.Find(dimGray, card, 0, matchColor: true, brightnessPct: null)));
    }

    [Fact]
    public void A_slight_tint_all_over_within_the_brightness_range_still_matches()
    {
        // A nearly white picture under slightly warm light: every channel is within the range, so it is only a tint,
        // not color the picture doesn't have.
        var white = Picked(64, 64, color: 0, light: 190);
        var screen = Screen(320, 220);
        Draw(screen, 130, 70, 64, 64, color: 0, light: 190, tint: (1, 0.95, 0.95));
        Assert.True(Taken(TemplateMatcher.Find(screen, white, 0, matchColor: true)));
    }

    [Theory]
    [InlineData(45)] // farther away: 70% of its size
    [InlineData(83)] // closer: 130%
    public void Found_smaller_or_bigger_it_still_has_its_colors(int shown)
    {
        var card = Picked(64, 64, color: 40);
        var colored = Screen(360, 260);
        Draw(colored, 140, 80, shown, shown, color: 40);
        var m = TemplateMatcher.Find(colored, card, null, matchColor: true)!.Value;
        Assert.True(Taken(m), $"{m}");
        Assert.InRange(m.X, 140 + shown / 2 - 3, 140 + shown / 2 + 3);

        var grayed = Screen(360, 260);
        Draw(grayed, 140, 80, shown, shown, color: 40, gray: 1);
        Assert.False(Taken(TemplateMatcher.Find(grayed, card, null, matchColor: true)));
    }

    [Theory]
    [MemberData(nameof(BothWays))]
    public void A_big_picture_shrunk_to_fit_refuses_its_grayed_out_copy_too(int? brightnessPct)
    {
        // Bigger than 200 px (most buttons and cards on a phone): saved shrunk, found on a shrunk screen first.
        var card = Picked(300, 240, color: 40);
        Assert.True(card.CapturedScale > 1.4);

        var colored = Screen(760, 520);
        Draw(colored, 233, 141, 300, 240, color: 40);
        var m = TemplateMatcher.Find(colored, card, 0, matchColor: true, brightnessPct)!.Value;
        Assert.True(Taken(m), $"{m}");
        Assert.InRange(m.X, 233 + 150 - 2, 233 + 150 + 2);

        var grayed = Screen(760, 520);
        Draw(grayed, 233, 141, 300, 240, color: 40, gray: 1);
        var g = TemplateMatcher.Find(grayed, card, 0, matchColor: true, brightnessPct)!.Value;
        Assert.True(g.Score > 0.97, $"score {g.Score:0.000}");
        Assert.False(g.ColorOk);

        // Both on screen: the colored one, not whichever comes first.
        var both = Screen(760, 560);
        Draw(both, 21, 37, 300, 240, color: 40, gray: 1);
        Draw(both, 431, 283, 300, 240, color: 40);
        var pick = TemplateMatcher.Find(both, card, 0, matchColor: true, brightnessPct)!.Value;
        Assert.True(Taken(pick), $"{pick}");
        Assert.InRange(pick.X, 431 + 150 - 2, 431 + 150 + 2);
        Assert.InRange(pick.Y, 283 + 120 - 2, 283 + 120 + 2);
    }

    [Fact]
    public void How_much_color_is_gone_is_measured_as_a_share()
    {
        static byte[] Thumb(double color, double gray)
        {
            var shot = new ScreenImage(0, 0, 64, 64, new byte[64 * 64 * 4]);
            Draw(shot, 0, 0, 64, 64, color, gray);
            return ImageTemplate.ColorThumb(shot, 0, 0, 64, 64, 16, 16);
        }
        var same = TemplateMatcher.FadeOf(Thumb(40, 0), Thumb(40, 0), 0.06);
        Assert.InRange(same.LostShare, 0, 0.02);
        Assert.InRange(same.GainedShare, 0, 0.02);
        var half = TemplateMatcher.FadeOf(Thumb(40, 0), Thumb(40, 0.5), 0.06);
        Assert.InRange(half.LostShare, 0.45, 0.55);
        Assert.InRange(half.GainedShare, 0, 0.03);
        var gone = TemplateMatcher.FadeOf(Thumb(40, 0), Thumb(40, 1), 0.06);
        Assert.InRange(gone.LostShare, 0.97, 1);
        Assert.True(gone.LostLevels > 25, $"lost {gone.LostLevels:0.0} levels");
        // The other way round, all of the screen's color is color the gray picture doesn't have.
        var appeared = TemplateMatcher.FadeOf(Thumb(40, 1), Thumb(40, 0), 0.06);
        Assert.InRange(appeared.GainedShare, 0.97, 1);
        // Average colors: a dimmer copy has lost none of its color.
        var dimmer = TemplateMatcher.FadeOf(Thumb(40, 0), Thumb(40, 0).Select(b => (byte)(b * 0.5)).ToArray(), null);
        Assert.InRange(dimmer.LostShare, 0, 0.05);
    }

    [Fact]
    public async Task Test_look_says_when_only_a_grayed_out_copy_is_there()
    {
        var card = Picked(64, 64, color: 40);
        var grayed = Screen(320, 220);
        Draw(grayed, 130, 70, 64, 64, color: 40, gray: 1);
        var env = new RunEnvironment { Input = new FakeInput(), Delay = new FakeDelay(), Screen = new FixedScreen(grayed) };
        var block = Block.Create("b1", BlockTypes.WaitForImage, new() { ["imageData"] = card.Encode(), ["confidencePct"] = 100, ["sizeTolerancePct"] = 0 });

        var look = await Blocks.DetectProbe.LookOnceAsync(block, env, null);
        Assert.False(look.Found);
        Assert.Contains("100% alike", look.Message);
        Assert.Contains("grayed-out", look.Message);

        block.Params["matchColor"] = false;
        Assert.True((await Blocks.DetectProbe.LookOnceAsync(block, env, null)).Found);
    }

    [Fact]
    public async Task Wait_for_picture_taps_the_colored_card_and_not_the_grayed_out_one()
    {
        var card = Picked(64, 64, color: 40);
        async Task<List<string>> PlayAsync(double gray)
        {
            var screen = Screen(320, 220);
            Draw(screen, 130, 70, 64, 64, color: 40, gray: gray);
            var touch = new FakeTouch();
            var env = new RunEnvironment { Input = new FakeInput(), Delay = new FakeDelay(), Screen = new FixedScreen(screen), Touch = touch };
            var doc = new MacroBuilder().Add(BlockTypes.WaitForImage, new()
            {
                ["imageData"] = card.Encode(), ["confidencePct"] = 100, ["sizeTolerancePct"] = 0, ["timeoutMs"] = 600, ["whenFound"] = "tap",
            }).Build();
            doc.Platform = MacroPlatforms.Android;
            Assert.Empty(MacroValidator.Validate(doc));
            var run = new MacroRun(Compiler.MacroCompiler.Compile(doc), env);
            run.Start();
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(run.State == RunState.Stopped, run.FaultMessage);
            return touch.Log;
        }
        Assert.Equal(["tap 162,102 50"], await PlayAsync(gray: 0));
        Assert.Empty(await PlayAsync(gray: 1));
    }

    [Theory]
    [InlineData(BlockTypes.SwipeScan)]
    [InlineData(BlockTypes.WalkTo)]
    public async Task Test_look_covers_the_phone_steps_that_look_for_a_picture(string type)
    {
        var card = Picked(64, 64, color: 40);
        var screen = Screen(320, 220);
        Draw(screen, 130, 70, 64, 64, color: 40);
        var env = new RunEnvironment { Input = new FakeInput(), Delay = new FakeDelay(), Screen = new FixedScreen(screen) };
        var block = Block.Create("b1", type, new() { ["target"] = "image", ["imageData"] = card.Encode(), ["sizeTolerancePct"] = 0 });
        Assert.True(Blocks.DetectProbe.CanTest(block));
        var look = await Blocks.DetectProbe.LookOnceAsync(block, env, null);
        Assert.True(look.Found, look.Message);
        Assert.Equal((130 + 32, 70 + 32), look.Point);
    }

    /// <summary>A screen that always shows the same picture.</summary>
    private sealed class FixedScreen(ScreenImage image) : IScreenCapture
    {
        public (int Left, int Top, int Width, int Height) Desktop => (0, 0, image.Width, image.Height);
        public ScreenImage Capture(int left, int top, int width, int height) => image.Crop(left, top, width, height);
    }
}
