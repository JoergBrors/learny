# 0005 — Authentication: OIDC, Development Login, `X-MCP-Key`

- **Status:** Accepted
- **Date:** 2026-10-24
- **Supplemented by:** [0011](0011-mehrbenutzerarchitektur.en.md) (internal user ID, roles, ownership), [0012](0012-rollen-aus-entra-app-roles.en.md) (roles from Entra app roles), [0013](0013-mcp-benutzergebundene-anmeldung.en.md) (MCP tokens; `X-MCP-Key` is now only a service key without data access)
- **Hardening proposal:** [0014](0014-sitzungs-und-betriebsmodus-haertung.en.md) (not implemented)

## Context

Production uses Entra ID, while local development should work without an identity provider; automation (MCP) requires non-browser access.

## Decision

> **As of 2026-10-24:** Statements about `user_sub` and full API access via `X-MCP-Key` have been superseded by ADRs 0011 and 0013 (internal user ID `uid`; the key does not grant data access). Known operational deviations: review findings F1–F3 (fallback to development login with `AUTH_MODE=auto`, cookie remaining after logout, and open circuits).

- `AUTH_MODE=auto`: select `oidc` when `OIDC_ISSUER` and `OIDC_CLIENT_ID` are set; otherwise `dev`.
- OIDC: authorization code flow with PKCE, ID token validation against JWKS (`iss`, `aud`, `exp`, `nonce`), then cookie session `lc.auth` (HttpOnly, SameSite=Lax, 10-hour sliding expiration).
- `/api/*` accepts either a login cookie **or** the `X-MCP-Key` header (`MCP_API_KEY`). Without either: HTTP 401 (no redirect).
- User-specific data (quiz results, chat, card state, theme) is separated by `user_sub`.
- Data Protection keys are persisted under `data/keys` or `DATA_PROTECTION_DIR`.

## Consequences

- `dev` mode is **not for production**.
- The MCP key grants full API access; it is a secret.

## Guardrails for Changes

- New `/api` endpoints inherit the authentication filter of the `api` group; do not add endpoints outside that group without justification.
- User-scoped endpoints must handle `X-MCP-Key` (no user `sub`); inspect the pattern in `ApiEndpoints.cs`.
- No secrets in code, sample files, or logs. Configure only via environment/`.env` (see `.env.example`).
- Add new settings to `AppConfig.cs`, `.env.example`, `deploy/iis/web.env`, and the documentation.

## Affected Code

- `src/LearnCards.Web/Auth/*`, `Api/ApiEndpoints.cs` (filter), `Infrastructure/AppConfig.cs`, `Program.cs`
