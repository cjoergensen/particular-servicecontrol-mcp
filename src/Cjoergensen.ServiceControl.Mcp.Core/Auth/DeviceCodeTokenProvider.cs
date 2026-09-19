using Microsoft.Extensions.Logging;

namespace Cjoergensen.ServiceControl.Mcp.Auth;

/// <summary>
/// OAuth device authorization, so a person signs in themselves and ServiceControl sees their own roles. A stdio MCP server cannot reliably show
/// a code to the user, so this never blocks: when sign-in is needed the call fails with instructions the agent can relay ("open this address and
/// enter this code"), and the next call after the person has approved picks the token up. A refresh token, when the provider issues one, keeps
/// the session going without asking again.
/// </summary>
internal sealed partial class DeviceCodeTokenProvider(
    IdentityProviderClient identity, ServiceControlDiscovery discovery, AuthOptions auth, TimeProvider timeProvider, ILogger<DeviceCodeTokenProvider> logger) : ITokenProvider, IDisposable
{
    const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";
    static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);
    static readonly TimeSpan FallbackLifetime = TimeSpan.FromMinutes(5);

    readonly SemaphoreSlim gate = new(1, 1);
    string? accessToken;
    DateTimeOffset accessTokenUntil;
    string? refreshToken;
    DeviceAuthorization? pending;
    DateTimeOffset pendingExpiresAt;
    DateTimeOffset nextPollAt;
    TimeSpan pollInterval;
    bool offlineAccessRefused;

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

            if (refreshToken is not null && await TryRefreshAsync(cancellationToken).ConfigureAwait(false))
            {
                return accessToken;
            }

            return pending is null
                ? await StartSignInAsync(cancellationToken).ConfigureAwait(false)
                : await ContinueSignInAsync(cancellationToken).ConfigureAwait(false);
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
            // Keep the refresh token: a rejected access token is normally cured by refreshing, not by asking the person again.
            accessToken = null;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();

    bool TryGetCached(out string? token)
    {
        token = accessToken;
        return token is not null && timeProvider.GetUtcNow() < accessTokenUntil;
    }

    async Task<string?> StartSignInAsync(CancellationToken cancellationToken)
    {
        var scope = auth.Scope;
        if (string.IsNullOrWhiteSpace(scope))
        {
            scope = (await discovery.GetAsync(cancellationToken).ConfigureAwait(false)).Scopes;
        }

        // ServiceControl advertises offline_access for ServicePulse. If this client or user is not allowed offline tokens, do without: a session
        // that must be renewed sooner is better than one that can never start.
        if (offlineAccessRefused && scope is not null)
        {
            scope = string.Join(' ', scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(part => part != "offline_access"));
        }

        DeviceAuthorization authorization;
        try
        {
            authorization = await identity.StartDeviceAuthorizationAsync(auth.ClientId!, scope ?? "openid", cancellationToken).ConfigureAwait(false);
        }
        catch (OAuthProtocolException ex)
        {
            throw new TokenAcquisitionException(
                $"The identity provider refused to start sign-in for client '{auth.ClientId}' ({ex.Message}). It must be a public client with the device authorization grant enabled.", ex);
        }

        var now = timeProvider.GetUtcNow();
        pending = authorization;
        pendingExpiresAt = now + authorization.ExpiresIn;
        pollInterval = authorization.Interval;
        nextPollAt = now + pollInterval;
        LogSignInStarted(authorization.VerificationUriComplete ?? authorization.VerificationUri, authorization.UserCode, (int)authorization.ExpiresIn.TotalSeconds);
        throw SignInRequired(authorization, started: true);
    }

    async Task<string?> ContinueSignInAsync(CancellationToken cancellationToken)
    {
        var authorization = pending!;
        var now = timeProvider.GetUtcNow();

        if (now >= pendingExpiresAt)
        {
            pending = null;
            return await StartSignInAsync(cancellationToken).ConfigureAwait(false);
        }

        if (now < nextPollAt)
        {
            throw SignInRequired(authorization, started: false);
        }

        try
        {
            var response = await identity.RequestTokenAsync(
                new Dictionary<string, string> { ["grant_type"] = DeviceGrant, ["device_code"] = authorization.DeviceCode },
                auth.ClientId!,
                clientSecret: null,
                cancellationToken).ConfigureAwait(false);

            pending = null;
            Store(response);
            LogSignedIn();
            return accessToken;
        }
        catch (OAuthProtocolException ex) when (ex.Error == "authorization_pending")
        {
            LogPollResult(ex.Error);
            nextPollAt = timeProvider.GetUtcNow() + pollInterval;
            throw SignInRequired(authorization, started: false);
        }
        catch (OAuthProtocolException ex) when (ex.Error == "slow_down")
        {
            LogPollResult(ex.Error);
            pollInterval += TimeSpan.FromSeconds(5);
            nextPollAt = timeProvider.GetUtcNow() + pollInterval;
            throw SignInRequired(authorization, started: false);
        }
        catch (OAuthProtocolException ex) when (ex.Error is "access_denied" or "expired_token")
        {
            LogPollResult(ex.Error);
            pending = null;
            throw new TokenAcquisitionException(
                ex.Error == "access_denied"
                    ? "Sign-in was declined. Ask again to start a new sign-in."
                    : "The sign-in code expired before it was used. Ask again to start a new sign-in.",
                ex);
        }
        catch (OAuthProtocolException ex)
        {
            LogPollResult(ex.Message);
            pending = null;

            var refusedOffline = string.IsNullOrWhiteSpace(auth.Scope) && !offlineAccessRefused &&
                                 (ex.Message.Contains("offline", StringComparison.OrdinalIgnoreCase) || ex.Error == "invalid_scope");
            if (refusedOffline)
            {
                offlineAccessRefused = true;
                LogOfflineRefused();
            }

            throw new TokenAcquisitionException(
                $"Sign-in failed ({ex.Message}). " +
                (refusedOffline ? "Ask again to start a new sign-in; it will not request long-lived (offline) access this time." : "Ask again to start a new sign-in."),
                ex);
        }
    }

    async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await identity.RequestTokenAsync(
                new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken! },
                auth.ClientId!,
                clientSecret: null,
                cancellationToken).ConfigureAwait(false);
            Store(response);
            return true;
        }
        catch (OAuthProtocolException)
        {
            // The refresh token was revoked or expired: the person has to sign in again.
            refreshToken = null;
            return false;
        }
    }

    void Store(TokenResponse response)
    {
        var now = timeProvider.GetUtcNow();
        accessToken = response.AccessToken;
        refreshToken = response.RefreshToken ?? refreshToken;
        accessTokenUntil = (JwtExpiry.TryRead(response.AccessToken) ?? (response.ExpiresIn is { } lifetime ? now + lifetime : now + FallbackLifetime)) - RefreshSkew;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in required: open {Address} and enter code {UserCode} (valid for {Seconds}s). Waiting for approval.")]
    partial void LogSignInStarted(string address, string userCode, int seconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sign-in poll answered: {Result}")]
    partial void LogPollResult(string result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The identity provider does not allow offline access for this client or user; the next sign-in will not request it.")]
    partial void LogOfflineRefused();

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in completed.")]
    partial void LogSignedIn();

    static TokenAcquisitionException SignInRequired(DeviceAuthorization authorization, bool started)
    {
        var where = authorization.VerificationUriComplete is { } complete
            ? $"open {complete} (or open {authorization.VerificationUri} and enter the code {authorization.UserCode})"
            : $"open {authorization.VerificationUri} and enter the code {authorization.UserCode}";

        return new TokenAcquisitionException(
            (started ? "Sign-in is required. " : "Sign-in is still pending. ") +
            $"Ask the user to {where}, sign in, and then repeat this request.");
    }
}
