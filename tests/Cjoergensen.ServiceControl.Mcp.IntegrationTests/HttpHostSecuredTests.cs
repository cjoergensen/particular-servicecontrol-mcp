using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;
using Cjoergensen.ServiceControl.Mcp.TestWorkload;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests;

/// <summary>
/// The HTTP host in front of a fully secured platform. The MCP server is an OAuth resource server: it accepts only tokens issued for it, and reaches
/// ServiceControl either as a service principal (enforcing each caller's own roles itself) or by exchanging each caller's token so ServiceControl
/// sees that caller. A caller's token is never forwarded.
/// </summary>
public sealed class HttpHostSecuredTests(SecuredPlatformFixture platform) : SecuredPlatformTest(platform)
{
    Dictionary<string, string> Common(bool writes) => new()
    {
        ["Url"] = Platform.PrimaryUrl,
        ["MonitoringUrl"] = Platform.MonitoringUrl!,
        ["EnableWrites"] = writes ? "true" : "false",
        ["TrustedCaCertificatePath"] = Platform.CaCertificatePath,
        ["Http__Authority"] = Platform.Authority,
        ["Http__Audience"] = KeycloakRealm.HttpServerClient,
        ["Http__RolesClaim"] = "realm_access.roles"
    };

    /// <summary>The host reaches ServiceControl as one identity (a writer bot); callers are limited by their own roles.</summary>
    Dictionary<string, string> ServicePrincipal(bool writes)
    {
        var settings = Common(writes);
        settings["Auth__ClientId"] = KeycloakRealm.WriterBot;
        settings["Auth__ClientSecret"] = KeycloakRealm.BotSecret;
        return settings;
    }

    /// <summary>The host exchanges each caller's token, so ServiceControl sees the caller.</summary>
    Dictionary<string, string> Exchanging(bool writes)
    {
        var settings = Common(writes);
        settings["Auth__Mode"] = "TokenExchange";
        settings["Auth__ClientId"] = KeycloakRealm.HttpServerClient;
        settings["Auth__ClientSecret"] = KeycloakRealm.HttpServerSecret;
        return settings;
    }

    Task<string> CallerToken(string user) => Platform.Keycloak.UserTokenForMcpServerAsync(user, Ct);

    static HttpRequestMessage Initialize(Uri endpoint, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}""",
                System.Text.Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    // ---- The MCP server as an OAuth resource server ----------------------------------------------------------------

    [Fact]
    public async Task An_unauthenticated_request_is_challenged_and_points_to_the_protected_resource_metadata()
    {
        await using var host = await HttpHost.StartAsync(ServicePrincipal(writes: false), Ct);

        using var challenge = await host.SendAsync(Initialize(host.McpEndpoint, token: null), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        var header = string.Join(" ", challenge.Headers.WwwAuthenticate.Select(h => h.ToString()));
        Assert.Contains("Bearer", header, StringComparison.Ordinal);
        Assert.Contains("resource_metadata", header, StringComparison.Ordinal);

        // RFC 9728: the metadata tells a client which authorization server to use, without any prior configuration.
        using var metadata = await host.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(host.BaseAddress, ".well-known/oauth-protected-resource")), Ct);
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        using var document = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync(Ct));
        Assert.Contains(document.RootElement.GetProperty("authorization_servers").EnumerateArray(), s => s.GetString() == Platform.Authority);
    }

    [Fact]
    public async Task A_token_issued_for_servicecontrol_is_not_accepted_by_the_mcp_server()
    {
        await using var host = await HttpHost.StartAsync(ServicePrincipal(writes: false), Ct);

        // This token is perfectly valid for ServiceControl itself (audience "servicecontrol"). Accepting it here would mean this server would also be
        // willing to relay tokens meant for other services: exactly the "token passthrough" the MCP specification forbids.
        var forServiceControl = await Platform.Keycloak.UserTokenAsync(KeycloakRealm.WriterUser, Ct);
        using var rejected = await host.SendAsync(Initialize(host.McpEndpoint, forServiceControl), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        using var accepted = await host.SendAsync(Initialize(host.McpEndpoint, await CallerToken(KeycloakRealm.WriterUser)), Ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task Garbage_and_missing_credentials_never_reach_a_tool()
    {
        await using var host = await HttpHost.StartAsync(ServicePrincipal(writes: false), Ct);

        using var garbage = await host.SendAsync(Initialize(host.McpEndpoint, "not-a-jwt"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(async () => await host.ConnectAsync(bearerToken: null, Ct));
    }

    // ---- Service principal: the server enforces each caller's roles ------------------------------------------------

    [Fact]
    public async Task With_a_service_principal_a_reader_caller_is_limited_even_though_the_principal_can_write()
    {
        await using var host = await HttpHost.StartAsync(ServicePrincipal(writes: true), Ct);
        await using var bob = await host.ConnectAsync(await CallerToken(KeycloakRealm.ReaderUser), Ct);

        var tools = await bob.ToolNamesAsync(Ct);
        Assert.Contains("list_failed_messages", tools);
        Assert.All(WriteTools, tool => Assert.DoesNotContain(tool, tools));

        // The upstream identity is a writer, so ServiceControl would allow this: only the server's own check stands in the way.
        var refused = await bob.CallRawAsync("retry_failed_message", new { id = "anything" }, Ct);
        Assert.True(refused.IsError);
        Assert.Contains("your roles: reader", McpHarness.TextOf(refused), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_a_service_principal_a_writer_caller_can_retry_and_the_audit_log_records_who_asked()
    {
        await using var host = await HttpHost.StartAsync(ServicePrincipal(writes: true), Ct);
        await using var alice = await host.ConnectAsync(await CallerToken(KeycloakRealm.WriterUser), Ct);
        try
        {
            var tools = await alice.ToolNamesAsync(Ct);
            Assert.All(WriteTools, tool => Assert.Contains(tool, tools));

            var (messageId, failedId) = await FailAMessageAsync(alice);
            Workload.State.BillingFails = false;
            await alice.CallAsync("retry_failed_message", new { id = failedId }, Ct);
            await WaitForFailedMessageAsync(alice, messageId, "resolved");

            // ServiceControl saw only the service principal, so the server's own log is the record of which person asked.
            Assert.Contains(host.Log, line => line.Contains("AUDIT change", StringComparison.Ordinal) && line.Contains("alice", StringComparison.Ordinal) &&
                                              line.Contains("retry_failed_message", StringComparison.Ordinal));
        }
        finally
        {
            Workload.State.BillingFails = false;
        }
    }

    // ---- Token exchange: ServiceControl sees the caller ------------------------------------------------------------

    [Fact]
    public async Task With_token_exchange_each_caller_gets_exactly_what_their_own_roles_allow()
    {
        await using var host = await HttpHost.StartAsync(Exchanging(writes: true), Ct);
        await using var alice = await host.ConnectAsync(await CallerToken(KeycloakRealm.WriterUser), Ct);
        await using var bob = await host.ConnectAsync(await CallerToken(KeycloakRealm.ReaderUser), Ct);

        var forAlice = await alice.ToolNamesAsync(Ct);
        var forBob = await bob.ToolNamesAsync(Ct);

        Assert.All(WriteTools, tool => Assert.Contains(tool, forAlice));
        Assert.All(WriteTools, tool => Assert.DoesNotContain(tool, forBob));

        // Bob's reads work through his own exchanged token.
        var overview = await bob.CallAsync("get_health_overview", null, Ct);
        Assert.Empty(overview.GetProperty("problems").EnumerateArray());

        // And the server never asked ServiceControl on anyone else's behalf: bob cannot borrow alice's rights.
        var refused = await bob.CallRawAsync("retry_failed_message", new { id = "anything" }, Ct);
        Assert.True(refused.IsError);
    }

    [Fact]
    public async Task With_token_exchange_a_writer_really_changes_things_as_themselves()
    {
        await using var host = await HttpHost.StartAsync(Exchanging(writes: true), Ct);
        await using var alice = await host.ConnectAsync(await CallerToken(KeycloakRealm.WriterUser), Ct);
        try
        {
            var (messageId, failedId) = await FailAMessageAsync(alice);
            Workload.State.BillingFails = false;
            await alice.CallAsync("retry_failed_message", new { id = failedId }, Ct);
            await WaitForFailedMessageAsync(alice, messageId, "resolved");

            Assert.Contains(host.Log, line => line.Contains("AUDIT change", StringComparison.Ordinal) && line.Contains("alice", StringComparison.Ordinal));
        }
        finally
        {
            Workload.State.BillingFails = false;
        }
    }

    [Fact]
    public async Task Concurrent_callers_never_see_each_others_permissions()
    {
        await using var host = await HttpHost.StartAsync(Exchanging(writes: true), Ct);
        var aliceToken = await CallerToken(KeycloakRealm.WriterUser);
        var bobToken = await CallerToken(KeycloakRealm.ReaderUser);

        // Many overlapping requests from both people, each on their own connection. Any mix-up of identity would show up as a reader seeing write
        // tools, or a writer missing them.
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(async i =>
        {
            var isAlice = i % 2 == 0;
            await using var client = await host.ConnectAsync(isAlice ? aliceToken : bobToken, Ct);
            var tools = await client.ToolNamesAsync(Ct);
            return (isAlice, HasWrites: tools.Contains("retry_failed_message"));
        }));

        Assert.All(results.Where(r => r.isAlice), r => Assert.True(r.HasWrites, "a writer was denied write tools"));
        Assert.All(results.Where(r => !r.isAlice), r => Assert.False(r.HasWrites, "a reader was shown write tools"));
    }
}
