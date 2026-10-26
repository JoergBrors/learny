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

## Repository-Vorlagen

Das Repository enthält jetzt nur noch Vorlagen und Platzhalter:

- `deploy/iis/deployment.json`
  Beispiel für die nicht-sensitiven Deploy-Werte.
- `deploy/iis/web.env`
  Enthält nur Struktur, Standardwerte und Platzhalter. Wird beim Deploy mit GitHub Secrets gerendert
  und dann als `.env` in die Web-App kopiert.
- `deploy/iis/mcpsettings.json`
  Enthält nur Struktur und Platzhalter. Wird beim Deploy mit GitHub Secrets gerendert und dann als
  `mcpsettings.json` in den MCP-Zielordner kopiert.

Die tatsächlichen Laufzeitwerte kommen aus GitHub `Variables` und `Secrets`.

## Optional: App-Konfiguration auf dem IIS-Server

Die Web-App liest diese Werte künftig aus der deployten `.env` im IIS-Zielordner.
Der MCP-Server liest seine Laufzeitkonfiguration aus der deployten `mcpsettings.json`.

Die sensiblen Werte selbst liegen nicht im Repository, sondern in GitHub `Secrets`.
Die Pipeline ersetzt die Platzhalter beim Deploy.

## GitHub Variables

Neu: `IIS_CONFIG_ROOT` (optional) – Basisverzeichnis für `.env` und Badge-Schlüssel außerhalb des Webroots; Standard `%ProgramData%\LearnCards\<IIS_SITE_NAME>`. Darf nicht im Webroot liegen.

Unter GitHub → `Settings` → `Secrets and variables` → `Actions` → `Variables` anlegen:

- `IIS_SITE_NAME`
- `IIS_APP_POOL`
- `IIS_PHYSICAL_PATH`
- `IIS_BINDING_URL`
- `IIS_PORT`
- `IIS_HOST_HEADER`
- `ASPNETCORE_HOSTING_BUNDLE_URL`
- `MCP_PHYSICAL_PATH`
- `MCP_SERVICE_NAME`
- `MCP_HTTP_HEALTH_URL`
- `APP_DOMAIN`
- `DEBUG`
- `DB_PROVIDER`
- `SQLITE_PATH`
- `POSTGRES_HOST`
- `POSTGRES_PORT`
- `POSTGRES_USER`
- `POSTGRES_DB`
- `POSTGRES_SSLMODE`
- `AUTH_MODE`
- `OIDC_ISSUER`
- `OIDC_CLIENT_ID`
- `OIDC_AUDIENCE`
- `OIDC_WEB_SCOPE`
- `OPENAI_MODEL`
- `OPENAI_BASE_URL`
- `DEV_USER_NAME`
- `DEV_USER_EMAIL`
- `MCP_SERVER_NAME`
- `MCP_SERVER_VERSION`
- `MCP_UPSTREAM_API_BASE_URL`
- `MCP_STDIO_ENABLED`
- `MCP_HTTP_ENABLED`
- `MCP_HTTP_URLS_JSON`
- `MCP_OAUTH_ENABLED`
- `MCP_OAUTH_ISSUER`
- `MCP_OAUTH_AUDIENCE`
- `MCP_OAUTH_TOKEN_LIFETIME_MINUTES`
- `MCP_OAUTH_CLIENT_ID`
- `MCP_OAUTH_CLIENT_SCOPES_JSON`

## Neue Konfigurationswerte seit ADR 0011–0013 (noch nicht vom Workflow gerendert)

Der Workflow ersetzt in `deploy/iis/web.env` nur die vorhandenen Platzhalter; `.env` wird bei jedem Deploy überschrieben. Folgende Werte daher als **IIS-Umgebungsvariablen**
(Anwendungspool bzw. `web.config`, haben Vorrang vor `.env`) setzen — oder den Workflow um Platzhalter erweitern:

- `AUTH_MODE=oidc` (in Produktion explizit; `auto` fällt ohne OIDC-Konfiguration auf den Dev-Login zurück, [Prüfbericht F1](reviews/2026-10-24-mehrbenutzer-pruefbericht.md))
- `ADMIN_SUBJECTS` — Entra-`sub` des ersten Administrators; Rollen alternativ über die App-Rolle `Admin.All` ([ADR 0012](adr/0012-rollen-aus-entra-app-roles.md))
- `MCP_PUBLIC_URL` — öffentliche URL des MCP-Servers (Allowlist für die OAuth-Rückleitung); ohne sie ist die OAuth-Anmeldung am MCP-Server nicht möglich
- optional `OIDC_ROLE_MAP`, `MCP_TOKEN_DAYS`
- am MCP-Server: `LEARNCARDS_WEB_PUBLIC_URL` (öffentliche Web-URL für die Browser-Anmeldung), für stdio-Clients `LEARNCARDS_MCP_TOKEN`
- **Bildspeicher** ([ADR 0017](adr/0017-konfigurierbare-bildspeicher.md)/[0018](adr/0018-zentraler-image-proxy-und-storage-zugriff.md)): Standard `database` (keine Aktion nötig). Für `local`: `IMAGE_STORAGE_PROVIDER=local` und `IMAGE_LOCAL_PATH` auf ein Verzeichnis **außerhalb** des Deploy-Ordners (`IIS_PHYSICAL_PATH` wird bei jedem Deploy ersetzt) mit Schreibrecht nur für die Identität des Anwendungspools (z. B. `IIS AppPool\LearnCards`).
  Für Azure Blob/S3/SharePoint die `IMAGE_*`-Variablen als IIS-Umgebungsvariablen oder über eine Secret-Verwaltung setzen (siehe `.env.example`); `IMAGE_CACHE_PATH` ebenfalls außerhalb des Deploy-Ordners. Ausgehender HTTPS-Zugriff auf den Speicher-Endpunkt muss die Firewall erlauben.

## GitHub Secrets

Unter GitHub → `Settings` → `Secrets and variables` → `Actions` → `Secrets` anlegen:

- `MCP_API_KEY`
  Pflicht. Dienstschlüssel zwischen MCP-Server und Web-App (identisch in beiden). Gewährt keinen Datenzugriff; Benutzer arbeiten mit eigenen Tokens ([ADR 0013](adr/0013-mcp-benutzergebundene-anmeldung.md)).
- `MCP_TOKEN_KEY`
  **Pflicht** (ADR 0025). Hauptschlüssel für MCP-Tokens, Einmal-Codes und OAuth-Clients in der Datenbank: Base64, mindestens 32 Byte — erzeugen mit `openssl rand -base64 32` oder in PowerShell mit `[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))`. Der Workflow prüft Länge und Format vor dem Deployment, rendert ihn nur in die geschützte `.env` und gibt ihn nie aus. Nicht zusammen mit Datenbank-Sicherungen aufbewahren; bei Verlust sind alle MCP-Zugänge ungültig (Neuanmeldung). Rotation: neuen Schlüssel als `MCP_TOKEN_KEY` setzen, den alten in `MCP_TOKEN_KEY_PREVIOUS` (IIS-Umgebungsvariable) belassen.
- `MCP_OAUTH_SIGNING_KEY`
  Wird vom Workflow weiterhin verlangt, solange `oauth.enabled=true` ist, vom MCP-Server aber **nicht mehr verwendet** (Tokens stellt die Web-App aus). Platzhalterwert setzen; Bereinigung des Workflows empfohlen.
- `MCP_OAUTH_CLIENT_SECRET`
  Nur nötig, wenn im MCP-Config ein vorregistrierter OAuth-Client definiert ist (Clients registrieren sich üblicherweise dynamisch; `client_credentials` wird nicht mehr unterstützt).
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
6. Das Web-Paket wird nach `IIS_PHYSICAL_PATH` gespiegelt (`robocopy /MIR /XD data logs`: `data` und `logs` werden weder überschrieben noch gelöscht).
7. Das MCP-Paket wird, falls `MCP_PHYSICAL_PATH` gesetzt ist, in dieses Verzeichnis gespiegelt.
8. Im MCP-Verzeichnis werden zusätzlich erzeugt:
   - `learncards-mcp.cmd` als Startskript
   - `claude-desktop.learncards.json` als Beispiel für einen MCP-Client
   - `mcpsettings.example.json` als Vorlage für einen eigenständigen HTTP-/OAuth-Betrieb
9. Die Pipeline rendert zuerst die Repo-Dateien mit GitHub Variables und Secrets:
   - `deploy/iis/web.env` → gerenderte `.env`
   - `deploy/iis/mcpsettings.json` → gerenderte `mcpsettings.json`
10. Die gerenderten Dateien werden auf den Zielserver gesetzt:
   - gerenderte `.env` → **außerhalb des Webroots** `<IIS_CONFIG_ROOT>\config\.env` (Standard `%ProgramData%\LearnCards\<Site>`; siehe „Geheimnisse und Badge-Schlüssel“)
   - gerenderte `mcpsettings.json` → `<MCP_PHYSICAL_PATH>\mcpsettings.json`
   Falls die MCP-Datei im Repository fehlt, greift die Pipeline auf die bestehende Datei oder
   notfalls auf `mcpsettings.example.json` zurück.
11. Falls `mcpsettings.json` vorhanden ist und `transports.http.enabled=true` setzt, richtet die Action den MCP-HTTP-Server
   als **geplante Aufgabe** (Start beim Systemstart, SYSTEM) ein bzw. aktualisiert sie; ist unter `MCP_SERVICE_NAME` bereits ein
   **Windows-Dienst** vorhanden, wird stattdessen dieser Dienst neu gestartet (kein Dienst wird angelegt). Details: Abschnitt „MCP-Neustart beim Deployment“.
12. App Pool und Website werden wieder gestartet.
13. Optional wird `/health` geprüft, wenn `IIS_BINDING_URL` gesetzt ist.
14. Optional wird der MCP-HTTP-Endpunkt geprüft, wenn `MCP_HTTP_HEALTH_URL` gesetzt ist.

## MCP-Neustart beim Deployment (ADR 0029)

Der MCP-HTTP-Server ist ein **eigener Prozess** (nicht der IIS-App-Pool). Bei jedem Deployment mit gesetztem `MCP_PHYSICAL_PATH`:

1. **Betriebsart ermitteln:** vorhandener Windows-Dienst `MCP_SERVICE_NAME` (Standard `LearnCardsMcpServer`) oder geplante Aufgabe gleichen Namens; der Zustand wird protokolliert.
2. **Kontrolliert stoppen:** Dienst stoppen (bis 60 s warten), geplante Aufgabe beenden, übrige `LearnCards.McpServer.exe` aus dem Zielverzeichnis beenden; **warten (bis 45 s), bis kein MCP-Prozess mehr läuft** — sonst bricht der Job ab, **ohne** Dateien zu ersetzen.
3. **Dateien aktualisieren** (`robocopy /MIR`); `mcpsettings.json` wird gesichert und danach aus dem sicheren Rendering (oder dem Bestand) wiederhergestellt; Geheimnisse werden nie protokolliert.
4. **Starten:** Windows-Dienst vorhanden → `Start-Service`, auf `Running` und einen MCP-Prozess warten. Sonst geplante Aufgabe neu anlegen und starten, bis 45 s auf den Prozess warten. HTTP-Transport deaktiviert (reiner stdio-Modus) → es wird nichts gestartet, die Aufgabe entfernt. Ein nicht vorhandener Dienst wird nie gestartet oder angelegt; ein vorhandener Windows-Dienst gleichen Namens, dessen Programm **nicht** im `MCP_PHYSICAL_PATH` liegt (veralteter Altbestand), wird mit einer Warnung ignoriert und nicht gestartet.
5. **Betriebsbereitschaft:** bei gesetzter `MCP_HTTP_HEALTH_URL` Healthcheck mit bis zu 12 Versuchen (je 5 s) plus Prüfung der OAuth-Metadaten. **Fehlstart oder fehlende Antwort lassen das Deployment fehlschlagen** (kein stiller Erfolg).
6. **Wiederholbar:** Der Ablauf ist idempotent; ein erneuter Lauf (Tag/`workflow_dispatch`) stoppt, aktualisiert und startet wieder.

**Wiederanlauf/Rollback:** Das vorherige MCP-Verzeichnis liegt im Runner-Temp (`backup-mcp\<Zeitstempel>`); bei Fehlstart den Dienst/die Aufgabe stoppen, Inhalt zurückkopieren (`robocopy <Backup> <MCP_PHYSICAL_PATH> /MIR`), `mcpsettings.json` prüfen und Dienst bzw. Aufgabe (`schtasks /Run /TN <Name>`) starten. Externe stdio-Clients werden nicht neu gestartet.

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
- Sobald `mcpsettings.json` den HTTP-Transport aktiviert, richtet die Pipeline den MCP-HTTP-Server als geplante Aufgabe
  (bzw. vorhandenen Windows-Dienst gleichen Namens) ein und startet ihn nach jedem Deployment neu.
- Unabhängig davon erzeugt die Pipeline ein fertiges Launcher-Skript. Ein Client wie Claude Desktop kann dieses
  Skript automatisch starten, sodass auch im `stdio`-Modus kein manueller Konsolenstart nötig ist.

## MCP ohne manuellen Start

Nach dem Deploy liegen im `MCP_PHYSICAL_PATH` zwei wichtige Dateien:

- `learncards-mcp.cmd`
- `claude-desktop.learncards.json`
- `mcpsettings.example.json`

Das Skript `learncards-mcp.cmd`:

- setzt `LEARNCARDS_API_URL` automatisch auf `IIS_BINDING_URL/api`, falls die Variable noch nicht gesetzt ist
- übernimmt `MCP_API_KEY` nach `LEARNCARDS_MCP_API_KEY`, falls nötig (Dienstschlüssel); der Benutzer trägt zusätzlich sein persönliches Token als `LEARNCARDS_MCP_TOKEN` in die Client-Konfiguration ein
- startet anschließend `LearnCards.McpServer.exe`

Damit kann z. B. Claude Desktop den MCP-Server selbst starten, sobald der Benutzer ihn im Client konfiguriert.

Beispiel: den Inhalt aus `claude-desktop.learncards.json` in die lokale Claude-Desktop-Konfiguration übernehmen.
Danach startet Claude den Server automatisch bei Bedarf, statt dass er manuell per Konsole gestartet werden muss.

## Optional: HTTP-MCP mit OAuth-Anmeldung pro Benutzer

Wenn der MCP-Server als eigenständiges Tool laufen soll:

1. `mcpsettings.example.json` nach `mcpsettings.json` kopieren.
2. `transports.http.enabled` aktivieren.
3. Für einen reinen Serverbetrieb `transports.stdio.enabled` deaktivieren.
4. `oauth.enabled` aktivieren und `oauth.issuer` auf die **öffentliche** MCP-URL setzen (`signingKey`/`clients` sind nicht mehr nötig; die Anmeldung erfolgt bei Learny, [ADR 0013](adr/0013-mcp-benutzergebundene-anmeldung.md)).
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

- `upstreamApi.apiKey` (Dienstschlüssel)
- `oauth.issuer` (öffentliche MCP-URL; muss in der Web-App als `MCP_PUBLIC_URL` stehen)
- optional `upstreamApi.webPublicUrl` bzw. Umgebungsvariable `LEARNCARDS_WEB_PUBLIC_URL`

Der Server stellt dann bereit:

- `POST /mcp`
- `GET /metadata`
- `GET /schema/import/cards`
- `GET /.well-known/oauth-authorization-server`
- `GET /oauth/authorize`, `POST /oauth/register`, `GET /oauth/callback`, `POST /oauth/token`

Damit ist der MCP-Server nicht mehr nur ein per Client gestarteter `stdio`-Prozess, sondern kann
auch als eigenständiger HTTP-Endpoint für MCP-fähige LLM-Clients betrieben werden.

Wenn `transports.http.enabled=false` ist, entfernt die Pipeline einen eventuell vorhandenen
Windows-Dienst wieder und belässt den MCP-Teil im reinen `stdio`-/Client-Start-Modus.

## Learny-PKI (ADR 0035)

Die Anwendung richtet ihre PKI beim ersten Start automatisch ein, sobald `PUBLIC_BASE_URL` (oder `PKI_PUBLIC_URL`) gesetzt ist; die Schlüssel liegen verschlüsselt in `<IIS_PHYSICAL_PATH>\data\pki` (außerhalb des Webroots, ACL nur Anwendungspool-Identität/SYSTEM/Administratoren). `PKI_KEY_PASSWORD` und `PKI_ROOT_PASSPHRASE` (je ≥ 16 Zeichen) gehören in die `.env` bzw. Secret-Verwaltung — fehlt das Passwort, wird eines erzeugt und als `data\pki\pki-key.pass` abgelegt (nach Übernahme in die Secret-Verwaltung entfernen). Das Deployment löscht `data\` nicht; sichern Sie `data\pki` zusammen mit der Datenbank. Danach den Root-Schlüssel offline sichern und in Administration → PKI vom Server entfernen. Details: [docs/pki.md](pki.md).

## Geheimnisse und Badge-Schlüssel (ADR 0023)

**Speicherorte** (außerhalb des Webroots, vom Spiegeln des Webroots unberührt; Basis `IIS_CONFIG_ROOT`, Standard `%ProgramData%\LearnCards\<IIS_SITE_NAME>`):

| Was | Pfad | Rechte |
|---|---|---|
| Produktive `.env` | `<root>\config\.env` (die Anwendung findet sie über die App-Pool-Umgebungsvariable `ENV_FILE`) | SYSTEM, Administratoren, Deploy-Konto: Vollzugriff; App-Pool: **nur Lesen** |
| Privater Badge-Schlüssel | `<root>\keys\badge-signing-key.pem` (`BADGE_SIGNING_KEY_FILE`, vom Deployment in die `.env` geschrieben) | wie oben; App-Pool nur Lesen, sobald Schlüssel **und** Marker existieren |
| Marker „initialisiert“ | `<root>\keys\badge-signing-key.pem.initialized` (nur Schlüssel-ID und Zeitpunkt, kein Geheimnis) | wie der Schlüssel |

- Vererbung ist entfernt; es gibt keinen Zugriff für `Users`/`Everyone`/`Authenticated Users` (das Deployment prüft das und bricht sonst ab).
- Im Webroot: App-Pool nur Lesen/Ausführen; Schreiben nur in `data` und `logs`. Der Pool hat keine Administratorrechte.
- Beim Upgrade werden vorhandene `.env`-Werte nie durch leere ersetzt (nur wenn der neu gerenderte Wert leer und der alte gefüllt ist); eine bisherige `.env` im Webroot wird als Quelle übernommen und danach beim Spiegeln entfernt.
- Der Workflow gibt keine Werte aus; Prüfungen nennen nur Pfade und Status.

**App-Pool:** `ApplicationPoolIdentity`, **Benutzerprofil laden = True** (setzt das Deployment), Umgebungsvariable `ENV_FILE`. Request Filtering (`web.config`) sperrt zusätzlich `.env`, `.pem`, `.key`, `.initialized`, `.db` (`.config` sperrt IIS bereits standardmäßig) sowie die Segmente `data`, `logs`, `keys`.

**Erstinitialisierung:** Ist weder Schlüssel noch Marker vorhanden, erzeugt die Anwendung beim ersten Start einen ECDSA-P-256-Schlüssel — in einer temporären Datei im Schlüsselverzeichnis, dann ohne Überschreiben an den Zielort verschoben (atomar; bei parallelen Starts gewinnt genau einer). Danach wird der Schlüssel neu geladen, eine Signatur-Selbstprüfung ausgeführt und der Marker geschrieben. Das Deployment gibt dem Pool Schreibrecht auf das Schlüsselverzeichnis nur bis Schlüssel und Marker existieren; ab dem nächsten Deployment gilt nur Lesen.

**Bestehende Installationen:** Liegt ein Schlüssel im bisherigen Webroot (`data\badge-signing-key.pem`), wird er **kopiert** (nie gelöscht); der Marker entsteht beim ersten Start. Ein Schlüssel, der zur Initialisierung nicht passt (andere Schlüssel-ID), wird abgelehnt.

**Schlüsselverlust:** Fehlt der Schlüssel bei vorhandenem Marker — oder entstünde ein neuer Schlüssel, obwohl bereits Zertifikate in der Datenbank liegen — bricht der Start mit einer klaren Meldung ab; es wird **kein** neuer Schlüssel erzeugt. Das Deployment bricht schon vorher ab. Wiederherstellung: Schlüsseldatei aus der Sicherung nach `<root>\keys\` kopieren. Nur bei bewusster Rotation (nicht automatisiert, macht ausgestellte Badges unprüfbar) Schlüssel und Marker gemeinsam entfernen.

**Sicherung:** `<root>\keys\badge-signing-key.pem` und `<root>\config\.env` verschlüsselt sichern (Passwortmanager/Tresor/Key-Vault-Backup); nie ins Repository. Der Marker ist ersetzbar, der Schlüssel nicht.

**Fehlerhafte PEM-Datei:** Der Start bricht mit „Der Badge-Signaturschlüssel konnte nicht geladen werden (…)“ ab (nur Fehlertyp, nie Inhalt). Erwartet: ECDSA P-256, PKCS#8 (`BEGIN PRIVATE KEY`) oder SEC1 (`BEGIN EC PRIVATE KEY`).

**Prüfung nach einem Deployment** (Schritt „Verify Secure Configuration“, schlägt bei Befund fehl): `/.env`, `/data/…pem`, `/keys/…pem`, `/appsettings.json`, `/web.config`, `/LearnCards.Web.dll`, `/data/learncards.db` u. a. dürfen keinen Inhalt liefern (403/404 zulässig, 2xx mit sensiblem Inhalt = Fehler); Schlüssel und Marker müssen existieren; die veröffentlichte Schlüssel-ID (`/.well-known/jwks.json`) muss der des Markers entsprechen — also derselbe Schlüssel wie bei der Initialisierung. Signieren und Prüfen testet die Anwendung bei jedem Start selbst (Start bricht sonst ab, `/health` bleibt rot).

### Fehlerbild HTTP 500.30: `The system cannot find the file specified` in `ECDsaCng.ImportPkcs8PrivateKey`

- **Vermutete Ursache (nicht unter IIS nachgestellt):** `ImportFromPem`/`ImportPkcs8PrivateKey` laufen unter Windows über den CNG-Schlüsseldienst (NCrypt); ohne geladenes Benutzerprofil des App-Pool-Kontos kann dieser Weg so fehlschlagen. Weiterer Auslöser in diesem Repository: `robocopy /MIR` löschte frühere `data\`-Inhalte im Webroot.
- **Behebung:** Der Schlüssel wird zuerst selbst in `ECParameters` zerlegt und flüchtig im Prozess importiert (ohne NCrypt-Schlüsselspeicher); „Benutzerprofil laden“ wird vom Deployment gesetzt; `data`/`logs` werden beim Spiegeln ausgenommen.

## Claude.ai mit dem MCP-Server verbinden (OAuth, Dynamic Client Registration)

Claude.ai liest die OAuth-Metadaten des MCP-Servers, registriert sich dort selbst (DCR) und schickt den Benutzer zur Anmeldung bei Learny.

**Benötigte GitHub-Variablen** (Environment, alle öffentlich erreichbare HTTPS-URLs ohne `/` am Ende):

| Variable | Beispiel | Bedeutung |
|---|---|---|
| `MCP_OAUTH_ISSUER` | `https://learny-mcp.brors.date` | **Öffentliche URL des MCP-Servers**; Issuer und Basis aller veröffentlichten OAuth-Endpunkte |
| `MCP_PUBLIC_URL` | `https://learny-mcp.brors.date` | muss **gleich** `MCP_OAUTH_ISSUER` sein; erlaubte Rückleitung nach der Anmeldung (Learny-Web) |
| `MCP_WEB_PUBLIC_URL` | `https://learny.brors.date` | öffentliche URL der Learny-Weboberfläche; dorthin leitet der MCP-Server den Browser zur Anmeldung |
| `MCP_HTTP_HEALTH_URL` | `https://learny-mcp.brors.date/health` | Prüf-URL nach dem Deployment (gleicher Host wie der Issuer) |

Der Deploy-Workflow bricht ab, wenn der Issuer kein `https://` ist, `MCP_PUBLIC_URL` vom Issuer abweicht oder `MCP_WEB_PUBLIC_URL` fehlt. Nach dem Start prüft der Schritt „Verify MCP HTTP Service“, dass `issuer`, `authorization_endpoint`, `token_endpoint` und `registration_endpoint` auf den öffentlichen MCP-Host zeigen und dass der Registrierungsendpunkt eine ungültige Anfrage mit HTTP 400 beantwortet.

**Prüfung von außen:**

```
GET  https://<mcp-host>/.well-known/oauth-authorization-server     → 200 JSON, alle URLs auf <mcp-host>
GET  https://<mcp-host>/.well-known/oauth-protected-resource/mcp   → 200 JSON, resource = https://<mcp-host>/mcp
POST https://<mcp-host>/oauth/register  (public client)            → 201 JSON mit client_id
POST https://<mcp-host>/mcp  ohne Token                            → 401 mit WWW-Authenticate (resource_metadata)
```

`GET /mcp` liefert bewusst 405 (nur POST, Streamable HTTP). Erforderlich für Claude sind die Authorization-Server-Metadaten und die Protected-Resource-Metadaten; `/.well-known/openid-configuration` ist optional. Unterstützt werden Authorization Code mit PKCE **S256**, öffentliche Clients (`token_endpoint_auth_method=none`), Redirect-URIs mit `https` (oder Loopback-`http`); `javascript:`/fremde `http`-Adressen werden mit `invalid_redirect_uri` abgewiesen. Feste Claude-Redirect-URIs werden nicht vorausgesetzt.

**In Claude.ai:** Einstellungen → Konnektoren → Benutzerdefinierten Konnektor hinzufügen → URL `https://<mcp-host>/mcp`. Claude registriert sich, leitet zu Learny (`/mcp/connect`), der Benutzer meldet sich an und stimmt zu; die Verknüpfung erscheint im Learny-Profil unter den MCP-Zugängen und lässt sich dort widerrufen.

**Fehlerbild „Registrierung beim Anmeldedienst von Learny fehlgeschlagen“:** Die Metadaten nannten einen Host, der nicht erreichbar ist (z. B. `MCP_OAUTH_ISSUER=https://mcplearny.brors.date`, obwohl der Server unter `https://learny-mcp.brors.date` läuft). Claude liest die Metadaten vom richtigen Host, ruft dann aber die dort genannte, nicht auflösbare `registration_endpoint`-Adresse auf. Abhilfe: Variablen oben auf den tatsächlichen öffentlichen Host setzen und neu deployen.

**Neustart:** Registrierte OAuth-Clients und offene Anmeldungen liegen nur im Arbeitsspeicher des MCP-Servers (ADR 0013). Nach einem Neustart (z. B. Deployment) sind sie weg; ausgestellte Tokens bleiben gültig (Learny speichert sie). Eine laufende Claude-Sitzung funktioniert weiter. Ein neues Verbinden registriert Claude erneut; ein Client, der mit einer alten `client_id` einen Anmeldevorgang startet, erhält `invalid_client` und muss sich neu registrieren. Eine dauerhafte Speicherung der Clients wäre eine Architekturänderung und erfordert eine eigene ADR.

## Fehlerbild: Anwendung zeigt „Lokal anmelden (Dev-Modus)“ / liest keine Variablen (ADR 0026)

- **Ursache** (Vorfall 2026-10-26): Nach dem Verschieben der `.env` aus dem Webroot (ADR 0023) wurde der Pfad (`ENV_FILE`) nicht wirksam; ältere Versionen starteten dann ohne Konfiguration und fielen bei `AUTH_MODE=auto` still auf den Dev-Login zurück.
- **Jetzt:** Die Anwendung bricht bei fehlender/unlesbarer `ENV_FILE` ab (HTTP 500.30, Grund im Log `<Webroot>\logs\stdout_*.log`) und aktiviert außerhalb von `Development` keinen Dev-Login ohne ausdrückliches `AUTH_MODE=dev`. `ENV_FILE` steht als Umgebungsvariable am App-Pool **und** in der `web.config` (`aspNetCore/environmentVariables`). Nach dem Start prüft der Workflow (`Verify Secure Configuration`), dass die Anmeldeseite keinen Dev-Login zeigt und die Aussteller-URL zu `PUBLIC_BASE_URL` passt.
- **Prüfen auf dem Server:** `type C:\inetpub\wwwroot\LearnCards\logs\stdout_*.log` zeigt „.env: <Pfad>“ in der Startzeile; der App-Pool-Konto `IIS AppPool\<Pool>` braucht Leserecht auf `<IIS_CONFIG_ROOT>\config\.env` (Standard `C:\ProgramData\LearnCards\<Site>`).
