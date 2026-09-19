using System.ComponentModel;
using Cjoergensen.ServiceControl.Mcp.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Cjoergensen.ServiceControl.Mcp.Tools;

/// <summary>Result of a state-changing call. ServiceControl performs these operations asynchronously, so <see cref="Accepted"/> means queued, not finished.</summary>
public sealed record WriteOutcome(string Operation, string Target, bool Accepted, string Message);

/// <summary>
/// Tools that change state. They are only registered when <see cref="ServiceControlMcpOptions.EnableWrites"/> is set, so a default
/// installation is read-only. ServiceControl still enforces its own roles (the writer role) on every call.
/// </summary>
[McpServerToolType]
public sealed partial class WriteTools(ServiceControlClient client, IOptions<ServiceControlMcpOptions> options, ILogger<WriteTools> logger)
{
    const string Confirm =
        " THIS CHANGES STATE: before calling, tell the user exactly what will happen and get their explicit confirmation in this conversation.";

    const string Asynchronous =
        "ServiceControl accepted the request and will carry it out in the background; this is not confirmation that it finished. ";

    // ---- Retry -----------------------------------------------------------------------------------------------------

    [McpServerTool(Name = "retry_failed_message", Title = "Retry one failed message", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Sends one failed message back to its endpoint for reprocessing." + Confirm)]
    public async Task<WriteOutcome> RetryFailedMessage(
        [Description("The failed message id from list_failed_messages.")]
        string id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ToolInputException("id is required. Use the 'id' of an item from list_failed_messages.");
        }

        await client.RetryFailedMessageAsync(id, cancellationToken).ConfigureAwait(false);
        LogWrite("retry_failed_message", id, 1);
        return Accepted("retry", id, Asynchronous + "Check the outcome with get_failed_message.");
    }

    [McpServerTool(Name = "retry_failed_messages", Title = "Retry several failed messages", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Sends the named failed messages back to their endpoints for reprocessing. For everything from one endpoint or one failure group use retry_endpoint_failures or retry_failure_group." + Confirm)]
    public async Task<WriteOutcome> RetryFailedMessages(
        [Description("Failed message ids from list_failed_messages.")]
        string[] ids,
        CancellationToken cancellationToken = default)
    {
        var validated = ValidateIds(ids);
        await client.RetryFailedMessagesAsync(validated, cancellationToken).ConfigureAwait(false);
        LogWrite("retry_failed_messages", "ids", validated.Count);
        return Accepted("retry", $"{validated.Count} messages", Asynchronous + "Check progress with get_failed_message_counts.");
    }

    [McpServerTool(Name = "retry_endpoint_failures", Title = "Retry all failed messages of an endpoint", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Retries every unresolved failed message of one endpoint. This can affect many messages, so you must pass expectedCount: the number of unresolved failures you have just seen and confirmed with the user. " +
        "The call is refused if the current number differs." + Confirm)]
    public async Task<WriteOutcome> RetryEndpointFailures(
        [Description("The endpoint name, exactly as in list_failed_messages.")]
        string endpoint,
        [Description("How many unresolved failed messages this endpoint has, as confirmed with the user. Refused if it no longer matches.")]
        int expectedCount,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ToolInputException("endpoint is required.");
        }

        var current = (await client.GetFailedMessagesAsync(new FailedMessagesQuery(Status: "unresolved", Endpoint: endpoint, PageSize: 1), cancellationToken).ConfigureAwait(false))
            .TotalCount ?? 0;
        EnsureCount($"endpoint '{endpoint}'", expectedCount, current);

        await client.RetryEndpointFailuresAsync(endpoint, cancellationToken).ConfigureAwait(false);
        LogWrite("retry_endpoint_failures", endpoint, current);
        return Accepted("retry endpoint", endpoint, Asynchronous + $"{current} messages are being retried. Check progress with get_failed_message_counts.");
    }

    [McpServerTool(Name = "retry_failure_group", Title = "Retry a failure group", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Retries every unresolved failed message in one failure group (messages failing for the same reason). Pass expectedCount: the group's message count you have just seen and confirmed with the user; " +
        "the call is refused if the current number differs. Progress appears in the group's 'operation' in list_failure_groups." + Confirm)]
    public async Task<WriteOutcome> RetryFailureGroup(
        [Description("The failure group id from list_failure_groups.")]
        string groupId,
        [Description("How many unresolved failed messages the group has, as confirmed with the user. Refused if it no longer matches.")]
        int expectedCount,
        CancellationToken cancellationToken = default)
    {
        var current = await CountGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        EnsureCount($"failure group '{groupId}'", expectedCount, current);

        await client.RetryFailureGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        LogWrite("retry_failure_group", groupId, current);
        return Accepted("retry group", groupId, Asynchronous + $"{current} messages are being retried. Track it in list_failure_groups.");
    }

    // ---- Archive ---------------------------------------------------------------------------------------------------

    [McpServerTool(Name = "archive_failed_messages", Title = "Archive failed messages", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Archives the named failed messages: they leave the unresolved list and will not be retried, and are removed when ServiceControl's retention period ends. Undo with unarchive_failed_messages while they are still retained." + Confirm)]
    public async Task<WriteOutcome> ArchiveFailedMessages(
        [Description("Failed message ids from list_failed_messages.")]
        string[] ids,
        CancellationToken cancellationToken = default)
    {
        var validated = ValidateIds(ids);
        await client.ArchiveFailedMessagesAsync(validated, cancellationToken).ConfigureAwait(false);
        LogWrite("archive_failed_messages", "ids", validated.Count);
        return Accepted("archive", $"{validated.Count} messages", Asynchronous);
    }

    [McpServerTool(Name = "archive_failure_group", Title = "Archive a failure group", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Archives every unresolved failed message in one failure group. Pass expectedCount: the group's message count you have just seen and confirmed with the user; the call is refused if the current number differs." + Confirm)]
    public async Task<WriteOutcome> ArchiveFailureGroup(
        [Description("The failure group id from list_failure_groups.")]
        string groupId,
        [Description("How many unresolved failed messages the group has, as confirmed with the user. Refused if it no longer matches.")]
        int expectedCount,
        CancellationToken cancellationToken = default)
    {
        var current = await CountGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        EnsureCount($"failure group '{groupId}'", expectedCount, current);

        await client.ArchiveFailureGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        LogWrite("archive_failure_group", groupId, current);
        return Accepted("archive group", groupId, Asynchronous + $"{current} messages are being archived. Track it in list_failure_groups.");
    }

    [McpServerTool(Name = "unarchive_failed_messages", Title = "Unarchive failed messages", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Moves archived failed messages back to unresolved so they can be retried." + Confirm)]
    public async Task<WriteOutcome> UnarchiveFailedMessages(
        [Description("Failed message ids of archived messages.")]
        string[] ids,
        CancellationToken cancellationToken = default)
    {
        var validated = ValidateIds(ids);
        await client.UnarchiveFailedMessagesAsync(validated, cancellationToken).ConfigureAwait(false);
        LogWrite("unarchive_failed_messages", "ids", validated.Count);
        return Accepted("unarchive", $"{validated.Count} messages", Asynchronous);
    }

    // ---- Custom checks ---------------------------------------------------------------------------------------------

    [McpServerTool(Name = "dismiss_custom_check", Title = "Dismiss a custom check", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Removes a custom check entry from ServiceControl, as the delete action in ServicePulse does. Use it for a stale check from an endpoint that no longer exists. " +
        "It does NOT fix the underlying problem: a check that is still failing will be reported again by its endpoint. Pass the 'id' exactly as list_custom_checks returns it." + Confirm)]
    public async Task<WriteOutcome> DismissCustomCheck(
        [Description("The custom check 'id' exactly as list_custom_checks returns it (for example CustomChecks/0f8fad5b-d9cb-469f-a165-70867728950e).")]
        string id,
        CancellationToken cancellationToken = default)
    {
        var guid = ParseCustomCheckId(id);
        await client.DeleteCustomCheckAsync(guid, cancellationToken).ConfigureAwait(false);
        LogWrite("dismiss_custom_check", guid.ToString(), 1);
        return Accepted("dismiss custom check", id.Trim(), Asynchronous);
    }

    /// <summary>ServiceControl lists custom checks as <c>CustomChecks/{guid}</c> but deletes them by the bare GUID; accept either form.</summary>
    internal static Guid ParseCustomCheckId(string? id)
    {
        var candidate = id?.Trim() ?? string.Empty;
        var slash = candidate.LastIndexOf('/');
        if (slash >= 0)
        {
            candidate = candidate[(slash + 1)..];
        }

        return Guid.TryParse(candidate, out var guid)
            ? guid
            : throw new ToolInputException("id must be a custom check id from list_custom_checks, for example CustomChecks/0f8fad5b-d9cb-469f-a165-70867728950e.");
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    static WriteOutcome Accepted(string operation, string target, string message) => new(operation, target, Accepted: true, message);

    List<string> ValidateIds(string[]? ids)
    {
        var cleaned = (ids ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct(StringComparer.Ordinal).ToList();

        if (cleaned.Count == 0)
        {
            throw new ToolInputException("ids must contain at least one failed message id.");
        }

        var max = options.Value.MaxBatchSize;
        if (cleaned.Count > max)
        {
            throw new ToolInputException(
                $"{cleaned.Count} ids is more than the allowed {max} per call. Split the list, or use retry_failure_group / retry_endpoint_failures for large jobs.");
        }

        return cleaned;
    }

    async Task<long> CountGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            throw new ToolInputException("groupId is required. Use the 'id' of a group from list_failure_groups.");
        }

        return await client.CountGroupFailedMessagesAsync(groupId, "unresolved", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a bulk operation when the caller's picture is stale. Best effort: messages can still change between this check and the
    /// operation, but it stops an agent acting on numbers the user never saw.
    /// </summary>
    static void EnsureCount(string scope, int expected, long current)
    {
        if (current == 0)
        {
            throw new ToolInputException($"There are no unresolved failed messages in {scope}, so there is nothing to do.");
        }

        if (expected != current)
        {
            throw new ToolInputException(
                $"Refusing to proceed: {scope} has {current} unresolved failed messages now, but expectedCount was {expected}. " +
                $"Tell the user the current number and, if they still want to go ahead, call again with expectedCount={current}.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Write operation {Operation} on {Target} ({Count} message(s)) sent to ServiceControl")]
    partial void LogWrite(string operation, string target, long count);
}
