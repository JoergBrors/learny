# 0025 — Sichere Speicherung von MCP-Tokens, Codes und OAuth-Clients (Schutz bei Zugriff auf den PostgreSQL-Server)

- **Status:** **Akzeptiert und umgesetzt** (Entscheidungen des Auftraggebers vom 2026-10-26, Abschnitt „Entscheidungen“; Umsetzung siehe „Umsetzung und Abweichungen“). Ersetzt in [0013](0013-mcp-benutzergebundene-anmeldung.md) die Speicherung („nur SHA-256“, Clients nur im Arbeitsspeicher)
- **Datum:** 2026-10-26
- **Baut auf:** [0013](0013-mcp-benutzergebundene-anmeldung.md) (Tokens nur als Hash), [0011](0011-mehrbenutzerarchitektur.md), [0005](0005-authentifizierung.md), [0023](0023-secrets-und-badge-schluessel-unter-iis.md) (Schlüsselablage außerhalb des Webroots)
- **Würde teilweise ersetzen:** 0013, Abschnitt „Persönliches Token“ und Speicherung (`mcp_links`, `mcp_link_codes`), sowie die Einschränkung „registrierte Clients nur im Arbeitsspeicher“
- **Bezug:** [0002](0002-keine-nuget-pakete.md) (nur .NET-Bordmittel: `HMACSHA256`, `HKDF`, `CryptographicOperations.FixedTimeEquals`), [0004](0004-datenbank-abstraktion.md) (SQLite und PostgreSQL)

## Kontext

Ein MCP-Token (`lcm_…`, 256 Bit zufällig) berechtigt zum Lesen und Ändern aller Module und Karten **eines** Benutzers. Heute liegt in `mcp_links` nur `SHA-256(token)`; Einmal-Codes (`mcp_link_codes`) sind ebenso gehasht. Das schützt gegen das **Lesen** der Tabelle: Aus einem Hash lässt sich ein 256-Bit-Zufallstoken nicht zurückrechnen.

Das genügt nicht, wenn jemand **Zugriff auf den PostgreSQL-Server** hat (gestohlenes Backup, SQL-Injection, kompromittierte Datenbank-Rolle, Administrator mit Schreibrecht):

| Angriff mit Datenbankzugriff | Heute |
|---|---|
| Tokens aus der Tabelle auslesen | nicht möglich (nur Hash) ✔ |
| **Eigenes Token einschleusen:** Angreifer erzeugt ein Token, berechnet dessen SHA-256 und fügt eine Zeile `(user_id = <Opfer>, token_hash = …)` ein | **möglich** ✘ — der Hash ist ohne Geheimnis berechenbar; die Zeile wird akzeptiert, der Angreifer handelt danach als das Opfer |
| Bestehende Zeile ändern: `user_id` umbiegen, `expires_at` verlängern, `revoked_at` zurücksetzen | **möglich** ✘ — nichts erkennt die Manipulation |
| Einmal-Code einschleusen (Code-Tausch) | möglich ✘ (gleiches Prinzip) |
| Geleaktes Backup + Brute Force | praktisch ausgeschlossen (256 Bit) ✔ |
| Spalte `token_hint` | speichert die **letzten 4 Zeichen des Tokens im Klartext** (24 Bit Teilwissen) — unnötig, wird entfernt ✘ |

Ziel dieser ADR: **Auch mit vollem Lese- und Schreibzugriff auf die Datenbank (ohne Zugriff auf die Anwendung und ihr Geheimnis) kann ein Angreifer weder ein Token erlangen noch ein gültiges Token herstellen oder vorhandene Zugänge verändern.**

## Bedrohungsmodell

- **In Scope:** Datenbank-Dump/Backup (Lesen), SQL-Zugriff mit Schreibrechten (Manipulieren/Einfügen/Löschen), Datenbank-Logs und `pg_stat_statements` (enthalten nur Parameterwerte, die die Anwendung sendet), Replikate.
- **Außerhalb:** Kompromittierung des Anwendungsservers samt Geheimnis (dann gilt: kurze Laufzeiten, Widerruf im Profil, nur Daten-Routen); Ausspähen im Browser/Client des Benutzers; Netzwerkabhören (TLS ist Voraussetzung).

## Entscheidung (Vorschlag)

### 1. Geheimer Schlüssel außerhalb der Datenbank
Die Anwendung hält einen Hauptschlüssel `MCP_TOKEN_KEY` (≥ 32 zufällige Bytes, Base64) **ausschließlich in der Geheimnisverwaltung der Anwendung** — IIS: `.env` außerhalb des Webroots bzw. Key Vault (ADR 0023), Docker: Umgebungsvariable/Secret, nie in Postgres, nie in Datenbank-Backups, nie im Repository, nie in Logs. Daraus werden per **HKDF-SHA-256** zwei getrennte Teilschlüssel abgeleitet (`info = "learny/mcp/token-hash/v1"` bzw. `"learny/mcp/row-mac/v1"`).

Start-Verhalten: Fehlt der Schlüssel oder ist er zu kurz (Pflichtvariable), **bricht der Start ab** (fail closed). Nur im Dev-Modus wird einmalig ein Schlüssel in `data/` erzeugt (wie ADR 0023: atomar, Marker, fehlender Schlüssel bei bestehender Installation = Fehler statt stiller Neuerzeugung). Gelegentlich genannte „Pepper-Datei neben der Datenbank“ ist ausdrücklich **nicht** zulässig.

### 2. Schlüssel-Hash statt reinem Hash (Schutz gegen Einschleusen)
`token_hash = HMAC-SHA256(K_hash, "lcm-token-v1" ‖ token)`. Ohne `K_hash` kann niemand zu einem selbst gewählten Token den passenden Datenbankeintrag berechnen — das Einschleusen eigener Zeilen scheitert. Dasselbe gilt für Einmal-Codes (`HMAC(K_hash, "lcm-code-v1" ‖ code)`).

### 3. Zeilen-Integrität (Schutz gegen Manipulation bestehender Zugänge)
Jede Zeile in `mcp_links` erhält `row_mac = HMAC-SHA256(K_mac, kanonische Kodierung von id ‖ user_id ‖ kind ‖ token_hash ‖ created_at ‖ expires_at ‖ revoked_at ‖ key_id)`. Die Prüfung läuft bei **jedem** API-Aufruf vor allen anderen Prüfungen, mit `CryptographicOperations.FixedTimeEquals`:

- falsche `user_id`, verlängerter Ablauf, zurückgesetzter Widerruf, vertauschter Hash → MAC passt nicht → **401**, Eintrag im Audit (`mcp.token.integrity_failure`, ohne Token/Hash);
- Widerruf und Ablauf-Änderungen durch die Anwendung berechnen den MAC neu; `last_used_at` ist bewusst **nicht** im MAC (sonst Schreiblast je Aufruf);
- ein Angreifer kann Zeilen nur **löschen** oder (durch Ausfall) Zugänge sperren — nie Rechte hinzufügen. Gelöschte Zeilen = Zugang weg (fail closed).

### 3a. Kein Token-Teilwissen in der Datenbank
Die Spalte `token_hint` (heute `…` + letzte 4 Zeichen des Tokens) entfällt; als Wiedererkennung im Profil dienen Name, Art, Erstellungs-, Ablauf- und letzte Nutzungszeit sowie eine **zufällige, vom Token unabhängige Kurzkennung** (z. B. 4 Zeichen aus der Zeilen-ID). Bestehende Hinweise werden bei der Migration geleert.

### 4. Token-Format und Erzeugung unverändert
`lcm_` + Base64url(32 Byte aus `RandomNumberGenerator`), 256 Bit; Anzeige nur einmal bei der Erzeugung; der Klartext erreicht **nie** die Datenbank (Hash und MAC werden in der Anwendung berechnet, auch Parameter in Datenbank-Logs enthalten nur Hashes). Tokens erscheinen nie in Logs, Fehlermeldungen oder API-Antworten (auch nicht gekürzt).

### 5. Einmal-Codes atomar verbrauchen
Austausch per einzelner Anweisung (`UPDATE … SET used_at = @now WHERE code_hash = @h AND used_at IS NULL AND expires_at > @now`; betroffene Zeilen = 1), damit Parallelität keinen Doppelverbrauch erlaubt (Lehre aus dem Upsert-Fehler bei parallelen Schreibzugriffen, siehe Commit „Upsert ohne Schlüsselverletzung“). Codes und Zeilen erhalten ebenfalls `row_mac`.

### 6. Schlüsselrotation
`key_id` (z. B. `k1`) steht in jeder Zeile. Konfiguration: `MCP_TOKEN_KEY` (aktiv, Pflicht) und optional `MCP_TOKEN_KEY_PREVIOUS` (nur Prüfung). Nach Rotation werden Tokens beim nächsten erfolgreichen Gebrauch mit dem neuen Schlüssel neu gehasht/MAC-geschützt (der Klartext liegt dann vor). Ein **Verlust** des Schlüssels macht alle Tokens ungültig (Benutzer verbinden neu) — kein Datenverlust an Lerninhalten; der Schlüssel gehört deshalb in die Sicherung der Geheimnisse, getrennt von der Datenbank-Sicherung.

### 7. Migration bestehender Tokens
Neue Spalten `hash_alg` (`sha256` = bisher, `hmac1` = neu), `key_id`, `row_mac` (nullable, `CREATE`/`ALTER … IF NOT EXISTS`, SQLite und PostgreSQL). Ein bestehendes `sha256`-Token wird weiter akzeptiert und bei seinem **ersten erfolgreichen Gebrauch** auf `hmac1` umgestellt (Klartext liegt dann vor). Altbestände sind bis dahin nur so sicher wie bisher; deshalb **Frist**: Nach `MCP_LEGACY_TOKEN_UNTIL` (Standard: 90 Tage nach dem Einführungsdatum) werden nicht umgestellte `sha256`-Zeilen abgelehnt, Benutzer verbinden neu (Hinweis im Profil). Neue Zeilen entstehen ausschließlich als `hmac1`.

### 8. OAuth-Clients: verschlüsselt in der Datenbank (Entscheidung)
Heute liegen registrierte OAuth-Clients nur im Speicher des MCP-Servers; nach einem Neustart/Deployment muss sich Claude neu registrieren. Künftig werden sie in **Learny** (PostgreSQL/SQLite) persistiert — **verschlüsselt**, mit einem Schlüssel aus der `.env`:

- Tabelle `mcp_oauth_clients(client_id PK, key_id, nonce, ciphertext, created_at, row_mac)`. Der Klartext-Datensatz (`client_name`, `redirect_uris`, `token_endpoint_auth_method`, `scopes`, `application_type`, ggf. gehashtes Secret) wird mit **AES-256-GCM** verschlüsselt; Schlüssel = HKDF-Teilschlüssel (`info = "learny/mcp/client-enc/v1"`) aus `MCP_TOKEN_KEY`, `client_id` als zusätzlich authentifizierte Daten (AAD), zufällige 96-Bit-Nonce je Datensatz. Ein Datenbank-Angreifer sieht weder Namen noch Redirect-URIs und kann den Datensatz nicht verändern (GCM-Tag + `row_mac`) oder austauschen (AAD).
- Öffentliche Clients haben kein Secret; Secrets vertraulicher Clients werden nur als HMAC gespeichert (wie Tokens).
- Zugriff nur über Endpunkte mit Dienstschlüssel (`X-MCP-Key`; kein Zugriff auf Benutzerdaten): `POST /api/mcp/oauth/clients` (Registrierung) und `GET /api/mcp/oauth/clients/{client_id}`. Der MCP-Server hält registrierte Clients zusätzlich kurz im Speicher (Cache) und lädt sie bei Bedarf nach; offene Anmeldungen (10 min) und MCP-Autorisierungscodes (5 min) bleiben bewusst im Speicher — ein Neustart bricht nur eine laufende Anmeldung ab.
- Obergrenze und Aufräumen: höchstens 1000 Clients, ungenutzte Clients (nie für eine Anmeldung verwendet) verfallen nach 30 Tagen, Rate-Limit auf der Registrierung.

### 9. Zusätzliche Härtung der Datenbank (Defense in Depth, keine alleinige Maßnahme)
- Die Anwendung nutzt eine eigene Rolle ohne Superuser/`CREATEROLE`; Backups verschlüsseln und getrennt vom Schlüssel aufbewahren.
- `log_statement`/`pg_stat_statements` dürfen Parameterwerte enthalten — sie enthalten nur HMACs, nie Klartext-Tokens.
- Optional später: Zeilenschutz je Benutzer über Postgres-Rollen ist **nicht** vorgesehen (Isolation bleibt in der Anwendung, ADR 0011).

## Konsequenzen

- Ein reiner Datenbank-Angreifer erlangt weder Tokens noch kann er Zugänge erzeugen oder verändern; bestenfalls kann er Zugänge löschen (Verfügbarkeit).
- Neues **Pflicht**-Geheimnis `MCP_TOKEN_KEY` im Betrieb (IIS-Workflow/Docker-Compose/`.env.example`/Doku); Sicherung des Schlüssels nötig, getrennt von Datenbank-Sicherungen. Der Start schlägt ohne gültigen Schlüssel fehl.
- Verlust des Schlüssels macht Tokens **und** gespeicherte OAuth-Clients unbrauchbar: Benutzer und Claude verbinden neu (kein Verlust von Lerninhalten).
- Schema-Erweiterung in `mcp_links`/`mcp_link_codes` (+ ggf. `mcp_oauth_clients`); `database-schema.md` ist mitzupflegen.
- Etwas mehr CPU je Aufruf (zwei HMACs) — vernachlässigbar.
- Bestehende Tokens laufen über die Frist aus oder werden beim Gebrauch umgestellt; die Frist begrenzt das Restrisiko der Altbestände.

## Entscheidungen (2026-10-26)

1. **Schlüsselablage:** `MCP_TOKEN_KEY` in der `.env` (außerhalb des Webroots, ADR 0023; Docker: `.env`/Umgebungsvariable). Key Vault ist optional später möglich, nicht Teil dieser Umsetzung.
2. **Altbestand:** Übergangsfrist von **90 Tagen** ab Einführung; bestehende SHA-256-Tokens werden beim ersten Gebrauch umgestellt, danach abgelehnt (Benutzer verbinden neu). *(Vom Auftraggeber mit „ja“ zur Empfehlung bestätigt; Änderung der Frist über `MCP_LEGACY_TOKEN_UNTIL`.)*
3. **OAuth-Clients:** in der Datenbank, **verschlüsselt** mit einem Schlüssel aus der `.env` (Abschnitt 8).
4. **`MCP_TOKEN_KEY` ist Pflicht:** außerhalb von `Development` bricht der Start ohne gültigen Schlüssel (Base64, ≥ 32 Byte) ab; Docker-Compose (`${MCP_TOKEN_KEY:?…}`), IIS-Workflow (GitHub-Secret `MCP_TOKEN_KEY`, Prüfung vor dem Deployment), `.env.example`, Variablentabellen und Hilfe führen ihn als Pflichtvariable. In `Development` wird einmalig ein Schlüssel in `data/` erzeugt.

## Umsetzung und Abweichungen

Umgesetzt in `Services/McpTokenProtector.cs`, `McpLinkService.cs`, `McpOAuthClientStore.cs`, `Infrastructure/McpTokenKeys.cs`, `Data/Schema.cs` (+ `EnsureColumn`/`EnsureTableColumnAsync`), `Api/ApiEndpoints.cs` (`POST/GET /api/mcp/oauth/clients`, nur Dienstschlüssel), MCP-Server (`LearnCardsApiClient`, `ProprietaryOAuthService.EnsureClientAsync`), Konfiguration und Doku. Tests: `McpTokenUnitTests` (M1–M44) und Neustart-Test im Ende-zu-Ende-Lauf.

Abweichungen/Präzisierungen gegenüber dem Vorschlag:
- **Persistiert werden nur öffentliche Clients** (`token_endpoint_auth_method=none`, kein Secret — der Fall für Claude und andere DCR-Clients). Dynamisch registrierte **vertrauliche** Clients (`client_secret_post`) bleiben im Arbeitsspeicher des MCP-Servers (Neuregistrierung nach Neustart); ihre Secrets würden sonst durch den MCP-Server über die API gehasht werden müssen. Vorregistrierte Clients aus `mcpsettings.json` sind unverändert.
- **Altbestand:** Standard-Fristende `2027-01-31` (`MCP_LEGACY_TOKEN_UNTIL`, ISO-Datum). Bis dahin kann ein Datenbank-Angreifer eine Zeile mit `hash_alg='sha256'` einschleusen — das Restrisiko des Übergangs; danach sind Altzeilen unbrauchbar. Beim Umstellen werden `token_hint`-Teile durch `#abcd` ersetzt; beim Start werden alle noch vorhandenen Hinweise (letzte Token-Zeichen) überschrieben.
- **Zeitstempel im MAC** in ganzen Sekunden (UTC), damit die Genauigkeit der Datenbank (SQLite/PostgreSQL) keine falschen Ablehnungen erzeugt.
- **Entwicklung:** `data/mcp-token.key` wird einmalig erzeugt (Development); der Integrationstest setzt einen Testwert.
- **Betrieb:** IIS-Workflow verlangt das GitHub-Secret `MCP_TOKEN_KEY` (Prüfung von Format und Länge vor dem Deployment); Docker-Compose bricht ohne `MCP_TOKEN_KEY` ab (`:?`).

## Umsetzungsplan (ursprünglich)

1. `Services/McpTokenProtector.cs` (HKDF, HMAC, MAC, Konstantzeitvergleich, Schlüssel-Laden/Fail-closed), Einbindung in `McpLinkService` (Erzeugen, Prüfen, Widerrufen, Code-Tausch).
2. `Data/Schema.cs`: Spalten/Tabellen idempotent; Migration beim ersten Gebrauch; `docs/database-schema.md`.
3. Konfiguration: `AppConfig` (`MCP_TOKEN_KEY` Pflicht, `MCP_TOKEN_KEY_PREVIOUS`, `MCP_LEGACY_TOKEN_UNTIL`), `.env.example` (Wurzel und `deploy/docker`), `docker-compose.yml` (beide), `deploy/iis/web.env` + Workflow (GitHub-Secret `MCP_TOKEN_KEY`, Vorabprüfung), `docs/iis-deployment.md`, `deploy/docker/README*.md`, `docs/database-schema.md`, `docs/architecture.md`, Help-Seite, `AGENTS.md`.
3a. MCP-Server: `ProprietaryOAuthService` lädt/speichert Clients über die Dienstschlüssel-Endpunkte (mit Cache), Neustart-Test.
4. Tests (Integration/Einheit): HMAC statt SHA-256; **Angriffsfälle** mit direktem Datenbankzugriff — eingeschleuste Zeile, geänderte `user_id`, verlängerter Ablauf, zurückgesetzter Widerruf, vertauschter Hash, Code-Replay/Parallelverbrauch → alle abgelehnt; Rotation; Altbestand (Umstellung und Frist); Schlüssel fehlt → Start bricht ab (MCP aktiv); kein Token/Hash in Logs und Antworten.

## Leitplanken für Änderungen (ab Freigabe)

- Tokens, Codes, Client-Secrets nur als HMAC mit dem Schlüssel außerhalb der Datenbank speichern; Klartext nie in Datenbank, Logs, Audit, Fehlermeldungen oder API-Antworten.
- Jede Prüfung eines Zugangs verifiziert zuerst den Zeilen-MAC (Konstantzeitvergleich); jede Änderung einer geschützten Spalte berechnet den MAC neu.
- Neue Spalten gehören in den MAC, wenn sie Berechtigung, Zuordnung oder Gültigkeit beeinflussen.
- Der Schlüssel `MCP_TOKEN_KEY` wird nie zusammen mit Datenbank-Sicherungen abgelegt und nie ins Repository eingecheckt.

## Betroffener Code (bei Umsetzung)

`Services/McpLinkService.cs`, neu `Services/McpTokenProtector.cs`, `Data/Schema.cs`, `Infrastructure/AppConfig.cs`, `Api/ApiEndpoints.cs` (`/mcp/link/*`), MCP `ProprietaryOAuthService.cs` (nur bei Option A/B), Tests, Deployment-Dateien.
