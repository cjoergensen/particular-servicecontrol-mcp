using Cjoergensen.ServiceControl.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// An empty builder on purpose: the default host would read every unprefixed environment variable into the
// configuration root, where a stray "URL" variable could silently override the ServiceControl address.
var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });

builder.Configuration
    .AddEnvironmentVariables(ServiceControlMcpOptions.EnvironmentPrefix)
    .AddCommandLine(args);

// stdout carries the MCP protocol, so all logging goes to stderr.
builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

// The HTTP client factory logs every request at Information, which would bury the messages an operator actually needs (such as a pending sign-in).
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

builder.Services
    .AddServiceControlMcp(builder.Configuration)
    .WithStdioServerTransport();

await builder.Build().RunAsync().ConfigureAwait(false);
