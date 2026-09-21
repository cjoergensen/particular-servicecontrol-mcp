using System.ComponentModel;
using Cjoergensen.ServiceControl.Mcp.Client;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Cjoergensen.ServiceControl.Mcp.Tools;

public sealed record EndpointHealth(
    string? Name,
    string? Host,
    bool HeartbeatMonitored,
    string? HeartbeatStatus,
    DateTime? LastHeartbeatAt);

public sealed record CustomCheckSummary(
    string? CustomCheckId,
    string? Category,
    string? Status,
    DateTime? ReportedAt,
    string? FailureReason,
    string? Endpoint,
    string? Host,
    bool Internal,
    string? Id);

[McpServerToolType]
public sealed class HealthTools(ServiceControlClient client, IOptions<ServiceControlMcpOptions> options)
{
    [McpServerTool(Name = "list_endpoints", Title = "List endpoints and heartbeat status", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Lists the endpoints ServiceControl knows about with their heartbeat status: 'beating' (healthy), 'dead' (heartbeat missed) or unknown when heartbeats are not monitored for that endpoint. " +
        "Use heartbeatStatus='dead' to find endpoints that stopped reporting.")]
    public async Task<PagedResult<EndpointHealth>> ListEndpoints(
        [Description("Only endpoints with this heartbeat status: beating or dead.")]
        string? heartbeatStatus = null,
        [Description("Only endpoints that have heartbeat monitoring enabled.")]
        bool monitoredOnly = false,
        [Description("1-based page number.")]
        int? page = null,
        [Description("Items per page; capped by the server's configured maximum.")]
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var (pageNumber, size) = ToolArguments.Paging(page, pageSize, options.Value);

        if (heartbeatStatus is not null && !heartbeatStatus.Equals("beating", StringComparison.OrdinalIgnoreCase) &&
            !heartbeatStatus.Equals("dead", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolInputException("heartbeatStatus must be 'beating' or 'dead'.");
        }

        var all = await client.GetEndpointsAsync(cancellationToken).ConfigureAwait(false);

        // ServiceControl returns every endpoint in one response, so filtering and paging happen here.
        var filtered = all.Items
            .Where(e => !monitoredOnly || e.MonitorHeartbeat)
            .Where(e => heartbeatStatus is null || string.Equals(e.HeartbeatInformation?.ReportedStatus, heartbeatStatus, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = filtered.Skip((pageNumber - 1) * size).Take(size).Select(ToHealth).ToList();
        return new PagedResult<EndpointHealth>(
            items, pageNumber, size, filtered.Count, (long)pageNumber * size < filtered.Count,
            all.IncompleteInstances.Count == 0 ? null : all.IncompleteInstances);
    }

    [McpServerTool(Name = "get_heartbeat_stats", Title = "Get heartbeat statistics", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns how many monitored endpoint instances are actively sending heartbeats and how many are failing (heartbeats missed).")]
    public Task<HeartbeatStats> GetHeartbeatStats(CancellationToken cancellationToken = default) =>
        client.GetHeartbeatStatsAsync(cancellationToken);

    [McpServerTool(Name = "list_custom_checks", Title = "List custom checks", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Lists custom checks: business or infrastructure health conditions reported by endpoints (and ServiceControl's own internal checks), with their pass/fail state and failure reason. " +
        "Defaults to failing checks. Use the 'id' with dismiss_custom_check to remove a stale entry.")]
    public async Task<PagedResult<CustomCheckSummary>> ListCustomChecks(
        [Description("Filter by state: fail (default) or pass. Use an empty string for both.")]
        string? status = "fail",
        [Description("1-based page number.")]
        int? page = null,
        [Description("Items per page; capped by the server's configured maximum.")]
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var (pageNumber, size) = ToolArguments.Paging(page, pageSize, options.Value);

        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            normalized = status.Trim().ToLowerInvariant();
            if (normalized is not ("fail" or "pass"))
            {
                throw new ToolInputException("status must be 'fail' or 'pass'.");
            }
        }

        var result = await client.GetCustomChecksAsync(normalized, pageNumber, size, cancellationToken).ConfigureAwait(false);
        return Paged.From(result, pageNumber, size, c => new CustomCheckSummary(
            c.CustomCheckId,
            c.Category,
            c.Status,
            c.ReportedAt,
            ToolArguments.Truncate(c.FailureReason, 1000),
            c.OriginatingEndpoint?.Name,
            c.OriginatingEndpoint?.Host,
            c.Internal,
            c.Id));
    }

    static EndpointHealth ToHealth(EndpointView endpoint) => new(
        endpoint.Name,
        endpoint.HostDisplayName,
        endpoint.MonitorHeartbeat,
        endpoint.MonitorHeartbeat ? endpoint.HeartbeatInformation?.ReportedStatus : null,
        endpoint.HeartbeatInformation?.LastReportAt);
}
