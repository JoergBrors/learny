# Learny mit Docker installieren

Dieses Paket installiert **Learny** (LearnCards) aus fertigen Images von Docker Hub — ohne Quellcode, ohne Git, ohne .NET SDK.

| Image | Zweck |
|---|---|
| [`joergb23/learny-web`](https://hub.docker.com/r/joergb23/learny-web) | Weboberfläche (Blazor) und REST-API in einem Dienst |
| [`joergb23/learny-mcp`](https://hub.docker.com/r/joergb23/learny-mcp) | optional: MCP-Server für KI-Clients |

Plattformen: **linux/amd64** und **linux/arm64**. Versionen: `1.2.3` (feste Version) und `latest` (letzte stabile Version; Vorabversionen wie `1.2.3-rc.1` überschreiben `latest` nie).

Inhalt dieses Pakets: `docker-compose.yml`, `.env.example`, `mcpsettings.example.json` (nur MCP), `nginx/learny.conf.template` (nur Reverse-Proxy-Profil), diese Anleitung sowie `README-web.md` und `README-mcp.md` (Docker-Hub-Beschreibungen).

## Schritt für Schritt

1. **Docker Engine und Compose installieren** (Docker Engine ab 24 mit Compose-Plugin ≥ 2.20): <https://docs.docker.com/engine/install/>. Prüfen: `docker compose version`.
2. **Verzeichnis anlegen** und die Dateien hineinlegen — entweder das ZIP `learny-docker-<version>.zip` aus dem GitHub Release entpacken oder `docker-compose.yml` und `.env.example` einzeln herunterladen:
   ```bash
   mkdir learny && cd learny
   unzip ../learny-docker-<version>.zip
   ```
3. **`.env` anlegen**: `cp .env.example .env` und `chmod 600 .env` (die Datei enthält Geheimnisse und gehört nie in ein Repository).
4. **Variablen konfigurieren** — mindestens `POSTGRES_PASSWORD` und die Anmeldung (`OIDC_*`, siehe [Microsoft Entra ID einrichten](#microsoft-entra-id-einrichten)). Alle Variablen stehen in der [Tabelle unten](#variablen).
5. **Sichere Passwörter und Schlüssel erzeugen**:
   ```bash
   openssl rand -base64 32      # z. B. für POSTGRES_PASSWORD und MCP_API_KEY
   ```
6. **Images laden**: `docker compose pull`
7. **Starten**: `docker compose up -d`
8. **Status prüfen**: `docker compose ps` — alle Dienste sollen `healthy` sein. Logs: `docker compose logs -f web`.
9. **Im Browser öffnen**: `http://localhost:8080` (nur lokal gebunden). Für den Zugriff von außen einen Reverse Proxy mit HTTPS davorschalten ([HTTPS](#https-und-reverse-proxy)).
10. **Optional MCP aktivieren**: siehe [MCP](#mcp-optional).
11. **Updates und Backups**: siehe [Updates](#updates) und [Backup und Wiederherstellung](#backup-und-wiederherstellung).

> **Betriebsmodus (ADR 0026):** Im Container (`ASPNETCORE_ENVIRONMENT=Production`) startet Learny ohne vollständige OIDC-Angaben **nicht**, statt still in den Dev-Login zu fallen. Der Dev-Login braucht ausdrücklich `AUTH_MODE=dev` (Warnung im Log). Tippfehler im Wert von `AUTH_MODE` führen ebenfalls zum Startabbruch.
>
> **Nur zum Ausprobieren auf dem eigenen Rechner** (ohne Entra ID): `AUTH_MODE=dev` in `.env`. Das ist ein Login ohne Passwort mit Administratorrechten — **niemals** auf einem Server oder hinter einer öffentlichen Adresse verwenden. Beispiel für `.env`:
> `POSTGRES_PASSWORD=...` + `AUTH_MODE=dev`, Zugriff nur über `http://localhost:8080`.

## Aufbau

```
Internet ──► Reverse Proxy (HTTPS, 443) ──► web:8080  ──► db:5432 (PostgreSQL, nur intern)
                                       └─► mcp:8787 (optional) ──► web:8080/api (intern)
```

- **Intern** (Docker-Netzwerk `internal`): `db`, `web`, `mcp` erreichen sich über ihre Service-Namen. PostgreSQL hat **keinen** veröffentlichten Port.
- **Öffentlich**: nur der Reverse Proxy. Die Ports von `web` und `mcp` sind standardmäßig an `127.0.0.1` gebunden (`LEARNY_BIND_ADDRESS`).
- Die Web-App vertraut `X-Forwarded-*`-Headern (nötig für korrekte OIDC-Weiterleitungen hinter HTTPS). Deshalb darf ihr Port nicht direkt aus dem Internet erreichbar sein.
- **Interne vs. öffentliche URL**: Der MCP-Container spricht die Web-API intern an (`http://web:8080/api`, fest in der Compose-Datei). Browser und KI-Clients benötigen die **öffentlichen** HTTPS-URLs (`PUBLIC_BASE_URL`, `LEARNCARDS_WEB_PUBLIC_URL`, `MCP_PUBLIC_URL`, Issuer in `mcpsettings.json`).

### Persistente Daten

| Volume | Inhalt |
|---|---|
| `postgres_data` | PostgreSQL-Datenbank (Module, Karten, Benutzer, Ergebnisse, Zertifikate) |
| `web_data` (`/app/data`) | Data-Protection-Schlüssel (`keys/`), Badge-Signaturschlüssel (`badge-signing-key.pem` + Marker), lokaler Bildspeicher (`images/`) und -Cache, ggf. SQLite-Datei |

Container-Neustarts und Updates verändern beide Volumes nicht. **`docker compose down -v` löscht sie** — ohne `-v` bleiben die Daten erhalten.

## Microsoft Entra ID einrichten

1. Azure-Portal → **Microsoft Entra ID → App-Registrierungen → Neue Registrierung**. Name z. B. „Learny“, Kontotyp nach Bedarf (meist „Nur dieses Verzeichnis“).
2. **Umleitungs-URI** (Plattform *Web*): `https://<deine öffentliche Adresse>/auth/callback`. (Lokal zum Test zusätzlich `http://localhost:8080/auth/callback`.)
3. **Zertifikate & Geheimnisse → Neuer geheimer Clientschlüssel**: den **Wert** (nicht die ID) sofort kopieren → `OIDC_CLIENT_SECRET`. Ablaufdatum notieren und rechtzeitig erneuern.
4. Aus der Übersicht übernehmen: **Anwendungs-(Client-)ID** → `OIDC_CLIENT_ID`; **Verzeichnis-(Mandanten-)ID** → `OIDC_ISSUER=https://login.microsoftonline.com/<Mandanten-ID>/v2.0`.
5. **Optional: Administratoren/Auditoren über App-Rollen**: App-Rolle mit dem Wert `Admin.All` (und/oder `Auditor`) anlegen und Benutzern zuweisen; Zuordnung über `OIDC_ROLE_MAP` (Standard: `Admin.All=admin`). Für die Ersteinrichtung kann alternativ die Objekt-/Subject-ID des ersten Administrators in `ADMIN_SUBJECTS` stehen.
6. `AUTH_MODE=oidc` (Standard dieses Pakets) und `PUBLIC_BASE_URL` auf die öffentliche Adresse setzen.

## HTTPS und Reverse Proxy

Mitgeliefert ist ein nginx-Beispiel (Profil `proxy`). Es erwartet `./certs/cert.pem` (Zertifikat samt Kette) und `./certs/key.pem` (privater Schlüssel, `chmod 600`):

```bash
# .env
COMPOSE_PROFILES=db,proxy        # (bei MCP: db,proxy,mcp)
LEARNY_HOST=learny.example.org
MCP_HOST=mcp.example.org
LEARNY_BIND_ADDRESS=127.0.0.1    # Web/MCP bleiben lokal; nur nginx ist öffentlich
```

nginx leitet HTTP auf HTTPS um, reicht WebSockets (Blazor/SignalR) durch und setzt `X-Forwarded-Proto/Host`. Wer bereits einen Reverse Proxy betreibt (Traefik, Caddy, nginx, IIS), lässt das Profil `proxy` weg und leitet auf `127.0.0.1:8080` (Web) bzw. `127.0.0.1:8787` (MCP) weiter — wichtig sind WebSocket-Unterstützung und die Header `X-Forwarded-Proto`, `X-Forwarded-Host`, `X-Forwarded-For`.

## MCP (optional)

Der MCP-Server lässt KI-Clients (z. B. Claude) auf Learny zugreifen. Er speichert nichts selbst; Datenzugriff geschieht immer mit dem **persönlichen Token des Benutzers** (Dienstschlüssel `MCP_API_KEY` gewährt nie Datenzugriff).

1. In `.env`: `MCP_API_KEY` (zufällig, lang), `MCP_PUBLIC_URL=https://mcp.example.org`, `LEARNCARDS_WEB_PUBLIC_URL=https://learny.example.org`, Profil `mcp` ergänzen (`COMPOSE_PROFILES=db,mcp`).
2. `cp mcpsettings.example.json mcpsettings.json` und `issuer` auf die **öffentliche MCP-URL** setzen (`https://mcp.example.org`).
3. `docker compose up -d` — MCP lauscht intern auf `http://0.0.0.0:8787` (TLS terminiert der Reverse Proxy).
4. MCP-Client verbinden mit `https://mcp.example.org/mcp`. Die Anmeldung erfolgt im Browser über Learny (OAuth); alternativ erzeugt jeder Benutzer im Profil ein persönliches Token.

Details und Fehlersuche: [README-mcp.md](README-mcp.md).

## Updates

```bash
# feste Version eintragen, z. B. LEARNY_VERSION=1.2.0 in .env — oder bei latest:
docker compose pull
docker compose up -d
```

Daten bleiben in den Volumes erhalten; die Datenbank-Tabellen werden beim Start bei Bedarf automatisch ergänzt. Vor größeren Versionssprüngen ein [Backup](#backup-und-wiederherstellung) anlegen. Zurück zur alten Version: `LEARNY_VERSION` zurücksetzen und `docker compose up -d` (nach einem Datenbank-Upgrade ggf. das Backup einspielen).

## Backup und Wiederherstellung

```bash
# Datenbank
docker compose exec -T db sh -c 'pg_dump -U "$POSTGRES_USER" "$POSTGRES_DB"' > learny-db-$(date +%F).sql
# Web-Daten: Data-Protection-Schlüssel, Badge-Schlüssel, Bilder
docker run --rm -v learny_web_data:/data -v "$PWD":/backup alpine tar czf /backup/learny-web-data-$(date +%F).tgz -C /data .
# zusätzlich sichern: .env und mcpsettings.json (enthalten Geheimnisse — verschlüsselt ablegen)
```

Wiederherstellung (Dienste gestoppt: `docker compose down`, **ohne** `-v`):
```bash
docker compose up -d db
docker compose exec -T db sh -c 'psql -U "$POSTGRES_USER" "$POSTGRES_DB"' < learny-db-<datum>.sql
docker run --rm -v learny_web_data:/data -v "$PWD":/backup alpine sh -c 'cd /data && tar xzf /backup/learny-web-data-<datum>.tgz'
docker compose up -d
```

Der **Badge-Signaturschlüssel** (`web_data/badge-signing-key.pem`) ist unersetzlich: Ohne ihn lassen sich bereits ausgestellte Zertifikate nicht mehr prüfen. Fehlt er bei einer bereits genutzten Installation, bricht Learny den Start ab, statt einen neuen zu erzeugen.

## Sicherheit

- Keine Geheimnisse in Images, YAML oder Repository — nur in `.env` (Rechte 600) bzw. einer Secret-Verwaltung.
- `AUTH_MODE=dev` nie in Produktion. Nicht `AUTH_MODE=auto` setzen (kann bei fehlenden OIDC-Angaben still auf `dev` zurückfallen, ADR 0014). Tippfehler im Wert vermeiden.
- PostgreSQL nicht veröffentlichen; Web/MCP nur über den Reverse Proxy mit HTTPS erreichbar machen.
- Die Container laufen als unprivilegierter Benutzer (UID 1654). Das Volume `web_data` gehört diesem Benutzer.
- Mehrbenutzer-Trennung: Module und Karten sind privat (Eigentümer); Freigaben und globale Module nur explizit verwaltet.
- Zugangsdaten für Bildspeicher (S3, Azure, SharePoint) und OIDC regelmäßig rotieren.

## Externe PostgreSQL-Instanz

```bash
# .env
COMPOSE_PROFILES=            # ohne "db" (ggf. nur mcp/proxy)
POSTGRES_HOST=postgres.example.org
POSTGRES_PORT=5432
POSTGRES_DB=learncards
POSTGRES_USER=learncards
POSTGRES_PASSWORD=...        # Pflicht
POSTGRES_SSLMODE=require     # bei Verbindung über öffentliche Netze
```
Datenbank und Benutzer müssen vorhanden sein; Learny legt die Tabellen beim Start an. Ein leeres `COMPOSE_PROFILES` startet den PostgreSQL-Container nicht.

## Beispielkonfigurationen

**Learny mit PostgreSQL und Entra ID** (Mindestkonfiguration):
```bash
POSTGRES_PASSWORD=<zufällig>
AUTH_MODE=oidc
OIDC_ISSUER=https://login.microsoftonline.com/<Mandanten-ID>/v2.0
OIDC_CLIENT_ID=<Client-ID>
OIDC_CLIENT_SECRET=<Wert>
PUBLIC_BASE_URL=https://learny.example.org
APP_DOMAIN=learny.example.org
```

**Zusätzlich MCP:**
```bash
COMPOSE_PROFILES=db,mcp
MCP_API_KEY=<zufällig>
MCP_PUBLIC_URL=https://mcp.example.org
LEARNCARDS_WEB_PUBLIC_URL=https://learny.example.org
# + mcpsettings.json mit issuer https://mcp.example.org
```

**Ohne OpenAI-Schlüssel:** `OPENAI_API_KEY` leer lassen — Learny läuft im gekennzeichneten Offline-Modus (Quizfragen aus dem Kartenpool, Bewertung per Stichwort-Heuristik; der KI-Chat weist auf den fehlenden Schlüssel hin). Alle übrigen Lernfunktionen bleiben nutzbar.

## Variablen

Alle Variablen aus `.env.example`. „Dienst“ nennt, wohin Compose den Wert weitergibt (`compose` = nur für die Compose-Datei selbst). Container-Umgebungsvariablen werden in `docker-compose.yml` unter `environment:` zugewiesen; `${VAR:-Standard}` wird beim Start aus `.env` ersetzt.

Pflicht: **PostgreSQL-Betrieb** → `POSTGRES_PASSWORD`; **OIDC-Betrieb** → `OIDC_ISSUER`, `OIDC_CLIENT_ID`, `OIDC_CLIENT_SECRET`; **MCP** → `MCP_API_KEY`, `MCP_PUBLIC_URL`, `LEARNCARDS_WEB_PUBLIC_URL`, `mcpsettings.json`; KI und Bildspeicher-Provider sind optional.

| Variable | Dienst | Pflicht | Standard | Beschreibung |
|---|---|---|---|---|
| `LEARNY_VERSION` | Compose | nein | `latest` | Version der Images (z. B. `1.0.0`); `latest` = letzte stabile Version. |
| `COMPOSE_PROFILES` | Compose | nein | `db` | Aktive Profile: `db` (PostgreSQL-Container), `mcp`, `proxy` (nginx/TLS); kommagetrennt. |
| `LEARNY_BIND_ADDRESS` | Compose | nein | `127.0.0.1` | Bindeadresse der veröffentlichten Ports. `0.0.0.0` nur in vertrauenswürdigen Netzen. |
| `LEARNY_WEB_PORT` | Compose | nein | `8080` | Host-Port der Web-App. |
| `LEARNY_MCP_PORT` | Compose | nein | `8787` | Host-Port des MCP-Servers (Profil `mcp`). |
| `DB_PROVIDER` | web | nein | `postgres` | `postgres` oder `sqlite` (SQLite-Datei im Volume, nur für kleine Einzelinstallationen). |
| `POSTGRES_HOST` | web | bei PostgreSQL | `db` | Datenbank-Host; bei externer Datenbank deren Adresse (und Profil `db` weglassen). |
| `POSTGRES_PORT` | web | nein | `5432` | Datenbank-Port. |
| `POSTGRES_DB` | web, db | nein | `learncards` | Datenbankname. |
| `POSTGRES_USER` | web, db | nein | `learncards` | Datenbankbenutzer. |
| `POSTGRES_PASSWORD` | web, db | **ja** | — | Passwort des Datenbankbenutzers. Compose bricht ohne Wert ab. |
| `POSTGRES_SSLMODE` | web | nein | `disable` | SSL-Modus; bei externer Datenbank über öffentliche Netze `require`. |
| `ASPNETCORE_ENVIRONMENT` | web | nein | `Production` | ASP.NET-Umgebung; nicht `Development` verwenden. |
| `DATA_PROTECTION_DIR` | web | nein | `/app/data/keys` | Ablage der Data-Protection-Schlüssel; liegt im persistenten Volume `web_data` — nicht ändern. |
| `APP_DOMAIN` | web | nein | `localhost` | Anzeige-Domain der Anwendung. |
| `PUBLIC_BASE_URL` | web | in Produktion | leer | Öffentliche Basis-URL (Badge-Aussteller, Prüf-Links), z. B. `https://learny.example.org`. |
| `DEBUG` | web | nein | `false` | Ausführliche Fehler (`true`/`false`). |
| `AUTH_MODE` | web | nein | `oidc` | `oidc` (Produktion) oder `dev` (Login ohne Passwort, nur lokal zum Ausprobieren). Nicht `auto` verwenden (ADR 0014). |
| `OIDC_ISSUER` | web | **ja** bei `oidc` | — | Issuer, z. B. `https://login.microsoftonline.com/<Tenant-ID>/v2.0`. |
| `OIDC_CLIENT_ID` | web | **ja** bei `oidc` | — | Anwendungs-(Client-)ID der Entra-App-Registrierung. |
| `OIDC_CLIENT_SECRET` | web | **ja** bei `oidc` | — | Geheimer Clientschlüssel der App-Registrierung (läuft ab). |
| `OIDC_AUDIENCE` | web | nein | leer | Erwartete Audience (üblich: Client-ID). |
| `OIDC_WEB_SCOPE` | web | nein | `openid profile email` | Angeforderte Scopes. |
| `OIDC_ROLE_MAP` | web | nein | `Admin.All=admin` | Entra-App-Rolle → Learny-Rolle (`admin`/`auditor`/`themen_admin`), z. B. `Admin.All=admin,Auditor=auditor`. |
| `ADMIN_SUBJECTS` | web | nein | leer | Subjects (`sub`), die beim Login Administrator werden (Bootstrap). |
| `MCP_API_KEY` | web, mcp | **ja** bei MCP | leer | Dienstschlüssel zwischen Web und MCP; gewährt nie Datenzugriff. |
| `MCP_PUBLIC_URL` | web | **ja** bei MCP (Produktion) | leer | Erlaubte öffentliche MCP-URL(n) für die Rückleitung, kommagetrennt, ohne `/` am Ende. |
| `MCP_TOKEN_KEY` | web | **ja** (immer) | — | Hauptschlüssel (Base64, ≥ 32 Byte) für MCP-Tokens, Einmal-Codes und OAuth-Clients in der Datenbank (ADR 0025). Nur in der `.env`; getrennt von Datenbank-Backups sichern. Ohne Schlüssel startet Learny nicht. |
| `MCP_TOKEN_KEY_PREVIOUS` | web | nein | leer | Bisherige Schlüssel nach einer Rotation (kommagetrennt), nur zum Prüfen/Entschlüsseln. |
| `MCP_LEGACY_TOKEN_UNTIL` | web | nein | `2027-01-31` | Ende der Übergangsfrist für ältere MCP-Tokens (reiner SHA-256). |
| `MCP_TOKEN_DAYS` | web | nein | `30` | Gültigkeit persönlicher MCP-Tokens in Tagen (1–365). |
| `LEARNCARDS_WEB_PUBLIC_URL` | mcp | **ja** bei MCP | leer | Öffentliche URL der Learny-Weboberfläche (Browser-Anmeldung). |
| `OPENAI_API_KEY` | web | nein | leer | OpenAI-Schlüssel für Chat/Quiz-Generierung/KI-Bewertung; ohne Schlüssel gilt der Offline-Fallback. |
| `OPENAI_MODEL` | web | nein | `gpt-4o-mini` | Modell. |
| `OPENAI_BASE_URL` | web | nein | `https://api.openai.com/v1` | Basis-URL einer OpenAI-kompatiblen API. |
| `BADGE_SIGNING_KEY_PEM` | web | nein | leer | Signaturschlüssel der Badges (ECDSA P-256, PKCS#8-PEM). Leer = Schlüssel wird beim ersten Start im Volume erzeugt. |
| `PKI_PUBLIC_URL` | web | nein | leer (→ `PUBLIC_BASE_URL`) | Öffentliche Basis-URL für die Learny-PKI (ADR 0035); steht unveränderlich in den CRL-Verteilungspunkten. Ohne diese und `PUBLIC_BASE_URL` wird die PKI nicht automatisch eingerichtet. |
| `PKI_AUTO_INIT` | web | nein | `true` | Einmalige automatische Einrichtung der PKI beim allerersten Start (nie bei späteren Starts). `false` = nur ausdrücklich unter Administration → PKI. |
| `PKI_ORGANIZATION` | web | nein | `Learny` | Organisationsname (O=) in den PKI-Zertifikaten. |
| `PKI_KEY_PASSWORD` | web | nein | leer | Passwort der verschlüsselten Schlüsseldateien in `/app/data/pki` (mind. 16 Zeichen). Leer = beim Erststart erzeugt und im Volume abgelegt. Streng geheim. |
| `PKI_ROOT_PASSPHRASE` | web | nein | leer | Passphrase der Root-Schlüsseldatei (mind. 16 Zeichen). Leer = zufällig, einmalig neben der Datei notiert. Streng geheim. |
| `PKI_TRUSTED_ROOT_SHA256` | web | nein | leer | Optionaler Vertrauensanker-Pin (SHA-256 der Root CA, kommagetrennt) für die Prüfung. |
| `PDF_SIGNING_CERT_FILE` | web | nein | leer | PKCS#12-Datei (im Container eingebunden) mit dem eigenen PDF-Signaturzertifikat für signierte Zertifikats-PDFs (ADR 0033). Leer (und ohne Base64-Variante) = keine PDFs (503). Siehe `docs/pdf-signature.md`. |
| `PDF_SIGNING_CERT_PASSWORD` | web | nein | leer | Passwort der PKCS#12-Datei bzw. -Base64-Variante. Streng geheim. |
| `PDF_SIGNING_CERT_PFX_BASE64` | web | nein | leer | PKCS#12 als Base64-Text (Secret-Verwaltung); hat Vorrang vor `PDF_SIGNING_CERT_FILE`. |
| `BLOCKCHAIN_ENABLED` | web | nein | `true` | Verankerung ein/aus (aus = nur signierte Badges). |
| `BLOCKCHAIN_NETWORK` | web | nein | `polygon-amoy` | Netzwerk (Testnetz); Mainnet `polygon-mainnet` braucht zusätzlich Admin-Freigabe. |
| `BLOCKCHAIN_AUTO_ANCHOR` | web | nein | `true` | Automatische Bündelung und Verankerung. |
| `BLOCKCHAIN_MAX_COST_EUR` | web | nein | `0.001` | Harte Kostengrenze je Nachweis (Maximum 0.001). |
| `BLOCKCHAIN_MAINNET_ENABLED` | web | nein | `false` | Mainnet technisch erlauben. |
| `BLOCKCHAIN_BATCH_ENABLED` | web | nein | `true` | Batch-Bündelung. |
| `BLOCKCHAIN_BATCH_MAX_SIZE` | web | nein | `100` | Maximale Nachweise je Batch. |
| `BLOCKCHAIN_BATCH_MAX_WAIT_MINUTES` | web | nein | `60` | Maximale Wartezeit bis zum Senden (Minuten). |
| `BLOCKCHAIN_WALLET_ADDRESS` | web | nein | leer | Öffentliche Wallet-Adresse (0x…), kein privater Schlüssel. |
| `LEARNY_HOST` | proxy | bei Profil `proxy` | `localhost` | Öffentlicher Hostname der Web-App (ohne `https://`). |
| `MCP_HOST` | proxy | bei Profil `proxy` + `mcp` | `mcp.localhost` | Öffentlicher Hostname des MCP-Servers (ohne `https://`). |
| `IMAGE_STORAGE_PROVIDER` | web | nein | `database` | `database`, `local`, `azureblob`, `s3`, `sharepoint`. |
| `IMAGE_LOCAL_PATH` | web | bei `local` | `/app/data/images` | Verzeichnis für lokale Bilder; muss unter `/app/data` liegen. |
| `IMAGE_PROXY_ENABLED` | web | nein | `true` | Bild-Proxy `/api/images/{id}` aktiv. |
| `IMAGE_HTTP_TIMEOUT_SECONDS` | web | nein | `30` | Timeout ausgehender Speicheraufrufe. |
| `IMAGE_CACHE_ENABLED` | web | nein | `true` | Bildcache ein/aus. |
| `IMAGE_CACHE_MAX_SIZE_MB` | web | nein | `512` | Maximale Cachegröße (MB). |
| `IMAGE_CACHE_TTL_MINUTES` | web | nein | `1440` | Cache-Gültigkeit (Minuten). |
| `IMAGE_CACHE_MEMORY_MB` | web | nein | `64` | Speichercache (MB). |
| `IMAGE_CACHE_PATH` | web | nein | leer | Cache-Verzeichnis (leer = unter `/app/data`). |
| `IMAGE_AZUREBLOB_ACCOUNT` | web | bei `azureblob` | leer | Speicherkonto (oder `IMAGE_AZUREBLOB_ENDPOINT`). |
| `IMAGE_AZUREBLOB_ENDPOINT` | web | nein | leer | Alternative zur Kontoangabe (eigener Endpunkt). |
| `IMAGE_AZUREBLOB_CONTAINER` | web | nein | `learncards-images` | Blob-Container. |
| `IMAGE_AZUREBLOB_AUTH` | web | nein | `managedidentity` | `managedidentity`, `clientsecret` oder `sharedkey` (nur Entwicklung/Test). |
| `IMAGE_AZUREBLOB_TENANT_ID` | web | bei `clientsecret` | leer | Tenant-ID. |
| `IMAGE_AZUREBLOB_CLIENT_ID` | web | bei `clientsecret` | leer | Client-ID. |
| `IMAGE_AZUREBLOB_CLIENT_SECRET` | web | bei `clientsecret` | leer | Client-Secret. |
| `IMAGE_AZUREBLOB_ACCOUNT_KEY` | web | bei `sharedkey` | leer | Kontoschlüssel. |
| `IMAGE_AZURE_SAS_TTL_SECONDS` | web | nein | `60` | Gültigkeit der Lese-Signaturen (Sekunden). |
| `IMAGE_S3_ENDPOINT` | web | nein | leer | S3-Endpunkt (leer = AWS; bei MinIO o. ä. setzen). |
| `IMAGE_S3_REGION` | web | nein | `us-east-1` | Region. |
| `IMAGE_S3_BUCKET` | web | bei `s3` | leer | Bucket. |
| `IMAGE_S3_ACCESS_KEY_ID` | web | bei `s3` | leer | Zugangsschlüssel-ID. |
| `IMAGE_S3_SECRET_ACCESS_KEY` | web | bei `s3` | leer | Geheimer Zugangsschlüssel. |
| `IMAGE_S3_SESSION_TOKEN` | web | nein | leer | Sitzungstoken (temporäre Zugangsdaten). |
| `IMAGE_S3_PATH_STYLE` | web | nein | `false` | Path-Style-Adressierung (MinIO: `true`). |
| `IMAGE_S3_PRESIGN_TTL_SECONDS` | web | nein | `60` | Gültigkeit der Lese-Signaturen (Sekunden). |
| `IMAGE_SHAREPOINT_TENANT_ID` | web | bei `sharepoint` | leer | Tenant-ID. |
| `IMAGE_SHAREPOINT_CLIENT_ID` | web | bei `sharepoint` | leer | Client-ID. |
| `IMAGE_SHAREPOINT_CLIENT_SECRET` | web | bei `sharepoint` | leer | Client-Secret. |
| `IMAGE_SHAREPOINT_SITE_ID` | web | bei `sharepoint` | leer | Site-ID. |
| `IMAGE_SHAREPOINT_DRIVE_ID` | web | bei `sharepoint` | leer | Laufwerks-ID. |
| `IMAGE_SHAREPOINT_FOLDER` | web | nein | `LearnCards/images` | Zielordner. |

Intern festgelegt (nicht über `.env` änderbar): `ASPNETCORE_URLS=http://+:8080`, `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, MCP: `LEARNCARDS_API_URL=http://web:8080/api`, `LEARNCARDS_MCP_HTTP=true`, `LEARNCARDS_MCP_STDIO=false`, `LEARNCARDS_MCP_HTTP_URLS=http://0.0.0.0:8787`, `LEARNCARDS_MCP_CONFIG=/config/mcpsettings.json`.
Selten benötigte Bildspeicher-Optionen (`…_ALLOW_HTTP`, `…_ALLOW_PRIVATE_NETWORK`, `…_AUTHORITY`) sind im Code vorhanden, in der Compose-Datei aber nicht vorbelegt; Hintergründe in den ADRs 0017/0018.

## Veröffentlichen (für Maintainer)

Ein GitHub Release löst den Workflow [`publish-docker.yml`](../../.github/workflows/publish-docker.yml) aus (Tests → Images bauen → Docker Hub → Beschreibungen → ZIP am Release). Voraussetzungen: Repository-Secrets `DOCKERHUB_USERNAME` (`joergb23`) und `DOCKERHUB_TOKEN` (Docker-Hub-Zugriffstoken mit Lese-, Schreib- und Löschrechten; für das Aktualisieren der Beschreibung nötig). Das Release auf einem **bereits vorhandenen Tag** anlegen — ein Release mit neuem Tag erzeugt den Tag und startet dadurch auch den IIS-Workflow. Vorabversionen (`1.0.0-rc.1` oder als „Pre-release“ markiert) bekommen nur ihren Versionstag; `latest` und die Docker-Hub-Beschreibung bleiben unverändert.

## Laufzeitstand prüfen (CVE-2026-111497)

Die Veröffentlichung prüft automatisch, dass die .NET-Laufzeit der Images mindestens 10.0.14 ist; ein erfolgreicher Build belegt aber nicht den Stand eines bereits laufenden Containers. Auf dem Docker-Host (lesend):

```
docker ps --format '{{.Names}} {{.Image}}'
docker image inspect --format '{{index .RepoDigests 0}} {{.Created}}' joergb23/learny-web:<version>
docker exec <container> dotnet --list-runtimes
```

Liegt `Microsoft.NETCore.App` oder `Microsoft.AspNetCore.App` unter 10.0.14: neue Version ziehen (`docker compose pull && docker compose up -d`). Details: [docs/security-update-cve-2026-111497.md](../../docs/security-update-cve-2026-111497.md).
