namespace Cjoergensen.ServiceControl.Mcp.Auth;

/// <summary>
/// Supplies the token of the caller currently being served, when there is one. Only a host that serves several callers (the HTTP host) provides
/// this; it is what <see cref="AuthMode.TokenExchange"/> exchanges. Implementations read it from the current request and must not cache it.
/// </summary>
public interface ISubjectTokenAccessor
{
    /// <summary>The bearer token the current caller presented to this server, or <c>null</c> when there is no current caller.</summary>
    string? GetSubjectToken();
}
