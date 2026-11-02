using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightLoop.Client;
using NightLoop.Contracts;
using NightLoop.Contracts.Api;
using NightLoop.LocalData;

namespace NightLoop.App.ViewModels;

public enum AuthMode
{
    SignIn,
    SignUp,
    /// <summary>6-digit code after sign-in or sign-up.</summary>
    Code,
    /// <summary>Forgot password: enter the email.</summary>
    Forgot,
    /// <summary>Forgot password: code plus the new password.</summary>
    Reset,
}

/// <summary>What the emailed code on the code screen is for.</summary>
public enum CodeFor
{
    SignIn,
    SignUp,
    /// <summary>The account asks for a code every time Night-loop opens (Settings › Ask for a code › Every sign-in).</summary>
    Unlock,
}

/// <summary>
/// Sign in, create account, the emailed code, and forgot password (schema section 8). Errors show under the
/// field the server named. Passwords come from the PasswordBoxes in code-behind; WPF can't bind them.
/// </summary>
public sealed partial class AuthViewModel : ObservableObject
{
    private readonly AccountSession _session;
    private readonly LocalSettings _settings;
    private Guid _challengeId;
    private CodeFor _codeFor;
    private DateTimeOffset _resendAllowedAt;

    public AuthViewModel(AccountSession session, LocalSettings settings, string? notice)
    {
        _session = session;
        _settings = settings;
        _email = settings.LastEmail ?? "";
        _notice = notice ?? "";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignIn), nameof(IsSignUp), nameof(IsCode), nameof(IsForgot), nameof(IsReset), nameof(IsUnlock), nameof(BackText), nameof(Title), nameof(Subtitle))]
    private AuthMode _mode = AuthMode.SignIn;

    [ObservableProperty] private string _email;
    [ObservableProperty] private string _code = "";
    /// <summary>On by default: with "New devices only" (the default) a trusted PC isn't asked for a code again.</summary>
    [ObservableProperty] private bool _trustThisPc = true;
    [ObservableProperty] private bool _acceptedTerms;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _notice;
    [ObservableProperty] private string _emailError = "";
    [ObservableProperty] private string _passwordError = "";
    [ObservableProperty] private string _codeError = "";
    [ObservableProperty] private string _formError = "";

    public string Password { get; set; } = "";
    public string ConfirmPassword { get; set; } = "";

    public bool IsSignIn => Mode == AuthMode.SignIn;
    public bool IsSignUp => Mode == AuthMode.SignUp;
    public bool IsCode => Mode == AuthMode.Code;
    public bool IsForgot => Mode == AuthMode.Forgot;
    public bool IsReset => Mode == AuthMode.Reset;
    public bool CodeIsForSignIn => _codeFor == CodeFor.SignIn;
    public bool IsUnlock => Mode == AuthMode.Code && _codeFor == CodeFor.Unlock;
    public string BackText => IsUnlock ? "← Sign out" : "← Back to sign in";

    /// <summary>
    /// Opening Night-loop on an account that asks for a code every time: the code screen, already sent. Back signs
    /// out.
    /// </summary>
    public static AuthViewModel ForUnlock(AccountSession session, LocalSettings settings)
    {
        var vm = new AuthViewModel(session, settings, null) { Email = session.PendingUnlockEmail ?? settings.LastEmail ?? "" };
        vm.GoToCode(session.PendingUnlock ?? Guid.Empty, CodeFor.Unlock);
        if (session.PendingUnlockError is { } error)
        {
            vm.CodeError = error;
            vm._resendAllowedAt = DateTimeOffset.UtcNow;
        }
        return vm;
    }

    public string Title => Mode switch
    {
        AuthMode.SignUp => "Create your account",
        AuthMode.Code => _codeFor switch
        {
            CodeFor.SignUp => "Verify your email",
            CodeFor.Unlock => "Enter your code",
            _ => "Check your email",
        },
        AuthMode.Forgot or AuthMode.Reset => "Reset your password",
        _ => "Sign in to Night-loop",
    };

    public string Subtitle => Mode switch
    {
        AuthMode.SignUp => $"Your account starts as user#<number>; you can pick a username later. Passwords need {AccountRules.PasswordMinLength}+ characters.",
        AuthMode.Code when _codeFor == CodeFor.Unlock && _challengeId == Guid.Empty =>
            $"Your account asks for a code each time Night-loop opens, but one couldn't be sent to {Email}. Send a new code to try again.",
        AuthMode.Code when _codeFor == CodeFor.Unlock =>
            $"Your account asks for a code each time Night-loop opens. We sent a {AccountRules.CodeLength}-digit code to {Email}. It expires in {AccountRules.CodeLifetimeMinutes} minutes.",
        AuthMode.Code => $"We sent a {AccountRules.CodeLength}-digit code to {Email}. It expires in {AccountRules.CodeLifetimeMinutes} minutes.",
        AuthMode.Forgot => "Enter your email and we'll send a code.",
        AuthMode.Reset => $"Enter the code sent to {Email} and a new password.",
        _ => "Your macros live on your account and follow you to any PC.",
    };

    [RelayCommand]
    private void Show(AuthMode mode)
    {
        ClearErrors();
        Notice = "";
        Mode = mode;
    }

    [RelayCommand]
    private Task SignInAsync() => RunAsync(async () =>
    {
        _settings.LastEmail = Email.Trim();
        var result = await _session.SignInAsync(Email, Password);
        if (result is SignInResult.NeedsCode needs) GoToCode(needs.ChallengeId, CodeFor.SignIn);
        // SignedIn is handled by the shell (it switches to the Macros page).
    });

    [RelayCommand]
    private Task SignUpAsync() => RunAsync(async () =>
    {
        if (AccountRules.PasswordProblem(Password, Email) is { } problem) { PasswordError = problem; return; }
        if (Password != ConfirmPassword) { PasswordError = "The passwords don't match."; return; }
        if (!AcceptedTerms) { FormError = "Accept the terms to create an account."; return; }
        _settings.LastEmail = Email.Trim();
        GoToCode(await _session.SignUpAsync(Email, Password, AcceptedTerms), CodeFor.SignUp);
    });

    [RelayCommand]
    private Task VerifyCodeAsync() => RunAsync(async () =>
    {
        if (!AccountRules.IsValidCode(Code.Trim())) { CodeError = $"Enter the {AccountRules.CodeLength}-digit code."; return; }
        switch (_codeFor)
        {
            case CodeFor.SignUp:
                await _session.VerifySignUpAsync(_challengeId, Code);
                break;
            case CodeFor.Unlock:
                await _session.VerifyUnlockAsync(Code);
                break;
            default:
                await _session.VerifySignInAsync(_challengeId, Code, TrustThisPc);
                break;
        }
    });

    [RelayCommand]
    private Task ResendAsync() => RunAsync(async () =>
    {
        var wait = _resendAllowedAt - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) { CodeError = $"You can ask for a new code in {Math.Ceiling(wait.TotalSeconds)} s."; return; }
        var first = _challengeId == Guid.Empty;
        _challengeId = _codeFor == CodeFor.Unlock ? await _session.ResendUnlockAsync() : await _session.ResendCodeAsync(_challengeId);
        _resendAllowedAt = DateTimeOffset.UtcNow.AddSeconds(AccountRules.ResendCooldownSeconds);
        Notice = first ? $"Code sent to {Email}." : "New code sent. The old one no longer works.";
        OnPropertyChanged(nameof(Subtitle));
    });

    /// <summary>The link under the form: back to sign in, or sign out from the code asked for at launch.</summary>
    [RelayCommand]
    private Task BackAsync() => RunAsync(async () =>
    {
        if (IsUnlock) await _session.SignOutAsync(); // the shell shows sign-in
        else Show(AuthMode.SignIn);
    });

    [RelayCommand]
    private Task ForgotAsync() => RunAsync(async () =>
    {
        _challengeId = await _session.ForgotPasswordAsync(Email);
        _resendAllowedAt = DateTimeOffset.UtcNow.AddSeconds(AccountRules.ResendCooldownSeconds);
        Code = "";
        Mode = AuthMode.Reset;
    });

    [RelayCommand]
    private Task ResetAsync() => RunAsync(async () =>
    {
        if (AccountRules.PasswordProblem(Password, Email) is { } problem) { PasswordError = problem; return; }
        if (Password != ConfirmPassword) { PasswordError = "The passwords don't match."; return; }
        await _session.ResetPasswordAsync(_challengeId, Code, Password);
        Mode = AuthMode.SignIn;
        Notice = "Password changed. Sign in with your new password.";
    });

    private void GoToCode(Guid challengeId, CodeFor codeFor)
    {
        _challengeId = challengeId;
        _codeFor = codeFor;
        _resendAllowedAt = DateTimeOffset.UtcNow.AddSeconds(AccountRules.ResendCooldownSeconds);
        Code = "";
        Mode = AuthMode.Code;
        OnPropertyChanged(nameof(CodeIsForSignIn));
        OnPropertyChanged(nameof(IsUnlock));
        OnPropertyChanged(nameof(BackText));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        ClearErrors();
        IsBusy = true;
        try
        {
            await action();
        }
        catch (ApiException ex)
        {
            ShowError(ex);
        }
        catch (ServerUnreachableException ex)
        {
            FormError = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            CodeError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowError(ApiException ex)
    {
        switch (ex.Code)
        {
            case ErrorCodes.InvalidEmail:
                EmailError = ex.MessageFor("email");
                break;
            case ErrorCodes.WeakPassword:
                PasswordError = ex.Fields?.Values.FirstOrDefault() ?? ex.Message;
                break;
            case ErrorCodes.InvalidCode or ErrorCodes.CodeExpired or ErrorCodes.TooManyAttempts:
                CodeError = ex.Message;
                break;
            case ErrorCodes.InvalidCredentials:
                PasswordError = ex.Message;
                break;
            default:
                FormError = ex.Message;
                break;
        }
    }

    private void ClearErrors()
    {
        EmailError = PasswordError = CodeError = FormError = "";
    }
}
