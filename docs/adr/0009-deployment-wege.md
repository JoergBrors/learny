# 0009 — Deployment: IIS per Tag, Docker, Azure

- **Status:** Akzeptiert
- **Datum:** 2026-10-24
- **Ergänzt durch:** [0011](0011-mehrbenutzerarchitektur.md), [0012](0012-rollen-aus-entra-app-roles.md), [0013](0013-mcp-benutzergebundene-anmeldung.md) (neue Konfigurationswerte, siehe unten)

## Kontext

Die App läuft in drei Umgebungen: Windows/IIS (selbstgehosteter Runner), Docker auf Linux, Azure App Service.

## Entscheidung

- **IIS:** GitHub Action `.github/workflows/deploy-iis-on-tag.yml` baut bei einem Git-Tag Web- und MCP-Paket (`dotnet publish`)
  und deployt auf einen IIS-Runner. Nicht-sensitive Werte aus `deploy/iis/deployment.json`, Secrets aus GitHub `Secrets`/`Variables`.
  Templates: `deploy/iis/web.env`, `deploy/iis/mcpsettings.json`.
- **Docker:** `docker-compose.yml` (Postgres + Web; Profile `mcp`, `production` mit nginx).
- **Azure:** App Service mit externer PostgreSQL-Datenbank (`docs/azure-deployment.md`).
- Konfiguration erfolgt überall über Umgebungsvariablen; `.env` nur lokal (nicht committen).
- **Lokale Entwicklung:** `docker-compose.dev.yml` (nur PostgreSQL, Port 55432) und die Starter in `.vscode/launch.json`/`tasks.json`; Details in [local-development.md](../local-development.md).
- **Neue Werte seit ADR 0011–0013:** `ADMIN_SUBJECTS`, `OIDC_ROLE_MAP`, `MCP_PUBLIC_URL`, `MCP_TOKEN_DAYS` (Web) sowie `LEARNCARDS_WEB_PUBLIC_URL`, `LEARNCARDS_MCP_TOKEN` (MCP-Server). Der IIS-Workflow rendert sie **noch nicht**
  (`deploy/iis/web.env` wird bei jedem Deploy überschrieben): als IIS-Umgebungsvariablen setzen oder den Workflow erweitern. Außerdem verlangt der Workflow weiterhin das nicht mehr genutzte Secret `MCP_OAUTH_SIGNING_KEY` (Platzhalterwert genügt).

## Konsequenzen

- Konfigurationsänderungen müssen in mehreren Templates gepflegt werden.
- Ein Tag löst ein Produktions-Deployment aus.

## Leitplanken für Änderungen

- **Keine Tags erstellen oder pushen** und keine Deploys auslösen, ohne ausdrückliche Anweisung.
- Neue Umgebungsvariable → `AppConfig.cs`, `.env.example`, `deploy/iis/web.env`, `docker-compose.yml`, `docs/*-deployment.md`.
- Keine Secrets in Workflow-Dateien, Templates oder Dokumentation.
- Build-Ausgaben (`bin/`, `obj/`, `publish-web-test/`) sind keine Quellen und nicht von Hand zu ändern.

## Betroffener Code

- `.github/workflows/*`, `deploy/**`, `docker-compose.yml`, `nginx/*`, `src/*/Dockerfile`, `docs/*-deployment.md`
