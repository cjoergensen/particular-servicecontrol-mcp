using System.ComponentModel;
using Cjoergensen.ServiceControl.Mcp.Client;
using ModelContextProtocol.Server;

namespace Cjoergensen.ServiceControl.Mcp.Tools;

/// <summary>One metric: the average over the requested history and the most recent value.</summary>
public sealed record MetricValue(double? Average, double? Latest);

public sealed record MonitoredEndpointSummary(
    string? Name,
    bool IsStale,
    int ConnectedInstances,
    int DisconnectedInstances,
    IReadOnlyDictionary<string, MetricValue> Metrics);

public sealed record MonitoredInstanceSummary(string? Name, string? Id, bool IsStale, IReadOnlyDictionary<string, MetricValue> Metrics);

public sealed record MonitoredMessageTypeSummary(string? TypeName, IReadOnlyDictionary<string, MetricValue> Metrics);

public sealed record MonitoredEndpointResult(
    string Name,
    IReadOnlyDictionary<string, MetricValue> Metrics,
    IReadOnlyList<MonitoredInstanceSummary> Instances,
    IReadOnlyList<MonitoredMessageTypeSummary> MessageTypes);

[McpServerToolType]
public sealed class MonitoringTools(MonitoringClient client)
{
    const string Experimental =
        " EXPERIMENTAL: the monitoring instance API is designed for ServicePulse and may change between ServiceControl versions. " +
        "The monitoring instance only keeps about ten minutes of data, so this shows current behavior, not history. " +
        "Metric names are those reported by ServiceControl, for example queueLength, throughput, retries, processingTime and criticalTime (times in milliseconds).";

    [McpServerTool(Name = "list_monitored_endpoints", Title = "List monitored endpoints with metrics", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists endpoints reporting metrics to the monitoring instance with their current queue length, throughput, retries, processing time and critical time, and how many instances are connected. Stale endpoints have stopped reporting." + Experimental)]
    public async Task<IReadOnlyList<MonitoredEndpointSummary>> ListMonitoredEndpoints(
        [Description("Minutes of history to average over (the monitoring instance keeps about 10). Defaults to the server default.")]
        int? historyMinutes = null,
        CancellationToken cancellationToken = default)
    {
        ValidateHistory(historyMinutes);
        var endpoints = await client.GetMonitoredEndpointsAsync(historyMinutes, cancellationToken).ConfigureAwait(false);

        return [.. endpoints
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new MonitoredEndpointSummary(e.Name, e.IsStale, e.ConnectedCount, e.DisconnectedCount, Summarize(e.Metrics)))];
    }

    [McpServerTool(Name = "get_monitored_endpoint", Title = "Get metrics of one monitored endpoint", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Gets the metrics of one endpoint in detail: overall figures, each running instance, and per message type. Use it to find which instance or message type is slow or backing up." + Experimental)]
    public async Task<MonitoredEndpointResult> GetMonitoredEndpoint(
        [Description("The endpoint name, as shown by list_monitored_endpoints.")]
        string endpointName,
        [Description("Minutes of history to average over.")]
        int? historyMinutes = null,
        [Description("Most message types to return, busiest first by processing time.")]
        int maxMessageTypes = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpointName))
        {
            throw new ToolInputException("endpointName is required.");
        }

        ValidateHistory(historyMinutes);
        if (maxMessageTypes is < 1 or > 100)
        {
            throw new ToolInputException("maxMessageTypes must be between 1 and 100.");
        }

        var details = await client.GetMonitoredEndpointAsync(endpointName, historyMinutes, cancellationToken).ConfigureAwait(false);

        var overall = (details.Digest?.Metrics ?? [])
            .ToDictionary(m => m.Key, m => new MetricValue(m.Value.Average, m.Value.Latest));

        var instances = (details.Instances ?? [])
            .Select(i => new MonitoredInstanceSummary(i.Name, i.Id, i.IsStale, Summarize(i.Metrics)))
            .ToList();

        var messageTypes = (details.MessageTypes ?? [])
            .Select(t => new MonitoredMessageTypeSummary(t.TypeName, Summarize(t.Metrics)))
            .OrderByDescending(t => t.Metrics.TryGetValue("processingTime", out var m) ? m.Average ?? 0 : 0)
            .Take(maxMessageTypes)
            .ToList();

        return new MonitoredEndpointResult(endpointName, overall, instances, messageTypes);
    }

    internal static IReadOnlyDictionary<string, MetricValue> Summarize(Dictionary<string, MonitoredValues>? metrics) =>
        (metrics ?? []).ToDictionary(
            m => m.Key,
            m => new MetricValue(m.Value.Average, m.Value.Points is { Length: > 0 } points ? points[^1] : null));

    static void ValidateHistory(int? historyMinutes)
    {
        if (historyMinutes is < 1 or > 60)
        {
            throw new ToolInputException("historyMinutes must be between 1 and 60.");
        }
    }
}
