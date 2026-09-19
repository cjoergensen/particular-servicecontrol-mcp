using Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;
using Cjoergensen.ServiceControl.Mcp.TestWorkload;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests;

public sealed class AuditTests(PlatformFixture platform) : PlatformTest(platform)
{
    [Fact]
    public async Task A_sagas_state_changes_are_available_as_its_audit_history()
    {
        await using var mcp = await ReadOnlyServerAsync();
        var orderId = Guid.NewGuid();

        await Platform.Workload.StartOrderAsync(orderId);
        await Eventually.Async(() => Task.FromResult(Workload.State.SagaIdsByOrder.ContainsKey(orderId)), started => started, "the saga to start", TimeSpan.FromSeconds(30), Ct);
        await Platform.Workload.CompleteOrderAsync(orderId);
        var sagaId = Workload.State.SagaIdsByOrder[orderId];

        var history = await Eventually.Async(
            async () => await mcp.CallAsync("get_saga_history", new { sagaId }, Ct),
            h => h.GetProperty("totalChanges").GetInt32() >= 2,
            "both saga state changes to be audited",
            cancellationToken: Ct);

        Assert.Contains("OrderPolicy", Str(history, "sagaType"), StringComparison.Ordinal);
        Assert.Equal(sagaId, history.GetProperty("sagaId").GetGuid());
        var stages = history.GetProperty("changes").EnumerateArray().Select(c => Str(c, "stateAfterChange")).ToList();
        Assert.Contains(stages, s => s.Contains("Started", StringComparison.Ordinal));
        Assert.Contains(stages, s => s.Contains("Completed", StringComparison.Ordinal));
        Assert.All(history.GetProperty("changes").EnumerateArray(), c => Assert.Equal("Billing", Str(c, "endpoint")));
    }

    [Fact]
    public async Task An_unknown_saga_gives_an_explanatory_error_not_a_crash()
    {
        await using var mcp = await ReadOnlyServerAsync();

        var result = await mcp.CallRawAsync("get_saga_history", new { sagaId = Guid.NewGuid() }, Ct);

        Assert.True(result.IsError);
        Assert.Contains("no audit history", McpHarness.TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_successfully_processed_message_can_be_found_by_searching_audited_messages()
    {
        await using var mcp = await ReadOnlyServerAsync();
        Workload.State.BillingFails = false;
        var messageId = await Platform.Workload.SendChargeAsync(Guid.NewGuid());

        var hit = await Eventually.Async(
            async () => Items(await mcp.CallAsync("search_messages", new { query = messageId.ToString() }, Ct))
                .FirstOrDefault(m => Str(m, "messageId") == messageId.ToString()),
            m => m.ValueKind == System.Text.Json.JsonValueKind.Object,
            $"message {messageId} to be searchable",
            cancellationToken: Ct);

        Assert.Contains("ChargeCustomer", Str(hit, "messageType"), StringComparison.Ordinal);
        Assert.Equal("Billing", Str(hit, "receivingEndpoint"));
        Assert.DoesNotContain("\"body\"", hit.GetRawText(), StringComparison.Ordinal);
    }
}
