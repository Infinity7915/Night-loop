using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NightLoop.Engine.Screen;

namespace NightLoop.App.Services;

/// <summary>Saves the Screenshot block's pictures as PNG files in Pictures\Night-loop.</summary>
public static class Screenshots
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Night-loop");

    /// <summary>Writes the picture. An existing file of the same name is kept and the new one gets the time added.</summary>
    public static void Save(ScreenImage image, string fileName)
    {
        // WPF imaging objects belong to the thread that makes them, so encode on the UI thread.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Save(image, fileName));
            return;
        }

        Directory.CreateDirectory(Folder);
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.Length == 0) name = "night-loop";
        var path = Path.Combine(Folder, name + ".png");
        if (File.Exists(path)) path = Path.Combine(Folder, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.png");

        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr32, null, image.Bgra, image.Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
