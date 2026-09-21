using ModelContextProtocol;

namespace Cjoergensen.ServiceControl.Mcp.Client;

public enum ServiceControlFailureKind
{
    Unreachable,
    Timeout,
    Unauthorized,

    /// <summary>No token could be obtained (for example a sign-in is pending), so no request was made.</summary>
    CredentialsUnavailable,
    Forbidden,
    NotFound,
    BadRequest,
    ServerError,
    InsecureTransport,
    Other
}

/// <summary>
/// A call to ServiceControl failed. Derives from <see cref="McpException"/> so the message reaches the caller as a tool
/// error the LLM can read and react to, instead of a generic failure. Messages never contain credentials.
/// </summary>
public sealed class ServiceControlApiException : McpException
{
    public ServiceControlApiException(ServiceControlFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public ServiceControlFailureKind Kind { get; }
}
