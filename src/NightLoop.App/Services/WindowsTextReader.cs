using System.Runtime.InteropServices.WindowsRuntime;
using NightLoop.Engine.Screen;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace NightLoop.App.Services;

/// <summary>
/// Reads text on screen with Windows' own text recognition (no download, works offline). Needs a Windows display
/// language that includes text recognition, which most do.
/// </summary>
public sealed class WindowsTextReader : ITextReader
{
    /// <summary>Small captures are enlarged first: game UI text is often only 10–14 px tall.</summary>
    private const int SmallHeight = 360;

    private readonly OcrEngine _engine;

    private WindowsTextReader(OcrEngine engine) => _engine = engine;

    /// <summary>Null when no installed language supports text recognition.</summary>
    public static WindowsTextReader? TryCreate()
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages() is { } engine ? new WindowsTextReader(engine) : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TypeLoadException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<TextLine>> ReadAsync(ScreenImage image, CancellationToken cancellationToken)
    {
        var max = (int)OcrEngine.MaxImageDimension;
        var scale = image.Height < SmallHeight && image.Width * 2 <= max ? 2.0 : 1.0;
        if (image.Width * scale > max || image.Height * scale > max) scale = Math.Min((double)max / image.Width, (double)max / image.Height);
        var (w, h) = (Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
        var pixels = scale == 1.0 ? image.Bgra : Resize(image, w, h);

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(0, w * h * 4), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Ignore);
        var result = await _engine.RecognizeAsync(bitmap).AsTask(cancellationToken);

        var lines = new List<TextLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var words = line.Words.Select(word => new TextWord(word.Text, new ScreenRect(
                image.Left + (int)(word.BoundingRect.X / scale),
                image.Top + (int)(word.BoundingRect.Y / scale),
                Math.Max(1, (int)(word.BoundingRect.Width / scale)),
                Math.Max(1, (int)(word.BoundingRect.Height / scale))))).ToList();
            if (words.Count > 0) lines.Add(new TextLine(words));
        }
        return lines;
    }

    /// <summary>Bilinear resize of BGRA pixels.</summary>
    private static byte[] Resize(ScreenImage image, int w, int h)
    {
        var src = image.Bgra;
        var dst = new byte[w * h * 4];
        var fx = (double)image.Width / w;
        var fy = (double)image.Height / h;
        for (var y = 0; y < h; y++)
        {
            var sy = Math.Max(0, (y + 0.5) * fy - 0.5);
            var y0 = Math.Min((int)sy, image.Height - 1);
            var y1 = Math.Min(y0 + 1, image.Height - 1);
            var ty = sy - y0;
            for (var x = 0; x < w; x++)
            {
                var sx = Math.Max(0, (x + 0.5) * fx - 0.5);
                var x0 = Math.Min((int)sx, image.Width - 1);
                var x1 = Math.Min(x0 + 1, image.Width - 1);
                var tx = sx - x0;
                var d = (y * w + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var top = src[(y0 * image.Width + x0) * 4 + c] * (1 - tx) + src[(y0 * image.Width + x1) * 4 + c] * tx;
                    var bottom = src[(y1 * image.Width + x0) * 4 + c] * (1 - tx) + src[(y1 * image.Width + x1) * 4 + c] * tx;
                    dst[d + c] = (byte)Math.Round(top * (1 - ty) + bottom * ty);
                }
                dst[d + 3] = 255;
            }
        }
        return dst;
    }
}
