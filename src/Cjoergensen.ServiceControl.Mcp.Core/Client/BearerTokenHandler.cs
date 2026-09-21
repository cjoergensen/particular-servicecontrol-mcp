using System.Net;
using System.Net.Http.Headers;
using Cjoergensen.ServiceControl.Mcp.Auth;
using Microsoft.Extensions.Options;

namespace Cjoergensen.ServiceControl.Mcp.Client;

/// <summary>
/// Adds the bearer token to every request to ServiceControl. Refuses to send a token over plain HTTP to a non-loopback host,
/// and retries once with a fresh token when ServiceControl answers 401 (a rejected request has not been processed, so
/// repeating it is safe for every method).
/// </summary>
internal sealed class BearerTokenHandler(ITokenProvider tokens, IOptions<ServiceControlMcpOptions> options) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? token;
        try
        {
            token = await tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TokenAcquisitionException ex)
        {
            throw new ServiceControlApiException(ServiceControlFailureKind.CredentialsUnavailable, $"Could not obtain a token for ServiceControl. {ex.Message}", ex);
        }

        if (token is null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        EnsureTokenMaySendTo(request.RequestUri);

        var snapshot = await RequestSnapshot.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        // The token may simply have expired or been revoked: get a new one and try once more.
        tokens.Invalidate();
        string? refreshed;
        try
        {
            refreshed = await tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TokenAcquisitionException)
        {
            return response;
        }

        if (refreshed is null || refreshed == token)
        {
            return response;
        }

        response.Dispose();
        using var retry = snapshot.CreateRequest();
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    void EnsureTokenMaySendTo(Uri? uri)
    {
        if (uri is null || uri.Scheme == Uri.UriSchemeHttps || options.Value.AllowInsecureTransport || IsLoopback(uri))
        {
            return;
        }

        throw new ServiceControlApiException(
            ServiceControlFailureKind.InsecureTransport,
            $"Refusing to send credentials to {uri.GetLeftPart(UriPartial.Authority)} over plain HTTP. Use an https:// URL " +
            "(ServiceControl requires HTTPS when authentication is enabled), or set AllowInsecureTransport for a trusted network.");
    }

    static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>A copy of a request that can be sent again, because an <see cref="HttpRequestMessage"/> can only be sent once.</summary>
    sealed class RequestSnapshot
    {
        HttpMethod method = HttpMethod.Get;
        Uri? uri;
        byte[]? content;
        List<KeyValuePair<string, IEnumerable<string>>> contentHeaders = [];
        List<KeyValuePair<string, IEnumerable<string>>> headers = [];

        public static async Task<RequestSnapshot> CaptureAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var snapshot = new RequestSnapshot
            {
                method = request.Method,
                uri = request.RequestUri,
                headers = [.. request.Headers]
            };

            if (request.Content is not null)
            {
                snapshot.content = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                snapshot.contentHeaders = [.. request.Content.Headers];
            }

            return snapshot;
        }

        public HttpRequestMessage CreateRequest()
        {
            var request = new HttpRequestMessage(method, uri);
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (content is not null)
            {
                request.Content = new ByteArrayContent(content);
                foreach (var header in contentHeaders)
                {
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return request;
        }
    }
}
