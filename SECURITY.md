# Security policy

This is an **unofficial** MCP server for Particular ServiceControl. It is not affiliated with or endorsed by Particular Software.

## Supported versions

The project is pre-release. Only the latest commit on `main` (and, once published, the latest release) receives security fixes.

## Reporting a vulnerability

Please report it **privately**, not in a public issue:

- Use GitHub's private vulnerability reporting: [Report a vulnerability](https://github.com/cjoergensen/particular-servicecontrol-mcp/security/advisories/new).

Include what you found, how to reproduce it, and the version or commit. This is a small project maintained on a best-effort basis: expect an
acknowledgement within a few days, and coordinated disclosure once a fix is available.

## Scope

In scope: this repository's code, its container images and its configuration defaults, in particular anything that could
leak or misuse a token or secret, bypass the read-only default or the caller's roles, or let one caller see another's permissions.

Out of scope: vulnerabilities in ServiceControl, ServicePulse, NServiceBus or an identity provider (report those to their vendors; Particular has its own
[security policy](https://github.com/Particular/ServiceControl/security)), and issues that need a deliberately unsafe configuration that the
server refuses to start with.
