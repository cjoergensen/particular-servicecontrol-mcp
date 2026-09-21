using System.Text.Json;
using Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;
using Cjoergensen.ServiceControl.Mcp.TestWorkload;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests;

/// <summary>Helpers shared by the tests of both platforms. Tests in a collection run one at a time, so they may flip workload switches.</summary>
public abstract class TestBase
{
    protected abstract Workload TestWorkload { get; }

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static IEnumerable<JsonElement> Items(JsonElement result) => result.GetProperty("items").EnumerateArray();

    protected static string Str(JsonElement element, string property) => element.GetProperty(property).GetString() ?? string.Empty;

    /// <summary>
    /// Makes Billing fail one payment and waits until ServiceControl lists it, returning the id ServiceControl uses for it.
    /// Leaves the failure switch on: callers decide when the system "recovers".
    /// </summary>
    protected async Task<(Guid MessageId, string FailedId)> FailAMessageAsync(McpHarness mcp)
    {
        Workload.State.BillingFails = true;
        var messageId = await TestWorkload.SendChargeAsync(Guid.NewGuid());
        var failedId = await WaitForFailedMessageAsync(mcp, messageId, "unresolved");
        return (messageId, failedId);
    }

    /// <summary>Waits for the message to appear in <paramref name="status"/> and returns its ServiceControl id.</summary>
    protected static async Task<string> WaitForFailedMessageAsync(McpHarness mcp, Guid messageId, string status)
    {
        var found = await Eventually.Async(
            async () => Items(await mcp.CallAsync("list_failed_messages", new { endpoint = Workload.BillingEndpoint, status, pageSize = 50 }, Ct))
                .Select(i => (Element: i, MessageId: Str(i, "messageId")))
                .FirstOrDefault(i => i.MessageId == messageId.ToString()),
            item => item.MessageId == messageId.ToString(),
            $"message {messageId} to be listed as {status}",
            cancellationToken: Ct);
        return Str(found.Element, "id");
    }
}

/// <summary>Base class for tests against the unsecured platform.</summary>
[Collection(PlatformCollectionDefinition.Name)]
public abstract class PlatformTest(PlatformFixture platform) : TestBase
{
    protected PlatformFixture Platform { get; } = platform;

    protected override Workload TestWorkload => Platform.Workload;

    protected Task<McpHarness> ReadOnlyServerAsync() => McpHarness.StartAsync(Platform, enableWrites: false, Ct);

    protected Task<McpHarness> WritableServerAsync() => McpHarness.StartAsync(Platform, enableWrites: true, Ct);
}

/// <summary>Base class for tests against the platform secured with TLS, OIDC authentication and role-based authorization.</summary>
[Collection(SecuredPlatformCollectionDefinition.Name)]
public abstract class SecuredPlatformTest(SecuredPlatformFixture platform) : TestBase
{
    protected SecuredPlatformFixture Platform { get; } = platform;

    protected override Workload TestWorkload => Platform.Workload;

    /// <summary>Starts the server trusting the platform's private CA, with the given credentials settings (names without the prefix).</summary>
    protected Task<McpHarness> ServerAsync(bool enableWrites, params (string Name, string Value)[] settings)
    {
        var all = new Dictionary<string, string> { ["TrustedCaCertificatePath"] = Platform.CaCertificatePath };
        foreach (var (name, value) in settings)
        {
            all[name] = value;
        }

        return McpHarness.StartAsync(Platform, enableWrites, Ct, all);
    }

    protected async Task<McpHarness> ServerWithTokenAsync(string username, bool enableWrites) =>
        await ServerAsync(enableWrites, ("Auth__Token", await Platform.Keycloak.UserTokenAsync(username, Ct)));

    protected static readonly string[] WriteTools =
    [
        "retry_failed_message", "retry_failed_messages", "retry_endpoint_failures", "retry_failure_group",
        "archive_failed_messages", "archive_failure_group", "unarchive_failed_messages", "dismiss_custom_check"
    ];
}
