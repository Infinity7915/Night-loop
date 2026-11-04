using Forms = System.Windows.Forms;

namespace NightLoop.App.Services;

/// <summary>
/// Night-loop's icon in the Windows notification area (system tray). Closing the window can hide Night-loop
/// there so hotkeys and macros keep running; right-click › Exit quits for real. The Notify block's messages go out
/// through it as Windows notifications (Windows 10 and 11 show tray balloons as regular notifications in the
/// Action Center / notification center).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    /// <summary>The app's one tray icon, or null before the window opens (and in the CI smoke test).</summary>
    public static TrayIcon? Current { get; private set; }

    private TrayIcon(Action open, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Night-loop", null, (_, _) => open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Night-loop",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
        _icon.BalloonTipClicked += (_, _) => open();
    }

    /// <param name="open">Shows the window again (tray click, Open, a click on a notification).</param>
    /// <param name="exit">Quits Night-loop (tray menu › Exit).</param>
    public static TrayIcon Create(Action open, Action exit)
    {
        Current?.Dispose();
        return Current = new TrayIcon(open, exit);
    }

    /// <summary>Shows a Windows notification. Returns false when there's no tray icon to send it from.</summary>
    public static bool Notify(string title, string message)
    {
        if (Current is not { } tray) return false;
        tray._icon.ShowBalloonTip(5000, title, message.Length == 0 ? " " : message, Forms.ToolTipIcon.None);
        return true;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("NightLoop.icon.ico");
        return stream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(stream);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
