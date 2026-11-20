# Review Report: Multi-user Support and User Isolation

- **Date:** 2026-10-24
- **Version reviewed:** Working directory based on commit `342c9b2` with still-uncommitted changes from ADRs 0011, 0012, and 0013.
- **Review type:** Assessment only, **no production-code change**; only integration tests and this report were added.
- **Summary:** User-data isolation worked in the version examined (no cross-user data mixing found), but a **high-risk** configuration issue (silent development-login fallback) and several medium/low session/concurrency issues were identified.

> **Addendum:** [ADR 0015](../adr/0015-freigaben-globale-lernmodule.en.md) subsequently introduced personal sharing/global modules. Statements about “no shared modules/cards” and “no results crossing user boundaries” describe the version reviewed; explicit sharing/global permissions are now separately tested (see [testing.md](../testing.en.md)). Findings F1–F11 were unchanged at the time of this addendum. Later ADR 0026 addresses part of F1; this report is a historical snapshot.

## 0. Important Note on the Original Request's Assumptions

The request described modules/cards as **global**, with `X-MCP-Key` granting **full API access without user identity** (“Assumption: joint use is intentional”). That accurately described the pre-ADR-0011–0013 system, but those explicit decisions replaced it.

| Request assumption | Historical revision (`342c9b2`) | Reviewed implementation |
|---|---|---|
| `modules`/`cards` global without user ID | **Confirmed:** `ListModulesAsync()`/`ListCardsAsync()` had no owner filter, `modules.name` globally `UNIQUE` | Both tables have `owner_user_id`, owner-only access ([ADR 0011](../adr/0011-mehrbenutzerarchitektur.en.md)) |
| `X-MCP-Key` grants full API access | **Confirmed:** `api.AddEndpointFilter` accepted `keyOk`, falling back to pseudo-user `"mcp-client"` | Service key with no data access; user-bound tokens ([ADR 0013](../adr/0013-mcp-benutzergebundene-anmeldung.en.md)) |
| Identity = OIDC `sub` / development name | Confirmed | Internal `uid`, external identities in `user_identities` |
| Docs did not describe private modules | Confirmed | Documentation updated (section 7) |

Historical assertions were confirmed by reading code; an old-version two-user test was **impossible** because development login then supported only one user. All findings below concern the **newer reviewed version**.

## 1. Answers to Ten Questions

| No. | Question | Status | Evidence and Result |
|---|---|---|---|
| 1 | Can two users sign in independently? | Confirmed | Internal IDs differ; `?devUser=<name>` creates separate users/sessions. Real OIDC could **not be verified** (no Entra access in test environment). |
| 2 | Are themes/preferences separate? | Confirmed | “Theme per user” test, `user_preferences.user_sub = @u`, `CardRepository.SaveThemeAsync`. |
| 3 | Does learning state avoid affecting other users? | Confirmed | Dora's state does not affect Emil's. Cards are private, so users do not update the same shared card in this version. |
| 4 | Are chat history/quiz results limited to authorized users? | Confirmed for quiz; partial for chat | Bob cannot read/delete Alice's quiz result. Chat reads return empty for foreign card. Only Blazor `ChatPanel` saves chat history via `SaveChatHistoryAsync` and circuit `uid`; `POST /api/chat/` saves nothing. Write path code-reviewed only. |
| 5 | Can manipulated requests access others' records? | Confirmed: denied | Foreign IDs yield 404 on cards/modules/state/quiz/chat. `X-User-Id`, `sub`, `uid`, `user_sub`/`owner_user_id` in header/query/body ignored. Invalid IDs give 404, not 500; SQL injection ineffective. |
| 6 | Are sessions separated after user switch/logout? | Partial (F2/F3) | Browser switching shows only new user's data. Logout clears browser cookie, **but copied pre-logout cookie still works** (F2); open Blazor circuits may retain permissions (F3). |
| 7 | Do concurrent actions mix users' data? | Confirmed: no mixing; F4 deviation | 40 simultaneous theme writes, 30 state updates across users showed no mixing. Singletons hold no per-user state. Simultaneous first-write of same key on PostgreSQL caused HTTP 500 (F4). |
| 8 | What personal endpoints can `X-MCP-Key` access? | Confirmed: none | 26 user-scoped endpoints returned 403. Only `GET /api/schema/import/cards` and `POST /api/mcp/link/exchange` work with service key. Previously all endpoints were reachable as `"mcp-client"`. |
| 9 | Which module/card changes affect everyone? | Confirmed: none | No common modules/cards; import/edit/archive/delete affect the owner only. Historically everything was global, including cascading deletion. |
| 10 | Does implementation comply with ADRs? | Confirmed with deviations | ADRs 0011–0013 followed; known discrepancies against ADRs 0005/0003 are F1–F3. Documentation gaps from section 7 were subsequently closed. |

## 2. Findings

“Confirmed” = matches code/docs; “Deviation” = observed mismatch; “Not verified” = only code review or unable to test.

| ID | Severity | Status | Finding |
|---|---|---|---|
| F1 | **High** | Deviation | `AUTH_MODE=auto` silently falls back to development login without OIDC, even in `Production` |
| F2 | Medium | Deviation | Session cookie remains valid after logout (no server-side revocation) |
| F3 | Medium | Not verified | Open Blazor circuits honor disabled accounts/role revocation only on reconnection |
| F4 | Medium | Deviation | SELECT→INSERT upsert race returns PostgreSQL HTTP 500 |
| F5 | Low | Confirmed | Legacy migration and admin bootstrap are not bound to the identity provider |
| F6 | Low | Confirmed | Forwarded headers trust any proxy (KnownProxies/KnownNetworks cleared) |
| F7 | Low | Confirmed | GET logout/development login (logout CSRF; development login allows user/role selection) |
| F8 | Low | Confirmed | No deletion path for personal data; `user_sub` columns lack foreign keys |
| F9 | Low | Confirmed | No rate limits/body-size caps for import/token endpoints; missing CSP headers |
| F10 | Info | Confirmed | Chat history is persisted only by Blazor UI, not API |
| F11 | Info | Confirmed | PostgreSQL uses **one** serialized connection, limiting throughput |

### F1 — Silent Development-login Fallback (High)

- **Files:** `Infrastructure/AppConfig.cs` (mode selection), `Auth/AuthEndpoints.cs` (`/auth/login`), `Services/UserService.cs` (`IsBootstrapAdmin`).
- **Cause:** Default `AUTH_MODE=auto` selects `dev` when `OIDC_ISSUER`/`OIDC_CLIENT_ID` are empty or begin with `YOUR_`. In production this previously opened the app to **anyone**; default `local-dev-user` was **administrator**, and `?devUser=`/`?devRoles=Admin.All` allowed arbitrary users/roles.
- **Impact:** Total takeover of admin/user management and development user data without authentication.
- **Executed reproduction:** `ASPNETCORE_ENVIRONMENT=Production`, unset `AUTH_MODE`, empty `OIDC_*` → log “Auth-Modus: dev”; `GET /auth/login` → 302, then authenticated `GET /api/me` returns `roles: ["admin","user"]`.
- **Suggested fix:** Allow dev login only with **explicit `AUTH_MODE=dev` and `ASPNETCORE_ENVIRONMENT=Development`**; otherwise fail closed. Restrict `devUser`/`devRoles` to development.
- **Later status:** ADR 0026 implements fail-closed production fallback but intentionally permits explicit `dev` outside Development with a warning.

### F2 — Cookie Still Valid After Logout (Medium)

- **Files:** `Program.cs`, `Auth/AuthEndpoints.cs`.
- **Cause:** Cookie is self-contained using Data Protection. `SignOutAsync` deletes it only from the browser; no server-side session ID/revocation list. Expiration is 10 hours sliding.
- **Impact:** A stolen cookie (proxy logs, malware) remains usable even after victim logs out.
- **Executed reproduction:** Login, copy cookie, `GET /auth/logout`, replay copied cookie → **HTTP 200**.
- **Fix:** Server-side per-user `session_version` claim checked by `OnValidatePrincipal`; increment on logout, sign out everywhere, account disable; or use session table. Add absolute expiry.

### F3 — Open Blazor Circuits (Medium, Not Verified)

- **Files:** `Components/**`, `Program.cs`.
- **Cause:** Circuit binds principal at connection. `OnValidatePrincipal` executes on HTTP requests, not SignalR messages. No `RevalidatingServerAuthenticationStateProvider`. Repository methods use the circuit `uid`, potentially allowing a **recently disabled** account to keep editing until the circuit ends. Isolation remains intact because `uid` is unchanged; admin methods recheck DB roles.
- **Suggested manual test:** Open a tab as user, disable them under `/admin`, edit cards in still-open tab → may succeed.
- **Fix:** Implement built-in `RevalidatingServerAuthenticationStateProvider` to periodically verify account status/session version (no NuGet).

### F4 — Upsert Race (Medium, Functional)

- **File:** `Services/CardRepository.cs`, `UpsertInternalAsync` (non-atomic `SELECT 1` followed by `INSERT` or `UPDATE`).
- **Impact:** Two concurrent first writes for same key (e.g., double-click “learned,” multiple tabs) produce one **HTTP 500** (`duplicate key … user_card_states_pkey`). No data mixing because PK is `(user_sub, card_id)`. SQLite did not reproduce; PostgreSQL did.
- **Executed reproduction:** 30 parallel `PUT /api/cards/{id}/state` with no existing state → four HTTP 500s.
- **Fix:** Atomic `INSERT … ON CONFLICT (…) DO UPDATE` for `user_preferences` and `user_card_states` in SQLite ≥3.24 and PostgreSQL.

### F5 — Provider Binding (Low)

- **Files:** `Services/UserService.cs` (`MigrateLegacyDataAsync`, `IsBootstrapAdmin`).
- **Cause:** Legacy `user_sub` migration and `ADMIN_SUBJECTS` check compare only subject, not provider. A newly added/changed IdP with the same subject could take legacy data or become admin. Not exploitable in the reviewed single-issuer configuration (development subjects `local-dev-user`/`dev-<name>`).
- **Fix:** Bind bootstrap/migration to `provider == OIDC_ISSUER`, never `dev`.

### F6 — Forwarded Headers (Low)

- **File:** `Program.cs`: `fwd.KnownNetworks.Clear(); fwd.KnownProxies.Clear();`.
- **Impact:** Clients can set `X-Forwarded-Host/-Proto`; these affect OIDC redirect URL (IdP still accepts only registered URIs) and the `Secure` cookie behavior (`SameAsRequest`). No IP-dependent behavior identified.
- **Fix:** Configure `KNOWN_PROXIES` or use IIS/Azure forwarded-header integration; force production `Secure=Always`.

### F7 — GET Logout and Development Login (Low)

`GET /auth/logout` can be invoked cross-site (logout CSRF; inconvenient, not data access). `GET /auth/login?devUser=` allows login CSRF only in development mode. Use POST logout with antiforgery protection.

### F8 — Personal Data Without Deletion Path (Low)

`chat_history`, `quiz_results`, `user_card_states`, `user_preferences` reference `users` by `user_sub` **without foreign keys**. No delete/anonymize user feature exists. Manually deleting a `users` row leaves orphan histories; `owner_user_id` becomes NULL and content moves into unassigned legacy collection. Privacy requests lack user export/erasure.

### F9 — Limits and Security Headers (Low)

No rate limits on login callback, token exchange, import; no explicit import-body cap; no `Content-Security-Policy`, `X-Content-Type-Options`, or `Referrer-Policy` in the version examined. Blazor HTML-encodes outputs and no direct `MarkupString`/`innerHTML` was found, so no concrete XSS was established.

### F10/F11 — Notes

- **F10:** API/MCP chat users get no persisted chat history; documented in [architecture.md](../architecture.en.md).
- **F11:** `PostgresDatabase` uses one connection protected by `SemaphoreSlim`, preventing interleaving (correctness benefit) but limiting throughput.

## 3. Additional Code-verified Points

- **Identity:** User ID derives exclusively from server-issued `uid` claim (Blazor) or endpoint-filter-populated `HttpContext.Items` (API). No endpoint trusts header/body/query user ID.
- **Personal data queries:** All `user_preferences`, `user_card_states`, `chat_history`, `quiz_results` operations in `CardRepository` filter using `user_sub=@u` or join through owned cards. Deleting results uses `WHERE id=@id AND user_sub=@user_sub`; write endpoints verify card ownership first via `GetCardAsync(id, owner)`.
- **Singletons:** `CardRepository`, `QuizService`, `ChatService`, `OpenAiClient`, `UserService`, `McpLinkService`, `OidcService` do not retain user-specific fields, caches, or static variables, other than constants/regex. Each Blazor component holds its own circuit's `uid`; MCP `AsyncLocal` binds user context per request.
- **SQLite/PostgreSQL:** Same SQL filters and equivalent outcomes: 131/131 SQLite checks, 130/131 PostgreSQL with only F4 failing.
- **Missing/invalid authentication:** API returns 401 without redirect, Blazor redirects to login; disabled/unknown accounts return 401; legacy cookies lacking `uid` require login again.

## 4. Executed and Unavailable Tests

**Executed:** `dotnet build LearnCards.slnx` succeeded; integration tests ([testing.md](../testing.en.md)) passed **131/131 SQLite**, **130/131 PostgreSQL 16 Docker** (parallel first write F4 failed). Users `alice`, `bob`, `dora`, `emil` with separate cookies; admin/auditor; real MCP server process; temporary databases, no production data. F1 additionally reproduced manually in production mode without OIDC.

**Not performed/automated:**
- Two **real interactive Blazor browser sessions**; no browser automation available at review time. Pages were checked by HTTP rendering, repository/API logic via integration tests.
- Actual Entra ID OIDC login, disablement inside open circuit (F3), load testing, and chat history write path (Blazor-only).

Manual two-browser test: normal window A and private window B; log in using `…/auth/login?devUser=anna` and `?devUser=ben`. In A import module/cards, switch theme, mark card learned. In B verify nothing from A is visible and theme/status are independent. Paste A's card ID into B's `/module/<id>` URL → “not found.”

## 5. Recommended Remediations (Priority at Review Time)

1. **F1 (High):** Dev login requires explicit `AUTH_MODE=dev` **and** `Development`; otherwise refuse startup. Dev user/role overrides only in Development.
2. **F4 (Medium):** Atomic `INSERT … ON CONFLICT DO UPDATE` for all upserts; keep regression test.
3. **F2/F3 (Medium):** Server-side session version plus `RevalidatingServerAuthenticationStateProvider`; POST logout.
4. **F5/F6 (Low):** Provider binding for bootstrap/migration; trusted proxies and production Secure cookies.
5. **F8/F9 (Low):** User export/deletion including cleanup/foreign keys, rate/body limits, security headers.

Proposed separate architecture decision: [ADR 0014 — Session/Operating-mode Hardening](../adr/0014-sitzungs-und-betriebsmodus-haertung.en.md) (proposed at review time, not implemented; requires approval). See ADR 0026 for a later partial implementation of item 1.

## 6. Open Architecture Questions

- Should admins inspect other users' private data for support/audit, and under what logged/two-person controls? Currently no.
- Session validity, “sign out everywhere,” maximum duration (then 10-hour sliding), mandatory re-login on role change?
- Retention/deletion policies for chat histories and quiz results; who may export/delete them?
- Should API/MCP chat calls persist history (UI only currently)?
- When should serialized single PostgreSQL connection be replaced with a connection pool?

## 7. Documentation Gaps

At review time, docs **lacked or contradicted** the implementation: `docs/architecture.md` and `docs/database-schema.md` described global cards/modules and lacked `users`, `user_identities`, `user_roles`, `audit_log`, `owner_user_id`; ADR 0005 described full key-based MCP access; deployment docs omitted `ADMIN_SUBJECTS`, `OIDC_ROLE_MAP`, `MCP_PUBLIC_URL`, `MCP_TOKEN_DAYS`, `LEARNCARDS_WEB_PUBLIC_URL`. The claim that only personal learning data was user-specific was not explicitly documented. These gaps were closed with ADRs 0011–0013 and corresponding documentation updates. Authoritative design at review time: *modules and cards are owner-private, personal learning records are user-specific, and there is no unrestricted shared collection*.
