using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cjoergensen.ServiceControl.Mcp.Client;

/// <summary>Filter for the failed message list. <see cref="Status"/> takes ServiceControl's comma-separated names, where a leading <c>-</c> excludes.</summary>
public sealed record FailedMessagesQuery(
    string? Status = null,
    string? Endpoint = null,
    string? QueueAddress = null,
    DateTime? ModifiedFrom = null,
    DateTime? ModifiedTo = null,
    string Sort = "time_of_failure",
    string Direction = "desc",
    int Page = 1,
    int PageSize = 25);

/// <summary>Typed client for the ServiceControl primary (error) instance API, which also aggregates its attached audit instances.</summary>
public sealed class ServiceControlClient : ApiClientBase
{
    public const string DefaultClassifier = "Exception Type and Stack Trace";

    /// <summary>JSON options for the primary and audit APIs: snake_case names, camelCase enum strings.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ServiceControlClient(HttpClient http) : base(http, JsonOptions)
    {
    }

    public Task<AuthConfiguration> GetAuthConfigurationAsync(CancellationToken cancellationToken) =>
        GetAsync<AuthConfiguration>("authentication/configuration", cancellationToken);

    /// <summary>The routes the current credentials may call. Not available on ServiceControl versions before role-based authorization (6.18).</summary>
    public Task<MyRoutes> GetMyRoutesAsync(CancellationToken cancellationToken) =>
        GetAsync<MyRoutes>("my/routes", cancellationToken);

    // ---- Failed messages -------------------------------------------------------------------------------------------

    public Task<ApiPage<FailedMessageView>> GetFailedMessagesAsync(FailedMessagesQuery query, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(query.Endpoint) ? "errors" : $"endpoints/{Uri.EscapeDataString(query.Endpoint)}/errors";
        var qs = new QueryBuilder()
            .Add("page", query.Page)
            .Add("per_page", query.PageSize)
            .Add("sort", query.Sort)
            .Add("direction", query.Direction)
            .Add("status", query.Status);

        // ServiceControl takes the range as a single "from...to" value and needs both ends.
        if (query.ModifiedFrom is not null || query.ModifiedTo is not null)
        {
            var from = query.ModifiedFrom ?? DateTime.UnixEpoch;
            var to = query.ModifiedTo ?? DateTime.UtcNow;
            qs.Add("modified", $"{from.ToUniversalTime():O}...{to.ToUniversalTime():O}");
        }

        // The queue filter only exists on the all-errors route; the per-endpoint route ignores it.
        if (string.IsNullOrWhiteSpace(query.Endpoint))
        {
            qs.Add("queueAddress", query.QueueAddress);
        }

        return GetPageAsync<FailedMessageView>(path + qs, cancellationToken);
    }

    public Task<FailedMessageDetail> GetFailedMessageAsync(string id, CancellationToken cancellationToken) =>
        GetAsync<FailedMessageDetail>($"errors/{Uri.EscapeDataString(id)}", cancellationToken);

    /// <summary>Counts failed messages with the given status filter without fetching them.</summary>
    public async Task<long> CountFailedMessagesAsync(string? status, CancellationToken cancellationToken)
    {
        var page = await GetFailedMessagesAsync(new FailedMessagesQuery(Status: status, PageSize: 1), cancellationToken).ConfigureAwait(false);
        return page.TotalCount ?? page.Items.Count;
    }

    // ---- Recoverability groups -------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> GetClassifiersAsync(CancellationToken cancellationToken) =>
        await GetAsync<List<string>>("recoverability/classifiers", cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<GroupOperation>> GetFailureGroupsAsync(string classifier, CancellationToken cancellationToken) =>
        await GetAsync<List<GroupOperation>>($"recoverability/groups/{Uri.EscapeDataString(classifier)}", cancellationToken).ConfigureAwait(false);

    public Task<ApiPage<FailedMessageView>> GetGroupFailedMessagesAsync(
        string groupId, string? status, string sort, string direction, int page, int pageSize, CancellationToken cancellationToken)
    {
        var qs = new QueryBuilder()
            .Add("page", page)
            .Add("per_page", pageSize)
            .Add("sort", sort)
            .Add("direction", direction)
            .Add("status", status);
        return GetPageAsync<FailedMessageView>($"recoverability/groups/{Uri.EscapeDataString(groupId)}/errors{qs}", cancellationToken);
    }

    // ---- Endpoints, heartbeats and custom checks -------------------------------------------------------------------

    public Task<ApiPage<EndpointView>> GetEndpointsAsync(CancellationToken cancellationToken) =>
        GetPageAsync<EndpointView>("endpoints", cancellationToken);

    public Task<HeartbeatStats> GetHeartbeatStatsAsync(CancellationToken cancellationToken) =>
        GetAsync<HeartbeatStats>("heartbeats/stats", cancellationToken);

    /// <param name="status"><c>fail</c> or <c>pass</c>; <c>null</c> for both.</param>
    public Task<ApiPage<CustomCheckView>> GetCustomChecksAsync(string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var qs = new QueryBuilder().Add("page", page).Add("per_page", pageSize).Add("status", status);
        return GetPageAsync<CustomCheckView>($"customchecks{qs}", cancellationToken);
    }

    // ---- Audit data (aggregated by the primary instance) -----------------------------------------------------------

    /// <summary>Returns the saga's audit history, or <c>null</c> when ServiceControl has none (it answers 204 No Content).</summary>
    public Task<SagaHistory?> GetSagaHistoryAsync(Guid sagaId, CancellationToken cancellationToken) =>
        GetOrNullAsync<SagaHistory>($"sagas/{sagaId}", cancellationToken);

    public Task<ApiPage<AuditMessageView>> SearchMessagesAsync(
        string query, string? endpoint, int page, int pageSize, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(endpoint) ? "messages/search" : $"endpoints/{Uri.EscapeDataString(endpoint)}/messages/search";
        var qs = new QueryBuilder().Add("q", query).Add("page", page).Add("per_page", pageSize);
        return GetPageAsync<AuditMessageView>(path + qs, cancellationToken);
    }

    // ---- Write operations ------------------------------------------------------------------------------------------
    // ServiceControl accepts these and performs them asynchronously (HTTP 202): success here means "queued", not "done".

    public Task RetryFailedMessageAsync(string id, CancellationToken cancellationToken) =>
        SendAcceptedAsync(HttpMethod.Post, $"errors/{Uri.EscapeDataString(id)}/retry", content: null, cancellationToken);

    public Task RetryFailedMessagesAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken) =>
        SendAcceptedAsync(HttpMethod.Post, "errors/retry", JsonContent.Create(ids, options: JsonOptions), cancellationToken);

    /// <summary>Retries every unresolved failed message of one endpoint.</summary>
    public Task RetryEndpointFailuresAsync(string endpoint, CancellationToken cancellationToken) =>
        SendAcceptedAsync(HttpMethod.Post, $"errors/{Uri.EscapeDataString(endpoint)}/retry/all", content: null, cancellationToken);

    public Task RetryFailureGroupAsync(string groupId, CancellationToken cancellationToken) =>
        SendAcceptedAsync(HttpMethod.Post, $"recoverability/groups/{Uri.EscapeDataString(groupId)}/errors/retry", content: null, cancellationToken);

    public Task ArchiveFailedMessagesAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken) =>
        SendAcceptedAsync(HttpMethod.Patch, "errors/archive", JsonContent.Create(ids, options: JsonOptions), cancellationToken);

    public Task ArchiveFailureGroupAsync(string groupId, CancellationToken cancellationToken) =>
        SendAcceptedAsync(HttpMethod.Post, $"recoverability/groups/{Uri.EscapeDataString(groupId)}/errors/archive", content: null, cancellationToken);

    public Task UnarchiveFailedMessagesAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken) =>
        SendAcceptedAsync(HttpMethod.Patch, "errors/unarchive", JsonContent.Create(ids, options: JsonOptions), cancellationToken);

    public Task DeleteCustomCheckAsync(Guid id, CancellationToken cancellationToken) =>
        SendAcceptedAsync(HttpMethod.Delete, $"customchecks/{id}", content: null, cancellationToken);

    /// <summary>Counts the failed messages of a group with the given status, without fetching them.</summary>
    public async Task<long> CountGroupFailedMessagesAsync(string groupId, string? status, CancellationToken cancellationToken)
    {
        var page = await GetGroupFailedMessagesAsync(groupId, status, "time_of_failure", "desc", 1, 1, cancellationToken).ConfigureAwait(false);
        return page.TotalCount ?? page.Items.Count;
    }
}
