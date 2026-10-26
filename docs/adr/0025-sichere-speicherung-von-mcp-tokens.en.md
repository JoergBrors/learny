# 0025 — Secure Storage of MCP Tokens, Codes, and OAuth Clients (Protection Against PostgreSQL Server Access)

- **Status:** **Accepted and implemented** (owner decisions dated 2026-10-26, below). Replaces “SHA-256 only” storage and memory-only client registration in [0013](0013-mcp-benutzergebundene-anmeldung.en.md).
- **Date:** 2026-10-26
- **Builds on:** [0013](0013-mcp-benutzergebundene-anmeldung.en.md), [0011](0011-mehrbenutzerarchitektur.en.md), [0005](0005-authentifizierung.en.md), [0023](0023-secrets-und-badge-schluessel-unter-iis.en.md)
- **Related:** [0002](0002-keine-nuget-pakete.en.md) (built-in .NET `HMACSHA256`, `HKDF`, `CryptographicOperations.FixedTimeEquals`), [0004](0004-datenbank-abstraktion.en.md) (SQLite/PostgreSQL).

## Context

A 256-bit random MCP token (`lcm_…`) grants read/write access to **one user's** modules and cards. Originally, `mcp_links` stored `SHA-256(token)`; one-use codes in `mcp_link_codes` were similarly hashed. This prevents **reading** tokens from a database dump but does not prevent a database attacker from inserting their own chosen token hash or tampering with existing records.

| Attack with database access | Original design |
|---|---|
| Read tokens from table | Not feasible; hashes only ✔ |
| **Inject attacker-chosen token:** compute SHA-256 and insert `(user_id = <victim>, token_hash = …)` | **Possible** ✘; no secret needed; attacker then acts as victim |
| Change `user_id`, extend `expires_at`, clear `revoked_at` | **Possible** ✘; no tamper detection |
| Inject one-time exchange code | Possible ✘ |
| Brute-force tokens from leaked backup | Practically infeasible at 256 bits ✔ |
| `token_hint` | Stored last four token characters in plaintext (24 bits of unnecessary partial disclosure) ✘ |

**Goal:** Even with complete read/write database access (but no application access or its secret), an attacker must not acquire, mint, or modify valid MCP credentials.

## Threat Model

- **In scope:** Database dumps/backups, SQL write access (tampering, insertion, deletion), database logs, `pg_stat_statements` (parameters sent by the application), and replicas.
- **Out of scope:** Compromise of the application server and secret (mitigated by expiration, profile revocation, and restricted data routes); client/browser compromise; network interception (TLS is assumed).

## Proposed Design (Subsequently Approved and Implemented)

### 1. Key Outside Database

The application holds `MCP_TOKEN_KEY` (at least 32 random bytes, Base64) **only in application secret storage**: for IIS, `.env` outside web root or Key Vault (ADR 0023); for Docker, environment/secret. Never put it in PostgreSQL, DB backups, repository, or logs. Derive independent subkeys using **HKDF-SHA-256**, with `info = "learny/mcp/token-hash/v1"` and `"learny/mcp/row-mac/v1"`.

Fail-closed startup if the required key is absent or too short. Only in development, generate once under `data/` (atomically, with marker; missing key on an existing installation must fail rather than regenerate). A “pepper file next to the database” is expressly forbidden.

### 2. Keyed Hash Prevents Token Injection

`token_hash = HMAC-SHA256(K_hash, "lcm-token-v1" ‖ token)`. Without `K_hash`, an attacker cannot compute a matching database hash for a chosen token. Apply `HMAC(K_hash, "lcm-code-v1" ‖ code)` to one-time codes.

### 3. Row Integrity Prevents Tampering

Each `mcp_links` row gets `row_mac = HMAC-SHA256(K_mac, canonical(id ‖ user_id ‖ kind ‖ token_hash ‖ created_at ‖ expires_at ‖ revoked_at ‖ key_id))`. Check the MAC on **every** API call **before all other checks** using `CryptographicOperations.FixedTimeEquals`:

- Changed user, extended validity, cleared revocation, or exchanged hash → MAC mismatch → **401** and audit event `mcp.token.integrity_failure` (never log token/hash).
- Legitimate revocation and expiration updates recompute the MAC. `last_used_at` is deliberately omitted to avoid a protected write on every request.
- Attackers may only **delete** records or deny service; they cannot add privileges. Deletions mean access is lost (fail closed).

### 3a. No Token Fragments in Database

Remove the previous `token_hint` (last four cleartext characters). Profile identification uses name, kind, creation/expiration/last-used times, and an **independent random short ID** (e.g., four characters from row ID). Clear legacy hints during migration.

### 4. Unchanged Token Format and Generation

`lcm_` + Base64url(32 bytes from `RandomNumberGenerator`), 256 bits, displayed once. **Never** send plaintext to the database; application computes hashes/MACs so DB query parameters/logs see only protected values. Do not show tokens in logs, errors, or API responses after issuance, not even shortened versions.

### 5. Consume One-time Codes Atomically

Exchange with a single statement: `UPDATE … SET used_at = @now WHERE code_hash = @h AND used_at IS NULL AND expires_at > @now`; require exactly one affected row. This prevents concurrent double redemption (lessons from the concurrent upsert bug). Codes and rows also receive a MAC.

### 6. Key Rotation

Store `key_id` (e.g., `k1`) per row. Configure mandatory current `MCP_TOKEN_KEY` and optional `MCP_TOKEN_KEY_PREVIOUS` for validation only. On next successful token use, re-hash and MAC with the new key (plaintext token is then available). Losing the key invalidates tokens, requiring users to reconnect, but does not delete learning data. Back up this secret **separately** from DB backups.

### 7. Legacy Token Migration

New columns: `hash_alg` (`sha256` legacy, `hmac1` new), `key_id`, `row_mac` (nullable, idempotent `CREATE`/`ALTER`, SQLite/PostgreSQL). Convert old `sha256` tokens to `hmac1` on **first successful use**, when plaintext is supplied. Until conversion, legacy tokens provide only the previous level of security. After `MCP_LEGACY_TOKEN_UNTIL` (original proposal: 90 days after introduction), reject old hashes and require reconnection. New rows use only `hmac1`.

### 8. Persist Encrypted OAuth Clients

Previously, MCP OAuth registrations were held only in server memory, forcing Claude to re-register after every restart/deployment. Instead, persist in **Learny's database**, **encrypted using the .env key**:

- Table `mcp_oauth_clients(client_id PK, key_id, nonce, ciphertext, created_at, row_mac)`. Encrypt client records (`client_name`, `redirect_uris`, `token_endpoint_auth_method`, `scopes`, `application_type`, optionally hashed secret) using **AES-256-GCM** with an HKDF subkey (`info = "learny/mcp/client-enc/v1"`) from `MCP_TOKEN_KEY`. Bind `client_id` as additional authenticated data (AAD), use random 96-bit nonces. A database attacker cannot read or modify names/redirects (GCM authentication and row MAC) or swap rows (AAD).
- Public clients have no secret; confidential client secrets are stored as keyed hashes.
- Service-key-only endpoints (`X-MCP-Key`, no user data access): `POST /api/mcp/oauth/clients` and `GET /api/mcp/oauth/clients/{client_id}`. MCP server additionally caches clients in memory and fetches them on demand. In-progress authorizations (10 min) and MCP authorization codes (5 min) stay in memory; restarts only interrupt active logins.
- Maximum 1,000 clients; expire unused registrations after 30 days; rate-limit registration.

### 9. Database Defense in Depth

- Application database account must not be superuser or have `CREATEROLE`; encrypt backups and store them separately from keys.
- `log_statement`/`pg_stat_statements` may log application parameters but contain only HMACs, never plaintext tokens.
- Per-user PostgreSQL row security may be considered later but is **not planned**; ownership isolation remains in application code under ADR 0011.

## Consequences

- Database-only attackers cannot obtain, forge, or change credentials; they may at most delete rows (availability).
- New required operational secret `MCP_TOKEN_KEY` in IIS workflows, Docker Compose, `.env.example`, docs. Back it up separately from DB backups. Missing/invalid key prevents startup.
- Key loss invalidates tokens **and** encrypted OAuth clients, requiring user/Claude reconnection but not losing learning content.
- Schema extensions in `mcp_links`/`mcp_link_codes` and `mcp_oauth_clients`; keep `database-schema.md` synchronized.
- Minor CPU overhead (two HMACs).
- Existing tokens are migrated on use or expire at the deadline; risk is bounded to that transitional period.

## Owner Decisions (2026-10-26)

1. Store `MCP_TOKEN_KEY` in `.env` outside IIS web root (ADR 0023), or Docker `.env`/environment. Key Vault may be added later.
2. Allow a **90-day** migration period, converting old SHA-256 tokens upon first use, then rejecting those still unconverted. Configure deadline via `MCP_LEGACY_TOKEN_UNTIL`.
3. Persist OAuth clients **encrypted** in the database using a key from `.env`.
4. Make `MCP_TOKEN_KEY` **mandatory** outside `Development` (Base64, ≥32 bytes). Docker Compose enforces `${MCP_TOKEN_KEY:?…}`; IIS workflow validates the GitHub secret `MCP_TOKEN_KEY` before deployment. Document requirement in templates, tables, and Help. In `Development`, generate a key once under `data/`.

## Implementation and Deviations

Implemented in `Services/McpTokenProtector.cs`, `McpLinkService.cs`, `McpOAuthClientStore.cs`, `Infrastructure/McpTokenKeys.cs`, `Data/Schema.cs` with migrations, `Api/ApiEndpoints.cs` (`POST/GET /api/mcp/oauth/clients`, service key only), MCP server (`LearnCardsApiClient`, `ProprietaryOAuthService.EnsureClientAsync`), configuration and documentation. Tests: `McpTokenUnitTests` (M1–M44) plus an end-to-end restart test.

Differences from the proposal:
- **Only public OAuth clients** (`token_endpoint_auth_method=none`, no secret: Claude and other DCR clients) are persisted. Dynamically registered **confidential** clients (`client_secret_post`) remain in MCP server memory and must re-register on restart. Otherwise the MCP server would have to hash their secrets through the API. Pre-registered `mcpsettings.json` clients are unchanged.
- **Legacy deadline:** Default `2027-01-31` (`MCP_LEGACY_TOKEN_UNTIL`, ISO date). Until then, a DB attacker can inject a `hash_alg='sha256'` row—residual transition risk. Afterward legacy rows are unusable. On migration, replace `token_hint` fragments with `#abcd`; overwrite any remaining token suffixes at startup.
- **Timestamps in MACs:** Whole UTC seconds to avoid SQLite/PostgreSQL precision differences and false rejects.
- **Development:** Generate `data/mcp-token.key` once. Integration tests set a test value.
- **Operations:** IIS workflow requires and validates GitHub secret `MCP_TOKEN_KEY` before deployment. Docker Compose aborts without `MCP_TOKEN_KEY` (`:?`).

## Original Implementation Plan

1. Implement `Services/McpTokenProtector.cs` (HKDF, HMAC, MAC, fixed-time verification, key loading/fail closed), integrate with `McpLinkService` (create, verify, revoke, code exchange).
2. Add idempotent columns/tables in `Data/Schema.cs`; migrate on use and update `docs/database-schema.md`.
3. Update `AppConfig` (`MCP_TOKEN_KEY` required, `MCP_TOKEN_KEY_PREVIOUS`, `MCP_LEGACY_TOKEN_UNTIL`), root and deployment `.env.example`, both Compose files, `deploy/iis/web.env` and workflow, deployment/database/architecture docs, Help and `AGENTS.md`.
3a. Update MCP `ProprietaryOAuthService` to load/store clients through service-key API endpoints and test restart.
4. Integration/unit tests: keyed HMAC instead of SHA-256; **attack cases** via direct DB access (insert rows, change user IDs, extend expiration, clear revocation, swap hashes, replay/concurrent code exchange—reject all), key rotation, legacy migration/deadline, missing key fails startup, no token/hash in logs/responses.

## Guardrails for Changes

- Store tokens, codes, and client secrets **only as HMACs** under a secret kept outside the DB. Never store plaintext in database, logs, audit, errors, or responses.
- Verify row MAC **before** all other checks using fixed-time comparison; recalculate it whenever a protected field changes.
- Add new authorization-, identity-, or lifetime-affecting fields to the MAC.
- Never back up `MCP_TOKEN_KEY` together with database backups or commit it.

## Affected Code

`Services/McpLinkService.cs`, new `Services/McpTokenProtector.cs`, `Data/Schema.cs`, `Infrastructure/AppConfig.cs`, `Api/ApiEndpoints.cs` (`/mcp/link/*`), MCP `ProprietaryOAuthService.cs`, tests and deployment files.
