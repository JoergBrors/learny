# Architektur

## Überblick

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
                           │            └── optional proprietäres OAuth (Bearer Token)
                           └──────────────────────────────▶ /api (X-MCP-Key)
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

- `Domain/` — Entitäten und DTOs (Karte, Modul, Quiz, Chat) — 1:1 zum kanonischen Karten-JSON-Format
- `Data/` — `IDatabase`-Abstraktion, `SqliteDatabase` (P/Invoke auf `libsqlite3`),
  `PostgresDatabase` (natives Wire-Protokoll v3, SCRAM-SHA-256/MD5/Cleartext, TLS optional)
- `Services/` — `CardRepository` (Datenzugriff), `OpenAiClient`, `QuizService`, `ChatService`
- `Auth/` — `OidcService` (Discovery, Authorization-Code-Flow + PKCE, ID-Token-Validierung gegen
  JWKS), `AuthEndpoints` (`/auth/login`, `/auth/callback`, `/auth/logout`)
- `Api/` — `ApiEndpoints` (Minimal-API unter `/api`, kompatibel zum ursprünglichen FastAPI-Schema)
- `Components/` — Blazor-Seiten (`Home`, `ModulePage`, `Login`) und Shared-Komponenten
  (`FlashCardView`, `ChatPanel`, `QuizPanel`)
- `Infrastructure/` — `AppConfig` (Konfiguration aus Umgebungsvariablen/.env), `EnvLoader`

## Authentifizierung

Zwei Modi, automatisch anhand der Konfiguration gewählt (`AUTH_MODE=auto`, siehe `AppConfig`):

- **`oidc`** — sobald `OIDC_ISSUER` und `OIDC_CLIENT_ID` gesetzt sind: vollständiger
  Authorization-Code-Flow mit PKCE gegen Entra ID (oder jeden anderen OIDC-Provider), Cookie-Session
  nach erfolgreicher ID-Token-Validierung (Signatur gegen JWKS, `iss`/`aud`/`exp`/`nonce`-Prüfung).
- **`dev`** — ohne OIDC-Konfiguration: lokaler Login ohne externen Identity Provider, für schnellen
  Einstieg in die lokale Entwicklung. **Nicht für Produktion.**

Die REST-API unter `/api` akzeptiert zusätzlich `X-MCP-Key` als Alternative zur Cookie-Session —
das ist der Zugriffsweg für den MCP-Server und andere Automatisierungs-Clients. Der MCP-Server
selbst kann gegenüber seinen eigenen HTTP-Clients optional ein proprietäres, dateibasiert
konfiguriertes OAuth mit `client_credentials` und Bearer-Token bereitstellen.

## Datenmodell

Identisch zur Python-Ursprungslösung: `modules`, `cards`, `quiz_results` — inklusive des
kanonischen Karten-JSON-Formats (`module`, `category`, `term`, `question`, `definition`,
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
