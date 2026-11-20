# 0026 — Fail-closed Operating Mode: No Silent Development Login; ENV_FILE Is Mandatory

- **Status:** Accepted (partially implements item 1 of [0014](0014-sitzungs-und-betriebsmodus-haertung.en.md))
- **Date:** 2026-10-26
- **Motivation:** Production incident: after moving `.env` out of the web root (ADR 0023), the application could no longer read its configuration. With `AUTH_MODE=auto`, it silently fell back to **development login with administrator privileges** (public login page displayed “Local login (Dev mode)”, and SQLite was used instead of PostgreSQL).
- **Related:** [0005](0005-authentifizierung.en.md), [0009](0009-deployment-wege.en.md), [0023](0023-secrets-und-badge-schluessel-unter-iis.en.md), review finding F1.

## Decision

1. **Never enable development login silently:** Outside `ASPNETCORE_ENVIRONMENT=Development`, `AppConfig` uses development login only when `AUTH_MODE=dev` is **explicitly** configured. `auto`, empty, or unknown values (e.g., typo `odic`) result in **OIDC** or a **startup failure** with a clear message (`InvalidOperationException`, no values exposed). `AUTH_MODE=oidc` also fails to start without `OIDC_ISSUER`/`OIDC_CLIENT_ID`/`OIDC_CLIENT_SECRET`. In `Development` (local `dotnet run`, VS Code), `auto` still selects development login when OIDC is not configured.
2. Explicit `AUTH_MODE=dev` **outside Development** remains supported (local Docker-image experiments, integration tests), but emits a **startup warning**. The stricter alternative in ADR 0014—allowing development login only in `Development`—remains a proposal.
3. **`ENV_FILE` is mandatory when specified:** If set and the file is missing or unreadable (e.g., insufficient app-pool permissions), startup fails rather than proceeding without configuration.
4. **IIS deployment:** In addition to the app-pool variable, the path to `.env` is placed in `web.config` (`aspNetCore/environmentVariables`, after mirroring). After startup, the workflow verifies that the login page does not expose development login when `AUTH_MODE≠dev`, and that `/badges/issuer` matches `PUBLIC_BASE_URL` (proving the `.env` was loaded). Otherwise deployment fails.

## Consequences

- Incorrectly located or unreadable configuration produces a visible startup error (HTTP 500.30; details in `stdout` logs), rather than an open administrator login.
- Existing installations using `AUTH_MODE=auto` with complete OIDC configuration continue unchanged; those without OIDC must explicitly select `AUTH_MODE=dev` (local tests only) or configure OIDC.
- Tests and the Docker smoke test already explicitly set `AUTH_MODE=dev`.

## Guardrails for Changes

- No missing-configuration path may grant unauthenticated access; new operating modes must require explicit configuration.
- Tests: `AuthModeTests` (production without OIDC → failure, typo → failure, Development → development login, missing `ENV_FILE` → failure).
