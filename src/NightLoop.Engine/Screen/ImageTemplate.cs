using System.IO.Compression;

namespace NightLoop.Engine.Screen;

/// <summary>
/// A small grayscale picture saved inside an image block ("imageData"), so the macro carries everything it needs
/// and stays on the server with the rest of the macro. Text format: "nl1:" + base64(deflate(width, height, gray bytes)).
/// </summary>
public sealed class ImageTemplate
{
    private const string Prefix = "nl1:";
    /// <summary>Largest side the app saves. Bigger captures are refused with a hint to pick a smaller area.</summary>
    public const int MaxSide = 200;

    private ImageTemplate(int width, int height, byte[] gray)
    {
        Width = width;
        Height = height;
        Gray = gray;
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>One byte per pixel, top row first.</summary>
    public byte[] Gray { get; }

    public static ImageTemplate FromImage(ScreenImage image)
    {
        if (image.Width > MaxSide || image.Height > MaxSide)
            throw new ArgumentException($"Pictures can be at most {MaxSide}×{MaxSide} px.", nameof(image));
        return new ImageTemplate(image.Width, image.Height, ToGray(image));
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
        }
        return Prefix + Convert.ToBase64String(raw.ToArray());
    }

    public static bool TryDecode(string? text, out ImageTemplate? template)
    {
        template = null;
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
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
            template = new ImageTemplate(w, h, gray);
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
    private const int Candidates = 5;
    private const int MinSide = 4;

    /// <param name="Scale">The size it was found at, 1 = as captured.</param>
    public readonly record struct Match(int X, int Y, double Score, double Scale = 1);

    /// <summary>
    /// Best match as the screen point at the middle of the found area, or null when the template doesn't fit.
    /// <paramref name="sizeTolerancePct"/>: how much smaller or bigger it may look (0 = only the captured size).
    /// </summary>
    public static Match? Find(ScreenImage screen, ImageTemplate template, int sizeTolerancePct = 0)
    {
        var hay = new Haystack(ImageTemplate.ToGray(screen), screen.Width, screen.Height);
        var scales = Scales(template, Math.Clamp(sizeTolerancePct, 0, MaxSizeTolerancePct), out var step);

        var results = new Found?[scales.Count];
        void Try(int i) => results[i] = FindAtScale(hay, template, scales[i]);
        if (scales.Count == 1) Try(0);
        else Parallel.For(0, scales.Count, Try);

        Found? best = null;
        foreach (var r in results)
            if (r is { } f && (best is null || f.Score > best.Value.Score)) best = f;
        if (best is not { } b) return null;

        // Between two tried sizes: try halfway either side, near where it was found, and keep the better one.
        if (scales.Count > 1)
        {
            foreach (var d in new[] { -step / 2, step / 2, -step / 4, step / 4 })
            {
                var s = b.Scale + d;
                if (s < 1 - sizeTolerancePct / 100.0 - 1e-9 || s > 1 + sizeTolerancePct / 100.0 + 1e-9) continue;
                if (Polish(hay, template, b, s) is { } p && p.Score > b.Score) b = p;
            }
        }
        return new Match(screen.Left + b.X + b.W / 2, screen.Top + b.Y + b.H / 2, Math.Max(0, b.Score), b.Scale);
    }

    /// <summary>Sizes to try: 1 first, then outwards in steps small enough that the edges are off by ~2 px at most.</summary>
    internal static List<double> Scales(ImageTemplate t, int tolerancePct, out double step)
    {
        step = Math.Clamp(2.5 / Math.Max(t.Width, t.Height), 0.04, 0.12);
        var list = new List<double> { 1 };
        var r = tolerancePct / 100.0;
        if (r <= 0) return list;
        var n = (int)Math.Ceiling(r / step);
        step = r / n;
        for (var i = 1; i <= n; i++)
        {
            list.Add(1 + i * step);
            list.Add(1 - i * step);
        }
        return list;
    }

    private readonly record struct Found(int X, int Y, int W, int H, double Score, double Scale);

    private static Found? FindAtScale(Haystack hay, ImageTemplate template, double scale)
    {
        var w = (int)Math.Round(template.Width * scale);
        var h = (int)Math.Round(template.Height * scale);
        if (w < MinSide || h < MinSide || w > hay.Width || h > hay.Height) return null;
        var full = Resize(template.Gray, template.Width, template.Height, w, h);
        var factor = Math.Clamp(Math.Min(w, h) / CoarseTarget, 1, MaxFactor);

        var coarse = hay.Level(factor);
        var tCoarse = new Needle(Shrink(full, w, h, factor, out var cw, out var ch), cw, ch);
        Found? best = null;
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
            if (best is null || score > best.Value.Score) best = new Found(fx, fy, w, h, score, scale);
        }
        return best;
    }

    /// <summary>Tries one more size around an earlier find, keeping its middle in place.</summary>
    private static Found? Polish(Haystack hay, ImageTemplate template, Found at, double scale)
    {
        var w = (int)Math.Round(template.Width * scale);
        var h = (int)Math.Round(template.Height * scale);
        if (w < MinSide || h < MinSide || w > hay.Width || h > hay.Height) return null;
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
        return (sumHT - sumH * t.Sum / n) / Math.Sqrt(varH * t.Variance);
    }

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
