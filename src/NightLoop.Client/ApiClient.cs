using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NightLoop.Contracts;
using NightLoop.Contracts.Api;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.Client;

/// <summary>
/// Typed client for the Night-loop API (server docs/api.md). Adds the session token, install id and app version to
/// every call, turns error bodies into <see cref="ApiException"/>, network failures into
/// <see cref="ServerUnreachableException"/>, and raises <see cref="SessionEnded"/> when the server ends the session.
/// </summary>
public sealed class ApiClient
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly Guid _installId;
    private readonly string _clientVersion;

    /// <param name="http">Its BaseAddress must end with /v1/ (e.g. https://api.example.com/v1/).</param>
    public ApiClient(HttpClient http, Guid installId, string clientVersion)
    {
        _http = http;
        _installId = installId;
        _clientVersion = clientVersion;
    }

    /// <summary>The session token. Null when signed out.</summary>
    public string? Token { get; set; }

    /// <summary>Raised (on the calling thread) when a call learns the session is over or the app is too old.</summary>
    public event EventHandler<SessionEndedEventArgs>? SessionEnded;

    // ---- Service ----

    public Task<HealthResponse> HealthAsync(CancellationToken ct = default) => SendAsync<HealthResponse>(HttpMethod.Get, "health", null, ct);
    public Task<VersionResponse> VersionAsync(CancellationToken ct = default) => SendAsync<VersionResponse>(HttpMethod.Get, "version", null, ct);
    public Task<AnnouncementResponse> AnnouncementAsync(CancellationToken ct = default) => SendAsync<AnnouncementResponse>(HttpMethod.Get, "announcement", null, ct);

    // ---- Auth ----

    public Task<ChallengeResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default) =>
        SendAsync<ChallengeResponse>(HttpMethod.Post, "auth/register", req, ct);

    public Task<AuthResponse> VerifyRegistrationAsync(RegisterVerifyRequest req, CancellationToken ct = default) =>
        SendAsync<AuthResponse>(HttpMethod.Post, "auth/register/verify", req, ct);

    /// <summary>Token and User are set when no code is needed; otherwise ChallengeId is.</summary>
    public Task<LoginResponse> LoginAsync(LoginRequest req, CancellationToken ct = default) =>
        SendAsync<LoginResponse>(HttpMethod.Post, "auth/login", req, ct);

    public Task<AuthResponse> VerifyLoginAsync(LoginVerifyRequest req, CancellationToken ct = default) =>
        SendAsync<AuthResponse>(HttpMethod.Post, "auth/login/verify", req, ct);

    public Task<ChallengeResponse> ResendCodeAsync(Guid challengeId, CancellationToken ct = default) =>
        SendAsync<ChallengeResponse>(HttpMethod.Post, "auth/code/resend", new ResendCodeRequest(challengeId), ct);

    public Task<ChallengeResponse> ForgotPasswordAsync(string email, CancellationToken ct = default) =>
        SendAsync<ChallengeResponse>(HttpMethod.Post, "auth/password/forgot", new ForgotPasswordRequest(email), ct);

    public Task ResetPasswordAsync(ResetPasswordRequest req, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Post, "auth/password/reset", req, ct);

    public Task LogoutAsync(CancellationToken ct = default) => SendAsync<object>(HttpMethod.Post, "auth/logout", null, ct);

    // ---- Account ----

    public async Task<UserDto> MeAsync(CancellationToken ct = default) =>
        (await SendAsync<MeResponse>(HttpMethod.Get, "me", null, ct)).User;

    public async Task<UserDto> ChangeUsernameAsync(string username, string currentPassword, CancellationToken ct = default) =>
        (await SendAsync<MeResponse>(HttpMethod.Patch, "me/username", new ChangeUsernameRequest(username, currentPassword), ct)).User;

    public Task<ChallengeResponse> StartEmailChangeAsync(string newEmail, string currentPassword, CancellationToken ct = default) =>
        SendAsync<ChallengeResponse>(HttpMethod.Post, "me/email", new ChangeEmailRequest(newEmail, currentPassword), ct);

    public async Task<UserDto> FinishEmailChangeAsync(Guid challengeId, string code, CancellationToken ct = default) =>
        (await SendAsync<MeResponse>(HttpMethod.Post, "me/email/verify", new VerifyCodeRequest(challengeId, code), ct)).User;

    public Task<ChallengeResponse> StartPasswordChangeAsync(string currentPassword, string newPassword, CancellationToken ct = default) =>
        SendAsync<ChallengeResponse>(HttpMethod.Post, "me/password", new ChangePasswordRequest(currentPassword, newPassword), ct);

    /// <summary>Every other session ends; the new token replaces <see cref="Token"/>.</summary>
    public async Task<string> FinishPasswordChangeAsync(Guid challengeId, string code, CancellationToken ct = default)
    {
        var res = await SendAsync<TokenResponse>(HttpMethod.Post, "me/password/verify", new VerifyCodeRequest(challengeId, code), ct);
        Token = res.Token;
        return res.Token;
    }

    public async Task<UserDto> UpdateSecurityAsync(SecurityRequest req, CancellationToken ct = default) =>
        (await SendAsync<MeResponse>(HttpMethod.Patch, "me/security", req, ct)).User;

    /// <summary>
    /// "Every sign-in" accounts: asks for the code that opens Night-loop on this launch. The server reuses a code
    /// it sent in the last few minutes. ChallengeId is null when no code is needed.
    /// </summary>
    public Task<UnlockResponse> StartUnlockAsync(CancellationToken ct = default) =>
        SendAsync<UnlockResponse>(HttpMethod.Post, "me/unlock", null, ct);

    public async Task<UserDto> FinishUnlockAsync(Guid challengeId, string code, CancellationToken ct = default) =>
        (await SendAsync<MeResponse>(HttpMethod.Post, "me/unlock/verify", new VerifyCodeRequest(challengeId, code), ct)).User;

    public Task DeleteAccountAsync(string currentPassword, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, "me", new DeleteAccountRequest(currentPassword), ct);

    // ---- Macros ----

    public Task<MacroListResponse> ListMacrosAsync(CancellationToken ct = default) =>
        SendAsync<MacroListResponse>(HttpMethod.Get, "macros", null, ct);

    public Task<MacroDto> GetMacroAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<MacroDto>(HttpMethod.Get, $"macros/{id}", null, ct);

    public Task<MacroDto> CreateMacroAsync(string name, MacroDoc doc, Guid? groupId = null, CancellationToken ct = default) =>
        SendAsync<MacroDto>(HttpMethod.Post, "macros", new CreateMacroRequest(name, doc, groupId), ct);

    public Task<DraftSavedResponse> SaveDraftAsync(Guid id, int version, MacroDoc doc, CancellationToken ct = default) =>
        SendAsync<DraftSavedResponse>(HttpMethod.Put, $"macros/{id}/draft", new SaveDraftRequest(doc), ct, version);

    public Task<MacroDto> SaveMacroAsync(Guid id, int version, CancellationToken ct = default) =>
        SendAsync<MacroDto>(HttpMethod.Post, $"macros/{id}/save", null, ct, version);

    public Task<MacroDto> DiscardDraftAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<MacroDto>(HttpMethod.Post, $"macros/{id}/discard-draft", null, ct);

    public Task<MacroDto> UpdateMacroAsync(Guid id, UpdateMacroRequest req, int? version = null, CancellationToken ct = default) =>
        SendAsync<MacroDto>(HttpMethod.Patch, $"macros/{id}", req, ct, version);

    public Task DeleteMacroAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"macros/{id}", null, ct);

    public Task<GroupDto> CreateGroupAsync(string name, string color, CancellationToken ct = default) =>
        SendAsync<GroupDto>(HttpMethod.Post, "groups", new CreateGroupRequest(name, color), ct);

    public Task<GroupDto> UpdateGroupAsync(Guid id, UpdateGroupRequest req, CancellationToken ct = default) =>
        SendAsync<GroupDto>(HttpMethod.Patch, $"groups/{id}", req, ct);

    public Task DeleteGroupAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"groups/{id}", null, ct);

    // ---- Plumbing ----

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct, int? ifMatch = null)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        if (Token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        req.Headers.Add(ApiHeaders.InstallId, _installId.ToString());
        req.Headers.Add(ApiHeaders.ClientVersion, _clientVersion);
        if (ifMatch is { } v) req.Headers.TryAddWithoutValidation(ApiHeaders.IfMatch, $"\"{v}\"");

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ServerUnreachableException("Can't reach the Night-loop server. Check your connection.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ServerUnreachableException("The Night-loop server took too long to answer.", ex);
        }

        using (res)
        {
            if (res.IsSuccessStatusCode)
            {
                if (res.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object)) return default!;
                return await res.Content.ReadFromJsonAsync<T>(Json, ct)
                       ?? throw new ServerUnreachableException("The server sent an empty answer.");
            }

            var error = await ReadErrorAsync(res, ct);
            var ex = new ApiException((int)res.StatusCode, error, res.Headers.RetryAfter?.Delta);
            RaiseIfSessionEnded(ex);
            throw ex;
        }
    }

    private static async Task<ErrorResponse> ReadErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var error = await res.Content.ReadFromJsonAsync<ErrorResponse>(Json, ct);
            if (error is { Error.Length: > 0 }) return error;
        }
        catch (JsonException)
        {
            // Not our JSON (a proxy error page, say); fall through.
        }
        return new ErrorResponse(res.StatusCode >= HttpStatusCode.InternalServerError ? ErrorCodes.ServerError : ErrorCodes.BadRequest,
            $"The server answered {(int)res.StatusCode} {res.ReasonPhrase}.");
    }

    private void RaiseIfSessionEnded(ApiException ex)
    {
        SessionEndedEventArgs? args = ex.Code switch
        {
            ErrorCodes.SessionReplaced => new(SessionEndReason.Replaced, "Signed in on another device."),
            ErrorCodes.SessionInvalid when Token is not null => new(SessionEndReason.Invalid, ex.Message),
            ErrorCodes.UpdateRequired => new(SessionEndReason.UpdateRequired, ex.Message),
            _ => null,
        };
        if (args is null) return;
        if (args.Reason != SessionEndReason.UpdateRequired) Token = null;
        SessionEnded?.Invoke(this, args);
    }
}
