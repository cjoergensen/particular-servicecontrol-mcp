using System.Text.RegularExpressions;
using Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;
using Cjoergensen.ServiceControl.Mcp.TestWorkload;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests;

/// <summary>
/// The MCP server against a ServiceControl platform that is configured like a real secured installation: HTTPS with a private CA, OIDC authentication
/// against Keycloak, and role-based authorization. Every test drives the real executable over stdio.
/// </summary>
public sealed partial class SecuredPlatformTests(SecuredPlatformFixture platform) : SecuredPlatformTest(platform)
{
    // ---- Without the right setup, the failure is explained ---------------------------------------------------------

    [Fact]
    public async Task Without_credentials_a_tool_explains_that_authentication_is_required()
    {
        await using var mcp = await ServerAsync(enableWrites: false);

        var result = await mcp.CallRawAsync("list_failed_messages", null, Ct);

        Assert.True(result.IsError);
        var text = McpHarness.TextOf(result);
        Assert.Contains("401", text, StringComparison.Ordinal);
        Assert.Contains("Auth", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_health_overview_reports_unreadable_sections_instead_of_looking_healthy()
    {
        await using var mcp = await ServerAsync(enableWrites: false);

        var overview = await mcp.CallAsync("get_health_overview", null, Ct);

        // A section that could not be read is left out entirely, and the problems say why.
        Assert.False(overview.TryGetProperty("failedMessages", out var counts) && counts.ValueKind != System.Text.Json.JsonValueKind.Null);
        Assert.NotEmpty(overview.GetProperty("problems").EnumerateArray());
    }

    [Fact]
    public async Task An_untrusted_private_ca_is_reported_as_a_certificate_problem_not_ignored()
    {
        // Same platform, but the server is not told to trust its CA: validation must stay on.
        await using var mcp = await McpHarness.StartAsync(Platform, enableWrites: false, Ct);

        var result = await mcp.CallRawAsync("list_failed_messages", null, Ct);

        Assert.True(result.IsError);
        var text = McpHarness.TextOf(result);
        Assert.Contains("Could not reach ServiceControl", text, StringComparison.Ordinal);
        Assert.Contains("certificate", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_invalid_token_is_rejected_with_guidance()
    {
        await using var mcp = await ServerAsync(enableWrites: false, ("Auth__Token", "not-a-real-token"));

        var result = await mcp.CallRawAsync("list_failed_messages", null, Ct);

        Assert.True(result.IsError);
        Assert.Contains("401", McpHarness.TextOf(result), StringComparison.Ordinal);
    }

    // ---- People: a token per role ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_reader_can_read_everything_but_is_not_offered_write_tools()
    {
        await using var mcp = await ServerWithTokenAsync(KeycloakRealm.ReaderUser, enableWrites: true);

        var tools = await mcp.ToolNamesAsync(Ct);
        Assert.Contains("list_failed_messages", tools);
        Assert.Contains("get_saga_history", tools);
        Assert.Contains("search_messages", tools);
        Assert.Contains("get_health_overview", tools);
        Assert.All(WriteTools, tool => Assert.DoesNotContain(tool, tools));

        var overview = await mcp.CallAsync("get_health_overview", null, Ct);
        Assert.Empty(overview.GetProperty("problems").EnumerateArray());

        // A client can ask for a tool it was never shown; the answer explains why not, and ServiceControl is never asked.
        var refused = await mcp.CallRawAsync("retry_failed_message", new { id = "anything" }, Ct);
        Assert.True(refused.IsError);
        Assert.Contains("roles: reader", McpHarness.TextOf(refused), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_writer_is_offered_write_tools_and_a_retry_really_works_through_authentication()
    {
        await using var mcp = await ServerWithTokenAsync(KeycloakRealm.WriterUser, enableWrites: true);
        try
        {
            var tools = await mcp.ToolNamesAsync(Ct);
            Assert.All(WriteTools, tool => Assert.Contains(tool, tools));

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
    public async Task Writes_stay_disabled_for_a_writer_unless_they_are_enabled()
    {
        await using var mcp = await ServerWithTokenAsync(KeycloakRealm.WriterUser, enableWrites: false);

        var tools = await mcp.ToolNamesAsync(Ct);

        Assert.Contains("list_failed_messages", tools);
        Assert.All(WriteTools, tool => Assert.DoesNotContain(tool, tools));
    }

    // ---- Bots: client credentials ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_writer_bot_signs_in_by_itself_with_client_credentials()
    {
        await using var mcp = await ServerAsync(
            enableWrites: true, ("Auth__ClientId", KeycloakRealm.WriterBot), ("Auth__ClientSecret", KeycloakRealm.BotSecret));

        var tools = await mcp.ToolNamesAsync(Ct);
        Assert.All(WriteTools, tool => Assert.Contains(tool, tools));

        var counts = await mcp.CallAsync("get_failed_message_counts", null, Ct);
        Assert.True(counts.GetProperty("unresolved").GetInt64() >= 0);
    }

    [Fact]
    public async Task A_reader_bot_is_limited_to_reading_by_the_roles_of_its_client()
    {
        await using var mcp = await ServerAsync(
            enableWrites: true, ("Auth__ClientId", KeycloakRealm.ReaderBot), ("Auth__ClientSecret", KeycloakRealm.BotSecret));

        var tools = await mcp.ToolNamesAsync(Ct);

        Assert.Contains("list_failed_messages", tools);
        Assert.All(WriteTools, tool => Assert.DoesNotContain(tool, tools));
    }

    [Fact]
    public async Task A_wrong_client_secret_says_which_setting_to_check_and_never_echoes_it()
    {
        await using var mcp = await ServerAsync(enableWrites: false, ("Auth__ClientId", KeycloakRealm.WriterBot), ("Auth__ClientSecret", "WRONG-SECRET-VALUE"));

        var result = await mcp.CallRawAsync("list_failed_messages", null, Ct);

        Assert.True(result.IsError);
        var text = McpHarness.TextOf(result);
        Assert.Contains("Auth:ClientSecret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("WRONG-SECRET-VALUE", text, StringComparison.Ordinal);
    }

    // ---- Token command ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_token_command_supplies_the_credentials()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses /bin/echo as the token command.");
        var token = await Platform.Keycloak.UserTokenAsync(KeycloakRealm.ReaderUser, Ct);
        await using var mcp = await ServerAsync(enableWrites: false, ("Auth__TokenCommand", "/bin/echo"), ("Auth__TokenCommandArguments__0", token));

        var counts = await mcp.CallAsync("get_failed_message_counts", null, Ct);

        Assert.True(counts.GetProperty("unresolved").GetInt64() >= 0);
    }

    // ---- The services behind the primary instance ------------------------------------------------------------------

    [Fact]
    public async Task The_token_is_honoured_by_the_audit_and_monitoring_instances_too()
    {
        await using var mcp = await ServerWithTokenAsync(KeycloakRealm.ReaderUser, enableWrites: false);
        var orderId = Guid.NewGuid();

        // Saga history and message search are answered by the audit instance, reached through the primary with the caller's token forwarded.
        await Platform.Workload.StartOrderAsync(orderId);
        await Eventually.Async(() => Task.FromResult(Workload.State.SagaIdsByOrder.ContainsKey(orderId)), started => started, "the saga to start", TimeSpan.FromSeconds(30), Ct);
        var history = await Eventually.Async(
            async () => await mcp.CallAsync("get_saga_history", new { sagaId = Workload.State.SagaIdsByOrder[orderId] }, Ct),
            h => h.GetProperty("totalChanges").GetInt32() >= 1,
            "the saga's audit history through the secured audit instance",
            cancellationToken: Ct);
        Assert.Contains("OrderPolicy", Str(history, "sagaType"), StringComparison.Ordinal);

        var monitored = await Eventually.Async(
            async () => (await mcp.CallAsync("list_monitored_endpoints", null, Ct)).EnumerateArray().Any(e => Str(e, "name") == Workload.BillingEndpoint),
            found => found,
            "Billing to appear in the secured monitoring instance",
            cancellationToken: Ct);
        Assert.True(monitored);

        var overview = await mcp.CallAsync("get_health_overview", null, Ct);
        Assert.Empty(overview.GetProperty("problems").EnumerateArray());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, overview.GetProperty("monitoring").ValueKind);
    }

    // ---- Device code: a person signs in in their own browser -------------------------------------------------------

    [Fact]
    public async Task Device_code_sign_in_is_relayed_to_the_user_and_completes_when_they_approve()
    {
        await using var mcp = await ServerAsync(enableWrites: true, ("Auth__ClientId", KeycloakRealm.CliClient));

        // 1. The first use cannot proceed without a person: the error carries the instructions the agent tells the user.
        var first = await mcp.CallRawAsync("get_failed_message_counts", null, Ct);
        Assert.True(first.IsError);
        var instructions = McpHarness.TextOf(first);
        Assert.True(instructions.Contains("Sign-in is", StringComparison.Ordinal), "Unexpected tool error: " + instructions);
        var verificationUri = VerificationUri().Match(instructions).Value;
        Assert.False(string.IsNullOrEmpty(verificationUri), "no verification address in: " + instructions);

        // 2. The person opens the address, signs in as a writer and approves.
        await Platform.Keycloak.ApproveDeviceAsync(verificationUri, KeycloakRealm.WriterUser, KeycloakRealm.UserPassword, Ct);

        // 3. Repeating the request now succeeds, as that person, with their roles.
        (System.Text.Json.JsonElement? Value, string Error) counts;
        try
        {
            counts = await Eventually.Async(
                async () =>
                {
                    var attempt = await mcp.CallRawAsync("get_failed_message_counts", null, Ct);
                    return attempt.IsError == true ? (Value: (System.Text.Json.JsonElement?)null, Error: McpHarness.TextOf(attempt)) : (Value: attempt.StructuredContent, Error: string.Empty);
                },
                outcome => outcome.Value is not null,
                "the device sign-in to complete",
                TimeSpan.FromSeconds(60),
                Ct);
        }
        catch (TimeoutException)
        {
            // The server's own log records what the identity provider answered on each poll.
            throw new InvalidOperationException("Device sign-in did not complete. Server log:\n" + string.Join('\n', mcp.ServerLog));
        }

        Assert.True(counts.Value!.Value.GetProperty("unresolved").GetInt64() >= 0);

        // The signed-in person is a writer, so the write tools appear once their permissions are known.
        var tools = await Eventually.Async(
            async () => await mcp.ToolNamesAsync(Ct),
            names => WriteTools.All(names.Contains),
            "write tools to be offered to the signed-in writer",
            TimeSpan.FromSeconds(60),
            Ct);
        Assert.Contains("retry_failed_message", tools);
    }

    [Fact]
    public async Task When_the_identity_provider_refuses_offline_access_sign_in_recovers_without_it()
    {
        // Bob is not allowed offline (long-lived) tokens, which ServiceControl's advertised scopes ask for. The server must not get stuck.
        await using var mcp = await ServerAsync(enableWrites: true, ("Auth__ClientId", KeycloakRealm.CliClient));

        var first = await mcp.CallRawAsync("get_failed_message_counts", null, Ct);
        var firstUri = VerificationUri().Match(McpHarness.TextOf(first)).Value;
        await Platform.Keycloak.ApproveDeviceAsync(firstUri, KeycloakRealm.ReaderUser, KeycloakRealm.UserPassword, Ct);

        // The approval is accepted, but exchanging it for a token is refused; the failure is explained and a retry is invited.
        var refused = await Eventually.Async(
            async () => McpHarness.TextOf(await mcp.CallRawAsync("get_failed_message_counts", null, Ct)),
            text => text.Contains("Sign-in failed", StringComparison.Ordinal),
            "the refused offline access to be reported",
            TimeSpan.FromSeconds(60),
            Ct);
        Assert.Contains("offline", refused, StringComparison.OrdinalIgnoreCase);

        // Asking again starts a new sign-in that no longer requests offline access, and this time it completes.
        var second = await mcp.CallRawAsync("get_failed_message_counts", null, Ct);
        var secondUri = VerificationUri().Match(McpHarness.TextOf(second)).Value;
        Assert.NotEqual(firstUri, secondUri);
        await Platform.Keycloak.ApproveDeviceAsync(secondUri, KeycloakRealm.ReaderUser, KeycloakRealm.UserPassword, Ct);

        var counts = await Eventually.Async(
            async () => await mcp.CallRawAsync("get_failed_message_counts", null, Ct),
            result => result.IsError != true,
            "sign-in to complete without offline access",
            TimeSpan.FromSeconds(60),
            Ct);
        Assert.True(counts.StructuredContent!.Value.GetProperty("unresolved").GetInt64() >= 0);

        // Bob is a reader, so once his permissions are known the write tools are not offered.
        var tools = await Eventually.Async(
            async () => await mcp.ToolNamesAsync(Ct),
            names => !names.Contains("retry_failed_message"),
            "write tools to be withheld from the signed-in reader",
            TimeSpan.FromSeconds(30),
            Ct);
        Assert.Contains("list_failed_messages", tools);
    }

    [GeneratedRegex(@"https://\S*user_code=[A-Za-z0-9-]+")]
    private static partial Regex VerificationUri();
}
