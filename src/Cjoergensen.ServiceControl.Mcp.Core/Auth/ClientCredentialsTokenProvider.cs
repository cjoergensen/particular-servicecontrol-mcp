namespace Cjoergensen.ServiceControl.Mcp.Auth;

/// <summary>
/// OAuth client credentials: signs in as this server itself. The token is cached until shortly before it expires and refreshed on demand;
/// concurrent callers share a single request.
/// </summary>
internal sealed class ClientCredentialsTokenProvider(
    IdentityProviderClient identity, AuthOptions auth, TimeProvider timeProvider) : ITokenProvider, IDisposable
{
    static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);
    static readonly TimeSpan FallbackLifetime = TimeSpan.FromMinutes(5);

    readonly SemaphoreSlim gate = new(1, 1);
    string? cachedToken;
    DateTimeOffset cachedUntil;

    public async ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (TryGetCached(out var token))
        {
            return token;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(out token))
            {
                return token;
            }

            var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
            if (!string.IsNullOrWhiteSpace(auth.Scope))
            {
                form["scope"] = auth.Scope;
            }

            if (!string.IsNullOrWhiteSpace(auth.Audience))
            {
                form["audience"] = auth.Audience;
            }

            try
            {
                var response = await identity.RequestTokenAsync(form, auth.ClientId!, auth.ClientSecret, cancellationToken).ConfigureAwait(false);
                var now = timeProvider.GetUtcNow();
                cachedToken = response.AccessToken;
                cachedUntil = (JwtExpiry.TryRead(response.AccessToken) ?? (response.ExpiresIn is { } lifetime ? now + lifetime : now + FallbackLifetime)) - RefreshSkew;
                return cachedToken;
            }
            catch (OAuthProtocolException ex)
            {
                throw new TokenAcquisitionException(
                    $"The identity provider refused the client credentials for '{auth.ClientId}' ({ex.Message}). Check Auth:ClientId, Auth:ClientSecret" +
                    (string.IsNullOrWhiteSpace(auth.Scope) ? " and, if your provider needs one, Auth:Scope." : " and Auth:Scope."), ex);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate()
    {
        gate.Wait();
        try
        {
            cachedToken = null;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();

    bool TryGetCached(out string? token)
    {
        token = cachedToken;
        return token is not null && timeProvider.GetUtcNow() < cachedUntil;
    }
}
