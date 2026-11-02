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

/// <summary>What was picked, in screen pixels. <see cref="Shot"/> is the frozen picture of the whole desktop.</summary>
public sealed record PickResult(int X, int Y, int Width, int Height, ScreenImage Shot)
{
    public Rgb Color => Shot[Math.Clamp(X - Shot.Left, 0, Shot.Width - 1), Math.Clamp(Y - Shot.Top, 0, Shot.Height - 1)];
}

/// <summary>
/// Pick a point, a color or a rectangle on screen. Shows a frozen screenshot of every monitor in one topmost window,
/// so clicks never reach the game, then maps the mouse back to real screen pixels. Esc or right-click cancels.
/// </summary>
public sealed class ScreenPicker : Window
{
    private readonly ScreenImage _shot;
    private readonly PickMode _mode;
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly Rectangle _selection;
    private readonly Border _loupe;
    private readonly Rectangle _loupeSwatch = new() { Width = 22, Height = 22, Stroke = Brushes.White, StrokeThickness = 1 };
    private readonly TextBlock _loupeText = new() { Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private Point? _start;

    private ScreenPicker(ScreenImage shot, PickMode mode, string hint, Color accent)
    {
        _shot = shot;
        _mode = mode;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        Background = Brushes.Black;
        Title = "Night-loop picker";

        var accentBrush = new SolidColorBrush(accent);
        _selection = new Rectangle
        {
            Stroke = accentBrush,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B)),
            Visibility = Visibility.Collapsed,
        };
        _canvas.Children.Add(_selection);

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

        Content = new Grid
        {
            Children =
            {
                new Image { Source = ToBitmap(shot), Stretch = Stretch.Fill },
                new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), IsHitTestVisible = false },
                _canvas,
                hintBar,
            },
        };

        // Rough placement in device-independent units; SourceInitialized then sets the exact pixels.
        var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? (Visual)this);
        Left = shot.Left / dpi.DpiScaleX;
        Top = shot.Top / dpi.DpiScaleY;
        Width = shot.Width / dpi.DpiScaleX;
        Height = shot.Height / dpi.DpiScaleY;

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

    /// <summary>Shows the picker over a fresh screenshot of the whole desktop.</summary>
    public static PickResult? Pick(IScreenCapture capture, PickMode mode, string hint, Color accent)
    {
        var d = capture.Desktop;
        var shot = capture.Capture(d.Left, d.Top, d.Width, d.Height);
        var picker = new ScreenPicker(shot, mode, hint, accent);
        picker.ShowDialog();
        return picker.Result;
    }

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
            Result = new PickResult(x, y, 1, 1, _shot);
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
        Result = new PickResult(left, top, w, h, _shot);
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
        var c = _shot[Math.Clamp(x - _shot.Left, 0, _shot.Width - 1), Math.Clamp(y - _shot.Top, 0, _shot.Height - 1)];
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
        var sx = ActualWidth > 0 ? _shot.Width / ActualWidth : 1;
        var sy = ActualHeight > 0 ? _shot.Height / ActualHeight : 1;
        var x = (int)Math.Floor(p.X * sx);
        var y = (int)Math.Floor(p.Y * sy);
        return (_shot.Left + Math.Clamp(x, 0, _shot.Width - 1), _shot.Top + Math.Clamp(y, 0, _shot.Height - 1));
    }

    /// <summary>Places the window over exactly the captured pixels (every monitor).</summary>
    private void Cover()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        _ = SetWindowPos(hwnd, new IntPtr(-1), _shot.Left, _shot.Top, _shot.Width, _shot.Height, 0x0010 /* SWP_NOACTIVATE */);
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
