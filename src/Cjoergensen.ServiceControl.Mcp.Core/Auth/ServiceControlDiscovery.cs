using System.Net.Http.Json;
using Cjoergensen.ServiceControl.Mcp.Client;
using Microsoft.Extensions.Options;

namespace Cjoergensen.ServiceControl.Mcp.Auth;

/// <summary>
/// Reads ServiceControl's anonymous authentication discovery document. It deliberately uses its own HTTP client without credentials: the
/// document is public, and the token providers that need it are themselves what supplies credentials to every other client.
/// </summary>
internal sealed class ServiceControlDiscovery(HttpClient http, IOptions<ServiceControlMcpOptions> options) : IDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    AuthConfiguration? cached;

    public async Task<AuthConfiguration> GetAsync(CancellationToken cancellationToken)
    {
        if (cached is not null)
        {
            return cached;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cached is not null)
            {
                return cached;
            }

            var url = new Uri(options.Value.Url.TrimEnd('/') + "/api/authentication/configuration");
            try
            {
                cached = await http.GetFromJsonAsync<AuthConfiguration>(url, ServiceControlClient.JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new TokenAcquisitionException($"ServiceControl at {options.Value.Url} returned an empty authentication configuration.");
                return cached;
            }
            catch (HttpRequestException ex)
            {
                throw new TokenAcquisitionException($"Could not read ServiceControl's authentication configuration from {url}: {ex.Message}", ex);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new TokenAcquisitionException($"ServiceControl at {url} did not return a recognisable authentication configuration.", ex);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}
