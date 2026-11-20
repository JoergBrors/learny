# Install Learny with Docker

This package installs **Learny** (LearnCards) from prebuilt Docker Hub images—without source code, Git, or the .NET SDK.

| Image | Purpose |
|---|---|
| [`joergb23/learny-web`](https://hub.docker.com/r/joergb23/learny-web) | Blazor web UI and REST API as one service |
| [`joergb23/learny-mcp`](https://hub.docker.com/r/joergb23/learny-mcp) | Optional MCP server for AI clients |

Platforms: **linux/amd64**, **linux/arm64**. Tags: `1.2.3` (pinned) and `latest` (latest stable; prereleases such as `1.2.3-rc.1` never replace `latest`).

Package includes `docker-compose.yml`, `.env.example`, `mcpsettings.example.json` (MCP only), `nginx/learny.conf.template` (proxy profile), this guide, and Docker Hub descriptions `README-web.md`/`README-mcp.md`.

## Step by Step

1. **Install Docker Engine and Compose** (Docker Engine ≥24, Compose plugin ≥2.20): <https://docs.docker.com/engine/install/>. Check `docker compose version`.
2. **Create a directory** and extract `learny-docker-<version>.zip` from the GitHub release or download `docker-compose.yml` and `.env.example`:
   ```bash
   mkdir learny && cd learny
   unzip ../learny-docker-<version>.zip
   ```
3. **Create `.env`:** `cp .env.example .env` and `chmod 600 .env`; it contains secrets and must never be committed.
4. **Configure variables:** At minimum `POSTGRES_PASSWORD` and authentication `OIDC_*` (see [Microsoft Entra ID setup](#set-up-microsoft-entra-id)); all settings appear [below](#variables).
5. **Generate random passwords/keys:**
   ```bash
   openssl rand -base64 32      # e.g. POSTGRES_PASSWORD and MCP_API_KEY
   ```
6. **Pull images:** `docker compose pull`.
7. **Start:** `docker compose up -d`.
8. **Check status:** `docker compose ps` (all services `healthy`); logs `docker compose logs -f web`.
9. **Open:** `http://localhost:8080` (bound locally). External clients need an HTTPS reverse proxy ([HTTPS](#https-and-reverse-proxy)).
10. **Optional MCP:** See [MCP](#mcp-optional).
11. **Maintenance:** See [Updates](#updates) and [Backup and Restore](#backup-and-restore).

> **Operating mode (ADR 0026):** In containers (`ASPNETCORE_ENVIRONMENT=Production`), Learny refuses startup without complete OIDC configuration rather than silently falling back to development login. Explicit `AUTH_MODE=dev` is required for development login and logs a warning; invalid `AUTH_MODE` values abort startup.
>
> **Local experimentation only:** `AUTH_MODE=dev` in `.env` permits passwordless administrator login. **Never** use on a public server. Example: `POSTGRES_PASSWORD=...` and `AUTH_MODE=dev`, reachable only at `http://localhost:8080`.

## Architecture

```text
Internet ──► Reverse Proxy (HTTPS, 443) ──► web:8080 ──► db:5432 (PostgreSQL, internal only)
                                       └─► mcp:8787 (optional) ──► web:8080/api (internal)
```

- **Internal Docker network** `internal`: `db`, `web`, and `mcp` communicate by service names; PostgreSQL **does not expose a host port**.
- **External:** Reverse proxy only. Web/MCP published host ports bind to `127.0.0.1` by default (`LEARNY_BIND_ADDRESS`).
- Web trusts `X-Forwarded-*` headers to construct correct HTTPS OIDC redirects. **Never expose its HTTP port directly** to the internet.
- **Internal/public URLs:** MCP calls `http://web:8080/api` internally. Browsers/AI clients use public HTTPS URLs (`PUBLIC_BASE_URL`, `LEARNCARDS_WEB_PUBLIC_URL`, `MCP_PUBLIC_URL`, `mcpsettings.json` issuer).

### Persistent Data

| Volume | Contents |
|---|---|
| `postgres_data` | PostgreSQL database (modules, cards, users, results, certificates) |
| `web_data` (`/app/data`) | Data Protection keys (`keys/`), badge signing key and marker, local images/cache, optional SQLite file |

Restarts and upgrades preserve volumes. **`docker compose down -v` deletes them**; omit `-v` to keep data.

## Set Up Microsoft Entra ID

1. In Azure Portal open **Microsoft Entra ID → App registrations → New registration**. Choose name “Learny” and appropriate supported account types (typically single tenant).
2. Add *Web* redirect URI `https://<your-public-address>/auth/callback` (optionally local `http://localhost:8080/auth/callback` for testing).
3. Under **Certificates & secrets → New client secret**, copy the **value**, not ID, immediately to `OIDC_CLIENT_SECRET`. Note expiration and rotate before expiry.
4. Set `OIDC_CLIENT_ID` to Application (client) ID, `OIDC_ISSUER=https://login.microsoftonline.com/<tenant-id>/v2.0` using Directory (tenant) ID.
5. Optionally define `Admin.All` and/or `Auditor` app roles and assign users; map with `OIDC_ROLE_MAP` (default `Admin.All=admin`). For initial setup, alternatively put the first admin's OIDC subject ID in `ADMIN_SUBJECTS`.
6. Set `AUTH_MODE=oidc` and `PUBLIC_BASE_URL` to the public URL.

## HTTPS and Reverse Proxy

An nginx template is included (profile `proxy`), expecting `./certs/cert.pem` (certificate + chain) and `./certs/key.pem` (private key, `chmod 600`).

```bash
# .env
COMPOSE_PROFILES=db,proxy        # With MCP: db,proxy,mcp
LEARNY_HOST=learny.example.org
MCP_HOST=mcp.example.org
LEARNY_BIND_ADDRESS=127.0.0.1    # Web/MCP remain local; only nginx public
```

nginx redirects HTTP to HTTPS, proxies WebSockets (Blazor/SignalR), and sets `X-Forwarded-Proto/Host`. Existing Traefik/Caddy/nginx/IIS proxies may be used instead; omit `proxy` profile and forward to `127.0.0.1:8080` (Web) / `127.0.0.1:8787` (MCP), supporting WebSockets and `X-Forwarded-Proto`, `X-Forwarded-Host`, `X-Forwarded-For`.

## MCP (Optional)

The MCP server connects AI clients such as Claude to Learny. It stores no application data; data access always uses **the user's personal token**, never the `MCP_API_KEY` service key.

1. In `.env`, configure long random `MCP_API_KEY`, `MCP_PUBLIC_URL=https://mcp.example.org`, `LEARNCARDS_WEB_PUBLIC_URL=https://learny.example.org`, and `COMPOSE_PROFILES=db,mcp`.
2. Run `cp mcpsettings.example.json mcpsettings.json`, set `issuer` to the **public MCP URL** `https://mcp.example.org`.
3. `docker compose up -d`; MCP listens internally at `http://0.0.0.0:8787`, with TLS terminated at the reverse proxy.
4. Connect clients to `https://mcp.example.org/mcp`; OAuth login occurs in the browser through Learny. Alternatively generate a personal profile token.

See [README-mcp.en.md](README-mcp.en.md) for details/troubleshooting.

## Updates

```bash
# Pin e.g. LEARNY_VERSION=1.2.0 in .env, or use latest
docker compose pull
docker compose up -d
```

Data remains in volumes; startup automatically extends database tables when required. Before major upgrades take a [backup](#backup-and-restore). To roll back set the previous `LEARNY_VERSION` and rerun `docker compose up -d`; database schema changes may require restoring a matching backup.

## Backup and Restore

```bash
# Database
docker compose exec -T db sh -c 'pg_dump -U "$POSTGRES_USER" "$POSTGRES_DB"' > learny-db-$(date +%F).sql
# Web data: Data Protection, badges, images
docker run --rm -v learny_web_data:/data -v "$PWD":/backup alpine tar czf /backup/learny-web-data-$(date +%F).tgz -C /data .
# Also back up .env and mcpsettings.json securely/encrypted (contain secrets)
```

Restore after `docker compose down` (**without** `-v`):

```bash
docker compose up -d db
docker compose exec -T db sh -c 'psql -U "$POSTGRES_USER" "$POSTGRES_DB"' < learny-db-<date>.sql
docker run --rm -v learny_web_data:/data -v "$PWD":/backup alpine sh -c 'cd /data && tar xzf /backup/learny-web-data-<date>.tgz'
docker compose up -d
```

**Badge signing key** `web_data/badge-signing-key.pem` is irreplaceable: issued certificates cannot be verified if it is lost. Learny fails startup on existing installations when the key is missing instead of silently regenerating.

## Security

- Keep secrets out of images, YAML, and repository. Use permission-600 `.env` or secret management.
- Never use `AUTH_MODE=dev` in production. Avoid `auto` (historically fell back to dev without OIDC, ADR 0014); never misspell it.
- Do not publish PostgreSQL; serve Web/MCP only via HTTPS reverse proxy.
- Containers run as nonprivileged UID 1654; `web_data` belongs to that user.
- Private modules/cards remain owner-isolated; explicit sharing/publication only.
- Regularly rotate image provider (S3/Azure/SharePoint) and OIDC credentials.

## External PostgreSQL

```bash
# .env
COMPOSE_PROFILES=            # Omit db, optionally enable mcp/proxy only
POSTGRES_HOST=postgres.example.org
POSTGRES_PORT=5432
POSTGRES_DB=learncards
POSTGRES_USER=learncards
POSTGRES_PASSWORD=...        # Required
POSTGRES_SSLMODE=require     # For public networks
```

Create database/user beforehand; Learny creates tables at startup. Empty `COMPOSE_PROFILES` means no PostgreSQL container.

## Example Configurations

**Minimum PostgreSQL + Entra ID setup:**

```bash
POSTGRES_PASSWORD=<random>
AUTH_MODE=oidc
OIDC_ISSUER=https://login.microsoftonline.com/<tenant-id>/v2.0
OIDC_CLIENT_ID=<client-id>
OIDC_CLIENT_SECRET=<secret-value>
PUBLIC_BASE_URL=https://learny.example.org
APP_DOMAIN=learny.example.org
```

**Add MCP:**

```bash
COMPOSE_PROFILES=db,mcp
MCP_API_KEY=<random>
MCP_PUBLIC_URL=https://mcp.example.org
LEARNCARDS_WEB_PUBLIC_URL=https://learny.example.org
# Also set mcpsettings.json issuer = https://mcp.example.org
```

**Without OpenAI key:** Leave `OPENAI_API_KEY` empty. Learny uses the labeled offline fallback (questions from card pool, keyword grading, AI chat informs users that the key is absent). Other learning functionality remains available.

## Variables

All variables from `.env.example`. “Service” indicates which Compose service receives the value (`Compose` means Compose itself). Values are injected under `environment:` in `docker-compose.yml`; `${VAR:-default}` is substituted from `.env`.

Required: **PostgreSQL** → `POSTGRES_PASSWORD`; **OIDC** → `OIDC_ISSUER`, `OIDC_CLIENT_ID`, `OIDC_CLIENT_SECRET`; **MCP** → `MCP_API_KEY`, `MCP_PUBLIC_URL`, `LEARNCARDS_WEB_PUBLIC_URL`, `mcpsettings.json`. AI/image providers optional.

| Variable | Service | Required | Default | Description |
|---|---|---|---|---|
| `LEARNY_VERSION` | Compose | No | `latest` | Image version; latest means latest stable |
| `COMPOSE_PROFILES` | Compose | No | `db` | Active comma-separated profiles: db (PostgreSQL), mcp, proxy (nginx/TLS) |
| `LEARNY_BIND_ADDRESS` | Compose | No | `127.0.0.1` | Published port bind address; 0.0.0.0 only in trusted networks |
| `LEARNY_WEB_PORT` | Compose | No | `8080` | Host port for Web |
| `LEARNY_MCP_PORT` | Compose | No | `8787` | Host port for MCP |
| `DB_PROVIDER` | web | No | `postgres` | postgres or sqlite (file in volume, only small installations) |
| `POSTGRES_HOST` | web | With PostgreSQL | `db` | Database host; specify external address and omit db profile if external |
| `POSTGRES_PORT` | web | No | `5432` | Database port |
| `POSTGRES_DB` | web, db | No | `learncards` | Database name |
| `POSTGRES_USER` | web, db | No | `learncards` | Database user |
| `POSTGRES_PASSWORD` | web, db | **Yes** | `—` | Database user password; Compose aborts when missing |
| `POSTGRES_SSLMODE` | web | No | `disable` | SSL mode; use require for external public networks |
| `ASPNETCORE_ENVIRONMENT` | web | No | `Production` | ASP.NET environment; do not use Development in production |
| `DATA_PROTECTION_DIR` | web | No | `/app/data/keys` | Persist Data Protection keys in web_data volume; do not change |
| `APP_DOMAIN` | web | No | `localhost` | Display domain |
| `PUBLIC_BASE_URL` | web | In production | `empty` | Public URL for badge issuer and verification links |
| `DEBUG` | web | No | `false` | Verbose errors true/false |
| `AUTH_MODE` | web | No | `oidc` | oidc production, dev passwordless local experimentation only; not auto |
| `OIDC_ISSUER` | web | **With oidc** | `—` | Issuer URL e.g. Microsoft Entra ID |
| `OIDC_CLIENT_ID` | web | **With oidc** | `—` | Entra application client ID |
| `OIDC_CLIENT_SECRET` | web | **With oidc** | `—` | Entra app secret (expires) |
| `OIDC_AUDIENCE` | web | No | `empty` | Expected audience (typically client ID) |
| `OIDC_WEB_SCOPE` | web | No | `openid profile email` | Requested OIDC scopes |
| `OIDC_ROLE_MAP` | web | No | `Admin.All=admin` | Entra app role mapping to admin/auditor/themen_admin |
| `ADMIN_SUBJECTS` | web | No | `empty` | Comma-separated bootstrap administrator subjects (sub) |
| `MCP_API_KEY` | web, mcp | **With MCP** | `empty` | Service key between Web and MCP, grants no user data access |
| `MCP_PUBLIC_URL` | web | **With MCP in production** | `empty` | Allowed public MCP callback URL(s), comma-separated, no trailing slash |
| `MCP_TOKEN_KEY` | web | **Always** | `—` | Base64 main secret ≥32 bytes for HMAC tokens/codes and OAuth clients; outside DB backups, mandatory for startup |
| `MCP_TOKEN_KEY_PREVIOUS` | web | No | `empty` | Comma-separated previous keys for validation/decryption after rotation |
| `MCP_LEGACY_TOKEN_UNTIL` | web | No | `2027-01-31` | End date for legacy SHA-256-only MCP tokens |
| `MCP_TOKEN_DAYS` | web | No | `30` | Personal MCP token lifetime, days (1–365) |
| `LEARNCARDS_WEB_PUBLIC_URL` | mcp | **With MCP** | `empty` | Public browser URL for Learny Web |
| `OPENAI_API_KEY` | web | No | `empty` | Chat/quiz AI key; absent → offline fallback |
| `OPENAI_MODEL` | web | No | `gpt-4o-mini` | Model |
| `OPENAI_BASE_URL` | web | No | `https://api.openai.com/v1` | OpenAI-compatible API base URL |
| `BADGE_SIGNING_KEY_PEM` | web | No | `empty` | ECDSA P-256 PKCS#8 badge signing key; empty generates once in volume |
| `BLOCKCHAIN_ENABLED` | web | No | `true` | Enable anchoring; false = signed badges only |
| `BLOCKCHAIN_NETWORK` | web | No | `polygon-amoy` | Test network; mainnet needs admin approval |
| `BLOCKCHAIN_AUTO_ANCHOR` | web | No | `true` | Automatic batching and anchoring |
| `BLOCKCHAIN_MAX_COST_EUR` | web | No | `0.001` | Hard EUR cost ceiling per credential, maximum 0.001 |
| `BLOCKCHAIN_MAINNET_ENABLED` | web | No | `false` | Technically enable mainnet |
| `BLOCKCHAIN_BATCH_ENABLED` | web | No | `true` | Batch mode |
| `BLOCKCHAIN_BATCH_MAX_SIZE` | web | No | `100` | Maximum credentials per batch |
| `BLOCKCHAIN_BATCH_MAX_WAIT_MINUTES` | web | No | `60` | Maximum minutes until sending |
| `BLOCKCHAIN_WALLET_ADDRESS` | web | No | `empty` | Public wallet address (0x…), never private key |
| `LEARNY_HOST` | proxy | With proxy profile | `localhost` | Public web hostname, without https:// |
| `MCP_HOST` | proxy | With proxy+mcp | `mcp.localhost` | Public MCP hostname, without https:// |
| `IMAGE_STORAGE_PROVIDER` | web | No | `database` | database/local/azureblob/s3/sharepoint |
| `IMAGE_LOCAL_PATH` | web | With local | `/app/data/images` | Local image folder; must reside under /app/data |
| `IMAGE_PROXY_ENABLED` | web | No | `true` | Enable /api/images/{id} proxy |
| `IMAGE_HTTP_TIMEOUT_SECONDS` | web | No | `30` | Outgoing storage request timeout |
| `IMAGE_CACHE_ENABLED` | web | No | `true` | Image cache on/off |
| `IMAGE_CACHE_MAX_SIZE_MB` | web | No | `512` | Maximum cache size MB |
| `IMAGE_CACHE_TTL_MINUTES` | web | No | `1440` | Cache TTL minutes |
| `IMAGE_CACHE_MEMORY_MB` | web | No | `64` | RAM cache size MB |
| `IMAGE_CACHE_PATH` | web | No | `empty` | Cache directory; empty uses /app/data |
| `IMAGE_AZUREBLOB_ACCOUNT` | web | With azureblob | `empty` | Azure storage account, or use IMAGE_AZUREBLOB_ENDPOINT |
| `IMAGE_AZUREBLOB_ENDPOINT` | web | No | `empty` | Custom Azure Blob endpoint |
| `IMAGE_AZUREBLOB_CONTAINER` | web | No | `learncards-images` | Blob container name |
| `IMAGE_AZUREBLOB_AUTH` | web | No | `managedidentity` | managedidentity/clientsecret/sharedkey (sharedkey dev/test only) |
| `IMAGE_AZUREBLOB_TENANT_ID` | web | With clientsecret | `empty` | Tenant ID |
| `IMAGE_AZUREBLOB_CLIENT_ID` | web | With clientsecret | `empty` | Client ID |
| `IMAGE_AZUREBLOB_CLIENT_SECRET` | web | With clientsecret | `empty` | Client secret |
| `IMAGE_AZUREBLOB_ACCOUNT_KEY` | web | With sharedkey | `empty` | Storage account key |
| `IMAGE_AZURE_SAS_TTL_SECONDS` | web | No | `60` | Read SAS validity in seconds |
| `IMAGE_S3_ENDPOINT` | web | No | `empty` | S3 endpoint, empty means AWS; set for MinIO |
| `IMAGE_S3_REGION` | web | No | `us-east-1` | S3 region |
| `IMAGE_S3_BUCKET` | web | With s3 | `empty` | S3 bucket |
| `IMAGE_S3_ACCESS_KEY_ID` | web | With s3 | `empty` | Access key ID |
| `IMAGE_S3_SECRET_ACCESS_KEY` | web | With s3 | `empty` | Secret access key |
| `IMAGE_S3_SESSION_TOKEN` | web | No | `empty` | Temporary security token |
| `IMAGE_S3_PATH_STYLE` | web | No | `false` | Path-style addressing, true for MinIO |
| `IMAGE_S3_PRESIGN_TTL_SECONDS` | web | No | `60` | Presigned read URL lifetime seconds |
| `IMAGE_SHAREPOINT_TENANT_ID` | web | With sharepoint | `empty` | Tenant ID |
| `IMAGE_SHAREPOINT_CLIENT_ID` | web | With sharepoint | `empty` | Client ID |
| `IMAGE_SHAREPOINT_CLIENT_SECRET` | web | With sharepoint | `empty` | Client secret |
| `IMAGE_SHAREPOINT_SITE_ID` | web | With sharepoint | `empty` | Site ID |
| `IMAGE_SHAREPOINT_DRIVE_ID` | web | With sharepoint | `empty` | Drive ID |
| `IMAGE_SHAREPOINT_FOLDER` | web | No | `LearnCards/images` | Destination folder |

Internally fixed (not configurable through `.env`): `ASPNETCORE_URLS=http://+:8080`, `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`; MCP: `LEARNCARDS_API_URL=http://web:8080/api`, `LEARNCARDS_MCP_HTTP=true`, `LEARNCARDS_MCP_STDIO=false`, `LEARNCARDS_MCP_HTTP_URLS=http://0.0.0.0:8787`, `LEARNCARDS_MCP_CONFIG=/config/mcpsettings.json`. Rare image storage flags (`…_ALLOW_HTTP`, `…_ALLOW_PRIVATE_NETWORK`, `…_AUTHORITY`) exist in code but are not set by Compose; see ADRs 0017/0018.

## Publishing (Maintainers)

A GitHub Release triggers [`publish-docker.yml`](../../.github/workflows/publish-docker.yml): tests → build/push images → Docker Hub descriptions → release ZIP. Set repository secrets `DOCKERHUB_USERNAME` (`joergb23`) and `DOCKERHUB_TOKEN` (Hub token with read/write/delete permissions required to update descriptions). **Create releases only on existing tags**: creating a new release tag also triggers the IIS workflow. Prereleases (e.g., `1.0.0-rc.1`, or marked “Pre-release”) receive only version tags; `latest` and Docker Hub descriptions remain unchanged.
