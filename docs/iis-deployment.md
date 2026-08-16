# Deployment nach Windows Server IIS per GitHub Actions

Diese Variante ist für einen **Windows-Server mit IIS** gedacht. Die GitHub Action wird bei
jedem neuen Git-Tag ausgelöst und deployt die Web-App `src/LearnCards.Web` auf einen
**self-hosted Windows Runner**, der direkt auf dem Zielserver läuft. Zusätzlich veröffentlicht
die Pipeline den MCP-Server `src/LearnCards.McpServer` in ein separates Verzeichnis auf dem Server.

## Überblick

- Trigger: `push` auf ein Tag, z. B. `v1.0.0`
- Build: GitHub-hosted `windows-latest`
- Deploy: self-hosted Runner mit Labels `self-hosted`, `Windows`, `X64`
- Ziel: IIS-Site + App Pool auf dem Windows-Server
- Zusätzlich: veröffentlichter MCP-Server in einem separaten Ordner
- Zusätzlich: Launcher-Skript + Claude-Desktop-Config-Snippet für automatischen MCP-Start durch den Client
- Optional: eigenständiger HTTP-MCP-Betrieb per `mcpsettings.json`

Workflow-Datei:

- `.github/workflows/deploy-iis-on-tag.yml`

## Voraussetzungen auf dem Windows-Server

Vor dem ersten Deploy muss der Zielserver vorbereitet sein:

1. Ein **GitHub Actions self-hosted Runner** läuft auf dem Server.
2. Der Runner hat die Labels:
   - `self-hosted`
   - `Windows`
   - `X64`
3. Der Runner läuft mit ausreichenden Rechten, um IIS-Features und die Site konfigurieren zu dürfen.

Die Action versucht IIS bei Bedarf selbst zu installieren und verwendet eine vorhandene
Konfiguration wieder. Falls das **ASP.NET Core Hosting Bundle** fehlt, kann die Action es ebenfalls
installieren, wenn eine Download-URL hinterlegt ist.

## Repository-Konfiguration

Die Pipeline liest ihre Deploy-Konfiguration direkt aus dem Repository:

- `deploy/iis/deployment.json`
  Enthält IIS-Site, App-Pool, Zielpfade, Hosting-Bundle-URL und MCP-Service-/Health-Settings.
- `deploy/iis/web.env`
  Enthält nur Struktur, Standardwerte und Platzhalter. Wird beim Deploy mit GitHub Secrets gerendert
  und dann als `.env` in die Web-App kopiert.
- `deploy/iis/mcpsettings.json`
  Enthält nur Struktur und Platzhalter. Wird beim Deploy mit GitHub Secrets gerendert und dann als
  `mcpsettings.json` in den MCP-Zielordner kopiert.

Damit ist das Repository die primäre Quelle für die IIS-/MCP-Konfiguration.

## Optional: App-Konfiguration auf dem IIS-Server

Die Web-App liest diese Werte künftig aus der deployten `.env` im IIS-Zielordner.
Der MCP-Server liest seine Laufzeitkonfiguration aus der deployten `mcpsettings.json`.

Die sensiblen Werte selbst liegen nicht im Repository, sondern in GitHub `Secrets`.
Die Pipeline ersetzt die Platzhalter beim Deploy.

## GitHub Secrets

Unter GitHub → `Settings` → `Secrets and variables` → `Actions` → `Secrets` anlegen:

- `MCP_API_KEY`
  Pflicht. Muss identisch in Web-App und MCP-Server verwendet werden.
- `MCP_OAUTH_SIGNING_KEY`
  Pflicht, solange `deploy/iis/mcpsettings.json` `oauth.enabled=true` setzt.
- `MCP_OAUTH_CLIENT_SECRET`
  Pflicht, solange im MCP-Config mindestens ein OAuth-Client definiert ist.
- `POSTGRES_PASSWORD`
  Optional. Nur nötig, wenn `web.env` PostgreSQL verwendet.
- `OIDC_CLIENT_SECRET`
  Optional. Nur nötig bei OIDC-Betrieb.
- `OPENAI_API_KEY`
  Optional. Nur nötig für produktiven KI-Betrieb.

## Ablauf des Deployments

Bei einem neuen Tag:

1. `dotnet publish` erzeugt ein Release-Paket der Web-App.
2. `dotnet publish` erzeugt zusätzlich ein Release-Paket des MCP-Servers.
3. Beide Pakete werden als ZIP-Artefakte bereitgestellt.
4. Der self-hosted IIS-Runner lädt die Pakete herunter.
5. Die Action prüft:
   - Runner-Variablen vorhanden
   - IIS vorhanden oder installierbar
   - `WebAdministration` verfügbar
   - ASP.NET Core Hosting Bundle vorhanden oder installierbar
   - .NET 10 ASP.NET Core Runtime vorhanden oder installierbar
   - Site/App Pool/Verzeichnis konfigurierbar
6. Das Web-Paket wird nach `IIS_PHYSICAL_PATH` gespiegelt.
7. Das MCP-Paket wird, falls `MCP_PHYSICAL_PATH` gesetzt ist, in dieses Verzeichnis gespiegelt.
8. Im MCP-Verzeichnis werden zusätzlich erzeugt:
   - `learncards-mcp.cmd` als Startskript
   - `claude-desktop.learncards.json` als Beispiel für einen MCP-Client
   - `mcpsettings.example.json` als Vorlage für einen eigenständigen HTTP-/OAuth-Betrieb
9. Die Pipeline rendert zuerst die Repo-Dateien mit GitHub Secrets:
   - `deploy/iis/web.env` → gerenderte `.env`
   - `deploy/iis/mcpsettings.json` → gerenderte `mcpsettings.json`
10. Die gerenderten Dateien werden auf den Zielserver gesetzt:
   - gerenderte `.env` → `<IIS_PHYSICAL_PATH>\.env`
   - gerenderte `mcpsettings.json` → `<MCP_PHYSICAL_PATH>\mcpsettings.json`
   Falls die MCP-Datei im Repository fehlt, greift die Pipeline auf die bestehende Datei oder
   notfalls auf `mcpsettings.example.json` zurück.
11. Falls `mcpsettings.json` vorhanden ist und `transports.http.enabled=true` setzt, legt die Action
   zusätzlich einen Windows-Dienst für den MCP-Server an oder aktualisiert ihn.
12. App Pool und Website werden wieder gestartet.
13. Optional wird `/health` geprüft, wenn `IIS_BINDING_URL` gesetzt ist.
14. Optional wird der MCP-HTTP-Endpunkt geprüft, wenn `MCP_HTTP_HEALTH_URL` gesetzt ist.

## Tag-basiertes Auslösen

Beispiel:

```bash
git tag v1.0.0
git push origin v1.0.0
```

Danach startet der Workflow automatisch.

## Hinweise

- Die Action legt Site und App Pool bei Bedarf an.
- Wenn IIS noch nicht vorhanden ist, versucht die Action die benötigten Windows-Features selbst zu installieren.
- Der App Pool wird als **No Managed Code** konfiguriert (`managedRuntimeVersion=""`).
- Vor dem Kopieren wird ein lokales Backup im Temp-Verzeichnis des Runners angelegt.
- Die Web-App wird als framework-dependent Deployment veröffentlicht; deshalb muss die
  passende Runtime auf dem IIS-Server vorhanden sein oder durch das Hosting Bundle installiert werden.
- Der MCP-Server wird ebenfalls framework-dependent veröffentlicht.
- Im reinen `stdio`-Modus bleibt der MCP-Server clientgesteuert und wird nicht als Dienst verwendet.
- Sobald `mcpsettings.json` den HTTP-Transport aktiviert, erzeugt und startet die Pipeline zusätzlich
  einen Windows-Dienst für den MCP-HTTP-Server.
- Unabhängig davon erzeugt die Pipeline ein fertiges Launcher-Skript. Ein Client wie Claude Desktop kann dieses
  Skript automatisch starten, sodass auch im `stdio`-Modus kein manueller Konsolenstart nötig ist.

## MCP ohne manuellen Start

Nach dem Deploy liegen im `MCP_PHYSICAL_PATH` zwei wichtige Dateien:

- `learncards-mcp.cmd`
- `claude-desktop.learncards.json`
- `mcpsettings.example.json`

Das Skript `learncards-mcp.cmd`:

- setzt `LEARNCARDS_API_URL` automatisch auf `IIS_BINDING_URL/api`, falls die Variable noch nicht gesetzt ist
- übernimmt `MCP_API_KEY` nach `LEARNCARDS_MCP_API_KEY`, falls nötig
- startet anschließend `LearnCards.McpServer.exe`

Damit kann z. B. Claude Desktop den MCP-Server selbst starten, sobald der Benutzer ihn im Client konfiguriert.

Beispiel: den Inhalt aus `claude-desktop.learncards.json` in die lokale Claude-Desktop-Konfiguration übernehmen.
Danach startet Claude den Server automatisch bei Bedarf, statt dass er manuell per Konsole gestartet werden muss.

## Optional: HTTP-MCP mit proprietärem OAuth

Wenn der MCP-Server als eigenständiges Tool laufen soll:

1. `mcpsettings.example.json` nach `mcpsettings.json` kopieren.
2. `transports.http.enabled` aktivieren.
3. Für einen reinen Serverbetrieb `transports.stdio.enabled` deaktivieren.
4. Optional `oauth.enabled` aktivieren und `signingKey` sowie `clients` setzen.
5. Die Datei im `MCP_PHYSICAL_PATH` ablegen.
6. Optional `MCP_SERVICE_NAME` und `MCP_HTTP_HEALTH_URL` als Repository Variables setzen.
7. Die Pipeline startet den HTTP-MCP-Server dann automatisch als Windows-Dienst, sobald
   `transports.http.enabled=true` gesetzt ist.

Hinweis: Solange `deploy/iis/mcpsettings.json` im Repository liegt, überschreibt die daraus
gerenderte Datei die Serverkonfiguration bei jedem Deploy bewusst.

Für Windows Server/IIS ist `src/LearnCards.McpServer/mcpsettings.iis.example.json` die bessere
Ausgangsbasis. Empfohlene Werte daraus:

- `upstreamApi.baseUrl`: `http://localhost/api`
- `transports.stdio.enabled`: `false`
- `transports.http.enabled`: `true`
- `transports.http.urls`: `["http://127.0.0.1:8787"]`
- `oauth.enabled`: `true`

Danach in `mcpsettings.json` nur noch diese produktiven Werte setzen:

- `upstreamApi.apiKey`
- `oauth.signingKey`
- `oauth.clients[0].clientSecret`
- optional weitere OAuth-Clients

Der Server stellt dann bereit:

- `POST /mcp`
- `GET /metadata`
- `GET /schema/import/cards`
- `GET /.well-known/oauth-authorization-server`
- `POST /oauth/token`

Damit ist der MCP-Server nicht mehr nur ein per Client gestarteter `stdio`-Prozess, sondern kann
auch als eigenständiger HTTP-Endpoint für MCP-fähige LLM-Clients betrieben werden.

Wenn `transports.http.enabled=false` ist, entfernt die Pipeline einen eventuell vorhandenen
Windows-Dienst wieder und belässt den MCP-Teil im reinen `stdio`-/Client-Start-Modus.
