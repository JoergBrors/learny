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

- Jeder Benutzer hat eine eigene interne ID, ein Profil (`/profile`) und **private Module und Karten**; andere Benutzer sehen sie nicht.
  Persönliche Lerndaten (Lernstatus, Chat-Verlauf, Quiz-Ergebnisse, Theme) sind ebenfalls benutzerbezogen.
- **Freigaben und globale Module** ([ADR 0015](docs/adr/0015-freigaben-globale-lernmodule.md)): Module lassen sich gezielt für einzelne Benutzer freigeben (nur Lernen, widerrufbar) und zur globalen Veröffentlichung einreichen;
  Administratoren prüfen und veröffentlichen eine eingefrorene, versionierte Kopie. Die Modulübersicht trennt *Meine Module*, *Mit mir geteilt* und *Globale Module*. Ergebnisse gehören immer den Lernenden; Auditoren und Administratoren sehen globale Ergebnisse nur lesend.
- Rollen: `user`, `admin` (Benutzerverwaltung und Veröffentlichung unter `/admin`), `auditor` (lesende Einsicht in globale Inhalte und Ergebnisse). Sie lassen sich in der App oder über Entra-ID-App-Rollen vergeben
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

## Docker — fertige Images von Docker Hub

Ohne Quellcode installieren: `joergb23/learny-web` und `joergb23/learny-mcp` (linux/amd64 + arm64) mit Compose-Paket, `.env.example` und Variablentabelle unter [deploy/docker/](deploy/docker/README.md) (auch als `learny-docker-<version>.zip` an jedem GitHub Release). Entscheidung: [ADR 0024](docs/adr/0024-docker-hub-veroeffentlichung.md).

## Docker — aus dem Quellcode bauen (eigener Linux-Rechner)

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
ein `quiz[]` mit festen Quizfragen (Karten- und Modulquiz, siehe unten) enthalten:

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

## Bilder in Karten

Karten können optional bis zu 5 Bilder enthalten (PNG, JPEG oder WebP, je höchstens 2 MiB dekodiert). Die Bilder stehen als Base64 im Feld `images` und werden direkt auf der Karte angezeigt — ohne KI-Schlüssel, ohne externen Speicher:

```json
"images": [
  {
    "mime_type": "image/png",
    "data_base64": "iVBORw0KGgoAAAANSUhEUgAAACAAAAAQCAIAAAD4YuoOAAAAI0lEQVR42mPg94mhKWIAYq3ls2mERi0YtWDUglELRpIFNEUAOleXE3NVou0AAAAASUVORK5CYII=",
    "alt_text": "Farbige Beispielfläche (32 × 16 Pixel)",
    "caption": "Beispielbild"
  }
]
```

`data_base64` ist reines Base64 ohne `data:`-Präfix (PowerShell: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("bild.png"))`). Fehlendes `images` bedeutet keine Bilder; beim Überschreiben bleiben vorhandene Bilder erhalten, `"images": []` entfernt sie.
Fehlerhafte Bilder lehnen die betroffene Karte mit einer Begründung ab. Details, Limits und Hinweise für KI-Assistenten: Hilfe-Seite der Anwendung und [ADR 0016](docs/adr/0016-bilder-in-lernkarten.md).

### Formatierte Texte

Die Textfelder `definition`, `how_it_works`, `context`, `key_fact` und `reference_answer` verstehen eine kleine, sichere Formatierung (Markdown-Teilmenge, **kein HTML**): `**fett**`, `*kursiv*`, `++unterstrichen++`, `# Überschrift`, `- Liste`, `1. Nummerierung`, `[Text](https://…)`; Absatz = Leerzeile. Reiner Text bleibt gültig. Die Syntax erklärt das Live-Schema (`rich_text_rules`) und die Hilfeseite; ausgegeben wird sie ausschließlich serverseitig gesichert ([ADR 0020](docs/adr/0020-sichere-visuelle-bearbeitung-formatierter-lerninhalte.md)).

Im Browser bearbeitest du Karten, Texte, Bilder und Quizfragen visuell: Modulseite → **✏ Karten bearbeiten** (nur eigene Module).

### Quizfragen (`quiz[]`)

`quiz` ist optional (fehlt es oder ist es `[]`, hat die Karte keine festen Fragen); alte Dateien mit nur `single_choice` — auch ohne `type` — bleiben gültig. Pflicht je Eintrag: `question` und `explanation`.

| `type` | Felder | Bewertung |
|---|---|---|
| `single_choice` (Standard) | genau 4 `options`, `correct_index` (0-basiert) | eine richtige Option |
| `multiple_response` | 3–6 `options`, `correct_indexes` (alle richtigen, 0-basiert, eindeutig) | nur die exakt richtige Menge zählt |
| `free_text` | keine `options`; `explanation` ist die Musterlösung | KI-Bewertung bzw. lokale Heuristik ohne KI |

```json
"quiz": [
  { "type": "single_choice", "question": "Welche Antwort stimmt?", "options": ["A", "B", "C", "D"], "correct_index": 1, "explanation": "B stimmt." },
  { "type": "multiple_response", "question": "Welche sind Primzahlen?", "options": ["2", "3", "4", "6"], "correct_indexes": [0, 1], "explanation": "2 und 3." },
  { "type": "free_text", "question": "Erkläre den Begriff.", "explanation": "Musterlösung." }
]
```

Das quiz[] enthält nur die **festen** Fragen. KI-generierte (und künftig im Editor erstellte) Fragen liegen im Fragenpool des Moduls ([ADR 0019](docs/adr/0019-einheitliches-quizsystem.md)) und gehören nicht zum Import-JSON. Das exakte, aktuelle Format liefert `GET /api/schema/import/cards` (MCP: `get_import_schema`); Vorlagen gibt es auf der Hilfeseite.

## Demo-Dateien

Unter `demoodule/` liegen importierbare Beispiel-Workloads:

- `demo.json` — kompakter PRINCE2-Projektmanagement-Datensatz mit Beispielbildern und Quizfragen aller drei Typen (Demo- und Testzwecke)

Die Datei lässt sich direkt über den JSON-Import in der Startseite laden. Weitere Vorlagen (Minimal, Vollständig, Folien, Karte mit Bild) stehen auf der Hilfeseite zum Download bereit (`src/LearnCards.Web/wwwroot/templates/`).

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

## MCP-Tokens und Schlüssel (ADR 0025)

`MCP_TOKEN_KEY` ist eine **Pflichtvariable** (Base64, ≥ 32 Byte, `openssl rand -base64 32`; nur in `.env`/Geheimnisverwaltung): Er schützt MCP-Tokens, Einmal-Codes und gespeicherte OAuth-Clients in der Datenbank (HMAC, Zeilen-MAC, AES-256-GCM). Auch mit Zugriff auf PostgreSQL lassen sich so keine MCP-Zugänge ablesen, herstellen oder verändern. Siehe [ADR 0025](docs/adr/0025-sichere-speicherung-von-mcp-tokens.md).

## Themen und Lernpfade

Unter **Themen** (`/topics`) fasst du mehrere Lernmodule zu einem Lernpfad zusammen, legst Modulprüfungen und eine Abschlussprüfung fest, veröffentlichst unveränderliche Versionen und teilst oder weist Themen anderen Benutzern zu. Globale Themenversionen benötigen die Freigabe eines Administrators. Details: [ADR 0027](docs/adr/0027-themen-lernpfade-versionierung-und-abschlusspruefungen.md). MCP-Clients können Themen lesen (`list_topics`, `get_topic`, `get_topic_exam_structure`) und eigene Themenentwürfe per JSON verwalten (siehe unten, ADR 0028).

### Module und Themen als JSON, Klonen und MCP

- **Module** (`/`) enthalten Lernkarten und Quizfragen; **Themen** (`/topics`) sind Lernpfade aus *eigenen* Modulen. Globale Module lassen sich mit **In eigene Module klonen** in eine private Kopie überführen und dann in Themen verwenden.
- Module sind vollständig als JSON exportier- und importierbar (`learny-module`: Karten, Bilder, Quiz, Konfiguration, Pool-Fragen); Themen als `learny-topic` (stabile Modulreferenz `module_ref` + Anzeigename, Reihenfolge, Prüfungen, Themenfragen, optional mit eingebetteten Modulen). Importe erzeugen private Objekte bzw. einen Themenentwurf und veröffentlichen nie. Schemas: `GET /api/schema/import/module` und `/api/schema/import/topic`; Vorlagen unter `/templates/module-template.json` und `/templates/topic-template.json`.
- **Karten über MCP:** `get_card` liest eine Karte vollständig (inkl. `quiz[]` und Bilder als Base64), `update_card` ändert eine Karte **eigener privater Module** (vorher `get_import_schema` und `get_card`; `images` fehlend = Bilder bleiben, `images=[]` = entfernen; `expected_updated_at` für Konflikterkennung), `export_cards` exportiert die Karten eines Moduls als kanonisches `{"cards":[…]}` in Stapeln (Standard 50, höchstens 200 Karten bzw. rund 4 MiB Bilder je Aufruf; `next_offset` bis `has_more=false`) — direkt in `import_cards` einsetzbar. REST: `GET/PUT /api/cards/{id}`, `GET /api/modules/{id}/export/cards`.
- MCP-Werkzeuge: `get_topic_import_schema`, `create_topic`, `import_topic`, `update_topic`, `export_topic`, `get_module_import_schema`, `export_module`, `import_module`, `clone_global_module` (zusätzlich zu `list_topics`, `get_topic`, `get_topic_exam_structure`). Sie wirken nur auf eigene Inhalte; Veröffentlichen, Freigaben, Zuweisungen und Administration sind über MCP nicht möglich. Details: [ADR 0028](docs/adr/0028-themenmodule-json-und-mcp-verwaltung.md).

### Freigaben, Pflichtthemen und Themen-Admin

- **Global** (für alle sichtbar, freiwillig), **freigegeben** (ausdrücklich berechtigte Benutzer), **zugewiesen** (Admin/Themen-Admin, konkrete Version) und **verpflichtend** (Zuweisung mit Pflicht) sind getrennte Begriffe; nur eine ausdrückliche Pflichtzuweisung erscheint auf der Startseite unter „Meine verpflichtenden Themen“.
- Wer ein Thema lernen darf, kann alle Module der Version öffnen und die Prüfungen ablegen; die privaten Ursprungsmodule bleiben geschützt.
- Rolle **Themen-Admin** (`themen_admin`, durch Administratoren vergeben): Themen zuweisen und Lernstatus lesen unter „Zuweisungen“ (`/topic-admin`). Auditoren lesen den Lernstatus. Details: [ADR 0029](docs/adr/0029-themenzugriff-pflichtzuweisungen-und-themenadministration.md).
- Das IIS-Deployment startet den MCP-HTTP-Server nach dem Dateiaustausch neu und prüft ihn (siehe [docs/iis-deployment.md](docs/iis-deployment.md)).
