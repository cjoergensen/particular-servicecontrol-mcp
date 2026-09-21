using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cjoergensen.ServiceControl.Mcp.Client;

/// <summary>
/// Typed client for the ServiceControl monitoring instance. Its API is documented as "designed for use by ServicePulse only and
/// may change at any time", so parsing is tolerant and the tools built on it are marked experimental.
/// </summary>
public sealed class MonitoringClient : ApiClientBase
{
    /// <summary>JSON options for the monitoring API: camelCase names and enum strings.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public MonitoringClient(HttpClient http) : base(http, JsonOptions)
    {
    }

    /// <param name="historyMinutes">Length of the metric history to include, in minutes. The monitoring instance keeps about ten minutes.</param>
    public async Task<IReadOnlyList<MonitoredEndpoint>> GetMonitoredEndpointsAsync(int? historyMinutes, CancellationToken cancellationToken)
    {
        var qs = new QueryBuilder().Add("history", historyMinutes);
        return await GetAsync<List<MonitoredEndpoint>>($"monitored-endpoints{qs}", cancellationToken).ConfigureAwait(false);
    }

    public Task<MonitoredEndpointDetails> GetMonitoredEndpointAsync(string endpointName, int? historyMinutes, CancellationToken cancellationToken)
    {
        var qs = new QueryBuilder().Add("history", historyMinutes);
        return GetAsync<MonitoredEndpointDetails>($"monitored-endpoints/{Uri.EscapeDataString(endpointName)}{qs}", cancellationToken);
    }

    public Task<int> GetDisconnectedEndpointCountAsync(CancellationToken cancellationToken) =>
        GetAsync<int>("monitored-endpoints/disconnected", cancellationToken);
}
