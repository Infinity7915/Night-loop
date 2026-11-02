using NightLoop.Contracts;
using NightLoop.Contracts.Api;

namespace NightLoop.Client;

/// <summary>The result of entering email and password.</summary>
public abstract record SignInResult
{
    public sealed record SignedIn(UserDto User) : SignInResult;
    /// <summary>A 6-digit code was emailed; call <see cref="AccountSession.VerifySignInAsync"/>.</summary>
    public sealed record NeedsCode(Guid ChallengeId) : SignInResult;
}

/// <summary>
/// The signed-in state of the app (schema sections 5 and 8): restores the saved token at start, signs in and up,
/// keeps the session alive with a 60-second heartbeat, and reports when the server ends it.
/// </summary>
public sealed class AccountSession : IDisposable
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);
    /// <summary>Running macros may keep going this long without reaching the server.</summary>
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromMinutes(5);

    private readonly ApiClient _api;
    private readonly ISessionStore _store;
    private readonly DeviceIdentity _device;
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _heartbeat;
    private string? _pendingEmail;
    private UserDto? _pendingUser;
    private bool _offlineGraceRaised;

    public AccountSession(ApiClient api, ISessionStore store, DeviceIdentity device, TimeProvider? clock = null)
    {
        _api = api;
        _store = store;
        _device = device;
        _clock = clock ?? TimeProvider.System;
        _api.SessionEnded += OnSessionEnded;
    }

    public ApiClient Api => _api;
    public UserDto? User { get; private set; }
    public bool IsSignedIn => User is not null;
    /// <summary>When the server last answered. Null before the first answer.</summary>
    public DateTimeOffset? LastContact { get; private set; }
    public bool IsOffline { get; private set; }

    public event Action<UserDto>? SignedIn;
    public event Action<UserDto>? UserChanged;
    public event Action<SessionEndedEventArgs>? SignedOut;
    /// <summary>Raised when IsOffline flips.</summary>
    public event Action<bool>? OfflineChanged;
    /// <summary>Raised once when the server has been unreachable for <see cref="OfflineGrace"/>: stop running macros.</summary>
    public event Action? OfflineGraceExpired;

    /// <summary>
    /// Uses the saved token if there is one. True when signed in, or when the account asks for a code every time
    /// Night-loop opens: then <see cref="PendingUnlock"/> is set and <see cref="VerifyUnlockAsync"/> finishes it.
    /// Throws <see cref="ServerUnreachableException"/> when offline, so the loading screen can offer Retry.
    /// </summary>
    public async Task<bool> TryRestoreAsync(CancellationToken ct = default)
    {
        var saved = _store.Load();
        if (string.IsNullOrEmpty(saved.Token)) return false;
        _api.Token = saved.Token;
        try
        {
            var user = await _api.MeAsync(ct);
            MarkOnline();
            if (user is { TwofaEnabled: true, TwofaMode: TwofaModes.EverySignin })
            {
                _pendingUser = user;
                _pendingEmail = user.Email;
                try
                {
                    var challenge = (await _api.StartUnlockAsync(ct)).ChallengeId;
                    if (challenge is { } id)
                    {
                        PendingUnlock = id;
                        return true;
                    }
                }
                catch (ApiException ex) when (ex.Status is 404 or 405)
                {
                    // An older server without the launch code: carry on as before.
                }
                catch (ApiException ex) when (ex.Code is not (ErrorCodes.SessionInvalid or ErrorCodes.SessionReplaced or ErrorCodes.UpdateRequired))
                {
                    // No code could be sent (too many today, for one). The code screen says why and offers Resend.
                    PendingUnlock = Guid.Empty;
                    PendingUnlockError = ex.Message;
                    return true;
                }
            }
            BecomeSignedIn(user, saved.Token);
            return true;
        }
        catch (ApiException ex) when (ex.Code is ErrorCodes.SessionInvalid or ErrorCodes.SessionReplaced)
        {
            ForgetToken();
            return false;
        }
    }

    /// <summary>The account asks for a code each time Night-loop opens: the challenge for this launch, until it's entered.</summary>
    public Guid? PendingUnlock { get; private set; }
    /// <summary>Why no launch code could be sent, when that happened.</summary>
    public string? PendingUnlockError { get; private set; }
    /// <summary>Where the launch code went.</summary>
    public string? PendingUnlockEmail => _pendingUser?.Email;

    /// <summary>Sends a launch code again (or for the first time, when sending failed).</summary>
    public async Task<Guid> ResendUnlockAsync(CancellationToken ct = default)
    {
        if (PendingUnlock is not { } current) throw new InvalidOperationException("Night-loop isn't waiting for a code.");
        Guid id;
        if (current != Guid.Empty) id = (await _api.ResendCodeAsync(current, ct)).ChallengeId;
        else if ((await _api.StartUnlockAsync(ct)).ChallengeId is { } started) id = started;
        else
        {
            // The account no longer asks for a code at launch (changed on another PC): open it.
            var user = await _api.MeAsync(ct);
            PendingUnlock = null;
            PendingUnlockError = null;
            _pendingUser = null;
            BecomeSignedIn(user, _api.Token!);
            return Guid.Empty;
        }
        MarkOnline();
        PendingUnlock = id;
        PendingUnlockError = null;
        return id;
    }

    /// <summary>The launch code was right: Night-loop opens.</summary>
    public async Task<UserDto> VerifyUnlockAsync(string code, CancellationToken ct = default)
    {
        if (PendingUnlock is not { } id || _pendingUser is null) throw new InvalidOperationException("Night-loop isn't waiting for a code.");
        if (id == Guid.Empty) throw new InvalidOperationException("No code was sent yet. Press Resend code.");
        var user = await _api.FinishUnlockAsync(id, code.Trim(), ct);
        MarkOnline();
        PendingUnlock = null;
        PendingUnlockError = null;
        _pendingUser = null;
        BecomeSignedIn(user, _api.Token!);
        return user;
    }

    public async Task<SignInResult> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        var trust = _store.Load().TrustTokens.GetValueOrDefault(Key(email));
        var res = await _api.LoginAsync(new LoginRequest(email.Trim(), password, _device.ToDeviceInfo(), trust), ct);
        MarkOnline();
        if (res.ChallengeId is { } challenge)
        {
            _pendingEmail = email;
            return new SignInResult.NeedsCode(challenge);
        }
        BecomeSignedIn(res.User!, res.Token!);
        return new SignInResult.SignedIn(res.User!);
    }

    public async Task<UserDto> VerifySignInAsync(Guid challengeId, string code, bool trustThisPc, CancellationToken ct = default)
    {
        var auth = await _api.VerifyLoginAsync(new LoginVerifyRequest(challengeId, code.Trim(), trustThisPc), ct);
        MarkOnline();
        if (auth.TrustToken is not null)
        {
            var saved = _store.Load();
            saved.TrustTokens[Key(_pendingEmail ?? auth.User.Email)] = auth.TrustToken;
            _store.Save(saved);
        }
        BecomeSignedIn(auth.User, auth.Token);
        return auth.User;
    }

    /// <summary>Starts sign-up; the server emails a code. Returns the challenge for <see cref="VerifySignUpAsync"/>.</summary>
    public async Task<Guid> SignUpAsync(string email, string password, bool acceptedTerms, CancellationToken ct = default)
    {
        var res = await _api.RegisterAsync(new RegisterRequest(email.Trim(), password, acceptedTerms), ct);
        MarkOnline();
        return res.ChallengeId;
    }

    public async Task<UserDto> VerifySignUpAsync(Guid challengeId, string code, CancellationToken ct = default)
    {
        var auth = await _api.VerifyRegistrationAsync(new RegisterVerifyRequest(challengeId, code.Trim(), _device.ToDeviceInfo()), ct);
        MarkOnline();
        BecomeSignedIn(auth.User, auth.Token);
        return auth.User;
    }

    public async Task<Guid> ResendCodeAsync(Guid challengeId, CancellationToken ct = default) =>
        (await _api.ResendCodeAsync(challengeId, ct)).ChallengeId;

    public async Task<Guid> ForgotPasswordAsync(string email, CancellationToken ct = default) =>
        (await _api.ForgotPasswordAsync(email.Trim(), ct)).ChallengeId;

    public Task ResetPasswordAsync(Guid challengeId, string code, string newPassword, CancellationToken ct = default) =>
        _api.ResetPasswordAsync(new ResetPasswordRequest(challengeId, code.Trim(), newPassword), ct);

    /// <summary>Ends the session on the server (best effort) and forgets the token.</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        try
        {
            await _api.LogoutAsync(ct);
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            // Signed out locally either way; the server session expires on its own.
        }
        EndSession(new SessionEndedEventArgs(SessionEndReason.SignedOut, "Signed out."));
    }

    /// <summary>After a password change the server issues a new token for this PC.</summary>
    public void ReplaceToken(string token)
    {
        _api.Token = token;
        var saved = _store.Load();
        saved.Token = token;
        _store.Save(saved);
    }

    public void UpdateUser(UserDto user)
    {
        User = user;
        UserChanged?.Invoke(user);
    }

    /// <summary>One heartbeat: GET /me. Public so the UI can poke it (e.g. after the PC wakes up).</summary>
    public async Task HeartbeatAsync(CancellationToken ct = default)
    {
        if (!IsSignedIn) return;
        try
        {
            var user = await _api.MeAsync(ct);
            MarkOnline();
            if (user != User) UpdateUser(user);
        }
        catch (ServerUnreachableException)
        {
            MarkOffline();
        }
        catch (ApiException)
        {
            // Session errors are handled by OnSessionEnded; anything else is not worth signing out over.
        }
    }

    private void BecomeSignedIn(UserDto user, string token)
    {
        _api.Token = token;
        var saved = _store.Load();
        saved.Token = token;
        _store.Save(saved);
        User = user;
        _pendingEmail = null;
        StartHeartbeat();
        SignedIn?.Invoke(user);
    }

    private void StartHeartbeat()
    {
        _heartbeat?.Cancel();
        var cts = _heartbeat = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(HeartbeatInterval, _clock);
            try
            {
                while (await timer.WaitForNextTickAsync(cts.Token))
                    await HeartbeatAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Signed out or closing.
            }
        });
    }

    private void MarkOnline()
    {
        LastContact = _clock.GetUtcNow();
        _offlineGraceRaised = false;
        if (!IsOffline) return;
        IsOffline = false;
        OfflineChanged?.Invoke(false);
    }

    private void MarkOffline()
    {
        if (!IsOffline)
        {
            IsOffline = true;
            OfflineChanged?.Invoke(true);
        }
        if (!_offlineGraceRaised && LastContact is { } last && _clock.GetUtcNow() - last >= OfflineGrace)
        {
            _offlineGraceRaised = true;
            OfflineGraceExpired?.Invoke();
        }
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        if (e.Reason == SessionEndReason.UpdateRequired)
        {
            SignedOut?.Invoke(e);
            return;
        }
        EndSession(e);
    }

    private void EndSession(SessionEndedEventArgs e)
    {
        var wasSignedIn = IsSignedIn || PendingUnlock is not null;
        ForgetToken();
        User = null;
        PendingUnlock = null;
        PendingUnlockError = null;
        _pendingUser = null;
        _heartbeat?.Cancel();
        _heartbeat = null;
        if (wasSignedIn || e.Reason == SessionEndReason.SignedOut) SignedOut?.Invoke(e);
    }

    private void ForgetToken()
    {
        _api.Token = null;
        var saved = _store.Load();
        saved.Token = null;
        _store.Save(saved);
    }

    private static string Key(string email) => email.Trim().ToLowerInvariant();

    public void Dispose()
    {
        _heartbeat?.Cancel();
        _api.SessionEnded -= OnSessionEnded;
    }
}
