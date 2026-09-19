using System.IO.Pipelines;
using System.Text.Json;
using Cjoergensen.ServiceControl.Mcp.Client;
using Cjoergensen.ServiceControl.Mcp.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static Cjoergensen.ServiceControl.Mcp.Tests.Support.TestKit;

namespace Cjoergensen.ServiceControl.Mcp.Tests;

/// <summary>
/// Drives the real MCP server through a real MCP client over in-memory pipes, with a fake ServiceControl behind it. This is the
/// closest a unit test gets to what an agent sees: the advertised tools, their schemas, and the results.
/// </summary>
public class McpProtocolTests
{
    sealed class Session(IHost host, McpClient client, FakeServiceControl fake) : IAsyncDisposable
    {
        public McpClient Client { get; } = client;

        public FakeServiceControl Fake { get; } = fake;

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await host.StopAsync(TestContext.Current.CancellationToken);
            host.Dispose();
        }
    }

    static async Task<Session> StartAsync(Dictionary<string, string?>? settings = null)
    {
        var fake = new FakeServiceControl()
            .Route("GET", "/api/errors", Payloads.FailedMessages, System.Net.HttpStatusCode.OK, ("Total-Count", "1"))
            .Route("GET", "/api/endpoints/", Payloads.FailedMessages, System.Net.HttpStatusCode.OK, ("Total-Count", "1"))
            .Route("GET", "/monitored-endpoints", Payloads.MonitoredEndpoints);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings ?? []);
        builder.Services
            .AddServiceControlMcp(builder.Configuration)
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        builder.Services.AddHttpClient<ServiceControlClient>().ConfigurePrimaryHttpMessageHandler(() => fake);
        builder.Services.AddHttpClient<MonitoringClient>().ConfigurePrimaryHttpMessageHandler(() => fake);

        var host = builder.Build();
        await host.StartAsync(Ct);

        var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
            cancellationToken: Ct);

        return new Session(host, client, fake);
    }

    [Fact]
    public async Task Read_tools_are_advertised_and_write_and_monitoring_tools_are_not_by_default()
    {
        await using var session = await StartAsync();

        var names = (await session.Client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToHashSet();

        Assert.Superset(
            new HashSet<string>
            {
                "get_health_overview", "list_failed_messages", "get_failed_message", "get_failed_message_counts", "list_failure_classifiers",
                "list_failure_groups", "list_failure_group_messages", "list_endpoints", "get_heartbeat_stats", "list_custom_checks",
                "get_saga_history", "search_messages"
            },
            names);
        Assert.DoesNotContain(names, n => n.StartsWith("list_monitored", StringComparison.Ordinal) || n.StartsWith("get_monitored", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("retry", StringComparison.Ordinal) || n.Contains("archive", StringComparison.Ordinal) || n.Contains("dismiss", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Monitoring_tools_appear_only_when_a_monitoring_url_is_configured()
    {
        await using var session = await StartAsync(new() { ["MonitoringUrl"] = "http://localhost:33633" });

        var names = (await session.Client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToHashSet();

        Assert.Contains("list_monitored_endpoints", names);
        Assert.Contains("get_monitored_endpoint", names);
    }

    [Fact]
    public async Task Every_tool_is_annotated_read_only_and_described()
    {
        await using var session = await StartAsync(new() { ["MonitoringUrl"] = "http://localhost:33633" });

        foreach (var tool in await session.Client.ListToolsAsync(cancellationToken: Ct))
        {
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint, $"{tool.Name} must be marked read-only");
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), $"{tool.Name} needs a description for the LLM");
        }
    }

    [Fact]
    public async Task Cancellation_and_service_parameters_are_not_part_of_the_tool_schema()
    {
        await using var session = await StartAsync();

        var tool = (await session.Client.ListToolsAsync(cancellationToken: Ct)).Single(t => t.Name == "list_failed_messages");
        var properties = tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();

        Assert.Contains("status", properties);
        Assert.Contains("modifiedWithinMinutes", properties);
        Assert.DoesNotContain("cancellationToken", properties);
    }

    [Fact]
    public async Task Calling_a_tool_returns_structured_content()
    {
        await using var session = await StartAsync();

        var result = await session.Client.CallToolAsync(
            "list_failed_messages",
            new Dictionary<string, object?> { ["endpoint"] = "Billing" },
            cancellationToken: Ct);

        Assert.True(result.IsError != true, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        var structured = result.StructuredContent!.Value;
        Assert.Equal("System.InvalidOperationException", structured.GetProperty("items")[0].GetProperty("exceptionType").GetString());
        Assert.Equal(1, structured.GetProperty("totalCount").GetInt64());
        Assert.Contains(session.Fake.Requests, r => r.PathAndQuery.Contains("/api/endpoints/Billing/errors", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_arguments_come_back_as_a_readable_tool_error_the_model_can_act_on()
    {
        await using var session = await StartAsync();

        var result = await session.Client.CallToolAsync(
            "list_failed_messages",
            new Dictionary<string, object?> { ["status"] = "sideways" },
            cancellationToken: Ct);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("Valid statuses are", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_servicecontrol_failure_comes_back_as_a_readable_tool_error()
    {
        await using var session = await StartAsync();
        session.Fake.Fail(new HttpRequestException("Connection refused"));

        var result = await session.Client.CallToolAsync("get_heartbeat_stats", cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("Could not reach ServiceControl", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_tools_are_advertised_only_when_writes_are_enabled_and_are_annotated_accordingly()
    {
        await using var session = await StartAsync(new() { ["EnableWrites"] = "true" });

        var tools = (await session.Client.ListToolsAsync(cancellationToken: Ct)).ToDictionary(t => t.Name);

        foreach (var name in new[] { "retry_failed_message", "retry_failed_messages", "retry_endpoint_failures", "retry_failure_group",
                     "archive_failed_messages", "archive_failure_group", "unarchive_failed_messages", "dismiss_custom_check" })
        {
            var tool = Assert.Contains(name, tools);
            Assert.NotEqual(true, tool.ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.Contains("explicit confirmation", tool.Description, StringComparison.Ordinal);
        }

        // Retrying is not destructive; archiving and dismissing remove things from view.
        Assert.False(tools["retry_failure_group"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tools["archive_failure_group"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tools["dismiss_custom_check"].ProtocolTool.Annotations?.DestructiveHint);

        // Reading is unaffected by enabling writes.
        Assert.True(tools["list_failed_messages"].ProtocolTool.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public async Task A_stale_bulk_confirmation_comes_back_as_a_tool_error_and_changes_nothing()
    {
        await using var session = await StartAsync(new() { ["EnableWrites"] = "true" });
        session.Fake.Override("GET", "/api/endpoints/Billing/errors", "[]", System.Net.HttpStatusCode.OK, ("Total-Count", "12"));

        var result = await session.Client.CallToolAsync(
            "retry_endpoint_failures",
            new Dictionary<string, object?> { ["endpoint"] = "Billing", ["expectedCount"] = 3 },
            cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("expectedCount=12", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Fake.Requests, r => r.Method == "POST");
    }

    static readonly string[] WriteTools =
    [
        "retry_failed_message", "retry_failed_messages", "retry_endpoint_failures", "retry_failure_group",
        "archive_failed_messages", "archive_failure_group", "unarchive_failed_messages", "dismiss_custom_check"
    ];

    [Fact]
    public async Task A_reader_is_not_offered_write_tools_even_when_writes_are_enabled()
    {
        await using var session = await StartAsync(new() { ["EnableWrites"] = "true" });
        session.Fake.Override("GET", "/api/my/routes", ToolAccessTests.RoutesJson("reader", includeWrites: false));

        var names = (await session.Client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToHashSet();

        Assert.Contains("list_failed_messages", names);
        Assert.Contains("get_health_overview", names);
        Assert.All(WriteTools, tool => Assert.DoesNotContain(tool, names));
    }

    [Fact]
    public async Task A_writer_is_offered_every_tool()
    {
        await using var session = await StartAsync(new() { ["EnableWrites"] = "true", ["MonitoringUrl"] = "http://localhost:33633" });
        session.Fake.Override("GET", "/api/my/routes", ToolAccessTests.RoutesJson("writer", includeWrites: true));

        var names = (await session.Client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToHashSet();

        Assert.All(WriteTools, tool => Assert.Contains(tool, names));
        Assert.Contains("list_monitored_endpoints", names);
    }

    [Fact]
    public async Task Calling_a_tool_that_was_hidden_is_refused_with_an_explanation_and_sends_nothing()
    {
        await using var session = await StartAsync(new() { ["EnableWrites"] = "true" });
        session.Fake.Override("GET", "/api/my/routes", ToolAccessTests.RoutesJson("reader", includeWrites: false));

        var result = await session.Client.CallToolAsync(
            "retry_failed_message", new Dictionary<string, object?> { ["id"] = "some-id" }, cancellationToken: Ct);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("roles: reader", text, StringComparison.Ordinal);
        Assert.Contains("writer role", text, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Fake.Requests, r => r.Method == "POST");
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.NotFound)]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.InternalServerError)]
    public async Task When_permissions_are_unknown_every_enabled_tool_is_still_offered(System.Net.HttpStatusCode status)
    {
        await using var session = await StartAsync(new() { ["EnableWrites"] = "true" });
        session.Fake.Override("GET", "/api/my/routes", "{}", status);

        var names = (await session.Client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToHashSet();

        Assert.All(WriteTools, tool => Assert.Contains(tool, names));
    }

    [Fact]
    public async Task Every_tool_is_either_gated_by_routes_or_deliberately_not()
    {
        await using var session = await StartAsync(new() { ["EnableWrites"] = "true", ["MonitoringUrl"] = "http://localhost:33633" });

        var names = (await session.Client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToList();

        // A new tool must declare the routes it calls (or be deliberately exempt), otherwise it would be offered to identities that may not use it.
        Assert.All(names, name => Assert.True(
            Tools.ToolRoutes.Required.ContainsKey(name) || Tools.ToolRoutes.Ungated.Contains(name),
            $"{name} is not in ToolRoutes.Required or ToolRoutes.Ungated"));
        Assert.All(Tools.ToolRoutes.Required.Keys, name => Assert.Contains(name, names));
    }

    [Fact]
    public async Task Server_identifies_itself()
    {
        await using var session = await StartAsync();

        Assert.Equal("servicecontrol-mcp", session.Client.ServerInfo.Name);
        Assert.False(string.IsNullOrEmpty(session.Client.ServerInfo.Version));
        _ = JsonSerializer.Serialize(session.Client.ServerCapabilities);
    }
}
