using NightLoop.Engine.Input;
using NightLoop.Engine.Runtime;
using NightLoop.Engine.Screen;
using NightLoop.Engine.Window;

namespace NightLoop.Engine.Tests;

/// <summary>Records every call as a short string, e.g. "down A", "click-down Left", "move 10,20".</summary>
internal sealed class FakeInput : IInputSender
{
    private readonly object _gate = new();
    private readonly List<string> _log = [];
    private (int X, int Y) _cursor;

    public List<string> Log { get { lock (_gate) return [.. _log]; } }

    private void Add(string s) { lock (_gate) _log.Add(s); }

    public void KeyDown(int vk) => Add($"down {Contracts.MacroDocument.KeyCatalog.NameOf(vk)}");
    public void KeyUp(int vk) => Add($"up {Contracts.MacroDocument.KeyCatalog.NameOf(vk)}");
    public void MouseDown(MouseButton b) => Add($"mdown {b}");
    public void MouseUp(MouseButton b) => Add($"mup {b}");
    public void MoveTo(int x, int y) { _cursor = (x, y); Add($"move {x},{y}"); }
    public void MoveBy(int dx, int dy) => Add($"rel {dx},{dy}");
    public void Scroll(int notches) => Add($"scroll {notches}");
    public void TypeChar(char c) => Add($"char {c}");
    public (int X, int Y) CursorPosition => _cursor;
}

/// <summary>Returns at once and adds up how long the macro asked to wait.</summary>
internal sealed class FakeDelay : IDelay
{
    private long _total;
    public long TotalMs => Interlocked.Read(ref _total);
    public Func<Task>? OnDelay { get; set; }

    private static async Task YieldAsync() => await Task.Yield();

    public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Add(ref _total, milliseconds);
        return OnDelay?.Invoke() ?? YieldAsync();
    }
}


internal sealed class FakeWindows : IWindowService
{
    public bool Foreground { get; set; } = true;
    public ClientArea Area { get; set; } = new(100, 200, 800, 600);
    public int ForegroundChecks;

    public bool IsForeground(string exeName)
    {
        Interlocked.Increment(ref ForegroundChecks);
        return Foreground;
    }

    public bool Running { get; set; } = true;
    public ClientArea Bounds { get; set; } = new(92, 170, 816, 638);
    public List<ClientArea> Monitors { get; } = [new(0, 0, 1920, 1040), new(1920, 0, 2560, 1400)];
    public List<string> Calls { get; } = [];

    public bool IsProcessRunning(string exeName) => Running;
    public ClientArea? GetClientArea(string exeName) => Running ? Area : null;
    public bool Resize(string exeName, int width, int height) { Calls.Add($"resize {exeName} {width}x{height}"); return true; }
    public bool MoveTo(string exeName, int x, int y) { Calls.Add($"moveto {exeName} {x},{y}"); return true; }
    public bool Focus(string exeName) { Calls.Add($"focus {exeName}"); return true; }
    public ClientArea? GetWindowBounds(string exeName) => Bounds;
    public IReadOnlyList<ClientArea> GetMonitors() => Monitors;

    /// <summary>What a launch does; by default the program opens its window at once.</summary>
    public Func<string, string?>? OnLaunch { get; set; }

    public string? Launch(string path, string? arguments)
    {
        Calls.Add($"launch {path}{(string.IsNullOrEmpty(arguments) ? "" : " " + arguments)}");
        if (OnLaunch is { } launch) return launch(path);
        Running = true;
        return null;
    }
}

/// <summary>A fake desktop: one solid color plus painted rectangles. Thread-safe enough for one run.</summary>
internal sealed class FakeScreen(int width = 1920, int height = 1080) : IScreenCapture
{
    private readonly List<(int X, int Y, int W, int H, Rgb Color)> _rects = [];
    private readonly List<(int X, int Y, ImageTemplate T)> _pictures = [];
    public Rgb Background { get; set; } = new(20, 20, 30);
    public int Captures;
    /// <summary>Runs before each capture, so a test can change the screen as the macro runs.</summary>
    public Action<FakeScreen>? OnCapture { get; set; }

    public (int Left, int Top, int Width, int Height) Desktop => (0, 0, width, height);

    public void Paint(int x, int y, int w, int h, Rgb color) { lock (_rects) _rects.Add((x, y, w, h, color)); }
    public void Clear() { lock (_rects) { _rects.Clear(); _pictures.Clear(); } }
    public void Show(int x, int y, ImageTemplate t) { lock (_rects) _pictures.Add((x, y, t)); }

    public ScreenImage Capture(int left, int top, int w, int h)
    {
        Interlocked.Increment(ref Captures);
        OnCapture?.Invoke(this);
        var px = new byte[w * h * 4];
        lock (_rects)
        {
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var sx = left + x;
                var sy = top + y;
                var c = Background;
                foreach (var r in _rects)
                    if (sx >= r.X && sx < r.X + r.W && sy >= r.Y && sy < r.Y + r.H) c = r.Color;
                byte? gray = null;
                foreach (var p in _pictures)
                    if (sx >= p.X && sx < p.X + p.T.Width && sy >= p.Y && sy < p.Y + p.T.Height) gray = p.T.Gray[(sy - p.Y) * p.T.Width + (sx - p.X)];
                var i = (y * w + x) * 4;
                if (gray is { } g) { px[i] = px[i + 1] = px[i + 2] = g; }
                else { px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; }
                px[i + 3] = 255;
            }
        }
        return new ScreenImage(left, top, w, h, px);
    }
}

/// <summary>Returns whatever lines the test set, clipped to the captured area.</summary>
internal sealed class FakeText : ITextReader
{
    public List<TextLine> Lines { get; } = [];
    public int Reads;

    public void Say(string text, int x, int y)
    {
        var words = new List<TextWord>();
        foreach (var w in text.Split(' '))
        {
            words.Add(new TextWord(w, new ScreenRect(x, y, w.Length * 10, 16)));
            x += w.Length * 10 + 6;
        }
        lock (Lines) Lines.Add(new TextLine(words));
    }

    public Task<IReadOnlyList<TextLine>> ReadAsync(ScreenImage image, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Reads);
        lock (Lines)
        {
            IReadOnlyList<TextLine> inside = Lines.Where(l => l.Words.All(w =>
                w.Bounds.Left >= image.Left && w.Bounds.Top >= image.Top
                && w.Bounds.Right <= image.Left + image.Width && w.Bounds.Bottom <= image.Top + image.Height)).ToList();
            return Task.FromResult(inside);
        }
    }
}
