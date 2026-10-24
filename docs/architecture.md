# Architektur

> Die Begründungen der Kernentscheidungen stehen als [ADRs](adr/README.md); Regeln für Änderungen in [AGENTS.md](../AGENTS.md).

## Überblick

LearnCards ist mehrbenutzerfähig: Module und Karten sind **privat** (Eigentümer), persönliche Lerndaten sind benutzerbezogen, es gibt keinen gemeinsamen Kartenbestand
([ADR 0011](adr/0011-mehrbenutzerarchitektur.md)). Eine Bewertung der Benutzertrennung steht im [Prüfbericht](reviews/2026-10-24-mehrbenutzer-pruefbericht.md).

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

- `Domain/` — Entitäten und DTOs (Karte, Modul, Quiz, Chat, Benutzer/Rollen) — Karten 1:1 zum kanonischen Karten-JSON-Format
- `Data/` — `IDatabase`-Abstraktion, `SqliteDatabase` (P/Invoke auf `libsqlite3`),
  `PostgresDatabase` (natives Wire-Protokoll v3, SCRAM-SHA-256/MD5/Cleartext, TLS optional)
- `Services/` — `CardRepository` (besitzgefilterter Datenzugriff), `UserService` (Benutzer, Rollen, Audit), `McpLinkService` (MCP-Zugänge), `OpenAiClient`, `QuizService`, `ChatService`
- `Auth/` — `OidcService` (Discovery, Authorization-Code-Flow + PKCE, ID-Token-Validierung gegen
  JWKS), `AuthEndpoints` (`/auth/login`, `/auth/callback`, `/auth/logout`)
- `Api/` — `ApiEndpoints` (Minimal-API unter `/api`, kompatibel zum ursprünglichen FastAPI-Schema)
- `Components/` — Blazor-Seiten (`Home`, `ModulePage`, `Login`, `Profile`, `Admin`, `McpConnect`, `Help`) und Shared-Komponenten
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

## KI-Integration

`OpenAiClient` spricht die OpenAI-Chat-Completions-API (per HTTP, kompatibel zu Azure-OpenAI- oder
lokalen OpenAI-kompatiblen Endpunkten über `OPENAI_BASE_URL`). Ohne konfigurierten
`OPENAI_API_KEY` fallen `ChatService` und `QuizService` auf einen klar gekennzeichneten
Offline-Modus zurück (Quiz-Fragen direkt aus dem Kartenpool, Bewertung per Stichwort-Heuristik),
damit lokale Entwicklung ohne Schlüssel möglich bleibt.
