using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace NightLoop.App.Controls;

/// <summary>
/// Shows a block's saved picture big, so the person can click (or drag) the exact spot inside it that Find &amp; click
/// and Scan &amp; turn should click once the picture is found. The spot is kept as a share of the picture's width and
/// height, so it stays on the same part of the picture when the picture is found smaller or bigger.
/// </summary>
public sealed class ClickSpotPicker : Window
{
    private const double MaxSide = 520;
    private const double MinSide = 260;

    private readonly Grid _surface = new() { Cursor = Cursors.Cross, Background = Brushes.Transparent };
    private readonly Line _crossX = new() { StrokeThickness = 1, IsHitTestVisible = false, SnapsToDevicePixels = true };
    private readonly Line _crossY = new() { StrokeThickness = 1, IsHitTestVisible = false, SnapsToDevicePixels = true };
    private readonly Ellipse _dot = new() { Width = 14, Height = 14, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly TextBlock _readout = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Canvas _marks = new() { IsHitTestVisible = false };
    private readonly double _w;
    private readonly double _h;
    private double _x;
    private double _y;

    private ClickSpotPicker(BitmapSource picture, double x, double y)
    {
        _x = Math.Clamp(x, 0, 1);
        _y = Math.Clamp(y, 0, 1);
        // Small pictures are blown up (sharp pixels) so a spot is easy to hit; big ones are shrunk to fit.
        var side = (double)Math.Max(1, Math.Max(picture.PixelWidth, picture.PixelHeight));
        var scale = side > MaxSide ? MaxSide / side
            : side < MinSide ? Math.Min(Math.Ceiling(MinSide / side), MaxSide / side) // whole steps keep pixels even
            : 1;
        _w = Math.Max(1, Math.Round(picture.PixelWidth * scale));
        _h = Math.Max(1, Math.Round(picture.PixelHeight * scale));

        Title = "Click spot";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Topmost = Application.Current?.MainWindow?.Topmost == true;
        SetResourceReference(BackgroundProperty, "Panel");

        var accent = TryFindResource("Accent") as SolidColorBrush ?? Brushes.MediumPurple;
        _crossX.Stroke = _crossY.Stroke = new SolidColorBrush(Color.FromArgb(0xC0, accent.Color.R, accent.Color.G, accent.Color.B));
        _dot.Stroke = accent;
        _dot.Fill = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));

        _surface.Width = _w;
        _surface.Height = _h;
        _surface.Children.Add(new Image
        {
            Source = picture, Width = _w, Height = _h, Stretch = Stretch.Fill,
        });
        RenderOptions.SetBitmapScalingMode(_surface.Children[0], BitmapScalingMode.NearestNeighbor);
        _marks.Children.Add(_crossX);
        _marks.Children.Add(_crossY);
        _marks.Children.Add(_dot);
        _surface.Children.Add(_marks);
        _surface.MouseLeftButtonDown += (_, e) => { _surface.CaptureMouse(); SetFrom(e.GetPosition(_surface)); };
        _surface.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && _surface.IsMouseCaptured) SetFrom(e.GetPosition(_surface)); };
        _surface.MouseLeftButtonUp += (_, _) => _surface.ReleaseMouseCapture();

        var frame = new Border
        {
            Child = _surface,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x0C, 0x1A)),
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");

        var hint = new TextBlock
        {
            Text = "Click the spot in the picture to click. Once the picture is found on screen, the click lands there, " +
                   "even when the picture shows up smaller or bigger.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12),
            MaxWidth = Math.Max(_w, 320),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSoft");
        _readout.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        var middle = new Button { Content = "Middle", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6, 14, 6) };
        middle.Click += (_, _) => { _x = _y = 0.5; Draw(); };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6, 14, 6) };
        var save = new Button { Content = "Save spot", IsDefault = true, Padding = new Thickness(14, 6, 14, 6) };
        if (TryFindResource("PrimaryButton") is Style primary) save.Style = primary;
        save.Click += (_, _) => { DialogResult = true; };

        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_readout, Dock.Left);
        buttons.Children.Add(_readout);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(middle);
        right.Children.Add(cancel);
        right.Children.Add(save);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(hint);
        root.Children.Add(frame);
        root.Children.Add(buttons);
        Content = root;

        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) =>
        {
            var bg = (TryFindResource("Panel") as SolidColorBrush)?.Color ?? Color.FromRgb(0x1A, 0x14, 0x2A);
            var text = (TryFindResource("Text") as SolidColorBrush)?.Color ?? Colors.White;
            DarkTitleBar.Apply(this, bg, text, dark: bg.R + bg.G + bg.B < 384);
        };
        Draw();
    }

    /// <summary>The chosen spot (0–1 across and down), or null when cancelled.</summary>
    public static (double X, double Y)? Pick(Window? owner, BitmapSource picture, double x, double y)
    {
        var picker = new ClickSpotPicker(picture, x, y);
        if (owner is { IsLoaded: true }) picker.Owner = owner;
        else picker.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return picker.ShowDialog() == true ? (picker._x, picker._y) : null;
    }

    /// <summary>The picker's contents without the window, for the smoke test's pictures.</summary>
    internal static FrameworkElement ContentForSmoke(BitmapSource picture, double x, double y)
    {
        var picker = new ClickSpotPicker(picture, x, y);
        var content = (FrameworkElement)picker.Content;
        picker.Content = null;
        picker.Close();
        return content;
    }

    private void SetFrom(Point p)
    {
        _x = Math.Clamp(p.X / _w, 0, 1);
        _y = Math.Clamp(p.Y / _h, 0, 1);
        Draw();
    }

    /// <summary>Arrow keys nudge the spot by 1% (Shift: 10%).</summary>
    private void OnKey(object sender, KeyEventArgs e)
    {
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.01;
        switch (e.Key)
        {
            case Key.Left: _x = Math.Clamp(_x - step, 0, 1); break;
            case Key.Right: _x = Math.Clamp(_x + step, 0, 1); break;
            case Key.Up: _y = Math.Clamp(_y - step, 0, 1); break;
            case Key.Down: _y = Math.Clamp(_y + step, 0, 1); break;
            default: return;
        }
        e.Handled = true;
        Draw();
    }

    private void Draw()
    {
        // Saved as whole percents, so show (and draw) the spot that will be saved.
        _x = Math.Round(_x * 100) / 100;
        _y = Math.Round(_y * 100) / 100;
        var (px, py) = (_x * _w, _y * _h);
        _crossX.X1 = 0; _crossX.X2 = _w; _crossX.Y1 = _crossX.Y2 = py;
        _crossY.Y1 = 0; _crossY.Y2 = _h; _crossY.X1 = _crossY.X2 = px;
        Canvas.SetLeft(_dot, px - _dot.Width / 2);
        Canvas.SetTop(_dot, py - _dot.Height / 2);
        _readout.Text = Describe(_x, _y);
    }

    /// <summary>"Middle", or how far across and down, for the picker and the inspector.</summary>
    public static string Describe(double x, double y) =>
        Math.Abs(x - 0.5) < 0.005 && Math.Abs(y - 0.5) < 0.005
            ? "Clicks the middle of the picture"
            : $"Clicks {Math.Round(x * 100)}% across, {Math.Round(y * 100)}% down";
}
