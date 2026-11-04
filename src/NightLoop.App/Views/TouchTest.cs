using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NightLoop.App.ViewModels;

namespace NightLoop.App.Views;

/// <summary>
/// <c>NightLoop.exe --touch-test &lt;folder&gt;</c>: opens the Macros screen in a real window and drives it with
/// Windows touch injection (InjectTouchInput), the same input a touch screen produces. Checks that a finger swipe
/// scrolls the page and a tap presses a button and ticks a block. Writes touch.txt. Exit 0 = passed, 1 = failed,
/// 3 = this PC can't inject touch (so nothing was tested).
/// </summary>
public static class TouchTest
{
    public static int Run(Application app, string folder)
    {
        Directory.CreateDirectory(folder);
        var log = new List<string>();
        var result = 1;
        var dir = Path.Combine(Path.GetTempPath(), "nightloop-touch");
        Directory.CreateDirectory(dir);
        var api = new Client.ApiClient(new System.Net.Http.HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/v1/") }, Guid.Empty, "touch");
        var settings = new LocalData.LocalSettings(Path.Combine(dir, "settings.json"));
        var vm = new MainViewModel(api, new TargetGamesViewModel(new LocalData.GamesStore(Path.Combine(dir, "games.json")), settings), settings);
        vm.ShowSample();
        var view = new MacrosView { DataContext = vm };
        var window = new Window
        {
            Title = "Night-loop touch test", Width = 1100, Height = 700, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = (Brush)app.FindResource("Bg"),
            Content = view,
        };

        window.Loaded += async (_, _) =>
        {
            try
            {
                result = await RunStepsAsync(window, view, vm, log);
            }
            catch (Exception ex)
            {
                log.Add("FAIL " + ex);
                result = 1;
            }
            File.WriteAllLines(Path.Combine(folder, "touch.txt"), log);
            foreach (var line in log) Console.WriteLine(line);
            window.Close();
            vm.Dispose();
            app.Shutdown(result);
        };
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        window.Show();
        window.Activate();
        return app.Run();
    }

    private static async Task<int> RunStepsAsync(Window window, MacrosView view, MainViewModel vm, List<string> log)
    {
        await Task.Delay(800); // let it lay out and come to the front
        if (!InitializeTouchInjection(2, TouchFeedbackNone))
        {
            log.Add($"SKIP touch injection isn't available here (error {Marshal.GetLastWin32Error()}).");
            return 3;
        }

        var touches = 0;
        var mice = 0;
        window.PreviewTouchDown += (_, _) => touches++;
        window.PreviewMouseDown += (_, _) => mice++;
        log.Add($"info tablet devices: {System.Windows.Input.Tablet.TabletDevices.Count}, active: {window.IsActive}, " +
                $"pointer support: {(AppContext.TryGetSwitch("Switch.System.Windows.Input.Stylus.EnablePointerSupport", out var on) && on)}");
        // Can this desktop deliver input to a window at all? A mouse click on the page header should arrive.
        var probe = window.PointToScreen(new Point(window.ActualWidth * 0.5, 20));
        SetCursorPos((int)probe.X, (int)probe.Y);
        mouse_event(MouseLeftDown, 0, 0, 0, IntPtr.Zero);
        mouse_event(MouseLeftUp, 0, 0, 0, IntPtr.Zero);
        await Task.Delay(500);
        if (mice == 0)
        {
            log.Add("SKIP this desktop doesn't deliver any input to windows (a real mouse click didn't arrive either), so touch can't be tested here.");
            return 3;
        }
        mice = 0;

        var scroller = (ScrollViewer)view.FindName("Scroller");
        // 1. A swipe up scrolls the page down.
        var before = scroller.VerticalOffset;
        var start = window.PointToScreen(new Point(window.ActualWidth * 0.35, window.ActualHeight * 0.75));
        var end = window.PointToScreen(new Point(window.ActualWidth * 0.35, window.ActualHeight * 0.25));
        if (!await SwipeAsync(start, end))
        {
            log.Add($"SKIP InjectTouchInput was refused (error {Marshal.GetLastWin32Error()}).");
            return 3;
        }
        await Task.Delay(600);
        var after = scroller.VerticalOffset;
        log.Add($"{(after > before + 50 ? "ok  " : "FAIL")} swipe scrolls the page: offset {before:0} -> {after:0}");
        var ok = after > before + 50;

        // 2. A tap on "Select all blocks" ticks every block; a second tap unticks them.
        var selectAll = FindButton(view, b => ReferenceEquals(b.Command, vm.ToggleCheckAllCommand))
                        ?? throw new InvalidOperationException("No Select all button.");
        selectAll.BringIntoView();
        await Task.Delay(400);
        await TapAsync(Center(selectAll));
        await Task.Delay(400);
        log.Add($"{(vm.AllChecked ? "ok  " : "FAIL")} tap on Select all ticks every block ({vm.CheckedCount} of {vm.Blocks.Count})");
        ok &= vm.AllChecked;
        await TapAsync(Center(selectAll));
        await Task.Delay(400);
        log.Add($"{(!vm.HasChecked ? "ok  " : "FAIL")} second tap unticks them ({vm.CheckedCount} left)");
        ok &= !vm.HasChecked;

        // 3. A tap on a timeline row highlights it in the block editor.
        var timeline = (ItemsControl)view.FindName("Timeline");
        if (timeline.ItemContainerGenerator.ContainerFromIndex(1) is FrameworkElement row)
        {
            row.BringIntoView();
            await Task.Delay(400);
            var p = row.PointToScreen(new Point(row.ActualWidth * 0.45, row.ActualHeight / 2));
            await TapAsync(p);
            await Task.Delay(400);
            var picked = vm.SelectedBlock == vm.Blocks[1];
            log.Add($"{(picked ? "ok  " : "FAIL")} tap on a block selects it ({vm.InspectorTitle})");
            ok &= picked;
        }

        log.Add($"info touch-downs seen: {touches}, mouse-downs seen: {mice}");
        log.Add(ok ? "PASSED" : "FAILED");
        return ok ? 0 : 1;
    }

    private static Point Center(FrameworkElement e) => e.PointToScreen(new Point(e.ActualWidth / 2, e.ActualHeight / 2));

    private static Button? FindButton(DependencyObject root, Func<Button, bool> match)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button b && match(b)) return b;
            if (FindButton(child, match) is { } found) return found;
        }
        return null;
    }

    // ---------------- Touch injection ----------------

    private static async Task<bool> SwipeAsync(Point from, Point to)
    {
        const int steps = 20;
        if (!Inject(from, PointerFlagDown)) return false;
        for (var i = 1; i <= steps; i++)
        {
            await Task.Delay(16);
            var p = new Point(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps);
            Inject(p, PointerFlagUpdate);
        }
        await Task.Delay(16);
        return Inject(to, PointerFlagUp);
    }

    private static async Task TapAsync(Point p)
    {
        Inject(p, PointerFlagDown);
        await Task.Delay(60);
        Inject(p, PointerFlagUp);
    }

    private static bool Inject(Point p, uint flags)
    {
        var x = (int)Math.Round(p.X);
        var y = (int)Math.Round(p.Y);
        var info = new PointerTouchInfo
        {
            PointerInfo = new PointerInfo { PointerType = PtTouch, PointerId = 0, PointerFlags = flags, PtPixelLocation = new NativePoint(x, y) },
            TouchMask = TouchMaskContactArea | TouchMaskOrientation | TouchMaskPressure,
            RcContact = new NativeRect(x - 2, y - 2, x + 2, y + 2),
            Orientation = 90,
            Pressure = 32000,
        };
        return InjectTouchInput(1, [info]);
    }

    private const uint TouchFeedbackNone = 0x3;
    private const uint PtTouch = 2;
    private const uint PointerFlagInRange = 0x2, PointerFlagInContact = 0x4;
    private const uint PointerFlagDown = 0x10000 | PointerFlagInRange | PointerFlagInContact;
    private const uint PointerFlagUpdate = 0x20000 | PointerFlagInRange | PointerFlagInContact;
    private const uint PointerFlagUp = 0x40000;
    private const uint TouchMaskContactArea = 1, TouchMaskOrientation = 2, TouchMaskPressure = 4;

    [StructLayout(LayoutKind.Sequential)]
    private record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private record struct NativeRect(int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInfo
    {
        public uint PointerType;
        public uint PointerId;
        public uint FrameId;
        public uint PointerFlags;
        public IntPtr SourceDevice;
        public IntPtr HwndTarget;
        public NativePoint PtPixelLocation;
        public NativePoint PtHimetricLocation;
        public NativePoint PtPixelLocationRaw;
        public NativePoint PtHimetricLocationRaw;
        public uint Time;
        public uint HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerTouchInfo
    {
        public PointerInfo PointerInfo;
        public uint TouchFlags;
        public uint TouchMask;
        public NativeRect RcContact;
        public NativeRect RcContactRaw;
        public uint Orientation;
        public uint Pressure;
    }

    private const uint MouseLeftDown = 0x0002, MouseLeftUp = 0x0004;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InjectTouchInput(uint count, [In] PointerTouchInfo[] contacts);
}
