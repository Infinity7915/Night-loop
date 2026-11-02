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
/// Finds a template in a screen capture with normalized cross-correlation: a quick pass on shrunk copies picks a
/// few candidates, then each is checked at full size. Scores run 0–1 (1 = identical); brightness changes barely matter.
/// </summary>
public static class TemplateMatcher
{
    private const int CoarseTarget = 12;
    private const int MaxFactor = 8;
    private const int Candidates = 6;

    public readonly record struct Match(int X, int Y, double Score);

    /// <summary>Best match as the screen point at the middle of the found area, or null when the template doesn't fit.</summary>
    public static Match? Find(ScreenImage screen, ImageTemplate template)
    {
        if (template.Width > screen.Width || template.Height > screen.Height) return null;
        var hay = ImageTemplate.ToGray(screen);
        var factor = Math.Clamp(Math.Min(template.Width, template.Height) / CoarseTarget, 1, MaxFactor);

        var small = Shrink(hay, screen.Width, screen.Height, factor, out var sw, out var sh);
        var tSmall = Shrink(template.Gray, template.Width, template.Height, factor, out var tw, out var th);
        var coarse = Scan(small, sw, sh, tSmall, tw, th, Candidates);

        Match? best = null;
        var reach = factor * 2;
        foreach (var (cx, cy, _) in coarse)
        {
            var x0 = Math.Max(0, cx * factor - reach);
            var y0 = Math.Max(0, cy * factor - reach);
            var x1 = Math.Min(screen.Width - template.Width, cx * factor + reach);
            var y1 = Math.Min(screen.Height - template.Height, cy * factor + reach);
            for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var score = Ncc(hay, screen.Width, x, y, template.Gray, template.Width, template.Height);
                if (best is null || score > best.Value.Score) best = new Match(x, y, score);
            }
        }
        if (best is not { } b) return null;
        return new Match(screen.Left + b.X + template.Width / 2, screen.Top + b.Y + template.Height / 2, Math.Max(0, b.Score));
    }

    private static List<(int X, int Y, double Score)> Scan(byte[] hay, int w, int h, byte[] t, int tw, int th, int keep)
    {
        var results = new List<(int X, int Y, double Score)>();
        if (tw > w || th > h) return results;
        for (var y = 0; y <= h - th; y++)
        for (var x = 0; x <= w - tw; x++)
        {
            var score = Ncc(hay, w, x, y, t, tw, th);
            if (results.Count < keep * 4 || score > results[^1].Score)
            {
                results.Add((x, y, score));
                results.Sort((a, b) => b.Score.CompareTo(a.Score));
                if (results.Count > keep * 4) results.RemoveAt(results.Count - 1);
            }
        }
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

    private static double Ncc(byte[] hay, int hayWidth, int ox, int oy, byte[] t, int tw, int th)
    {
        double sumH = 0, sumT = 0, sumHH = 0, sumTT = 0, sumHT = 0;
        var n = tw * th;
        for (var y = 0; y < th; y++)
        {
            var row = (oy + y) * hayWidth + ox;
            var trow = y * tw;
            for (var x = 0; x < tw; x++)
            {
                double a = hay[row + x], b = t[trow + x];
                sumH += a;
                sumT += b;
                sumHH += a * a;
                sumTT += b * b;
                sumHT += a * b;
            }
        }
        var varH = sumHH - sumH * sumH / n;
        var varT = sumTT - sumT * sumT / n;
        if (varT < 1e-6 || varH < 1e-6)
        {
            // A flat template (or flat screen area): compare average brightness instead.
            var diff = Math.Abs(sumH / n - sumT / n);
            return varT < 1e-6 && varH < 1e-6 ? 1 - diff / 255.0 : 0;
        }
        return (sumHT - sumH * sumT / n) / Math.Sqrt(varH * varT);
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
}
