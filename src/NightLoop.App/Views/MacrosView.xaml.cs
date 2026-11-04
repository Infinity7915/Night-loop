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
        PreviewKeyDown += OnPreviewKeyDown;
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.RevealRequested -= Reveal;
            if (e.NewValue is MainViewModel now) now.RevealRequested += Reveal;
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>Scrolls a timeline row into sight once it's laid out (after recording, the new blocks).</summary>
    private void Reveal(int index) => Dispatcher.BeginInvoke(() =>
    {
        if (Timeline.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row) row.BringIntoView();
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BlockRow row } || Vm is not { } vm) return;
        // Shift+click on a row ticks the whole run from the last tick, like Shift+click on its tick box.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && vm.HasChecked) vm.CheckRow(row, shift: true);
        else vm.SelectBlockCommand.Execute(row);
    }

    /// <summary>The tick box selects the row for Move up / Move down / Delete; the view model decides, not the box.</summary>
    private void OnRowCheckDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: BlockRow row } && Vm is { } vm)
            vm.CheckRow(row, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    /// <summary>Keeps the click on the tick box from also highlighting the row.</summary>
    private void OnRowCheckUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        // Capturing a start/stop key for the loaded macro or a saved one, or the record key.
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

        // Delete removes the selected block, unless you're typing in a field.
        if (e.Key == Key.Delete && !IsTyping(e.OriginalSource))
        {
            vm.DeleteSelected();
            e.Handled = true;
        }
    }

    private static HotkeyModifiers CurrentModifiers()
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
