# Learny MCP-Server — `joergb23/learny-mcp`

Der **Learny MCP-Server** verbindet KI-Clients (z. B. Claude Desktop, andere MCP-fähige Anwendungen) per [Model Context Protocol](https://modelcontextprotocol.io) mit einer **Learny**-Installation: Module und Karten anlegen und lesen, Lerninhalte importieren, Quizfunktionen nutzen.

- Quellcode und Dokumentation: <https://github.com/JoergBrors/learny>
- Installationsanleitung mit Compose-Datei und Variablentabelle: <https://github.com/JoergBrors/learny/tree/main/deploy/docker>
- Web-Anwendung: [`joergb23/learny-web`](https://hub.docker.com/r/joergb23/learny-web)

## Zweck und Architektur

Der MCP-Server ist ein **eigenständiger API-Client** der Learny-Web-API (er greift nie direkt auf die Datenbank zu). Er bietet:

- **HTTP-Transport** `POST /mcp` für Dauerbetrieb hinter einem Reverse Proxy (Claude-Web, Remote-Clients),
- **stdio-Transport** für Clients, die den Container direkt starten,
- `GET /health`, `GET /metadata`, `GET /schema/import/cards` (öffentliches Karten-Import-Schema) und OAuth-Endpunkte (`/.well-known/…`, `/oauth/authorize`, `/oauth/token`, `/oauth/register`, `/oauth/callback`).

Plattformen: `linux/amd64`, `linux/arm64` · Tags: `1.2.3`, `latest` (nur stabile Versionen) · Basis: `mcr.microsoft.com/dotnet/aspnet:10.0` · unprivilegierter Benutzer (UID 1654) · Port `8787` (HTTP) · keine Geheimnisse im Image.

## Voraussetzungen

1. Eine laufende **Learny-Web-Installation** (`joergb23/learny-web`), erreichbar für den MCP-Container (im Compose-Netzwerk: `http://web:8080`).
2. Beide Seiten kennen denselben **Dienstschlüssel** (`MCP_API_KEY` der Web-App = `LEARNCARDS_MCP_API_KEY` des MCP-Servers).
3. Für den Zugriff von außen: **HTTPS** über einen Reverse Proxy (der MCP-Server selbst lauscht nur per HTTP und lehnt `https://`-Listener bewusst ab).
4. Öffentliche URLs: Learny (z. B. `https://learny.example.org`) und MCP (z. B. `https://mcp.example.org`).

## Umgebungsvariablen (MCP-Container)

| Variable | Pflicht | Bedeutung |
|---|---|---|
| `LEARNCARDS_API_URL` | **ja** | **Interne** Adresse der Web-API, z. B. `http://web:8080/api` (nicht die öffentliche URL). Standard ohne Angabe: `http://localhost:5000/api`. |
| `LEARNCARDS_MCP_API_KEY` (oder `MCP_API_KEY`) | **ja** | Dienstschlüssel. Gewährt **nie** Datenzugriff, nur Code-Tausch und das öffentliche Schema. |
| `LEARNCARDS_WEB_PUBLIC_URL` | **ja** (OAuth) | Öffentliche URL der Learny-Weboberfläche; dorthin leitet der Browser zur Anmeldung/Zustimmung weiter. Standard: API-URL ohne `/api`. |
| `LEARNCARDS_MCP_HTTP` | nein | `true` aktiviert den HTTP-Transport (Compose setzt `true`). |
| `LEARNCARDS_MCP_STDIO` | nein | `true`/`false` für stdio. Standard ohne weitere Konfiguration: stdio an. Im HTTP-Betrieb `false`. |
| `LEARNCARDS_MCP_HTTP_URLS` | nein | Listener, `;`-getrennt, z. B. `http://0.0.0.0:8787` (aktiviert HTTP). **Nur `http://`.** |
| `LEARNCARDS_MCP_CONFIG` | nein | Pfad zu einer `mcpsettings.json` (Compose: `/config/mcpsettings.json`, schreibgeschützt eingehängt). |
| `LEARNCARDS_MCP_TOKEN` | stdio | Persönliches Learny-Token für den stdio-Betrieb (im Learny-Profil erzeugt). |

Die OAuth-Einstellungen (`oauth.enabled`, `oauth.issuer`, `oauth.clients`) stehen in `mcpsettings.json` (Vorlage `mcpsettings.example.json`):

```json
{
  "serverName": "Learny MCP",
  "serverVersion": "2.1.0",
  "oauth": { "enabled": true, "issuer": "https://mcp.example.org", "audience": "learncards-mcp", "clients": [] }
}
```

`issuer` ist die **öffentliche MCP-URL** (ohne `/` am Ende). Auf der Web-Seite muss dieselbe URL in `MCP_PUBLIC_URL` stehen — sonst lehnt Learny die Rückleitung nach der Anmeldung ab.

## Docker-Compose-Beispiel

Ausschnitt (vollständig in [deploy/docker](https://github.com/JoergBrors/learny/tree/main/deploy/docker)):

```yaml
services:
  mcp:
    image: joergb23/learny-mcp:latest
    restart: unless-stopped
    depends_on:
      web: { condition: service_healthy }
    environment:
      LEARNCARDS_API_URL: http://web:8080/api
      LEARNCARDS_WEB_PUBLIC_URL: https://learny.example.org
      LEARNCARDS_MCP_API_KEY: ${MCP_API_KEY}
      LEARNCARDS_MCP_HTTP: "true"
      LEARNCARDS_MCP_STDIO: "false"
      LEARNCARDS_MCP_HTTP_URLS: http://0.0.0.0:8787
      LEARNCARDS_MCP_CONFIG: /config/mcpsettings.json
    volumes:
      - ./mcpsettings.json:/config/mcpsettings.json:ro
    ports:
      - "127.0.0.1:8787:8787"     # Reverse Proxy leitet https://mcp.example.org hierher
```

Die Web-Seite braucht zusätzlich den Pflicht-Schlüssel `MCP_TOKEN_KEY` (schützt Tokens und gespeicherte OAuth-Clients in der Datenbank, siehe Installationsanleitung), `MCP_API_KEY` (identisch), `MCP_PUBLIC_URL=https://mcp.example.org` und optional `MCP_TOKEN_DAYS` (Standard 30).

stdio ohne Compose (der Client startet den Container):

```bash
docker run -i --rm \
  -e LEARNCARDS_API_URL=https://learny.example.org/api \
  -e LEARNCARDS_MCP_API_KEY=<dienstschluessel> -e LEARNCARDS_MCP_TOKEN=<persoenliches-token> \
  joergb23/learny-mcp:latest
```

## Anmeldung: OAuth und persönliche Tokens

- **Datenzugriff hat immer das Token eines Benutzers** (benutzergebunden, widerrufbar). Der MCP-Server hält keine Benutzerdaten.
- **OAuth (HTTP):** Der Client ruft `/.well-known/oauth-protected-resource` bzw. `/.well-known/oauth-authorization-server` ab und leitet den Benutzer zu `/oauth/authorize`. Dieser meldet sich bei **Learny** an und stimmt zu (`/mcp/connect`); danach tauscht der Client den Code gegen ein Token. Das Token stellt Learny aus (Gültigkeit `MCP_TOKEN_DAYS`).
- **Persönliches Token (stdio oder manuelle Clients):** im Learny-Profil erzeugen und als `LEARNCARDS_MCP_TOKEN` setzen bzw. als `Authorization: Bearer lcm_…` senden. Tokens lassen sich im Profil widerrufen.
- **Dienstschlüssel vs. Benutzertoken:** Der Dienstschlüssel (`MCP_API_KEY`) authentifiziert nur den MCP-Server gegenüber Learny für Code-Tausch und öffentliches Schema — er gewährt nie Zugriff auf Module oder Karten und wird nie an Clients weitergegeben. Ein Benutzertoken berechtigt nur zu den Daten dieses Benutzers und nur zu den für MCP freigegebenen API-Pfaden (keine Verwaltungs-, Freigabe- oder Zertifikatsfunktionen).

## Öffentliche URLs und HTTPS

- Der Container spricht intern HTTP; **TLS terminiert der Reverse Proxy** (nginx, Caddy, Traefik, IIS). Leite `https://mcp.example.org` auf `127.0.0.1:8787` weiter, ohne Pufferung (`proxy_buffering off`).
- `oauth.issuer` (JSON), `MCP_PUBLIC_URL` (Web) und die im Client eingetragene Adresse müssen dieselbe öffentliche HTTPS-URL bezeichnen.
- `LEARNCARDS_WEB_PUBLIC_URL` muss im Browser des Benutzers erreichbar sein (nicht `http://web:8080`).

## Troubleshooting

| Symptom | Ursache / Abhilfe |
|---|---|
| `GET /health` liefert nichts | Container läuft im stdio-Modus. `LEARNCARDS_MCP_HTTP=true` und `LEARNCARDS_MCP_HTTP_URLS=http://0.0.0.0:8787` setzen. |
| Start bricht ab: „HTTPS listener URLs are not supported“ | Nur `http://` als Listener. TLS im Reverse Proxy terminieren. |
| 401 auf `/mcp` | Kein/ungültiges Benutzertoken. OAuth-Anmeldung erneut durchlaufen oder Token im Profil neu erzeugen. |
| Anmeldung kehrt mit Fehler zurück | `MCP_PUBLIC_URL` der Web-App stimmt nicht mit der öffentlichen MCP-URL überein oder ist leer (in Produktion ist die MCP-Anmeldung dann deaktiviert). |
| Claude muss sich nach einem Neustart nicht mehr neu registrieren | Öffentliche OAuth-Clients werden verschlüsselt in Learny gespeichert (`mcp_oauth_clients`, Schlüssel `MCP_TOKEN_KEY`). Verlust des Schlüssels macht gespeicherte Clients und alle MCP-Zugänge ungültig — neu verbinden. |
| Fehler beim Aufruf der Learny-API | `LEARNCARDS_API_URL` falsch (intern `http://web:8080/api`) oder `MCP_API_KEY` stimmt auf beiden Seiten nicht überein. |
| `mcpsettings.json` wird als Verzeichnis angelegt | Die Datei fehlte beim Start des Profils `mcp`. `cp mcpsettings.example.json mcpsettings.json`, Verzeichnis entfernen, neu starten. |
| Browser leitet auf `http://web:8080` weiter | `LEARNCARDS_WEB_PUBLIC_URL` nicht gesetzt. Öffentliche Learny-URL eintragen. |

Logs: `docker compose logs -f mcp`. Der Server protokolliert keine Tokens oder Schlüssel.

Lizenz: MIT · Quelle: <https://github.com/JoergBrors/learny>

## Werkzeuge für Module und Themen (ab Version 0.1.11)

Zusätzlich zu den Karten-Werkzeugen (`get_import_schema`, `import_cards`, `list_modules`, `list_cards` …) bietet der Server — immer nur im Bereich des angemeldeten Benutzers:

- Lesen: `list_topics`, `get_topic`, `get_topic_exam_structure`.
- Themenentwürfe per JSON: `get_topic_import_schema`, `create_topic`, `import_topic`, `update_topic` (ersetzt nur den Entwurf), `export_topic`.
- Module per JSON: `get_module_import_schema`, `export_module`, `import_module`, `clone_global_module` (globales Modul in ein eigenes klonen).

Module werden in Themen über die stabile Referenz (`module_ref` = `reference_id`) und nur aus **eigenen** Modulen zugeordnet. Veröffentlichen, globale Freigaben, Freigaben/Zuweisungen an andere Benutzer und Administration sind über MCP nicht möglich; der Dienstschlüssel `MCP_API_KEY` gewährt keinen Datenzugriff. Dokumentation: [ADR 0028](https://github.com/JoergBrors/learny/blob/main/docs/adr/0028-themenmodule-json-und-mcp-verwaltung.md).
