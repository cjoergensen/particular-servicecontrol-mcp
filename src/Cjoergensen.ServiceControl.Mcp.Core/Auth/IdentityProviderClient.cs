using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Cjoergensen.ServiceControl.Mcp.Auth;

internal sealed record OidcMetadata(string TokenEndpoint, string? DeviceAuthorizationEndpoint);

internal sealed record TokenResponse(string AccessToken, string? RefreshToken, TimeSpan? ExpiresIn);

internal sealed record DeviceAuthorization(
    string DeviceCode, string UserCode, string VerificationUri, string? VerificationUriComplete, TimeSpan ExpiresIn, TimeSpan Interval);

/// <summary>An OAuth error response from the identity provider, such as <c>authorization_pending</c> or <c>invalid_client</c>.</summary>
internal sealed class OAuthProtocolException(string error, string? description) : Exception(description is null ? error : $"{error}: {description}")
{
    public string Error { get; } = error;
}

/// <summary>
/// Talks to the OpenID Connect identity provider: finds its endpoints from the discovery document, and performs the token and device
/// authorization requests. Secrets are only ever sent to the token endpoint over HTTPS (or to loopback) and never appear in exceptions.
/// </summary>
internal sealed class IdentityProviderClient(HttpClient http, ServiceControlDiscovery discovery, IOptions<ServiceControlMcpOptions> options) : IDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    readonly SemaphoreSlim gate = new(1, 1);
    OidcMetadata? metadata;

    public async Task<OidcMetadata> GetMetadataAsync(CancellationToken cancellationToken)
    {
        if (metadata is not null)
        {
            return metadata;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (metadata is not null)
            {
                return metadata;
            }

            var authority = options.Value.Auth.Authority;
            if (string.IsNullOrWhiteSpace(authority))
            {
                authority = (await discovery.GetAsync(cancellationToken).ConfigureAwait(false)).Authority;
            }

            if (string.IsNullOrWhiteSpace(authority))
            {
                throw new TokenAcquisitionException(
                    "ServiceControl does not advertise an authority to sign in with (is authentication enabled?). Set Auth:Authority to the OpenID Connect authority URL.");
            }

            var url = new Uri(authority.TrimEnd('/') + "/.well-known/openid-configuration");
            EnsureSecure(url);

            try
            {
                var document = await http.GetFromJsonAsync<DiscoveryDocument>(url, Json, cancellationToken).ConfigureAwait(false);
                metadata = string.IsNullOrWhiteSpace(document?.TokenEndpoint)
                    ? throw new TokenAcquisitionException($"The identity provider at {authority} did not publish a token endpoint.")
                    : new OidcMetadata(document.TokenEndpoint, document.DeviceAuthorizationEndpoint);
                return metadata;
            }
            catch (HttpRequestException ex)
            {
                throw new TokenAcquisitionException($"Could not reach the identity provider at {authority}: {ex.Message}", ex);
            }
            catch (JsonException ex)
            {
                throw new TokenAcquisitionException($"The identity provider at {authority} returned an unrecognisable discovery document.", ex);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Sends a token request. The client authenticates with HTTP Basic when it has a secret, otherwise it identifies itself in the form.</summary>
    public async Task<TokenResponse> RequestTokenAsync(
        Dictionary<string, string> form, string clientId, string? clientSecret, CancellationToken cancellationToken)
    {
        var endpoint = (await GetMetadataAsync(cancellationToken).ConfigureAwait(false)).TokenEndpoint;
        using var response = await PostFormAsync(endpoint, form, clientId, clientSecret, cancellationToken).ConfigureAwait(false);
        var body = await ReadAsync<TokenBody>(response, endpoint, cancellationToken).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(body.AccessToken)
            ? throw new TokenAcquisitionException("The identity provider answered the token request without an access token.")
            : new TokenResponse(body.AccessToken, body.RefreshToken, body.ExpiresIn is > 0 ? TimeSpan.FromSeconds(body.ExpiresIn.Value) : null);
    }

    public async Task<DeviceAuthorization> StartDeviceAuthorizationAsync(string clientId, string scope, CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = metadata.DeviceAuthorizationEndpoint
            ?? throw new TokenAcquisitionException("The identity provider does not support the device authorization flow (no device_authorization_endpoint).");

        using var response = await PostFormAsync(endpoint, new Dictionary<string, string> { ["scope"] = scope }, clientId, clientSecret: null, cancellationToken).ConfigureAwait(false);
        var body = await ReadAsync<DeviceBody>(response, endpoint, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(body.DeviceCode) || string.IsNullOrWhiteSpace(body.UserCode) || string.IsNullOrWhiteSpace(body.VerificationUri))
        {
            throw new TokenAcquisitionException("The identity provider's device authorization response was incomplete.");
        }

        return new DeviceAuthorization(
            body.DeviceCode,
            body.UserCode,
            body.VerificationUri,
            body.VerificationUriComplete,
            TimeSpan.FromSeconds(body.ExpiresIn is > 0 ? body.ExpiresIn.Value : 600),
            TimeSpan.FromSeconds(body.Interval is > 0 ? body.Interval.Value : 5));
    }

    async Task<HttpResponseMessage> PostFormAsync(
        string endpoint, Dictionary<string, string> form, string clientId, string? clientSecret, CancellationToken cancellationToken)
    {
        var url = new Uri(endpoint);
        EnsureSecure(url);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (clientSecret is null)
        {
            form = new Dictionary<string, string>(form) { ["client_id"] = clientId };
        }
        else
        {
            // RFC 6749 section 2.3.1: the id and secret are form-encoded, then sent as HTTP Basic credentials.
            var credentials = $"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(clientSecret)}";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }

        request.Content = new FormUrlEncodedContent(form);
        try
        {
            return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TokenAcquisitionException($"Could not reach the identity provider at {url.GetLeftPart(UriPartial.Authority)}: {ex.Message}", ex);
        }
    }

    static async Task<T> ReadAsync<T>(HttpResponseMessage response, string endpoint, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // OAuth reports problems as {"error": "...", "error_description": "..."}; those are safe to show and useful to act on.
            try
            {
                var error = JsonSerializer.Deserialize<ErrorBody>(text, Json);
                if (!string.IsNullOrWhiteSpace(error?.Error))
                {
                    throw new OAuthProtocolException(error.Error, error.ErrorDescription);
                }
            }
            catch (JsonException)
            {
                // Not an OAuth error document; fall through to the generic message.
            }

            throw new TokenAcquisitionException($"The identity provider at {new Uri(endpoint).GetLeftPart(UriPartial.Authority)} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, Json) ?? throw new TokenAcquisitionException("The identity provider returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new TokenAcquisitionException("The identity provider returned a response that could not be understood.", ex);
        }
    }

    void EnsureSecure(Uri url)
    {
        if (url.Scheme == Uri.UriSchemeHttps || url.IsLoopback || options.Value.AllowInsecureTransport)
        {
            return;
        }

        throw new TokenAcquisitionException(
            $"Refusing to contact the identity provider at {url.GetLeftPart(UriPartial.Authority)} over plain HTTP: it would expose credentials. Use HTTPS, or set AllowInsecureTransport for a trusted network.");
    }

    public void Dispose() => gate.Dispose();

    sealed record DiscoveryDocument(string? TokenEndpoint, string? DeviceAuthorizationEndpoint);

    sealed record TokenBody(string? AccessToken, string? RefreshToken, int? ExpiresIn);

    sealed record DeviceBody(string? DeviceCode, string? UserCode, string? VerificationUri, string? VerificationUriComplete, int? ExpiresIn, int? Interval);

    sealed record ErrorBody(string? Error, [property: JsonPropertyName("error_description")] string? ErrorDescription);
}
