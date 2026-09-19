using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using ModelContextProtocol.Client;

namespace Cjoergensen.ServiceControl.Mcp.IntegrationTests.Platform;

/// <summary>
/// The real HTTP host executable, started as a child process on a free loopback port and driven by a real MCP client over Streamable HTTP. Its
/// log is captured so tests can check what it recorded (for example who asked for a change).
/// </summary>
public sealed class HttpHost : IAsyncDisposable
{
    readonly Process process;
    readonly HttpClient probe = new();

    HttpHost(Process process, Uri baseAddress, ConcurrentQueue<string> log)
    {
        this.process = process;
        BaseAddress = baseAddress;
        Log = log;
    }

    public Uri BaseAddress { get; }

    public Uri McpEndpoint => new(BaseAddress, "mcp");

    /// <summary>Everything the host wrote to stdout and stderr.</summary>
    public ConcurrentQueue<string> Log { get; }

    /// <summary>Starts the host with the given settings (names without the prefix) and waits until it answers.</summary>
    public static async Task<HttpHost> StartAsync(IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken, string? urls = null)
    {
        var (process, log, baseAddress) = Launch(settings, urls);
        var host = new HttpHost(process, baseAddress, log);
        await host.WaitUntilReadyAsync(cancellationToken);
        return host;
    }

    /// <summary>Starts the host expecting it to refuse to run; returns its exit code and output.</summary>
    public static async Task<(int ExitCode, string Output)> StartExpectingRefusalAsync(IReadOnlyDictionary<string, string> settings, string urls, CancellationToken cancellationToken)
    {
        var (process, log, _) = Launch(settings, urls);
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("The host was expected to refuse to start but kept running. Output:\n" + string.Join('\n', log));
            }

            return (process.ExitCode, string.Join('\n', log));
        }
    }

    /// <summary>Connects a real MCP client over Streamable HTTP, presenting the token as a bearer credential (or none).</summary>
    public async Task<McpHarness> ConnectAsync(string? bearerToken, CancellationToken cancellationToken)
    {
        var options = new HttpClientTransportOptions { Endpoint = McpEndpoint, TransportMode = HttpTransportMode.StreamableHttp };
        if (bearerToken is not null)
        {
            options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + bearerToken };
        }

        var client = await McpClient.CreateAsync(new HttpClientTransport(options), cancellationToken: cancellationToken);
        return McpHarness.FromClient(client, Log);
    }

    /// <summary>A raw request, for tests of the HTTP behaviour itself (challenges, metadata).</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => probe.SendAsync(request, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        probe.Dispose();
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        process.Dispose();
    }

    static (Process Process, ConcurrentQueue<string> Log, Uri BaseAddress) Launch(IReadOnlyDictionary<string, string> settings, string? urls)
    {
        var dll = typeof(HttpHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "HttpDll").Value
            ?? throw new InvalidOperationException("The path of the HTTP host was not embedded at build time.");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException("The HTTP host has not been built.", dll);
        }

        var port = FreePort();
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(dll);
        start.Environment["ASPNETCORE_URLS"] = urls ?? $"http://127.0.0.1:{port}";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        foreach (var (name, value) in settings)
        {
            start.Environment["SERVICECONTROL_MCP_" + name] = value;
        }

        var log = new ConcurrentQueue<string>();
        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Enqueue(log, e.Data);
        process.ErrorDataReceived += (_, e) => Enqueue(log, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return (process, log, new Uri($"http://127.0.0.1:{port}/"));
    }

    static void Enqueue(ConcurrentQueue<string> log, string? line)
    {
        if (line is not null)
        {
            log.Enqueue(line);
        }
    }

    async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"The HTTP host exited with code {process.ExitCode} before it was ready:\n{string.Join('\n', Log)}");
            }

            try
            {
                using var response = await probe.GetAsync(new Uri(BaseAddress, "healthz"), cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }

        throw new TimeoutException("The HTTP host did not become ready. Log:\n" + string.Join('\n', Log));
    }

    static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
