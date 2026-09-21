using System.Net;
using System.Text.Json;
using Cjoergensen.ServiceControl.Mcp.Tests.Support;
using Cjoergensen.ServiceControl.Mcp.Tools;
using static Cjoergensen.ServiceControl.Mcp.Tests.Support.TestKit;

namespace Cjoergensen.ServiceControl.Mcp.Tests;

public class ToolTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    static FailedMessageTools FailedMessages(FakeServiceControl fake, Action<ServiceControlMcpOptions>? configure = null) =>
        new(PrimaryClient(fake), Wrap(Options(configure)), new FixedTimeProvider(Now));

    // ---- list_failed_messages ------------------------------------------------------------------------------------

    [Fact]
    public async Task Failed_messages_default_to_unresolved_and_are_summarized()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/errors", Payloads.FailedMessages, HttpStatusCode.OK, ("Total-Count", "1"));

        var result = await FailedMessages(fake).ListFailedMessages(cancellationToken: Ct);

        Assert.Contains("status=unresolved", fake.Requests[0].Uri.Query, StringComparison.Ordinal);
        var item = Assert.Single(result.Items);
        Assert.Equal("Billing", item.Endpoint);
        Assert.Equal("System.InvalidOperationException", item.ExceptionType);
        Assert.Equal("Card gateway timed out", item.ExceptionMessage);
        Assert.False(result.HasMore);
        Assert.Null(result.IncompleteInstances);
    }

    [Fact]
    public async Task Modified_within_minutes_is_turned_into_a_time_range_ending_now()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/errors", Payloads.FailedMessages);

        await FailedMessages(fake).ListFailedMessages(modifiedWithinMinutes: 60, cancellationToken: Ct);

        var query = Uri.UnescapeDataString(fake.Requests[0].Uri.Query);
        Assert.Contains("modified=2026-09-19T11:00:00.0000000Z...2026-09-19T12:00:00.0000000Z", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paging_reports_more_results_and_page_size_is_capped()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/errors", Payloads.FailedMessages, HttpStatusCode.OK, ("Total-Count", "500"));

        var result = await FailedMessages(fake, o => o.MaxPageSize = 20).ListFailedMessages(pageSize: 1000, cancellationToken: Ct);

        Assert.Contains("per_page=20", fake.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Equal(20, result.PageSize);
        Assert.True(result.HasMore);
        Assert.Equal(500, result.TotalCount);
    }

    [Fact]
    public async Task Incomplete_results_from_unreachable_audit_instances_are_surfaced_not_hidden()
    {
        var fake = new FakeServiceControl().Route(
            "GET", "/api/errors", Payloads.FailedMessages, HttpStatusCode.OK, ("X-Particular-Incomplete-Results", "audit-1:unavailable"));

        var result = await FailedMessages(fake).ListFailedMessages(cancellationToken: Ct);

        Assert.Equal(["audit-1:unavailable"], result.IncompleteInstances);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("unresolved,nonsense")]
    public async Task Unknown_statuses_are_rejected_with_an_explanation(string status)
    {
        var fake = new FakeServiceControl();

        var exception = await Assert.ThrowsAsync<ToolInputException>(() => FailedMessages(fake).ListFailedMessages(status: status, cancellationToken: Ct));

        Assert.Contains("Valid statuses", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Requests);
    }

    [Theory]
    [InlineData("unresolved,retryIssued")]
    [InlineData("-archived")]
    public async Task Combined_and_excluding_statuses_are_accepted(string status)
    {
        var fake = new FakeServiceControl().Route("GET", "/api/errors", Payloads.FailedMessages);

        await FailedMessages(fake).ListFailedMessages(status: status, cancellationToken: Ct);

        Assert.Contains("status=" + Uri.EscapeDataString(status), fake.Requests[0].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_arguments_are_rejected_before_any_request_is_made()
    {
        var fake = new FakeServiceControl();
        var tools = FailedMessages(fake);

        await Assert.ThrowsAsync<ToolInputException>(() => tools.ListFailedMessages(page: 0, cancellationToken: Ct));
        await Assert.ThrowsAsync<ToolInputException>(() => tools.ListFailedMessages(sortBy: "; drop table", cancellationToken: Ct));
        await Assert.ThrowsAsync<ToolInputException>(() => tools.ListFailedMessages(direction: "sideways", cancellationToken: Ct));
        await Assert.ThrowsAsync<ToolInputException>(() => tools.ListFailedMessages(modifiedWithinMinutes: -5, cancellationToken: Ct));

        Assert.Empty(fake.Requests);
    }

    // ---- get_failed_message --------------------------------------------------------------------------------------

    [Fact]
    public async Task Failed_message_details_never_include_the_message_body()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/errors/", Payloads.FailedMessageDetail);

        var result = await FailedMessages(fake).GetFailedMessage("b4f1a3d2-0000-0000-0000-000000000001", cancellationToken: Ct);

        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("SECRET-BODY-MUST-NOT-LEAK", json, StringComparison.Ordinal);
        var attempt = Assert.Single(result.Attempts);
        Assert.Equal("System.InvalidOperationException", attempt.ExceptionType);
        Assert.Null(attempt.Headers);
        Assert.Equal("group-1", Assert.Single(result.Groups).Id);
    }

    [Fact]
    public async Task Headers_are_included_only_on_request_and_long_stack_traces_are_truncated()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/errors/", Payloads.FailedMessageDetail);
        var tools = FailedMessages(fake);

        var withHeaders = await tools.GetFailedMessage("x", includeHeaders: true, maxStackTraceChars: 100, cancellationToken: Ct);
        Assert.Equal("11111111-1111-1111-1111-111111111111", withHeaders.Attempts[0].Headers!["NServiceBus.MessageId"]);

        var truncated = await tools.GetFailedMessage("x", maxStackTraceChars: 100, cancellationToken: Ct);
        Assert.NotNull(truncated.Attempts[0].StackTrace);

        await Assert.ThrowsAsync<ToolInputException>(() => tools.GetFailedMessage(" ", cancellationToken: Ct));
        await Assert.ThrowsAsync<ToolInputException>(() => tools.GetFailedMessage("x", maxStackTraceChars: 5, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_message_that_does_not_exist_gives_a_not_found_tool_error()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/errors/", "{}", HttpStatusCode.NotFound);

        var exception = await Assert.ThrowsAsync<Client.ServiceControlApiException>(() => FailedMessages(fake).GetFailedMessage("nope", cancellationToken: Ct));

        Assert.Equal(Client.ServiceControlFailureKind.NotFound, exception.Kind);
    }

    [Fact]
    public async Task Counts_query_each_status_with_a_single_item_page()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/errors", "[]", HttpStatusCode.OK, ("Total-Count", "7"));

        var counts = await FailedMessages(fake).GetFailedMessageCounts(Ct);

        Assert.Equal(4, fake.Requests.Count);
        Assert.All(fake.Requests, r => Assert.Contains("per_page=1", r.Uri.Query, StringComparison.Ordinal));
        Assert.Equal(7, counts.Unresolved);
        Assert.Equal(7, counts.RetryIssued);
    }

    // ---- failure groups ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Failure_groups_are_sorted_by_size_and_show_running_operations()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/recoverability/groups/", Payloads.FailureGroups);
        var tools = new FailureGroupTools(PrimaryClient(fake), Wrap(Options()));

        var groups = await tools.ListFailureGroups(cancellationToken: Ct);

        Assert.Equal(["big", "small"], groups.Select(g => g.Id));
        Assert.Equal("RetryInProgress", groups[0].Operation?.Status);
        Assert.Equal(20, groups[0].Operation?.Remaining);
        Assert.Null(groups[1].Operation);
    }

    // ---- endpoints, heartbeats, custom checks --------------------------------------------------------------------

    [Fact]
    public async Task Dead_endpoints_can_be_filtered_and_unmonitored_endpoints_have_no_heartbeat_status()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/endpoints", Payloads.Endpoints);
        var tools = new HealthTools(PrimaryClient(fake), Wrap(Options()));

        var dead = await tools.ListEndpoints(heartbeatStatus: "dead", cancellationToken: Ct);
        Assert.Equal("Billing", Assert.Single(dead.Items).Name);

        var all = await tools.ListEndpoints(cancellationToken: Ct);
        Assert.Equal(["Billing", "Sales", "Shipping"], all.Items.Select(e => e.Name));
        Assert.Null(all.Items.Single(e => e.Name == "Shipping").HeartbeatStatus);

        var monitored = await tools.ListEndpoints(monitoredOnly: true, cancellationToken: Ct);
        Assert.DoesNotContain(monitored.Items, e => e.Name == "Shipping");

        await Assert.ThrowsAsync<ToolInputException>(() => tools.ListEndpoints(heartbeatStatus: "zombie", cancellationToken: Ct));
    }

    [Fact]
    public async Task Endpoint_paging_is_applied_locally_because_the_server_returns_everything()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/endpoints", Payloads.Endpoints);
        var tools = new HealthTools(PrimaryClient(fake), Wrap(Options()));

        var first = await tools.ListEndpoints(pageSize: 2, cancellationToken: Ct);
        var second = await tools.ListEndpoints(page: 2, pageSize: 2, cancellationToken: Ct);

        Assert.True(first.HasMore);
        Assert.Equal(3, first.TotalCount);
        Assert.False(second.HasMore);
        Assert.Equal("Shipping", Assert.Single(second.Items).Name);
    }

    [Fact]
    public async Task Custom_checks_default_to_failing_ones()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/customchecks", Payloads.CustomChecks, HttpStatusCode.OK, ("Total-Count", "1"));
        var tools = new HealthTools(PrimaryClient(fake), Wrap(Options()));

        var result = await tools.ListCustomChecks(cancellationToken: Ct);

        Assert.Contains("status=fail", fake.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Equal("Cannot open connection", Assert.Single(result.Items).FailureReason);
        await Assert.ThrowsAsync<ToolInputException>(() => tools.ListCustomChecks(status: "maybe", cancellationToken: Ct));
    }

    // ---- audit ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_saga_explains_the_likely_causes_instead_of_a_parse_error()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/sagas/", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var tools = new AuditTools(PrimaryClient(fake), Wrap(Options()));

        var exception = await Assert.ThrowsAsync<Client.ServiceControlApiException>(() => tools.GetSagaHistory(Guid.NewGuid(), cancellationToken: Ct));

        Assert.Equal(Client.ServiceControlFailureKind.NotFound, exception.Kind);
        Assert.Contains("saga auditing", exception.Message, StringComparison.Ordinal);
    }

    // ---- monitoring ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Monitored_endpoints_report_average_and_latest_metric_values()
    {
        var fake = new FakeServiceControl().Route("GET", "/monitored-endpoints", Payloads.MonitoredEndpoints);
        var tools = new MonitoringTools(MonitoringClient(fake));

        var endpoints = await tools.ListMonitoredEndpoints(cancellationToken: Ct);

        var billing = endpoints.Single(e => e.Name == "Billing");
        Assert.Equal(new MetricValue(12.5, 15), billing.Metrics["queueLength"]);
        Assert.Equal(1, billing.DisconnectedInstances);
        Assert.True(endpoints.Single(e => e.Name == "Sales").IsStale);
        await Assert.ThrowsAsync<ToolInputException>(() => tools.ListMonitoredEndpoints(historyMinutes: 0, cancellationToken: Ct));
    }

    // ---- overview ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Health_overview_combines_sections_and_reports_the_ones_that_failed()
    {
        var fake = new FakeServiceControl()
            .Route("GET", "/api/errors", "[]", HttpStatusCode.OK, ("Total-Count", "3"))
            .Route("GET", "/api/recoverability/groups/", Payloads.FailureGroups)
            .Route("GET", "/api/heartbeats/stats", """{ "active": 5, "failing": 1 }""")
            .Route("GET", "/api/endpoints", Payloads.Endpoints)
            .Route("GET", "/api/customchecks", "{}", HttpStatusCode.Forbidden);
        var options = Options(o => o.MonitoringUrl = "http://localhost:33633");
        var tools = new OverviewTools(PrimaryClient(fake), MonitoringClient(new FakeServiceControl().Route("GET", "/monitored-endpoints", Payloads.MonitoredEndpoints)), Wrap(options));

        var overview = await tools.GetHealthOverview(Ct);

        Assert.Equal(3, overview.FailedMessages?.Unresolved);
        Assert.Equal("big", overview.TopFailureGroups?[0].Id);
        Assert.Equal(1, overview.Heartbeats?.Failing);
        Assert.Equal(["Billing"], overview.DeadEndpoints);
        Assert.Equal(1, overview.Monitoring?.StaleEndpoints);

        // The forbidden section is null and explained, so an agent cannot mistake "unknown" for "healthy".
        Assert.Null(overview.FailingCustomChecks);
        var problem = Assert.Single(overview.Problems);
        Assert.StartsWith("custom checks:", problem, StringComparison.Ordinal);
    }
}
