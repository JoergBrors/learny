# Architektur

> Die Begründungen der Kernentscheidungen stehen als [ADRs](adr/README.md); Regeln für Änderungen in [AGENTS.md](../AGENTS.md).

## Überblick

LearnCards ist mehrbenutzerfähig: Module und Karten sind **privat** (Eigentümer), persönliche Lerndaten sind benutzerbezogen ([ADR 0011](adr/0011-mehrbenutzerarchitektur.md)).
Zusätzlich gibt es zwei explizit verwaltete Bereiche ([ADR 0015](adr/0015-freigaben-globale-lernmodule.md)): **freigegebene** Module (der Eigentümer erlaubt einzelnen Benutzern das Lernen) und **globale** Module
(ein Administrator veröffentlicht eine eingefrorene, versionierte Kopie für alle). Private Bestände bleiben isoliert; es gibt keinen frei zugänglichen gemeinsamen Kartenbestand. Eine Bewertung der Benutzertrennung steht im [Prüfbericht](reviews/2026-10-24-mehrbenutzer-pruefbericht.md).

```
                        ┌─────────────────────────────┐
                        │   LearnCards.Web (.NET 10)   │
                        │                              │
  Browser ──────────────▶  Blazor (Server-Interaktiv)  │
                        │  + REST-API  /api/*          │
                        │  + OIDC-Login /auth/*        │
                        └───────────────┬──────────────┘
                                         │
                        ┌────────────────┴───────────────┐
                        │                                 │
                 SQLite-Datei                    PostgreSQL (Wire-Protokoll)
              (lokal, kein Docker)          (Docker-Compose / Azure Database for PostgreSQL)

  Claude Desktop u.a. ──▶ LearnCards.McpServer (stdio oder HTTP MCP)
                           │            │
                           │            └── OAuth-Anmeldung über Learny (Authorization Code + PKCE)
                           └──────────────────────────────▶ /api (Bearer: benutzergebundenes MCP-Token)
```

Eine einzige deploybare Web-Einheit vereint UI, API und Auth — dadurch entfällt der separate
Reverse-Proxy im Entwicklungsbetrieb (kein CORS, kein zweiter Prozess, keine SSE-Sonderfälle,
da Blazor-Server-Streaming ohnehin über SignalR läuft; der Karten-Chat nutzt zusätzlich einen
klassischen SSE-Endpunkt `/api/chat/` für API-/MCP-Konsumenten).

## Warum ohne NuGet-Pakete?

Die Portierung ist bewusst **ausschließlich auf dem .NET-SDK und dem ASP.NET-Core-Framework**
aufgebaut (keine `Microsoft.EntityFrameworkCore.*`, kein `Npgsql`, kein `Microsoft.Data.Sqlite`,
kein OpenAI-SDK, keine `Microsoft.AspNetCore.Authentication.OpenIdConnect`-Middleware). Das hat
zwei Gründe:

1. **Robustheit gegenüber eingeschränktem Netzwerkzugriff** (z. B. Firmenproxys, Build-Server
   ohne NuGet-Zugriff) — `dotnet build`/`dotnet publish` funktionieren mit nichts weiter als dem
   installierten SDK.
2. **Volle Kontrolle über das Wire-Verhalten** — insbesondere die SCRAM-SHA-256-Authentifizierung
   gegen PostgreSQL und die OIDC-Token-Validierung sind direkt gegen die jeweilige Spezifikation
   implementiert und leicht nachvollziehbar (`src/LearnCards.Web/Data/PostgresDatabase.cs`,
   `src/LearnCards.Web/Auth/OidcService.cs`).

Wer lieber mit EF Core / Npgsql / dem offiziellen OpenAI-SDK arbeiten möchte: Die
`IDatabase`-Abstraktion (`Data/IDatabase.cs`) und `OpenAiClient` sind bewusst schmal gehalten,
sodass ein Austausch gegen Standard-Pakete (sobald NuGet erreichbar ist) ohne Änderungen an den
API-Endpunkten oder der UI möglich ist.

## Projekte

| Projekt | Zweck |
|---|---|
| `LearnCards.Web` | Blazor-UI, REST-API, Auth, Datenzugriff, KI-Integration |
| `LearnCards.McpServer` | MCP-Server für Claude Desktop / andere MCP-Clients, wahlweise per `stdio` oder HTTP |

### LearnCards.Web — Ordnerstruktur

- `Domain/` — Entitäten und DTOs (Karte inkl. optionaler Bilder, Modul, Quiz, Chat, Benutzer/Rollen; `CardImages.cs` mit Limits und Validator) — Karten 1:1 zum kanonischen Karten-JSON-Format
- `Data/` — `IDatabase`-Abstraktion, `SqliteDatabase` (P/Invoke auf `libsqlite3`),
  `PostgresDatabase` (natives Wire-Protokoll v3, SCRAM-SHA-256/MD5/Cleartext, TLS optional)
- `Services/` — `CardRepository` (besitzgefilterter Datenzugriff; `.Sharing.cs`: Zugriffsentscheid und Lesepfade für freigegebene/globale Module), `UserService` (Benutzer, Rollen, Audit), `SharingService` (Freigaben, Einreichung, Veröffentlichung, globale Ergebnisse), `McpLinkService` (MCP-Zugänge), `OpenAiClient`, `QuizService`, `ChatService`
- `Auth/` — `OidcService` (Discovery, Authorization-Code-Flow + PKCE, ID-Token-Validierung gegen
  JWKS), `AuthEndpoints` (`/auth/login`, `/auth/callback`, `/auth/logout`)
- `Api/` — `ApiEndpoints` (Minimal-API unter `/api`, kompatibel zum ursprünglichen FastAPI-Schema)
- `Components/` — Blazor-Seiten (`Home`, `ModulePage`, `Login`, `Profile`, `Admin`, `McpConnect`, `Help`; Shared u. a. `SharePanel`) und Shared-Komponenten
  (`FlashCardView`, `ChatPanel`, `QuizPanel`)
- `Infrastructure/` — `AppConfig` (Konfiguration aus Umgebungsvariablen/.env), `EnvLoader`

## Authentifizierung

Zwei Modi, automatisch anhand der Konfiguration gewählt (`AUTH_MODE=auto`, siehe `AppConfig`). **Achtung:** Fehlt die OIDC-Konfiguration, fällt `auto` lautlos auf `dev` zurück (Prüfbericht F1) — in Produktion immer `AUTH_MODE=oidc` setzen:

- **`oidc`** — sobald `OIDC_ISSUER` und `OIDC_CLIENT_ID` gesetzt sind: vollständiger
  Authorization-Code-Flow mit PKCE gegen Entra ID (oder jeden anderen OIDC-Provider), Cookie-Session
  nach erfolgreicher ID-Token-Validierung (Signatur gegen JWKS, `iss`/`aud`/`exp`/`nonce`-Prüfung).
- **`dev`** — ohne OIDC-Konfiguration: lokaler Login ohne externen Identity Provider, für schnellen
  Einstieg in die lokale Entwicklung. **Nicht für Produktion.** Der Standardbenutzer `local-dev-user` ist Administrator; `?devUser=<name>[&devRoles=Admin.All]` meldet weitere Testbenutzer an.

Die REST-API unter `/api` akzeptiert zusätzlich `X-MCP-Key` als Alternative zur Cookie-Session —
das ist der Zugriffsweg für den MCP-Server und andere Automatisierungs-Clients. Der gemeinsame Schlüssel ist nur noch ein Dienstschlüssel (Code-Tausch, Schema) ohne Datenzugriff. Benutzer arbeiten über MCP mit
benutzergebundenen Tokens (OAuth-Anmeldung bei Learny oder persönliches Token), siehe [ADR 0013](adr/0013-mcp-benutzergebundene-anmeldung.md). Der MCP-Server
stellt für HTTP-Clients einen OAuth-Ablauf (Authorization Code + PKCE) bereit, bei dem sich der Benutzer bei Learny anmeldet; es gibt kein `client_credentials` mehr.

**Benutzer und Sitzungen:** Beim Login wird die externe Identität (`provider` + `sub`) einem internen Benutzer zugeordnet; das Cookie trägt den Claim `uid`.
Bei jeder Anfrage wird der Benutzer gegen die DB geprüft (aktiv?) und die Rollen werden frisch geladen. Bekannte Grenzen (Prüfbericht): Cookie bleibt nach Logout bis Ablauf gültig (F2), offene Blazor-Circuits übernehmen Sperren nicht sofort (F3).

**Rollen:** `user`, `admin`, `auditor` — vergeben in `/admin` oder über Entra-App-Rollen ([ADR 0012](adr/0012-rollen-aus-entra-app-roles.md)).

**MCP:** benutzergebundene Tokens (`lcm_…`), nur Daten-Routen erlaubt, widerrufbar im Profil ([ADR 0013](adr/0013-mcp-benutzergebundene-anmeldung.md)).

## Datenmodell

Vollständiges Tabellenschema (PostgreSQL) inkl. ER-Diagramm und Migrationen: [database-schema.md](database-schema.md).

**Freigaben und globaler Bereich (ADR 0015):** Ein Zugriffsentscheid (`CardRepository.ResolveModuleAccessAsync`) ordnet jedes Modul aus Sicht des Benutzers als `private` (bearbeitbar), `shared` oder `global` (nur lernen) ein. Freigaben (`module_shares`) sind widerrufbar;
globale Module sind eigene Modul-Zeilen (`scope = 'global'`, `publication_id`, `global_version`, `global_status`) aus einer vom Administrator geprüften Einreichung (`module_submissions`). Ergebnisse tragen den Kontext (`quiz_results.context`) und gehören immer dem Lernenden;
Administrator und Auditor sehen globale Ergebnisse nur lesend und minimiert. Oberflächen: Reiter *Meine Module / Mit mir geteilt / Globale Module*, Freigabedialog im Modul, Administration (Vorschläge, globale Module, globale Ergebnisse).

**Mehrbenutzer (ADR 0011):** Jeder Benutzer hat eine interne ID (`users.id`, Claim `uid`); Module und Karten gehören einem Eigentümer
(`owner_user_id`) und sind für andere unsichtbar. Rollen: `user`, `admin`, `auditor` (serverseitig gegen die DB geprüft). Profil unter `/profile`,
Administration unter `/admin`. Rollen können zusätzlich über Entra-ID-App-Rollen (`Admin.All` → `admin`) vergeben werden ([ADR 0012](adr/0012-rollen-aus-entra-app-roles.md)). Details und Berechtigungsmatrix: [ADR 0011](adr/0011-mehrbenutzerarchitektur.md).

Aus der Python-Ursprungslösung stammen `modules`, `cards`, `quiz_results` — jetzt mit Eigentümer (`owner_user_id`); hinzugekommen sind `users`, `user_identities`, `user_roles`, `audit_log`, `mcp_links`, `mcp_link_codes`.
Benutzerbezogen sind: Module, Karten, `user_preferences`, `user_card_states`, `chat_history`, `quiz_results`, MCP-Zugänge. Der Chat-Verlauf wird nur von der Oberfläche gespeichert, nicht über `POST /api/chat/`.
Das kanonische Karten-JSON-Format (`module`, `category`, `term`, `question`, `definition`,
`how_it_works`, `context`, `key_fact`, `reference_answer`, `chat_prompt`, `official_sources`,
`archived`, `sort_order`), das API, Import und MCP-Server gemeinsam nutzen. Die API stellt dieses
Format zusätzlich live unter `/api/schema/import/cards` bereit; der MCP-Server reicht es per Tool
`get_import_schema` und per HTTP-Endpunkt `/schema/import/cards` weiter.

## Bilder in Karten

Karten können optional bis zu 5 Bilder (PNG/JPEG/WebP, je höchstens 2 MiB dekodiert) als Base64 im Karten-JSON tragen (`images[]`, [ADR 0016](adr/0016-bilder-in-lernkarten.md)). Sie werden serverseitig validiert (Base64, Größe, Dateisignatur)
und vom `CardImageService` über den konfigurierten **Bildspeicher** abgelegt ([ADR 0017](adr/0017-konfigurierbare-bildspeicher.md)): `IMAGE_STORAGE_PROVIDER=database|local|azureblob|s3|sharepoint`. Die Datenbank hält nur Metadaten (`card_images`: Provider, Schlüssel, MIME, Größe, SHA-256, Alt-Text, Beschriftung, Reihenfolge).

```
Browser / MCP ──► GET /api/images/{id} ──► Berechtigung (privat/freigegeben/global)
                                              │
                                      Bild-Cache (Speicher + Datei, nur Remote-Provider)
                                              │ Treffer: fertig, kein Speicherzugriff
                                              ▼
                             IImageStorageProvider ◄── IStorageAccessBroker (SAS / Presign, nur lesend, 60 s)
                  database │ local │ azureblob │ s3 │ sharepoint     (SafeHttp: kein Redirect, SSRF-Schutz, Größenlimit)
```

Clients erhalten Bilder ausschließlich über `/api/images/{id}` ([ADR 0018](adr/0018-zentraler-image-proxy-und-storage-zugriff.md)); Karten-JSON enthält nur diese URL (nie Provider, Schlüssel, SAS oder Cloud-URLs). `FlashCardView` zeigt sie mit `loading="lazy"`.
Listenendpunkte liefern nur Metadaten; Base64 gibt es nur mit `?include_image_data=true`. Die Migration zwischen Providern ist eine ausdrücklich gestartete Admin-Aktion (`/api/admin/images/migrate`), die Quelle bleibt erhalten.

## Zertifikate und Blockchain-Verankerung

Nach einem bestandenen, sitzungsverifizierten Modulquiz stellt `QuizService` über `CertificateService` ein signiertes Open Badge 3.0 aus ([ADR 0021](adr/0021-zertifikate-und-open-badges.md)). Der Prüf-Hash des Badges geht in eine Warteschlange; der `AnchorService` (Hintergrund-Worker) bündelt fällige Nachweise zu einem Merkle-Baum,
prüft die Kosten hart (`AnchorCostGuard`, ≤ 0,001 EUR je Nachweis) und verankert die Wurzel als Nullwert-Transaktion auf Polygon ([ADR 0022](adr/0022-polygon-verankerung-und-kostenschranke.md)). Ohne Blockchain, ohne Freigabe oder ohne sichere Kostenschätzung bleibt das Badge gültig und die Verankerung ausstehend.

```
QuizService (bestanden) ─► CertificateService ─► Badge (VC-JWT, ES256) ─► Warteschlange (chain_status=pending)
AnchorWorker ─► AnchorService: Batch + Merkle-Wurzel ─► AnchorCostGuard (2×) ─► IAnchorSigner ─► IChainClient (JSON-RPC) ─► Bestätigung/Finalität ─► Verifikation der On-Chain-Daten
```

## Karteneditor und formatierte Texte

Der Karteneditor (`/module/{id}/edit`) ist eine reine Oberfläche über den vorhandenen Diensten ([ADR 0020](adr/0020-sichere-visuelle-bearbeitung-formatierter-lerninhalte.md)): Karten über `CardRepository` (anlegen, ändern mit Konflikterkennung, duplizieren, sortieren, archivieren), Bilder über `CardImageService`,
Quizfragen über `QuizService`/`QuizPoolService`. Formatierte Kartentexte sind ein begrenztes Markup in den bestehenden Textfeldern; der Browser-Editor (`RichTextEditor` + `rte.js`) serialisiert in dieses Markup, `RichText.ToHtml` rendert es serverseitig gesichert (`RichTextView`, auch in `FlashCardView`).
Es gibt keine zweite Persistenz und keine neuen Tabellen.

```
Browser (contenteditable) ─► rte.js: DOM → Markup ─► ModuleEditor ─► CardRepository / QuizService ─► Datenbank
                                                         │ Validierung: CardJson.Validate (+ RichText.Validate)
Lernansicht / Vorschau ◄── RichText.ToHtml (alles kodiert, feste Elementliste) ◄── Markup in den Textfeldern
```

## Quiz-Engine

Karten- und Modulquiz nutzen dieselbe Engine ([ADR 0019](adr/0019-einheitliches-quizsystem.md)). `QuizService.StartSessionAsync` wählt Fragen (gespeicherter Pool → ggf. KI-Generierung → Offline-Ableitung aus den Karten) nach der **Quizkonfiguration des Moduls**
und legt eine serverseitige Sitzung (`quiz_sessions`, mit Lösungen) an; `SubmitQuizAsync` bewertet Auswahlfragen exakt lokal und Freitext per KI (Fallback Heuristik) und speichert das Ergebnis inkl. Bestanden-Status.

```
CardQuizPanel (Karte)  ┐                      ┌ Pool quiz_questions  (card_json = Projektion von quiz[] · ai · editor)
QuizPanel (Modul)      ├─► QuizRunner ─► QuizService ─► KI (CompleteJsonAsync, optional) ─► Pool (nur Eigentümer)
REST /api/quiz/*       ┘                      └ Sitzung quiz_sessions ─► Bewertung ─► quiz_results (+ session, passed, pass_percent, module_version)
```

Begriffe: **Quizkonfiguration** (`quiz_configs`, je Modul/Version) · **Fragenpool** (`quiz_questions`) · **Karten-JSON** (`quiz[]`, nur feste Fragen) · **Quizsitzung** (`quiz_sessions`, 4 h gültig, nach 24 h bereinigt) · **Quiz-Ergebnis** (`quiz_results`, bleibt dauerhaft).
`quiz[]` im Karten-JSON bleibt der Import-/Export-Vertrag und ist Quelle der Wahrheit für die darin gepflegten Fragen; KI-Fragen leben nur im Pool. Globale Versionen frieren Konfiguration und aktive Fragen ein; Lernende ändern den Pool nie.

## KI-Integration

`OpenAiClient` spricht die OpenAI-Chat-Completions-API (per HTTP, kompatibel zu Azure-OpenAI- oder
lokalen OpenAI-kompatiblen Endpunkten über `OPENAI_BASE_URL`). Ohne konfigurierten
`OPENAI_API_KEY` fallen `ChatService` und `QuizService` auf einen klar gekennzeichneten
Offline-Modus zurück (Quiz-Fragen direkt aus dem Kartenpool, Bewertung per Stichwort-Heuristik),
damit lokale Entwicklung ohne Schlüssel möglich bleibt.

## Verteilung als Docker-Images

Beide Dienste werden zusätzlich als öffentliche Docker-Hub-Images (`joergb23/learny-web`, `joergb23/learny-mcp`; amd64 + arm64) veröffentlicht. Das Paket `deploy/docker/` startet Web (mit PostgreSQL) und optional den MCP-Server; die Web-App bleibt eine deploybare Einheit (ADR 0003), der MCP-Server ein getrennter API-Client (ADR 0007). Siehe [ADR 0024](adr/0024-docker-hub-veroeffentlichung.md).

## Schutz von MCP-Tokens bei Datenbankzugriff (ADR 0025)

MCP-Tokens und Einmal-Codes liegen nur als HMAC (Schlüssel `MCP_TOKEN_KEY` aus der `.env`) mit Zeilen-MAC in der Datenbank; OAuth-Clients des MCP-Servers werden AES-256-GCM-verschlüsselt in `mcp_oauth_clients` gespeichert. Ein Angreifer mit Lese- und Schreibzugriff auf Postgres kann weder Tokens ablesen noch gültige herstellen oder Zugänge verändern (`McpTokenProtector`, `McpLinkService`, `McpOAuthClientStore`).

## Themen, Lernpfade und Abschlussprüfungen

Ein Thema ([ADR 0027](adr/0027-themen-lernpfade-versionierung-und-abschlusspruefungen.md)) ordnet bestehende Module in einer Reihenfolge zu (keine Duplikate), konfiguriert Modul- und Abschlussprüfungen und wird über „Veröffentlichen“ in unveränderliche Versionen eingefroren. Unveränderte Module teilen sich die eingefrorene Kopie (Inhalts-Hash), geänderte erhalten eine neue.

```
Entwurf (topics, topic_modules, topic_exam_configs) ─ Veröffentlichen ─► topic_versions ─► topic_version_modules ─► eingefrorene Modulkopien (scope=topic)
Eigentümer ─► Freigabe / Zuweisung        Eigentümer ─► Einreichen ─► pending_approval ─► Administrator: freigeben | ablehnen
Lernender ─► TopicPage ─► StartTopicExamSessionAsync ─► QuizService (Sitzung, Bewertung) ─► topic_exam_attempts ─► Fortschritt
```

Dienste: `TopicService` (+ `.Versions`, `.Exams`), `CardRepository.Topics`, `QuizService.Topics`; REST in `Api/TopicEndpoints.cs`; MCP liest (`list_topics`, `get_topic`, `get_topic_exam_structure`) und verwaltet eigene Entwürfe per JSON (ADR 0028). Es gibt keine zweite Quiz-Engine und keine Themenzertifikate.

### Module und Themen als JSON, Klonen, MCP-Verwaltung (ADR 0028)

Themenentwürfe enthalten ausschließlich **eigene private Module** (`TopicService.CanUseModuleInTopicAsync`, gleiche Prüfung für UI, REST, MCP und Import). Globale Module werden zuvor mit `ModuleTransferService.CloneGlobalAsync` in ein eigenes Modul geklont.

```
Modul ─ Export ─► learny-module (Karten+Bilder+Quiz+Konfiguration+Pool) ─ Import/Klonen ─► neues privates Modul (alles oder nichts)
Thema (Entwurf) ─ Export ─► learny-topic (Modulnamen, Reihenfolge, Prüfungen, Themenfragen) ─ Import/Update ─► Entwurf (nie veröffentlicht)
MCP-Werkzeug ─► REST (/api/topics/import, /api/topics/{id}/definition, /api/modules/import|clone|export) ─► dieselben Services
```

Schemas: `/api/schema/import/module`, `/api/schema/import/topic` (`Api/TransferSchema.cs`); Vorlagen: `wwwroot/templates/module-template.json`, `topic-template.json`. MCP-Tokens dürfen nur Themen importieren und den eigenen Entwurf aktualisieren (`TokenMayReadTopics`); Veröffentlichen, Freigaben, Zuweisungen und Administration bleiben gesperrt.
