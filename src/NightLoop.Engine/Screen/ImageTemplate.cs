using NightLoop.Contracts.MacroDocument;
using System.IO.Compression;

namespace NightLoop.Engine.Screen;

/// <summary>
/// A small grayscale picture saved inside an image block ("imageData"), so the macro carries everything it needs
/// and stays on the server with the rest of the macro. Text format: "nl1:" + base64(deflate(width, height, gray bytes)).
/// </summary>
public sealed class ImageTemplate
{
    private const string Prefix = "nl1:";
    /// <summary>Same, plus a small color copy for "Match colors": … gray bytes, thumb width, thumb height, RGB bytes.</summary>
    private const string ColorPrefix = "nl2:";
    /// <summary>The color copy is at most this many pixels a side: enough to tell a red coin from a blue one.</summary>
    public const int ColorSide = 16;
    /// <summary>Largest side the app saves. Bigger captures are refused with a hint to pick a smaller area.</summary>
    public const int MaxSide = 200;

    private ImageTemplate(int width, int height, byte[] gray, int colorWidth = 0, int colorHeight = 0, byte[]? rgb = null)
    {
        Width = width;
        Height = height;
        Gray = gray;
        ColorWidth = colorWidth;
        ColorHeight = colorHeight;
        Rgb = rgb;
    }

    /// <summary>A small color copy (RGB bytes, top row first), or null for pictures captured before 0.6.2.</summary>
    public byte[]? Rgb { get; }
    public int ColorWidth { get; }
    public int ColorHeight { get; }
    public bool HasColor => Rgb is not null;

    public int Width { get; }
    public int Height { get; }
    /// <summary>One byte per pixel, top row first.</summary>
    public byte[] Gray { get; }

    public static ImageTemplate FromImage(ScreenImage image)
    {
        if (image.Width > MaxSide || image.Height > MaxSide)
            throw new ArgumentException($"Pictures can be at most {MaxSide}×{MaxSide} px.", nameof(image));
        var cw = Math.Min(ColorSide, image.Width);
        var ch = Math.Min(ColorSide, image.Height);
        return new ImageTemplate(image.Width, image.Height, ToGray(image), cw, ch, ColorThumb(image, 0, 0, image.Width, image.Height, cw, ch));
    }

    /// <summary>Area-averaged RGB of part of an image, shrunk to <paramref name="cw"/>×<paramref name="ch"/>.</summary>
    internal static byte[] ColorThumb(ScreenImage image, int x0, int y0, int w, int h, int cw, int ch)
    {
        var rgb = new byte[cw * ch * 3];
        var px = image.Bgra;
        for (var cy = 0; cy < ch; cy++)
        for (var cx = 0; cx < cw; cx++)
        {
            int ax = x0 + cx * w / cw, bx = Math.Max(ax + 1, x0 + (cx + 1) * w / cw);
            int ay = y0 + cy * h / ch, by = Math.Max(ay + 1, y0 + (cy + 1) * h / ch);
            long r = 0, g = 0, b = 0, n = 0;
            for (var y = ay; y < by && y < image.Height; y++)
            for (var x = ax; x < bx && x < image.Width; x++)
            {
                var i = (y * image.Width + x) * 4;
                b += px[i];
                g += px[i + 1];
                r += px[i + 2];
                n++;
            }
            if (n == 0) n = 1;
            var o = (cy * cw + cx) * 3;
            rgb[o] = (byte)(r / n);
            rgb[o + 1] = (byte)(g / n);
            rgb[o + 2] = (byte)(b / n);
        }
        return rgb;
    }

    public string Encode()
    {
        using var raw = new MemoryStream();
        using (var z = new DeflateStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            z.WriteByte((byte)(Width >> 8));
            z.WriteByte((byte)Width);
            z.WriteByte((byte)(Height >> 8));
            z.WriteByte((byte)Height);
            z.Write(Gray);
            if (Rgb is not null)
            {
                z.WriteByte((byte)ColorWidth);
                z.WriteByte((byte)ColorHeight);
                z.Write(Rgb);
            }
        }
        return (Rgb is null ? Prefix : ColorPrefix) + Convert.ToBase64String(raw.ToArray());
    }

    public static bool TryDecode(string? text, out ImageTemplate? template)
    {
        template = null;
        if (text is null) return false;
        var color = text.StartsWith(ColorPrefix, StringComparison.Ordinal);
        if (!color && !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            var bytes = Convert.FromBase64String(text[Prefix.Length..]);
            using var z = new DeflateStream(new MemoryStream(bytes), CompressionMode.Decompress);
            Span<byte> head = stackalloc byte[4];
            z.ReadExactly(head);
            var w = (head[0] << 8) | head[1];
            var h = (head[2] << 8) | head[3];
            if (w is < 1 or > MaxSide || h is < 1 or > MaxSide) return false;
            var gray = new byte[w * h];
            z.ReadExactly(gray);
            if (!color)
            {
                template = new ImageTemplate(w, h, gray);
                return true;
            }
            Span<byte> size = stackalloc byte[2];
            z.ReadExactly(size);
            if (size[0] is < 1 or > ColorSide || size[1] is < 1 or > ColorSide) return false;
            var rgb = new byte[size[0] * size[1] * 3];
            z.ReadExactly(rgb);
            template = new ImageTemplate(w, h, gray, size[0], size[1], rgb);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or EndOfStreamException)
        {
            return false;
        }
    }

    internal static byte[] ToGray(ScreenImage image)
    {
        var gray = new byte[image.Width * image.Height];
        var px = image.Bgra;
        for (int i = 0, j = 0; i < gray.Length; i++, j += 4)
            gray[i] = (byte)((px[j] * 29 + px[j + 1] * 150 + px[j + 2] * 77) >> 8);
        return gray;
    }
}

/// <summary>
/// Finds a template in a screen capture with normalized cross-correlation. Scores run 0–1 (1 = identical);
/// brightness changes barely matter.
/// <para>
/// With a size tolerance the template is also tried smaller and bigger (a thing farther away in a game looks
/// smaller than when it was captured). For each size, a quick pass on shrunk copies picks a few candidates, and each
/// is followed down to full size. Sums over screen areas come from integral images, so each check only multiplies.
/// </para>
/// </summary>
public static class TemplateMatcher
{
    /// <summary>Most the size tolerance goes: 60% finds the picture from 40% to 160% of its captured size.</summary>
    public const int MaxSizeTolerancePct = NightLoop.Contracts.MacroDocument.MacroLimits.SizeToleranceMaxPct;
    private const int CoarseTarget = 8;
    private const int MaxFactor = 8;
    private const int Candidates = 6;
    /// <summary>Smaller than this, a picture has too little detail to tell apart from anything else.</summary>
    private const int MinSide = 8;
    /// <summary>
    /// A picture shrunk to look for it farther away must keep at least this much (or its captured size, if that is
    /// smaller): a few pixels of a shrunk picture look like lots of things, and scanning many sizes finds one.
    /// </summary>
    private const int MinScaledSide = 12;

    /// <param name="Scale">The size it was found at, 1 = as captured.</param>
    /// <param name="ColorOk">False when colors were checked and no well-shaped spot had the picture's colors.</param>
    public readonly record struct Match(int X, int Y, double Score, double Scale = 1, bool ColorOk = true);

    /// <summary>How far colors may be (0–255, per channel, averaged over the picture) for "Match colors".</summary>
    public const int ColorLimit = 45;


    /// <summary>Automatic size: from this much of the captured size (far away)…</summary>
    public const double AutoMinScale = 0.3;
    /// <summary>…up to this much (close up).</summary>
    public const double AutoMaxScale = 1.6;

    /// <summary>
    /// Best match as the screen point at the middle of the found area, or null when the template doesn't fit.
    /// <paramref name="sizeTolerancePct"/>: how much smaller or bigger it may look (0 = only the captured size);
    /// null looks at every size from <see cref="AutoMinScale"/> to <see cref="AutoMaxScale"/>.
    /// </summary>
    public static Match? Find(ScreenImage screen, ImageTemplate template, int? sizeTolerancePct = 0, bool matchColor = false, int brightnessPct = Brightness.DefaultPct)
    {
        var (min, max) = sizeTolerancePct is { } pct
            ? (1 - Math.Clamp(pct, 0, MaxSizeTolerancePct) / 100.0, 1 + Math.Clamp(pct, 0, MaxSizeTolerancePct) / 100.0)
            : (AutoMinScale, AutoMaxScale);
        return Find(screen, template, min, max, matchColor, brightnessPct);
    }

    /// <summary>Best match at any size between <paramref name="minScale"/> and <paramref name="maxScale"/> of the captured one.</summary>
    /// With <paramref name="matchColor"/> (and a picture that has its colors saved), the best spot whose colors match
    /// too: two things with the same shape but different colors aren't mixed up. <paramref name="brightnessPct"/> is how
    /// much dimmer or brighter than captured a copy may be (lighting in games); 0 = exactly as captured.
    public static Match? Find(ScreenImage screen, ImageTemplate template, double minScale, double maxScale, bool matchColor = false, int brightnessPct = Brightness.DefaultPct)
    {
        var hay = new Haystack(ImageTemplate.ToGray(screen), screen.Width, screen.Height);
        var scales = Scales(template, minScale, maxScale, out var step);

        var results = new List<Found>[scales.Count];
        void Try(int i) => results[i] = FindAtScale(hay, template, scales[i]);
        if (scales.Count == 1) Try(0);
        else Parallel.For(0, scales.Count, Try);

        var all = results.SelectMany(r => r).OrderByDescending(f => f.Score).ToList();
        if (all.Count == 0) return null;
        var b = all[0];
        var checkColor = matchColor && template.HasColor;
        var range = Math.Clamp(brightnessPct, Brightness.MinPct, Brightness.MaxPct) / 100.0;
        var colorOk = true;
        if (checkColor)
        {
            // The best-shaped spot with the right colors; a same-shaped thing in another color is skipped.
            var pick = all.FirstOrDefault(f => ColorDistance(screen, template, f, range) <= ColorLimit);
            if (pick.W > 0) b = pick;
            else colorOk = false;
        }

        // Between two tried sizes: try halfway either side, near where it was found, and keep the better one.
        if (scales.Count > 1)
        {
            foreach (var d in new[] { -step / 2, step / 2, -step / 4, step / 4 })
            {
                var s = b.Scale + d;
                if (s < minScale - 1e-9 || s > maxScale + 1e-9) continue;
                if (Polish(hay, template, b, s) is { } p && p.Score > b.Score
                    && (!checkColor || !colorOk || ColorDistance(screen, template, p, range) <= ColorLimit)) b = p;
            }
        }
        return new Match(screen.Left + b.X + b.W / 2, screen.Top + b.Y + b.H / 2, Math.Max(0, b.Score), b.Scale, colorOk);
    }

    /// <summary>
    /// How different the colors are at a found spot: both are shrunk to a small grid and the per-channel difference is
    /// averaged. A spot up to <paramref name="range"/> (a share, 0.1 = 10%) dimmer or brighter than the picture is first
    /// evened out to the picture's brightness (lighting changes in games); a spot further off doesn't match at all.
    /// </summary>
    internal static double ColorDistance(ScreenImage screen, ImageTemplate t, (int X, int Y, int W, int H) at, double range)
    {
        if (t.Rgb is not { } want) return 0;
        var gw = Math.Min(t.ColorWidth, Math.Max(1, at.W / 2));
        var gh = Math.Min(t.ColorHeight, Math.Max(1, at.H / 2));
        var mine = gw == t.ColorWidth && gh == t.ColorHeight ? want : ResizeRgb(want, t.ColorWidth, t.ColorHeight, gw, gh);
        var seen = ImageTemplate.ColorThumb(screen, at.X, at.Y, at.W, at.H, gw, gh);
        double sumWant = 0, sumSeen = 0;
        for (var i = 0; i < mine.Length; i++) { sumWant += mine[i]; sumSeen += seen[i]; }
        // The spot must be within the brightness range of the picture. The per-cell difference alone let a copy 30%
        // dimmer through, since a dim copy keeps the same hues.
        if (BrightnessOff(mine, seen, range)) return 255;
        var gain = sumSeen < 1 ? 1 : Math.Clamp(sumWant / sumSeen, 1 / (1 + range), 1 / Math.Max(0.01, 1 - range));
        double total = 0;
        for (var i = 0; i < mine.Length; i += 3)
        {
            var d = 0.0;
            for (var c = 0; c < 3; c++) d = Math.Max(d, Math.Abs(mine[i + c] - Math.Min(255, seen[i + c] * gain)));
            total += d;
        }
        return total / (mine.Length / 3);
    }

    /// <summary>
    /// Whether any color channel is more than <paramref name="range"/> dimmer or brighter on screen than in the
    /// picture. Per channel, because a bright channel that is already at full (255) can't get brighter, while the
    /// others still show it. Channels that are nearly black in the picture are skipped (no brightness to compare).
    /// </summary>
    private static bool BrightnessOff(byte[] want, byte[] seen, double range)
    {
        for (var c = 0; c < 3; c++)
        {
            double w = 0, s = 0;
            for (var i = c; i < want.Length; i += 3) { w += want[i]; s += seen[i]; }
            var cells = want.Length / 3;
            if (w / cells < 12) continue;
            var ratio = s / w;
            if (ratio < 1 - range || ratio > 1 + range) return true;
        }
        return false;
    }

    private static double ColorDistance(ScreenImage screen, ImageTemplate t, Found f, double range) => ColorDistance(screen, t, (f.X, f.Y, f.W, f.H), range);

    private static byte[] ResizeRgb(byte[] rgb, int w, int h, int nw, int nh)
    {
        var dst = new byte[nw * nh * 3];
        for (var c = 0; c < 3; c++)
        {
            var plane = new byte[w * h];
            for (var i = 0; i < plane.Length; i++) plane[i] = rgb[i * 3 + c];
            var small = Resize(plane, w, h, nw, nh);
            for (var i = 0; i < small.Length; i++) dst[i * 3 + c] = small[i];
        }
        return dst;
    }

    /// <summary>Sizes to try: 1 first, then outwards in steps small enough that the edges are off by ~2 px at most.</summary>
    internal static List<double> Scales(ImageTemplate t, double minScale, double maxScale, out double step)
    {
        step = Math.Clamp(2.5 / Math.Max(t.Width, t.Height), 0.04, 0.12);
        var list = new List<double> { 1 };
        minScale = Math.Min(1, minScale);
        maxScale = Math.Max(1, maxScale);
        for (var i = 1; 1 + i * step <= maxScale + 1e-9 || 1 - i * step >= minScale - 1e-9; i++)
        {
            if (1 + i * step <= maxScale + 1e-9) list.Add(1 + i * step);
            if (1 - i * step >= minScale - 1e-9) list.Add(1 - i * step);
        }
        return list;
    }

    private readonly record struct Found(int X, int Y, int W, int H, double Score, double Scale);

    /// <summary>The spots that look most like the picture at this size, each followed down to full size.</summary>
    private static List<Found> FindAtScale(Haystack hay, ImageTemplate template, double scale)
    {
        var found = new List<Found>();
        var w = (int)Math.Round(template.Width * scale);
        var h = (int)Math.Round(template.Height * scale);
        if (TooSmall(template, w, h) || w > hay.Width || h > hay.Height) return found;
        var full = Resize(template.Gray, template.Width, template.Height, w, h);
        var factor = Math.Clamp(Math.Min(w, h) / CoarseTarget, 1, MaxFactor);

        var coarse = hay.Level(factor);
        var tCoarse = new Needle(Shrink(full, w, h, factor, out var cw, out var ch), cw, ch);
        foreach (var (cx, cy, _) in Scan(coarse, tCoarse, Candidates))
        {
            // Follow the candidate down to full size, halving the shrink each time.
            int x = cx, y = cy, f = factor;
            while (f > 1)
            {
                var next = Math.Max(1, f / 2);
                var level = hay.Level(next);
                var t = next == 1 ? new Needle(full, w, h) : new Needle(Shrink(full, w, h, next, out var nw, out var nh), nw, nh);
                var reach = f / next + 1;
                (x, y, _) = BestNear(level, t, x * f / next, y * f / next, reach);
                f = next;
            }
            var (fx, fy, score) = BestNear(hay.Level(1), new Needle(full, w, h), x, y, 1);
            found.Add(new Found(fx, fy, w, h, score, scale));
        }
        return found;
    }

    private static bool TooSmall(ImageTemplate t, int w, int h) =>
        w < MinSide || h < MinSide
        || (w < t.Width && w < MinScaledSide) || (h < t.Height && h < MinScaledSide);

    /// <summary>Tries one more size around an earlier find, keeping its middle in place.</summary>
    private static Found? Polish(Haystack hay, ImageTemplate template, Found at, double scale)
    {
        var w = (int)Math.Round(template.Width * scale);
        var h = (int)Math.Round(template.Height * scale);
        if (TooSmall(template, w, h) || w > hay.Width || h > hay.Height) return null;
        var x = at.X + at.W / 2 - w / 2;
        var y = at.Y + at.H / 2 - h / 2;
        var (fx, fy, score) = BestNear(hay.Level(1), new Needle(Resize(template.Gray, template.Width, template.Height, w, h), w, h), x, y, 2);
        return new Found(fx, fy, w, h, score, scale);
    }

    private static (int X, int Y, double Score) BestNear(Plane hay, Needle t, int cx, int cy, int reach)
    {
        var x0 = Math.Clamp(cx - reach, 0, Math.Max(0, hay.Width - t.Width));
        var y0 = Math.Clamp(cy - reach, 0, Math.Max(0, hay.Height - t.Height));
        var x1 = Math.Clamp(cx + reach, 0, hay.Width - t.Width);
        var y1 = Math.Clamp(cy + reach, 0, hay.Height - t.Height);
        (int, int, double) best = (x0, y0, double.MinValue);
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var score = Ncc(hay, x, y, t);
            if (score > best.Item3) best = (x, y, score);
        }
        return best;
    }

    private static List<(int X, int Y, double Score)> Scan(Plane hay, Needle t, int keep)
    {
        var results = new List<(int X, int Y, double Score)>();
        if (t.Width > hay.Width || t.Height > hay.Height) return results;
        var room = keep * 4;
        var worst = double.MinValue;
        for (var y = 0; y <= hay.Height - t.Height; y++)
        for (var x = 0; x <= hay.Width - t.Width; x++)
        {
            var score = Ncc(hay, x, y, t);
            if (results.Count >= room && score <= worst) continue;
            results.Add((x, y, score));
            if (results.Count > room)
            {
                results.Sort((a, b) => b.Score.CompareTo(a.Score));
                results.RemoveRange(room, results.Count - room);
                worst = results[^1].Score;
            }
        }
        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        // Keep candidates that aren't right next to a better one.
        var picked = new List<(int X, int Y, double Score)>();
        foreach (var r in results)
        {
            if (picked.Any(p => Math.Abs(p.X - r.X) <= 1 && Math.Abs(p.Y - r.Y) <= 1)) continue;
            picked.Add(r);
            if (picked.Count == keep) break;
        }
        return picked;
    }

    private static double Ncc(Plane hay, int ox, int oy, Needle t)
    {
        var n = t.Count;
        var (sumH, sumHH) = hay.Sums(ox, oy, t.Width, t.Height);
        double sumHT = 0;
        var px = hay.Pixels;
        var tp = t.Pixels;
        for (var y = 0; y < t.Height; y++)
        {
            var row = (oy + y) * hay.Width + ox;
            var trow = y * t.Width;
            long acc = 0;
            for (var x = 0; x < t.Width; x++) acc += px[row + x] * tp[trow + x];
            sumHT += acc;
        }
        var varH = sumHH - sumH * sumH / n;
        if (t.Variance < 1e-6 || varH < 1e-6)
        {
            // A flat template (or flat screen area): compare average brightness instead.
            var diff = Math.Abs(sumH / n - t.Sum / n);
            return t.Variance < 1e-6 && varH < 1e-6 ? 1 - diff / 255.0 : 0;
        }
        var score = (sumHT - sumH * t.Sum / n) / Math.Sqrt(varH * t.Variance);
        // Correlation alone ignores contrast, so a faint smudge with the right outline (or a busy patch with the
        // right light and dark spots) can score high. The real thing has about the picture's contrast, give or take
        // lighting: far outside that, the score is lowered.
        var contrast = Math.Sqrt(varH / t.Variance);
        if (score > 0 && contrast < MinContrast) score *= contrast / MinContrast;
        else if (score > 0 && contrast > MaxContrast) score *= MaxContrast / contrast;
        return score;
    }

    /// <summary>A found spot may have this much less (or more) contrast than the picture before its score is lowered.</summary>
    private const double MinContrast = 0.4, MaxContrast = 2.5;

    private static byte[] Shrink(byte[] src, int w, int h, int f, out int nw, out int nh)
    {
        nw = Math.Max(1, w / f);
        nh = Math.Max(1, h / f);
        if (f == 1) return src;
        var dst = new byte[nw * nh];
        for (var y = 0; y < nh; y++)
        for (var x = 0; x < nw; x++)
        {
            var sum = 0;
            for (var dy = 0; dy < f; dy++)
            {
                var row = (y * f + dy) * w + x * f;
                for (var dx = 0; dx < f; dx++) sum += src[row + dx];
            }
            dst[y * nw + x] = (byte)(sum / (f * f));
        }
        return dst;
    }

    /// <summary>Resizes a grayscale picture: averages when shrinking (no lost detail), blends when growing.</summary>
    internal static byte[] Resize(byte[] src, int w, int h, int nw, int nh)
    {
        if (nw == w && nh == h) return src;
        var across = new float[nw * h];
        for (var y = 0; y < h; y++)
            Resample(i => src[y * w + i], w, nw, (i, v) => across[y * nw + i] = v);
        var dst = new byte[nw * nh];
        for (var x = 0; x < nw; x++)
            Resample(i => across[i * nw + x], h, nh, (i, v) => dst[i * nw + x] = (byte)Math.Clamp((int)Math.Round(v), 0, 255));
        return dst;
    }

    private static void Resample(Func<int, float> get, int n, int m, Action<int, float> set)
    {
        var ratio = n / (double)m;
        for (var i = 0; i < m; i++)
        {
            if (ratio > 1)
            {
                // Average the source pixels this one covers, counting part-covered ones by how much is covered.
                double from = i * ratio, to = (i + 1) * ratio, sum = 0;
                for (var k = (int)from; k < Math.Min(n, (int)Math.Ceiling(to)); k++)
                    sum += get(k) * (Math.Min(to, k + 1) - Math.Max(from, k));
                set(i, (float)(sum / ratio));
            }
            else
            {
                var at = Math.Clamp((i + 0.5) * ratio - 0.5, 0, n - 1);
                var k = (int)at;
                var frac = at - k;
                var b = k + 1 < n ? get(k + 1) : get(k);
                set(i, (float)(get(k) * (1 - frac) + b * frac));
            }
        }
    }

    private sealed class Needle
    {
        public Needle(byte[] pixels, int width, int height)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
            Count = width * height;
            double s = 0, ss = 0;
            for (var i = 0; i < Count; i++)
            {
                s += pixels[i];
                ss += pixels[i] * pixels[i];
            }
            Sum = s;
            Variance = ss - s * s / Count;
        }

        public byte[] Pixels { get; }
        public int Width { get; }
        public int Height { get; }
        public int Count { get; }
        public double Sum { get; }
        public double Variance { get; }
    }

    /// <summary>A grayscale screen (or a shrunk copy) with running sums for quick area totals.</summary>
    private sealed class Plane
    {
        private readonly long[] _sum;
        private readonly long[] _sq;

        public Plane(byte[] pixels, int width, int height)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
            var stride = width + 1;
            _sum = new long[stride * (height + 1)];
            _sq = new long[stride * (height + 1)];
            for (var y = 0; y < height; y++)
            {
                long row = 0, rowSq = 0;
                for (var x = 0; x < width; x++)
                {
                    long v = pixels[y * width + x];
                    row += v;
                    rowSq += v * v;
                    _sum[(y + 1) * stride + x + 1] = _sum[y * stride + x + 1] + row;
                    _sq[(y + 1) * stride + x + 1] = _sq[y * stride + x + 1] + rowSq;
                }
            }
        }

        public byte[] Pixels { get; }
        public int Width { get; }
        public int Height { get; }

        public (double Sum, double SumSq) Sums(int x, int y, int w, int h)
        {
            var s = Width + 1;
            int a = y * s + x, b = y * s + x + w, c = (y + h) * s + x, d = (y + h) * s + x + w;
            return (_sum[d] - _sum[b] - _sum[c] + _sum[a], _sq[d] - _sq[b] - _sq[c] + _sq[a]);
        }
    }

    /// <summary>The screen at full size plus shrunk copies, made when first needed and shared by every size tried.</summary>
    private sealed class Haystack(byte[] gray, int width, int height)
    {
        private readonly Lazy<Plane>[] _levels = Enumerable.Range(0, MaxFactor + 1)
            .Select(f => new Lazy<Plane>(() =>
            {
                var px = Shrink(gray, width, height, Math.Max(1, f), out var w, out var h);
                return new Plane(px, w, h);
            }, LazyThreadSafetyMode.ExecutionAndPublication))
            .ToArray();

        public int Width => width;
        public int Height => height;
        public Plane Level(int factor) => _levels[Math.Clamp(factor, 1, MaxFactor)].Value;
    }
}
