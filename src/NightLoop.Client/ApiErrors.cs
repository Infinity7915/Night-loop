using NightLoop.Contracts;
using NightLoop.Contracts.Api;

namespace NightLoop.Client;

/// <summary>The server answered with an error body. <see cref="Code"/> is one of <see cref="ErrorCodes"/>.</summary>
public sealed class ApiException(int status, ErrorResponse error, TimeSpan? retryAfter = null) : Exception(error.Message)
{
    public int Status { get; } = status;
    public string Code { get; } = error.Error;
    public IReadOnlyDictionary<string, string>? Fields { get; } = error.Fields;
    /// <summary>On version_conflict: the server's copy of the macro.</summary>
    public MacroDto? Current { get; } = error.Current;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>The message for one field, if the server named it, else the general message.</summary>
    public string MessageFor(string field) => Fields is not null && Fields.TryGetValue(field, out var m) ? m : Message;
}

/// <summary>The server couldn't be reached (no network, DNS, TLS, timeout).</summary>
public sealed class ServerUnreachableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Why the app was signed out.</summary>
public enum SessionEndReason
{
    SignedOut,
    /// <summary>This account signed in on another PC (401 session_replaced).</summary>
    Replaced,
    /// <summary>Expired, revoked (password change, account deleted) or otherwise not valid.</summary>
    Invalid,
    /// <summary>This app is too old for the server (426 update_required).</summary>
    UpdateRequired,
}

public sealed record SessionEndedEventArgs(SessionEndReason Reason, string Message);
