using System.Net;
using Cjoergensen.ServiceControl.Mcp.Client;
using Cjoergensen.ServiceControl.Mcp.Tests.Support;
using Cjoergensen.ServiceControl.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using static Cjoergensen.ServiceControl.Mcp.Tests.Support.TestKit;

namespace Cjoergensen.ServiceControl.Mcp.Tests;

public class WriteToolTests
{
    static WriteTools Tools(FakeServiceControl fake, Action<ServiceControlMcpOptions>? configure = null) =>
        new(PrimaryClient(fake), Wrap(Options(configure)), NullLogger<WriteTools>.Instance);

    static FakeServiceControl Accepting() => new FakeServiceControl()
        .Route("POST", "/api/", "{}", HttpStatusCode.Accepted)
        .Route("PATCH", "/api/", "{}", HttpStatusCode.Accepted)
        .Route("DELETE", "/api/", "{}", HttpStatusCode.Accepted);

    static IEnumerable<RecordedRequest> Changes(FakeServiceControl fake) => fake.Requests.Where(r => r.Method != "GET");

    // ---- single and batch --------------------------------------------------------------------------------------

    [Fact]
    public async Task Retrying_one_message_posts_to_its_retry_route()
    {
        var fake = Accepting();

        var outcome = await Tools(fake).RetryFailedMessage("b4f1a3d2-0000-0000-0000-000000000001", Ct);

        var request = Assert.Single(fake.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/errors/b4f1a3d2-0000-0000-0000-000000000001/retry", request.Uri.AbsolutePath);
        Assert.True(outcome.Accepted);
        Assert.Contains("not confirmation that it finished", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_retry_sends_a_json_array_of_distinct_trimmed_ids()
    {
        var fake = Accepting();

        var outcome = await Tools(fake).RetryFailedMessages([" a ", "b", "a", "", "  "], Ct);

        var request = Assert.Single(fake.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/errors/retry", request.Uri.AbsolutePath);
        Assert.Equal("""["a","b"]""", request.Body);
        Assert.Equal("2 messages", outcome.Target);
    }

    [Fact]
    public async Task Batches_are_limited_and_empty_batches_are_rejected_without_calling_servicecontrol()
    {
        var fake = Accepting();
        var tools = Tools(fake, o => o.MaxBatchSize = 3);

        await Assert.ThrowsAsync<ToolInputException>(() => tools.RetryFailedMessages([], Ct));
        await Assert.ThrowsAsync<ToolInputException>(() => tools.ArchiveFailedMessages(["", " "], Ct));
        var tooMany = await Assert.ThrowsAsync<ToolInputException>(() => tools.RetryFailedMessages(["1", "2", "3", "4"], Ct));

        Assert.Contains("more than the allowed 3", tooMany.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Archive_and_unarchive_use_patch_with_a_json_array()
    {
        var fake = Accepting();
        var tools = Tools(fake);

        await tools.ArchiveFailedMessages(["x", "y"], Ct);
        await tools.UnarchiveFailedMessages(["x"], Ct);

        Assert.Equal(("PATCH", "/api/errors/archive", """["x","y"]"""), (fake.Requests[0].Method, fake.Requests[0].Uri.AbsolutePath, fake.Requests[0].Body));
        Assert.Equal(("PATCH", "/api/errors/unarchive", """["x"]"""), (fake.Requests[1].Method, fake.Requests[1].Uri.AbsolutePath, fake.Requests[1].Body));
    }

    [Theory]
    [InlineData("CustomChecks/11111111-2222-3333-4444-555555555555")]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    [InlineData("  CustomChecks/11111111-2222-3333-4444-555555555555  ")]
    public async Task Dismissing_a_custom_check_accepts_the_id_as_listed_and_deletes_it_by_guid(string id)
    {
        var fake = Accepting();

        await Tools(fake).DismissCustomCheck(id, Ct);

        var request = Assert.Single(fake.Requests);
        Assert.Equal("DELETE", request.Method);
        Assert.Equal("/api/customchecks/11111111-2222-3333-4444-555555555555", request.Uri.AbsolutePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("CustomChecks/")]
    public async Task A_malformed_custom_check_id_is_rejected_before_any_request(string id)
    {
        var fake = Accepting();

        await Assert.ThrowsAsync<ToolInputException>(() => Tools(fake).DismissCustomCheck(id, Ct));

        Assert.Empty(fake.Requests);
    }

    // ---- count guard ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Retrying_an_endpoint_proceeds_when_the_expected_count_matches()
    {
        var fake = Accepting();
        fake.Route("GET", "/api/endpoints/Billing/errors", "[]", HttpStatusCode.OK, ("Total-Count", "5"));

        var outcome = await Tools(fake).RetryEndpointFailures("Billing", 5, Ct);

        var change = Assert.Single(Changes(fake));
        Assert.Equal("/api/errors/Billing/retry/all", change.Uri.AbsolutePath);
        Assert.Contains("5 messages", outcome.Message, StringComparison.Ordinal);

        // The live count was read for unresolved messages of that endpoint only.
        var read = fake.Requests[0];
        Assert.Contains("status=unresolved", read.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_expected_count_refuses_the_operation_and_sends_nothing()
    {
        var fake = Accepting();
        fake.Route("GET", "/api/endpoints/Billing/errors", "[]", HttpStatusCode.OK, ("Total-Count", "9"));

        var exception = await Assert.ThrowsAsync<ToolInputException>(() => Tools(fake).RetryEndpointFailures("Billing", 5, Ct));

        Assert.Contains("has 9 unresolved", exception.Message, StringComparison.Ordinal);
        Assert.Contains("expectedCount=9", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Changes(fake));
    }

    [Fact]
    public async Task Nothing_to_retry_is_reported_instead_of_sending_an_empty_operation()
    {
        var fake = Accepting();
        fake.Route("GET", "/api/endpoints/Billing/errors", "[]", HttpStatusCode.OK, ("Total-Count", "0"));

        var exception = await Assert.ThrowsAsync<ToolInputException>(() => Tools(fake).RetryEndpointFailures("Billing", 0, Ct));

        Assert.Contains("nothing to do", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Changes(fake));
    }

    [Fact]
    public async Task Group_operations_check_the_live_group_size_before_acting()
    {
        var fake = Accepting();
        fake.Route("GET", "/api/recoverability/groups/grp-1/errors", "[]", HttpStatusCode.OK, ("Total-Count", "40"));
        var tools = Tools(fake);

        await Assert.ThrowsAsync<ToolInputException>(() => tools.RetryFailureGroup("grp-1", 39, Ct));
        await Assert.ThrowsAsync<ToolInputException>(() => tools.ArchiveFailureGroup("grp-1", 41, Ct));
        Assert.Empty(Changes(fake));

        await tools.RetryFailureGroup("grp-1", 40, Ct);
        await tools.ArchiveFailureGroup("grp-1", 40, Ct);

        Assert.Equal(
            ["/api/recoverability/groups/grp-1/errors/retry", "/api/recoverability/groups/grp-1/errors/archive"],
            Changes(fake).Select(r => r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task Required_arguments_are_validated_before_any_request()
    {
        var fake = Accepting();
        var tools = Tools(fake);

        await Assert.ThrowsAsync<ToolInputException>(() => tools.RetryFailedMessage(" ", Ct));
        await Assert.ThrowsAsync<ToolInputException>(() => tools.RetryEndpointFailures("", 1, Ct));
        await Assert.ThrowsAsync<ToolInputException>(() => tools.RetryFailureGroup("", 1, Ct));

        Assert.Empty(fake.Requests);
    }

    // ---- authorization -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reader_who_lacks_the_writer_role_gets_a_clear_forbidden_error()
    {
        var fake = new FakeServiceControl().Route("POST", "/api/", "{}", HttpStatusCode.Forbidden);

        var exception = await Assert.ThrowsAsync<ServiceControlApiException>(() => Tools(fake).RetryFailedMessage("id-1", Ct));

        Assert.Equal(ServiceControlFailureKind.Forbidden, exception.Kind);
        Assert.Contains("writer role", exception.Message, StringComparison.Ordinal);
    }
}
