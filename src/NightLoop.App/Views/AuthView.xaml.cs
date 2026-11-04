using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using NightLoop.App.ViewModels;

namespace NightLoop.App.Views;

/// <summary>Sign in, create account, code and password reset. PasswordBox can't bind, so it's passed along here.</summary>
public partial class AuthView : UserControl
{
    public AuthView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is AuthViewModel old) old.PropertyChanged -= OnVmChanged;
            if (e.NewValue is AuthViewModel vm) vm.PropertyChanged += OnVmChanged;
        };
    }

    private AuthViewModel? Vm => DataContext as AuthViewModel;

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.Password = PasswordBox.Password;
    }

    private void OnConfirmChanged(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.ConfirmPassword = ConfirmBox.Password;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AuthViewModel.Mode) || Vm is not { } vm) return;
        // New screen: clear typed passwords and put the cursor where it's needed.
        PasswordBox.Clear();
        ConfirmBox.Clear();
        Dispatcher.BeginInvoke(() =>
        {
            if (vm.IsCode || vm.IsReset) CodeBox.Focus();
            else PasswordBox.Focus();
        });
    }

    /// <summary>The blue terms line: opens the official Terms of Service on GitHub in the browser.</summary>
    private void OnTermsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Updates.GitHubRepo.TermsUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"Couldn't open the browser. The terms are at {Updates.GitHubRepo.TermsUrl}", "Night-loop");
        }
    }
}
