using System.ComponentModel;
using Cjoergensen.ServiceControl.Mcp.Client;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Cjoergensen.ServiceControl.Mcp.Tools;

public sealed record GroupOperationState(string? Status, double Progress, int? Remaining, bool? Failed, DateTime? StartedAt, DateTime? CompletedAt);

public sealed record FailureGroupSummary(
    string? Id,
    string? Title,
    string? Type,
    int Count,
    DateTime? First,
    DateTime? Last,
    string? Comment,
    GroupOperationState? Operation);

[McpServerToolType]
public sealed class FailureGroupTools(ServiceControlClient client, IOptions<ServiceControlMcpOptions> options)
{
    [McpServerTool(Name = "list_failure_classifiers", Title = "List failure classifiers", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the ways ServiceControl can group failed messages, for use as the classifier of list_failure_groups. The default is 'Exception Type and Stack Trace'.")]
    public async Task<IReadOnlyList<string>> ListFailureClassifiers(CancellationToken cancellationToken = default) =>
        await client.GetClassifiersAsync(cancellationToken).ConfigureAwait(false);

    [McpServerTool(Name = "list_failure_groups", Title = "List failure groups", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Groups unresolved failed messages by a common cause (by default the same exception type and stack trace), largest first. This is the best way to see 'what is failing and how much', " +
        "and to answer questions about a specific exception type: find its group, then use list_failure_group_messages. If a retry or archive is running on a group, its progress is in 'operation'.")]
    public async Task<IReadOnlyList<FailureGroupSummary>> ListFailureGroups(
        [Description("How to group: 'Exception Type and Stack Trace' (default), or another classifier from list_failure_classifiers such as 'Message Type'.")]
        string? classifier = null,
        CancellationToken cancellationToken = default)
    {
        var groups = await client.GetFailureGroupsAsync(
            string.IsNullOrWhiteSpace(classifier) ? ServiceControlClient.DefaultClassifier : classifier,
            cancellationToken).ConfigureAwait(false);

        return [.. groups.OrderByDescending(g => g.Count).Select(Summarize)];
    }

    [McpServerTool(Name = "list_failure_group_messages", Title = "List failed messages in a group", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the failed messages that belong to one failure group (use the group id from list_failure_groups). Paged like list_failed_messages.")]
    public async Task<PagedResult<FailedMessageSummary>> ListFailureGroupMessages(
        [Description("The failure group id from list_failure_groups.")]
        string groupId,
        [Description("Status filter: unresolved (default), resolved, archived or retryIssued; commas combine, a leading minus excludes.")]
        string? status = "unresolved",
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
        if (string.IsNullOrWhiteSpace(groupId))
        {
            throw new ToolInputException("groupId is required. Use the 'id' of a group from list_failure_groups.");
        }

        var (pageNumber, size) = ToolArguments.Paging(page, pageSize, options.Value);
        var result = await client.GetGroupFailedMessagesAsync(
            groupId,
            ToolArguments.Status(status),
            ToolArguments.SortField(sortBy),
            ToolArguments.SortDirection(direction),
            pageNumber,
            size,
            cancellationToken).ConfigureAwait(false);

        return Paged.From(result, pageNumber, size, FailedMessageTools.Summarize);
    }

    internal static FailureGroupSummary Summarize(GroupOperation group) => new(
        group.Id,
        group.Title,
        group.Type,
        group.Count,
        group.First,
        group.Last,
        group.Comment,
        group.OperationStatus is null || group.OperationStatus == "None"
            ? null
            : new GroupOperationState(
                group.OperationStatus,
                group.OperationProgress,
                group.OperationRemainingCount,
                group.OperationFailed,
                group.OperationStartTime,
                group.OperationCompletionTime));
}
