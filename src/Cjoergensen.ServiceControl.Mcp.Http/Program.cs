using System.Net.Http.Json;
using Cjoergensen.ServiceControl.Mcp;
using Cjoergensen.ServiceControl.Mcp.Auth;
using Cjoergensen.ServiceControl.Mcp.Client;
using Cjoergensen.ServiceControl.Mcp.Http;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;

// ServiceControl settings come from a dedicated, prefixed configuration: the web host's own configuration reads every environment variable, and a
// stray "URL" must not be able to redirect this server. Hosting settings (such as ASPNETCORE_URLS) still use the standard names.
var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables(ServiceControlMcpOptions.EnvironmentPrefix)
    .AddCommandLine(args)
    .Build();
var http = configuration.GetSection(HttpHostOptions.SectionName).Get<HttpHostOptions>() ?? new HttpHostOptions();
var serviceControl = configuration.Get<ServiceControlMcpOptions>() ?? new ServiceControlMcpOptions();

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

var mode = serviceControl.Auth.EffectiveMode;
var authority = http.Authority;
if (!http.AllowAnonymous && string.IsNullOrWhiteSpace(authority))
{
    authority = DiscoverAuthority(serviceControl);
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ISubjectTokenAccessor, HttpSubjectTokenAccessor>();
builder.Services.Configure<HostFilteringOptions>(options =>
    options.AllowedHosts = http.AllowedHosts is { Length: > 0 } ? [.. http.AllowedHosts] : ["localhost", "127.0.0.1", "[::1]"]);

var mcp = builder.Services
    .AddServiceControlMcp(configuration)
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithCallAuditLogging();

// When the server reaches ServiceControl as itself, ServiceControl cannot tell callers apart, so this server limits each caller by their own roles.
// With token exchange ServiceControl already sees the caller, so it decides; and an anonymous (local development) server has no callers to limit.
if (http.EnforceCallerRoles ?? (!http.AllowAnonymous && mode != AuthMode.TokenExchange))
{
    mcp.WithCallerRoleEnforcement(http.RolesClaim);
}

if (!http.AllowAnonymous)
{
    var audiences = new[] { http.Audience }.Concat(http.Audiences).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!).ToArray();
    var authorityUri = new Uri(authority!);
    var insecureAuthority = authorityUri.Scheme != Uri.UriSchemeHttps && !authorityUri.IsLoopback && !http.AllowInsecureTransport;
    if (insecureAuthority)
    {
        Fail($"The identity provider authority {authority} is not HTTPS, so its signing keys could be tampered with in transit. Use HTTPS, or set Http:AllowInsecureTransport on a trusted network.");
    }

    builder.Services
        .AddAuthentication(options =>
        {
            options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.RequireHttpsMetadata = authorityUri.Scheme == Uri.UriSchemeHttps;
            options.MapInboundClaims = false; // keep claim names as the identity provider sent them (roles, realm_access, sub ...)
            options.BackchannelHttpHandler = TlsTrust.CreateHandler(serviceControl.TrustedCaCertificatePath);
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidAudiences = audiences,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
        })
        .AddMcp(options =>
        {
            options.ResourceMetadata = new()
            {
                AuthorizationServers = { authority! },
                ScopesSupported = [.. http.Scopes]
            };
            if (!string.IsNullOrWhiteSpace(http.ResourceUrl))
            {
                options.ResourceMetadata.Resource = http.ResourceUrl;
            }
        });
    builder.Services.AddAuthorization();
}

var app = builder.Build();

// Check the configured addresses before listening. app.Urls is empty until the server starts, so read the same settings the host does.
var problems = HostSafety.Problems(http, serviceControl, HostSafety.ConfiguredUrls(builder.Configuration).Concat(app.Urls));
if (problems.Count > 0)
{
    Fail(string.Join(Environment.NewLine, problems));
}

// And check what was actually bound once it has started: whatever mechanism chose the addresses (Kestrel endpoint configuration, for example),
// an unsafe binding must not keep running.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var bound = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
        .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses ?? [];
    var unsafeBindings = HostSafety.Problems(http, serviceControl, bound);
    if (unsafeBindings.Count > 0)
    {
        Fail(string.Join(Environment.NewLine, unsafeBindings));
    }
});

app.UseHostFiltering();
if (!http.AllowAnonymous)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

var endpoint = app.MapMcp(http.Path);
if (!http.AllowAnonymous)
{
    endpoint.RequireAuthorization();
}

// Answers without touching ServiceControl or the identity provider, so it reports only that this process is up.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

await app.RunAsync().ConfigureAwait(false);
return 0;

static void Fail(string message)
{
    Console.Error.WriteLine("servicecontrol-mcp-http cannot start:");
    Console.Error.WriteLine(message);
    Environment.Exit(1);
}

static string? DiscoverAuthority(ServiceControlMcpOptions options)
{
    try
    {
        using var client = new HttpClient(TlsTrust.CreateHandler(options.TrustedCaCertificatePath)) { Timeout = TimeSpan.FromSeconds(20) };
        var found = client.GetFromJsonAsync<AuthConfiguration>(
            options.Url.TrimEnd('/') + "/api/authentication/configuration", ServiceControlClient.JsonOptions).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(found?.Authority))
        {
            Fail($"Http:Authority is not set and ServiceControl at {options.Url} does not advertise one (is authentication enabled?). Set Http:Authority to the identity provider that issues callers' tokens, or Http:AllowAnonymous on loopback for local development.");
        }

        return found?.Authority;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
    {
        Fail($"Http:Authority is not set and it could not be discovered from ServiceControl at {options.Url}: {ex.Message}. Set Http:Authority to the identity provider that issues callers' tokens.");
        return null;
    }
}
