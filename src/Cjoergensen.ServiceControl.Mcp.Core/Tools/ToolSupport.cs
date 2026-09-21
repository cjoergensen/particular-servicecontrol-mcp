using ModelContextProtocol;

namespace Cjoergensen.ServiceControl.Mcp.Tools;

/// <summary>A tool argument was invalid. Derives from <see cref="McpException"/> so the explanation reaches the LLM and it can correct the call.</summary>
public sealed class ToolInputException(string message) : McpException(message);

/// <summary>Envelope for list results. Makes truncation and missing data explicit so an agent does not mistake a page for the whole picture.</summary>
/// <param name="Items">Items on this page.</param>
/// <param name="Page">The 1-based page returned.</param>
/// <param name="PageSize">The page size used.</param>
/// <param name="TotalCount">Total matching items across all pages, when known.</param>
/// <param name="HasMore">True when more pages exist; ask for the next <c>page</c> to continue.</param>
/// <param name="IncompleteInstances">
/// ServiceControl instances (for example audit instances) that did not answer, so this result is missing their data. Null when complete.
/// </param>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long? TotalCount,
    bool HasMore,
    IReadOnlyList<string>? IncompleteInstances = null);

internal static class Paged
{
    public static PagedResult<T> From<TSource, T>(Client.ApiPage<TSource> page, int pageNumber, int pageSize, Func<TSource, T> map)
    {
        var items = page.Items.Select(map).ToList();
        var hasMore = page.TotalCount is { } total ? (long)pageNumber * pageSize < total : items.Count >= pageSize;
        return new PagedResult<T>(items, pageNumber, pageSize, page.TotalCount, hasMore, page.IncompleteInstances.Count == 0 ? null : page.IncompleteInstances);
    }
}

internal static class ToolArguments
{
    static readonly string[] KnownStatuses = ["unresolved", "resolved", "archived", "retryissued"];
    static readonly string[] FailedMessageSortFields = ["time_of_failure", "modified", "message_type", "status", "time_sent"];

    public static (int Page, int PageSize) Paging(int? page, int? pageSize, ServiceControlMcpOptions options)
    {
        if (page is < 1)
        {
            throw new ToolInputException("page must be 1 or greater.");
        }

        if (pageSize is < 1)
        {
            throw new ToolInputException("pageSize must be 1 or greater.");
        }

        return (page ?? 1, Math.Min(pageSize ?? options.DefaultPageSize, options.MaxPageSize));
    }

    /// <summary>Validates a failed-message status filter such as <c>unresolved</c> or <c>unresolved,retryIssued</c> or <c>-archived</c>.</summary>
    public static string? Status(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var parts = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var name = part.TrimStart('-');
            if (!KnownStatuses.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new ToolInputException(
                    $"Unknown status '{part}'. Valid statuses are unresolved, resolved, archived and retryIssued; combine with commas, or prefix with - to exclude.");
            }
        }

        return string.Join(',', parts);
    }

    public static string SortField(string? sortBy)
    {
        if (string.IsNullOrWhiteSpace(sortBy))
        {
            return "time_of_failure";
        }

        var normalized = sortBy.Trim().ToLowerInvariant();
        return FailedMessageSortFields.Contains(normalized)
            ? normalized
            : throw new ToolInputException($"Unknown sortBy '{sortBy}'. Valid values: {string.Join(", ", FailedMessageSortFields)}.");
    }

    public static string SortDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction))
        {
            return "desc";
        }

        var normalized = direction.Trim().ToLowerInvariant();
        return normalized is "asc" or "desc" ? normalized : throw new ToolInputException("direction must be 'asc' or 'desc'.");
    }

    public static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength] + $"... [truncated, {value.Length - maxLength} more characters]";
}
