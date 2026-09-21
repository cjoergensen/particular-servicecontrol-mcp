using Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;
using Cjoergensen.ServiceControl.Mcp.TestWorkload;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests;

public sealed class EndpointHealthTests(PlatformFixture platform) : PlatformTest(platform)
{
    [Fact]
    public async Task Running_endpoints_are_listed_as_beating()
    {
        await using var mcp = await ReadOnlyServerAsync();

        var endpoints = await Eventually.Async(
            async () => Items(await mcp.CallAsync("list_endpoints", new { pageSize = 50 }, Ct)).ToList(),
            list => list.Any(e => Str(e, "name") == Workload.BillingEndpoint && Str(e, "heartbeatStatus") == "beating") &&
                    list.Any(e => Str(e, "name") == Workload.ShippingEndpoint && Str(e, "heartbeatStatus") == "beating"),
            "Billing and Shipping to report heartbeats",
            cancellationToken: Ct);

        Assert.All(endpoints.Where(e => Str(e, "name") is Workload.BillingEndpoint or Workload.ShippingEndpoint), e =>
            Assert.True(e.GetProperty("heartbeatMonitored").GetBoolean()));
    }

    [Fact]
    public async Task An_endpoint_that_stops_is_reported_dead_after_the_grace_period()
    {
        await using var mcp = await ReadOnlyServerAsync();
        var name = "Ephemeral-" + Guid.NewGuid().ToString("N")[..8];

        await Platform.Workload.StartHeartbeatEndpointAsync(name, Ct);
        await Eventually.Async(
            async () => Items(await mcp.CallAsync("list_endpoints", new { heartbeatStatus = "beating", pageSize = 50 }, Ct)).Any(e => Str(e, "name") == name),
            beating => beating,
            $"{name} to be listed as beating",
            cancellationToken: Ct);

        await Platform.Workload.StopEndpointAsync(name);

        var dead = await Eventually.Async(
            async () => Items(await mcp.CallAsync("list_endpoints", new { heartbeatStatus = "dead", pageSize = 50 }, Ct)).FirstOrDefault(e => Str(e, "name") == name),
            e => e.ValueKind == System.Text.Json.JsonValueKind.Object,
            $"{name} to be listed as dead (grace period {PlatformFixture.HeartbeatGracePeriod.TotalSeconds:0}s)",
            TimeSpan.FromSeconds(120),
            Ct);
        Assert.Equal("dead", Str(dead, "heartbeatStatus"));

        var stats = await mcp.CallAsync("get_heartbeat_stats", null, Ct);
        Assert.True(stats.GetProperty("failing").GetInt32() >= 1);

        var overview = await mcp.CallAsync("get_health_overview", null, Ct);
        Assert.Contains(overview.GetProperty("deadEndpoints").EnumerateArray(), e => e.GetString() == name);
    }
}
