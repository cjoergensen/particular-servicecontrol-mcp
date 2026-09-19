# Testing and verification to-do

What is still unverified, mostly around authentication. Tick items off as they are verified, and update "Known limitations" in the README to match.

Suggested order: A, then the Entra items in B, then the startup check in D, then C, then E. The first two items of A settle whether the recommended setup works at all.

## A. Login flow

- [ ] **A real MCP client completes the browser login against the HTTP host on Keycloak.** Try Claude Code first, then Claude Desktop, VS Code/Copilot and MCP Inspector. Record which ones work. *Done when:* `get_health_overview` succeeds and the server log shows the caller's name.
- [ ] **ServiceControl sees the person.** Do a retry as alice. *Done when:* the server's audit line shows alice and ServiceControl's own log shows alice, not a service account.
- [ ] **Token expiry mid-session.** Let the caller's token expire and confirm the client refreshes it and the exchanged token is renewed, not stale.
- [ ] **Wrong-token cases.** Confirm each is refused: a token for ServiceControl, a token for another audience, an expired token, a token from another issuer. Part of this is already covered by the secured tests.
- [ ] **Metadata correctness.** Check `resource`, `scopes_supported` and the `WWW-Authenticate` header, including behind a reverse proxy with `Http__ResourceUrl` set.

## B. Identity providers

- [ ] **Entra ID, token exchange (on-behalf-of).** Run the README walkthrough on a real tenant. Check token version 2, `aud` and `iss`, both audience spellings, admin consent, and the `roles` claim reaching ServiceControl.
- [ ] **Entra with the MCP clients.** Entra has no dynamic client registration: test with a pre-registered client id, redirect URIs and pre-authorization, and see whether each MCP client copes with Entra's OpenID Connect metadata.
- [ ] **Entra, other modes.** Client credentials with `api://…/.default`, and `az account get-access-token` as a token command.
- [ ] **Entra roles in service-principal mode.** Check that `Http__RolesClaim=roles` hides and refuses tools correctly for a reader.
- [ ] **Auth0 and Okta.** Client credentials with the `audience` parameter, and device code.
- [ ] **Device code on a second provider.** Keycloak is the only one tested.

## C. Environment

- [ ] **Linux.** Run both test suites on Linux (needs CI). `SCMCP_IT_TRANSPORT=rabbitmq` is the fallback for the shared-folder problem.
- [ ] **TLS on the HTTP host's own endpoint.** The tests use plain HTTP on loopback. Test with a real certificate and a private CA.
- [ ] **TLS-terminating proxy.** Test `Http__AllowInsecureTransport`, `AllowedHosts` and forwarded headers together.
- [ ] **Other ServiceControl versions.** One older than 6.18 (no `/api/my/routes`) and the latest.
- [ ] **Monitoring instance with the exchanged token.** Confirm its audience and roles behave with Entra.

## D. Code changes still to make and test

- [ ] **Refuse device code in the HTTP host at startup**, with a unit test and an end-to-end check that the server exits with a clear message. (Pending decision.)
- [ ] **Browser login for the local stdio server**, if wanted: loopback redirect, PKCE and a token cache.

## E. Documentation

- [ ] **Have someone follow the README cold**, with no help, on Keycloak and then Entra. Note every place they stall and fix the text there.
- [ ] **Compare the discovery JSON example** in the README with a real `/api/authentication/configuration` response from an authenticated ServiceControl. The example is reconstructed from the code.
- [ ] **Update "Known limitations"** as items above are ticked off.
