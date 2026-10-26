# 0007 — Standalone MCP Server as an API Client

- **Status:** Accepted
- **Date:** 2026-10-24
- **Supplemented by:** [0016](0016-bilder-in-lernkarten.md) (`add_card`/`import_cards` with `images[]`, no image logic in the MCP server), [0013](0013-mcp-benutzergebundene-anmeldung.md) (authentication with user-bound tokens instead of a shared key; OAuth login via Learny), [0015](0015-freigaben-globale-lernmodule.md) (`list_modules` with `scope`)

## Context

AI clients (e.g., Claude Desktop) should be able to read, create, and import cards without direct database access.

## Decision

- `LearnCards.McpServer` is a separate project with no database access. It calls only the Web API (`LEARNCARDS_API_URL`, authentication via `X-MCP-Key`) through `LearnCardsApiClient`.
- Transports: `stdio` and/or HTTP (`/mcp`), configured through `mcpsettings.json` (`McpSettings.cs`).
- *(Superseded by [0013](0013-mcp-benutzergebundene-anmeldung.md): no more `client_credentials`; login through Learny.)* OAuth for HTTP clients in `ProprietaryOAuthService`, including `/.well-known/oauth-authorization-server` and `/oauth/token`.
- Registered tools (`McpProtocol.RegisterTool`): `get_import_schema`, `whoami` (since ADR 0013), `add_card`, `import_cards`, `list_modules` (with `scope` since ADR 0015), `list_cards`, `archive_card`, `restore_card`.

> **As of 2026-10-24:** Shared-key authentication via `X-MCP-Key` has been superseded by [ADR 0013](0013-mcp-benutzergebundene-anmeldung.md): data requests use the user's token. The `whoami` tool was also added.

- Clients are instructed to fetch the schema before importing and not invent fields.
- Under IIS, the MCP client launches the server itself (`learncards-mcp.cmd`); no Windows service is required.

## Consequences

- Every new capability requires a Web API endpoint before adding an MCP tool.
- The MCP protocol is implemented in-house (`McpProtocol.cs`), without an SDK.

## Guardrails for Changes

- New tool: add `engine.RegisterTool(new ToolDefinition(...), handler)` in `Program.cs`, with a clear description and argument JSON schema.
- **Never** duplicate database or domain logic in the MCP server; always use the API.
- Never commit secrets (`mcpsettings.json`, client secrets); use only `*.example.json` and templates under `deploy/iis/*`.
- Add destructive tools (deletion) only when explicitly requested; current tools archive instead of deleting.

## Affected Code

- `src/LearnCards.McpServer/*`
