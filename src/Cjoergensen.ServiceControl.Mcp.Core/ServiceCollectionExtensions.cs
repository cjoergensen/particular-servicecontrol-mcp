using System.Net.Http.Headers;
using System.Reflection;
using Cjoergensen.ServiceControl.Mcp.Auth;
using Cjoergensen.ServiceControl.Mcp.Client;
using Cjoergensen.ServiceControl.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cjoergensen.ServiceControl.Mcp;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MCP server, the ServiceControl clients, credentials and the tools. The caller adds the transport
    /// (<c>WithStdioServerTransport</c> or <c>WithHttpTransport</c>) to the returned builder.
    /// </summary>
    public static IMcpServerBuilder AddServiceControlMcp(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ServiceControlMcpOptions>()
            .Bind(configuration)
            .ValidateDataAnnotations()
            .Validate(o => Uri.TryCreate(o.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https", "Url must be an absolute http(s) URL.")
            .Validate(o => o.DefaultPageSize <= o.MaxPageSize, "DefaultPageSize must not exceed MaxPageSize.")
            .Validate(
                o => string.IsNullOrWhiteSpace(o.TrustedCaCertificatePath) || File.Exists(o.TrustedCaCertificatePath),
                "TrustedCaCertificatePath must point to an existing PEM file.")
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<ServiceControlMcpOptions>, AuthOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);

        // The discovery and identity-provider clients carry no ServiceControl credentials (they are what obtains them) but trust the same private CA.
        services.AddHttpClient(DiscoveryClientName).ConfigurePrimaryHttpMessageHandler(CreateTrustedHandler);
        services.AddHttpClient(IdentityClientName).ConfigurePrimaryHttpMessageHandler(CreateTrustedHandler);
        services.TryAddSingleton(sp => new ServiceControlDiscovery(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(DiscoveryClientName), sp.GetRequiredService<IOptions<ServiceControlMcpOptions>>()));
        services.TryAddSingleton(sp => new IdentityProviderClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(IdentityClientName),
            sp.GetRequiredService<ServiceControlDiscovery>(),
            sp.GetRequiredService<IOptions<ServiceControlMcpOptions>>()));
        services.TryAddSingleton<ITokenProvider>(CreateTokenProvider);
        services.TryAddSingleton<ToolAccess>();
        services.AddTransient<BearerTokenHandler>();

        services.AddHttpClient<ServiceControlClient>(ConfigurePrimary)
            .ConfigurePrimaryHttpMessageHandler(CreateTrustedHandler)
            .AddHttpMessageHandler<BearerTokenHandler>();
        services.AddHttpClient<MonitoringClient>(ConfigureMonitoring)
            .ConfigurePrimaryHttpMessageHandler(CreateTrustedHandler)
            .AddHttpMessageHandler<BearerTokenHandler>();

        // Tool registration is decided from configuration now, so unavailable tools are never advertised to the client.
        var options = configuration.Get<ServiceControlMcpOptions>() ?? new ServiceControlMcpOptions();

        var builder = services.AddMcpServer(server => server.ServerInfo = new() { Name = "servicecontrol-mcp", Version = Version });
        builder
            .WithRequestFilters(filters =>
            {
                // Offer only the tools this identity may use, and refuse a call to one it may not (a client can call a tool it was never shown).
                filters.AddListToolsFilter(next => async (context, cancellationToken) =>
                {
                    var result = await next(context, cancellationToken).ConfigureAwait(false);
                    foreach (var tool in result.Tools)
                    {
                        tool.InputSchema = ToolSchemas.WithoutNullableTypes(tool.InputSchema);
                    }

                    var snapshot = await context.Services!.GetRequiredService<ToolAccess>().GetAsync(context.User, cancellationToken).ConfigureAwait(false);
                    if (!snapshot.Restricted)
                    {
                        return result;
                    }

                    result.Tools = [.. result.Tools.Where(tool => ToolAccess.IsAllowed(tool.Name, snapshot))];
                    return result;
                });
                filters.AddCallToolFilter(next => async (context, cancellationToken) =>
                {
                    var name = context.Params?.Name;
                    if (name is not null)
                    {
                        var snapshot = await context.Services!.GetRequiredService<ToolAccess>().GetAsync(context.User, cancellationToken).ConfigureAwait(false);
                        var missing = ToolAccess.MissingRoutes(name, snapshot);
                        if (missing.Count > 0)
                        {
                            return new CallToolResult
                            {
                                IsError = true,
                                Content = [new TextContentBlock { Text = NotPermitted(name, snapshot, missing) }]
                            };
                        }
                    }

                    return await next(context, cancellationToken).ConfigureAwait(false);
                });
            })
            .WithTools<OverviewTools>()
            .WithTools<FailedMessageTools>()
            .WithTools<FailureGroupTools>()
            .WithTools<HealthTools>()
            .WithTools<AuditTools>();

        if (!string.IsNullOrWhiteSpace(options.MonitoringUrl))
        {
            builder.WithTools<MonitoringTools>();
        }

        // Read-only unless writes are explicitly enabled: the state-changing tools are then not even advertised.
        if (options.EnableWrites)
        {
            builder.WithTools<WriteTools>();
        }

        return builder;
    }

    static HttpMessageHandler CreateTrustedHandler(IServiceProvider services) =>
        TlsTrust.CreateHandler(services.GetRequiredService<IOptions<ServiceControlMcpOptions>>().Value.TrustedCaCertificatePath);

    static string Version =>
        typeof(ServiceCollectionExtensions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    const string DiscoveryClientName = "scmcp-discovery";
    const string IdentityClientName = "scmcp-identity";

    static string NotPermitted(string tool, AccessSnapshot snapshot, IReadOnlyList<string> missing) =>
        $"The tool '{tool}' is not available to your ServiceControl identity" +
        (snapshot.Roles.Count > 0 ? $" (roles: {string.Join(", ", snapshot.Roles)})" : " (no recognised ServiceControl role)") +
        $": it needs {string.Join(" and ", missing)}, which your roles do not allow. Reading needs the reader role; retry, archive and dismiss need the writer role.";

    internal static AuthMode ResolveMode(AuthOptions auth) => auth.EffectiveMode;

    /// <summary>A description of what is missing for the resolved mode, or <c>null</c> when the settings are complete.</summary>
    internal static string? AuthProblem(AuthOptions auth) => ResolveMode(auth) switch
    {
        AuthMode.StaticToken when string.IsNullOrWhiteSpace(auth.Token) => "Auth:Mode is StaticToken but Auth:Token is not set.",
        AuthMode.Command when string.IsNullOrWhiteSpace(auth.TokenCommand) => "Auth:Mode is Command but Auth:TokenCommand is not set.",
        AuthMode.ClientCredentials when string.IsNullOrWhiteSpace(auth.ClientId) || string.IsNullOrWhiteSpace(auth.ClientSecret) =>
            "Auth:Mode is ClientCredentials but Auth:ClientId and Auth:ClientSecret are not both set.",
        AuthMode.DeviceCode when string.IsNullOrWhiteSpace(auth.ClientId) => "Auth:Mode is DeviceCode but Auth:ClientId is not set.",
        AuthMode.TokenExchange when string.IsNullOrWhiteSpace(auth.ClientId) || string.IsNullOrWhiteSpace(auth.ClientSecret) =>
            "Auth:Mode is TokenExchange but Auth:ClientId and Auth:ClientSecret (the confidential client that performs the exchange) are not both set.",
        AuthMode.TokenExchange when auth.ExchangeStyle == ExchangeStyle.OnBehalfOf && string.IsNullOrWhiteSpace(auth.Scope) =>
            "Auth:ExchangeStyle is OnBehalfOf but Auth:Scope (for example api://{servicecontrol-app}/.default) is not set.",
        _ => null
    };

    static ITokenProvider CreateTokenProvider(IServiceProvider services)
    {
        var auth = services.GetRequiredService<IOptions<ServiceControlMcpOptions>>().Value.Auth;
        var time = services.GetRequiredService<TimeProvider>();

        return ResolveMode(auth) switch
        {
            AuthMode.StaticToken => new StaticTokenProvider(auth.Token!),
            AuthMode.Command => new CommandTokenProvider(auth.TokenCommand!, auth.TokenCommandArguments, time, services.GetRequiredService<ILogger<CommandTokenProvider>>()),
            AuthMode.ClientCredentials => new ClientCredentialsTokenProvider(services.GetRequiredService<IdentityProviderClient>(), auth, time),
            AuthMode.TokenExchange => new TokenExchangeTokenProvider(
                services.GetRequiredService<IdentityProviderClient>(),
                services.GetRequiredService<ServiceControlDiscovery>(),
                services.GetService<ISubjectTokenAccessor>() ?? throw new InvalidOperationException(
                    "Auth:Mode TokenExchange exchanges each caller's token, so it is only available in the HTTP host, where there are callers."),
                auth,
                time),
            AuthMode.DeviceCode => new DeviceCodeTokenProvider(
                services.GetRequiredService<IdentityProviderClient>(), services.GetRequiredService<ServiceControlDiscovery>(), auth, time,
                services.GetRequiredService<ILogger<DeviceCodeTokenProvider>>()),
            _ => new NoTokenProvider()
        };
    }

    static void ConfigurePrimary(IServiceProvider services, HttpClient client)
    {
        var options = services.GetRequiredService<IOptions<ServiceControlMcpOptions>>().Value;
        Configure(client, options, new Uri(options.Url.TrimEnd('/') + "/api/"));
    }

    static void ConfigureMonitoring(IServiceProvider services, HttpClient client)
    {
        var options = services.GetRequiredService<IOptions<ServiceControlMcpOptions>>().Value;

        // Left unset when no monitoring instance is configured; the monitoring tools are not registered in that case.
        Configure(client, options, string.IsNullOrWhiteSpace(options.MonitoringUrl) ? null : new Uri(options.MonitoringUrl.TrimEnd('/') + "/"));
    }

    static void Configure(HttpClient client, ServiceControlMcpOptions options, Uri? baseAddress)
    {
        client.BaseAddress = baseAddress;
        client.Timeout = options.RequestTimeout;
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Cjoergensen.ServiceControl.Mcp", Version));
    }
}

/// <summary>Reports exactly which authentication setting is missing, so a misconfiguration fails at startup with an actionable message.</summary>
sealed class AuthOptionsValidator : IValidateOptions<ServiceControlMcpOptions>
{
    public ValidateOptionsResult Validate(string? name, ServiceControlMcpOptions options) =>
        ServiceCollectionExtensions.AuthProblem(options.Auth) is { } problem ? ValidateOptionsResult.Fail(problem) : ValidateOptionsResult.Success;
}

