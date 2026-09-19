# ServiceControl MCP server

An **unofficial** [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server for [Particular ServiceControl](https://docs.particular.net/servicecontrol/).
It lets AI agents (Claude, Copilot, custom agents) query and act on message-processing health data - failed messages, heartbeats, custom
checks, sagas and monitoring metrics - without a human clicking through ServicePulse or hand-writing HTTP clients.

> Not affiliated with or endorsed by Particular Software. "ServiceControl", "ServicePulse" and "NServiceBus" are Particular's trademarks.
> This project addresses [Particular/ServiceControl#5908](https://github.com/Particular/ServiceControl/issues/5908).

A typical exchange: *"Which endpoints have failing heartbeats or custom checks right now, and are there related failed messages I should
retry?"* The agent reads the health picture, summarises it, and - only after you confirm - retries the affected messages.

## Contents

- [Current state](#current-state)
- [What it does](#what-it-does)
- [How it is built](#how-it-is-built)
- [Quick start](#quick-start)
- [Tools](#tools)
- [Safe by default](#safe-by-default)
- [Configuration](#configuration)
- [Authentication](#authentication)
- [Running it for a team (Streamable HTTP)](#running-it-for-a-team-streamable-http)
- [Testing](#testing)
- [Roadmap](#roadmap)
- [License and notices](#license-and-notices)

## Current state

**Early, but thoroughly tested.** Milestones 1-5 of the plan are complete; packaging and CI (milestone 6) are not.

| Area | State |
|---|---|
| Read tools (failed messages, groups, endpoints, heartbeats, custom checks, sagas, audit search, health overview) | Done, verified end to end |
| Monitoring tools | Done, verified; marked *experimental* because ServiceControl documents the monitoring API as unstable |
| Write tools (retry, archive, unarchive, dismiss) | Done, verified end to end; off by default |
| Local server (stdio) | Done |
| Shared server (Streamable HTTP, OAuth resource server) | Done |
| Authentication: static token, token command, client credentials, device code, token exchange | Done; verified against Keycloak |
| Private/internal CA trust, role-aware tool visibility | Done, verified |
| Unit and MCP protocol tests | 175, all passing, ~4 s, no Docker |
| End-to-end tests | 48, all passing, ~6.5 min, needs Docker |
| NuGet package / `dotnet tool`, container images, CI | **Not yet** (built from source today) |

**Verified against** ServiceControl **6.21.0**, on macOS with Docker Desktop (arm64), using real ServiceControl containers, a real NServiceBus 10
workload and Keycloak 26.

**Known limitations and unverified areas** - please read these before relying on it:

- **Linux is unverified.** The test suite has only been run on macOS. The default test topology shares a folder between the test process and the
  containers, which may hit file-permission differences on Linux; `SCMCP_IT_TRANSPORT=rabbitmq` is the fallback. CI (which would settle this) does not exist yet.
- **Microsoft Entra ID token exchange** (on-behalf-of) is implemented and unit-tested but has **not** been tried against a real tenant.
- **Identity providers other than Keycloak** are supported through standard OIDC/OAuth flows but have not been tested against Entra ID, Auth0, Okta
  or others (in particular device code, and the `audience` parameter some providers need for client credentials).
- **MCP-client-driven OAuth login** (an MCP client discovering the identity provider from the server's metadata and completing the login itself) is
  verified only up to the metadata and the 401 challenge, not with a real client performing the whole flow.
- **TLS on the HTTP host's own endpoint** is not exercised by the tests (they use plain HTTP on loopback); the settings are documented below.
- **Only ServiceControl 6.21.0 has been tested.** Older versions without `/api/my/routes` (before 6.18) fall back to "show every tool"; ServiceControl
  still enforces its own permissions.
- Retries and archives are **asynchronous** in ServiceControl. The tools report "accepted", not "done", and say how to follow progress.

## What it does

- **Read** - list failed messages (filter by status, endpoint, queue, time window), see every processing attempt with its exception and stack trace,
  group failures by cause, see heartbeat and custom-check health, look up a saga's audit trail, search audited messages, and read live endpoint
  metrics from the monitoring instance.
- **Act** - retry one message, a batch, everything from an endpoint, or a whole failure group; archive and unarchive; dismiss a stale custom check.
  Bulk actions require you to have seen and confirmed the number of messages affected.
- **Understand** - a single `get_health_overview` call answers "is anything wrong right now?" and says which parts it could not load, so
  "unknown" is never mistaken for "healthy".

It is a thin, typed wrapper over ServiceControl's existing HTTP API: no new business logic, and no change to your ServiceControl installation. Only the
primary instance URL is needed for audit data, because the primary instance aggregates its attached audit instances.

## How it is built

C# on .NET 10 with the official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk). One library holds all the logic; two thin hosts
choose a transport, so every tool behaves the same locally and when shared.

```
src/                                            what ships
├── Cjoergensen.ServiceControl.Mcp.Core/        the library
│   ├── Client/    typed HTTP clients for ServiceControl (primary/audit and monitoring), failure mapping, TLS trust
│   ├── Auth/      token providers: static, command, client credentials, device code, token exchange; OIDC discovery
│   ├── Tools/     the MCP tools, tool → route registry, per-caller permissions, caller-role policy
│   └── ServiceControlMcpOptions.cs, ServiceCollectionExtensions.cs, McpServerBuilderExtensions.cs
├── Cjoergensen.ServiceControl.Mcp.Stdio/       local server (stdio); packs as the `servicecontrol-mcp` dotnet tool
└── Cjoergensen.ServiceControl.Mcp.Http/        shared server (ASP.NET Core, Streamable HTTP): JWT resource server,
                                                RFC 9728 metadata, per-caller identity, startup safety checks

tests/
├── Cjoergensen.ServiceControl.Mcp.Core.Tests/            175 unit + MCP protocol tests (fake ServiceControl, in-memory client↔server)
├── Cjoergensen.ServiceControl.Mcp.IntegrationTests/      48 end-to-end tests: real containers, real executables, real MCP clients
├── Cjoergensen.ServiceControl.Mcp.TestWorkload/          real NServiceBus endpoints that produce failures, heartbeats, sagas, metrics
└── Shared/TestPki.cs                                     throwaway certificate authority for TLS tests
```

The NServiceBus dependency exists only in the test workload and never ships with the tool.

## Quick start

Requires the .NET 10 SDK. Build from source:

```bash
dotnet build -c Release
```

Point the local server at your ServiceControl and register it with your MCP client. For Claude Desktop / Claude Code:

```json
{
  "mcpServers": {
    "servicecontrol": {
      "command": "dotnet",
      "args": ["/path/to/src/Cjoergensen.ServiceControl.Mcp.Stdio/bin/Release/net10.0/Cjoergensen.ServiceControl.Mcp.Stdio.dll"],
      "env": {
        "SERVICECONTROL_MCP_Url": "http://localhost:33333",
        "SERVICECONTROL_MCP_MonitoringUrl": "http://localhost:33633"
      }
    }
  }
}
```

That gives a **read-only** server. Add `"SERVICECONTROL_MCP_EnableWrites": "true"` to also offer the retry/archive/dismiss tools. If your ServiceControl
has authentication enabled, see [Authentication](#authentication).

## Tools

| Tool | What it does |
|---|---|
| `get_health_overview` | One-call snapshot: failure counts, largest failure groups, dead heartbeats, failing custom checks, stale monitored endpoints |
| `list_failed_messages` | Failed messages, filterable by status, endpoint, queue, time window; paged |
| `get_failed_message` | Every attempt with exception and stack trace (no body) |
| `get_failed_message_counts` | Counts per status |
| `list_failure_groups` / `list_failure_classifiers` / `list_failure_group_messages` | Failures grouped by cause; retry/archive progress |
| `list_endpoints` / `get_heartbeat_stats` | Endpoints and heartbeat health |
| `list_custom_checks` | Custom check results, defaulting to failures |
| `get_saga_history` | Saga audit trail |
| `search_messages` | Search audited (successfully processed) messages |
| `list_monitored_endpoints` / `get_monitored_endpoint` | Live metrics (experimental; needs `MonitoringUrl`) |
| `retry_failed_message`, `retry_failed_messages`, `retry_endpoint_failures`, `retry_failure_group` | Retry (needs `EnableWrites`) |
| `archive_failed_messages`, `archive_failure_group`, `unarchive_failed_messages` | Archive / restore (needs `EnableWrites`) |
| `dismiss_custom_check` | Remove a custom check entry (needs `EnableWrites`) |

## Safe by default

- **Read-only unless you opt in.** Tools that change state are not even advertised until `EnableWrites` is set.
- **Bulk operations need a confirmed count.** Retrying a whole endpoint or failure group requires `expectedCount`; if the live number differs,
  nothing is sent and the agent is told the current number.
- **Every state-changing tool tells the model to get your explicit confirmation first**, and carries MCP annotations
  (`readOnlyHint`, `destructiveHint`) so clients can prompt as well.
- **Message bodies are never returned.**
- **Results say when they are incomplete.** If an attached audit instance did not answer, the result names it; the health overview reports
  sections that failed to load instead of hiding them.
- **Credentials are never sent over plain HTTP** to a non-loopback host (unless you explicitly allow it) and never logged.
- **Tools follow the caller's permissions.** With role-based authorization enabled, the server asks ServiceControl which routes the signed-in
  identity may call (`GET /api/my/routes`) and offers only the tools that identity can actually use: a reader never sees retry or archive,
  even with `EnableWrites` on, and a call to a hidden tool is refused with an explanation. If that cannot be determined (an older ServiceControl,
  bad credentials) nothing is hidden and ServiceControl's own answer surfaces when a tool is used.
- **ServiceControl remains the authority:** its `reader`/`writer` roles are enforced on every call regardless.

## Configuration

Set environment variables (prefix `SERVICECONTROL_MCP_`) or command-line arguments. Nested names use `__`, for example `SERVICECONTROL_MCP_Auth__ClientId`.

| Setting | Meaning | Default |
|---|---|---|
| `Url` | ServiceControl primary instance, e.g. `https://servicecontrol.example.com` (include a reverse-proxy path prefix if you use one) | `http://localhost:33333` |
| `MonitoringUrl` | Monitoring instance; enables the monitoring tools | not set |
| `EnableWrites` | Enable retry/archive/dismiss tools | `false` |
| `MaxPageSize` / `DefaultPageSize` | Result page limits | `50` / `25` |
| `MaxBatchSize` | Most ids per batch call | `100` |
| `RequestTimeout` | Per-request timeout | `00:00:30` |
| `AllowInsecureTransport` | Allow sending credentials over plain HTTP to a non-loopback host | `false` |
| `TrustedCaCertificatePath` | PEM file with private/internal CA certificates to trust for ServiceControl and the identity provider. Validation stays on; this only adds roots | not set |
| `Auth__Mode` | `Auto` (default), `None`, `StaticToken`, `Command`, `ClientCredentials`, `DeviceCode`, `TokenExchange` (HTTP host only) | `Auto` |
| `Auth__Token` | Bearer token (short-lived; for development) | not set |
| `Auth__TokenCommand`, `Auth__TokenCommandArguments__0..n` | A command that prints a token, e.g. `az account get-access-token ...`; run directly, never through a shell; refreshed automatically | not set |
| `Auth__ClientId`, `Auth__ClientSecret` | OAuth client for client credentials (with a secret), device code (without), or token exchange | not set |
| `Auth__Authority` | OIDC authority to sign in against; defaults to the one ServiceControl advertises | from ServiceControl |
| `Auth__Scope`, `Auth__Audience` | Scope (or `audience` parameter, for providers such as Auth0) to request; defaults follow the mode | see below |
| `Auth__ExchangeStyle` | `Standard` (RFC 8693) or `OnBehalfOf` (Microsoft Entra ID), for token exchange | `Standard` |

## Authentication

ServiceControl authenticates with standard OIDC JWT bearer tokens (any OIDC-compliant provider) and has no machine-to-machine mechanism of
its own, so this server needs a token for the audience your instance expects. If ServiceControl authentication is disabled (the default) no
credentials are needed. Otherwise pick how the server gets one; `Auto` chooses from what you configure, most specific first:

| You configure | Mode | What happens |
|---|---|---|
| `Auth__Token` | static token | Uses the token as is. Short-lived; for development and CI. |
| `Auth__TokenCommand` | token command | Runs your command (for example the Azure CLI) whenever a token is needed, caching until it expires. Reuses a login you already have; needs nothing registered for this server at the identity provider. |
| `Auth__ClientId` + `Auth__ClientSecret` | client credentials | The server signs in as itself. Suits shared or unattended use. ServiceControl sees the roles granted to that client, not to a person. |
| `Auth__ClientId` only | device code | A person signs in with their own browser, so ServiceControl sees *their* roles and audit trail. See below. |

**Device code.** A stdio server cannot reliably show a prompt, so it never blocks: the first tool call fails with a message such as
"Sign-in is required. Ask the user to open https://idp.example/device?user_code=ABCD-EFGH ...", which the agent relays to you. Once you approve, repeat
the request and it succeeds; a refresh token (when the provider issues one) keeps the session going. The same instructions are written to the
server's log (stderr). The client must be a *public* client with the device authorization grant enabled. By default it asks for the scopes
ServiceControl advertises for its own sign-in; if the provider refuses long-lived (`offline_access`) tokens the server retries without it.

The authority, and the scopes for device code, are read from ServiceControl's anonymous `GET /api/authentication/configuration`, so a typical setup
needs only the ServiceControl URL and a client. Client secrets belong in the environment, never on a command line.

### Private certificate authorities

A ServiceControl (or identity provider) behind a private or self-signed CA is normal. Point `TrustedCaCertificatePath` at a PEM file with the CA
certificate(s). Certificates are still fully validated (chain, dates, host name); the file only adds trusted roots. Without it, such a server is
rejected with a message explaining the certificate problem.

## Running it for a team (Streamable HTTP)

The stdio server runs on your own machine as you. For one server that several people or agents share, run the HTTP host
(`src/Cjoergensen.ServiceControl.Mcp.Http`). It is an OAuth 2.0 *resource server*, as the MCP specification requires: callers present a bearer token
issued **for this server**, it publishes its protected resource metadata (RFC 9728) so MCP clients can find the identity provider by themselves, and
it validates every token (issuer, signature, lifetime, audience).

**It never forwards a caller's token.** A token issued for the MCP server is not one ServiceControl should see, and the MCP specification forbids
passing it on ("token passthrough"). There are two ways for the server to reach ServiceControl instead:

| | Service principal | Token exchange |
|---|---|---|
| How | The server signs in as itself (`Auth__ClientId` + `Auth__ClientSecret`, or a token, or a command) | The server exchanges each caller's token at the identity provider for one for ServiceControl (`Auth__Mode=TokenExchange`) |
| ServiceControl sees | The service principal | The caller: their roles, their name in ServiceControl's audit log |
| Who limits what a caller may do | **This server**, from the roles in the caller's own token (`Http__RolesClaim`); a caller with no reader/writer role gets nothing | ServiceControl, as for any user; the tools offered are what that person may use |
| Works with | Any OIDC provider | Providers with token exchange: Keycloak (RFC 8693, the default style) and Microsoft Entra ID (on-behalf-of, with `Auth__Scope`; not yet tested against a real tenant) |

Because a service principal hides the caller from ServiceControl, the server keeps its own record: every state-changing call is logged as
`AUDIT change: caller alice (via mcp-cli) called retry_failed_message -> ok`, and reads at debug level.

Minimal configuration (environment variables, prefix `SERVICECONTROL_MCP_`):

| Setting | Meaning |
|---|---|
| `Http__Authority` | The identity provider that issues callers' tokens. Defaults to the authority ServiceControl advertises |
| `Http__Audience` | The audience callers' tokens must be issued for: this server. **Required.** Tokens for anything else (notably ServiceControl itself) are refused |
| `Http__RolesClaim` | Where the caller's roles are: a claim name or dotted path (`realm_access.roles`, `roles`) |
| `Http__EnforceCallerRoles` | Whether this server limits callers by role. Default: yes, except with token exchange |
| `Http__ResourceUrl`, `Http__Scopes__0..n` | Advertised in the protected resource metadata |
| `Http__AllowedHosts__0..n` | Host names the server answers to. Required beyond loopback (guards against DNS rebinding) |
| `Http__AllowInsecureTransport` | Permit plain HTTP beyond loopback, only when TLS is terminated in front of the server |
| `Http__AllowAnonymous` | No authentication. Local development only: refused unless the server listens on loopback |
| `Http__Path` | The MCP endpoint path (default `/mcp`); `/healthz` answers without touching ServiceControl |
| `ASPNETCORE_URLS`, `ASPNETCORE_Kestrel__Certificates__Default__Path` (and `__Password`) | The standard ASP.NET Core address and TLS certificate settings |

All the ServiceControl settings above (`Url`, `MonitoringUrl`, `EnableWrites`, `TrustedCaCertificatePath`, `Auth__*`) apply unchanged.

```bash
export SERVICECONTROL_MCP_Url=https://servicecontrol.example.com
export SERVICECONTROL_MCP_EnableWrites=true
export SERVICECONTROL_MCP_Http__Audience=api://servicecontrol-mcp
export SERVICECONTROL_MCP_Http__RolesClaim=roles
export SERVICECONTROL_MCP_Auth__Mode=TokenExchange
export SERVICECONTROL_MCP_Auth__ClientId=servicecontrol-mcp
export SERVICECONTROL_MCP_Auth__ClientSecret=...        # from your secret store, never a command line
export ASPNETCORE_URLS=https://0.0.0.0:8443
export SERVICECONTROL_MCP_Http__AllowedHosts__0=mcp.example.com
dotnet src/Cjoergensen.ServiceControl.Mcp.Http/bin/Release/net10.0/Cjoergensen.ServiceControl.Mcp.Http.dll
```

**The server refuses to start** when configured unsafely, and says how to fix it: anonymous access beyond loopback, no audience, bearer tokens over
plain HTTP beyond loopback, a non-loopback address with no allowed host names, or token exchange without authentication. The checks read both the
configured and the actually bound addresses.

**Identity provider checklist.** (1) Register this server as an API/resource and use its identifier as `Http__Audience`; MCP clients discover the
authorization server from the metadata and request tokens for it. (2) Make sure callers' tokens carry their roles (`reader`, `writer`, `admin`).
(3) For token exchange, register the server as a confidential client that is allowed to exchange tokens for ServiceControl's audience (on Keycloak: enable
standard token exchange on the client and add an audience mapper for ServiceControl's client; the integration test realm is a working example in
`tests/Cjoergensen.ServiceControl.Mcp.IntegrationTests/Platform/KeycloakRealm.cs`).

## Testing

```bash
dotnet test --project tests/Cjoergensen.ServiceControl.Mcp.Core.Tests          # 175 unit + MCP protocol tests, no Docker
dotnet test --project tests/Cjoergensen.ServiceControl.Mcp.IntegrationTests    # 48 end-to-end tests, needs Docker
```

The integration suite starts a real ServiceControl platform (error, audit and monitoring instances and RavenDB) in containers, runs real NServiceBus
endpoints against it (failing handlers, heartbeats, custom checks, sagas, audit, metrics), and drives the real `servicecontrol-mcp` and HTTP host
executables with real MCP clients. It pins ServiceControl 6.21.0; set `SCMCP_IT_SC_TAG` to test another version.

By default the platform uses ServiceControl's file-system learning transport (non-production): no broker, just a folder shared between the
containers and the test process. That is the simplest and fastest topology, and the transport is not what these tests exercise. Set
`SCMCP_IT_TRANSPORT=rabbitmq` to use a RabbitMQ broker instead, as in a typical production deployment. To iterate against a platform that is already
running, set `SCMCP_IT_URL`, `SCMCP_IT_RABBITMQ` (broker connection string reachable from your machine), and optionally `SCMCP_IT_MONITORING_URL`
and `SCMCP_IT_RABBITMQ_MANAGEMENT`.

**A second, secured platform** is tested the same way: a generated private CA, HTTPS on every instance, Keycloak as the identity provider, and
role-based authorization with reader and writer users and bots. Those tests cover:

- no credentials, an untrusted CA, and bad tokens;
- reader versus writer tools, client credentials, a token command, and the token reaching the audit and monitoring instances;
- a complete device-code sign-in (the test plays the person in the browser by submitting Keycloak's login and consent forms);
- the HTTP host in both modes: challenge and metadata, rejection of a token meant for ServiceControl, role enforcement, token exchange with Keycloak,
  and 24 overlapping calls from two people that must never see each other's permissions.

The identity provider must be reachable at the same `https://localhost:PORT` from inside the containers and from your machine, so the ServiceControl
containers share Keycloak's network namespace. Keycloak's port defaults to 18443 (`SCMCP_IT_KEYCLOAK_PORT` to change it).

ServiceControl containers run without a license file by falling back to an automatic trial. Whether that suits your use is between you and
Particular ([licensing](https://particular.net/licensing)).

## Roadmap

Milestone 6 - packaging and polish, not started:

- publish the stdio host as a `dotnet tool` and publish container images for both hosts;
- GitHub Actions CI (build, unit tests, the end-to-end suite), which also settles the Linux question;
- versioning and a changelog;
- verify against more ServiceControl versions and identity providers (Entra ID first).

## License and notices

MIT - see [LICENSE](LICENSE).

The NServiceBus packages used by the test workload (`tests/Cjoergensen.ServiceControl.Mcp.TestWorkload`) are under NServiceBus's own license terms;
they are used only for tests and are never shipped with the tool.
