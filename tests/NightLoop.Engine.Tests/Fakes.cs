using NightLoop.Engine.Input;
using NightLoop.Engine.Runtime;
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

    public bool IsProcessRunning(string exeName) => true;
    public ClientArea? GetClientArea(string exeName) => Area;
    public bool Resize(string exeName, int width, int height) => true;
    public bool MoveTo(string exeName, int x, int y) => true;
    public bool Focus(string exeName) => true;
}
