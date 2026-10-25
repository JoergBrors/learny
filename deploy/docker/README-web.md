# Learny — `joergb23/learny-web`

**Learny** (LearnCards) ist eine selbst hostbare Lernplattform für Karteikarten und Lernmodule: Weboberfläche und REST-API in **einem** Container.

- Quellcode, Dokumentation, Issues: <https://github.com/JoergBrors/learny>
- Vollständige Installationsanleitung mit Compose-Datei, `.env.example` und Variablentabelle: <https://github.com/JoergBrors/learny/tree/main/deploy/docker> (als `learny-docker-<version>.zip` auch an jedem [GitHub Release](https://github.com/JoergBrors/learny/releases))
- Zugehöriger MCP-Server: [`joergb23/learny-mcp`](https://hub.docker.com/r/joergb23/learny-mcp)

## Funktionen

- Lernmodule und Karteikarten (Text, Bilder, formatierter Text), Folienmodus, JSON-Import/-Export
- Quiz: Karten- und Modulquiz, Fragenpool, Quizkonfiguration, serverseitige Bewertung
- KI-Chat, Quiz-Generierung und Bewertung über eine OpenAI-kompatible API — **optional**, ohne Schlüssel läuft ein gekennzeichneter Offline-Modus
- Mehrbenutzerbetrieb mit Anmeldung über **Microsoft Entra ID (OIDC)**, Rollen (Benutzer, Administrator, Auditor); Module sind privat, explizit freigebbar oder global veröffentlichbar
- Zertifikate als **Open Badges 3.0** (signiert), optional mit Verankerung der Prüfsummen auf Polygon (kein Personenbezug on-chain)
- Bildspeicher wahlweise in der Datenbank, lokal, Azure Blob, S3 oder SharePoint
- Zugang für KI-Clients über den optionalen MCP-Server

## Image

| | |
|---|---|
| Plattformen | `linux/amd64`, `linux/arm64` |
| Tags | `1.2.3` (feste Version), `latest` (letzte stabile Version); Vorabversionen nur mit eigenem Tag |
| Basis | `mcr.microsoft.com/dotnet/aspnet:10.0` (Ubuntu), Multi-Stage-Build — kein SDK im Image |
| Benutzer | unprivilegiert (UID 1654) |
| Port | `8080` (HTTP; HTTPS terminiert ein Reverse Proxy) |
| Volume | `/app/data` — Data-Protection-Schlüssel, Badge-Signaturschlüssel, lokale Bilder, ggf. SQLite |
| Healthcheck | `GET /health` |
| Geheimnisse | keine im Image — alles über Umgebungsvariablen |

## Architektur

Blazor Web App (Server-Interaktivität), REST-API unter `/api`, Datenbank-Abstraktion für **PostgreSQL** (empfohlen für den Betrieb) und SQLite (lokal/klein). Eine deploybare Einheit; der MCP-Server ist ein getrennter API-Client.

## Schnellstart mit Docker Compose

```bash
mkdir learny && cd learny
# docker-compose.yml und .env.example aus dem Paket (siehe Link oben) hierher legen
cp .env.example .env && chmod 600 .env     # POSTGRES_PASSWORD und OIDC_* eintragen
docker compose pull
docker compose up -d
docker compose ps                           # alle Dienste "healthy"
```

Oberfläche: `http://localhost:8080` (nur lokal gebunden). Für den Zugriff von außen einen Reverse Proxy mit HTTPS davorschalten.

Nur Container, ohne Compose (PostgreSQL muss bereits laufen):

```bash
docker run -d --name learny-web -p 127.0.0.1:8080:8080 \
  -e DB_PROVIDER=postgres -e POSTGRES_HOST=<host> -e POSTGRES_DB=learncards \
  -e POSTGRES_USER=learncards -e POSTGRES_PASSWORD=<passwort> \
  -e AUTH_MODE=oidc -e OIDC_ISSUER=https://login.microsoftonline.com/<tenant>/v2.0 \
  -e OIDC_CLIENT_ID=<client-id> -e OIDC_CLIENT_SECRET=<secret> \
  -e PUBLIC_BASE_URL=https://learny.example.org \
  -v learny_data:/app/data joergb23/learny-web:latest
```

## Wichtige Umgebungsvariablen

Die vollständige Liste (alle Variablen, Pflicht/Standard) steht in der [Installationsanleitung](https://github.com/JoergBrors/learny/blob/main/deploy/docker/README.md#variablen).

| Variable | Pflicht | Standard | Bedeutung |
|---|---|---|---|
| `DB_PROVIDER` | nein | `auto` (Image: ohne `POSTGRES_HOST` → SQLite) | `postgres` oder `sqlite` |
| `POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | bei PostgreSQL | Port `5432` | Verbindung zur PostgreSQL-Datenbank |
| `POSTGRES_SSLMODE` | nein | `prefer` | z. B. `disable` (internes Netz) oder `require` |
| `AUTH_MODE` | **ja, setzen** | `auto` | `oidc` (Produktion) oder `dev` (Login ohne Passwort, nur lokal) |
| `OIDC_ISSUER`, `OIDC_CLIENT_ID`, `OIDC_CLIENT_SECRET` | bei `oidc` | — | Microsoft Entra ID |
| `OIDC_AUDIENCE`, `OIDC_WEB_SCOPE`, `OIDC_ROLE_MAP`, `ADMIN_SUBJECTS` | nein | — | Zielgruppe, Scopes, Rollenzuordnung, Admin-Bootstrap |
| `PUBLIC_BASE_URL` | in Produktion | leer | öffentliche URL (Badge-Aussteller, Prüf-Links) |
| `OPENAI_API_KEY`, `OPENAI_MODEL`, `OPENAI_BASE_URL` | nein | — / `gpt-4o-mini` / OpenAI | KI-Funktionen |
| `MCP_TOKEN_KEY` | **ja (immer)** | — | Hauptschlüssel (Base64, ≥ 32 Byte, `openssl rand -base64 32`) für MCP-Tokens, Einmal-Codes und OAuth-Clients in der Datenbank (ADR 0025); nie in der Datenbank/Backup, ohne ihn startet Learny nicht |
| `MCP_API_KEY`, `MCP_PUBLIC_URL`, `MCP_TOKEN_DAYS` | bei MCP | — / — / `30` | Anbindung des MCP-Servers |
| `BADGE_SIGNING_KEY_PEM` | nein | leer | Signaturschlüssel der Badges (leer = wird im Volume erzeugt) |
| `IMAGE_STORAGE_PROVIDER` | nein | `database` | `database`, `local`, `azureblob`, `s3`, `sharepoint` (+ providerspezifische `IMAGE_*`-Variablen) |
| `DATA_PROTECTION_DIR` | nein | `data/keys` (= `/app/data/keys`) | Ablage der Data-Protection-Schlüssel (auf dem Volume lassen) |

> **Sicherheit (ADR 0026):** `AUTH_MODE` immer ausdrücklich setzen. `dev` meldet jeden ohne Passwort als Administrator an. Im Container (`Production`) startet Learny mit `auto`, leerem oder falschem Wert und ohne vollständige OIDC-Angaben **nicht**, statt still in den Dev-Login zu fallen; `AUTH_MODE=dev` ist nur zum lokalen Ausprobieren gedacht und schreibt eine Warnung ins Log. Eine gesetzte, aber nicht lesbare `ENV_FILE` verhindert ebenfalls den Start.

## PostgreSQL

Learny legt Tabellen beim Start selbst an (`CREATE IF NOT EXISTS`) und ergänzt sie bei Updates. Die Compose-Datei startet `postgres:16-alpine` mit Volume `postgres_data` ohne veröffentlichten Port. Eine externe Instanz wird über `POSTGRES_HOST`/`POSTGRES_*` angebunden (`POSTGRES_SSLMODE=require` über öffentliche Netze).

## Microsoft Entra ID einrichten (Kurzfassung)

1. App-Registrierung anlegen; Umleitungs-URI (Web): `https://<Adresse>/auth/callback`.
2. Geheimen Clientschlüssel erzeugen (Wert → `OIDC_CLIENT_SECRET`).
3. `OIDC_CLIENT_ID` = Anwendungs-ID, `OIDC_ISSUER` = `https://login.microsoftonline.com/<Mandanten-ID>/v2.0`.
4. Optional App-Rolle `Admin.All` (Wert) zuweisen → Administrator (`OIDC_ROLE_MAP`), oder `ADMIN_SUBJECTS` für die Ersteinrichtung.

Schritt-für-Schritt: [Installationsanleitung](https://github.com/JoergBrors/learny/blob/main/deploy/docker/README.md#microsoft-entra-id-einrichten).

## Daten und Updates

- **Persistenz:** Datenbank (PostgreSQL-Volume) und `/app/data` (Volume). Container-Neustarts und Updates verlieren keine Daten; `docker compose down -v` löscht die Volumes.
- **Update:** neue Version in `LEARNY_VERSION` eintragen (oder `latest`), dann `docker compose pull && docker compose up -d`. Vorher Backup anlegen (Anleitung: Abschnitt „Backup“ der Installationsanleitung).
- **Badge-Schlüssel:** `/app/data/badge-signing-key.pem` sichern — ohne ihn sind ausgestellte Zertifikate nicht mehr prüfbar. Fehlt er bei einer bereits genutzten Installation, bricht Learny den Start ab, statt still einen neuen zu erzeugen.

## MCP (optional)

Mit dem Image [`joergb23/learny-mcp`](https://hub.docker.com/r/joergb23/learny-mcp) (Compose-Profil `mcp`) greifen KI-Clients per Model Context Protocol auf Learny zu. Die Web-App benötigt dafür `MCP_API_KEY` (Dienstschlüssel, gewährt nie Datenzugriff) und `MCP_PUBLIC_URL` (erlaubte öffentliche MCP-URL für die Anmeldung). Benutzer authentifizieren sich mit ihrem eigenen Token.

## Sicherheitshinweise

- Hinter HTTPS betreiben (Reverse Proxy). Die Anwendung wertet `X-Forwarded-*` aus — den Port **nicht** direkt ins Internet stellen.
- PostgreSQL nie veröffentlichen; starke, zufällige Passwörter; `.env` mit Rechten 600.
- Keine Geheimnisse in Images oder Compose-Dateien; Zugangsdaten regelmäßig erneuern (Entra-Clientschlüssel laufen ab).
- Benutzerdaten sind pro Benutzer getrennt; Administratoren und Auditoren sehen private Module nicht automatisch.
- Das Image enthält keine Datenbank, keine Schlüssel und keine Zugangsdaten.

Lizenz: MIT · Quelle: <https://github.com/JoergBrors/learny>

## Neu: Module und Themen als JSON (ab Version 0.1.11)

Module (Karten, Bilder, Quiz, Konfiguration) und Themen (Lernpfade aus eigenen Modulen mit Modul- und Abschlussprüfung) lassen sich vollständig als JSON exportieren und importieren (`learny-module`, `learny-topic`, stabile Modulreferenz `reference_id`/`module_ref`); globale Module werden vor der Verwendung in Themen in eigene Module geklont. Importe erzeugen private Objekte bzw. Entwürfe und veröffentlichen nie; Dateien sind auf 30 MB begrenzt (Kestrel-Standard). Beim Update wird die Datenbank automatisch um `modules.reference_id` ergänzt (idempotent, keine Konfigurationsänderung). Schemas: `/api/schema/import/module`, `/api/schema/import/topic`. Details: [ADR 0028](https://github.com/JoergBrors/learny/blob/main/docs/adr/0028-themenmodule-json-und-mcp-verwaltung.md).
