# 0004 — `IDatabase`: SQLite Locally, PostgreSQL in Production

- **Status:** Accepted
- **Date:** 2026-10-24
- **Supplemented by:** [0016](0016-bilder-in-lernkarten.md) (`cards.images_json` column), [0011](0011-mehrbenutzerarchitektur.md) (ownership columns and migrations), [0013](0013-mcp-benutzergebundene-anmeldung.md) (MCP tables), [0015](0015-freigaben-globale-lernmodule.md) (sharing/publication tables and columns)

## Context

Local development must work without Docker; production (Docker, Azure) uses PostgreSQL. No ORM (see [0002](0002-keine-nuget-pakete.en.md)).

## Decision

- `IDatabase` exposes only `InitAsync`, `QueryAsync`, and `ExecuteAsync`, with bound `@name` parameters.
- Values are transferred **as text**; `DbValue` (`ToText`, `ToBool`, `ToInt`, etc.) and `CardRepository` perform conversion.
- `AppConfig` selects the provider via `DB_PROVIDER` (`auto` → SQLite when `POSTGRES_HOST` is absent; PostgreSQL otherwise).
- The schema lives in `Data/Schema.cs` (`CREATE TABLE/INDEX IF NOT EXISTS`) and is initialized at startup.
  Tables: `users`, `user_identities`, `user_roles`, `audit_log`, `mcp_links`, `mcp_link_codes`, `module_shares`, `module_submissions`, `modules`, `cards`, `user_preferences`, `user_card_states`, `chat_history`, `quiz_results` (details: [database-schema.md](../database-schema.md)).

## Consequences

- A single SQL dialect must work on both providers.
- There is no migration framework; schema changes must be idempotent and backward compatible.

## Guardrails for Changes

- **Always** parameterize SQL; never concatenate strings into queries.
- Every query must work with **both** SQLite and PostgreSQL (no provider-specific SQL in `CardRepository`).
- PostgreSQL is the reference implementation: design changes for it first, then ensure SQLite supports them. Schema overview: [database-schema.md](../database-schema.md).
- A new column in `CREATE TABLE IF NOT EXISTS` does not alter existing tables. Therefore: (1) add the column to `Schema.cs`, (2) migrate it in `PostgresDatabase.InitAsync` using `EnsureTableColumnAsync`/`EnsureCardColumnAsync`, (3) migrate it in `SqliteDatabase.InitAsync` using `EnsureColumn`, and (4) update `docs/database-schema.md`. New columns require a `DEFAULT` or `NULL`. Also update the model (`Domain/Models.cs`), mappings in `CardRepository`, and the card JSON/schema ([0006](0006-kanonisches-karten-json.md)).
- Upserts should be atomic (`INSERT … ON CONFLICT`); the current read-then-write approach can produce HTTP 500 under PostgreSQL concurrency (review finding F4; proposal in [0014](0014-sitzungs-und-betriebsmodus-haertung.md)).
- Do not delete data without an explicit request; prefer archiving cards (`archived`).

## Affected Code

- `src/LearnCards.Web/Data/*`, `Services/CardRepository.cs`, `Infrastructure/AppConfig.cs`
