# Lokale Entwicklung

## Voraussetzungen

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Optional: Docker (nur für den PostgreSQL-Vollbetrieb oder Container-Tests)
- Optional: VS Code mit der C-Dev-Kit-Erweiterung (siehe `.vscode/extensions.json`)

## 1. Konfiguration

```bash
cp .env.example .env
```

Die mitgelieferte `.env` (falls vorhanden) enthält bereits funktionierende Werte für die
Entra-ID-App-Registrierung und den OpenAI-Schlüssel-Platzhalter — für den reinen UI-/API-Test
reicht das Starten ohne weitere Änderungen (Dev-Login greift automatisch, solange
`AUTH_MODE=auto` bleibt und die OIDC-Werte nicht vollständig sind). Der Dev-Benutzer `local-dev-user` ist **Administrator**; weitere Testbenutzer:
`http://localhost:5000/auth/login?devUser=anna`. **Nie in Produktion** (dort `AUTH_MODE=oidc`, siehe [Prüfbericht F1](reviews/2026-10-24-mehrbenutzer-pruefbericht.md)).

## 2. Starten

```bash
cd src/LearnCards.Web
dotnet run
```

→ `http://localhost:5000`. Erststart legt automatisch eine SQLite-Datenbank unter
`src/LearnCards.Web/data/learncards.db` an (Schema wird beim Boot erzeugt, keine Migrationen
nötig).

## 2a. Start aus VS Code (empfohlen)

Im Debug-Dropdown (`.vscode/launch.json`) stehen vier Starter bereit:

| Auswahl | Startet | Datenbank |
|---|---|---|
| **App (SQLite)** | Web (`http://localhost:5000`) + MCP-Server | SQLite-Datei, kein Docker |
| **App (PostgreSQL Docker)** | Web + MCP-Server | PostgreSQL 16 im Container `learncards-dev-db` |
| App (… ) + MCP-Health | wie oben, zusätzlich Browser auf `http://127.0.0.1:8787/health` | |
| Web (SQLite) / Web (PostgreSQL Docker) / MCP-Server (lokal) | einzelne Prozesse | |

Die PostgreSQL-Variante startet vorher automatisch den Container aus [docker-compose.dev.yml](../docker-compose.dev.yml)
(`docker compose up -d --wait`, wartet auf „healthy“) und baut danach. Der Container ist nur an `127.0.0.1:55432` gebunden
(nicht 5432, damit eine lokal installierte PostgreSQL-Instanz nicht stört); Zugangsdaten `learncards` / `learncards` / DB `learncards`.
Das Schema legt die Anwendung beim Start selbst an.

Nützliche Tasks (`Strg+Shift+P` → „Tasks: Run Task“):

- `dev: postgres up` / `dev: postgres down` / `dev: postgres reset (Daten löschen)`
- `dev: postgres psql` — interaktive SQL-Konsole im Container
- `test: integration (SQLite)` / `test: integration (PostgreSQL Docker)` — Integrationstests ([testing.md](testing.md));
  die PostgreSQL-Variante legt dafür die Wegwerf-Datenbank `learncards_test` neu an und berührt die Entwicklungsdatenbank nicht.

Hinweis MCP: Der gemeinsame `MCP_API_KEY` (in den Starten `change-me-mcp-key`) ist nur ein Dienstschlüssel des MCP-Servers und gewährt keinen Datenzugriff.
Jeder Benutzer meldet sich selbst an — siehe Abschnitt „5. MCP-Anmeldung pro Benutzer“ ([ADR 0013](adr/0013-mcp-benutzergebundene-anmeldung.md)).

## 3. MCP-Server lokal testen

```bash
cd src/LearnCards.McpServer
dotnet run
```

Der Server liest `LEARNCARDS_API_URL` (Standard `http://localhost:5000/api`) aus der `.env` und spricht per stdio mit MCP-Clients
(z. B. Claude Desktop). Er arbeitet im Namen **eines Benutzers**: dafür unter `/profile` → „MCP-Verknüpfungen“ ein persönliches Token erzeugen (wird nur einmal angezeigt)
und als `LEARNCARDS_MCP_TOKEN` übergeben. Beispielkonfiguration für Claude Desktop (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "learncards": {
      "command": "dotnet",
      "args": ["run", "--project", "/absoluter/pfad/src/LearnCards.McpServer"],
      "env": {
        "LEARNCARDS_API_URL": "http://localhost:5000/api",
        "LEARNCARDS_MCP_TOKEN": "lcm_…"
      }
    }
  }
}
```

Für den produktiven Einsatz empfiehlt sich stattdessen `dotnet publish` und der Aufruf der
erzeugten `LearnCards.McpServer.dll` bzw. das Docker-Image (`--profile mcp`).

Wichtig: Der MCP-Server ist ein `stdio`-Server. Er wird deshalb nicht sinnvoll als dauerhafter
Hintergrunddienst betrieben, sondern normalerweise automatisch vom MCP-Client gestartet.
Für das IIS-Deployment erzeugt die Pipeline dafür ein fertiges `learncards-mcp.cmd`.

### Umgebungsvariablen des MCP-Servers

Neben `mcpsettings.json` (Pfad über `LEARNCARDS_MCP_CONFIG`) überschreiben diese Variablen die Konfiguration:

| Variable | Wirkung |
|---|---|
| `LEARNCARDS_MCP_CONFIG` | Pfad zur `mcpsettings.json` (sonst Suche im Arbeitsverzeichnis, darüber und neben der EXE) |
| `LEARNCARDS_API_URL` | Basis-URL der Web-API, z. B. `http://localhost:5000/api` |
| `LEARNCARDS_MCP_API_KEY` / `MCP_API_KEY` | Dienstschlüssel (nur Code-Tausch und Schema, kein Datenzugriff) |
| `LEARNCARDS_MCP_TOKEN` | persönliches Learny-Token des Benutzers für den stdio-Transport ([ADR 0013](adr/0013-mcp-benutzergebundene-anmeldung.md)) |
| `LEARNCARDS_WEB_PUBLIC_URL` | öffentliche URL der Web-Oberfläche für die OAuth-Anmeldung im Browser |
| `LEARNCARDS_MCP_STDIO` / `LEARNCARDS_MCP_HTTP` | `true`/`false`: Transporte ein-/ausschalten |
| `LEARNCARDS_MCP_HTTP_URLS` | Listener des HTTP-Transports, Semikolon-getrennt (aktiviert HTTP), z. B. `http://127.0.0.1:8787` |

## 4. MCP-Server als eigenständiger HTTP-Dienst

Wenn der MCP-Server nicht nur per `stdio`, sondern als eigenständiger Prozess laufen soll:

1. `src/LearnCards.McpServer/mcpsettings.example.json` nach `mcpsettings.json` kopieren.
2. `transports.http.enabled` auf `true` setzen.
3. Falls nur HTTP gewünscht ist, `transports.stdio.enabled` auf `false` setzen.
4. Für die Anmeldung pro Benutzer `oauth.enabled` auf `true` und `oauth.issuer` auf die öffentliche MCP-URL setzen (`signingKey`/`tokenLifetimeMinutes` werden nicht mehr verwendet).
5. Den Server starten:

```bash
cd src/LearnCards.McpServer
dotnet run
```

Dann stehen standardmäßig folgende Endpunkte bereit:

- `GET /health`
- `POST /mcp`
- `GET /metadata`
- `GET /schema/import/cards`
- optional bei aktiviertem OAuth:
  - `GET /.well-known/oauth-authorization-server`
  - `POST /oauth/token`, `POST /oauth/register`, `GET /oauth/authorize`, `GET /oauth/callback`

## 5. MCP-Anmeldung pro Benutzer

Jeder Benutzer sieht über MCP nur seine eigenen Module und Karten ([ADR 0013](adr/0013-mcp-benutzergebundene-anmeldung.md)).

**HTTP mit OAuth (VS Code, MCP Inspector, Claude):**

1. Web-App und MCP-Server starten (VS Code: „App (SQLite)“ oder „App (PostgreSQL Docker)“). Die Starts setzen `MCP_PUBLIC_URL=http://127.0.0.1:8787` und aktivieren OAuth in `.vscode/mcpsettings.debug.json`.
2. Im MCP-Client den Server `http://127.0.0.1:8787/mcp` eintragen. Der Client registriert sich automatisch, öffnet den Browser bei Learny (Anmeldung per Entra ID bzw. Dev-Login) und fragt nach der Zustimmung.
3. Nach „Erlauben“ arbeitet der Client in deinem Namen. Prüfen: Tool `whoami` aufrufen. Widerruf: `/profile` → MCP-Verknüpfungen → Widerrufen.
4. Mit `AUTH_MODE=oidc` muss `MCP_PUBLIC_URL` gesetzt sein, im Dev-Modus ist `http://127.0.0.1:8787` voreingestellt. Ist die Learny-Weboberfläche unter einer anderen URL als `http://localhost:5000` erreichbar,
   `LEARNCARDS_WEB_PUBLIC_URL` für den MCP-Server setzen.

**MCP Inspector:** `npx @modelcontextprotocol/inspector` → Transport „Streamable HTTP“/„HTTP“, URL `http://127.0.0.1:8787/mcp`, „OAuth“ wählen und den Ablauf durchlaufen. Alternativ „Authorization: Bearer <persönliches Token>“ eintragen.

**stdio:** persönliches Token unter `/profile` erzeugen und `LEARNCARDS_MCP_TOKEN` setzen (siehe Abschnitt 3).

Das Tool `get_import_schema` fragt immer das aktuelle Import-Schema vom LearnCards-Server ab.
Dadurch muss ein LLM das Kartenformat nicht erraten oder lokal nachbauen.

## 6. PostgreSQL lokal statt SQLite

Falls du das Verhalten mit PostgreSQL testen möchtest, ohne den vollen Docker-Stack zu starten, nutze die Entwicklungs-Datenbank (Port **55432**, damit eine lokale PostgreSQL-Installation nicht stört):

```bash
docker compose -f docker-compose.dev.yml up -d --wait
```

In `.env` (oder einfach die VS-Code-Konfiguration „App (PostgreSQL Docker)“ nutzen, die das selbst setzt):

```
DB_PROVIDER=postgres
POSTGRES_HOST=127.0.0.1
POSTGRES_PORT=55432
POSTGRES_USER=learncards
POSTGRES_PASSWORD=learncards
POSTGRES_DB=learncards
POSTGRES_SSLMODE=disable
```

## 7. Tests / Smoke-Checks

```bash
curl http://localhost:5000/health
curl -b cookies.txt http://localhost:5000/api/modules/
```

Ohne Login antwortet `/api/*` mit `401`. Authentifizieren per Browser-Login (`/auth/login`) oder mit einem persönlichen MCP-Token
(`Authorization: Bearer lcm_…`, nur Daten-Routen). Der `X-MCP-Key` gewährt **keinen** Datenzugriff (nur Schema und Code-Tausch).
Automatisierte Integrationstests: [testing.md](testing.md).

## 7a. Freigaben und globale Module ausprobieren

Mit zwei Browser-Sitzungen (normal + privates Fenster) und Dev-Login: `http://localhost:5000/auth/login?devUser=anna` bzw. `?devUser=ben`; der Standardbenutzer `local-dev-user` ist Administrator.

1. Als *anna* ein Modul importieren, im Modul **Modul freigeben** wählen, *ben* suchen und freigeben.
2. Als *ben* unter **Mit mir geteilt** lernen und ein Quiz machen; anna sieht Bens Ergebnisse nicht.
3. Als *anna* **Zur globalen Veröffentlichung einreichen**; als Administrator unter `/admin` → *Veröffentlichungsvorschläge* Vorschau ansehen und **Veröffentlichen**.
4. Unter **Globale Module** lernen alle Benutzer; Administrator und Auditor (`/admin`, Rolle `auditor`) sehen unter *Globale Lernergebnisse* die minimierten Ergebnisse.

Hintergrund und Regeln: [ADR 0015](adr/0015-freigaben-globale-lernmodule.md).

## 8. Demo-JSON importieren

Im Ordner `demoodule/` liegen sofort nutzbare Beispiel-Dateien:

- `demoodule/kvno_seed_module.json` — umfangreiche Azure-/Security-Demo
- `demoodule/demo.json` — kompakte PRINCE2-Projektmanagement-Demo

Import über die Startseite:

1. Web-App starten.
2. Im Browser anmelden (der Import landet in **deinem** privaten Bestand).
3. Auf `JSON-Import` klicken.
4. Die gewünschte Datei aus `demoodule/` auswählen.
5. Die Analyse prüfen und anschließend den Import bestätigen.

## Bekannte Unterschiede zur Python-Ursprungslösung

Siehe [migration-notes.md](migration-notes.md).

## Betriebsmodus lokal (ADR 0026)

Unter `dotnet run`/VS Code gilt `ASPNETCORE_ENVIRONMENT=Development`: Ohne OIDC-Konfiguration wird der Dev-Login verwendet (`AUTH_MODE=auto`). Startest du die Anwendung anders (z. B. `dotnet LearnCards.Web.dll`, Container), gilt `Production`: dann ist `AUTH_MODE=dev` **ausdrücklich** zu setzen, sonst bricht der Start mit einer Meldung ab.

## MCP_TOKEN_KEY lokal (ADR 0025)

`MCP_TOKEN_KEY` (Base64, ≥ 32 Byte) ist Pflicht. Unter `dotnet run` (Development) wird ohne Angabe einmalig `data/mcp-token.key` erzeugt und wiederverwendet. Für `Production`-Starts (Container, `dotnet LearnCards.Web.dll`) den Schlüssel selbst setzen: `openssl rand -base64 32`. Die Datei `data/mcp-token.key` nie committen (Verzeichnis `data/` ist ignoriert).
