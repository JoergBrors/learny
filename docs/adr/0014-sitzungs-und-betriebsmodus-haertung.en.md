# 0014 — Session and Operating-Mode Hardening

- **Status:** Proposed (item 1 partly implemented by [0026](0026-betriebsmodus-fail-closed.md); remaining items not implemented and require approval)
- **Date:** 2026-10-24
- **Motivation:** [Multi-user review report](../reviews/2026-10-24-mehrbenutzer-pruefbericht.md), findings F1–F6
- **Would supplement:** [0005](0005-authentifizierung.en.md), [0011](0011-mehrbenutzerarchitektur.md)

## Context

The review found that isolation between users works, but (F1) incomplete production configuration silently enables development login with admin privileges, (F2) session cookies can be reused after logout, (F3) open Blazor circuits do not immediately honor disabled accounts, (F4) upserts are not atomic, and (F5/F6) provider binding and trusted proxy enforcement are missing.

## Proposed Decision

1. **Fail-closed operating mode:** Development login is enabled only when `AUTH_MODE=dev` is explicitly configured **and** `ASPNETCORE_ENVIRONMENT=Development`. Remove `AUTH_MODE=auto` or change it to “OIDC or refuse startup.” `devUser`/`devRoles` may exist only in `Development`.
2. **Server-side session version:** Add `users.session_version` as a cookie claim and compare it in `OnValidatePrincipal`. Logout, “sign out everywhere,” disabling an account, and role revocation increment it. Add an absolute session lifetime. Use POST with antiforgery protection for logout.
3. **Circuit revalidation:** Use `RevalidatingServerAuthenticationStateProvider` (framework component, no NuGet) to check user status and session version at short intervals.
4. **Atomic upserts:** Use `INSERT … ON CONFLICT … DO UPDATE` for `user_preferences` and `user_card_states` in both providers.
5. **Provider binding:** Apply `ADMIN_SUBJECTS` and legacy-data migration only when `provider = OIDC_ISSUER`, never for `dev`.
6. **Proxies:** Configure permitted forwarded-header sources (`KNOWN_PROXIES`); require `Secure` cookies in production.

## Consequences (If Accepted)

- Local development must explicitly set `AUTH_MODE=dev` (VS Code launch profiles and tests already do this or must be updated).
- An additional database column and migration (`ALTER … ADD COLUMN session_version`) and a little extra work per request (validation already exists).
- Existing sessions will be invalidated once.

## Guardrails for Changes

- No configuration error may result in unauthenticated admin access.
- Tests: production mode without OIDC must not start; replaying a cookie after logout must return 401; concurrent first writes must not return 500 (see `tests/LearnCards.IntegrationTests`).
