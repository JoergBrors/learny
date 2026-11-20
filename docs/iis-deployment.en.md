# Deploy to Windows Server IIS Using GitHub Actions

This method targets a **Windows Server running IIS**. A GitHub Actions workflow triggers on every new Git tag and deploys `src/LearnCards.Web` using a **self-hosted Windows runner** installed directly on the destination server. It also publishes `src/LearnCards.McpServer` into a separate directory.

## Overview

- Trigger: push a Git tag such as `v1.0.0`.
- Build: GitHub-hosted `windows-latest`.
- Deployment: self-hosted runner with labels `self-hosted`, `Windows`, `X64`.
- Destination: IIS site and application pool.
- Also publish MCP server into its own folder and generate a launch script and Claude Desktop configuration fragment for client-managed startup.
- Optional standalone HTTP MCP mode with `mcpsettings.json`.
- Workflow: `.github/workflows/deploy-iis-on-tag.yml`.

## Windows Server Prerequisites

Before the first deployment:

1. Install and run a **GitHub Actions self-hosted runner** on the server.
2. Ensure labels `self-hosted`, `Windows`, and `X64`.
3. Grant enough permissions to configure IIS features, site, and app pool.

The action attempts to install IIS if missing and reuses existing settings. It can also install an absent **ASP.NET Core Hosting Bundle** if a download URL is configured.

## Repository Templates

Only templates/placeholders are committed:

- `deploy/iis/deployment.json`: non-sensitive deployment settings.
- `deploy/iis/web.env`: structure/defaults/placeholders, rendered using GitHub Secrets and deployed as `.env`.
- `deploy/iis/mcpsettings.json`: MCP HTTP/OAuth placeholders, rendered and placed in MCP directory.

Real values come from GitHub **Variables** and **Secrets**, never source files.

## Optional IIS Server Configuration

Web reads runtime values from deployed `.env`; MCP reads `mcpsettings.json`. Sensitive inputs come from GitHub Secrets. The pipeline substitutes them for placeholders.

## GitHub Variables

New optional `IIS_CONFIG_ROOT`: base directory for `.env` and badge keys **outside web root**, default `%ProgramData%\LearnCards\<IIS_SITE_NAME>`. Must not reside under web root.

Under GitHub → Settings → Secrets and variables → Actions → Variables, configure:

```text
IIS_SITE_NAME
IIS_APP_POOL
IIS_PHYSICAL_PATH
IIS_BINDING_URL
IIS_PORT
IIS_HOST_HEADER
ASPNETCORE_HOSTING_BUNDLE_URL
MCP_PHYSICAL_PATH
MCP_SERVICE_NAME
MCP_HTTP_HEALTH_URL
APP_DOMAIN
DEBUG
DB_PROVIDER
SQLITE_PATH
POSTGRES_HOST
POSTGRES_PORT
POSTGRES_USER
POSTGRES_DB
POSTGRES_SSLMODE
AUTH_MODE
OIDC_ISSUER
OIDC_CLIENT_ID
OIDC_AUDIENCE
OIDC_WEB_SCOPE
OPENAI_MODEL
OPENAI_BASE_URL
DEV_USER_NAME
DEV_USER_EMAIL
MCP_SERVER_NAME
MCP_SERVER_VERSION
MCP_UPSTREAM_API_BASE_URL
MCP_STDIO_ENABLED
MCP_HTTP_ENABLED
MCP_HTTP_URLS_JSON
MCP_OAUTH_ENABLED
MCP_OAUTH_ISSUER
MCP_OAUTH_AUDIENCE
MCP_OAUTH_TOKEN_LIFETIME_MINUTES
MCP_OAUTH_CLIENT_ID
MCP_OAUTH_CLIENT_SCOPES_JSON
```

## Configuration Values Introduced by ADRs 0011–0013 (Originally Not Rendered)

The workflow substitutes only existing placeholders in `deploy/iis/web.env`; `.env` is overwritten at every deployment. Therefore add these as **IIS environment variables** (app pool/`web.config` override `.env`) or extend the workflow:

- `AUTH_MODE=oidc` explicitly for production. Historically `auto` fell back to development login without OIDC ([review F1](reviews/2026-10-24-mehrbenutzer-pruefbericht.en.md)); see later ADR 0026 for fail-closed behavior.
- `ADMIN_SUBJECTS`: Entra `sub` of initial admin; alternatively map `Admin.All` app role ([ADR 0012](adr/0012-rollen-aus-entra-app-roles.en.md)).
- `MCP_PUBLIC_URL`: publicly accessible MCP OAuth callback allowlist; required for MCP login.
- Optionally `OIDC_ROLE_MAP`, `MCP_TOKEN_DAYS`.
- MCP: `LEARNCARDS_WEB_PUBLIC_URL` for browser login; `LEARNCARDS_MCP_TOKEN` for stdio clients.
- **Image storage** (ADRs [0017](adr/0017-konfigurierbare-bildspeicher.en.md)/[0018](adr/0018-zentraler-image-proxy-und-storage-zugriff.en.md)): default `database` needs no action. For local provider, set `IMAGE_STORAGE_PROVIDER=local`, `IMAGE_LOCAL_PATH` **outside** overwritten deploy directory `IIS_PHYSICAL_PATH`, writable only by app-pool identity (e.g., `IIS AppPool\LearnCards`). For Azure Blob/S3/SharePoint configure `IMAGE_*` as environment secrets (see `.env.example`), `IMAGE_CACHE_PATH` also outside the deployment directory, and allow outgoing HTTPS storage traffic through firewall.

## GitHub Secrets

Under GitHub → Settings → Secrets and variables → Actions → Secrets:

- **`MCP_API_KEY` (required):** Matching service key for MCP/Web. Never grants user-data access; users employ personal tokens ([ADR 0013](adr/0013-mcp-benutzergebundene-anmeldung.en.md)).
- **`MCP_TOKEN_KEY` (required, ADR 0025):** Base64 ≥32-byte primary key protecting database MCP tokens, codes, OAuth clients. Generate `openssl rand -base64 32` or PowerShell `[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))`. Workflow checks format/length before deploying, renders to secure `.env` only, never prints it. Store separately from DB backups; losing it invalidates all MCP links. Rotate with new `MCP_TOKEN_KEY`, previous one in IIS `MCP_TOKEN_KEY_PREVIOUS`.
- `MCP_OAUTH_SIGNING_KEY`: Workflow still requires it while OAuth enabled, although MCP no longer uses it (Web issues tokens). Placeholder is sufficient; removing obsolete requirement is recommended.
- `MCP_OAUTH_CLIENT_SECRET`: Only for preregistered OAuth confidential clients, if used; dynamic registration is usual, `client_credentials` unsupported.
- `POSTGRES_PASSWORD`: Required only when using PostgreSQL.
- `OIDC_CLIENT_SECRET`: Required only with OIDC.
- `OPENAI_API_KEY`: Optional, for live AI.

## Deployment Sequence

For a new tag:

1. `dotnet publish` produces Web release.
2. `dotnet publish` produces separate MCP release.
3. Upload both as ZIP workflow artifacts.
4. Self-hosted runner downloads packages.
5. Validate required runner variables, IIS installation/availability, PowerShell `WebAdministration`, ASP.NET Core Hosting Bundle, .NET 10 ASP.NET Core runtime, and site/app pool/directory access.
6. Mirror Web package to `IIS_PHYSICAL_PATH` using `robocopy /MIR /XD data logs`, preserving `data` and `logs`.
7. Mirror MCP package when `MCP_PHYSICAL_PATH` configured.
8. Create `learncards-mcp.cmd`, `claude-desktop.learncards.json`, and `mcpsettings.example.json` under MCP.
9. Render `deploy/iis/web.env` → `.env`, and `deploy/iis/mcpsettings.json` → `mcpsettings.json` from Variables/Secrets.
10. Store rendered `.env` **outside web root** at `<IIS_CONFIG_ROOT>\config\.env` (default `%ProgramData%\LearnCards\<Site>`); store MCP configuration at `<MCP_PHYSICAL_PATH>\mcpsettings.json`. If repository MCP file missing, use existing file or example as fallback.
11. If `transports.http.enabled=true`, configure/update HTTP MCP as a **scheduled task** started at system boot under SYSTEM; if Windows service `MCP_SERVICE_NAME` already exists, restart it instead (never create a new Windows service). See restart section.
12. Restart app pool/site.
13. Optionally check Web `/health` if `IIS_BINDING_URL` set.
14. Optionally check MCP HTTP health when `MCP_HTTP_HEALTH_URL` set.

## Restart MCP at Deployment (ADR 0029)

The MCP HTTP server is a **separate process**, not an IIS app pool. When `MCP_PHYSICAL_PATH` is set:

1. **Detect mode:** Existing Windows service `MCP_SERVICE_NAME` (default `LearnCardsMcpServer`) or scheduled task; log state.
2. **Controlled shutdown:** Stop service (up to 60 s), stop scheduled task, terminate remaining `LearnCards.McpServer.exe` processes from the target directory. **Wait up to 45 s until no MCP process remains**; otherwise abort **without replacing files**.
3. **Update files:** `robocopy /MIR`; back up and restore `mcpsettings.json` from secure rendering/existing version; never log secrets.
4. **Start:** If service exists, `Start-Service` and wait for `Running` and a process. Otherwise create/run scheduled task, wait up to 45 s for process. HTTP disabled (stdio only) → start nothing and remove scheduled task. Never create/start a nonexistent Windows service; ignore with warning an existing same-named service whose executable path does not reside under `MCP_PHYSICAL_PATH`.
5. **Health:** If `MCP_HTTP_HEALTH_URL` configured, try up to 12 times (5 s apart) and verify OAuth metadata. **Failure to start/respond fails deployment**.
6. **Idempotent:** Rerunning tag/`workflow_dispatch` shuts down, updates, restarts.

**Recovery/rollback:** Previous MCP directory is backed up in runner temp at `backup-mcp\<timestamp>`. On failure stop task/service, mirror backup back using `robocopy <Backup> <MCP_PHYSICAL_PATH> /MIR`, check `mcpsettings.json`, start task/service (`schtasks /Run /TN <Name>`). External stdio clients are not restarted.

## Tag Trigger

Example only; **creating/pushing tags triggers production deployment**, so never do it without explicit authorization:

```bash
git tag v1.0.0
git push origin v1.0.0
```

## Notes

- Action creates IIS site/app pool if missing and attempts Windows IIS feature installation.
- App pool is configured **No Managed Code** (`managedRuntimeVersion=""`).
- Local backup is created in runner temp before file copying.
- Web and MCP are published framework-dependent; .NET runtime/Hosting Bundle must be present or installed.
- In stdio-only mode, clients manage MCP startup, without a service.
- In HTTP mode, the pipeline creates/updates task (or uses existing service) and restarts it every deployment.
- A generated launch script allows Claude Desktop to start stdio MCP automatically.

## MCP Without Manual Startup

Under `MCP_PHYSICAL_PATH`:

- `learncards-mcp.cmd`
- `claude-desktop.learncards.json`
- `mcpsettings.example.json`

The `learncards-mcp.cmd` script sets `LEARNCARDS_API_URL` from `IIS_BINDING_URL/api` if missing, maps `MCP_API_KEY` to `LEARNCARDS_MCP_API_KEY` if necessary, and starts `LearnCards.McpServer.exe`. The user must also supply **their own** `LEARNCARDS_MCP_TOKEN` in the client configuration. Import the Claude Desktop JSON snippet into the local client; Claude will launch the process on demand.

## Optional HTTP MCP with Per-user OAuth

For an independent HTTP MCP endpoint:

1. Copy `mcpsettings.example.json` to `mcpsettings.json`.
2. Enable `transports.http.enabled`.
3. Disable `transports.stdio.enabled` for HTTP-only service.
4. Enable `oauth.enabled` and set `oauth.issuer` to **public MCP URL** (`signingKey`/`clients` are no longer required, Learny performs login; [ADR 0013](adr/0013-mcp-benutzergebundene-anmeldung.en.md)).
5. Place configuration under `MCP_PHYSICAL_PATH`.
6. Optionally define `MCP_SERVICE_NAME`, `MCP_HTTP_HEALTH_URL` as GitHub Variables.
7. Pipeline starts/restarts HTTP MCP automatically as a scheduled task or existing Windows service (see above).

**Important:** Rendered repository `deploy/iis/mcpsettings.json` intentionally overwrites local MCP configuration at every deployment.

For Windows/IIS, start with `src/LearnCards.McpServer/mcpsettings.iis.example.json`:

```text
upstreamApi.baseUrl        = http://localhost/api
transports.stdio.enabled   = false
transports.http.enabled    = true
transports.http.urls       = ["http://127.0.0.1:8787"]
oauth.enabled              = true
```

Then set production `upstreamApi.apiKey` (service key), `oauth.issuer` (must match Web `MCP_PUBLIC_URL`), optionally `upstreamApi.webPublicUrl`/`LEARNCARDS_WEB_PUBLIC_URL`.

HTTP endpoints:

- `POST /mcp`, `GET /metadata`, `GET /schema/import/cards`
- `GET /.well-known/oauth-authorization-server`
- `GET /oauth/authorize`, `POST /oauth/register`, `GET /oauth/callback`, `POST /oauth/token`

With HTTP disabled, pipeline removes the scheduled task and leaves MCP client-managed stdio (historical text mentioned removing Windows service; follow ADR 0029's rule not to alter an unrelated service).

## Secrets and Badge Keys (ADR 0023)

Locations are **outside web root** and unaffected by mirroring; `IIS_CONFIG_ROOT` defaults to `%ProgramData%\LearnCards\<IIS_SITE_NAME>`:

| Item | Path | Permissions |
|---|---|---|
| Production `.env` | `<root>\config\.env`, found via app-pool `ENV_FILE` | SYSTEM, admins, deploy account: full; app pool: **read only** |
| Private badge key | `<root>\keys\badge-signing-key.pem` (`BADGE_SIGNING_KEY_FILE` in `.env`) | As above; app pool read-only after key+marker initialization |
| Initialization marker | `<root>\keys\badge-signing-key.pem.initialized` (key ID/time only, no secret) | Same as key |

- Remove NTFS inheritance; deny access for `Users`/`Everyone`/`Authenticated Users` (deployment checks and aborts if misconfigured).
- In Web root app pool receives read/execute only; `data`, `logs` are writable. No administrator rights for app pool.
- On upgrade, rendered empty `.env` settings do not overwrite older nonempty settings. Old `.env` in web root is migrated then removed by mirroring.
- Workflow logs no secret values, only paths/status.

**App pool:** `ApplicationPoolIdentity`, **Load User Profile = True** (set by deployment), `ENV_FILE` environment variable. Request Filtering via `web.config` denies `.env`, `.pem`, `.key`, `.initialized`, `.db` (`.config` already denied by IIS) plus URL segments `data`, `logs`, `keys`.

**First initialization:** If key and marker both absent, generate ECDSA P-256 signing key into a temporary file in key directory, then atomically move without overwrite (one winner across concurrent starts). Reload, perform signing self-test, write marker. Deployment temporarily gives app pool write permission to key directory until key+marker exist, read-only on subsequent deploys.

**Existing installations:** Copy old key from web root (`data\badge-signing-key.pem`), **never delete** it; initialize marker on first start. Reject mismatching key ID.

**Key loss:** If marker exists and key missing, or generating a new key would overwrite certificates in database, fail startup with clear error—**no key regeneration**. Deployment also aborts. Restore key from encrypted backup to `<root>\keys\`. Rotation is deliberate, not automated; discarding old key and marker invalidates old badges.

**Backups:** Encrypt backups of `<root>\keys\badge-signing-key.pem` and `<root>\config\.env` (vault/password manager/Key Vault backup), never commit. Marker can be recreated, private key cannot.

**Invalid PEM:** Startup error states badge key could not be loaded (error type only, never contents). Expected ECDSA P-256 PKCS#8 (`BEGIN PRIVATE KEY`) or SEC1 (`BEGIN EC PRIVATE KEY`).

**Post-deployment verification (“Verify Secure Configuration”):** `/.env`, `/data/…pem`, `/keys/…pem`, `/appsettings.json`, `/web.config`, `/LearnCards.Web.dll`, `/data/learncards.db`, etc. must not return sensitive data (403/404 accepted; 2xx with sensitive contents fails). Key+marker must exist; public key ID at `/.well-known/jwks.json` must match marker. Application signs/verifies on every startup, otherwise fails and health remains red.

### HTTP 500.30: “The system cannot find the file specified” in `ECDsaCng.ImportPkcs8PrivateKey`

- **Suspected cause (not reproduced under IIS):** `ImportFromPem`/`ImportPkcs8PrivateKey` use Windows CNG/NCrypt key storage; without loaded app-pool user profile this may fail. `robocopy /MIR` previously also deleted `data\`.
- **Fix:** Parse key into `ECParameters` and import transiently in-process (no NCrypt key store). Deployment sets Load User Profile and excludes `data`/`logs` from mirroring.

## Connect Claude.ai to MCP (OAuth, Dynamic Client Registration)

Claude.ai reads OAuth metadata, dynamically registers (DCR), then redirects user to Learny for authentication.

**Required GitHub Variables** (all public HTTPS URLs, no trailing slash):

| Variable | Example | Meaning |
|---|---|---|
| `MCP_OAUTH_ISSUER` | `https://learny-mcp.brors.date` | **Public MCP base URL**, OAuth issuer and endpoint host |
| `MCP_PUBLIC_URL` | `https://learny-mcp.brors.date` | Must **equal** issuer, allowed callback target by Learny Web |
| `MCP_WEB_PUBLIC_URL` | `https://learny.brors.date` | Public Learny Web browser-login URL |
| `MCP_HTTP_HEALTH_URL` | `https://learny-mcp.brors.date/health` | Health endpoint, same host as issuer |

Deployment aborts unless issuer is HTTPS, public URL equals issuer, and Web public URL is set. Following startup, “Verify MCP HTTP Service” checks `issuer`, `authorization_endpoint`, `token_endpoint`, `registration_endpoint` all use public MCP host, and invalid registration returns HTTP 400.

**Check from outside:**

```text
GET  https://<mcp-host>/.well-known/oauth-authorization-server   → 200 JSON with all URLs on MCP host
GET  https://<mcp-host>/.well-known/oauth-protected-resource/mcp → 200 JSON, resource = https://<mcp-host>/mcp
POST https://<mcp-host>/oauth/register  (public client)          → 201 JSON with client_id
POST https://<mcp-host>/mcp without token                         → 401 WWW-Authenticate (resource_metadata)
```

`GET /mcp` intentionally returns 405 (Streamable HTTP POST only). Claude needs authorization-server and protected-resource metadata; `/.well-known/openid-configuration` is optional. Supports authorization code + **PKCE S256**, public clients (`token_endpoint_auth_method=none`), HTTPS redirects or loopback HTTP. Reject `javascript:`/foreign HTTP with `invalid_redirect_uri`; no fixed Claude callback URL is assumed.

**In Claude.ai:** Settings → Connectors → Add custom connector → `https://<mcp-host>/mcp`. Claude registers, redirects to Learny `/mcp/connect`; user logs in and consents. Connection appears in Learny profile and can be revoked.

**“Registration with Learny's authentication service failed”:** OAuth metadata advertises an unreachable/mismatched host (e.g., `MCP_OAUTH_ISSUER=https://mcplearny.brors.date` although server lives at `https://learny-mcp.brors.date`). Claude fetches metadata from correct host but then calls advertised invalid registration endpoint. Align URLs with actual public MCP host and redeploy.

**Restart caveat:** Original ADR 0013 said OAuth clients and pending logins were in MCP memory and client registration was lost on restart; access tokens stayed valid. **Later ADR 0025 changes this for public OAuth clients**, persisting encrypted client registrations in Learny DB. In-progress logins/codes and confidential dynamically registered clients still need re-registration after restart. If an old `client_id` is invalid, the client must register again.

## “Local Login (Dev Mode)” / Missing Variables (ADR 0026)

- **Cause (2026-10-26 incident):** After `.env` moved outside web root (ADR 0023), `ENV_FILE` path was ineffective; old binaries started unconfigured with `AUTH_MODE=auto`, silently enabling dev login.
- **Current behavior:** Missing/unreadable configured `ENV_FILE` fails startup (HTTP 500.30; details in `<Webroot>\logs\stdout_*.log`). Outside Development, dev login requires explicit `AUTH_MODE=dev`; `ENV_FILE` is defined both on app pool and in `web.config` `aspNetCore/environmentVariables`. “Verify Secure Configuration” checks login page does not show dev mode and issuer URL matches `PUBLIC_BASE_URL`.
- **On-server check:** `type C:\inetpub\wwwroot\LearnCards\logs\stdout_*.log` should show `.env: <path>` on startup. App-pool identity `IIS AppPool\<Pool>` needs read access to `<IIS_CONFIG_ROOT>\config\.env` (default `C:\ProgramData\LearnCards\<Site>`).
