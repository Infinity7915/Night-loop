using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightLoop.Client;
using NightLoop.Contracts.Api;

namespace NightLoop.App.ViewModels;

/// <summary>
/// Settings → Account (schema sections 4 and 5): change username, email and password, two-factor settings,
/// delete the account, sign out. Macros never leave the server, so there is no data download here. Every change asks for the current password; email and password
/// changes also need a code sent by email. The server enforces all of it.
/// </summary>
public sealed partial class AccountViewModel : ObservableObject
{
    private readonly AccountSession _session;
    private readonly Func<Task> _signOut;

    public AccountViewModel(AccountSession session, Func<Task> signOut)
    {
        _session = session;
        _signOut = signOut;
        session.UserChanged += _ => OnUi(RefreshUser);
    }

    private UserDto? User => _session.User;

    public string Username => User?.Username ?? "";
    public string Email => User?.Email ?? "";

    public string UsernameHint => User switch
    {
        null => "",
        { UsernameIsDefault: true } => "Default name · pick your own — your first pick is free",
        { UsernameChangesLeft: 0 } => "No username changes left",
        { UsernameChangesLeft: var n } => $"{n} of {AccountRules.MaxUsernameChanges} changes left",
    };

    public bool UsernameIsDefault => User?.UsernameIsDefault == true;
    /// <summary>The username hint shows in amber for a default name or the last change.</summary>
    public bool UsernameHintWarns => User is { UsernameIsDefault: true } or { UsernameChangesLeft: <= 1 };
    public bool CanChangeUsername => User?.UsernameChangesLeft > 0;

    /// <summary>Bound to the switch. Changing it asks for the password first, then saves.</summary>
    public bool TwofaEnabled
    {
        get => User?.TwofaEnabled ?? true;
        set
        {
            if (User is null || value == User.TwofaEnabled) return;
            OpenSecurity(value, User.TwofaMode);
        }
    }

    public string TwofaMode
    {
        get => User?.TwofaMode ?? TwofaModes.EverySignin;
        set
        {
            if (User is null || value == User.TwofaMode || value is not (TwofaModes.EverySignin or TwofaModes.NewDevices)) return;
            OpenSecurity(User.TwofaEnabled, value);
        }
    }

    [ObservableProperty] private AccountDialog? _dialog;
    [ObservableProperty] private string _notice = "";

    private void RefreshUser()
    {
        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(Email));
        OnPropertyChanged(nameof(UsernameHint));
        OnPropertyChanged(nameof(UsernameIsDefault));
        OnPropertyChanged(nameof(UsernameHintWarns));
        OnPropertyChanged(nameof(CanChangeUsername));
        OnPropertyChanged(nameof(TwofaEnabled));
        OnPropertyChanged(nameof(TwofaMode));
    }

    // ---- Opening dialogs ----

    [RelayCommand]
    private void ChangeUsername() => Open(new AccountDialog(AccountDialogKind.Username)
    {
        Title = "Change username",
        Intro = UsernameIsDefault
            ? "Pick a name others will see. Your first pick is free; after that you have a few changes."
            : $"You have {User?.UsernameChangesLeft} change(s) left.",
        NewValueLabel = "New username",
        NewValue = UsernameIsDefault ? "" : Username,
        ConfirmText = "Save username",
    });

    [RelayCommand]
    private void ChangeEmail() => Open(new AccountDialog(AccountDialogKind.Email)
    {
        Title = "Change email",
        Intro = "We'll send a code to the new address. Your old address gets a notice once it's changed.",
        NewValueLabel = "New email",
        ConfirmText = "Send code",
    });

    [RelayCommand]
    private void ChangePassword() => Open(new AccountDialog(AccountDialogKind.Password)
    {
        Title = "Change password",
        Intro = $"Use at least {AccountRules.PasswordMinLength} characters. We'll email you a code to confirm. Other PCs get signed out.",
        ConfirmText = "Send code",
    });

    private void OpenSecurity(bool enabled, string mode)
    {
        Open(new AccountDialog(AccountDialogKind.Security)
        {
            Title = enabled ? "Two-factor authentication" : "Turn off two-factor?",
            Intro = !enabled
                ? "Without it, your password alone signs you in. Enter your password to turn it off."
                : mode == TwofaModes.NewDevices
                    ? "You'll get a code only when signing in on a PC we haven't seen. Enter your password to save."
                    : "You'll get a code every time you sign in. Enter your password to save.",
            ConfirmText = enabled ? "Save" : "Turn off",
            PendingTwofaEnabled = enabled,
            PendingTwofaMode = mode,
        });
        RefreshUser(); // the switch snaps back until the change is confirmed
    }

    [RelayCommand]
    private void DeleteAccount() => Open(new AccountDialog(AccountDialogKind.Delete)
    {
        Title = "Delete account",
        Intro = "Your account and every macro on it are removed. You have 30 days to change your mind by contacting support; after that it's gone for good.",
        ConfirmText = "Delete my account",
        IsDanger = true,
    });

    private void Open(AccountDialog dialog)
    {
        Notice = "";
        Dialog = dialog;
    }

    [RelayCommand]
    private void CancelDialog() => Dialog = null;

    // ---- Submitting ----

    [RelayCommand]
    private async Task SubmitDialogAsync()
    {
        if (Dialog is not { IsBusy: false } d) return;
        d.Error = "";
        var problem = d.IsCodeStage ? CodeProblem(d) : FormProblem(d);
        if (problem is not null)
        {
            d.Error = problem;
            return;
        }

        d.IsBusy = true;
        try
        {
            var api = _session.Api;
            switch (d.Kind)
            {
                case AccountDialogKind.Username:
                    Finish(await api.ChangeUsernameAsync(d.NewValue.Trim(), d.CurrentPassword), "Username changed.");
                    break;
                case AccountDialogKind.Email when !d.IsCodeStage:
                    d.ToCodeStage((await api.StartEmailChangeAsync(d.NewValue.Trim(), d.CurrentPassword)).ChallengeId,
                        $"Enter the 6-digit code we sent to {d.NewValue.Trim()}.");
                    break;
                case AccountDialogKind.Email:
                    Finish(await api.FinishEmailChangeAsync(d.ChallengeId!.Value, d.Code.Trim()), "Email changed.");
                    break;
                case AccountDialogKind.Password when !d.IsCodeStage:
                    d.ToCodeStage((await api.StartPasswordChangeAsync(d.CurrentPassword, d.NewPassword)).ChallengeId,
                        $"Enter the 6-digit code we sent to {Email}.");
                    break;
                case AccountDialogKind.Password:
                    var token = await api.FinishPasswordChangeAsync(d.ChallengeId!.Value, d.Code.Trim());
                    _session.ReplaceToken(token);
                    Dialog = null;
                    Notice = "Password changed. Other PCs were signed out.";
                    break;
                case AccountDialogKind.Security:
                    Finish(await api.UpdateSecurityAsync(new SecurityRequest(d.PendingTwofaEnabled, d.PendingTwofaMode, d.CurrentPassword)),
                        d.PendingTwofaEnabled ? "Two-factor settings saved." : "Two-factor is off.");
                    break;
                case AccountDialogKind.Delete:
                    await api.DeleteAccountAsync(d.CurrentPassword);
                    Dialog = null;
                    await _signOut();
                    break;
            }
        }
        catch (ApiException ex)
        {
            d.Error = ex.Message;
        }
        catch (ServerUnreachableException ex)
        {
            d.Error = ex.Message;
        }
        finally
        {
            d.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ResendCodeAsync()
    {
        if (Dialog is not { ChallengeId: { } id, IsBusy: false } d) return;
        d.IsBusy = true;
        try
        {
            d.ChallengeId = await _session.ResendCodeAsync(id);
            d.Info = "We sent a new code. The old one no longer works.";
            d.Error = "";
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            d.Error = ex.Message;
        }
        finally
        {
            d.IsBusy = false;
        }
    }

    [RelayCommand]
    private Task SignOutAsync() => _signOut();

    private void Finish(UserDto user, string notice)
    {
        _session.UpdateUser(user);
        Dialog = null;
        Notice = notice;
    }

    private string? FormProblem(AccountDialog d)
    {
        if (d.CurrentPassword.Length == 0) return "Enter your current password.";
        switch (d.Kind)
        {
            case AccountDialogKind.Username:
                if (string.Equals(d.NewValue.Trim(), Username, StringComparison.Ordinal)) return "That's already your username.";
                return AccountRules.UsernameProblem(d.NewValue.Trim())?.Message;
            case AccountDialogKind.Email:
                if (!AccountRules.IsValidEmail(d.NewValue.Trim())) return "Enter a valid email address.";
                if (string.Equals(d.NewValue.Trim(), Email, StringComparison.OrdinalIgnoreCase)) return "That's already your email.";
                return null;
            case AccountDialogKind.Password:
                if (AccountRules.PasswordProblem(d.NewPassword, Email) is { } p) return p;
                if (d.NewPassword != d.ConfirmPassword) return "The new passwords don't match.";
                if (d.NewPassword == d.CurrentPassword) return "Pick a password you haven't been using.";
                return null;
            default:
                return null;
        }
    }

    private static string? CodeProblem(AccountDialog d) =>
        AccountRules.IsValidCode(d.Code.Trim()) ? null : $"Enter the {AccountRules.CodeLength}-digit code from the email.";

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

public enum AccountDialogKind { Username, Email, Password, Security, Delete }

/// <summary>The small sheet that opens over Settings for one account change.</summary>
public sealed partial class AccountDialog(AccountDialogKind kind) : ObservableObject
{
    public AccountDialogKind Kind { get; } = kind;
    public string Title { get; init; } = "";
    public string Intro { get; init; } = "";
    public string NewValueLabel { get; init; } = "";
    public string ConfirmText { get; init; } = "Save";
    public bool IsDanger { get; init; }
    public bool PendingTwofaEnabled { get; init; }
    public string PendingTwofaMode { get; init; } = TwofaModes.EverySignin;

    public bool ShowNewValue => Kind is AccountDialogKind.Username or AccountDialogKind.Email && !IsCodeStage;
    public bool ShowNewPassword => Kind == AccountDialogKind.Password && !IsCodeStage;
    public bool ShowCurrentPassword => !IsCodeStage;

    [ObservableProperty] private string _newValue = "";
    [ObservableProperty] private string _currentPassword = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private Guid? _challengeId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNewValue), nameof(ShowNewPassword), nameof(ShowCurrentPassword), nameof(SubmitText))]
    private bool _isCodeStage;

    public string SubmitText => IsCodeStage ? "Confirm" : ConfirmText;

    internal void ToCodeStage(Guid challengeId, string info)
    {
        ChallengeId = challengeId;
        Info = info;
        Code = "";
        IsCodeStage = true;
    }
}
