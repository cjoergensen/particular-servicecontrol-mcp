using Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;
using Cjoergensen.ServiceControl.Mcp.TestWorkload;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests;

public sealed class MonitoringAndOverviewTests(PlatformFixture platform) : PlatformTest(platform)
{
    [Fact]
    public async Task Monitored_endpoints_report_live_metrics_from_the_monitoring_instance()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Platform.MonitoringUrl), "No monitoring instance configured (set SCMCP_IT_MONITORING_URL).");
        await using var mcp = await ReadOnlyServerAsync();
        Workload.State.BillingFails = false;

        // Some traffic, so there is something to measure.
        for (var i = 0; i < 5; i++)
        {
            await Platform.Workload.SendChargeAsync(Guid.NewGuid());
        }

        var billing = await Eventually.Async(
            async () => (await mcp.CallAsync("list_monitored_endpoints", null, Ct)).EnumerateArray().FirstOrDefault(e => Str(e, "name") == Workload.BillingEndpoint),
            e => e.ValueKind == System.Text.Json.JsonValueKind.Object && e.GetProperty("metrics").EnumerateObject().Any(),
            "Billing to report metrics",
            cancellationToken: Ct);

        Assert.True(billing.GetProperty("connectedInstances").GetInt32() >= 1);
        Assert.False(billing.GetProperty("isStale").GetBoolean());

        var detail = await Eventually.Async(
            async () => await mcp.CallAsync("get_monitored_endpoint", new { endpointName = Workload.BillingEndpoint }, Ct),
            d => d.GetProperty("instances").GetArrayLength() >= 1,
            "Billing instance details",
            cancellationToken: Ct);
        Assert.Equal(Workload.BillingEndpoint, Str(detail, "name"));
    }

    [Fact]
    public async Task The_health_overview_answers_in_one_call_and_loads_every_section()
    {
        await using var mcp = await ReadOnlyServerAsync();

        var overview = await mcp.CallAsync("get_health_overview", null, Ct);

        Assert.Empty(overview.GetProperty("problems").EnumerateArray());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, overview.GetProperty("failedMessages").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, overview.GetProperty("heartbeats").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, overview.GetProperty("topFailureGroups").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, overview.GetProperty("deadEndpoints").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, overview.GetProperty("failingCustomChecks").ValueKind);
        if (!string.IsNullOrWhiteSpace(Platform.MonitoringUrl))
        {
            Assert.Equal(System.Text.Json.JsonValueKind.Object, overview.GetProperty("monitoring").ValueKind);
        }
    }

    [Fact]
    public async Task The_server_starts_and_advertises_its_tools_over_real_stdio()
    {
        await using var mcp = await ReadOnlyServerAsync();

        var names = await mcp.ToolNamesAsync(Ct);

        Assert.Contains("get_health_overview", names);
        Assert.Contains("list_monitored_endpoints", names);
        Assert.Equal(names.Count, names.Distinct().Count());
    }
}
