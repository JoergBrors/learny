# Migration from the Original Python Implementation

This .NET 10 implementation fully reimplements the same application functionality (FastAPI + React + Python MCP server → Blazor Web App + integrated API + .NET MCP server). The data model, card JSON format, and API routes were intentionally kept compatible.

## What Stayed the Same

- Data model: `modules`, `cards`, `quiz_results` with equivalent fields
- Canonical card JSON (`module`, `category`, `term`, `question`, `definition`, `how_it_works`, `context`, `key_fact`, `chat_prompt`, `archived`, `sort_order`)
- API routes under `/api/*` (modules, cards, import, quiz, chat), preserving paths and snake_case JSON field names
- MCP tools (`add_card`, `import_cards`, `list_modules`, `list_cards`, `archive_card`, `restore_card`) with identical names/schemas
- Basic authentication approach: OIDC-protected API (since ADRs 0011–0013, user-bound MCP tokens replace shared `X-MCP-Key` data access)

## What Changed

| Area | Python original | .NET 10 implementation |
|---|---|---|
| Frontend | React 18 + Vite (separate SPA) | Blazor Web App, interactive server rendering (combined with API) |
| Backend | FastAPI + SQLAlchemy (async) | ASP.NET Core Minimal API + custom `IDatabase` abstraction |
| Local database | PostgreSQL via Docker (mandatory) | SQLite file; Docker not required |
| Production database | PostgreSQL (asyncpg) | PostgreSQL (native wire protocol v3, no ORM) |
| OIDC | `python-jose` against JWKS | Custom RS256 verification against JWKS (`System.Security.Cryptography`) |
| OpenAI | Python `openai` SDK | Custom `HttpClient` Chat Completions client |
| MCP server | Python (`mcp` package), separate process | .NET console application, custom stdio JSON-RPC |
| Reverse proxy | nginx required for the full stack | nginx optional (only TLS termination for Docker) |

## Further Development Since the Python Version

The port introduced **multi-user support**: modules/cards belong to individual users rather than being global, with roles, profiles, and user-bound MCP access ([ADR 0011](adr/0011-mehrbenutzerarchitektur.en.md), [0012](adr/0012-rollen-aus-entra-app-roles.en.md), [0013](adr/0013-mcp-benutzergebundene-anmeldung.en.md)). Migrating the old Python database thus produces **unassigned legacy content**, which an administrator assigns in `/admin`. The schema is no longer identical (see [database-schema.md](database-schema.md)).

## Why No NuGet Packages?

See [architecture.md](architecture.md#warum-ohne-nuget-pakete). In brief: reliable builds and deployments in restricted-network environments. The code architecture, especially `IDatabase`, permits a future switch to EF Core, Npgsql, or the official OpenAI SDK without changing API or UI.

## Migrating PostgreSQL Data (Python Version)

Core columns in `modules`, `cards`, and `quiz_results` are compatible. On first startup the application migrates the schema (ownership columns and new tables), so a dump/restore suffices:

```bash
pg_dump --data-only --table=modules --table=cards --table=quiz_results \
  -h <old-host> -U learncards learncards > data.sql
psql -h <new-host> -U learncards learncards < data.sql
```

Alternatively, export cards using `GET /api/cards/` from the old API and import through `POST /api/import/cards` or the MCP tool `import_cards`. Card JSON remains compatible; imported cards belong to the importing user.

**Note:** After restore, `modules` and `cards` have no owner; an administrator must assign them under `/admin`.
