# Deploy as an Azure Web App

The Web application (`src/LearnCards.Web`) runs as-is in Azure App Service (Linux). Because App Service file systems are not reliably persistent, Azure deployments **must use an external PostgreSQL database**, not the local SQLite fallback.

## 1. Create Azure Database for PostgreSQL Flexible Server

```bash
az postgres flexible-server create \
  --resource-group <RESOURCE_GROUP> \
  --name learncards-db \
  --location westeurope \
  --admin-user learncards \
  --admin-password '<STRONG_PASSWORD>' \
  --sku-name Standard_B1ms \
  --tier Burstable \
  --version 16 \
  --database-name learncards \
  --public-access 0.0.0.0-255.255.255.255   # Restrict to Azure services / App Service outbound IPs
```

Azure Database for PostgreSQL requires TLS. The app supports it through `POSTGRES_SSLMODE=require`; this encrypts the connection without CA certificate verification, matching libpq's default `sslmode=require` behavior.

## 2. Create App Service (Linux, .NET 10, or Container)

**Option A — code deployment (App Service Linux, .NET 10 runtime stack):**

```bash
az webapp create \
  --resource-group <RESOURCE_GROUP> \
  --plan <APP_SERVICE_PLAN> \
  --name learncards-app \
  --runtime "DOTNETCORE:10.0"

cd src/LearnCards.Web
dotnet publish -c Release -o publish
cd publish && zip -r ../publish.zip . && cd ..
az webapp deploy --resource-group <RESOURCE_GROUP> --name learncards-app --src-path publish.zip --type zip
```

**Option B — container deployment (recommended; same image as the self-managed Linux host):**

```bash
az acr build --registry <REGISTRY> --image learncards-web:latest \
  --file src/LearnCards.Web/Dockerfile .

az webapp create \
  --resource-group <RESOURCE_GROUP> \
  --plan <APP_SERVICE_PLAN> \
  --name learncards-app \
  --deployment-container-image-name <REGISTRY>.azurecr.io/learncards-web:latest

az webapp config set --resource-group <RESOURCE_GROUP> --name learncards-app --generic-configurations '{"linuxFxVersion": "SITECONTAINERS"}'
az webapp config appsettings set --resource-group <RESOURCE_GROUP> --name learncards-app \
  --settings WEBSITES_PORT=8080
```

## 3. App Settings (Environment Variables)

Azure Portal → App Service → Configuration → Application settings, or use:

```bash
az webapp config appsettings set --resource-group <RESOURCE_GROUP> --name learncards-app --settings \
  DB_PROVIDER=postgres \
  POSTGRES_HOST=learncards-db.postgres.database.azure.com \
  POSTGRES_PORT=5432 \
  POSTGRES_USER=learncards \
  POSTGRES_PASSWORD='<STRONG_PASSWORD>' \
  POSTGRES_DB=learncards \
  POSTGRES_SSLMODE=require \
  AUTH_MODE=oidc \
  OIDC_ISSUER='https://login.microsoftonline.com/<TENANT_ID>/v2.0' \
  OIDC_CLIENT_ID='<CLIENT_ID>' \
  OIDC_CLIENT_SECRET='<CLIENT_SECRET>' \
  OIDC_AUDIENCE='<CLIENT_ID>' \
  OIDC_WEB_SCOPE='openid profile email api://<CLIENT_ID>/access_as_user' \
  OPENAI_API_KEY='<OPENAI_KEY>' \
  MCP_API_KEY='<STRONG_RANDOM_STRING>' \
  MCP_TOKEN_KEY='<BASE64_32_BYTES>' \
  MCP_PUBLIC_URL='https://<public-mcp-url>' \
  ADMIN_SUBJECTS='<ENTRA_SUB_OF_FIRST_ADMIN>' \
  APP_DOMAIN=learncards-app.azurewebsites.net \
  ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
  DATA_PROTECTION_DIR=/home/data/keys
```

Important:

- **`DATA_PROTECTION_DIR=/home/data/keys`:** On Linux App Service, `/home` is the persistent location across restarts and scaling. Without this, authentication cookies can become invalid on every restart/scale-out as each instance gets different Data Protection keys.
- **`OIDC_ISSUER`/`OIDC_CLIENT_ID`/etc.:** Use the same Entra ID app registration as locally, adding production redirect URI `https://learncards-app.azurewebsites.net/auth/callback`.
- **`AUTH_MODE=oidc`:** Always set this explicitly in production. Historical risk: incomplete OIDC configuration with `auto` could expose an administrator-enabled development login ([review F1](reviews/2026-10-24-mehrbenutzer-pruefbericht.md)). ADR 0026 later adds fail-closed protection.
- **`ADMIN_SUBJECTS`:** Entra `sub`, not email, of the initial admin. Assign additional roles through Entra app role `Admin.All` ([ADR 0012](adr/0012-rollen-aus-entra-app-roles.en.md); optional `OIDC_ROLE_MAP`). Legacy modules/cards are unassigned and must be assigned under `/admin` ([ADR 0011](adr/0011-mehrbenutzerarchitektur.en.md)).
- **`MCP_PUBLIC_URL`:** The public MCP server URL, the only allowed OAuth callback destination ([ADR 0013](adr/0013-mcp-benutzergebundene-anmeldung.en.md)). Optional `MCP_TOKEN_DAYS`.
- **Image storage** ([ADR 0017](adr/0017-konfigurierbare-bildspeicher.en.md)/[0018](adr/0018-zentraler-image-proxy-und-storage-zugriff.en.md)): Set `IMAGE_STORAGE_PROVIDER=azureblob`, `IMAGE_AZUREBLOB_ACCOUNT`, `IMAGE_AZUREBLOB_CONTAINER`, `IMAGE_AZUREBLOB_AUTH=managedidentity`, optionally `IMAGE_AZURE_SAS_TTL_SECONDS` (default 60). Enable **Managed Identity** on the web app and grant *Storage Blob Data Contributor* (write/delete) and *Storage Blob Delegator* (user delegation key for read SAS) on the storage account/container. Keep the container private, disable anonymous access, preferably disable storage account key access. For a private endpoint, add `IMAGE_AZUREBLOB_ALLOW_PRIVATE_NETWORK=true`. The `local` provider on App Service needs mounted persistent storage. Use App Settings or Key Vault references for secrets.
- **`ASPNETCORE_FORWARDEDHEADERS_ENABLED`:** App Service terminates TLS ahead of the app. The app must honor `X-Forwarded-Proto` to construct HTTPS OIDC redirect URIs (`UseForwardedHeaders` is configured in `Program.cs`).

## 4. Health Checks and Scaling

Configure `/health` as App Service's health-check path:

```bash
az webapp config set --resource-group <RESOURCE_GROUP> --name learncards-app --health-check-path /health
```

With multiple instances, persistent Data Protection keys in `/home/data/keys` keep login sessions valid across instances. Blazor Server interactivity uses SignalR/WebSockets. Retain ARR Affinity (on by default in App Service) so each client stays on the same instance during its session.

## 5. MCP Server

The MCP server does not need to run inside Azure. It can run locally as an stdio process launched by Claude Desktop or another client and access `LEARNCARDS_API_URL=https://learncards-app.azurewebsites.net/api` **on behalf of the user**. Generate a personal token at `/profile` and set `LEARNCARDS_MCP_TOKEN`. The `MCP_API_KEY` is only a service key for OAuth code exchange and grants no data access. An HTTP-hosted MCP service uses OAuth login (`LEARNCARDS_WEB_PUBLIC_URL` = public web URL).
