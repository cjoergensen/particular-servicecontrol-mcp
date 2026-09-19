using System.ComponentModel.DataAnnotations;

namespace Cjoergensen.ServiceControl.Mcp;

/// <summary>
/// Configuration for the MCP server. Bound from the configuration root, so it can be supplied as
/// <c>SERVICECONTROL_MCP_Url</c> style environment variables, command-line arguments or appsettings.
/// </summary>
public sealed class ServiceControlMcpOptions
{
    public const string EnvironmentPrefix = "SERVICECONTROL_MCP_";

    /// <summary>Base URL of the ServiceControl primary (error) instance, for example <c>http://localhost:33333</c>.</summary>
    /// <remarks>
    /// The API lives under <c>/api</c>. A reverse-proxy path prefix (ServiceControl's VirtualDirectory) is
    /// supported: include it in the URL. The primary instance aggregates data from the audit instances
    /// attached to it, so audit instances need no separate URL.
    /// </remarks>
    [Required, Url]
    public string Url { get; set; } = "http://localhost:33333";

    /// <summary>Base URL of the ServiceControl monitoring instance. The monitoring tools are only offered when this is set.</summary>
    [Url]
    public string? MonitoringUrl { get; set; }

    /// <summary>
    /// Registers the tools that change state (retry, archive, dismiss). Off by default: the server is read-only
    /// unless this is explicitly enabled.
    /// </summary>
    public bool EnableWrites { get; set; }

    /// <summary>Most message ids a single retry, archive or unarchive call may name. Larger jobs should use a group or endpoint operation.</summary>
    [Range(1, 1000)]
    public int MaxBatchSize { get; set; } = 100;

    /// <summary>Largest page size a tool will request. Keeps responses small enough for an LLM context.</summary>
    [Range(1, 500)]
    public int MaxPageSize { get; set; } = 50;

    /// <summary>Page size used when a tool call does not specify one.</summary>
    [Range(1, 500)]
    public int DefaultPageSize { get; set; } = 25;

    /// <summary>Timeout for a single request to ServiceControl.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Permit sending a bearer token over plain HTTP to a non-loopback host. Off by default: tokens are only sent
    /// over HTTPS (or to a loopback address for local development).
    /// </summary>
    public bool AllowInsecureTransport { get; set; }

    /// <summary>
    /// Path to a PEM file with one or more certificate authority certificates to trust in addition to the operating system's, for a
    /// ServiceControl (or identity provider) that uses a private or self-signed CA. Certificates are still fully validated; this only
    /// widens who is trusted. Certificate validation is never turned off.
    /// </summary>
    public string? TrustedCaCertificatePath { get; set; }

    /// <summary>Credentials used to call ServiceControl.</summary>
    public AuthOptions Auth { get; set; } = new();
}

/// <summary>How the server obtains a token for ServiceControl. See <see cref="AuthMode"/>.</summary>
public sealed class AuthOptions
{
    public AuthMode Mode { get; set; } = AuthMode.Auto;

    /// <summary>A ready-made bearer token, for <see cref="AuthMode.StaticToken"/>. Short-lived; intended for development and CI.</summary>
    public string? Token { get; set; }

    /// <summary>
    /// Executable that prints a bearer token on stdout, for <see cref="AuthMode.Command"/>, for example <c>az</c> with
    /// <c>account get-access-token --resource api://... --query accessToken -o tsv</c>. Run directly, never through a shell.
    /// </summary>
    public string? TokenCommand { get; set; }

    /// <summary>Arguments passed to <see cref="TokenCommand"/>.</summary>
    public string[] TokenCommandArguments { get; set; } = [];

    /// <summary>
    /// The OpenID Connect authority (issuer URL) to get tokens from. Defaults to the authority ServiceControl advertises. Set it when the
    /// advertised one is not reachable from where this server runs, for example behind a proxy.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>The OAuth client registered for this server at the identity provider, for <see cref="AuthMode.ClientCredentials"/> and <see cref="AuthMode.DeviceCode"/>.</summary>
    public string? ClientId { get; set; }

    /// <summary>The client secret, for <see cref="AuthMode.ClientCredentials"/>. Treat as a secret: supply it through the environment, never a command line.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Scope to request. What is right depends on the identity provider (for example <c>api://{app}/.default</c> on Microsoft Entra ID). When unset,
    /// client credentials request no scope and device code uses the scopes ServiceControl advertises for its own sign-in.
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>
    /// The <c>audience</c> to request. For client credentials it is sent as a request parameter when set, which some providers (for example Auth0)
    /// require. For <see cref="AuthMode.TokenExchange"/> it names the API the exchanged token is for, defaulting to the audience ServiceControl advertises.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>The token exchange protocol, for <see cref="AuthMode.TokenExchange"/>.</summary>
    public ExchangeStyle ExchangeStyle { get; set; } = ExchangeStyle.Standard;

    /// <summary>The mode actually in effect: <see cref="Mode"/>, or for <see cref="AuthMode.Auto"/> what the configured settings imply.</summary>
    public AuthMode EffectiveMode
    {
        get
        {
            if (Mode != AuthMode.Auto)
            {
                return Mode;
            }

            return !string.IsNullOrWhiteSpace(Token) ? AuthMode.StaticToken
                : !string.IsNullOrWhiteSpace(TokenCommand) ? AuthMode.Command
                : !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) ? AuthMode.ClientCredentials
                : !string.IsNullOrWhiteSpace(ClientId) ? AuthMode.DeviceCode
                : AuthMode.None;
        }
    }
}

public enum ExchangeStyle
{
    /// <summary>OAuth 2.0 Token Exchange (RFC 8693), supported by Keycloak and others.</summary>
    Standard,

    /// <summary>The Microsoft identity platform's on-behalf-of flow (Microsoft Entra ID). Needs <see cref="AuthOptions.Scope"/>.</summary>
    OnBehalfOf
}

public enum AuthMode
{
    /// <summary>
    /// Pick from what is configured: a token, else a token command, else a client id with a secret (client credentials), else a client id
    /// alone (device code), else no credentials.
    /// </summary>
    Auto,

    /// <summary>No credentials. Correct when ServiceControl authentication is disabled.</summary>
    None,

    /// <summary>Use <see cref="AuthOptions.Token"/>.</summary>
    StaticToken,

    /// <summary>Run <see cref="AuthOptions.TokenCommand"/> to obtain (and refresh) a token.</summary>
    Command,

    /// <summary>
    /// OAuth client credentials: this server signs in as itself with <see cref="AuthOptions.ClientId"/> and <see cref="AuthOptions.ClientSecret"/>.
    /// Suits shared or unattended use; ServiceControl sees the roles granted to that client, not to a person.
    /// </summary>
    ClientCredentials,

    /// <summary>
    /// OAuth device authorization: a person signs in in their browser with a short code, so ServiceControl sees their own roles. Needs a public
    /// client with the device grant enabled.
    /// </summary>
    DeviceCode,

    /// <summary>
    /// For the HTTP host only. Each caller's own token (issued for this server) is exchanged at the identity provider for a token for ServiceControl, so
    /// ServiceControl sees that person: their roles, their audit trail. The caller's token is never forwarded. Needs a confidential client
    /// (<see cref="AuthOptions.ClientId"/> and <see cref="AuthOptions.ClientSecret"/>) allowed to perform the exchange. Never chosen automatically.
    /// </summary>
    TokenExchange
}
