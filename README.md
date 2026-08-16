# LearnCards (.NET 10)

LearnCards ist eine Flashcard-Lernplattform: Blazor-Web-App (Server-Interaktivität) mit
integrierter REST-API, PostgreSQL/SQLite-Datenzugriff, OpenID-Connect-Login (Entra ID) und
einem eigenständigen MCP-Server. Vollständige C#/.NET-10-Portierung der ursprünglichen
FastAPI/React/Python-Lösung — **ohne externe NuGet-Pakete**, damit der Build auch offline
funktioniert (SQLite und PostgreSQL sind direkt implementiert, OpenID Connect und der
OpenAI-Client laufen über den eingebauten `HttpClient`).

## Architektur

- `src/LearnCards.Web/` — Blazor Web App (.NET 10, interaktive Server-Komponenten) + REST-API
  unter `/api` + OIDC-Login unter `/auth/*`. Eine einzige deploybare Einheit.
- `src/LearnCards.McpServer/` — eigenständiger MCP-Server mit `stdio`- und HTTP-Transport,
  spricht mit der Web-API über `X-MCP-Key` und kann optional ein dateibasiertes proprietäres OAuth
  für HTTP-Clients bereitstellen.
- `nginx/` — optionaler Reverse Proxy für TLS-Terminierung im Docker-Betrieb.
- `docker-compose.yml` — Postgres + Web (+ MCP-Server-Profil, + nginx-Profil).

Details: [docs/architecture.md](docs/architecture.md).

## Datenbank: lokal SQLite, produktiv PostgreSQL

Die App wählt den Datenbank-Provider automatisch:

- **Lokal ohne Docker**: `DB_PROVIDER=auto` (Standard) + keine `POSTGRES_HOST` gesetzt →
  SQLite-Datei unter `data/learncards.db`. Kein Docker, keine externe DB nötig.
- **Docker / eigener Linux-Server**: `docker-compose.yml` setzt `DB_PROVIDER=postgres` und
  startet einen PostgreSQL-Container.
- **Azure Web App**: `DB_PROVIDER=postgres` + `POSTGRES_SSLMODE=require` gegen eine externe
  Datenbank (z. B. Azure Database for PostgreSQL Flexible Server) — siehe
  [docs/azure-deployment.md](docs/azure-deployment.md).

Beide Provider sind direkt gegen das jeweilige Wire-Protokoll implementiert (kein ORM, keine
NuGet-Abhängigkeit) und legen beim Start automatisch das Schema an.

## Schnellstart — lokale Entwicklung

Voraussetzung: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
cp .env.example .env     # eigene Werte eintragen (oder die mitgelieferte .env direkt nutzen)
cd src/LearnCards.Web
dotnet run
```

Die App startet unter `http://localhost:5000`. Ohne konfigurierte Entra-ID-Werte greift
automatisch der lokale Dev-Login (`AUTH_MODE=auto` → `dev`), ohne konfigurierten
`OPENAI_API_KEY` laufen Chat und Quiz mit einem klar gekennzeichneten Offline-Fallback.

Alternativ in VS Code: `App (lokal: Web + MCP)` aus der Debug-Ansicht starten
(`.vscode/launch.json`).

MCP-Server separat starten (z. B. für Claude Desktop):

```bash
cd src/LearnCards.McpServer
dotnet run
```

Im produktiven IIS-Deployment wird der MCP-Server nicht als Windows-Dienst betrieben, sondern
vom MCP-Client automatisch gestartet. Die GitHub-Action erzeugt dafür im MCP-Zielordner
`learncards-mcp.cmd` und eine passende `claude-desktop.learncards.json`.

Für einen eigenständigen HTTP-Betrieb liegt unter
`src/LearnCards.McpServer/mcpsettings.example.json` eine Beispielkonfiguration. Als
`mcpsettings.json` neben der veröffentlichten EXE abgelegt, kann der Server:

- `stdio` für klassische MCP-Desktop-Clients bedienen
- zusätzlich oder alternativ HTTP unter `/mcp` bereitstellen
- unter `/.well-known/oauth-authorization-server` und `/oauth/token` ein einfaches proprietäres
  OAuth für Bearer-Token anbieten
- unter `/metadata` und per MCP-Tool `get_import_schema` das aktuelle Import-Schema des Learn-Servers liefern

Für IIS-/Windows-Dienst-Betrieb gibt es zusätzlich eine produktionsnahe Vorlage unter
`src/LearnCards.McpServer/mcpsettings.iis.example.json`:

- `upstreamApi.baseUrl` zeigt auf `http://localhost/api`
- `stdio` ist deaktiviert
- `http` ist aktiviert auf `http://127.0.0.1:8787`
- proprietäres OAuth ist aktiviert und muss nur noch mit echten Secrets befüllt werden

## Docker — eigener Linux-Rechner

```bash
docker compose up --build -d                        # db + web
docker compose --profile production up --build -d   # zusätzlich nginx mit TLS
docker compose --profile mcp run --rm mcp-server     # MCP-Server interaktiv (stdio)
```

Die Web-App ist danach unter `http://<server>:8080` (bzw. `https://<server>` mit nginx-Profil)
erreichbar. Zertifikate für nginx unter `nginx/certs/cert.pem` + `key.pem` ablegen.

## Azure Web App

Die App kann unverändert als Azure App Service (Linux, Container oder Code-Deploy) laufen und
benötigt dafür eine **externe** Datenbank (Azure Database for PostgreSQL Flexible Server).
Schritt-für-Schritt-Anleitung inkl. App-Settings-Mapping: [docs/azure-deployment.md](docs/azure-deployment.md).

## Wichtige lokale URLs

- App + API: `http://localhost:5000`
- Health-Check: `http://localhost:5000/health`

## Karten-JSON

Der JSON-Import akzeptiert weiterhin ein Array von Karten oder ein Objekt mit `cards`. Für
Prüfungsmodus mit Musterlösung und offiziellen Quellen kann jede Karte zusätzlich
`reference_answer` und `official_sources` enthalten:

```json
{
  "cards": [
    {
      "module": "Azure Container",
      "category": "Container",
      "term": ":latest-Problem / SHA-Digest Pinning",
      "question": "Warum ist :latest in Produktion gefährlich und was ist die sichere Alternative?",
      "definition": "Bewegliche Tags wie :latest sind nicht reproduzierbar.",
      "how_it_works": "Ein Digest pinnt exakt das ausgelieferte Image.",
      "context": "Wichtig für CI/CD, Rollbacks und Incident Analysis.",
      "key_fact": "Deployments sollten auf immutable Digests zeigen.",
      "reference_answer": "In Produktion sollte kein beweglicher Tag wie :latest verwendet werden, weil sich der referenzierte Inhalt ändern kann. Stattdessen wird das Image per SHA-Digest gepinnt, damit Deployments reproduzierbar, nachvollziehbar und sicher bleiben.",
      "official_sources": [
        {
          "title": "Docker image digests",
          "url": "https://docs.docker.com/dhi/core-concepts/digests/",
          "publisher": "Docker Docs"
        },
        {
          "title": "Best practices for container image management",
          "url": "https://learn.microsoft.com/azure/container-registry/container-registry-image-tag-version",
          "publisher": "Microsoft Learn"
        }
      ]
    }
  ]
}
```

## Demo-Dateien

Unter `demoodule/` liegen importierbare Beispiel-Workloads:

- `kvno_seed_module.json` — großer Azure-L400-Datensatz mit Quellenverzeichnis pro Karte
- `demo.json` — kompakter PRINCE2-Projektmanagement-Datensatz für Demo- und Testzwecke

Beide Dateien lassen sich direkt über den JSON-Import in der Startseite laden.

## Dokumentation

- [Architektur](docs/architecture.md)
- [Lokale Entwicklung](docs/local-development.md)
- [Azure-Deployment](docs/azure-deployment.md)
- [IIS-Deployment per GitHub Actions](docs/iis-deployment.md)
- [Migration von der Python-Version](docs/migration-notes.md)

## Design

Eigenes, für Lernumgebungen gestaltetes Farbsystem (gedämpftes Petrol/Salbei als Markenfarbe,
warmes Papierweiß, sanfter Bernstein-Akzent für „Key Facts") mit vollständigem Dark Mode —
siehe `src/LearnCards.Web/wwwroot/app.css`.
