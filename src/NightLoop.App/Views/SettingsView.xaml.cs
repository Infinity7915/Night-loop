using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NightLoop.App.ViewModels;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) Macros?.CancelShortcutCapture();
        };
    }

    private MainViewModel? Macros => (DataContext as SettingsViewModel)?.Shell.Macros;

    /// <summary>Picking an editor shortcut: the next key combo pressed becomes it.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Macros is not { CapturingShortcut: not null } vm) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (KeyCatalog.IsModifier(vk) || key is Key.None or Key.ImeProcessed) return; // wait for the real key
        e.Handled = true;
        if (KeyCatalog.NameOf(vk) is null)
        {
            vm.CancelShortcutCapture();
            return;
        }
        vm.OnShortcutCaptured(new Hotkey(MacrosView.CurrentModifiers(), vk));
    }
}
