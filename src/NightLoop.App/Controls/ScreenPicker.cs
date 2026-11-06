using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NightLoop.Engine.Screen;

namespace NightLoop.App.Controls;

public enum PickMode
{
    /// <summary>Click one point.</summary>
    Point,
    /// <summary>Click one point and read its color.</summary>
    Color,
    /// <summary>Drag a rectangle.</summary>
    Region,
}

/// <summary>
/// What was picked, in screen pixels. <see cref="Shot"/> holds the picked pixels: the frozen picture of the whole
/// desktop, or (live picking) the picked spot or area captured the moment the pick was confirmed.
/// </summary>
public sealed record PickResult(int X, int Y, int Width, int Height, ScreenImage Shot)
{
    public Rgb Color => Shot[Math.Clamp(X - Shot.Left, 0, Shot.Width - 1), Math.Clamp(Y - Shot.Top, 0, Shot.Height - 1)];
}

/// <summary>
/// Pick a point, a color or a rectangle on screen. Shows a frozen screenshot of every monitor in one topmost window,
/// so clicks never reach the game, then maps the mouse back to real screen pixels. Esc or right-click cancels.
/// Live picking (Settings › Macros) shows a see-through window instead, so the screen keeps moving underneath with
/// a crosshair, the position and the outline drawn on top; the pixels are captured the moment the pick is confirmed.
/// GDI captures leave out see-through (layered) windows, so the outline and labels never end up in the picture.
/// </summary>
public sealed class ScreenPicker : Window
{
    private readonly ScreenImage? _shot;
    private readonly IScreenCapture _capture;
    private readonly (int Left, int Top, int Width, int Height) _area;
    private readonly bool _live;
    private readonly PickMode _mode;
    private readonly Line _crossX;
    private readonly Line _crossY;
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly Rectangle _selection;
    private readonly Border _loupe;
    private readonly Rectangle _loupeSwatch = new() { Width = 22, Height = 22, Stroke = Brushes.White, StrokeThickness = 1 };
    private readonly TextBlock _loupeText = new() { Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private Point? _start;

    private ScreenPicker(IScreenCapture capture, ScreenImage? shot, PickMode mode, string hint, Color accent)
    {
        _capture = capture;
        _shot = shot;
        _live = shot is null;
        _area = shot is null ? capture.Desktop : (shot.Left, shot.Top, shot.Width, shot.Height);
        _mode = mode;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        Title = "Night-loop picker";
        if (_live)
        {
            // Almost see-through: the screen shows as it is, but the window still takes the clicks so they
            // never reach the game.
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));
        }
        else
        {
            Background = Brushes.Black;
        }

        var accentBrush = new SolidColorBrush(accent);
        _selection = new Rectangle
        {
            Stroke = accentBrush,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(_live ? (byte)0x1A : (byte)0x33, accent.R, accent.G, accent.B)),
            Visibility = Visibility.Collapsed,
        };
        _canvas.Children.Add(_selection);

        // Live picking: a thin crosshair through the cursor shows exactly which pixel a click lands on.
        var crossBrush = new SolidColorBrush(Color.FromArgb(0xB0, accent.R, accent.G, accent.B));
        _crossX = new Line { Stroke = crossBrush, StrokeThickness = 1, Visibility = Visibility.Collapsed, SnapsToDevicePixels = true };
        _crossY = new Line { Stroke = crossBrush, StrokeThickness = 1, Visibility = Visibility.Collapsed, SnapsToDevicePixels = true };
        _canvas.Children.Add(_crossX);
        _canvas.Children.Add(_crossY);

        _loupe = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x14, 0x10, 0x22)),
            BorderBrush = accentBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 10, 6),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _loupeSwatch, _loupeText } },
            Visibility = Visibility.Collapsed,
        };
        _canvas.Children.Add(_loupe);

        var hintBar = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
            Padding = new Thickness(16, 10, 16, 10),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x14, 0x10, 0x22)),
            BorderBrush = accentBrush,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = hint + "  ·  Esc cancels", Foreground = Brushes.White, FontSize = 14 },
        };

        var grid = new Grid();
        if (shot is not null)
        {
            grid.Children.Add(new Image { Source = ToBitmap(shot), Stretch = Stretch.Fill });
            grid.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), IsHitTestVisible = false });
        }
        grid.Children.Add(_canvas);
        grid.Children.Add(hintBar);
        Content = grid;

        // Rough placement in device-independent units; SourceInitialized then sets the exact pixels.
        var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? (Visual)this);
        Left = _area.Left / dpi.DpiScaleX;
        Top = _area.Top / dpi.DpiScaleY;
        Width = _area.Width / dpi.DpiScaleX;
        Height = _area.Height / dpi.DpiScaleY;

        SourceInitialized += (_, _) => Cover();
        ContentRendered += (_, _) => Cover();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Cover);
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
        };
    }

    /// <summary>The result, or null when cancelled.</summary>
    public PickResult? Result { get; private set; }

    /// <summary>
    /// Shows the picker over a fresh screenshot of the whole desktop, or with <paramref name="live"/> over the
    /// moving screen.
    /// </summary>
    public static PickResult? Pick(IScreenCapture capture, PickMode mode, string hint, Color accent, bool live = false)
    {
        ScreenImage? shot = null;
        if (!live)
        {
            var d = capture.Desktop;
            shot = capture.Capture(d.Left, d.Top, d.Width, d.Height);
        }
        var picker = new ScreenPicker(capture, shot, mode, hint, accent);
        picker.ShowDialog();
        return picker.Result;
    }

    /// <summary>The picked pixels: from the frozen picture, or captured right now when picking live.</summary>
    private PickResult Confirm(int x, int y, int width, int height) =>
        new(x, y, width, height, _shot ?? _capture.Capture(x, y, width, height));

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Result = null;
            Close();
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        Result = null;
        Close();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_mode != PickMode.Region)
        {
            var (x, y) = ToScreen(p);
            Result = Confirm(x, y, 1, 1);
            Close();
            return;
        }
        _start = p;
        CaptureMouse();
        UpdateSelection(p);
        _selection.Visibility = Visibility.Visible;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_start is not null) UpdateSelection(p);
        UpdateLoupe(p);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_start is not { } start) return;
        ReleaseMouseCapture();
        var (x1, y1) = ToScreen(start);
        var (x2, y2) = ToScreen(e.GetPosition(this));
        _start = null;
        var (left, top) = (Math.Min(x1, x2), Math.Min(y1, y2));
        var (w, h) = (Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        if (w < 4 || h < 4)
        {
            // A click without a drag: start again.
            _selection.Visibility = Visibility.Collapsed;
            return;
        }
        Result = Confirm(left, top, w, h);
        Close();
    }

    private void UpdateSelection(Point p)
    {
        if (_start is not { } s) return;
        Canvas.SetLeft(_selection, Math.Min(s.X, p.X));
        Canvas.SetTop(_selection, Math.Min(s.Y, p.Y));
        _selection.Width = Math.Abs(p.X - s.X);
        _selection.Height = Math.Abs(p.Y - s.Y);
    }

    /// <summary>Shows the screen position (and color) next to the cursor.</summary>
    private void UpdateLoupe(Point p)
    {
        var (x, y) = ToScreen(p);
        var c = _mode != PickMode.Color ? default
            : _shot is { } shot ? shot[Math.Clamp(x - shot.Left, 0, shot.Width - 1), Math.Clamp(y - shot.Top, 0, shot.Height - 1)]
            : _capture.Capture(x, y, 1, 1)[0, 0];
        if (_live)
        {
            _crossX.X1 = 0;
            _crossX.X2 = ActualWidth;
            _crossX.Y1 = _crossX.Y2 = Math.Floor(p.Y) + 0.5;
            _crossY.Y1 = 0;
            _crossY.Y2 = ActualHeight;
            _crossY.X1 = _crossY.X2 = Math.Floor(p.X) + 0.5;
            _crossX.Visibility = _crossY.Visibility = Visibility.Visible;
        }
        _loupeSwatch.Visibility = _mode == PickMode.Color ? Visibility.Visible : Visibility.Collapsed;
        _loupeSwatch.Fill = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        _loupeText.Text = _mode == PickMode.Color ? $"{c}   {x}, {y}" : $"{x}, {y}";
        _loupe.Visibility = Visibility.Visible;
        var left = p.X + 18;
        var top = p.Y + 18;
        if (left + 180 > ActualWidth) left = p.X - 190;
        if (top + 40 > ActualHeight) top = p.Y - 50;
        Canvas.SetLeft(_loupe, left);
        Canvas.SetTop(_loupe, top);
    }

    /// <summary>Window position (device-independent) to real screen pixels.</summary>
    private (int X, int Y) ToScreen(Point p)
    {
        var sx = ActualWidth > 0 ? _area.Width / ActualWidth : 1;
        var sy = ActualHeight > 0 ? _area.Height / ActualHeight : 1;
        var x = (int)Math.Floor(p.X * sx);
        var y = (int)Math.Floor(p.Y * sy);
        return (_area.Left + Math.Clamp(x, 0, _area.Width - 1), _area.Top + Math.Clamp(y, 0, _area.Height - 1));
    }

    /// <summary>Places the window over exactly the captured pixels (every monitor).</summary>
    private void Cover()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        _ = SetWindowPos(hwnd, new IntPtr(-1), _area.Left, _area.Top, _area.Width, _area.Height, 0x0010 /* SWP_NOACTIVATE */);
    }

    private static BitmapSource ToBitmap(ScreenImage shot)
    {
        var bitmap = BitmapSource.Create(shot.Width, shot.Height, 96, 96, PixelFormats.Bgr32, null, shot.Bgra, shot.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
