using Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;
using Cjoergensen.ServiceControl.Mcp.TestWorkload;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests;

public sealed class CustomCheckTests(PlatformFixture platform) : PlatformTest(platform)
{
    const string CheckId = "Integration-Dependency";

    [Fact]
    public async Task A_failing_custom_check_is_listed_with_its_reason_and_clears_when_it_recovers()
    {
        await using var mcp = await ReadOnlyServerAsync();
        try
        {
            Workload.State.DependencyHealthy = false;
            var failing = await Eventually.Async(
                async () => Items(await mcp.CallAsync("list_custom_checks", new { status = "fail", pageSize = 50 }, Ct))
                    .FirstOrDefault(c => Str(c, "customCheckId") == CheckId),
                c => c.ValueKind == System.Text.Json.JsonValueKind.Object,
                "the integration custom check to be reported as failing",
                cancellationToken: Ct);
            Assert.Equal(WorkloadState.DependencyFailureReason, Str(failing, "failureReason"));
            Assert.Equal("Billing", Str(failing, "endpoint"));
            Assert.Equal("Integration", Str(failing, "category"));
            Assert.False(failing.GetProperty("internal").GetBoolean());

            Workload.State.DependencyHealthy = true;
            await Eventually.Async(
                async () => Items(await mcp.CallAsync("list_custom_checks", new { status = "pass", pageSize = 50 }, Ct))
                    .Any(c => Str(c, "customCheckId") == CheckId),
                passed => passed,
                "the integration custom check to be reported as passing again",
                cancellationToken: Ct);
        }
        finally
        {
            Workload.State.DependencyHealthy = true;
        }
    }

    [Fact]
    public async Task Dismissing_a_custom_check_is_accepted_and_the_endpoint_reports_it_again()
    {
        await using var mcp = await WritableServerAsync();

        var listed = await Eventually.Async(
            async () => Items(await mcp.CallAsync("list_custom_checks", new { status = "", pageSize = 50 }, Ct))
                .FirstOrDefault(c => Str(c, "customCheckId") == CheckId),
            c => c.ValueKind == System.Text.Json.JsonValueKind.Object,
            "the integration custom check to be listed",
            cancellationToken: Ct);
        var id = Str(listed, "id");
        Assert.StartsWith("CustomChecks/", id, StringComparison.Ordinal);

        // The id is passed exactly as listed. Dismissing removes ServiceControl's record; the healthy endpoint reports again.
        var outcome = await mcp.CallAsync("dismiss_custom_check", new { id }, Ct);
        Assert.True(outcome.GetProperty("accepted").GetBoolean());

        await Eventually.Async(
            async () => Items(await mcp.CallAsync("list_custom_checks", new { status = "", pageSize = 50 }, Ct))
                .Any(c => Str(c, "customCheckId") == CheckId),
            present => present,
            "the endpoint to report the dismissed custom check again",
            cancellationToken: Ct);
    }
}
