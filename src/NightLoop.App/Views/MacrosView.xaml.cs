using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NightLoop.App.ViewModels;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.App.Views;

/// <summary>The block editor and the account's saved macros. DataContext is a MainViewModel.</summary>
public partial class MacrosView : UserControl
{
    public MacrosView()
    {
        InitializeComponent();
        // Keys are caught on the whole window, so the shortcuts work wherever the keyboard focus happens to be
        // (after clicking a row, nothing inside this page may have it).
        Loaded += (_, _) => HookWindow(Window.GetWindow(this));
        Unloaded += (_, _) => HookWindow(null);
        PreviewMouseDown += OnPreviewMouseDown;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) Vm?.CancelDeleteConfirms();
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.RevealRequested -= Reveal;
            if (e.NewValue is MainViewModel now) now.RevealRequested += Reveal;
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;
    private Window? _window;

    private void HookWindow(Window? window)
    {
        if (_window == window) return;
        if (_window is not null)
        {
            _window.PreviewKeyDown -= OnPreviewKeyDown;
            _window.Deactivated -= OnWindowDeactivated;
        }
        _window = window;
        if (_window is not null)
        {
            _window.PreviewKeyDown += OnPreviewKeyDown;
            _window.Deactivated += OnWindowDeactivated;
        }
    }

    /// <summary>Switching to another window (or the game) puts an armed "Delete?" back.</summary>
    private void OnWindowDeactivated(object? sender, EventArgs e) => Vm?.CancelDeleteConfirms();

    /// <summary>
    /// Any click puts an armed "Delete?" back, unless it's on that same button (the second press that deletes).
    /// </summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm) return;
        object? keep = null;
        if (FindParent<Button>(e.OriginalSource as DependencyObject) is { } button)
        {
            keep = button.DataContext switch
            {
                SavedMacroRow row when button.Command == row.DeleteCommand => row,
                GroupSection section when button.Command == section.DeleteCommand => section,
                _ => null,
            };
        }
        vm.CancelDeleteConfirms(keep);
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    /// <summary>Clicking a row moves the keyboard focus off any field, so Ctrl+C, Ctrl+V, Ctrl+Z and Delete act on blocks.</summary>
    private static void FocusRow(object sender)
    {
        var row = sender as DependencyObject;
        while (row is not null and not FrameworkElement { Name: "Row" })
            row = System.Windows.Media.VisualTreeHelper.GetParent(row);
        if (row is UIElement element) element.Focus();
    }

    /// <summary>Scrolls a timeline row into sight once it's laid out (after recording, the new blocks).</summary>
    private void Reveal(int index) => Dispatcher.BeginInvoke(() =>
    {
        if (Timeline.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row) row.BringIntoView();
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BlockRow row } || Vm is not { } vm) return;
        FocusRow(sender);
        // Shift+click on a row ticks the whole run from the last tick, like Shift+click on its tick box.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && vm.HasChecked) vm.CheckRow(row, shift: true);
        else vm.SelectBlockCommand.Execute(row);
    }

    /// <summary>The tick box selects the row for Move up / Move down / Delete; the view model decides, not the box.</summary>
    private void OnRowCheckDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        FocusRow(sender);
        if (sender is FrameworkElement { DataContext: BlockRow row } && Vm is { } vm)
            vm.CheckRow(row, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    /// <summary>Keeps the click on the tick box from also highlighting the row.</summary>
    private void OnRowCheckUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || !IsVisible) return;

        // A key press puts an armed "Delete?" back, unless it's Enter or Space on that button (which presses it).
        var onDelete = Keyboard.FocusedElement is Button { DataContext: SavedMacroRow or GroupSection } && e.Key is Key.Enter or Key.Space;
        if (!onDelete) vm.CancelDeleteConfirms();

        // Capturing a start/stop key for the loaded macro or a saved one, or the record key.
        if (vm.CapturingShortcut is not null) return; // Settings is picking an editor shortcut
        if (vm.IsCapturingKey)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var vk = KeyInterop.VirtualKeyFromKey(key);
            if (KeyCatalog.IsModifier(vk) || key is Key.None or Key.ImeProcessed) return; // wait for the real key
            e.Handled = true;

            Hotkey? hotkey = null;
            var clear = false;
            if (key is Key.Back or Key.Delete) clear = true;
            else if (key != Key.Escape && KeyCatalog.NameOf(vk) is not null) hotkey = new Hotkey(CurrentModifiers(), vk);

            if (vm.IsCapturingRecordHotkey) vm.OnRecordHotkeyCaptured(hotkey, clear);
            else if (vm.IsCapturingEditorHotkey) vm.OnEditorHotkeyCaptured(hotkey, clear);
            else vm.CapturingRow?.OnCaptured(hotkey, clear);
            return;
        }

        // Esc first clears the ticks, then lets go of the highlighted block, so Play runs from the top and Record
        // adds at the end.
        if (e.Key == Key.Escape && (vm.HasChecked || vm.HasSelection) && !IsTyping(e.OriginalSource))
        {
            if (vm.HasChecked) vm.ClearChecked();
            else vm.ClearSelection();
            e.Handled = true;
            return;
        }

        // Ctrl+A ticks every block, unless you're typing in a field.
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && !IsTyping(e.OriginalSource))
        {
            vm.CheckAllCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Undo, redo, copy, cut, paste and delete, with the keys from Settings › Macros. In a text field the keys
        // keep their usual meaning there.
        if (IsTyping(e.OriginalSource)) return;
        var pressed = e.Key == Key.System ? e.SystemKey : e.Key;
        var code = KeyInterop.VirtualKeyFromKey(pressed);
        if (KeyCatalog.IsModifier(code) || pressed is Key.None or Key.ImeProcessed) return;
        if (vm.ShortcutAction(new Hotkey(CurrentModifiers(), code)) is not { } action) return;
        vm.RunShortcut(action);
        e.Handled = true;
    }

    internal static HotkeyModifiers CurrentModifiers()
    {
        var mods = HotkeyModifiers.None;
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Ctrl;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        return mods;
    }

    private static bool IsTyping(object source) => source is TextBoxBase or PasswordBox or ComboBox { IsEditable: true };

    private void OnEditorBodySizeChanged(object sender, SizeChangedEventArgs e) => FollowScroll();

    private void OnInspectorSizeChanged(object sender, SizeChangedEventArgs e) => FollowScroll();

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => FollowScroll();

    /// <summary>
    /// Block editor follows scrolling (Settings › Macros). When the timeline is taller than the page, the editor
    /// moves down its column by the same share the timeline has been scrolled: 0% scrolled keeps it at the top,
    /// 100% puts it at the bottom. Because it moves by that share of the spare room, it stays on screen the whole way.
    /// </summary>
    private void FollowScroll()
    {
        if (!IsLoaded || Vm is not { } vm || EditorBody.ActualHeight <= 0) return;
        var room = Inspector.ActualHeight - Inspector.Padding.Top - Inspector.Padding.Bottom - InspectorPanel.ActualHeight;
        var offset = 0.0;
        if (vm.EditorFollowsScroll && room > 1)
        {
            var top = EditorBody.TranslatePoint(new Point(0, 0), Scroller).Y; // < 0 once the timeline's top scrolls away
            var travel = EditorBody.ActualHeight - Scroller.ViewportHeight;
            var share = travel > 1 ? Math.Clamp(-top / travel, 0, 1) : 0;
            offset = Math.Round(share * room);
        }
        if (Math.Abs(InspectorShift.Y - offset) >= 0.5) InspectorShift.Y = offset;
    }
}
