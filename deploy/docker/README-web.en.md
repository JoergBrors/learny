# Learny — `joergb23/learny-web`

**Learny** (LearnCards) is a self-hosted learning platform for flashcards and learning modules, providing its web UI and REST API in **one** container.

- Source, documentation, issues: <https://github.com/JoergBrors/learny>
- Full Compose installation instructions, `.env.example`, variable table: <https://github.com/JoergBrors/learny/tree/main/deploy/docker> (also available as `learny-docker-<version>.zip` attached to [GitHub releases](https://github.com/JoergBrors/learny/releases))
- Companion MCP server: [`joergb23/learny-mcp`](https://hub.docker.com/r/joergb23/learny-mcp)

## Features

- Learning modules and flashcards (text, images, rich text), slide mode, JSON import/export
- Card and module quizzes, question pool, configuration, server-side grading
- AI chat, quiz generation/grading through an OpenAI-compatible API—**optional**, with a clearly labeled offline fallback when no key exists
- Multi-user Microsoft Entra ID (OIDC) login; user/admin/auditor roles; private, explicitly shared, or globally published modules
- Signed **Open Badges 3.0** certificates, optionally anchored on Polygon using only hashes (no personal data on-chain)
- Storage in database, local files, Azure Blob, S3, or SharePoint
- Optional MCP server for AI clients

## Image

| | |
|---|---|
| Platforms | `linux/amd64`, `linux/arm64` |
| Tags | `1.2.3` (pinned version), `latest` (latest stable); prereleases use their own version tag only |
| Base | `mcr.microsoft.com/dotnet/aspnet:10.0` (Ubuntu), multistage build, no SDK in image |
| User | Nonprivileged UID 1654 |
| Port | 8080 HTTP (HTTPS termination via reverse proxy) |
| Volume | `/app/data` for Data Protection, badge signing keys, local images, optional SQLite |
| Health check | `GET /health` |
| Secrets | None baked into image; use environment variables |

## Architecture

Blazor Web App (server interactivity), REST API under `/api`, database abstraction supporting **PostgreSQL** (recommended for production) and SQLite (local/small deployments). One deployable unit; MCP is a separate API client.

## Quick Start with Docker Compose

```bash
mkdir learny && cd learny
# Place docker-compose.yml and .env.example from the release package here
cp .env.example .env && chmod 600 .env     # Set POSTGRES_PASSWORD and OIDC_*
docker compose pull
docker compose up -d
docker compose ps                           # All services "healthy"
```

UI: `http://localhost:8080` (bound locally only). Add an HTTPS reverse proxy for external use.

Run a single container without Compose (PostgreSQL must already be running):

```bash
docker run -d --name learny-web -p 127.0.0.1:8080:8080 \
  -e DB_PROVIDER=postgres -e POSTGRES_HOST=<host> -e POSTGRES_DB=learncards \
  -e POSTGRES_USER=learncards -e POSTGRES_PASSWORD=<password> \
  -e AUTH_MODE=oidc -e OIDC_ISSUER=https://login.microsoftonline.com/<tenant>/v2.0 \
  -e OIDC_CLIENT_ID=<client-id> -e OIDC_CLIENT_SECRET=<secret> \
  -e PUBLIC_BASE_URL=https://learny.example.org \
  -v learny_data:/app/data joergb23/learny-web:latest
```

## Important Environment Variables

See the full list in the [installation guide](https://github.com/JoergBrors/learny/blob/main/deploy/docker/README.md#variablen).

| Variable | Required | Default | Meaning |
|---|---|---|---|
| `DB_PROVIDER` | No | `auto` (without `POSTGRES_HOST` → SQLite) | `postgres` or `sqlite` |
| `POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | For PostgreSQL | Port 5432 | PostgreSQL connection |
| `POSTGRES_SSLMODE` | No | `prefer` | e.g., `disable` on trusted internal networks, `require` otherwise |
| `AUTH_MODE` | **Set explicitly** | `auto` | `oidc` production; `dev` passwordless local only |
| `OIDC_ISSUER`, `OIDC_CLIENT_ID`, `OIDC_CLIENT_SECRET` | With OIDC | — | Microsoft Entra ID |
| `OIDC_AUDIENCE`, `OIDC_WEB_SCOPE`, `OIDC_ROLE_MAP`, `ADMIN_SUBJECTS` | No | — | Audience, scopes, role mapping, admin bootstrap |
| `PUBLIC_BASE_URL` | Production | Empty | Public base URL for badge issuer/verification links |
| `OPENAI_API_KEY`, `OPENAI_MODEL`, `OPENAI_BASE_URL` | No | — / `gpt-4o-mini` / OpenAI | AI functions |
| `MCP_TOKEN_KEY` | **Always** | — | Main key (Base64 ≥32 bytes, `openssl rand -base64 32`) protects MCP tokens/codes/OAuth clients in DB (ADR 0025); never store with DB backups; without it Learny will not start |
| `MCP_API_KEY`, `MCP_PUBLIC_URL`, `MCP_TOKEN_DAYS` | MCP | — / — / 30 | MCP integration |
| `BADGE_SIGNING_KEY_PEM` | No | Empty | Badge key; generated in volume when absent |
| `IMAGE_STORAGE_PROVIDER` | No | `database` | `database`, `local`, `azureblob`, `s3`, `sharepoint` and provider-specific `IMAGE_*` |
| `DATA_PROTECTION_DIR` | No | `data/keys` = `/app/data/keys` | Data Protection key storage; keep on volume |

> **Security (ADR 0026):** Always explicitly set `AUTH_MODE`. `dev` grants administrator login without password to anyone reaching the service. In production/container mode, `auto`, empty, or invalid values with incomplete OIDC credentials **abort startup**, rather than silently enabling development login. Explicit `dev` is intended only for local experiments and is logged as a warning. An explicitly set but unreadable `ENV_FILE` also prevents startup.

## PostgreSQL

Learny creates and migrates tables on startup (`CREATE IF NOT EXISTS`). Compose uses `postgres:16-alpine`, named volume `postgres_data`, and does not publish the database port. Use `POSTGRES_HOST`/`POSTGRES_*` for an external instance and `POSTGRES_SSLMODE=require` across public networks.

## Set Up Microsoft Entra ID (Summary)

1. Create app registration and Web redirect URI `https://<address>/auth/callback`.
2. Create client secret; set its value in `OIDC_CLIENT_SECRET`.
3. `OIDC_CLIENT_ID` = application ID; `OIDC_ISSUER` = `https://login.microsoftonline.com/<tenant-id>/v2.0`.
4. Optionally assign app role `Admin.All` for admin via `OIDC_ROLE_MAP`, or use `ADMIN_SUBJECTS` to bootstrap.

See [installation guide](https://github.com/JoergBrors/learny/blob/main/deploy/docker/README.md#microsoft-entra-id-einrichten).

## Data and Updates

- **Persistence:** PostgreSQL database volume plus `/app/data`. Container restarts and image updates preserve data; `docker compose down -v` deletes volumes.
- **Upgrade:** Set new `LEARNY_VERSION` (or use `latest`), then `docker compose pull && docker compose up -d`. Back up first (installation guide, “Backup” section).
- **Badge keys:** Back up `/app/data/badge-signing-key.pem`. Losing it invalidates verification of issued credentials. Existing installations fail startup when it is missing, rather than generating a replacement silently.

## MCP (Optional)

With [`joergb23/learny-mcp`](https://hub.docker.com/r/joergb23/learny-mcp) (Compose profile `mcp`), AI clients use Model Context Protocol. Web needs `MCP_API_KEY` (service key, no user-data access) and `MCP_PUBLIC_URL` (approved public OAuth callback URL). Users authenticate with their own tokens.

## Security Notes

- Use HTTPS via a reverse proxy. The app honors `X-Forwarded-*`; **do not expose the container's HTTP port directly to the internet**.
- Do not expose PostgreSQL; use strong random passwords and restrict `.env` to mode 600.
- Never put secrets in images/Compose; rotate expiring credentials such as Entra client secrets.
- User data is isolated; admins and auditors do not automatically see private modules.
- Image contains no database, keys, or credentials.

License: MIT · Source: <https://github.com/JoergBrors/learny>

## New: Modules and Topics as JSON (Since Version 0.1.11)

Modules (cards, images, quiz, config) and topics (learning paths from owned modules with module/final exams) can be fully exported/imported as `learny-module` and `learny-topic` JSON using stable `reference_id`/`module_ref`. Global modules must first be cloned into owned modules before topic inclusion. Imports create private modules/drafts and **never publish**. Files are limited to 30 MB (Kestrel default). Upgrade automatically adds `modules.reference_id` via an idempotent migration; no new settings. Schemas: `/api/schema/import/module`, `/api/schema/import/topic`. See [ADR 0028](https://github.com/JoergBrors/learny/blob/main/docs/adr/0028-themenmodule-json-und-mcp-verwaltung.md).
