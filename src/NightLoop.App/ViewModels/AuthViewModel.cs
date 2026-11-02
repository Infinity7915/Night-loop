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

/// <summary>
/// Sign in, create account, the emailed code, and forgot password (schema section 8). Errors show under the
/// field the server named. Passwords come from the PasswordBoxes in code-behind; WPF can't bind them.
/// </summary>
public sealed partial class AuthViewModel : ObservableObject
{
    private readonly AccountSession _session;
    private readonly LocalSettings _settings;
    private Guid _challengeId;
    private bool _codeIsForSignUp;
    private DateTimeOffset _resendAllowedAt;

    public AuthViewModel(AccountSession session, LocalSettings settings, string? notice)
    {
        _session = session;
        _settings = settings;
        _email = settings.LastEmail ?? "";
        _notice = notice ?? "";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignIn), nameof(IsSignUp), nameof(IsCode), nameof(IsForgot), nameof(IsReset), nameof(Title), nameof(Subtitle))]
    private AuthMode _mode = AuthMode.SignIn;

    [ObservableProperty] private string _email;
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private bool _trustThisPc;
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
    public bool CodeIsForSignIn => !_codeIsForSignUp;

    public string Title => Mode switch
    {
        AuthMode.SignUp => "Create your account",
        AuthMode.Code => _codeIsForSignUp ? "Verify your email" : "Check your email",
        AuthMode.Forgot or AuthMode.Reset => "Reset your password",
        _ => "Sign in to Night-loop",
    };

    public string Subtitle => Mode switch
    {
        AuthMode.SignUp => $"Your account starts as user#<number>; you can pick a username later. Passwords need {AccountRules.PasswordMinLength}+ characters.",
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
        if (result is SignInResult.NeedsCode needs) GoToCode(needs.ChallengeId, signUp: false);
        // SignedIn is handled by the shell (it switches to the Macros page).
    });

    [RelayCommand]
    private Task SignUpAsync() => RunAsync(async () =>
    {
        if (AccountRules.PasswordProblem(Password, Email) is { } problem) { PasswordError = problem; return; }
        if (Password != ConfirmPassword) { PasswordError = "The passwords don't match."; return; }
        if (!AcceptedTerms) { FormError = "Accept the terms to create an account."; return; }
        _settings.LastEmail = Email.Trim();
        GoToCode(await _session.SignUpAsync(Email, Password, AcceptedTerms), signUp: true);
    });

    [RelayCommand]
    private Task VerifyCodeAsync() => RunAsync(async () =>
    {
        if (!AccountRules.IsValidCode(Code.Trim())) { CodeError = $"Enter the {AccountRules.CodeLength}-digit code."; return; }
        if (_codeIsForSignUp) await _session.VerifySignUpAsync(_challengeId, Code);
        else await _session.VerifySignInAsync(_challengeId, Code, TrustThisPc);
    });

    [RelayCommand]
    private Task ResendAsync() => RunAsync(async () =>
    {
        var wait = _resendAllowedAt - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) { CodeError = $"You can ask for a new code in {Math.Ceiling(wait.TotalSeconds)} s."; return; }
        _challengeId = await _session.ResendCodeAsync(_challengeId);
        _resendAllowedAt = DateTimeOffset.UtcNow.AddSeconds(AccountRules.ResendCooldownSeconds);
        Notice = "New code sent. The old one no longer works.";
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

    private void GoToCode(Guid challengeId, bool signUp)
    {
        _challengeId = challengeId;
        _codeIsForSignUp = signUp;
        _resendAllowedAt = DateTimeOffset.UtcNow.AddSeconds(AccountRules.ResendCooldownSeconds);
        Code = "";
        Mode = AuthMode.Code;
        OnPropertyChanged(nameof(CodeIsForSignIn));
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
