# Local Development

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Optional Docker (for full PostgreSQL operation or container tests)
- Optional VS Code with C# Dev Kit (see `.vscode/extensions.json`)

## 1. Configuration

```bash
cp .env.example .env
```

A supplied local `.env`, when present, may include working Entra ID settings and an OpenAI key placeholder. Basic UI/API tests can run without further changes: development login is used when `AUTH_MODE=auto` and OIDC settings are incomplete. The default `local-dev-user` is an **administrator**. Additional users can log in via `http://localhost:5000/auth/login?devUser=anna`. **Never use development login in production**; set `AUTH_MODE=oidc` and see [review F1](reviews/2026-10-24-mehrbenutzer-pruefbericht.en.md).

## 2. Start

```bash
cd src/LearnCards.Web
dotnet run
```

Open `http://localhost:5000`. The first run automatically creates `src/LearnCards.Web/data/learncards.db` (SQLite); schema initialization runs at startup.

## 2a. Launch from VS Code (Recommended)

Four launch options are defined in `.vscode/launch.json`:

| Selection | Starts | Database |
|---|---|---|
| **App (SQLite)** | Web (`http://localhost:5000`) + MCP server | SQLite file, no Docker |
| **App (PostgreSQL Docker)** | Web + MCP server | PostgreSQL 16 in `learncards-dev-db` |
| App (…) + MCP Health | As above, additionally opens `http://127.0.0.1:8787/health` | |
| Web (SQLite) / Web (PostgreSQL Docker) / MCP Server (local) | Individual processes | |

The PostgreSQL option first starts [docker-compose.dev.yml](../docker-compose.dev.yml) using `docker compose up -d --wait`, then builds. Container listens only at `127.0.0.1:55432` (rather than 5432, avoiding conflicts), with credentials `learncards` / `learncards`, database `learncards`. The application creates its schema on startup.

Useful VS Code tasks (Command Palette → “Tasks: Run Task”):

- `dev: postgres up` / `dev: postgres down` / `dev: postgres reset (delete data)`
- `dev: postgres psql` — interactive container SQL shell
- `test: integration (SQLite)` / `test: integration (PostgreSQL Docker)` — integration tests ([testing.md](testing.en.md)). PostgreSQL tests recreate disposable database `learncards_test`; they do not affect development data.

**MCP note:** Shared `MCP_API_KEY` (launch default `change-me-mcp-key`) is only a technical service key and grants no data access. Users must authenticate individually (section 5, [ADR 0013](adr/0013-mcp-benutzergebundene-anmeldung.en.md)).

## 3. Test the MCP Server Locally

```bash
cd src/LearnCards.McpServer
dotnet run
```

The server reads `LEARNCARDS_API_URL` (default `http://localhost:5000/api`) from `.env` and communicates with MCP clients (e.g., Claude Desktop) via stdio. It acts **on behalf of one user**: create a personal token under `/profile` → “MCP Connections” (shown only once) and provide it through `LEARNCARDS_MCP_TOKEN`. Example Claude Desktop `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "learncards": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/src/LearnCards.McpServer"],
      "env": {
        "LEARNCARDS_API_URL": "http://localhost:5000/api",
        "LEARNCARDS_MCP_TOKEN": "lcm_…"
      }
    }
  }
}
```

For production use, prefer `dotnet publish` and run the generated `LearnCards.McpServer.dll`, or use the Docker image (`--profile mcp`).

An stdio MCP server should normally **not** run as a permanent background service; the MCP client starts it when needed. The IIS pipeline creates `learncards-mcp.cmd` for this purpose.

### MCP Server Environment Variables

In addition to `mcpsettings.json` (path via `LEARNCARDS_MCP_CONFIG`), these variables override configuration:

| Variable | Effect |
|---|---|
| `LEARNCARDS_MCP_CONFIG` | Path to `mcpsettings.json` (otherwise searched in working directory, parents, and next to executable) |
| `LEARNCARDS_API_URL` | Base Web API URL, e.g., `http://localhost:5000/api` |
| `LEARNCARDS_MCP_API_KEY` / `MCP_API_KEY` | Service key (code exchange and schema only; no data access) |
| `LEARNCARDS_MCP_TOKEN` | Personal Learny token for stdio ([ADR 0013](adr/0013-mcp-benutzergebundene-anmeldung.en.md)) |
| `LEARNCARDS_WEB_PUBLIC_URL` | Public Web URL for OAuth browser login |
| `LEARNCARDS_MCP_STDIO` / `LEARNCARDS_MCP_HTTP` | `true`/`false`: enable/disable transports |
| `LEARNCARDS_MCP_HTTP_URLS` | Semicolon-separated HTTP listeners (enables HTTP), e.g., `http://127.0.0.1:8787` |

## 4. MCP Server as a Standalone HTTP Service

1. Copy `src/LearnCards.McpServer/mcpsettings.example.json` to `mcpsettings.json`.
2. Set `transports.http.enabled=true`.
3. For HTTP only, set `transports.stdio.enabled=false`.
4. For user-bound login, set `oauth.enabled=true` and `oauth.issuer` to the public MCP URL (`signingKey`/`tokenLifetimeMinutes` are no longer used).
5. Start:

```bash
cd src/LearnCards.McpServer
dotnet run
```

Default endpoints:

- `GET /health`, `POST /mcp`, `GET /metadata`, `GET /schema/import/cards`
- With OAuth enabled: `GET /.well-known/oauth-authorization-server`, `POST /oauth/token`, `POST /oauth/register`, `GET /oauth/authorize`, `GET /oauth/callback`

## 5. User-specific MCP Login

Each user can access only their own modules/cards via MCP ([ADR 0013](adr/0013-mcp-benutzergebundene-anmeldung.en.md)).

**HTTP with OAuth (VS Code, MCP Inspector, Claude):**

1. Start Web and MCP (VS Code “App (SQLite)” or “App (PostgreSQL Docker)”). Launch profiles set `MCP_PUBLIC_URL=http://127.0.0.1:8787` and enable OAuth in `.vscode/mcpsettings.debug.json`.
2. Configure `http://127.0.0.1:8787/mcp` in the MCP client. It registers, opens the Learny login (Entra ID or development login), and requests consent.
3. After “Allow,” the client acts as the authenticated user. Verify with `whoami`. Revoke under `/profile` → MCP Connections.
4. Under `AUTH_MODE=oidc`, configure `MCP_PUBLIC_URL`; development mode defaults to `http://127.0.0.1:8787`. When the web UI is not `http://localhost:5000`, configure `LEARNCARDS_WEB_PUBLIC_URL`.

**MCP Inspector:** Run `npx @modelcontextprotocol/inspector`, select Streamable HTTP/HTTP transport, use `http://127.0.0.1:8787/mcp`, choose OAuth and complete login. Alternatively send `Authorization: Bearer <personal token>`.

**stdio:** Generate a personal token under `/profile` and set `LEARNCARDS_MCP_TOKEN` (section 3).

The `get_import_schema` tool always retrieves the current card import schema from the server, so an LLM never needs to guess its fields.

## 6. Use Local PostgreSQL Instead of SQLite

Use the development database on port **55432**:

```bash
docker compose -f docker-compose.dev.yml up -d --wait
```

In `.env` (or use the VS Code “App (PostgreSQL Docker)” profile):

```text
DB_PROVIDER=postgres
POSTGRES_HOST=127.0.0.1
POSTGRES_PORT=55432
POSTGRES_USER=learncards
POSTGRES_PASSWORD=learncards
POSTGRES_DB=learncards
POSTGRES_SSLMODE=disable
```

## 7. Tests and Smoke Checks

```bash
curl http://localhost:5000/health
curl -b cookies.txt http://localhost:5000/api/modules/
```

Without login, `/api/*` returns 401. Log in through `/auth/login` in a browser, or send a personal MCP Bearer token (data routes only). `X-MCP-Key` **does not grant data access**. See [testing.md](testing.en.md) for automated tests.

## 7a. Try Sharing and Global Modules

Use two browser sessions (regular + private browsing), logging in as `?devUser=anna` and `?devUser=ben`; `local-dev-user` is an admin.

1. As *anna*, import a module; choose **Share module**, find *ben*, and grant access.
2. As *ben*, study under **Shared with me** and complete a quiz; Anna cannot see Ben's results.
3. As *anna*, **Submit for global publication**; as admin open `/admin` → publication submissions, review the staged copy, and **Publish**.
4. Under **Global modules**, everyone may study. Admins and auditors see minimized aggregate/detail results in the global results view.

See [ADR 0015](adr/0015-freigaben-globale-lernmodule.en.md).

## 8. Import Demo JSON

The `demoodule/` folder contains ready-to-import examples:

- `demoodule/kvno_seed_module.json` — extensive Azure/security example
- `demoodule/demo.json` — compact PRINCE2 project-management example

From the home page:

1. Start the web app.
2. Log in; imports enter **your private collection**.
3. Select **JSON Import**.
4. Pick a file under `demoodule/`.
5. Review analysis and confirm import.

## Known Differences from the Original Python Version

See [migration-notes.en.md](migration-notes.en.md).

## Local Operating Mode (ADR 0026)

Under `dotnet run`/VS Code, `ASPNETCORE_ENVIRONMENT=Development`; without OIDC setup, `AUTH_MODE=auto` uses development login. Starting differently (e.g., `dotnet LearnCards.Web.dll`, container) uses `Production`: then explicitly configure `AUTH_MODE=dev` or startup fails (see ADR 0026).

## Local MCP_TOKEN_KEY (ADR 0025)

`MCP_TOKEN_KEY` (Base64, ≥32 bytes) is required. Under `dotnet run` (`Development`), a missing key is generated once as `data/mcp-token.key` and reused. For `Production` processes/containers, generate and configure your own key using `openssl rand -base64 32`. Never commit `data/mcp-token.key` (the directory is ignored).
