using CommunityToolkit.Mvvm.ComponentModel;

namespace NightLoop.App.ViewModels;

/// <summary>
/// Settings: Updates and the account for now. Appearance, target games and account changes (schema section 8)
/// come with milestone M8.
/// </summary>
public sealed class SettingsViewModel(ShellViewModel shell) : ObservableObject
{
    public ShellViewModel Shell => shell;
    public UpdatesViewModel Updates => shell.Updates;
}
