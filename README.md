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

**If your ServiceControl has authentication turned off (its default), you need no credentials. Skip this section.**

Otherwise, keep one idea in mind: there are **two separate hops**, and they are configured separately.

```
 MCP client  ───────────────▶  this MCP server  ───────────────▶  ServiceControl
 (Claude, Copilot ...)   hop 1                    hop 2
                         "may this caller use     "which identity does this server
                          the MCP server?"         present to ServiceControl?"
```

| | Hop 1: caller → this server | Hop 2: this server → ServiceControl |
|---|---|---|
| **Local server (stdio)** | **Nothing to configure.** Your MCP client starts the server as a child process on your machine, as you. There is no network hop to protect. | `Auth__*` settings |
| **Shared server (HTTP)** | `Http__*` settings: callers present a token issued *for this server* | `Auth__*` settings |

Everything called `Auth__…` is about hop 2. Everything called `Http__…` is about hop 1 and exists only in the HTTP host.

### How this compares with ServicePulse

ServicePulse (see its [authentication docs](https://docs.particular.net/servicepulse/security/configuration/authentication)) is a browser app. It reads the
sign-in details ServiceControl publishes, redirects you to the identity provider (authorization code flow with PKCE), you sign in, and
ServicePulse calls ServiceControl with a token that is *yours*. This server cannot show a browser page on its own, so it offers the closest
equivalent for each way of running it:

| | ServicePulse-like behaviour (ServiceControl sees *you*) | Available today |
|---|---|---|
| Local (stdio) | **Device code**: the agent gives you a link and a code, you sign in in your browser, ServiceControl sees you. It is a code, not a redirect, and you must register a client at your identity provider for it. | Yes, [device code](#device-code-a-person-signs-in) |
| Shared (HTTP) | **Token exchange**: your MCP client signs you in through your identity provider's browser page and the server swaps your token for a ServiceControl one. This is the same "you sign in, ServiceControl sees you" result. | Yes, [token exchange](#shared-server-http-both-hops); the client-driven browser login has only been verified up to the 401 challenge, not end to end with a real client |
| Either | A plain browser login (redirect, no code to type) for the local server | **No**, not implemented |

### Step 1: find out what your ServiceControl expects

ServiceControl publishes its sign-in details anonymously:

```bash
curl -s https://servicecontrol.example.com/api/authentication/configuration
```

```json
{ "enabled": true, "authority": "https://idp.example/realms/platform", "audience": "api://servicecontrol",
  "role_based_authorization_enabled": true, "client_id": "servicepulse", "scopes": "openid profile offline_access", "api_scopes": "..." }
```

- `"enabled": false` - nothing more to do.
- `authority` - the identity provider ServiceControl trusts. This server uses it automatically; you only set `Auth__Authority` if it is not reachable from where this server runs.
- `audience` - every token sent to ServiceControl must be issued for this value.
- `role_based_authorization_enabled` - when true, the identity needs the `reader` role to read, and `writer` to retry, archive or dismiss.
- `client_id` is **ServicePulse's** client, registered as a single-page app with ServicePulse's own redirect URIs. This server cannot reuse it; the modes that need a client use one you register for this server.

### Step 2: choose how this server signs in to ServiceControl (hop 2)

Pick the row that matches your situation. `Auth__Mode` is normally left on `Auto`, which chooses from what you set, most specific first
(token, then token command, then client id + secret, then client id alone).

| I want to... | Mode | ServiceControl sees | You set |
|---|---|---|---|
| Try it, or run in CI, with a token I already have | static token | whoever the token belongs to | `Auth__Token` |
| Reuse a login I already have (`az login`, another CLI) | token command | you | `Auth__TokenCommand` (+ arguments) |
| Have each person sign in as themselves (local server) | device code | that person | `Auth__ClientId` |
| Run unattended, or one shared identity for everyone | client credentials | the server's own client (a "service principal") | `Auth__ClientId` + `Auth__ClientSecret` |
| Shared HTTP server, ServiceControl should see each caller | token exchange | each caller | `Auth__Mode=TokenExchange` + client id and secret |

Whatever you choose, the token that reaches ServiceControl must come from the identity provider in `authority`, be issued for `audience`,
and carry the role(s) above. Most "401" and "403" problems are one of those three.

#### Static token

For development and CI. Short-lived, so it stops working when it expires.

```json
"env": { "SERVICECONTROL_MCP_Url": "https://servicecontrol.example.com", "SERVICECONTROL_MCP_Auth__Token": "eyJhbGciOi..." }
```

#### Token command

The server runs your command whenever it needs a token, caches the result until it expires, and runs it again afterwards. Nothing needs to be
registered for this server at the identity provider, because it borrows a login you already have. The command is run directly, never through a shell,
and each argument is its own numbered setting.

```json
"env": {
  "SERVICECONTROL_MCP_Url": "https://servicecontrol.example.com",
  "SERVICECONTROL_MCP_Auth__TokenCommand": "az",
  "SERVICECONTROL_MCP_Auth__TokenCommandArguments__0": "account",
  "SERVICECONTROL_MCP_Auth__TokenCommandArguments__1": "get-access-token",
  "SERVICECONTROL_MCP_Auth__TokenCommandArguments__2": "--resource",
  "SERVICECONTROL_MCP_Auth__TokenCommandArguments__3": "api://servicecontrol",
  "SERVICECONTROL_MCP_Auth__TokenCommandArguments__4": "--query",
  "SERVICECONTROL_MCP_Auth__TokenCommandArguments__5": "accessToken",
  "SERVICECONTROL_MCP_Auth__TokenCommandArguments__6": "-o",
  "SERVICECONTROL_MCP_Auth__TokenCommandArguments__7": "tsv"
}
```

#### Device code (a person signs in)

At the identity provider, register a **public** client for this server with the **device authorization grant** enabled. Then set only its id:

```json
"env": { "SERVICECONTROL_MCP_Url": "https://servicecontrol.example.com", "SERVICECONTROL_MCP_Auth__ClientId": "servicecontrol-mcp" }
```

How it feels: your first request fails with a message like *"Sign-in is required. Ask the user to open https://idp.example/device?user_code=ABCD-EFGH ..."*
and the agent relays it to you. Open the link, sign in, then ask again. It works from then on, and a refresh token (if your provider issues one) keeps the
session going. The server never blocks waiting for you, because a stdio server has no reliable way to show a prompt. The same instructions go to the
server's log (stderr).

It asks for the scopes ServiceControl advertises for its own sign-in. If the provider refuses long-lived (`offline_access`) tokens, it retries without them.
**Use this with the local server only.** In the shared HTTP host there is one server-wide session, so one person's sign-in would be used for everyone;
use token exchange there.

#### Client credentials (the server signs in as itself)

Register a **confidential** client, give its service account the `reader` (and, for write tools, `writer`) role, and make sure its tokens carry
ServiceControl's `audience`.

```json
"env": {
  "SERVICECONTROL_MCP_Url": "https://servicecontrol.example.com",
  "SERVICECONTROL_MCP_Auth__ClientId": "servicecontrol-mcp",
  "SERVICECONTROL_MCP_Auth__ClientSecret": "..."
}
```

Providers differ in how a token gets the right audience: Keycloak uses an audience mapper on the client; Microsoft Entra ID wants
`Auth__Scope=api://{servicecontrol-app}/.default`; Auth0 wants `Auth__Audience`. Only Keycloak has been tested. The secret belongs in the environment or a secret store, never on a command line.

### Shared server (HTTP): both hops

Run the HTTP host (`src/Cjoergensen.ServiceControl.Mcp.Http`) when several people or agents share one server. Now both hops exist.

#### The recommended setup, in plain words

This is the setup where ServiceControl sees each *person*, as it does with ServicePulse.

1. **ServiceControl is set up for authentication as usual.** People hold ServiceControl's own roles (`reader`, `writer`) there, exactly as for ServicePulse. Nothing about that changes.
2. **This MCP server gets its own app registration** at the identity provider. It is *not* a second set of roles. It does two jobs: it is the "audience" a person's
   sign-in is issued for, and it is what the server uses to swap that sign-in for a ServiceControl one (the token exchange).
3. **People sign in through their MCP client** (Claude Code, Claude Desktop, Copilot ...). The server has no login page of its own:
   1. You add the server's address to your MCP client, for example `https://mcp.example.com/mcp`.
   2. The client calls it with no token and gets `401`. The reply points to the server's metadata, which names your identity provider.
   3. The client opens your **browser** on the normal company login page (authorization code with PKCE, like ServicePulse). You sign in, MFA included.
   4. The identity provider gives the client a token *for the MCP server*. The client stores and renews it; you sign in again only when it lapses.
   5. Every tool call carries that token.
4. **The server swaps it** at the identity provider, using its own app registration, for a token *for ServiceControl* that still names you and carries your roles.
   ServiceControl checks your roles and logs your name.

```
you ─ browser sign-in ─▶ identity provider ─ token for the MCP server ─▶ MCP client ─▶ MCP server
                                                                                          │ swap (own app registration)
                                                                                          ▼
                                            ServiceControl ◀─ token for ServiceControl, still you ─ identity provider
```

The server never passes your MCP-server token on, and a token issued for ServiceControl itself is refused by the MCP server.

Two things to know:
- Whether **your MCP client** supports this browser sign-in varies, and the flow has been verified only as far as the `401` and the metadata, not with a real client
  completing a login. If a client cannot do it, the fallback for development is pasting a short-lived token into the client's header settings.
- Some identity providers do not let a client register itself on the fly (Microsoft Entra ID is one). Then you register a client for your MCP clients and
  give its id to the MCP client. The Entra example below shows this.

The next two subsections give the detail behind the same idea.

#### The two hops in detail

**Hop 1, callers to this server.** The HTTP host is an OAuth 2.0 *resource server*, as the MCP specification requires. Callers present a bearer token
issued **for this server** (`Http__Audience`), and the server checks issuer, signature, lifetime and audience on every request. It also publishes its
protected resource metadata (RFC 9728), so an MCP client can find your identity provider and start the browser sign-in by itself. A token issued for
ServiceControl itself is refused here.

**Hop 2, this server to ServiceControl.** The server **never forwards the caller's token**: it was issued for the MCP server, and the MCP specification
forbids passing it on ("token passthrough"). Instead it does one of two things:

| | Service principal | Token exchange |
|---|---|---|
| How | The server signs in as itself: `Auth__ClientId` + `Auth__ClientSecret` (or a token, or a command) | The server swaps each caller's token at the identity provider for one for ServiceControl: `Auth__Mode=TokenExchange` |
| ServiceControl sees | The service principal, for every caller | The caller: their roles, their name in ServiceControl's audit log |
| Who limits what a caller may do | **This server**, from the roles in the caller's own token (`Http__RolesClaim`); a caller with no `reader`/`writer` role gets nothing | ServiceControl, as for any user; the tools offered are what that person may use |
| Works with | Any OIDC provider | Providers with token exchange: Keycloak (RFC 8693, the default `Standard` style) and Microsoft Entra ID (on-behalf-of, with `Auth__Scope`; not yet tested against a real tenant) |
| Choose it when | You want simple setup and a shared identity is acceptable | ServiceControl's audit log must show who did what (**this is the ServicePulse-like choice**) |

Because a service principal hides the caller from ServiceControl, the server keeps its own record: every state-changing call is logged as
`AUDIT change: caller alice (via mcp-cli) called retry_failed_message -> ok`, and reads at debug level.

The settings, an example, the startup safety checks, identity provider setup (Keycloak and Microsoft Entra ID) and verification steps are in
[Running it for a team](#running-it-for-a-team-streamable-http).

### When something goes wrong

| What you see | Meaning | Fix |
|---|---|---|
| `ServiceControl rejected the credentials (401 Unauthorized)` | The token is missing, expired, or not for ServiceControl's `audience` | Check `audience` in Step 1; check `Auth__*` gives a token for it. With `Auth__Mode` unset and nothing configured, no token is sent at all |
| `ServiceControl denied the request (403 Forbidden)` | Signed in, but the identity lacks the role | Give the person or client `reader` (and `writer` for write tools). With a service principal, the roles are the client's |
| `Sign-in is required. Ask the user to open ...` | Device code is waiting for you | Open the link, sign in, ask again |
| `Could not obtain a token for ServiceControl` | The identity provider refused the request | The message includes its reason: usually a wrong client id/secret, a client without the device grant, or a missing `Auth__Scope` |
| Retry/archive tools are missing | Not a bug: writes are off, or your role is `reader` | Set `EnableWrites`, and use an identity with `writer` |
| HTTP host refuses to start | An unsafe configuration | The message says which setting to fix |
| A certificate error | ServiceControl or the identity provider uses a private CA | See [Private certificate authorities](#private-certificate-authorities) |

### Private certificate authorities

A ServiceControl (or identity provider) behind a private or self-signed CA is normal. Point `TrustedCaCertificatePath` at a PEM file with the CA
certificate(s). Certificates are still fully validated (chain, dates, host name); the file only adds trusted roots. Without it, such a server is
rejected with a message explaining the certificate problem.

## Running it for a team (Streamable HTTP)

The stdio server runs on your own machine as you. For one server that several people or agents share, run the HTTP host
(`src/Cjoergensen.ServiceControl.Mcp.Http`). Read [Authentication](#authentication) first: it explains the two hops (caller → this server, and
this server → ServiceControl) and how to choose between *service principal* and *token exchange*. This section is the settings reference and a worked example.

Hop 1 settings (`Http__*`) and hop 2 settings (`Auth__*`) are separate. Minimal configuration (environment variables, prefix `SERVICECONTROL_MCP_`):

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

### Setting up the identity provider

You need three registrations for the token-exchange setup, whichever provider you use:

| Registration | What it is | Why |
|---|---|---|
| **ServiceControl** | The audience and roles you already have for ServiceControl and ServicePulse | The final token must be issued for it and carry `reader`/`writer` |
| **The MCP server** | A *confidential* client (has a secret) that is also the audience callers' tokens are issued for | Callers' tokens are for it, and it performs the swap. Its id is `Http__Audience` and `Auth__ClientId` |
| **A client for people's MCP clients** | A *public* client that can do a browser sign-in with PKCE and get tokens for the MCP server | It starts the login. Some MCP clients register themselves; others need this client's id |

**Keycloak** (tested). Enable *standard token exchange* on the MCP server's client, and add an audience mapper to it for ServiceControl's client (otherwise
Keycloak answers "Requested audience not available"). Keycloak requires the requesting client to be the subject token's audience, so the MCP server's client id is
also its `Http__Audience`. The people's client gets an audience mapper for the MCP server's client id. A complete working example is
[`KeycloakRealm.cs`](tests/Cjoergensen.ServiceControl.Mcp.IntegrationTests/Platform/KeycloakRealm.cs), and the settings are the example above with
`Http__RolesClaim=realm_access.roles`.

#### Example: Microsoft Entra ID

> **Not yet tested against a real tenant.** The on-behalf-of exchange is implemented and unit-tested, but this walkthrough has not been run end to end.
> Expect to adjust it, and please report what you find.

`{tenant}` is your directory (tenant) id. `{sc-app}` is the ServiceControl app registration's application (client) id, `{mcp-app}` the MCP server's.

**1. ServiceControl's registration.** You already have this if ServiceControl authenticates against Entra. It should have an Application ID URI (`api://{sc-app}`), at least
one delegated scope (the one ServicePulse uses), and **app roles** `reader`, `writer` (and `admin`). Assign people to those roles under *Enterprise applications →
ServiceControl → Users and groups*. This is where ServiceControl's permissions live, and it is all you assign per person.

**2. The MCP server's registration.** *App registrations → New registration*, single tenant, name it for example `ServiceControl MCP`. Then:

1. *Certificates & secrets*: create a client secret and put it in your secret store.
2. *Expose an API*: accept the Application ID URI `api://{mcp-app}` and add a scope, for example `mcp.access` (who can consent: admins and users).
3. *Manifest*: set the access token version to 2 (`requestedAccessTokenVersion: 2`). Tokens are then issued by `https://login.microsoftonline.com/{tenant}/v2.0`, and their
   audience (`aud`) is the application id `{mcp-app}` rather than the `api://` form.
4. *API permissions*: *Add a permission → My APIs → ServiceControl → Delegated*, choose ServiceControl's scope, then **Grant admin consent**. The on-behalf-of exchange
   fails with a consent error (`AADSTS65001`) without this.

**3. A client for people's MCP clients.** Entra does not support dynamic client registration, so register one. *New registration*, platform *Mobile and desktop applications*
(a public client), with the redirect address your MCP client uses for its browser sign-in (see its documentation; it is usually a `http://localhost` or `http://127.0.0.1` address).
Then *API permissions → My APIs → ServiceControl MCP → Delegated → `mcp.access`*. To skip the consent prompt for users, either grant admin consent here or, in the
MCP server's *Expose an API*, add this client under *Authorized client applications*. Tell your MCP client this client id and your tenant. For example, Claude Code has options for a
pre-registered client id and callback port (see `claude mcp add --help`).

**4. The server's settings.**

```bash
export SERVICECONTROL_MCP_Url=https://servicecontrol.example.com
export SERVICECONTROL_MCP_EnableWrites=true

# Hop 1: callers' tokens (issued by Entra for the MCP server)
export SERVICECONTROL_MCP_Http__Authority=https://login.microsoftonline.com/{tenant}/v2.0
export SERVICECONTROL_MCP_Http__Audience={mcp-app}                # what a v2 token's "aud" holds
export SERVICECONTROL_MCP_Http__Audiences__0=api://{mcp-app}      # the other spelling, accepted too
export SERVICECONTROL_MCP_Http__Scopes__0=api://{mcp-app}/mcp.access
export SERVICECONTROL_MCP_Http__ResourceUrl=https://mcp.example.com/mcp

# Hop 2: swap the caller's token for a ServiceControl one (on-behalf-of)
export SERVICECONTROL_MCP_Auth__Mode=TokenExchange
export SERVICECONTROL_MCP_Auth__ExchangeStyle=OnBehalfOf
export SERVICECONTROL_MCP_Auth__Authority=https://login.microsoftonline.com/{tenant}/v2.0
export SERVICECONTROL_MCP_Auth__ClientId={mcp-app}
export SERVICECONTROL_MCP_Auth__ClientSecret=...                  # from your secret store, never a command line
export SERVICECONTROL_MCP_Auth__Scope=api://{sc-app}/.default

export ASPNETCORE_URLS=https://0.0.0.0:8443
export SERVICECONTROL_MCP_Http__AllowedHosts__0=mcp.example.com
```

`Http__RolesClaim` is not needed here: with token exchange, ServiceControl reads the `roles` in the swapped token and decides.

**5. Check it.** Follow the checks in [Verify it](#verify-it-one-hop-at-a-time). For Entra, additionally decode a token your MCP client received (for example at jwt.io):
`iss` should be `https://login.microsoftonline.com/{tenant}/v2.0` and `aud` should be `{mcp-app}`. If `iss` is `https://sts.windows.net/...`, the token version is
still 1 (step 2.3).

**Likely problems on Entra**

| You see | Cause | Fix |
|---|---|---|
| `401` from the MCP server for a valid sign-in | Token version 1, or the audience differs | Step 2.3; check `aud` against `Http__Audience` / `Http__Audiences` |
| Token exchange fails with `AADSTS65001` | No consent for the MCP app to call ServiceControl | Step 2.4, grant admin consent |
| `403` from ServiceControl | The person has no app role on ServiceControl | Assign `reader`/`writer` in step 1 |
| The MCP client cannot find the sign-in | The client expects the authorization server to publish OAuth metadata in a form Entra does not | Entra publishes OpenID Connect metadata. Support depends on the client; not yet tested |

### Verify it, one hop at a time

1. **The server is up:** `curl https://mcp.example.com/healthz` returns `{"status":"ok"}`.
2. **Hop 1 turns strangers away:** `curl -i https://mcp.example.com/mcp` returns `401`, and its `WWW-Authenticate` header points to the protected resource metadata. Open that
   address: it names your identity provider, which is what an MCP client uses to start the sign-in.
3. **Hop 1 turns the wrong token away:** a token issued for ServiceControl itself must also be refused with `401`.
4. **The whole path:** connect an MCP client, sign in, and call `get_health_overview`. A state-changing call is logged as `AUDIT change: caller alice ... -> ok`, and
   ServiceControl records the same person.

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
