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
    /// <summary>Largest side a saved picture has. A bigger box is shrunk to fit (see <see cref="FromBox"/>).</summary>
    public const int MaxSide = 200;
    /// <summary>The most a picture is shrunk to fit <see cref="MaxSide"/> (a 4000 px box).</summary>
    public const double MaxCapturedScale = 20;

    private ImageTemplate(int width, int height, byte[] gray, int colorWidth = 0, int colorHeight = 0, byte[]? rgb = null, double capturedScale = 1,
        int boxWidth = 0, int boxHeight = 0)
    {
        CapturedScale = capturedScale;
        BoxWidth = boxWidth;
        BoxHeight = boxHeight;
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

    /// <summary>
    /// How much bigger the picture is on screen than saved: a box bigger than <see cref="MaxSide"/> is shrunk to fit
    /// (on phones), and this says by how much, so it's looked for at its real size. 1 for everything else.
    /// </summary>
    public double CapturedScale { get; }

    /// <summary>
    /// For a shrunk picture: the exact size of the box it was picked from, in screen pixels (0 when not known: not
    /// shrunk, or saved before 0.10.12). The matcher uses it to line the screen's pixels up with the picture's.
    /// </summary>
    public int BoxWidth { get; }
    /// <inheritdoc cref="BoxWidth"/>
    public int BoxHeight { get; }

    public int Width { get; }
    public int Height { get; }
    /// <summary>One byte per pixel, top row first.</summary>
    public byte[] Gray { get; }

    /// <summary>
    /// The picture is (almost) one plain color, like part of a white loading screen. A plain picture matches any
    /// plain area of that color, so it can't tell screens apart; the apps refuse it.
    /// </summary>
    public bool IsPlain
    {
        get
        {
            if (Gray.Length == 0) return true;
            double sum = 0, sq = 0;
            foreach (var g in Gray) { sum += g; sq += g * g; }
            var mean = sum / Gray.Length;
            return sq / Gray.Length - mean * mean < PlainSpread * PlainSpread;
        }
    }

    /// <summary>Gray levels a picture's pixels must spread by (standard deviation) not to count as plain.</summary>
    public const double PlainSpread = 3;

    /// <param name="capturedScale">How much <paramref name="image"/> was shrunk from the screen to fit <see cref="MaxSide"/>.</param>
    public static ImageTemplate FromImage(ScreenImage image, double capturedScale = 1)
    {
        if (image.Width > MaxSide || image.Height > MaxSide)
            throw new ArgumentException($"Pictures can be at most {MaxSide}×{MaxSide} px.", nameof(image));
        var cw = Math.Min(ColorSide, image.Width);
        var ch = Math.Min(ColorSide, image.Height);
        return new ImageTemplate(image.Width, image.Height, ToGray(image), cw, ch, ColorThumb(image, 0, 0, image.Width, image.Height, cw, ch),
            Math.Clamp(capturedScale, 1, MaxCapturedScale));
    }

    /// <summary>
    /// The picture of a box picked on screen. A box bigger than <see cref="MaxSide"/> is shrunk to fit, the same way the
    /// matcher shrinks the screen, and remembers its exact size so it's looked for at the size it really is on screen.
    /// </summary>
    public static ImageTemplate FromBox(ScreenImage box)
    {
        if (box.Width <= MaxSide && box.Height <= MaxSide) return FromImage(box);
        var by = Math.Max((double)box.Width / MaxSide, (double)box.Height / MaxSide);
        if (by > MaxCapturedScale)
            throw new ArgumentException($"Pick a smaller area (at most {MaxSide * MaxCapturedScale:0} px a side).");
        var small = TemplateMatcher.Shrunk(box, by);
        var t = FromImage(small, (double)box.Width / small.Width);
        return new ImageTemplate(t.Width, t.Height, t.Gray, t.ColorWidth, t.ColorHeight, t.Rgb, t.CapturedScale, box.Width, box.Height);
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
                // Shrunk pictures add how much (in hundredths) at the end, then (0.10.12+) the exact box size. Older
                // apps stop reading after what they know.
                if (CapturedScale > 1.005 || BoxWidth > 0)
                {
                    var pct = (int)Math.Round(CapturedScale * 100);
                    z.WriteByte((byte)(pct >> 8));
                    z.WriteByte((byte)pct);
                    if (BoxWidth > 0)
                    {
                        z.WriteByte((byte)(BoxWidth >> 8));
                        z.WriteByte((byte)BoxWidth);
                        z.WriteByte((byte)(BoxHeight >> 8));
                        z.WriteByte((byte)BoxHeight);
                    }
                }
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
            Span<byte> scale = stackalloc byte[2];
            if (z.ReadAtLeast(scale, 2, throwOnEndOfStream: false) < 2)
            {
                template = new ImageTemplate(w, h, gray, size[0], size[1], rgb);
                return true;
            }
            var captured = Math.Clamp(((scale[0] << 8) | scale[1]) / 100.0, 1, MaxCapturedScale);
            Span<byte> box = stackalloc byte[4];
            int bw = 0, bh = 0;
            if (z.ReadAtLeast(box, 4, throwOnEndOfStream: false) == 4)
            {
                (bw, bh) = ((box[0] << 8) | box[1], (box[2] << 8) | box[3]);
                // Only a box the picture could have been shrunk from counts.
                if (bw < w || bh < h || bw > w * MaxCapturedScale + 1 || bh > h * MaxCapturedScale + 1) (bw, bh) = (0, 0);
                else captured = Math.Clamp((double)bw / w, 1, MaxCapturedScale);
            }
            template = new ImageTemplate(w, h, gray, size[0], size[1], rgb, captured, bw, bh);
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
    public static Match? Find(ScreenImage screen, ImageTemplate template, int? sizeTolerancePct = 0, bool matchColor = false, int? brightnessPct = Brightness.DefaultPct)
    {
        var (min, max) = sizeTolerancePct is { } pct
            ? (1 - Math.Clamp(pct, 0, MaxSizeTolerancePct) / 100.0, 1 + Math.Clamp(pct, 0, MaxSizeTolerancePct) / 100.0)
            : (AutoMinScale, AutoMaxScale);
        return Find(screen, template, min, max, matchColor, brightnessPct);
    }

    /// <summary>Best match at any size between <paramref name="minScale"/> and <paramref name="maxScale"/> of the captured one.</summary>
    /// With <paramref name="matchColor"/> (and a picture that has its colors saved), the best spot whose colors match
    /// too: two things with the same shape but different colors aren't mixed up. <paramref name="brightnessPct"/> is how
    /// much dimmer or brighter than captured a copy may be (lighting in games); null evens the brightness out instead.
    public static Match? Find(ScreenImage screen, ImageTemplate template, double minScale, double maxScale, bool matchColor = false, int? brightnessPct = Brightness.DefaultPct)
    {
        double? range = brightnessPct is { } pct ? Math.Clamp(pct, Brightness.MinPct, Brightness.MaxPct) / 100.0 : null;
        // A picture shrunk when it was saved: shrink the screen the same way, look, then line the find up on the real
        // screen. Its Scale is then the size on screen compared with the saved picture, as for any other picture.
        var shrink = template.CapturedScale;
        if (shrink > 1.005)
        {
            var small = Shrunk(screen, shrink);
            // Colors on the shrunk screen are only roughly right (its pixels don't line up with the picture's), so
            // they only choose which find to line up; the lined-up box is checked properly.
            return Best(small, template, minScale, maxScale, matchColor, range, LooseLevels) is { } coarse
                ? LineUp(screen, small, template, coarse, matchColor && template.HasColor, range)
                : null;
        }
        return Best(screen, template, minScale, maxScale, matchColor, range, NoiseLevels) is { } r ? ToMatch(screen, r.Box, r.ColorOk) : null;
    }

    private static Match ToMatch(ScreenImage screen, Found b, bool colorOk) =>
        new(screen.Left + b.X + b.W / 2, screen.Top + b.Y + b.H / 2, Math.Max(0, b.Score), b.Scale, colorOk);

    /// <summary>
    /// A shrunk picture's pixels each cover a few screen pixels, starting wherever its box started when it was
    /// picked. The shrunk screen's pixels start at the screen's corner instead, so they line up with the picture's only
    /// by chance, and text or fine detail then scores far lower than it should (0.86 for the same text on the same
    /// screen). This shrinks the screen again around the find, starting at each nearby pixel (and, without the box's
    /// exact size, at a pixel more or less wide and high), and keeps the best: the same thing then scores ~1.
    /// </summary>
    private static Match LineUp(ScreenImage screen, ScreenImage small, ImageTemplate t, (Found Box, bool ColorOk) coarse, bool checkColor, double? range)
    {
        var b = coarse.Box;
        double sx = (double)screen.Width / small.Width, sy = (double)screen.Height / small.Height;
        // The size it was picked at (exact when the picture knows its box), and the size the shrunk look found.
        var saved = t.BoxWidth > 0 ? (W: t.BoxWidth, H: t.BoxHeight)
            : (W: (int)Math.Round(t.Width * t.CapturedScale), H: (int)Math.Round(t.Height * t.CapturedScale));
        var seen = Math.Abs(b.Scale - 1) < 1e-9 ? saved : (W: (int)Math.Round(b.W * sx), H: (int)Math.Round(b.H * sy));
        // A size close to the saved one is most likely the saved one (the shrunk look can't tell a few pixels apart).
        var sizes = seen == saved || Math.Abs(b.Scale - 1) > 0.1 ? new[] { seen } : new[] { seen, saved };
        var cx0 = (b.X + b.W / 2.0) * sx;
        var cy0 = (b.Y + b.H / 2.0) * sy;
        var reach = (int)Math.Ceiling(Math.Max(sx, sy)) + 1;
        var step = Math.Max(1, (int)(Math.Max(sx, sy) / 2));
        // Without the exact box (pictures saved before 0.10.12), its short side is only known to about half a shrunk
        // pixel, and its long side to a pixel.
        var grow = t.BoxWidth > 0 ? 1 : 1 + (int)Math.Ceiling(t.CapturedScale / 2);
        var margin = reach + step + grow + 2;
        var span = (W: Math.Max(seen.W, saved.W), H: Math.Max(seen.H, saved.H));
        var near = new Region(screen, (int)(cx0 - span.W / 2.0) - margin, (int)(cy0 - span.H / 2.0) - margin, span.W + 2 * margin + grow, span.H + 2 * margin + grow);
        var needle = new Needle(t.Gray, t.Width, t.Height);

        var best = (X: 0, Y: 0, W: 0, H: 0, Score: double.MinValue);
        void Try(int x, int y, int w, int h)
        {
            if (x < near.Left || y < near.Top || x + w > near.Right || y + h > near.Bottom || w < t.Width || h < t.Height) return;
            var score = near.Score(x, y, w, h, needle);
            if (score > best.Score) best = (x, y, w, h, score);
        }
        foreach (var (bw, bh) in sizes)
        {
            // Every start a shrunk pixel or so around the find; far-shrunk pictures first every few pixels, then each.
            var x0 = (int)Math.Round(cx0 - bw / 2.0);
            var y0 = (int)Math.Round(cy0 - bh / 2.0);
            var before = best;
            best = (0, 0, 0, 0, double.MinValue);
            for (var dy = -reach; dy <= reach; dy += step)
            for (var dx = -reach; dx <= reach; dx += step)
                Try(x0 + dx, y0 + dy, bw, bh);
            if (step > 1 && best.W > 0)
            {
                var (cx, cy) = (best.X, best.Y);
                for (var dy = -step; dy <= step; dy++)
                for (var dx = -step; dx <= step; dx++)
                    Try(cx + dx, cy + dy, bw, bh);
            }
            if (before.Score > best.Score) best = before;
        }
        if (best.W == 0) return Coarse();
        if (t.BoxWidth == 0 || (best.W, best.H) != saved)
        {
            // Not the exact box: try it a little wider, then higher, then a pixel either way of both.
            var (cx, cy, cw, ch) = (best.X, best.Y, best.W, best.H);
            for (var dw = -grow; dw <= grow; dw++)
            for (var dx = -1; dx <= 1; dx++)
                Try(cx + dx, cy, cw + dw, ch);
            (cx, cy, cw, ch) = (best.X, best.Y, best.W, best.H);
            for (var dh = -grow; dh <= grow; dh++)
            for (var dy = -1; dy <= 1; dy++)
                Try(cx, cy + dy, cw, ch + dh);
            (cx, cy, cw, ch) = (best.X, best.Y, best.W, best.H);
            for (var dh = -1; dh <= 1; dh++)
            for (var dw = -1; dw <= 1; dw++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                Try(cx + dx, cy + dy, cw + dw, ch + dh);
        }
        if (best.Score < b.Score) return Coarse();

        // Colors are checked on the lined-up box, shrunk exactly the way the picture was: the shrunk look's box can
        // be a pixel off, and on a small or dark picture that alone looks a few percent dimmer.
        var colorOk = !checkColor || ColorFits(screen, t, (best.X, best.Y, best.W, best.H), range);
        return new Match(screen.Left + best.X + best.W / 2, screen.Top + best.Y + best.H / 2, Math.Max(0, best.Score), (double)best.W / t.Width, colorOk);

        // The find as the shrunk look saw it, in screen pixels.
        Match Coarse() => new(
            screen.Left + (int)Math.Round((b.X + b.W / 2 + 0.5) * sx),
            screen.Top + (int)Math.Round((b.Y + b.H / 2 + 0.5) * sy),
            Math.Max(0, b.Score), b.Scale * sx, coarse.ColorOk);
    }

    /// <summary>Whether the box's colors match a shrunk picture's, compared at the picture's own pixel size.</summary>
    private static bool ColorFits(ScreenImage screen, ImageTemplate t, (int X, int Y, int W, int H) box, double? range)
    {
        // Room around the box for the color check to try it a little bigger (see BestFit).
        var room = (int)Math.Ceiling(Math.Max(t.Width, t.Height) * 0.22) + 1;
        var (image, left, top) = ShrunkAround(screen, box, t.Width, t.Height, room);
        return ColorDistance(image, t, (left, top, t.Width, t.Height), range) <= ColorLimit;
    }

    /// <summary>
    /// The box shrunk to <paramref name="tw"/>×<paramref name="th"/> the way <see cref="Shrunk"/> does, with up to
    /// <paramref name="room"/> more shrunk pixels around it (as far as the screen goes). Also says where the box is in it.
    /// </summary>
    private static (ScreenImage Image, int Left, int Top) ShrunkAround(ScreenImage screen, (int X, int Y, int W, int H) box, int tw, int th, int room)
    {
        static int Edge(int start, int i, int size, int parts) => start + (int)Math.Floor((double)i * size / parts);
        int i0 = -room, i1 = tw + room, j0 = -room, j1 = th + room;
        while (i0 < 0 && Edge(box.X, i0, box.W, tw) < 0) i0++;
        while (i1 > tw && Edge(box.X, i1, box.W, tw) > screen.Width) i1--;
        while (j0 < 0 && Edge(box.Y, j0, box.H, th) < 0) j0++;
        while (j1 > th && Edge(box.Y, j1, box.H, th) > screen.Height) j1--;
        int w = i1 - i0, h = j1 - j0;
        var src = screen.Bgra;
        var px = new byte[w * h * 4];
        for (var j = j0; j < j1; j++)
        {
            int y0 = Edge(box.Y, j, box.H, th), y1 = Math.Max(y0 + 1, Edge(box.Y, j + 1, box.H, th));
            for (var i = i0; i < i1; i++)
            {
                int x0 = Edge(box.X, i, box.W, tw), x1 = Math.Max(x0 + 1, Edge(box.X, i + 1, box.W, tw));
                long b = 0, g = 0, r = 0, n = 0;
                for (var y = y0; y < y1 && y < screen.Height; y++)
                for (var x = x0; x < x1 && x < screen.Width; x++)
                {
                    var k = (y * screen.Width + x) * 4;
                    b += src[k]; g += src[k + 1]; r += src[k + 2]; n++;
                }
                if (n == 0) n = 1;
                var o = ((j - j0) * w + i - i0) * 4;
                px[o] = (byte)(b / n); px[o + 1] = (byte)(g / n); px[o + 2] = (byte)(r / n); px[o + 3] = 255;
            }
        }
        return (new ScreenImage(0, 0, w, h, px), -i0, -j0);
    }

    /// <summary>
    /// Part of the screen in gray with running sums, to shrink any box in it the way <see cref="Shrunk"/> does (each
    /// pixel the average of the ones it covers) and compare it with a picture. Positions count from the image's corner.
    /// </summary>
    private sealed class Region
    {
        private readonly long[] _sum;
        private readonly int _stride;

        public Region(ScreenImage screen, int left, int top, int width, int height)
        {
            Left = Math.Max(0, left);
            Top = Math.Max(0, top);
            Right = Math.Min(screen.Width, left + width);
            Bottom = Math.Min(screen.Height, top + height);
            var w = Math.Max(0, Right - Left);
            var h = Math.Max(0, Bottom - Top);
            _stride = w + 1;
            _sum = new long[_stride * (h + 1)];
            var px = screen.Bgra;
            for (var y = 0; y < h; y++)
            {
                long row = 0;
                var i = ((Top + y) * screen.Width + Left) * 4;
                for (var x = 0; x < w; x++, i += 4)
                {
                    row += (px[i] * 29 + px[i + 1] * 150 + px[i + 2] * 77) >> 8;
                    _sum[(y + 1) * _stride + x + 1] = _sum[y * _stride + x + 1] + row;
                }
            }
        }

        public int Left { get; }
        public int Top { get; }
        public int Right { get; }
        public int Bottom { get; }

        /// <summary>How alike the box at <paramref name="x"/>, <paramref name="y"/>, shrunk to the picture's size, is to it.</summary>
        public double Score(int x, int y, int w, int h, Needle t)
        {
            // Where each picture column and row starts in the box, as Shrunk splits it.
            Span<int> cols = stackalloc int[t.Width + 1];
            Span<int> rows = stackalloc int[t.Height + 1];
            Edges(cols, w, t.Width, x - Left);
            Edges(rows, h, t.Height, y - Top);
            double sum = 0, sumSq = 0, cross = 0;
            var px = t.Pixels;
            for (var ty = 0; ty < t.Height; ty++)
            {
                int r0 = rows[ty] * _stride, r1 = rows[ty + 1] * _stride, tall = rows[ty + 1] - rows[ty];
                for (var tx = 0; tx < t.Width; tx++)
                {
                    int c0 = cols[tx], c1 = cols[tx + 1];
                    var v = (double)(_sum[r1 + c1] - _sum[r0 + c1] - _sum[r1 + c0] + _sum[r0 + c0]) / ((c1 - c0) * tall);
                    sum += v;
                    sumSq += v * v;
                    cross += v * px[ty * t.Width + tx];
                }
            }
            return Correlation(t.Count, sum, sumSq, cross, t);
        }

        private static void Edges(Span<int> edges, int size, int parts, int offset)
        {
            for (var i = 0; i < parts; i++)
            {
                var start = i * size / parts;
                edges[i] = offset + start;
                edges[i + 1] = offset + Math.Max(start + 1, (i + 1) * size / parts);
            }
        }
    }

    /// <summary>The screen shrunk by <paramref name="by"/>, each pixel the average of the ones it covers. Its corner is 0, 0. Apps shrink big picked pictures with this too, so they match the shrunk screen.</summary>
    public static ScreenImage Shrunk(ScreenImage screen, double by)
    {
        int w = Math.Max(1, (int)Math.Round(screen.Width / by)), h = Math.Max(1, (int)Math.Round(screen.Height / by));
        var src = screen.Bgra;
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            int y0 = y * screen.Height / h, y1 = Math.Max(y0 + 1, (y + 1) * screen.Height / h);
            for (var x = 0; x < w; x++)
            {
                int x0 = x * screen.Width / w, x1 = Math.Max(x0 + 1, (x + 1) * screen.Width / w);
                long b = 0, g = 0, r = 0, n = 0;
                for (var sy = y0; sy < y1; sy++)
                for (var sx = x0; sx < x1; sx++)
                {
                    var i = (sy * screen.Width + sx) * 4;
                    b += src[i]; g += src[i + 1]; r += src[i + 2]; n++;
                }
                var o = (y * w + x) * 4;
                px[o] = (byte)(b / n); px[o + 1] = (byte)(g / n); px[o + 2] = (byte)(r / n); px[o + 3] = 255;
            }
        }
        return new ScreenImage(0, 0, w, h, px);
    }

    /// <summary>The best spot at the picture's own pixel size (the screen as given), and whether its colors match.</summary>
    private static (Found Box, bool ColorOk)? Best(ScreenImage screen, ImageTemplate template, double minScale, double maxScale, bool matchColor, double? range, double levels)
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
        var colorOk = true;
        if (checkColor)
        {
            // The best-shaped spot with the right colors; a same-shaped thing in another color is skipped.
            var pick = all.FirstOrDefault(f => ColorDistance(screen, template, f, range, levels) <= ColorLimit);
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
                    && (!checkColor || !colorOk || ColorDistance(screen, template, p, range, levels) <= ColorLimit)) b = p;
            }
        }
        return (b, colorOk);
    }

    /// <summary>
    /// How different the colors are at a found spot: both are shrunk to a small grid and the per-channel difference is
    /// averaged. <paramref name="range"/> null (Dimmer or brighter on): the screen's brightness is first evened out to
    /// the picture's, so a dimmer or brighter copy matches too. Otherwise a spot more than <paramref name="range"/>
    /// (a share, 0.1 = 10%) dimmer or brighter than the picture doesn't match at all.
    /// </summary>
    internal static double ColorDistance(ScreenImage screen, ImageTemplate t, (int X, int Y, int W, int H) at, double? range, double levels = NoiseLevels)
    {
        if (t.Rgb is null) return 0;
        if (range is not { } r)
        {
            var (mine, seen) = Thumbs(screen, t, at);
            return Distance(mine, seen, 0.6, 1.6);
        }
        // A dimmer copy is often found a little too small (the dim edge blends into the background), and a box
        // inside the picture only sees its middle, which is brighter. Measure where the colors line up best.
        var (bestMine, bestSeen) = BestFit(screen, t, at);
        var allowed = r + BrightnessNoise;
        if (BrightnessOff(bestMine, bestSeen, allowed, levels)) return 255;
        return Distance(bestMine, bestSeen, 1 / (1 + allowed), 1 / Math.Max(0.01, 1 - allowed));
    }

    /// <summary>Added to every brightness range: screens never show a picture exactly as captured.</summary>
    public const double BrightnessNoise = 0.03;

    private static (byte[] Mine, byte[] Seen) Thumbs(ScreenImage screen, ImageTemplate t, (int X, int Y, int W, int H) at)
    {
        var gw = Math.Min(t.ColorWidth, Math.Max(1, at.W / 2));
        var gh = Math.Min(t.ColorHeight, Math.Max(1, at.H / 2));
        var mine = gw == t.ColorWidth && gh == t.ColorHeight ? t.Rgb! : ResizeRgb(t.Rgb!, t.ColorWidth, t.ColorHeight, gw, gh);
        return (mine, ImageTemplate.ColorThumb(screen, at.X, at.Y, at.W, at.H, gw, gh));
    }

    /// <summary>The found box, or one a little bigger or smaller around the same middle, whose colors fit best once evened out.</summary>
    private static (byte[] Mine, byte[] Seen) BestFit(ScreenImage screen, ImageTemplate t, (int X, int Y, int W, int H) at)
    {
        (byte[], byte[]) best = default;
        var bestD = double.MaxValue;
        foreach (var f in new[] { 1.0, 1.06, 1.12, 1.19, 1.26, 1.34, 1.42, 0.94, 0.88 })
        {
            int w = (int)Math.Round(at.W * f), h = (int)Math.Round(at.H * f);
            int x = at.X + at.W / 2 - w / 2, y = at.Y + at.H / 2 - h / 2;
            if (w < 2 || h < 2 || x < 0 || y < 0 || x + w > screen.Width || y + h > screen.Height) continue;
            var pair = Thumbs(screen, t, (x, y, w, h));
            var d = Distance(pair.Mine, pair.Seen, 0.4, 2.5);
            if (d < bestD - 0.5) (bestD, best) = (d, pair); // a clearly better fit; otherwise keep the found size
        }
        return best.Item1 is null ? Thumbs(screen, t, at) : best;
    }

    private static double Distance(byte[] mine, byte[] seen, double minGain, double maxGain)
    {
        double sumWant = 0, sumSeen = 0;
        for (var i = 0; i < mine.Length; i++) { sumWant += mine[i]; sumSeen += seen[i]; }
        var gain = sumSeen < 1 ? 1 : Math.Clamp(sumWant / sumSeen, minGain, maxGain);
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
    private static bool BrightnessOff(byte[] want, byte[] seen, double range, double levels)
    {
        for (var c = 0; c < 3; c++)
        {
            double w = 0, s = 0;
            for (var i = c; i < want.Length; i += 3) { w += want[i]; s += seen[i]; }
            var cells = want.Length / 3;
            if (w / cells < 12) continue;
            var ratio = s / w;
            // On a dark picture a few levels either way is already a few percent (see LooseLevels).
            var allowed = Math.Max(range, levels * cells / w);
            if (ratio < 1 - allowed || ratio > 1 + allowed) return true;
        }
        return false;
    }

    /// <summary>Brightness levels (of 255) a color may be off by however dark it is, for the real check: none.</summary>
    private const double NoiseLevels = 0;
    /// <summary>
    /// Same, for the rough first look on a shrunk screen, whose pixels don't line up with the picture's: a pixel's
    /// shift of light text on a dark picture looked over 6% dimmer there. The lined-up box is then checked exactly.
    /// </summary>
    private const double LooseLevels = 5;

    private static double ColorDistance(ScreenImage screen, ImageTemplate t, Found f, double? range, double levels) =>
        ColorDistance(screen, t, (f.X, f.Y, f.W, f.H), range, levels);

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
        var factor = Math.Clamp((int)Math.Round(Math.Min(w, h) / (double)CoarseTarget, MidpointRounding.AwayFromZero), 1, MaxFactor);

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
        return Correlation(n, sumH, sumHH, sumHT, t);
    }

    /// <summary>The score from a screen area's sum, sum of squares and sum of products with the picture.</summary>
    private static double Correlation(double n, double sumH, double sumHH, double sumHT, Needle t)
    {
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
