# Testing and verification to-do

Scope: **the unsecured setup, then Microsoft Entra ID.** ServiceControl runs on a VM in Azure and ServicePulse as a Container App, both registered as apps in Entra, and the MCP server is registered as a third app. Tick items off as they are verified, and update "Known limitations" in the README to match.

Order: each phase isolates one layer, so a failure points at one thing.

1. Local stdio server with a token (ServiceControl's roles and audience).
2. HTTP host run locally with Entra (hop 1 and the token exchange), optional but cheap.
3. HTTP host as a Container App (deployment).
4. MCP clients signing in with OAuth (the client login).

## Unsecured setup

Done (see [Done](#done)). Keep it as the regression baseline: re-run the end-to-end suite when tools or schemas change.

## Phase 0: Prerequisites

- [ ] **Record the facts about the existing setup:** ServiceControl's URL and TLS certificate (public CA or private), the output of `/api/authentication/configuration` (`authority`, `audience`, `role_based_authorization_enabled`), ServicePulse's client id, and whether a monitoring instance is on the VM.
- [ ] **Network path.** Confirm the Container App can reach the VM (VNet, NSG, DNS) and that it trusts its certificate (private CA: mount the PEM and set `TrustedCaCertificatePath`).
- [ ] **Three test users** assigned on the ServiceControl enterprise app: a **reader**, a **writer** (only the writer role, to learn whether it can also read), and one with **no role**.
- [ ] **Register the MCP server app** as in the README's Entra example: client secret, `api://` URI and scope, token version 2, delegated permission to ServiceControl with admin consent.
- [ ] **Register a public client app** for MCP clients (redirect URIs per client), and pre-authorize it on the MCP app if you want no consent prompt.

## Phase 1: Local stdio server with a token

- [ ] **Token command with `az`.** `az account get-access-token --resource api://…` may be refused unless the Azure CLI is an authorized client of ServiceControl's API. Note what is needed. Device code with a registered public client is the alternative.
- [ ] **Read tools** work as the reader, writer and no-role users: correct data, the right tools offered, and the no-role user gets a clear message.
- [ ] **Write tools** as the writer: retry, archive, unarchive, dismiss. The reader must be refused with the "needs the writer role" message and never see the tools.
- [ ] **ServiceControl sees the person.** Its own log or audit trail shows the user's name for a retry.
- [ ] **Wrong audience** (a token for another API) gives an understandable 401.
- [ ] **Expiry.** After the token expires the command runs again and the session continues.
- [ ] **Monitoring instance** with the same token, if one runs on the VM.

## Phase 2: HTTP host locally with Entra (optional but recommended)

Run the HTTP host on your machine against the Azure ServiceControl, and use a script or `az` to get a token for the MCP server, so no MCP client is involved.

- [ ] **Hop 1 rejects strangers:** no token gives 401 with the metadata pointer; a token for ServiceControl itself is refused; an expired token is refused.
- [ ] **Token exchange works** for the reader and the writer: check `iss` and `aud` on the incoming token (v2 and the audience spellings in `Http__Audience`/`Http__Audiences`).
- [ ] **Failure modes read clearly:** no admin consent (`AADSTS65001`), wrong secret, missing scope.
- [ ] **Roles** reach ServiceControl through the exchanged token, and ServiceControl's decision shows up in the tools offered.
- [ ] **Two users at once** see different tool sets and never each other's permissions.

## Phase 3: HTTP host as a Container App

- [ ] **Image published by the release workflow.** Creating a release builds and publishes the HTTP host image (the workflow and its tag versions are yours to control). Confirm the registry and image name, that the image is `linux/amd64` (what Container Apps runs), that it starts as a non-root user on port 8080, how a release maps to a tag (pin the Container App to a version tag, not `latest`), and how the Container App is given pull access if the registry is private.
- [ ] **Configure the Container App:** HTTPS ingress, the client secret as a secret (or from Key Vault), `Http__AllowedHosts`, `Http__ResourceUrl` (the public https address), `Http__AllowInsecureTransport=true` (TLS ends at the ingress), `TrustedCaCertificatePath` if needed.
- [ ] **Health probe** on `/healthz`; the 401 challenge and protected resource metadata work through the ingress and advertise the public address.
- [ ] **Logs** in Azure show the `AUDIT change:` lines and never a token or secret.
- [ ] **More than one replica** works (the host is stateless, but the exchange cache is per replica).
- [ ] **Startup safety checks** behave as documented when a setting is wrong (the container should exit with the message).

## Phase 4: MCP clients sign in with OAuth

Entra has no dynamic client registration, so each client needs the pre-registered client id.

- [ ] **Which clients work.** Try Claude Code, Claude Desktop, VS Code/Copilot chat, Copilot CLI and MCP Inspector. Record for each: the browser login completes, `get_health_overview` works, the token refreshes, signing out and back in works, and it copes with Entra's OpenID Connect metadata.
- [ ] **Per person:** the reader, writer and no-role users each see the right tools and get the right refusals from a real client.
- [ ] **Every tool listed and callable** in each client (the schema problem found with Copilot CLI must not recur).
- [ ] **Write tools from a real agent:** retry a whole failure group (the client should ask to confirm the count), follow the asynchronous progress, archive and unarchive, dismiss a custom check, and give a wrong `expectedCount` (nothing may be sent). Note whether the client prompts for the destructive tools.

## Follow-up work

- [ ] **Refuse device code in the HTTP host at startup**, with a unit test and an end-to-end check that the server exits with a clear message. (Pending decision.)
- [ ] **Skip `GET /api/my/routes` when authentication is disabled.** ServiceControl 6.21.0 answers it with `500` in that case; the server falls back to offering every tool but logs a warning on each tool listing. Use `/api/authentication/configuration` (`enabled: false`) to skip the call, with a test.
- [ ] **Connect the HTTP host to the release workflow.** The repository has no container settings committed yet, so the workflow has to supply them. The .NET SDK can build the image without a Dockerfile (`dotnet publish -c Release -r linux-x64 -t:PublishContainer`); decide whether the settings live in the project file or the workflow. Add the stdio host only if wanted.
- [ ] **Have someone follow the README cold** on the Entra setup, with no help. Note every place they stall and fix the text there.
- [ ] **Compare the discovery JSON example** in the README with the real `/api/authentication/configuration` response from the Azure ServiceControl.
- [ ] **Add the demo runner and a manual test guide to the repo** (for example under `samples/`): Particular's compose platform plus a small program over the test workload that produces failures, a failing custom check and a dead heartbeat, with a healthy mode so retries succeed. Today it exists only as a throwaway.
- [ ] **Update "Known limitations"** as items above are ticked off.

## Parked (out of scope for now)

- Keycloak beyond the existing automated tests; Auth0 and Okta (client credentials with `audience`, device code).
- Browser login for the local stdio server (loopback redirect, PKCE and a token cache): decide after phase 4.
- Linux (needs CI), TLS on the HTTP host's own endpoint (the Container App ingress terminates TLS), TLS-terminating proxies other than the ingress.
- ServiceControl versions other than 6.21.0, including one older than 6.18 (no `/api/my/routes`).
- Metadata behind other reverse proxies; token expiry mid-session on Keycloak.

## Done

- [x] **Local server from Copilot CLI, no authentication (2026-09-20).** Copilot CLI 1.0.86, stdio server, against the Docker Compose platform from Particular/PlatformContainerExamples (ServiceControl 6.21.0 with audit and monitoring) and the test workload. Called directly over MCP, the read tools returned correct data (failure groups, health overview, custom checks, heartbeats). Copilot at first did not use the `list_*` tools and `search_messages`; the likely cause was their schemas using `"type": ["string", "null"]` (not confirmed inside Copilot). Fixed by advertising plain types, with a unit test. Still to record: what each tool returned when driven by Copilot itself.
- [x] **Full suite after the fix (2026-09-20).** 176 unit and protocol tests and all 48 end-to-end tests passed.
