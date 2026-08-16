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

## Repository Variables

Unter GitHub → `Settings` → `Secrets and variables` → `Actions` → `Variables` anlegen:

- `IIS_SITE_NAME`
  Beispiel: `LearnCards`
- `IIS_APP_POOL`
  Beispiel: `LearnCardsAppPool`
- `IIS_PHYSICAL_PATH`
  Beispiel: `C:\inetpub\LearnCards`
- `IIS_BINDING_URL`
  Optional für Health-Check, z. B. `https://learncards.contoso.local`
- `IIS_PORT`
  Optional, Standard `80`
- `IIS_HOST_HEADER`
  Optional, z. B. `learncards.contoso.local`
- `ASPNETCORE_HOSTING_BUNDLE_URL`
  Optional, aber empfohlen. Offizielle Download-URL für das .NET 10 Hosting Bundle von Microsoft.
- `MCP_PHYSICAL_PATH`
  Optional, aber empfohlen. Zielordner für den veröffentlichten MCP-Server, z. B. `C:\tools\LearnCards.McpServer`

## Optional: App-Konfiguration auf dem IIS-Server

Die GitHub Action deployt nur die Anwendung. Umgebungsvariablen wie:

- `DB_PROVIDER`
- `POSTGRES_HOST`
- `POSTGRES_USER`
- `POSTGRES_PASSWORD`
- `POSTGRES_DB`
- `POSTGRES_SSLMODE`
- `AUTH_MODE`
- `OIDC_*`
- `OPENAI_API_KEY`
- `MCP_API_KEY`

sollten auf dem Server separat konfiguriert werden, z. B.:

- als System-Umgebungsvariablen
- per IIS-App-Pool-/Site-Konfiguration
- oder per `web.config`, falls ihr das bewusst so betreiben wollt

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
9. App Pool und Website werden wieder gestartet.
10. Optional wird `/health` geprüft, wenn `IIS_BINDING_URL` gesetzt ist.

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
- Der MCP-Server wird bewusst **nicht als Windows-Dienst** gestartet, weil er technisch ein
  `stdio`-MCP-Server ist und erst mit einem verbundenen MCP-Client sinnvoll arbeitet.
- Die Pipeline erzeugt stattdessen ein fertiges Launcher-Skript. Ein Client wie Claude Desktop kann dieses
  Skript automatisch starten, sodass kein manueller Konsolenstart nötig ist.

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
5. Den Server dann direkt über `LearnCards.McpServer.exe` oder einen Windows-Dienst/Task Scheduler starten.

Der Server stellt dann bereit:

- `POST /mcp`
- `GET /metadata`
- `GET /schema/import/cards`
- `GET /.well-known/oauth-authorization-server`
- `POST /oauth/token`

Damit ist der MCP-Server nicht mehr nur ein per Client gestarteter `stdio`-Prozess, sondern kann
auch als eigenständiger HTTP-Endpoint für MCP-fähige LLM-Clients betrieben werden.
