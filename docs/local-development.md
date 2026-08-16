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
`AUTH_MODE=auto` bleibt und die OIDC-Werte nicht vollständig sind).

## 2. Starten

```bash
cd src/LearnCards.Web
dotnet run
```

→ `http://localhost:5000`. Erststart legt automatisch eine SQLite-Datenbank unter
`src/LearnCards.Web/data/learncards.db` an (Schema wird beim Boot erzeugt, keine Migrationen
nötig).

## 3. MCP-Server lokal testen

```bash
cd src/LearnCards.McpServer
dotnet run
```

Der Server liest `LEARNCARDS_API_URL` (Standard `http://localhost:5000/api`) und
`LEARNCARDS_MCP_API_KEY`/`MCP_API_KEY` aus der `.env` und spricht per stdio mit MCP-Clients
(z. B. Claude Desktop). Beispielkonfiguration für Claude Desktop (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "learncards": {
      "command": "dotnet",
      "args": ["run", "--project", "/absoluter/pfad/src/LearnCards.McpServer"]
    }
  }
}
```

Für den produktiven Einsatz empfiehlt sich stattdessen `dotnet publish` und der Aufruf der
erzeugten `LearnCards.McpServer.dll` bzw. das Docker-Image (`--profile mcp`).

Wichtig: Der MCP-Server ist ein `stdio`-Server. Er wird deshalb nicht sinnvoll als dauerhafter
Hintergrunddienst betrieben, sondern normalerweise automatisch vom MCP-Client gestartet.
Für das IIS-Deployment erzeugt die Pipeline dafür ein fertiges `learncards-mcp.cmd`.

## 4. MCP-Server als eigenständiger HTTP-Dienst

Wenn der MCP-Server nicht nur per `stdio`, sondern als eigenständiger Prozess laufen soll:

1. `src/LearnCards.McpServer/mcpsettings.example.json` nach `mcpsettings.json` kopieren.
2. `transports.http.enabled` auf `true` setzen.
3. Falls nur HTTP gewünscht ist, `transports.stdio.enabled` auf `false` setzen.
4. Optional `oauth.enabled` aktivieren und `signingKey` plus `clients` befüllen.
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
  - `POST /oauth/token`

Das Tool `get_import_schema` fragt immer das aktuelle Import-Schema vom LearnCards-Server ab.
Dadurch muss ein LLM das Kartenformat nicht erraten oder lokal nachbauen.

## 5. PostgreSQL lokal statt SQLite

Falls du das Verhalten mit PostgreSQL testen möchtest, ohne den vollen Docker-Stack zu starten:

```bash
docker run --rm -d --name learncards-pg \
  -e POSTGRES_USER=learncards -e POSTGRES_PASSWORD=learncards -e POSTGRES_DB=learncards \
  -p 5432:5432 postgres:16-alpine
```

In `.env`:

```
DB_PROVIDER=postgres
POSTGRES_HOST=localhost
POSTGRES_SSLMODE=disable
```

## 6. Tests / Smoke-Checks

```bash
curl http://localhost:5000/health
curl -b cookies.txt http://localhost:5000/api/modules/
```

Ohne Login antwortet `/api/*` mit `401` — entweder über den Browser-Login (`/auth/login`) oder
per `X-MCP-Key`-Header authentifizieren.

## 7. Demo-JSON importieren

Im Ordner `demoodule/` liegen sofort nutzbare Beispiel-Dateien:

- `demoodule/kvno_seed_module.json` — umfangreiche Azure-/Security-Demo
- `demoodule/demo.json` — kompakte PRINCE2-Projektmanagement-Demo

Import über die Startseite:

1. Web-App starten.
2. Im Browser anmelden.
3. Auf `JSON-Import` klicken.
4. Die gewünschte Datei aus `demoodule/` auswählen.
5. Die Analyse prüfen und anschließend den Import bestätigen.

## Bekannte Unterschiede zur Python-Ursprungslösung

Siehe [migration-notes.md](migration-notes.md).
