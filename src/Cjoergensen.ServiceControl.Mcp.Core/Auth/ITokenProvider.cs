namespace Cjoergensen.ServiceControl.Mcp.Auth;

/// <summary>Supplies the bearer token sent to ServiceControl. Implementations must never log the token.</summary>
public interface ITokenProvider
{
    /// <summary>Returns a bearer token, or <c>null</c> when no credentials are configured.</summary>
    ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken);

    /// <summary>Discards any cached token because ServiceControl rejected it, so the next call obtains a fresh one.</summary>
    void Invalidate();
}

/// <summary>No credentials: for ServiceControl instances with authentication disabled.</summary>
public sealed class NoTokenProvider : ITokenProvider
{
    public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

    public void Invalidate()
    {
    }
}

/// <summary>A fixed token supplied through configuration.</summary>
public sealed class StaticTokenProvider(string token) : ITokenProvider
{
    public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(token);

    public void Invalidate()
    {
    }
}
