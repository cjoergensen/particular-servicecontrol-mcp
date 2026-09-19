using System.ComponentModel;
using Cjoergensen.ServiceControl.Mcp.Client;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cjoergensen.ServiceControl.Mcp.Tools;

public sealed record MonitoringHealth(int StaleEndpoints, int DisconnectedInstances, IReadOnlyList<string> StaleEndpointNames);

/// <summary>A snapshot of platform health. Sections that could not be loaded are null (and so left out of the serialized result) and explained in <see cref="Problems"/>.</summary>
public sealed record HealthOverview(
    FailedMessageCounts? FailedMessages,
    IReadOnlyList<FailureGroupSummary>? TopFailureGroups,
    HeartbeatStats? Heartbeats,
    IReadOnlyList<string>? DeadEndpoints,
    IReadOnlyList<CustomCheckSummary>? FailingCustomChecks,
    MonitoringHealth? Monitoring,
    IReadOnlyList<string> Problems);

[McpServerToolType]
public sealed class OverviewTools(
    ServiceControlClient client,
    MonitoringClient monitoringClient,
    IOptions<ServiceControlMcpOptions> options)
{
    const int TopGroups = 5;
    const int MaxItems = 20;

    [McpServerTool(Name = "get_health_overview", Title = "Get platform health overview", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "One-call health snapshot: failed message counts, the largest failure groups, endpoints with dead heartbeats, failing custom checks and (when a monitoring instance is configured) stale endpoints. " +
        "Start here for questions like 'is anything wrong right now?', then drill down with the specific tools. If a section could not be loaded it is left out of the result and 'problems' says why; " +
        "never assume a missing section means healthy.")]
    public async Task<HealthOverview> GetHealthOverview(CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();

        var counts = Section("failed message counts", problems, () => FailedMessageTools.CountAsync(client, cancellationToken));
        var groups = Section("failure groups", problems, async () =>
            (IReadOnlyList<FailureGroupSummary>)[.. (await client.GetFailureGroupsAsync(ServiceControlClient.DefaultClassifier, cancellationToken).ConfigureAwait(false))
                .OrderByDescending(g => g.Count).Take(TopGroups).Select(FailureGroupTools.Summarize)]);
        var heartbeats = Section("heartbeat statistics", problems, () => client.GetHeartbeatStatsAsync(cancellationToken));
        var dead = Section("endpoint heartbeats", problems, async () =>
            (IReadOnlyList<string>)[.. (await client.GetEndpointsAsync(cancellationToken).ConfigureAwait(false)).Items
                .Where(e => e.MonitorHeartbeat && string.Equals(e.HeartbeatInformation?.ReportedStatus, "dead", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Name ?? "(unnamed)").Order(StringComparer.OrdinalIgnoreCase).Take(MaxItems)]);
        var checks = Section("custom checks", problems, async () =>
            (IReadOnlyList<CustomCheckSummary>)[.. (await client.GetCustomChecksAsync("fail", 1, MaxItems, cancellationToken).ConfigureAwait(false)).Items
                .Select(c => new CustomCheckSummary(
                    c.CustomCheckId, c.Category, c.Status, c.ReportedAt, ToolArguments.Truncate(c.FailureReason, 500),
                    c.OriginatingEndpoint?.Name, c.OriginatingEndpoint?.Host, c.Internal, c.Id))]);

        Task<MonitoringHealth?> monitoring = string.IsNullOrWhiteSpace(options.Value.MonitoringUrl)
            ? Task.FromResult<MonitoringHealth?>(null)
            : Section("monitoring", problems, async () =>
            {
                var endpoints = await monitoringClient.GetMonitoredEndpointsAsync(null, cancellationToken).ConfigureAwait(false);
                var stale = endpoints.Where(e => e.IsStale).Select(e => e.Name ?? "(unnamed)").Order(StringComparer.OrdinalIgnoreCase).ToList();
                return new MonitoringHealth(stale.Count, endpoints.Sum(e => e.DisconnectedCount), [.. stale.Take(MaxItems)]);
            });

        await Task.WhenAll(counts, groups, heartbeats, dead, checks, monitoring).ConfigureAwait(false);

        return new HealthOverview(counts.Result, groups.Result, heartbeats.Result, dead.Result, checks.Result, monitoring.Result, problems);
    }

    /// <summary>Runs one section, turning a failure into a recorded problem so the other sections still return.</summary>
    static async Task<T?> Section<T>(string name, List<string> problems, Func<Task<T>> load)
        where T : class
    {
        try
        {
            return await load().ConfigureAwait(false);
        }
        catch (McpException ex)
        {
            lock (problems)
            {
                problems.Add($"{name}: {ex.Message}");
            }

            return null;
        }
    }
}
