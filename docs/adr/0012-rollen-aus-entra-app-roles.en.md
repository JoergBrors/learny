# 0012 — Roles from Entra ID App Roles

- **Status:** Accepted (supplemented by [0029](0029-themenzugriff-pflichtzuweisungen-und-themenadministration.en.md): `themen_admin` can be mapped through `OIDC_ROLE_MAP`)
- **Date:** 2026-10-24
- **Supplements:** [0011](0011-mehrbenutzerarchitektur.en.md) (roles), [0005](0005-authentifizierung.en.md) (OIDC)

## Context

Roles should be managed centrally in the identity provider, not only in the application's `/admin` interface. The Entra ID app registration “learnpath” defines the app roles **`Admin.All`** (display name “Admin”) and **`Users`**. Entra includes assigned roles in the ID token's `roles` array claim.

## Decision

- During OIDC login, `AuthEndpoints` reads the `roles` claim from the **validated** ID token (including signature, `iss`, `aud`, and `nonce` verification).
- `OIDC_ROLE_MAP` (`AppConfig.RoleMap`) maps role values to internal roles. Default: **`Admin.All=admin`**. Example: `Admin.All=admin,Auditor=auditor` (requires an “Auditor” app role in Entra). This ADR originally allows mapping only `admin` and `auditor`; every user already has `user` (the `Users` app role is relevant only to assignment in Entra, e.g., when “Assignment required” is enabled). ADR 0029 later adds `themen_admin`.
- Roles are synchronized on **every login** (`UserService.SyncIdpRolesAsync`):
  - A mapped role appears in the token but not internally → grant it with `granted_by = 'idp'` and audit event `role.grant.idp`.
  - A mapped role is absent from the token but exists internally with `granted_by = 'idp'` → revoke it and log `role.revoke.idp`.
  - Roles assigned manually (admin UI, `granted_by = <user ID>`) or by bootstrap (`ADMIN_SUBJECTS`) are **not** modified.
  - Unmapped roles are ignored.
- Changes take effect on the next login; existing sessions retain their IdP-derived roles until then. Role changes made inside the application take effect immediately.
- Development mode: `/auth/login?devUser=<name>&devRoles=Admin.All` simulates the claim through the same mapping (`AUTH_MODE=dev` only, for tests).

## Consequences

- Removing `Admin.All` in Entra removes admin privileges at the next login. Removing an IdP-controlled role only in the application is not permanent—it will be restored on the next login. Make such changes in Entra.
- The last-admin protection from ADR 0011 applies only to manual changes; the IdP is authoritative. `ADMIN_SUBJECTS` remains an emergency-access mechanism.
- The `roles` claim is present only if the user (or one of their groups) has been assigned the app role under **Enterprise applications → Users and groups**.

## Guardrails for Changes

- Never trust unvalidated token roles: read only the validated ID token and map exclusively via `RoleMap`.
- For a new IdP-controlled role, update `RoleMap`, `Roles.All` in `Domain/Users.cs`, integration tests, [database-schema.md](../database-schema.en.md), and this ADR.
- `granted_by = 'idp'` exclusively identifies IdP-controlled roles.

## Configuration in Entra ID

1. App registration → **App roles**: add a role with value `Admin.All` (already present), optionally `Auditor`.
2. **Enterprise applications → learnpath → Users and groups**: assign users/groups to the role.
3. Set `OIDC_ROLE_MAP` in the application only if the default `Admin.All=admin` is insufficient.

## Affected Code

- `Auth/AuthEndpoints.cs` (`GetRoles`, development simulation), `Services/UserService.cs` (`SyncIdpRolesAsync`), `Infrastructure/AppConfig.cs` (`RoleMap`)
