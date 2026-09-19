using System.ComponentModel;
using System.Text.Json;
using Cjoergensen.ServiceControl.Mcp.Client;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Cjoergensen.ServiceControl.Mcp.Tools;

public sealed record SagaChange(
    DateTime? StartTime,
    DateTime? FinishTime,
    string? Status,
    string? Endpoint,
    string? StateAfterChange,
    JsonElement? InitiatingMessage,
    JsonElement? OutgoingMessages);

public sealed record SagaHistoryResult(Guid SagaId, string? SagaType, int TotalChanges, IReadOnlyList<SagaChange> Changes, bool Truncated);

public sealed record AuditedMessage(
    string? Id,
    string? MessageId,
    string? MessageType,
    string? Status,
    string? SendingEndpoint,
    string? ReceivingEndpoint,
    DateTime? TimeSent,
    DateTime? ProcessedAt,
    double? ProcessingTimeMs,
    double? CriticalTimeMs,
    string? ConversationId);

[McpServerToolType]
public sealed class AuditTools(ServiceControlClient client, IOptions<ServiceControlMcpOptions> options)
{
    [McpServerTool(Name = "get_saga_history", Title = "Get saga audit history", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Returns the audit trail of one saga: each state change with the message that triggered it, the state after the change and the messages it sent. " +
        "Needs the saga id (a GUID) and requires an audit instance with saga auditing. Long histories return the most recent changes.")]
    public async Task<SagaHistoryResult> GetSagaHistory(
        [Description("The saga id (GUID).")]
        Guid sagaId,
        [Description("Most recent state changes to return.")]
        int maxChanges = 20,
        CancellationToken cancellationToken = default)
    {
        if (maxChanges is < 1 or > 200)
        {
            throw new ToolInputException("maxChanges must be between 1 and 200.");
        }

        var history = await client.GetSagaHistoryAsync(sagaId, cancellationToken).ConfigureAwait(false)
            ?? throw new ServiceControlApiException(
                ServiceControlFailureKind.NotFound,
                $"ServiceControl has no audit history for saga {sagaId}. The id may be wrong, saga auditing may not be enabled for the endpoint, " +
                "or the audit data may have passed its retention period.");
        var changes = (history.Changes ?? []).OrderBy(c => c.StartTime).ToList();
        var recent = changes.Skip(Math.Max(0, changes.Count - maxChanges)).ToList();

        return new SagaHistoryResult(
            history.SagaId,
            history.SagaType,
            changes.Count,
            [.. recent.Select(c => new SagaChange(
                c.StartTime,
                c.FinishTime,
                c.Status,
                c.Endpoint,
                ToolArguments.Truncate(c.StateAfterChange, 4000),
                c.InitiatingMessage,
                c.OutgoingMessages))],
            recent.Count < changes.Count);
    }

    [McpServerTool(Name = "search_messages", Title = "Search audited messages", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Full-text search over successfully processed (audited) messages: matches message ids, types, headers and bodies indexed by the audit instance. " +
        "Use it to trace a message end to end or find related messages. Returns metadata only, never message bodies.")]
    public async Task<PagedResult<AuditedMessage>> SearchMessages(
        [Description("Search text, for example a message id, an order number or a message type name.")]
        string query,
        [Description("Restrict to messages processed by this endpoint.")]
        string? endpoint = null,
        [Description("1-based page number.")]
        int? page = null,
        [Description("Items per page; capped by the server's configured maximum.")]
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ToolInputException("query is required.");
        }

        var (pageNumber, size) = ToolArguments.Paging(page, pageSize, options.Value);
        var result = await client.SearchMessagesAsync(query, endpoint, pageNumber, size, cancellationToken).ConfigureAwait(false);

        return Paged.From(result, pageNumber, size, m => new AuditedMessage(
            m.Id,
            m.MessageId,
            m.MessageType,
            m.Status,
            m.SendingEndpoint?.Name,
            m.ReceivingEndpoint?.Name,
            m.TimeSent,
            m.ProcessedAt,
            m.ProcessingTime?.TotalMilliseconds,
            m.CriticalTime?.TotalMilliseconds,
            m.ConversationId));
    }
}
