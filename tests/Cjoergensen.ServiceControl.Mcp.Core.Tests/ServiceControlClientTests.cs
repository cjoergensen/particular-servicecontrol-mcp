using System.Net;
using Cjoergensen.ServiceControl.Mcp.Client;
using Cjoergensen.ServiceControl.Mcp.Tests.Support;
using static Cjoergensen.ServiceControl.Mcp.Tests.Support.TestKit;

namespace Cjoergensen.ServiceControl.Mcp.Tests;

public class ServiceControlClientTests
{
    [Fact]
    public async Task Failed_messages_request_carries_paging_sorting_and_status()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/errors", Payloads.FailedMessages);
        var client = PrimaryClient(fake);

        await client.GetFailedMessagesAsync(new FailedMessagesQuery(Status: "unresolved", Page: 2, PageSize: 10), Ct);

        var request = Assert.Single(fake.Requests);
        Assert.Equal("/api/errors", request.Uri.AbsolutePath);
        Assert.Equal("?page=2&per_page=10&sort=time_of_failure&direction=desc&status=unresolved", request.Uri.Query);
    }

    [Fact]
    public async Task Endpoint_filter_uses_the_per_endpoint_route_and_escapes_the_name()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/endpoints/", Payloads.FailedMessages);
        var client = PrimaryClient(fake);

        await client.GetFailedMessagesAsync(new FailedMessagesQuery(Endpoint: "Billing.Api/v2", QueueAddress: "ignored"), Ct);

        var request = Assert.Single(fake.Requests);
        Assert.Equal("/api/endpoints/Billing.Api%2Fv2/errors", request.Uri.AbsolutePath, ignoreCase: true);
        Assert.DoesNotContain("queueAddress", request.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Modified_range_is_sent_as_a_single_from_to_value()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/errors", Payloads.FailedMessages);
        var client = PrimaryClient(fake);

        await client.GetFailedMessagesAsync(
            new FailedMessagesQuery(
                ModifiedFrom: new DateTime(2026, 9, 19, 7, 0, 0, DateTimeKind.Utc),
                ModifiedTo: new DateTime(2026, 9, 19, 8, 0, 0, DateTimeKind.Utc)),
            Ct);

        var query = Uri.UnescapeDataString(fake.Requests[0].Uri.Query);
        Assert.Contains("modified=2026-09-19T07:00:00.0000000Z...2026-09-19T08:00:00.0000000Z", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Response_is_parsed_from_snake_case_with_total_count_and_incomplete_instances()
    {
        var fake = new FakeServiceControl().Route(
            "GET", "/api/errors", Payloads.FailedMessages, HttpStatusCode.OK,
            ("Total-Count", "42"), ("X-Particular-Incomplete-Results", "audit-1:unavailable, audit-2:timeout"));
        var client = PrimaryClient(fake);

        var page = await client.GetFailedMessagesAsync(new FailedMessagesQuery(), Ct);

        Assert.Equal(42, page.TotalCount);
        Assert.Equal(["audit-1:unavailable", "audit-2:timeout"], page.IncompleteInstances);
        var message = Assert.Single(page.Items);
        Assert.Equal("System.InvalidOperationException", message.Exception?.ExceptionType);
        Assert.Equal("Billing", message.ReceivingEndpoint?.Name);
        Assert.Equal(3, message.NumberOfProcessingAttempts);
        Assert.Equal("unresolved", message.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ServiceControlFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, ServiceControlFailureKind.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, ServiceControlFailureKind.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, ServiceControlFailureKind.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError, ServiceControlFailureKind.ServerError)]
    public async Task Http_failures_map_to_a_readable_exception_kind(HttpStatusCode status, ServiceControlFailureKind expected)
    {
        var fake = new FakeServiceControl().Route("GET", "/api/", "\"boom\"", status);
        var client = PrimaryClient(fake);

        var exception = await Assert.ThrowsAsync<ServiceControlApiException>(() => client.GetHeartbeatStatsAsync(Ct));

        Assert.Equal(expected, exception.Kind);
    }

    [Fact]
    public async Task Forbidden_message_explains_which_role_is_needed()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/", "{}", HttpStatusCode.Forbidden);
        var client = PrimaryClient(fake);

        var exception = await Assert.ThrowsAsync<ServiceControlApiException>(() => client.GetHeartbeatStatsAsync(Ct));

        Assert.Contains("writer role", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_failure_is_reported_as_unreachable_with_the_address()
    {
        var fake = new FakeServiceControl().Fail(new HttpRequestException("Connection refused"));
        var client = PrimaryClient(fake);

        var exception = await Assert.ThrowsAsync<ServiceControlApiException>(() => client.GetHeartbeatStatsAsync(Ct));

        Assert.Equal(ServiceControlFailureKind.Unreachable, exception.Kind);
        Assert.Contains("localhost:33333", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_payload_that_cannot_be_parsed_is_reported_as_an_incompatible_response()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/heartbeats/stats", "<html>not json</html>");
        var client = PrimaryClient(fake);

        var exception = await Assert.ThrowsAsync<ServiceControlApiException>(() => client.GetHeartbeatStatsAsync(Ct));

        Assert.Equal(ServiceControlFailureKind.Other, exception.Kind);
    }

    [Fact]
    public async Task Failure_group_classifier_with_spaces_is_escaped_in_the_path()
    {
        var fake = new FakeServiceControl().Route("GET", "/api/recoverability/groups/", Payloads.FailureGroups);
        var client = PrimaryClient(fake);

        await client.GetFailureGroupsAsync(ServiceControlClient.DefaultClassifier, Ct);

        Assert.Equal("/api/recoverability/groups/Exception%20Type%20and%20Stack%20Trace", fake.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Incomplete_instances_are_decoded_from_the_base64_ids_servicecontrol_uses()
    {
        // Captured from a real ServiceControl 6.21 response when the audit instance answered 204 to a proxied request.
        var fake = new FakeServiceControl().Route(
            "GET", "/api/errors", "[]", HttpStatusCode.OK,
            ("X-Particular-Incomplete-Results", "aHR0cDovL3NlcnZpY2Vjb250cm9sLWF1ZGl0OjQ0NDQ0:error"));
        var client = PrimaryClient(fake);

        var page = await client.GetFailedMessagesAsync(new FailedMessagesQuery(), Ct);

        Assert.Equal(["http://servicecontrol-audit:44444 (error)"], page.IncompleteInstances);
    }

    [Theory]
    [InlineData("audit-1:timeout", "audit-1:timeout")]
    [InlineData("no-separator", "no-separator")]
    [InlineData("bm90LWEtdXJs:error", "bm90LWEtdXJs:error")]
    public void Unrecognised_incomplete_instance_entries_are_passed_through(string entry, string expected) =>
        Assert.Equal(expected, ApiClientBase.DescribeIncomplete(entry));

    [Fact]
    public async Task A_saga_that_does_not_exist_yields_null_because_servicecontrol_answers_204()
    {
        // Real ServiceControl answers 204 No Content with an empty body for an unknown saga id.
        var fake = new FakeServiceControl().Route("GET", "/api/sagas/", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = PrimaryClient(fake);

        Assert.Null(await client.GetSagaHistoryAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Monitoring_responses_are_parsed_from_camel_case()
    {
        var fake = new FakeServiceControl().Route("GET", "/monitored-endpoints", Payloads.MonitoredEndpoints);
        var client = MonitoringClient(fake);

        var endpoints = await client.GetMonitoredEndpointsAsync(5, Ct);

        Assert.Equal("?history=5", fake.Requests[0].Uri.Query);
        var billing = Assert.Single(endpoints, e => e.Name == "Billing");
        Assert.Equal(1, billing.DisconnectedCount);
        Assert.Equal(12.5, billing.Metrics!["queueLength"].Average);
        Assert.True(endpoints.Single(e => e.Name == "Sales").IsStale);
    }
}
