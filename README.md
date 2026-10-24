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
  spricht mit der Web-API im Namen **eines Benutzers** (OAuth-Anmeldung bei Learny oder persönliches Token, siehe [ADR 0013](docs/adr/0013-mcp-benutzergebundene-anmeldung.md));
  der gemeinsame `X-MCP-Key` ist nur ein Dienstschlüssel ohne Datenzugriff.
- `nginx/` — optionaler Reverse Proxy für TLS-Terminierung im Docker-Betrieb.
- `docker-compose.yml` — Postgres + Web (+ MCP-Server-Profil, + nginx-Profil).

Details: [docs/architecture.md](docs/architecture.md).

## Benutzer, Rollen und Datenschutz

LearnCards ist mehrbenutzerfähig ([ADR 0011](docs/adr/0011-mehrbenutzerarchitektur.md)):

- Jeder Benutzer hat eine eigene interne ID, ein Profil (`/profile`) und **private Module und Karten**; andere Benutzer sehen sie nicht (kein gemeinsamer Kartenbestand).
  Persönliche Lerndaten (Lernstatus, Chat-Verlauf, Quiz-Ergebnisse, Theme) sind ebenfalls benutzerbezogen.
- Rollen: `user`, `admin` (Benutzerverwaltung unter `/admin`), `auditor`. Sie lassen sich in der App oder über Entra-ID-App-Rollen vergeben
  (`Admin.All` → `admin`, [ADR 0012](docs/adr/0012-rollen-aus-entra-app-roles.md)).
- Über MCP arbeitet jeder Benutzer nur in seinem Bereich; Zugänge sind im Profil widerrufbar.
- Ergebnisse der Sicherheitsprüfung: [Prüfbericht](docs/reviews/2026-10-24-mehrbenutzer-pruefbericht.md) (u. a. Hinweis: für Produktion `AUTH_MODE=oidc` explizit setzen).

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
automatisch der lokale Dev-Login (`AUTH_MODE=auto` → `dev`; der Dev-Benutzer ist **Administrator** — nie in Produktion, dort `AUTH_MODE=oidc` setzen), ohne konfigurierten
`OPENAI_API_KEY` laufen Chat und Quiz mit einem klar gekennzeichneten Offline-Fallback.

Alternativ in VS Code in der Debug-Ansicht **App (SQLite)** oder **App (PostgreSQL Docker)** starten (`.vscode/launch.json`;
PostgreSQL läuft dafür im Container aus `docker-compose.dev.yml`, siehe [docs/local-development.md](docs/local-development.md)).

MCP-Server separat starten (z. B. für Claude Desktop):

```bash
cd src/LearnCards.McpServer
dotnet run
```

> **Mehrbenutzer (ADR 0011/0013):** Jeder Benutzer arbeitet über MCP nur in seinem eigenen Bereich. HTTP-Clients melden sich per **OAuth** an (Anmeldung und Zustimmung
> bei Learny), stdio-Clients nutzen ein **persönliches Token** (`LEARNCARDS_MCP_TOKEN`, erzeugt unter `/profile`). Der gemeinsame `MCP_API_KEY` ist nur noch ein
> Dienstschlüssel des MCP-Servers und gewährt keinen Datenzugriff. Zugänge lassen sich im Profil widerrufen. Beim ersten Start nach dem Update `ADMIN_SUBJECTS` setzen und
> unzugeordneten Altbestand unter `/admin` einem Benutzer zuweisen.

Im produktiven IIS-Deployment wird der MCP-Server nicht als Windows-Dienst betrieben, sondern
vom MCP-Client automatisch gestartet. Die GitHub-Action erzeugt dafür im MCP-Zielordner
`learncards-mcp.cmd` und eine passende `claude-desktop.learncards.json`.

Für einen eigenständigen HTTP-Betrieb liegt unter
`src/LearnCards.McpServer/mcpsettings.example.json` eine Beispielkonfiguration. Als
`mcpsettings.json` neben der veröffentlichten EXE abgelegt, kann der Server:

- `stdio` für klassische MCP-Desktop-Clients bedienen
- zusätzlich oder alternativ HTTP unter `/mcp` bereitstellen
- unter `/.well-known/oauth-authorization-server`, `/oauth/authorize`, `/oauth/register`, `/oauth/token` einen OAuth-Ablauf (Authorization Code + PKCE) anbieten,
  bei dem die Anmeldung bei Learny stattfindet und Tokens an den jeweiligen Benutzer gebunden sind
- unter `/metadata` und per MCP-Tool `get_import_schema` das aktuelle Import-Schema des Learn-Servers liefern

Für IIS-/Windows-Dienst-Betrieb gibt es zusätzlich eine produktionsnahe Vorlage unter
`src/LearnCards.McpServer/mcpsettings.iis.example.json`:

- `upstreamApi.baseUrl` zeigt auf `http://localhost/api`
- `stdio` ist deaktiviert
- `http` ist aktiviert auf `http://127.0.0.1:8787`
- OAuth ist aktiviert; `oauth.issuer` muss die öffentliche MCP-URL sein, die Web-App braucht `MCP_PUBLIC_URL` (siehe [ADR 0013](docs/adr/0013-mcp-benutzergebundene-anmeldung.md))

Für ein vollständig repo-basiertes IIS-Deployment liegen zusätzlich diese Dateien unter `deploy/iis/`:

- `deployment.json` als Beispiel für nicht-sensitive Werte
- `web.env` als Template für die Web-App-Konfiguration
- `mcpsettings.json` als Template für die MCP-HTTP-/OAuth-Konfiguration

Die echten Werte kommen beim Deploy aus GitHub `Variables` und `Secrets`.

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
`reference_answer`, `official_sources` sowie optional `slide_number`, `target_time_sec` und
ein statisches `quiz` für den Folien-/Seitenmodus enthalten:

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
      "slide_number": 3,
      "target_time_sec": 90,
      "quiz": [
        {
          "type": "single_choice",
          "question": "Warum ist ein SHA-Digest in CI/CD stabiler als :latest?",
          "options": [
            "Weil :latest nur lokal funktioniert",
            "Weil ein Digest exakt denselben Image-Inhalt referenziert",
            "Weil Digests automatisch gepatcht werden",
            "Weil Digests keine Registry benötigen"
          ],
          "correct_index": 1,
          "explanation": "Ein Digest referenziert ein unveränderliches Artefakt und macht Deployments reproduzierbar."
        }
      ],
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
- `kvno_folien_lernkarten.json` — Foliendeck-Datensatz mit `slide_number`, Zeitziel und vorbereitetem Seitenquiz
- `demo.json` — kompakter PRINCE2-Projektmanagement-Datensatz für Demo- und Testzwecke

Beide Dateien lassen sich direkt über den JSON-Import in der Startseite laden.

## Dokumentation

- [Architektur](docs/architecture.md)
- [Architekturentscheidungen (ADR)](docs/adr/README.md)
- [Entwickeln mit KI-Assistenten](docs/ai-development.md) · [AGENTS.md](AGENTS.md)
- [Lokale Entwicklung (VS Code, PostgreSQL-Docker, MCP-Anmeldung)](docs/local-development.md)
- [Datenbankschema](docs/database-schema.md)
- [Tests](docs/testing.md)
- [Prüfbericht Mehrbenutzerfähigkeit](docs/reviews/2026-10-24-mehrbenutzer-pruefbericht.md)
- [Prompt für das GPT-Projekt (Änderungsaufträge)](docs/gpt-project-prompt.md)
- [Azure-Deployment](docs/azure-deployment.md)
- [IIS-Deployment per GitHub Actions](docs/iis-deployment.md)
- [Migration von der Python-Version](docs/migration-notes.md)

## Design

Eigenes, für Lernumgebungen gestaltetes Farbsystem (gedämpftes Petrol/Salbei als Markenfarbe,
warmes Papierweiß, sanfter Bernstein-Akzent für „Key Facts") mit vollständigem Dark Mode —
siehe `src/LearnCards.Web/wwwroot/app.css`.
