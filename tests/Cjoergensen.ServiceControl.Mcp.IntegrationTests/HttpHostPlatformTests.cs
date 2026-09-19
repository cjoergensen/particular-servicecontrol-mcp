using Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;
using Cjoergensen.ServiceControl.Mcp.TestWorkload;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests;

/// <summary>The complete path over HTTP against a real (unsecured) platform: real workload, real ServiceControl, real host process, real MCP client.</summary>
public sealed class HttpHostPlatformTests(PlatformFixture platform) : PlatformTest(platform)
{
    Dictionary<string, string> Settings(bool writes) => new()
    {
        ["Url"] = Platform.PrimaryUrl,
        ["MonitoringUrl"] = Platform.MonitoringUrl ?? string.Empty,
        ["EnableWrites"] = writes ? "true" : "false",
        ["Http__AllowAnonymous"] = "true"
    };

    [Fact]
    public async Task A_failed_message_is_found_retried_and_resolved_entirely_over_http()
    {
        await using var host = await HttpHost.StartAsync(Settings(writes: true), Ct);
        await using var mcp = await host.ConnectAsync(bearerToken: null, Ct);
        try
        {
            var (messageId, failedId) = await FailAMessageAsync(mcp);
            Workload.State.BillingFails = false;

            var outcome = await mcp.CallAsync("retry_failed_message", new { id = failedId }, Ct);
            Assert.True(outcome.GetProperty("accepted").GetBoolean());

            await WaitForFailedMessageAsync(mcp, messageId, "resolved");
        }
        finally
        {
            Workload.State.BillingFails = false;
        }
    }

    [Fact]
    public async Task The_health_overview_and_monitoring_work_over_http()
    {
        await using var host = await HttpHost.StartAsync(Settings(writes: false), Ct);
        await using var mcp = await host.ConnectAsync(bearerToken: null, Ct);

        var overview = await mcp.CallAsync("get_health_overview", null, Ct);

        Assert.Empty(overview.GetProperty("problems").EnumerateArray());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, overview.GetProperty("monitoring").ValueKind);
    }

    [Fact]
    public async Task Writes_are_off_by_default_over_http_too()
    {
        await using var host = await HttpHost.StartAsync(Settings(writes: false), Ct);
        await using var mcp = await host.ConnectAsync(bearerToken: null, Ct);

        var tools = await mcp.ToolNamesAsync(Ct);

        Assert.DoesNotContain("retry_failed_message", tools);
    }
}
