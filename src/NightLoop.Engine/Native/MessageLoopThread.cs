using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NightLoop.Engine.Native;

/// <summary>
/// A background thread with a Win32 message loop. RegisterHotKey and low-level hooks both deliver to the
/// thread that set them up, and that thread must keep pumping messages.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed class MessageLoopThread : IDisposable
{
    private const uint WakeMessage = PInvoke.WM_APP + 1;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Action<MSG> _onMessage;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;

    public MessageLoopThread(string name, Action<MSG> onMessage)
    {
        _onMessage = onMessage;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Runs <paramref name="func"/> on the loop thread and returns its result.</summary>
    public T Invoke<T>(Func<T> func)
    {
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId) return func();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        PInvoke.PostThreadMessage(_threadId, WakeMessage, 0, 0);
        return tcs.Task.GetAwaiter().GetResult();
    }

    public void Invoke(Action action) => Invoke(() => { action(); return true; });

    private void Run()
    {
        _threadId = PInvoke.GetCurrentThreadId();
        // Touch the queue so PostThreadMessage works before the first GetMessage.
        PInvoke.PeekMessage(out _, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
        _ready.Set();

        while (PInvoke.GetMessage(out var msg, default, 0, 0) > 0)
        {
            if (msg.message == WakeMessage)
            {
                while (_work.TryDequeue(out var work)) work();
                continue;
            }
            _onMessage(msg);
            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }
    }

    public void Dispose()
    {
        if (_thread.IsAlive)
        {
            PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        _ready.Dispose();
    }
}
