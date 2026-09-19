using System.ComponentModel;
using Cjoergensen.ServiceControl.Mcp.Client;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Cjoergensen.ServiceControl.Mcp.Tools;

public sealed record FailedMessageSummary(
    string Id,
    string? MessageId,
    string? MessageType,
    string? Status,
    string? Endpoint,
    string? QueueAddress,
    DateTime TimeOfFailure,
    DateTime LastModified,
    int ProcessingAttempts,
    string? ExceptionType,
    string? ExceptionMessage);

public sealed record FailedMessageCounts(long Unresolved, long RetryIssued, long Archived, long Resolved);

public sealed record FailureAttempt(
    DateTime? AttemptedAt,
    string? FailingEndpointAddress,
    DateTime? TimeOfFailure,
    string? ExceptionType,
    string? ExceptionMessage,
    string? Source,
    string? StackTrace,
    IReadOnlyDictionary<string, string>? Headers);

public sealed record FailedMessageDetails(
    string Id,
    string? Status,
    IReadOnlyList<FailureGroupReference> Groups,
    IReadOnlyList<FailureAttempt> Attempts);

[McpServerToolType]
public sealed class FailedMessageTools(ServiceControlClient client, IOptions<ServiceControlMcpOptions> options, TimeProvider timeProvider)
{
    const int MessageSnippetLength = 500;

    [McpServerTool(Name = "list_failed_messages", Title = "List failed messages", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Lists messages that failed processing (the ServiceControl error queue), newest failure first. Each item has the message id to use with " +
        "get_failed_message and the retry/archive tools, plus the failing endpoint and exception. Defaults to unresolved failures, which are the ones needing attention. " +
        "Results are paged: if hasMore is true, call again with the next page.")]
    public async Task<PagedResult<FailedMessageSummary>> ListFailedMessages(
        [Description("Filter by status: unresolved (default), resolved, archived or retryIssued. Combine with commas (unresolved,retryIssued) or exclude with a leading minus (-archived). Use an empty string for all statuses.")]
        string? status = "unresolved",
        [Description("Only failures of this endpoint, for example 'Billing'. Matches the endpoint name exactly.")]
        string? endpoint = null,
        [Description("Only failures from this queue address (all-endpoints listing only; ignored when endpoint is set).")]
        string? queueAddress = null,
        [Description("Only messages whose status last changed within this many minutes before modifiedTo (default: now), for example 60 for the last hour. Ignored when modifiedFrom is set.")]
        int? modifiedWithinMinutes = null,
        [Description("Only messages modified at or after this UTC time (ISO 8601, for example 2026-09-19T08:00:00Z).")]
        DateTime? modifiedFrom = null,
        [Description("Only messages modified at or before this UTC time (ISO 8601).")]
        DateTime? modifiedTo = null,
        [Description("Sort field: time_of_failure (default), modified, message_type, status or time_sent.")]
        string? sortBy = null,
        [Description("Sort direction: desc (default) or asc.")]
        string? direction = null,
        [Description("1-based page number.")]
        int? page = null,
        [Description("Items per page; capped by the server's configured maximum.")]
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var (pageNumber, size) = ToolArguments.Paging(page, pageSize, options.Value);

        if (modifiedWithinMinutes is <= 0)
        {
            throw new ToolInputException("modifiedWithinMinutes must be greater than 0.");
        }

        // ServiceControl needs both ends of the range. Resolve them here, from the injected clock, so the request is deterministic.
        if (modifiedFrom is not null || modifiedTo is not null || modifiedWithinMinutes is not null)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            modifiedTo ??= now;
            modifiedFrom ??= modifiedWithinMinutes is { } minutes ? modifiedTo.Value.AddMinutes(-minutes) : DateTime.UnixEpoch;
        }

        var query = new FailedMessagesQuery(
            Status: ToolArguments.Status(status),
            Endpoint: endpoint,
            QueueAddress: queueAddress,
            ModifiedFrom: modifiedFrom,
            ModifiedTo: modifiedTo,
            Sort: ToolArguments.SortField(sortBy),
            Direction: ToolArguments.SortDirection(direction),
            Page: pageNumber,
            PageSize: size);

        var result = await client.GetFailedMessagesAsync(query, cancellationToken).ConfigureAwait(false);
        return Paged.From(result, pageNumber, size, Summarize);
    }

    [McpServerTool(Name = "get_failed_message", Title = "Get failed message details", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Gets the full failure details of one failed message: every processing attempt with exception type, message and stack trace, and the failure groups it belongs to. " +
        "The message body is never returned. Use the id from list_failed_messages.")]
    public async Task<FailedMessageDetails> GetFailedMessage(
        [Description("The failed message id (the 'id' field from list_failed_messages).")]
        string id,
        [Description("Include the NServiceBus message headers of each attempt (verbose). Off by default.")]
        bool includeHeaders = false,
        [Description("Longest stack trace to return per attempt, in characters; longer ones are truncated.")]
        int maxStackTraceChars = 4000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ToolInputException("id is required. Use the 'id' of an item from list_failed_messages.");
        }

        if (maxStackTraceChars is < 100 or > 50_000)
        {
            throw new ToolInputException("maxStackTraceChars must be between 100 and 50000.");
        }

        var detail = await client.GetFailedMessageAsync(id, cancellationToken).ConfigureAwait(false);

        var attempts = (detail.ProcessingAttempts ?? [])
            .Select(attempt => new FailureAttempt(
                attempt.AttemptedAt,
                attempt.FailureDetails?.AddressOfFailingEndpoint,
                attempt.FailureDetails?.TimeOfFailure,
                attempt.FailureDetails?.Exception?.ExceptionType,
                ToolArguments.Truncate(attempt.FailureDetails?.Exception?.Message, MessageSnippetLength * 4),
                attempt.FailureDetails?.Exception?.Source,
                ToolArguments.Truncate(attempt.FailureDetails?.Exception?.StackTrace, maxStackTraceChars),
                includeHeaders ? attempt.Headers : null))
            .ToList();

        return new FailedMessageDetails(detail.UniqueMessageId ?? detail.Id ?? id, detail.Status, detail.FailureGroups ?? [], attempts);
    }

    [McpServerTool(Name = "get_failed_message_counts", Title = "Count failed messages by status", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns how many failed messages exist per status (unresolved, retryIssued, archived, resolved). A cheap way to gauge the size of a problem before listing.")]
    public Task<FailedMessageCounts> GetFailedMessageCounts(CancellationToken cancellationToken = default) =>
        CountAsync(client, cancellationToken);

    internal static async Task<FailedMessageCounts> CountAsync(ServiceControlClient client, CancellationToken cancellationToken)
    {
        var unresolved = client.CountFailedMessagesAsync("unresolved", cancellationToken);
        var retryIssued = client.CountFailedMessagesAsync("retryissued", cancellationToken);
        var archived = client.CountFailedMessagesAsync("archived", cancellationToken);
        var resolved = client.CountFailedMessagesAsync("resolved", cancellationToken);

        await Task.WhenAll(unresolved, retryIssued, archived, resolved).ConfigureAwait(false);
        return new FailedMessageCounts(unresolved.Result, retryIssued.Result, archived.Result, resolved.Result);
    }

    internal static FailedMessageSummary Summarize(FailedMessageView message) => new(
        message.Id,
        message.MessageId,
        message.MessageType,
        message.Status,
        message.ReceivingEndpoint?.Name,
        message.QueueAddress,
        message.TimeOfFailure,
        message.LastModified,
        message.NumberOfProcessingAttempts,
        message.Exception?.ExceptionType,
        ToolArguments.Truncate(message.Exception?.Message, MessageSnippetLength));
}
