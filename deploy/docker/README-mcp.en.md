# Learny MCP Server — `joergb23/learny-mcp`

The **Learny MCP server** connects AI clients (Claude Desktop and other MCP-capable applications) to a Learny installation through the [Model Context Protocol](https://modelcontextprotocol.io). Clients can create/read modules and cards, import learning content, and use quiz features.

- Source and documentation: <https://github.com/JoergBrors/learny>
- Compose installation instructions and variable table: <https://github.com/JoergBrors/learny/tree/main/deploy/docker>
- Web application: [`joergb23/learny-web`](https://hub.docker.com/r/joergb23/learny-web)

## Purpose and Architecture

The MCP server is an **independent API client** of Learny Web (never accesses the database directly). It supports:

- **HTTP transport:** `POST /mcp` for persistent operation behind a reverse proxy (Claude Web, remote clients).
- **stdio transport:** for clients that launch the container themselves.
- `GET /health`, `GET /metadata`, `GET /schema/import/cards` (public import schema) and OAuth endpoints (`/.well-known/…`, `/oauth/authorize`, `/oauth/token`, `/oauth/register`, `/oauth/callback`).

Platforms: `linux/amd64`, `linux/arm64`; tags `1.2.3`, `latest` (stable only); base `mcr.microsoft.com/dotnet/aspnet:10.0`; nonprivileged UID 1654; HTTP port 8787; no secrets embedded in the image.

## Requirements

1. A running **Learny Web installation** (`joergb23/learny-web`) reachable from MCP (inside Compose: `http://web:8080`).
2. Matching **service keys** on both sides (`MCP_API_KEY` in Web = `LEARNCARDS_MCP_API_KEY` in MCP).
3. **HTTPS reverse proxy** for external access (MCP listens using HTTP only and deliberately rejects HTTPS listener URLs).
4. Public URLs for Learny (e.g., `https://learny.example.org`) and MCP (e.g., `https://mcp.example.org`).

## Environment Variables (MCP Container)

| Variable | Required | Meaning |
|---|---|---|
| `LEARNCARDS_API_URL` | **Yes** | **Internal** Web API URL, e.g., `http://web:8080/api`, not the public URL; default `http://localhost:5000/api` |
| `LEARNCARDS_MCP_API_KEY` (or `MCP_API_KEY`) | **Yes** | Service key; **never** grants data access, only code exchange and public schema |
| `LEARNCARDS_WEB_PUBLIC_URL` | **Yes** (OAuth) | Public Learny UI URL for browser login/consent; defaults to API URL without `/api` |
| `LEARNCARDS_MCP_HTTP` | No | `true` enables HTTP; Compose sets it |
| `LEARNCARDS_MCP_STDIO` | No | `true`/`false`; stdio enabled by default, disable in HTTP mode |
| `LEARNCARDS_MCP_HTTP_URLS` | No | Semicolon-separated listeners, e.g., `http://0.0.0.0:8787`; **HTTP only** |
| `LEARNCARDS_MCP_CONFIG` | No | Path to `mcpsettings.json` (Compose mounts `/config/mcpsettings.json` read-only) |
| `LEARNCARDS_MCP_TOKEN` | stdio | Personal Learny token for stdio, generated in profile |

OAuth settings (`oauth.enabled`, `oauth.issuer`, `oauth.clients`) are in `mcpsettings.json` (template `mcpsettings.example.json`):

```json
{
  "serverName": "Learny MCP",
  "serverVersion": "2.1.0",
  "oauth": { "enabled": true, "issuer": "https://mcp.example.org", "audience": "learncards-mcp", "clients": [] }
}
```

`issuer` is the **public MCP URL**, without a trailing slash. The Web app must use the same URL in `MCP_PUBLIC_URL`; otherwise Learny rejects the callback after authentication.

## Docker Compose Example

Excerpt (full version in [deploy/docker](https://github.com/JoergBrors/learny/tree/main/deploy/docker)):

```yaml
services:
  mcp:
    image: joergb23/learny-mcp:latest
    restart: unless-stopped
    depends_on:
      web: { condition: service_healthy }
    environment:
      LEARNCARDS_API_URL: http://web:8080/api
      LEARNCARDS_WEB_PUBLIC_URL: https://learny.example.org
      LEARNCARDS_MCP_API_KEY: ${MCP_API_KEY}
      LEARNCARDS_MCP_HTTP: "true"
      LEARNCARDS_MCP_STDIO: "false"
      LEARNCARDS_MCP_HTTP_URLS: http://0.0.0.0:8787
      LEARNCARDS_MCP_CONFIG: /config/mcpsettings.json
    volumes:
      - ./mcpsettings.json:/config/mcpsettings.json:ro
    ports:
      - "127.0.0.1:8787:8787"     # Reverse proxy forwards https://mcp.example.org here
```

Web additionally needs mandatory `MCP_TOKEN_KEY` (protects database-stored tokens and OAuth clients), matching `MCP_API_KEY`, `MCP_PUBLIC_URL=https://mcp.example.org`, and optionally `MCP_TOKEN_DAYS` (default 30).

stdio without Compose (container launched by client):

```bash
docker run -i --rm \
  -e LEARNCARDS_API_URL=https://learny.example.org/api \
  -e LEARNCARDS_MCP_API_KEY=<service-key> -e LEARNCARDS_MCP_TOKEN=<personal-token> \
  joergb23/learny-mcp:latest
```

## Authentication: OAuth and Personal Tokens

- **Data access always uses an individual user's token** (user-bound and revocable). MCP stores no user data.
- **OAuth (HTTP):** Client discovers `/.well-known/oauth-protected-resource` or `/.well-known/oauth-authorization-server`, then sends users to `/oauth/authorize`. Users log into **Learny**, consent under `/mcp/connect`, and the client exchanges the code for a Learny-issued access token, valid for `MCP_TOKEN_DAYS`.
- **Personal token (stdio/manual clients):** Generate under the Learny profile, set `LEARNCARDS_MCP_TOKEN` or send `Authorization: Bearer lcm_…`. Revoke through profile.
- **Service key vs. user token:** `MCP_API_KEY` authenticates the MCP server to Learny only for code exchange and public schema. It never accesses user modules/cards and must never be shared with clients. User tokens permit only that user's data and MCP-allowed API routes (not administration, sharing, or certificates).

## Public URLs and HTTPS

- The container communicates internally by HTTP. The reverse proxy (nginx, Caddy, Traefik, IIS) **terminates TLS**, forwarding `https://mcp.example.org` to `127.0.0.1:8787` with buffering disabled (`proxy_buffering off`).
- `oauth.issuer` (JSON), `MCP_PUBLIC_URL` (Web), and client-configured MCP URL must refer to the **same public HTTPS URL**.
- `LEARNCARDS_WEB_PUBLIC_URL` must be reachable by the user's browser, not internal `http://web:8080`.

## Troubleshooting

| Symptom | Cause / Remedy |
|---|---|
| `GET /health` does not respond | Container is in stdio mode. Set `LEARNCARDS_MCP_HTTP=true`, `LEARNCARDS_MCP_HTTP_URLS=http://0.0.0.0:8787` |
| Startup error: “HTTPS listener URLs are not supported” | Use only `http://` listeners; terminate TLS at proxy |
| HTTP 401 on `/mcp` | Missing/invalid user token. Repeat OAuth login or regenerate token |
| Login callback fails | Web `MCP_PUBLIC_URL` does not match public MCP URL or is empty (production login disabled in that case) |
| Claude no longer needs to re-register after restart | Public OAuth clients are encrypted in Learny DB (`mcp_oauth_clients` using `MCP_TOKEN_KEY`). Losing the key invalidates stored clients/tokens; reconnect |
| Learny API call fails | Wrong `LEARNCARDS_API_URL` (inside Compose `http://web:8080/api`) or mismatched service keys |
| `mcpsettings.json` becomes a directory | File was absent when the `mcp` profile started. Copy `mcpsettings.example.json` to `mcpsettings.json`, remove accidental directory and restart |
| Browser redirects to `http://web:8080` | Set `LEARNCARDS_WEB_PUBLIC_URL` to public Learny URL |

Logs: `docker compose logs -f mcp`. Tokens/keys are not logged.

License: MIT · Source: <https://github.com/JoergBrors/learny>

## Module and Topic Tools (Since Version 0.1.11)

Beyond card tools (`get_import_schema`, `import_cards`, `list_modules`, `list_cards`, etc.), the server offers user-scoped tools:

- Read: `list_topics`, `get_topic`, `get_topic_exam_structure`.
- Draft topics as JSON: `get_topic_import_schema`, `create_topic`, `import_topic`, `update_topic` (replaces draft only), `export_topic`.
- Cards: `get_card` (full card, quiz and images), `update_card` (owned private cards only; omitted `images` means retain, `[]` means remove; optional `expected_updated_at`), `export_cards` (canonical JSON in `offset`/`limit` batches with `has_more`/`next_offset`; import again through `import_cards`).
- Modules as JSON: `get_module_import_schema`, `export_module`, `import_module`, `clone_global_module`.

Topics refer to **owned** modules only using stable `module_ref` = `reference_id`. Publication, global approvals, other-user sharing/assignments, and administration are not exposed through MCP. `MCP_API_KEY` never grants data access. See [ADR 0028](https://github.com/JoergBrors/learny/blob/main/docs/adr/0028-themenmodule-json-und-mcp-verwaltung.md).
