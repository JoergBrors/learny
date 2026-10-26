# 0011 — Multi-user Architecture: Identity, Ownership, Roles, Migration

- **Status:** Accepted (supplemented by [0029](0029-themenzugriff-pflichtzuweisungen-und-themenadministration.md): `themen_admin` role)
- **Date:** 2026-10-24
- **Partially superseded by:** [0013](0013-mcp-benutzergebundene-anmeldung.md) (`MCP_LINKED_USER_ID` removed; MCP access uses user-bound tokens)
- **Refined by:** [0015](0015-freigaben-globale-lernmodule.md) (shared and global modules as explicitly managed exceptions to purely private ownership; `auditor` receives read access)
- **Reviewed in:** [Multi-user review report](../reviews/2026-10-24-mehrbenutzer-pruefbericht.md); hardening proposal: [0014](0014-sitzungs-und-betriebsmodus-haertung.en.md)
- **Supplemented by:** [0012](0012-rollen-aus-entra-app-roles.en.md) (roles derived from Entra ID app roles)
- **Supplements:** [0005](0005-authentifizierung.en.md) (authentication), [0004](0004-datenbank-abstraktion.en.md) (schema changes)

## Context

Before this decision, modules and cards were global: every signed-in user could see and modify everything, `modules.name` was globally unique, and requests authenticated using `X-MCP-Key` ran as the pseudo-user `"mcp-client"`. Only quiz results, card state, chat history, and theme were separated using `user_sub` (the external identity provider subject). Learny needs multi-user support; sharing, global content, and user-bound MCP access build on this foundation.

## Decision

### Identity Model

- Each user has a **stable internal ID** (`users.id`, UUID), independent of the identity provider.
- External identities (`user_identities`: `provider` + `subject` → `user_id`, unique) are created on first login. The provider is the OIDC issuer URL or `dev`. One user may have multiple identities (model prepared, no UI yet).
- On login the server puts the **`uid`** claim into the cookie. This is the sole basis for authorization; user IDs supplied by clients (headers, bodies, query parameters) are never trusted.
- On **every** cookie request (`OnValidatePrincipal`), the database is checked to confirm that the user exists and `status = active`; role claims are refreshed from the database rather than read from the cookie. Legacy cookies without `uid` are rejected.
- Disabled users (`status = disabled`) are immediately denied (API 401, no new login).

### Ownership and Data Isolation

- `modules.owner_user_id` and `cards.owner_user_id` (foreign key to `users`, `ON DELETE SET NULL`). The card owner always equals the module owner.
- `modules.name` is unique only **per owner** (`ux_modules_owner_name`).
- In user-specific tables (`user_preferences`, `user_card_states`, `chat_history`, `quiz_results`), `user_sub` now stores the **internal user ID** (column name retained for compatibility, no foreign key).
- **Every** repository method that reads or changes modules/cards takes `ownerId` and filters in SQL (`WHERE owner_user_id = @owner`). Other users' records and unassigned records appear nonexistent (404 or empty list).
- Quiz results may reference only owned modules/cards. Card state and chat are limited to owned cards.
- Client-supplied card IDs are accepted only when globally unused; collisions generate new IDs (preventing overwrites or guessing others' cards while allowing imports).
- Invalid IDs (not GUIDs) are rejected early (404 rather than a database error).

### Roles and Permissions

Users may have multiple roles (`user_roles`); all users always have `user`.

| Capability | `user` | `admin` | `auditor` |
|---|:---:|:---:|:---:|
| Manage own modules/cards/results and change display name | Yes | Yes | Yes |
| Read or modify someone else's **private** modules/cards/results | No | No | No |
| Disable/enable users, assign roles | No | Yes | No |
| Assign unowned legacy content to a user | No | Yes | No |
| Read audit log | No | Yes | No |
| Read global content/results (introduced in phase 2) | — | — | Yes (read-only) |
| Publish/edit global content (introduced in phase 2) | — | Yes | No |

- Even administrators **cannot access others' private content** through the normal API. Administration does not imply access to private user data.
- Roles are assigned only through `UserService`, which checks the caller's role **against the database** (`RequireRoleAsync`), not claims. The last active administrator cannot lose the role or access; users cannot disable themselves.
- In this phase, `auditor` existed and could be assigned but provided no extra privileges; read access to global content/results was introduced by [0015](0015-freigaben-globale-lernmodule.md). Further role permissions are decided in the sharing-model ADR as needed.
- Bootstrap: subjects listed in `ADMIN_SUBJECTS` (comma-separated IdP `sub` values, **not** email addresses because those are not reliably verified) receive `admin` on login. Development mode also grants this role to `local-dev-user`.
- Administrative activities (role assignments, disabling users, ownership assignments, initial creation) are written to `audit_log`.

### API and MCP Key (Superseded by [0013](0013-mcp-benutzergebundene-anmeldung.md))

- `/api` requires a login cookie **or** `X-MCP-Key` (constant-time comparison).
- The shared key provides only **technical** access. It operates exclusively on behalf of the user defined by `MCP_LINKED_USER_ID` (temporary solution). When unset, key-based clients receive 403 from all user-specific endpoints (only `/api/schema/import/cards` remains accessible). Administrative endpoints (`/api/admin/*`) are never accessible using the key.
- User-bound MCP connections (profile linking, tokens, revocation) are specified in a separate ADR (phase 3).

### Migration of Existing Data (Repeatable, Without Data Loss)

1. Add `users`, `user_identities`, `user_roles`, `audit_log` via `CREATE TABLE IF NOT EXISTS`.
2. Add `owner_user_id` using idempotent column migrations (PostgreSQL: `PostgresDatabase.MigrateOwnershipAsync`; SQLite: `SqliteDatabase.MigrateOwnership`). Existing records retain `owner_user_id = NULL` (“unassigned”).
3. PostgreSQL: remove constraint `modules_name_key`. SQLite: rebuild `modules` using the standard procedure (disable foreign keys, copy table, swap tables, re-enable foreign keys), because inline `UNIQUE` cannot be removed using `ALTER`.
4. Create ownership indexes (`Schema.OwnershipIndexStatements`) **after** column migrations.
5. On an identity's first login, rewrite legacy records belonging to that subject (`user_sub = <external subject>`) in the four user-specific tables to the internal ID; then set `users.legacy_migrated = 1`. Only records for exactly that subject are touched.
6. **Never assign ambiguous ownership automatically.** Unassigned modules are invisible to regular users; an admin assigns them explicitly in `/admin` (or `POST /api/admin/modules/{id}/owner`). Name conflicts produce an error and audit entry.
7. Rollback: back up the database before rollout. Migration deletes nothing and keeps original columns/tables. Reverting to the previous application version makes sense only when no new user/ownership data has been created in the meantime (the previous version ignores `owner_user_id` and continues operating, but `user_sub` values are now internal IDs and card/module access would be global). See [database-schema.md](../database-schema.md).

## Consequences

- **Breaking change for the MCP server:** Shared-key clients receive 403 (the original `MCP_LINKED_USER_ID` transitional mechanism was replaced by user-bound tokens in [0013](0013-mcp-benutzergebundene-anmeldung.md)).
- At least one admin must be configured through `ADMIN_SUBJECTS` after rollout; otherwise legacy data remains unassigned.
- Every cookie request adds a small database lookup, allowing account disablement and role changes to take effect immediately.
- Blazor circuits retain the principal established on connection; admin operations additionally check roles in the database.
- A change to the OIDC issuer creates new identities (and users); identity merging is not implemented yet.

## Guardrails for Changes

- **Every new repository method operating on modules/cards must take an `ownerId` parameter and filter on it in SQL.** Never read or write only by `id`.
- User ID comes **exclusively** from claim `uid` (Blazor) or `HttpContext.Items["lc.user"]` (API, populated by endpoint filter); never from request parameters or headers.
- New API endpoints belong to the `secured` group (requires user context); admin endpoints belong under `/admin`, with database-backed role checks.
- Always use `UserService.RequireRoleAsync` (database) for authorization of changes, not just `User.IsInRole`.
- For new user-related tables, add an ownership/user column, an isolation test in `tests/LearnCards.IntegrationTests`, and update [database-schema.md](../database-schema.md).
- Validate IDs supplied in new user-input queries using `UserService.IsGuid` (otherwise PostgreSQL may raise type errors).
- Never allow security-sensitive roles to be assigned through profile endpoints (`/api/me`).

## Affected Code

- `Domain/Users.cs`, `Services/UserService.cs`, `Services/CardRepository.cs`, `Services/QuizService.cs`, `Services/ChatService.cs`
- `Data/Schema.cs`, `Data/PostgresDatabase.cs`, `Data/SqliteDatabase.cs`
- `Api/ApiEndpoints.cs`, `Auth/AuthEndpoints.cs`, `Program.cs`, `Infrastructure/AppConfig.cs`
- `Components/Pages/Profile.razor`, `Admin.razor`, `Components/Layout/MainLayout.razor`
- `tests/LearnCards.IntegrationTests`
