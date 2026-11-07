namespace NightLoop.Contracts;

/// <summary>Error codes the API returns in { "error": "..." }. Section 11 of the schema.</summary>
public static class ErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string InvalidCredentials = "invalid_credentials";
    public const string SessionInvalid = "session_invalid";
    public const string SessionReplaced = "session_replaced";
    public const string MacroLimit = "macro_limit";
    public const string GroupLimit = "group_limit";
    public const string UsernameChangeLimit = "username_change_limit";
    public const string EmailNotVerified = "email_not_verified";
    public const string NotFound = "not_found";
    public const string EmailTaken = "email_taken";
    public const string UsernameTaken = "username_taken";
    public const string GroupNameTaken = "group_name_taken";
    public const string VersionConflict = "version_conflict";
    public const string PayloadTooLarge = "payload_too_large";
    public const string InvalidEmail = "invalid_email";
    public const string WeakPassword = "weak_password";
    public const string InvalidUsername = "invalid_username";
    public const string UsernameReserved = "username_reserved";
    public const string InvalidCode = "invalid_code";
    public const string CodeExpired = "code_expired";
    public const string InvalidMacro = "invalid_macro";
    public const string InvalidGroup = "invalid_group";
    public const string InvalidAsset = "invalid_asset";
    public const string AccountLocked = "account_locked";
    public const string UpdateRequired = "update_required";
    public const string RateLimited = "rate_limited";
    public const string TooManyAttempts = "too_many_attempts";
    public const string ServerError = "server_error";

    // Codes the API uses beyond the section 11 map.
    /// <summary>422: the "I accept the terms" box was not ticked at sign-up.</summary>
    public const string TermsRequired = "terms_required";
    /// <summary>409: discard-draft on a macro that was never saved (delete it instead).</summary>
    public const string NoSavedCopy = "no_saved_copy";
    /// <summary>428: a macro write arrived without an If-Match version.</summary>
    public const string VersionRequired = "version_required";
    /// <summary>403: the change places more blocks than are left today (BlocksPerDay) or on the account (BlocksPerUser).</summary>
    public const string BlockLimit = "block_limit";
}
