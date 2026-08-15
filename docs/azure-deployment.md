# Deployment als Azure Web App

Die Web-App (`src/LearnCards.Web`) läuft unverändert als Azure App Service (Linux). Da App-Service-
Dateisysteme nicht dauerhaft persistent sind, wird für Azure **zwingend PostgreSQL als externe
Datenbank** verwendet (nicht der lokale SQLite-Fallback).

## 1. Azure Database for PostgreSQL Flexible Server anlegen

```bash
az postgres flexible-server create \
  --resource-group <RESOURCE_GROUP> \
  --name learncards-db \
  --location westeurope \
  --admin-user learncards \
  --admin-password '<STARKES_PASSWORT>' \
  --sku-name Standard_B1ms \
  --tier Burstable \
  --version 16 \
  --database-name learncards \
  --public-access 0.0.0.0-255.255.255.255   # einschränken auf Azure-Dienste / App-Service-Ausgangs-IPs
```

Azure Database for PostgreSQL erzwingt TLS — die App unterstützt das direkt über
`POSTGRES_SSLMODE=require` (kein zusätzliches Zertifikat nötig, da die Verbindung nur
verschlüsselt, nicht CA-geprüft wird — identisch zum Standardverhalten von `libpq` mit
`sslmode=require`).

## 2. App Service anlegen (Linux, .NET 10, oder Container)

**Option A — Code-Deploy (App Service Linux, .NET-10-Runtime-Stack):**

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

**Option B — Container-Deploy (empfohlen, identisches Image wie auf dem eigenen Linux-Server):**

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

## 3. App Settings (Umgebungsvariablen)

In Azure Portal → App Service → Konfiguration → Anwendungseinstellungen, oder per CLI:

```bash
az webapp config appsettings set --resource-group <RESOURCE_GROUP> --name learncards-app --settings \
  DB_PROVIDER=postgres \
  POSTGRES_HOST=learncards-db.postgres.database.azure.com \
  POSTGRES_PORT=5432 \
  POSTGRES_USER=learncards \
  POSTGRES_PASSWORD='<STARKES_PASSWORT>' \
  POSTGRES_DB=learncards \
  POSTGRES_SSLMODE=require \
  AUTH_MODE=oidc \
  OIDC_ISSUER='https://login.microsoftonline.com/<TENANT_ID>/v2.0' \
  OIDC_CLIENT_ID='<CLIENT_ID>' \
  OIDC_CLIENT_SECRET='<CLIENT_SECRET>' \
  OIDC_AUDIENCE='<CLIENT_ID>' \
  OIDC_WEB_SCOPE='openid profile email api://<CLIENT_ID>/access_as_user' \
  OPENAI_API_KEY='<OPENAI_KEY>' \
  MCP_API_KEY='<STARKER_ZUFALLSSTRING>' \
  APP_DOMAIN=learncards-app.azurewebsites.net \
  ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
  DATA_PROTECTION_DIR=/home/data/keys
```

Wichtig:

- **`DATA_PROTECTION_DIR=/home/data/keys`** — `/home` ist bei App Service (Linux) der einzige
  über Neustarts/Skalierung hinweg persistente Pfad. Ohne diese Einstellung würden
  Login-Cookies bei jedem Neustart/Scale-Out ungültig (neue Data-Protection-Schlüssel pro Instanz).
- **`OIDC_ISSUER`/`OIDC_CLIENT_ID`/…** — dieselbe Entra-ID-App-Registrierung wie lokal, ergänzt um
  die Produktions-Redirect-URI `https://learncards-app.azurewebsites.net/auth/callback` in der
  App-Registrierung.
- **`ASPNETCORE_FORWARDEDHEADERS_ENABLED`** — App Service terminiert TLS vor der App; die App
  muss `X-Forwarded-Proto` auswerten, damit OIDC-Redirect-URIs korrekt auf `https://` lauten
  (bereits in `Program.cs` über `UseForwardedHeaders` vorbereitet).

## 4. Health-Check & Skalierung

App Service Health-Check auf `/health` konfigurieren:

```bash
az webapp config set --resource-group <RESOURCE_GROUP> --name learncards-app --health-check-path /health
```

Bei horizontaler Skalierung (mehrere Instanzen) sorgt der persistente Data-Protection-Key-Pfad
(`/home/data/keys`) dafür, dass Login-Sessions instanzübergreifend gültig bleiben. Blazor-Server-
Interaktivität nutzt SignalR/WebSockets — bei mehreren Instanzen "ARR Affinity" (Standard bei App
Service aktiv) beibehalten, damit ein Client während seiner Sitzung an derselben Instanz bleibt.

## 5. MCP-Server

Der MCP-Server läuft nicht in Azure — er ist ein lokaler stdio-Prozess, den Claude Desktop o. ä.
auf dem Rechner des Nutzers startet und der über `LEARNCARDS_API_URL=https://learncards-app.azurewebsites.net/api`
sowie den produktiven `MCP_API_KEY` gegen die Azure Web App spricht.
