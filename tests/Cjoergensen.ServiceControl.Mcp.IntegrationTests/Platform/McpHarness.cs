using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;

/// <summary>
/// The real <c>servicecontrol-mcp</c> executable, launched as a child process and driven by a real MCP client over stdio, exactly as
/// Claude Desktop or Claude Code would. Configuration is passed the way a user would pass it: environment variables.
/// </summary>
public sealed class McpHarness : IAsyncDisposable
{
    readonly McpClient client;

    McpHarness(McpClient client, System.Collections.Concurrent.ConcurrentQueue<string> log)
    {
        this.client = client;
        ServerLog = log;
    }

    /// <summary>What the server wrote to stderr, for diagnosing a failing test.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> ServerLog { get; }

    public static async Task<McpHarness> StartAsync(
        IPlatform platform, bool enableWrites, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? settings = null)
    {
        var dll = typeof(McpHarness).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "StdioDll").Value
            ?? throw new InvalidOperationException("The path of the stdio server was not embedded at build time.");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException("The servicecontrol-mcp executable has not been built.", dll);
        }

        var environment = new Dictionary<string, string?>
        {
            ["SERVICECONTROL_MCP_Url"] = platform.PrimaryUrl,
            ["SERVICECONTROL_MCP_EnableWrites"] = enableWrites ? "true" : "false"
        };
        if (!string.IsNullOrWhiteSpace(platform.MonitoringUrl))
        {
            environment["SERVICECONTROL_MCP_MonitoringUrl"] = platform.MonitoringUrl;
        }

        // Extra settings are given without the prefix, e.g. ["Auth__Token"] = "..." becomes SERVICECONTROL_MCP_Auth__Token.
        foreach (var (name, value) in settings ?? new Dictionary<string, string>())
        {
            environment["SERVICECONTROL_MCP_" + name] = value;
        }

        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            StandardErrorLines = log.Enqueue,
            Name = "servicecontrol-mcp",
            Command = "dotnet",
            Arguments = [dll],
            EnvironmentVariables = environment
        });

        return new McpHarness(await McpClient.CreateAsync(transport, cancellationToken: cancellationToken), log);
    }

    /// <summary>Wraps an already connected client (for example over HTTP) so the same helpers work for every transport.</summary>
    public static McpHarness FromClient(McpClient client, System.Collections.Concurrent.ConcurrentQueue<string> log) => new(client, log);

    public async Task<IReadOnlyList<string>> ToolNamesAsync(CancellationToken cancellationToken) =>
        [.. (await client.ListToolsAsync(cancellationToken: cancellationToken)).Select(t => t.Name)];

    /// <summary>Calls a tool and returns its structured result. A tool error fails the calling test with the server's message.</summary>
    public async Task<JsonElement> CallAsync(string tool, object? arguments, CancellationToken cancellationToken)
    {
        var result = await CallRawAsync(tool, arguments, cancellationToken);
        if (result.IsError == true)
        {
            throw new McpToolFailedException(tool, TextOf(result));
        }

        return result.StructuredContent ?? throw new InvalidOperationException($"{tool} returned no structured content.");
    }

    /// <summary>Calls a tool without interpreting the outcome, for tests that expect it to be refused.</summary>
    public async Task<CallToolResult> CallRawAsync(string tool, object? arguments, CancellationToken cancellationToken) =>
        await client.CallToolAsync(tool, ToDictionary(arguments), cancellationToken: cancellationToken);

    public static string TextOf(CallToolResult result) =>
        string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    public ValueTask DisposeAsync() => client.DisposeAsync();

    static Dictionary<string, object?> ToDictionary(object? arguments) =>
        arguments is null
            ? []
            : arguments.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(arguments));
}

public sealed class McpToolFailedException(string tool, string message) : Exception($"Tool '{tool}' failed: {message}");
