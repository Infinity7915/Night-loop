using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightLoop.App.Controls;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Blocks;
using NightLoop.Engine.Runtime;
using NightLoop.Engine.Screen;

namespace NightLoop.App.ViewModels;

/// <summary>A button under the inspector fields, like "Pick point on screen".</summary>
public sealed record InspectorTool(string Label, string Kind);

/// <summary>
/// The editor's helpers: the starter sequence for new macros, the inspector's screen tools (pick a point, region,
/// color or picture; use the window's current size or position) and the toast that replaces a status bar.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly DispatcherTimer _toastTimer;
    /// <summary>A new macro nobody has touched yet: changing the target game rebuilds it instead of saving it.</summary>
    private bool _pristine;

    [ObservableProperty] private bool _isToastVisible;
    /// <summary>The picture the selected block looks for, as Night-loop compares it (grayscale). Null when it has none.</summary>
    [ObservableProperty] private ImageSource? _inspectorImage;
    [ObservableProperty] private string _inspectorImageText = "";
    /// <summary>Find &amp; click / Scan &amp; turn with a picture: the click spot can be set on the preview.</summary>
    [ObservableProperty] private bool _inspectorCanSetClickSpot;
    /// <summary>Where the click spot is in the preview, 0–1 across and down.</summary>
    [ObservableProperty] private double _inspectorClickX = 0.5;
    [ObservableProperty] private double _inspectorClickY = 0.5;
    /// <summary>"You've reached the limit…" after pressing New macro with every slot used.</summary>
    [ObservableProperty] private bool _showLimit;

    public string LimitText => $"You've reached the limit of {_limit} macros, including drafts. The server won't accept another until you delete one.";
    public bool IsAtLimit => _rows.Count >= _limit;

    partial void OnStatusChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            HideToast();
            return;
        }
        IsToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(3 + value.Length / 20.0, 4, 10));
        _toastTimer.Start();
    }

    [RelayCommand]
    private void HideToast()
    {
        _toastTimer.Stop();
        IsToastVisible = false;
    }

    // ---------------- Starter sequence ----------------

    /// <summary>
    /// The ready-made blocks every new macro starts with (Settings › Macros turns them off): line the game window up,
    /// a labelled loop to fill in, then a notification. Returns the block to select (inside the loop), so Add block
    /// puts new blocks there.
    /// </summary>
    private int AddStarterBlocks(MacroDoc doc)
    {
        var game = SelectedGame.Exe is null ? null : SelectedGame.Name;
        var n = 0;
        void Add(string type, JsonObject? p = null) => doc.Blocks.Add(Block.Create($"b{++n}", type, p));

        if (game is not null)
        {
            Add(BlockTypes.WindowFocus);
            Add(BlockTypes.WindowResize, new() { ["width"] = 1280, ["height"] = 720 });
            Add(BlockTypes.WindowPosition, new() { ["monitor"] = 1, ["anchor"] = "topLeft", ["x"] = 0, ["y"] = 0 });
        }
        Add(BlockTypes.Description, new()
        {
            ["text"] = game is null
                ? "What this macro does. Add blocks inside the loop."
                : $"What this macro does. Focus, Resize and Position run first so every click lands in the same place in {game} each run.\nAdd blocks inside the loop.",
        });
        Add(BlockTypes.Label, new() { ["name"] = "start" });
        Add(BlockTypes.Loop, new() { ["count"] = 10 });
        Add(BlockTypes.WaitRandom, new() { ["minMs"] = 400, ["maxMs"] = 900 });
        var inside = doc.Blocks.Count - 1;
        Add(BlockTypes.End);
        Add(BlockTypes.Notify, new() { ["kind"] = "both", ["message"] = "Macro finished" });
        return inside;
    }

    // ---------------- Inspector tools ----------------

    private static IEnumerable<InspectorTool> ToolsFor(Block b)
    {
        var fields = BlockCatalog.For(b.Type)?.Fields ?? [];
        var target = b.GetString("target", "text");
        var finds = b.Type is BlockTypes.FindClick or BlockTypes.ScanTurn;
        var condition = b.Type is BlockTypes.If or BlockTypes.RepeatUntil
            ? Conditions.Read(b.GetObject("condition")).Kind.TrimStart('!')
            : "";
        var hasImage = ImageDataOf(b) is { Length: > 0 };

        if (b.Type is BlockTypes.WaitForImage or BlockTypes.WaitForImageGone || (finds && target == "image") || condition == "imageVisible")
            yield return new(hasImage ? "Capture the picture again" : "Capture image from screen", "image");
        if (finds && target == "image" && hasImage && ClicksPicture(b))
        {
            yield return new("Set click spot in picture", "clickspot");
            yield return new("Set click spot on screen", "clickscreen");
        }
        if (fields.Any(f => f.Key == "region") || condition is "textVisible" or "imageVisible")
            yield return new("Select region on screen", "region");
        if (fields.Any(f => f.Key == "color") || (finds && target == "pixel") || condition == "pixelColor")
            yield return new("Pick color from screen", "color");
        if (b.Type is BlockTypes.Click or BlockTypes.Move) yield return new("Pick point on screen", "point");
        if (b.Type == BlockTypes.WindowResize) yield return new("Use current window size", "size");
        if (b.Type == BlockTypes.WindowPosition) yield return new("Use current window position", "position");
        if (DetectProbe.CanTest(b)) yield return new(b.Type is BlockTypes.If or BlockTypes.RepeatUntil ? "Check it on screen now" : "Test on screen now", "test");
    }

    /// <summary>Whether this block clicks (or turns toward) the picture it finds, so its click spot matters.</summary>
    private static bool ClicksPicture(Block b) =>
        b.Type == BlockTypes.FindClick || (b.Type == BlockTypes.ScanTurn && b.GetString("whenFound", "click") != "continue");

    /// <summary>The saved picture of a picture block or an "Image visible" condition.</summary>
    private static string? ImageDataOf(Block b) =>
        b.Type is BlockTypes.If or BlockTypes.RepeatUntil
            ? Conditions.Inner(b.GetObject("condition"))?["imageData"] is JsonValue v && v.TryGetValue(out string? s) ? s : null
            : b.GetString("imageData");

    /// <summary>The inspector's little preview of the picture a block looks for.</summary>
    private void ShowImagePreview(Block? b)
    {
        InspectorImage = null;
        InspectorImageText = "";
        InspectorCanSetClickSpot = false;
        if (b is null || ImageDataOf(b) is not { Length: > 0 } data) return;
        if (b.Type is BlockTypes.FindClick or BlockTypes.ScanTurn && b.GetString("target", "text") != "image") return;
        if (!ImageTemplate.TryDecode(data, out var t) || t is null)
        {
            InspectorImageText = "The saved picture is damaged. Capture it again.";
            return;
        }
        var bitmap = BitmapSource.Create(t.Width, t.Height, 96, 96, PixelFormats.Gray8, null, t.Gray, t.Width);
        bitmap.Freeze();
        InspectorImage = bitmap;
        InspectorImageText = t.HasColor
            ? $"Looking for this picture · {t.Width}×{t.Height}, at any size. With Match colors on, its colors have to match too."
            : $"Looking for this picture · {t.Width}×{t.Height}, at any size. It was captured before colors were saved, so only its shape counts. Capture it again to match colors.";
        if (b.Type is BlockTypes.FindClick or BlockTypes.ScanTurn && ClicksPicture(b))
        {
            (InspectorClickX, InspectorClickY) = ClickSpot.Read(b);
            InspectorCanSetClickSpot = true;
            InspectorImageText += $" {ClickSpotPicker.Describe(InspectorClickX, InspectorClickY)} (the dot). Click the picture to move it.";
        }
    }

    [RelayCommand]
    private async Task UseToolAsync(InspectorTool tool)
    {
        if (SelectedBlock is not { } row || IsPlaying) return;
        var b = row.Block;
        try
        {
            var changed = tool.Kind switch
            {
                "size" => UseWindowSize(b),
                "position" => UseWindowPosition(b),
                "point" => await PickPointAsync(b),
                "color" => await PickColorAsync(b),
                "region" => await PickRegionAsync(b),
                "image" => await CaptureImageAsync(b),
                "test" => await TestOnScreenAsync(b),
                "clickspot" => PickClickSpot(b),
                "clickscreen" => await PickClickSpotOnScreenAsync(b),
                _ => false,
            };
            if (!changed) return;
            row.Refresh();
            OnRowChanged(row);
            BuildInspector();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            Status = $"Couldn't read the screen: {ex.Message}";
        }
    }

    /// <summary>Opens the picture big so the person can click the exact spot to click once it is found.</summary>
    private bool PickClickSpot(Block b)
    {
        if (InspectorImage is not BitmapSource picture) return false;
        var (x, y) = ClickSpot.Read(b);
        if (ClickSpotPicker.Pick(Application.Current?.MainWindow, picture, x, y) is not { } spot) return false;
        StoreClickSpot(b, spot.X, spot.Y);
        return true;
    }

    /// <summary>
    /// "Set click spot on screen": with the picture showing on screen, click anywhere (inside the picture or beside
    /// it); the spot is saved relative to the picture, so it follows it wherever it is found.
    /// </summary>
    private async Task<bool> PickClickSpotOnScreenAsync(Block b)
    {
        if (await PickAsync(PickMode.Point, "Click where to click once the picture is found (it must be on screen now)") is not { } r) return false;
        var spot = DetectProbe.SpotFromScreen(b, r.Shot, r.X, r.Y);
        if (!spot.Ok)
        {
            Status = spot.Message;
            return false;
        }
        StoreClickSpot(b, spot.X, spot.Y);
        return true;
    }

    /// <summary>The preview in the inspector was clicked: move the click spot there.</summary>
    public void SetClickSpot(double x, double y)
    {
        if (SelectedBlock is not { } row || IsPlaying || !InspectorCanSetClickSpot) return;
        StoreClickSpot(row.Block, x, y);
        row.Refresh();
        OnRowChanged(row);
        BuildInspector();
    }

    /// <summary>Saves the spot as whole percents; the middle is left blank, as blocks had it before there was a spot.</summary>
    private void StoreClickSpot(Block b, double x, double y)
    {
        var (px, py) = ClickSpot.Write(b, x, y);
        Status = ClickSpotPicker.Describe(px / 100.0, py / 100.0) + " once it is found.";
    }

    /// <summary>The process a window block works on: its own, else the target game.</summary>
    private string? WindowProcess(Block b) => b.GetString("process") is { Length: > 0 } p ? p : TargetExe;

    private bool UseWindowSize(Block b)
    {
        if (WindowProcess(b) is not { } exe) return NoGame();
        if (_windows.GetWindowBounds(exe) is not { } w) return NotRunning(exe);
        b.Params["width"] = w.Width;
        b.Params["height"] = w.Height;
        Status = $"Set to {exe}'s current size, {w.Width}×{w.Height}.";
        return true;
    }

    private bool UseWindowPosition(Block b)
    {
        if (WindowProcess(b) is not { } exe) return NoGame();
        if (_windows.GetWindowBounds(exe) is not { } w) return NotRunning(exe);
        var monitors = _windows.GetMonitors();
        var (cx, cy) = (w.Left + w.Width / 2, w.Top + w.Height / 2);
        var index = Math.Max(0, monitors.ToList().FindIndex(m => m.Contains(cx, cy)));
        var area = monitors.Count > 0 ? monitors[index] : new Engine.Window.ClientArea(0, 0, 0, 0);
        b.Params["monitor"] = Math.Min(index + 1, 16);
        b.Params["anchor"] = "custom";
        b.Params["x"] = w.Left - area.Left;
        b.Params["y"] = w.Top - area.Top;
        Status = $"Set to where {exe} is now.";
        return true;
    }

    private async Task<bool> PickPointAsync(Block b)
    {
        if (await PickAsync(PickMode.Point, "Click where the mouse should go") is not { } r) return false;
        if (ToBlockPoint(r.X, r.Y) is not { } p) return false;
        b.Params["x"] = p.X;
        b.Params["y"] = p.Y;
        b.Params["relativeTo"] = p.RelativeTo;
        if (b.Type == BlockTypes.Move)
        {
            b.Params["mode"] = "absolute";
            b.Params.Remove("path"); // a recorded path led to the old point
        }
        return true;
    }

    private async Task<bool> PickColorAsync(Block b)
    {
        if (await PickAsync(PickMode.Color, "Click the pixel whose color to use") is not { } r) return false;
        var hex = r.Color.ToString();
        if (b.Type is BlockTypes.FindClick or BlockTypes.ScanTurn)
        {
            b.Params["value"] = hex;
            return true;
        }
        if (ToBlockPoint(r.X, r.Y) is not { } p) return false;
        if (b.Type is BlockTypes.If or BlockTypes.RepeatUntil)
        {
            var (kind, _) = Conditions.Read(b.GetObject("condition"));
            var c = Conditions.Write(kind, $"{p.X}, {p.Y}, {hex}", b.GetObject("condition"));
            (c["kind"]?.GetValue<string>() == "not" ? (JsonObject)c["condition"]! : c)["relativeTo"] = p.RelativeTo;
            b.Params["condition"] = c;
            return true;
        }
        b.Params["x"] = p.X;
        b.Params["y"] = p.Y;
        b.Params["color"] = hex;
        b.Params["relativeTo"] = p.RelativeTo;
        return true;
    }

    private async Task<bool> PickRegionAsync(Block b)
    {
        if (await PickAsync(PickMode.Region, "Drag over the area to look in") is not { } r) return false;
        if (ToBlockPoint(r.X, r.Y) is not { } p) return false;
        var region = new JsonArray(p.X, p.Y, r.Width, r.Height);
        if (b.Type is BlockTypes.If or BlockTypes.RepeatUntil)
        {
            var c = b.GetObject("condition")?.DeepClone() as JsonObject;
            if (Conditions.Inner(c) is not { } inner) return false;
            inner["region"] = region;
            b.Params["condition"] = c;
        }
        else b.Params["region"] = region;
        Status = $"It now looks only in that {r.Width}×{r.Height} area, which is faster and avoids look-alikes elsewhere.";
        return true;
    }

    /// <summary>
    /// "Test on screen now": hides Night-loop, takes one look for what the block is set to find (no clicks, no
    /// turning, no waiting) and says what it saw.
    /// </summary>
    private async Task<bool> TestOnScreenAsync(Block b)
    {
        var window = Application.Current?.MainWindow;
        var exe = TargetExe;
        var wasVisible = window?.IsVisible == true;
        ProbeResult result;
        try
        {
            window?.Hide();
            if (exe is not null && _windows.IsProcessRunning(exe)) _windows.Focus(exe);
            await Task.Delay(350); // let the windows behind redraw first
            var env = new RunEnvironment
            {
                Input = new Engine.Input.Win32InputSender(), // not used: a test look sends no input
                Windows = _windows,
                Screen = _screen,
                Text = _runs.Text,
                IsKeyDown = Engine.Input.KeyState.IsDown,
            };
            result = await DetectProbe.LookOnceAsync(b, env, exe);
        }
        finally
        {
            if (wasVisible && window is not null)
            {
                window.Show();
                window.Activate();
            }
        }
        Status = result.Message;
        return false;
    }

    private async Task<bool> CaptureImageAsync(Block b)
    {
        if (await PickAsync(PickMode.Region, "Drag over the picture to look for (small and distinctive works best)") is not { } r) return false;
        var data = ImageTemplate.FromImage(r.Shot.Crop(r.X, r.Y, r.Width, r.Height)).Encode();
        if (data.Length > MacroLimits.ImageDataMaxLength)
        {
            Status = "That picture is too detailed to save. Drag over a smaller area.";
            return false;
        }
        var name = $"picture {r.Width}×{r.Height}";
        if (b.Type is BlockTypes.If or BlockTypes.RepeatUntil)
        {
            var c = b.GetObject("condition");
            var (kind, value) = Conditions.Read(c);
            var written = Conditions.Write(kind, value.Length > 0 ? value : name, c);
            var inner = written["kind"]?.GetValue<string>() == "not" ? (JsonObject)written["condition"]! : written;
            inner["imageData"] = data;
            b.Params["condition"] = written;
            return true;
        }
        b.Params["imageData"] = data;
        if (b.Type is BlockTypes.FindClick or BlockTypes.ScanTurn)
        {
            if (b.GetString("value") is not { Length: > 0 } || b.GetString("target") != "image") b.Params["value"] = name;
        }
        else if (b.GetString("image") is not { Length: > 0 })
        {
            b.Params["image"] = name;
        }
        Status = $"Saved a {r.Width}×{r.Height} picture in this block. Its preview is in the block editor.";
        return true;
    }

    /// <summary>
    /// A screen point as the block stores it: inside the target game's window when there is one (so it still lines
    /// up when the window moves), else screen pixels. Null (with a message) when the game isn't running.
    /// </summary>
    private (int X, int Y, string RelativeTo)? ToBlockPoint(int x, int y)
    {
        if (TargetExe is not { } exe) return (x, y, "screen");
        if (_windows.GetClientArea(exe) is { } a) return (x - a.Left, y - a.Top, "gameWindow");
        NotRunning(exe);
        return null;
    }

    /// <summary>
    /// Hides Night-loop, brings the game forward, then shows the picker over a frozen screenshot (or over the moving
    /// screen when Settings › Macros › Pick on the live screen is on).
    /// </summary>
    private async Task<PickResult?> PickAsync(PickMode mode, string hint)
    {
        var window = Application.Current?.MainWindow;
        var exe = TargetExe;
        if (exe is not null && !_windows.IsProcessRunning(exe))
        {
            NotRunning(exe);
            return null;
        }
        var wasVisible = window?.IsVisible == true;
        try
        {
            window?.Hide();
            if (exe is not null) _windows.Focus(exe);
            await Task.Delay(350); // let the windows behind redraw before the screenshot
            var accent = Application.Current?.TryFindResource("Accent") is SolidColorBrush brush ? brush.Color : Color.FromRgb(0xA7, 0x8B, 0xFA);
            return ScreenPicker.Pick(_screen, mode, hint, accent, _settings.LivePicking);
        }
        finally
        {
            if (wasVisible && window is not null)
            {
                window.Show();
                window.Activate();
            }
        }
    }

    private bool NoGame()
    {
        Status = "Pick a target game in Settings, or pick the program in this block.";
        return false;
    }

    private bool NotRunning(string exe)
    {
        Status = $"Start {exe} first, so the position lines up with its window.";
        return false;
    }
}
