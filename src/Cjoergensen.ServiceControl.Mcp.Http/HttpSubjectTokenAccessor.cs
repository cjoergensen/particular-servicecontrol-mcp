using Cjoergensen.ServiceControl.Mcp.Auth;

namespace Cjoergensen.ServiceControl.Mcp.Http;

/// <summary>Reads the bearer token of the request being served, for token exchange. It is read per request and never stored.</summary>
sealed class HttpSubjectTokenAccessor(IHttpContextAccessor accessor) : ISubjectTokenAccessor
{
    const string Prefix = "Bearer ";

    public string? GetSubjectToken()
    {
        var header = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        return header is not null && header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? header[Prefix.Length..].Trim() : null;
    }
}
