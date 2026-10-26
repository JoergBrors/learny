# 0013 — MCP: User-bound OAuth Login and Personal Tokens

- **Status:** Accepted
- **Clarification (2026-10-26):** The MCP server's `oauth.issuer` must be the publicly reachable HTTPS URL. `MCP_PUBLIC_URL` (Learny Web) and `webPublicUrl` must match it (deployment check; see the Claude.ai section in [iis-deployment.md](../iis-deployment.md)).
- **Date:** 2026-10-24
- **Supplemented by:** [0025](0025-sichere-speicherung-von-mcp-tokens.md) (storage: HMAC with a key from `.env`, row MAC, encrypted OAuth clients; supersedes “SHA-256 hash only” and “clients in memory only”), [0015](0015-freigaben-globale-lernmodule.md) (`list_modules` with `scope`, read access to shared/global modules; sharing/publication remain prohibited for tokens)
- **Partially supersedes:** [0011](0011-mehrbenutzerarchitektur.en.md) (“API and MCP Key”: `MCP_LINKED_USER_ID` removed), [0007](0007-mcp-server.en.md) (MCP server authentication)

## Context

Each user should authenticate to the MCP server with **their own account** and access **only their own** data. Previously, the MCP server used a shared service key (`X-MCP-Key`), and its OAuth layer issued tokens without user identities. The consent page could be approved without logging in. A shared key tied to a fixed user (`MCP_LINKED_USER_ID`, temporary solution from ADR 0011) is unsuitable for multi-user access.

## Decision

### Learny Is the Identity Source; the MCP Server Holds No User State

- An **MCP token** (`lcm_…`, 256 random bits) identifies exactly one Learny user. Originally, only its SHA-256 hash was stored in `mcp_links`, along with name, kind (`oauth` | `token`), expiration, last use, and revocation. See ADR 0025 for the updated storage design. Every API call checks that the token exists, has not expired or been revoked, and belongs to an active user.
- The API accepts `Authorization: Bearer lcm_…` and acts **only as that user** (all ownership constraints from ADR 0011 apply). Only data routes are allowed: `/api/modules`, `/api/cards`, `/api/import`, `/api/schema`, `/api/mcp/whoami`. Profile, quiz/result APIs, administration, and management of other MCP connections are forbidden to tokens (403).
- The shared **service key `X-MCP-Key` no longer carries a user identity** and does not grant data access. It is used only by the MCP server for code exchange (`POST /api/mcp/link/exchange`) and the public schema. `MCP_LINKED_USER_ID` was removed.

### OAuth Flow (HTTP Transport, e.g., VS Code, MCP Inspector, Claude)

1. The client registers dynamically (`/oauth/register`; redirect URIs may use HTTPS, loopback HTTP, or a custom scheme—never `javascript:`, `data:`, or `file:`) and invokes `/oauth/authorize` using Authorization Code with **mandatory PKCE S256**; neither `plain` nor `client_credentials` is allowed.
2. The MCP server validates the request, stores it as `pending` (10 minutes), sets the HttpOnly cookie `lc_mcp_pending`, and redirects the browser to `{Learny}/mcp/connect?return=…&state=<pendingId>&client=…`.
3. The user logs into Learny (Entra ID) and decides on the consent page. Learny creates a **single-use code** (5 minutes, stored only as a hash) and redirects to `{MCP_PUBLIC_URL}/oauth/callback?code=…&state=…`. **Only** callbacks to `{MCP_PUBLIC_URL}/oauth/callback` are allowed (no open redirect).
4. The MCP server checks cookie = `state` (login CSRF protection: an attacker cannot complete their authorization with a victim's consent), exchanges the code server-side using the service key for a user-bound token, and issues its own one-time authorization code to the client.
5. At the token endpoint, following PKCE validation, the client receives the **Learny token as an access token** (lifetime `MCP_TOKEN_DAYS`, default 30 days, no refresh token).
6. Before each `/mcp` request, the MCP server checks the token using `GET /api/mcp/whoami`. Revoked/expired tokens or disabled users immediately produce HTTP 401 with `WWW-Authenticate`, requiring reauthentication. If Learny is unreachable, access is denied (**fail closed**).
7. Tool calls forward the request's token to the API (`McpUserContext`, scoped to the request via `AsyncLocal`).

### Personal Token (stdio, Scripts)

Users can generate a token in `/profile` for 30, 90, or 365 days. It is displayed **only once**. The stdio MCP server reads it from `LEARNCARDS_MCP_TOKEN`; HTTP clients send it as a Bearer header. Each connection can be revoked individually in the profile. A token cannot create further connections.

### Configuration

- Web: `MCP_PUBLIC_URL` (allowed public base URL(s) of the MCP server, callback allowlist), `MCP_TOKEN_DAYS`, `MCP_API_KEY` (service key). Development defaults: `http://127.0.0.1:8787` / `http://localhost:8787`.
- MCP server: `upstreamApi.baseUrl`/`apiKey`, `LEARNCARDS_WEB_PUBLIC_URL` or `upstreamApi.webPublicUrl` (public Learny URL for browser redirects; defaults to `baseUrl` without `/api`), `oauth.enabled`, `oauth.issuer` (public MCP URL), `LEARNCARDS_MCP_TOKEN` (stdio only). `oauth.signingKey` and `oauth.tokenLifetimeMinutes` are no longer used.

## Consequences

- **Breaking change for existing MCP integrations:** The shared key alone no longer grants data access. HTTP clients log in through OAuth; stdio clients need a personal token. Previously registered clients (in memory) must register again after an MCP-server restart (dynamic registration).
- Consent uses the Learny session (Entra ID): users who cannot log in (e.g., disabled accounts) cannot obtain MCP access.
- There are no refresh tokens; users must authenticate again after expiration. A stolen token remains usable until revocation/expiration; therefore the profile supports revocation and tokens are restricted to data routes.
- Pending authorizations and MCP authorization codes reside in MCP server memory; restarting during login interrupts the process.
- Scaling the MCP server to multiple instances would require shared state for registration, pending requests, and codes.

## Guardrails for Changes

- The MCP server remains a **pure API client** without a database (ADR 0007); user identity comes exclusively from forwarded Learny tokens.
- New MCP tools use `LearnCardsApiClient` (token forwarding), **never** the service key for data. Their API routes must be in `TokenAllowedPrefixes` and honor ADR 0011 ownership rules. Administrative features must **not** be enabled for tokens without a separate security decision/ADR.
- The service key is used only server-side by the MCP server; never expose it to clients or include it in tokens or logs.
- Redirect URLs must pass the `MCP_PUBLIC_URL` allowlist; never store or log tokens/codes in plaintext.
- Cover flow changes with tests in `tests/LearnCards.IntegrationTests` (“MCP connections” and “MCP server: OAuth login”).

## Affected Code

- Web: `Services/McpLinkService.cs`, `Api/ApiEndpoints.cs` (auth filter, `/mcp/*`), `Components/Pages/McpConnect.razor`, `Profile.razor`, `Data/Schema.cs` (`mcp_links`, `mcp_link_codes`)
- MCP server: `ProprietaryOAuthService.cs`, `Program.cs` (OAuth endpoints, `/mcp`), `LearnCardsApiClient.cs` (`McpUserContext`), `McpSettings.cs`
