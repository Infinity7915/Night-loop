using System.Text.Json.Serialization;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.Contracts.Api;

// Request and response bodies for the Night-loop API (schema section 4). The server and the app's
// ApiClient both compile these, so a field renamed here is renamed on both sides.
// JSON uses camelCase (System.Text.Json web defaults) and ISO 8601 UTC times.

/// <summary>Headers the app sends on every call.</summary>
public static class ApiHeaders
{
    public const string InstallId = "X-Install-Id";
    public const string ClientVersion = "X-Client-Version";
    public const string IfMatch = "If-Match";
}

/// <summary>Values for <see cref="UserDto.TwofaMode"/>.</summary>
public static class TwofaModes
{
    public const string EverySignin = "every_signin";
    public const string NewDevices = "new_devices";
}

/// <summary>The PC signing in. Fingerprint is a hex SHA-256 the app computes from hardware ids.</summary>
public sealed record DeviceInfo(Guid InstallId, string Fingerprint, string Label);

// ---- Auth (public) ----

public sealed record RegisterRequest(string Email, string Password, bool AcceptedTerms);

public sealed record RegisterVerifyRequest(Guid ChallengeId, string Code, DeviceInfo Device);

public sealed record LoginRequest(string Email, string Password, DeviceInfo Device, string? TrustToken = null);

public sealed record LoginVerifyRequest(Guid ChallengeId, string Code, bool TrustThisPc);

public sealed record ResendCodeRequest(Guid ChallengeId);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(Guid ChallengeId, string Code, string NewPassword);

/// <summary>202 answer when an emailed code is needed next.</summary>
public sealed record ChallengeResponse(Guid ChallengeId);

/// <summary>Signed in. TrustToken is set only when the user ticked "Trust this PC for 30 days".</summary>
public sealed record AuthResponse(string Token, UserDto User, string? TrustToken = null);

/// <summary>POST /auth/login answers 200 with Auth, or 202 with ChallengeId when a code was emailed.</summary>
public sealed record LoginResponse(string? Token, UserDto? User, Guid? ChallengeId);

// ---- Account ----

public sealed record UserDto(
    Guid Id,
    long UserNumber,
    string Email,
    string Username,
    bool UsernameIsDefault,
    int UsernameChangesLeft,
    bool TwofaEnabled,
    string TwofaMode,
    DateTimeOffset CreatedAt);

public sealed record MeResponse(UserDto User);

/// <summary>
/// GET /me/data: the "Download my data" summary. The password is never included: the server only has a one-way hash,
/// so PasswordSet just says there is one. Macros themselves are never exported, only how many the account holds.
/// </summary>
public sealed record AccountDataResponse(
    string Email,
    string Username,
    long UserNumber,
    long UsageSeconds,
    int MacroCount,
    bool PasswordSet,
    DateTimeOffset? PasswordChangedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExportedAt);

public sealed record ChangeUsernameRequest(string Username, string CurrentPassword);

public sealed record ChangeEmailRequest(string NewEmail, string CurrentPassword);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record VerifyCodeRequest(Guid ChallengeId, string Code);

/// <summary>CurrentPassword may be empty when the change only makes sign-in stricter (see AccountRules.SecurityChangeNeedsPassword).</summary>
public sealed record SecurityRequest(bool TwofaEnabled, string TwofaMode, string CurrentPassword);

/// <summary>
/// POST /me/unlock, sent when the app starts with a saved session. With codes on every sign-in, a code is emailed and
/// ChallengeId is set: the app asks for it (POST /me/unlock/verify) before opening the account. Null: carry on.
/// </summary>
public sealed record UnlockResponse(Guid? ChallengeId);

public sealed record DeleteAccountRequest(string CurrentPassword);

public sealed record TokenResponse(string Token);

// ---- Macros and groups ----

public sealed record MacroSummaryDto(
    Guid Id,
    string Name,
    Guid? GroupId,
    string? TargetGame,
    bool HasDraft,
    bool HasSaved,
    int BlockCount,
    int Version,
    bool RunAlongside,
    string? HotkeyStart,
    string? HotkeyStop,
    string TriggerMode,
    DateTimeOffset? SavedAt,
    DateTimeOffset? DraftUpdatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>One macro with both copies. DraftDoc is null when there are no unsaved changes.</summary>
public sealed record MacroDto(
    Guid Id,
    string Name,
    Guid? GroupId,
    string? TargetGame,
    int BlockCount,
    int Version,
    bool RunAlongside,
    string? HotkeyStart,
    string? HotkeyStop,
    string TriggerMode,
    MacroDoc? SavedDoc,
    DateTimeOffset? SavedAt,
    MacroDoc? DraftDoc,
    DateTimeOffset? DraftUpdatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The copy the editor should open: the draft when there is one, else the saved copy.</summary>
    [JsonIgnore]
    public MacroDoc? Current => DraftDoc ?? SavedDoc;
}

public sealed record GroupDto(Guid Id, string Name, string Color, int SortOrder);

public sealed record MacroListResponse(IReadOnlyList<MacroSummaryDto> Macros, IReadOnlyList<GroupDto> Groups, int Limit);

/// <summary>Creates a macro as a draft. The doc's name is replaced by <see cref="Name"/>.</summary>
public sealed record CreateMacroRequest(string Name, MacroDoc Doc, Guid? GroupId = null);

public sealed record SaveDraftRequest(MacroDoc Doc);

public sealed record DraftSavedResponse(int Version, DateTimeOffset DraftUpdatedAt);

/// <summary>
/// PATCH /macros/{id}. Only fields that are not null change. Set Ungroup to move the macro out of its group.
/// An empty string for a hotkey clears it. Option changes are written into the stored documents too.
/// </summary>
public sealed record UpdateMacroRequest(
    string? Name = null,
    Guid? GroupId = null,
    bool? Ungroup = null,
    bool? RunAlongside = null,
    string? HotkeyStart = null,
    string? HotkeyStop = null,
    string? TriggerMode = null);

public sealed record CreateGroupRequest(string Name, string Color);

public sealed record UpdateGroupRequest(string? Name = null, string? Color = null, int? SortOrder = null);

// ---- Service ----

public sealed record HealthResponse(string Status, string Db);

public sealed record VersionResponse(string Minimum, string Latest, string Message);

public sealed record AnnouncementResponse(string Message, DateTimeOffset? UpdatedAt);

/// <summary>
/// Every error: { "error": "code", "message": "...", "fields": {...} }. Fields only on validation errors.
/// Current is set on 409 version_conflict and holds the server's copy of the macro.
/// </summary>
public sealed record ErrorResponse(
    string Error,
    string Message,
    IReadOnlyDictionary<string, string>? Fields = null,
    MacroDto? Current = null);
