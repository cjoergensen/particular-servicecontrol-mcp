using Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;
using Cjoergensen.ServiceControl.Mcp.TestWorkload;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests;

public sealed class FailedMessageTests(PlatformFixture platform) : PlatformTest(platform)
{
    [Fact]
    public async Task A_failing_handler_appears_as_a_failed_message_with_its_exception_and_group()
    {
        await using var mcp = await ReadOnlyServerAsync();
        try
        {
            var (messageId, failedId) = await FailAMessageAsync(mcp);

            var listed = Items(await mcp.CallAsync("list_failed_messages", new { endpoint = Workload.BillingEndpoint, pageSize = 50 }, Ct))
                .Single(i => Str(i, "messageId") == messageId.ToString());
            Assert.Equal("unresolved", Str(listed, "status"));
            Assert.Equal("Billing", Str(listed, "endpoint"));
            Assert.Equal("System.InvalidOperationException", Str(listed, "exceptionType"));
            Assert.Equal(WorkloadState.ChargeFailureMessage, Str(listed, "exceptionMessage"));
            Assert.Contains("ChargeCustomer", Str(listed, "messageType"), StringComparison.Ordinal);

            var details = await mcp.CallAsync("get_failed_message", new { id = failedId }, Ct);
            var attempt = details.GetProperty("attempts")[0];
            Assert.Equal("System.InvalidOperationException", Str(attempt, "exceptionType"));
            Assert.Contains("ChargeCustomerHandler", Str(attempt, "stackTrace"), StringComparison.Ordinal);
            Assert.DoesNotContain("\"body\"", details.GetRawText(), StringComparison.Ordinal);

            var counts = await mcp.CallAsync("get_failed_message_counts", null, Ct);
            Assert.True(counts.GetProperty("unresolved").GetInt64() >= 1);

            var group = await Eventually.Async(
                async () => (await mcp.CallAsync("list_failure_groups", null, Ct)).EnumerateArray()
                    .FirstOrDefault(g => Str(g, "title").Contains("InvalidOperationException", StringComparison.Ordinal)),
                g => g.ValueKind == System.Text.Json.JsonValueKind.Object,
                "a failure group for InvalidOperationException",
                cancellationToken: Ct);
            var inGroup = await mcp.CallAsync("list_failure_group_messages", new { groupId = Str(group, "id") }, Ct);
            Assert.Contains(Items(inGroup), i => Str(i, "messageId") == messageId.ToString());
        }
        finally
        {
            Workload.State.BillingFails = false;
        }
    }

    [Fact]
    public async Task Retrying_a_failed_message_reprocesses_it_and_it_resolves()
    {
        await using var mcp = await WritableServerAsync();
        try
        {
            var orderId = Guid.NewGuid();
            Workload.State.BillingFails = true;
            var messageId = await Platform.Workload.SendChargeAsync(orderId);
            var failedId = await WaitForFailedMessageAsync(mcp, messageId, "unresolved");

            // The system recovers, then the operator (via the agent) retries.
            Workload.State.BillingFails = false;
            var outcome = await mcp.CallAsync("retry_failed_message", new { id = failedId }, Ct);
            Assert.True(outcome.GetProperty("accepted").GetBoolean());

            await WaitForFailedMessageAsync(mcp, messageId, "resolved");
            Assert.Contains(orderId, Workload.State.ChargedOrders);
        }
        finally
        {
            Workload.State.BillingFails = false;
        }
    }

    [Fact]
    public async Task Archiving_hides_a_message_from_unresolved_and_unarchiving_brings_it_back()
    {
        await using var mcp = await WritableServerAsync();
        try
        {
            var (messageId, failedId) = await FailAMessageAsync(mcp);

            await mcp.CallAsync("archive_failed_messages", new { ids = new[] { failedId } }, Ct);
            await WaitForFailedMessageAsync(mcp, messageId, "archived");

            await mcp.CallAsync("unarchive_failed_messages", new { ids = new[] { failedId } }, Ct);
            await WaitForFailedMessageAsync(mcp, messageId, "unresolved");
        }
        finally
        {
            Workload.State.BillingFails = false;
        }
    }

    [Fact]
    public async Task A_group_retry_is_refused_on_a_stale_count_and_proceeds_on_the_live_one()
    {
        await using var mcp = await WritableServerAsync();
        try
        {
            var (first, _) = await FailAMessageAsync(mcp);
            var (second, _) = await FailAMessageAsync(mcp);

            var group = await Eventually.Async(
                async () => (await mcp.CallAsync("list_failure_groups", null, Ct)).EnumerateArray()
                    .FirstOrDefault(g => Str(g, "title").Contains("InvalidOperationException", StringComparison.Ordinal)),
                g => g.ValueKind == System.Text.Json.JsonValueKind.Object && g.GetProperty("count").GetInt32() >= 2,
                "a group holding both failures",
                cancellationToken: Ct);
            var groupId = Str(group, "id");
            var liveCount = group.GetProperty("count").GetInt32();

            // A stale number is refused, and nothing is retried.
            var refused = await mcp.CallRawAsync("retry_failure_group", new { groupId, expectedCount = liveCount + 1 }, Ct);
            Assert.True(refused.IsError);
            Assert.Contains($"expectedCount={liveCount}", McpHarness.TextOf(refused), StringComparison.Ordinal);
            var stillFailing = await mcp.CallAsync("list_failed_messages", new { endpoint = Workload.BillingEndpoint, pageSize = 50 }, Ct);
            Assert.Contains(Items(stillFailing), i => Str(i, "messageId") == first.ToString());

            // The live number is accepted; the system has recovered, so both messages resolve.
            Workload.State.BillingFails = false;
            var outcome = await mcp.CallAsync("retry_failure_group", new { groupId, expectedCount = liveCount }, Ct);
            Assert.True(outcome.GetProperty("accepted").GetBoolean());

            await WaitForFailedMessageAsync(mcp, first, "resolved");
            await WaitForFailedMessageAsync(mcp, second, "resolved");
        }
        finally
        {
            Workload.State.BillingFails = false;
        }
    }

    [Fact]
    public async Task An_endpoint_retry_is_refused_on_a_stale_count()
    {
        await using var mcp = await WritableServerAsync();
        try
        {
            var (messageId, _) = await FailAMessageAsync(mcp);

            var refused = await mcp.CallRawAsync("retry_endpoint_failures", new { endpoint = Workload.BillingEndpoint, expectedCount = 100_000 }, Ct);
            Assert.True(refused.IsError);
            Assert.Contains("expectedCount=", McpHarness.TextOf(refused), StringComparison.Ordinal);

            // Clean up: recover and retry properly so later tests start from a clean error queue.
            Workload.State.BillingFails = false;
            var live = (await mcp.CallAsync("list_failed_messages", new { endpoint = Workload.BillingEndpoint, pageSize = 1 }, Ct)).GetProperty("totalCount").GetInt32();
            await mcp.CallAsync("retry_endpoint_failures", new { endpoint = Workload.BillingEndpoint, expectedCount = live }, Ct);
            await WaitForFailedMessageAsync(mcp, messageId, "resolved");
        }
        finally
        {
            Workload.State.BillingFails = false;
        }
    }

    [Fact]
    public async Task A_read_only_server_offers_no_tools_that_change_anything()
    {
        await using var mcp = await ReadOnlyServerAsync();

        var names = await mcp.ToolNamesAsync(Ct);

        Assert.Contains("list_failed_messages", names);
        Assert.DoesNotContain(names, n => n.StartsWith("retry_", StringComparison.Ordinal) || n.StartsWith("archive_", StringComparison.Ordinal) ||
                                          n.StartsWith("unarchive_", StringComparison.Ordinal) || n.StartsWith("dismiss_", StringComparison.Ordinal));
    }
}
