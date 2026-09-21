using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Cjoergensen.ServiceControl.Mcp.Auth;

/// <summary>
/// Exchanges the current caller's token for one for ServiceControl at the identity provider. This is the alternative to forwarding the caller's
/// token, which the MCP specification forbids: the caller's token was issued for this server and is never sent on. The exchanged token carries the
/// caller's identity, so ServiceControl applies that person's roles and records their name in its audit log.
/// </summary>
internal sealed class TokenExchangeTokenProvider(
    IdentityProviderClient identity,
    ServiceControlDiscovery discovery,
    ISubjectTokenAccessor subjects,
    AuthOptions auth,
    TimeProvider timeProvider) : ITokenProvider, IDisposable
{
    const string StandardGrant = "urn:ietf:params:oauth:grant-type:token-exchange";
    const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";
    const string OnBehalfOfGrant = "urn:ietf:params:oauth:grant-type:jwt-bearer";
    const int MaxCachedCallers = 1000;
    static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);
    static readonly TimeSpan FallbackLifetime = TimeSpan.FromMinutes(5);

    readonly ConcurrentDictionary<string, CachedToken> cache = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);

    sealed record CachedToken(string Token, DateTimeOffset Until);

    public async ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        var subject = subjects.GetSubjectToken()
            ?? throw new TokenAcquisitionException("There is no caller token to exchange for a ServiceControl token; the request was not authenticated.");
        var key = KeyOf(subject);

        if (TryGetCached(key, out var token))
        {
            return token;
        }

        // One exchange per caller at a time; different callers do not wait for each other.
        var gate = gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(key, out token))
            {
                return token;
            }

            try
            {
                var response = await identity.RequestTokenAsync(await BuildRequestAsync(subject, cancellationToken).ConfigureAwait(false), auth.ClientId!, auth.ClientSecret, cancellationToken)
                    .ConfigureAwait(false);

                var now = timeProvider.GetUtcNow();
                var until = (JwtExpiry.TryRead(response.AccessToken) ?? (response.ExpiresIn is { } lifetime ? now + lifetime : now + FallbackLifetime)) - RefreshSkew;
                Prune(now);
                cache[key] = new CachedToken(response.AccessToken, until);
                return response.AccessToken;
            }
            catch (OAuthProtocolException ex)
            {
                throw new TokenAcquisitionException(
                    $"The identity provider would not exchange your token for a ServiceControl token ({ex.Message}). The token must have been issued for this server, " +
                    "and the server's client must be allowed to perform token exchange.", ex);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate()
    {
        // Only the current caller's exchanged token is dropped: other callers' tokens are unaffected by this caller's rejection.
        if (subjects.GetSubjectToken() is { } subject)
        {
            cache.TryRemove(KeyOf(subject), out _);
        }
    }

    public void Dispose()
    {
        foreach (var gate in gates.Values)
        {
            gate.Dispose();
        }
    }

    async Task<Dictionary<string, string>> BuildRequestAsync(string subject, CancellationToken cancellationToken)
    {
        if (auth.ExchangeStyle == ExchangeStyle.OnBehalfOf)
        {
            return new Dictionary<string, string>
            {
                ["grant_type"] = OnBehalfOfGrant,
                ["assertion"] = subject,
                ["requested_token_use"] = "on_behalf_of",
                ["scope"] = auth.Scope!
            };
        }

        var audience = auth.Audience;
        if (string.IsNullOrWhiteSpace(audience))
        {
            audience = (await discovery.GetAsync(cancellationToken).ConfigureAwait(false)).Audience;
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = StandardGrant,
            ["subject_token"] = subject,
            ["subject_token_type"] = AccessTokenType,
            ["requested_token_type"] = AccessTokenType
        };

        if (!string.IsNullOrWhiteSpace(audience))
        {
            form["audience"] = audience;
        }

        if (!string.IsNullOrWhiteSpace(auth.Scope))
        {
            form["scope"] = auth.Scope;
        }

        return form;
    }

    bool TryGetCached(string key, out string? token)
    {
        if (cache.TryGetValue(key, out var entry) && timeProvider.GetUtcNow() < entry.Until)
        {
            token = entry.Token;
            return true;
        }

        token = null;
        return false;
    }

    void Prune(DateTimeOffset now)
    {
        if (cache.Count < MaxCachedCallers)
        {
            return;
        }

        foreach (var (key, entry) in cache)
        {
            if (entry.Until <= now)
            {
                cache.TryRemove(key, out _);
                if (gates.TryRemove(key, out var gate))
                {
                    gate.Dispose();
                }
            }
        }

        // Still full of live entries: make room by dropping arbitrary ones. They are simply exchanged again on the next call.
        while (cache.Count >= MaxCachedCallers && cache.Keys.FirstOrDefault() is { } victim)
        {
            cache.TryRemove(victim, out _);
        }
    }

    /// <summary>Callers are identified by a hash of their token, so the token itself is never kept as a dictionary key or logged.</summary>
    static string KeyOf(string subject) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject)));
}
