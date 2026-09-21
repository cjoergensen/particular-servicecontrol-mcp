namespace Cjoergensen.ServiceControl.Mcp.Http;

/// <summary>Settings specific to the HTTP host, under the <c>Http</c> section (for example <c>SERVICECONTROL_MCP_Http__Authority</c>).</summary>
public sealed class HttpHostOptions
{
    public const string SectionName = "Http";

    /// <summary>
    /// The OpenID Connect authority that issues the tokens callers present to this server. Defaults to the authority ServiceControl advertises, which
    /// is right when one identity provider serves both.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    /// The audience callers' tokens must be issued for: this server. Tokens issued for anything else, notably for ServiceControl itself, are rejected,
    /// which is what stops a token meant for another service being replayed here (and this server replaying one onward).
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>Further accepted audiences, for identity providers that name the same resource in more than one way.</summary>
    public string[] Audiences { get; set; } = [];

    /// <summary>The canonical URL of this server, advertised as the <c>resource</c> in the protected resource metadata. Defaults to the request's own address.</summary>
    public string? ResourceUrl { get; set; }

    /// <summary>Scopes advertised in the protected resource metadata, when the identity provider uses them for this server.</summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>Where the caller's roles are in their token: a claim name or a dotted path such as <c>realm_access.roles</c>.</summary>
    public string RolesClaim { get; set; } = "roles";

    /// <summary>
    /// Whether the server itself limits callers to the tools their roles allow. Defaults to on, except with token exchange, where ServiceControl already
    /// applies each caller's own roles. Turn it off only if every authenticated caller should get everything ServiceControl's identity allows.
    /// </summary>
    public bool? EnforceCallerRoles { get; set; }

    /// <summary>Serve without authentication. Refused unless the server listens only on loopback: for local development.</summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>Allow bearer tokens over plain HTTP on a non-loopback address. Only for a network where TLS is terminated in front of this server.</summary>
    public bool AllowInsecureTransport { get; set; }

    /// <summary>The host names this server answers to. Required when listening on a non-loopback address; it guards against DNS rebinding.</summary>
    public string[]? AllowedHosts { get; set; }

    /// <summary>The path of the MCP endpoint.</summary>
    public string Path { get; set; } = "/mcp";
}
