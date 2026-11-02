namespace NightLoop.Engine.Screen;

/// <summary>A captured rectangle of the screen. Pixels are 32-bit BGRA, top row first.</summary>
public sealed class ScreenImage
{
    public ScreenImage(int left, int top, int width, int height, byte[] bgra)
    {
        if (bgra.Length < width * height * 4) throw new ArgumentException("Not enough pixel data.", nameof(bgra));
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    /// <summary>Screen position of the top-left pixel.</summary>
    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }

    /// <summary>Color at a point inside the image (image coordinates).</summary>
    public Rgb this[int x, int y]
    {
        get
        {
            var i = (y * Width + x) * 4;
            return new Rgb(Bgra[i + 2], Bgra[i + 1], Bgra[i]);
        }
    }

    /// <summary>Screen point of the pixel closest to the middle of the matching area, or null when no pixel matches.</summary>
    public (int X, int Y)? Find(Rgb color, int tolerance)
    {
        long sumX = 0, sumY = 0, count = 0;
        int firstX = -1, firstY = -1;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (!this[x, y].IsNear(color, tolerance)) continue;
                if (firstX < 0) { firstX = x; firstY = y; }
                sumX += x;
                sumY += y;
                count++;
            }
        }
        if (count == 0) return null;
        // The middle of all matching pixels, unless that misses the color (e.g. two separate spots): then the first hit.
        var cx = (int)(sumX / count);
        var cy = (int)(sumY / count);
        if (!this[cx, cy].IsNear(color, tolerance)) (cx, cy) = (firstX, firstY);
        return (Left + cx, Top + cy);
    }
}

public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Parses "#RRGGBB".</summary>
    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        if (text is not { Length: 7 } || text[0] != '#') return false;
        if (!int.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        color = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>Every channel within <paramref name="tolerance"/> (0–255).</summary>
    public bool IsNear(Rgb other, int tolerance) =>
        Math.Abs(R - other.R) <= tolerance && Math.Abs(G - other.G) <= tolerance && Math.Abs(B - other.B) <= tolerance;

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>Reads pixels from the screen. Faked in tests.</summary>
public interface IScreenCapture
{
    /// <summary>Captures a rectangle in screen pixels. Parts outside the desktop come back black.</summary>
    ScreenImage Capture(int left, int top, int width, int height);

    /// <summary>The whole virtual desktop (all monitors).</summary>
    (int Left, int Top, int Width, int Height) Desktop { get; }
}
