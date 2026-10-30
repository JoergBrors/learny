# Architecture

> The rationale behind key decisions is documented in [ADRs](adr/README.en.md); the rules for changes are in [AGENTS.md](../AGENTS.en.md).

## Overview

LearnCards is multi-user: modules and cards are **private** to their owners, and learning data belongs to individual users ([ADR 0011](adr/0011-mehrbenutzerarchitektur.en.md)). There are also two explicitly managed areas ([ADR 0015](adr/0015-freigaben-globale-lernmodule.en.md)): **shared** modules (owners grant specific users learning access) and **global** modules (an admin publishes an immutable, versioned snapshot for everyone). Private collections remain isolated; there is no unrestricted common card collection. See the [multi-user review report](reviews/2026-10-24-mehrbenutzer-pruefbericht.en.md).

```text
                        ┌─────────────────────────────┐
                        │   LearnCards.Web (.NET 10)   │
                        │                              │
  Browser ──────────────▶  Blazor (Server-interactive) │
                        │  + REST API  /api/*          │
                        │  + OIDC login /auth/*        │
                        └───────────────┬──────────────┘
                                         │
                        ┌────────────────┴───────────────┐
                        │                                 │
                   SQLite file                    PostgreSQL (wire protocol)
              (local, no Docker)              (Docker Compose / Azure PostgreSQL)

  Claude Desktop, etc. ──▶ LearnCards.McpServer (stdio or HTTP MCP)
                           │            │
                           │            └── OAuth login via Learny (Authorization Code + PKCE)
                           └──────────────────────────────▶ /api (Bearer: user-bound MCP token)
```

The single deployable Web unit includes UI, API, and authentication, avoiding a separate reverse proxy in development (no CORS, second process, or special SSE routing). Blazor Server streams through SignalR; card chat additionally exposes the standard `/api/chat/` SSE endpoint for API/MCP consumers.

## Why No NuGet Packages?

The port deliberately uses **only the .NET SDK and ASP.NET Core framework** (no `Microsoft.EntityFrameworkCore.*`, `Npgsql`, `Microsoft.Data.Sqlite`, OpenAI SDK, or `Microsoft.AspNetCore.Authentication.OpenIdConnect` middleware):

1. **Robustness in restricted networks:** Corporate proxies/build servers without NuGet access can still execute `dotnet build`/`dotnet publish` with only the installed SDK.
2. **Direct control of wire behavior:** PostgreSQL SCRAM-SHA-256 and OIDC token validation are implemented directly against their respective specifications and are inspectable in `src/LearnCards.Web/Data/PostgresDatabase.cs` and `src/LearnCards.Web/Auth/OidcService.cs`.

If EF Core, Npgsql, or the official OpenAI SDK becomes preferable, the deliberately narrow `IDatabase` interface (`Data/IDatabase.cs`) and `OpenAiClient` allow replacement without changing endpoints/UI.

## Projects

| Project | Purpose |
|---|---|
| `LearnCards.Web` | Blazor UI, REST API, authentication, data access, AI integration |
| `LearnCards.McpServer` | MCP server for Claude Desktop and other clients over stdio or HTTP |

### LearnCards.Web Directory Layout

- `Domain/`: Entities/DTOs (cards with optional images, modules, quizzes, chat, users/roles; `CardImages.cs` limits/validation), matching canonical card JSON.
- `Data/`: `IDatabase` abstraction, `SqliteDatabase` (P/Invoke `libsqlite3`), `PostgresDatabase` (native v3 wire protocol, SCRAM-SHA-256/MD5/plaintext, optional TLS).
- `Services/`: `CardRepository` (owner-filtered access; `.Sharing.cs` for shared/global read authorization), `UserService` (users, roles, audit), `SharingService` (shares, submissions, publication, global results), `McpLinkService` (MCP connections), `OpenAiClient`, `QuizService`, `ChatService`.
- `Auth/`: `OidcService` (discovery, PKCE authorization code flow, JWKS ID token validation), `AuthEndpoints` (`/auth/login`, `/auth/callback`, `/auth/logout`).
- `Api/`: `ApiEndpoints` (Minimal API under `/api`, compatible with original FastAPI routes).
- `Components/`: Blazor pages (`Home`, `ModulePage`, `Login`, `Profile`, `Admin`, `McpConnect`, `Help`, shared `SharePanel`), components (`FlashCardView`, `ChatPanel`, `QuizPanel`).
- `Infrastructure/`: `AppConfig` (environment/.env configuration), `EnvLoader`.

## Authentication

Two modes are selected via `AUTH_MODE=auto` according to configuration (`AppConfig`). **Historical warning:** Without OIDC configuration, `auto` silently selected `dev` (review F1). Always explicitly set `AUTH_MODE=oidc` in production; ADR 0026 subsequently introduced fail-closed startup behavior.

- **`oidc`:** When `OIDC_ISSUER` and `OIDC_CLIENT_ID` are configured, use full PKCE authorization code flow against Entra ID or another OIDC provider and create a cookie after validated JWKS signature/`iss`/`aud`/`exp`/`nonce`.
- **`dev`:** Local login without external identity provider, for development only. **Never production.** Default `local-dev-user` is admin; `?devUser=<name>[&devRoles=Admin.All]` signs in additional test users.

The REST API under `/api` historically accepted `X-MCP-Key` alongside cookies. This shared key is now only a service credential for OAuth code exchange and schema, **never user data access**. MCP users authenticate with user-bound Learny tokens (OAuth or personal token, [ADR 0013](adr/0013-mcp-benutzergebundene-anmeldung.en.md)); HTTP clients use authorization code plus PKCE; `client_credentials` is no longer supported.

**Users/sessions:** On login, external `provider` + `sub` is mapped to an internal user and the cookie carries `uid`. Every request checks that the user is active and refreshes roles from the DB. Historical review limitations: cookies remaining usable after logout (F2); open Blazor circuits not immediately honoring disablement (F3).

**Roles:** `user`, `admin`, `auditor` via `/admin` or Entra app roles ([ADR 0012](adr/0012-rollen-aus-entra-app-roles.en.md)). ADR 0029 later adds `themen_admin`.

**MCP:** Revocable user-bound `lcm_…` tokens, limited to approved data routes and managed from profile ([ADR 0013](adr/0013-mcp-benutzergebundene-anmeldung.en.md)).

## Data Model

For full PostgreSQL schema, ER diagram, and migrations see [database-schema.md](database-schema.en.md).

**Sharing/global area (ADR 0015):** `CardRepository.ResolveModuleAccessAsync` classifies a module for the current user as editable `private`, or read-only-for-learning `shared`/`global`. `module_shares` are revocable. Global snapshots occupy distinct `modules` rows (`scope='global'`, `publication_id`, `global_version`, `global_status`), created from admin-reviewed `module_submissions`. `quiz_results.context` identifies private/shared/global, but results always belong to the learner. Admins/auditors have minimized read-only access to global results. UI tabs: My Modules / Shared with Me / Global Modules, module sharing panel, and publication/global result administration.

**Multi-user (ADR 0011):** Internal `users.id` mapped to cookie `uid`; `modules` and `cards` have `owner_user_id` and are invisible to other users. Server DB-checks `user`, `admin`, `auditor` permissions; profile is `/profile`, admin is `/admin`. Entra `Admin.All` maps to `admin` through app roles ([ADR 0012](adr/0012-rollen-aus-entra-app-roles.en.md)); complete matrix in [ADR 0011](adr/0011-mehrbenutzerarchitektur.en.md).

Legacy Python tables: `modules`, `cards`, `quiz_results` now with owner columns. Added `users`, `user_identities`, `user_roles`, `audit_log`, `mcp_links`, `mcp_link_codes`. User-scoped data includes modules/cards, `user_preferences`, `user_card_states`, `chat_history`, `quiz_results`, and MCP access. Only the Blazor UI persists chat history, not `POST /api/chat/`.

Canonical card JSON fields are `module`, `category`, `term`, `question`, `definition`, `how_it_works`, `context`, `key_fact`, `reference_answer`, `chat_prompt`, `official_sources`, `archived`, `sort_order`. The API exposes the live format at `/api/schema/import/cards`; MCP `get_import_schema` and HTTP `/schema/import/cards` forward it.

**MCP card lifecycle:** `get_card` → edit → `update_card` (`PUT /api/cards/{id}` via `CardRepository.UpdateCardAsync`, sole write pathway and owner-only, with `X-Card-Updated-At` conflict checks). `export_cards` (`GET /api/modules/{id}/export/cards`, `ModuleTransferService.ExportCardsAsync`) exports ordered card batches (`offset`/`limit`, ~4 MiB image budget) as canonical JSON for `import_cards`, using image Base64 through `CardImageService`, never provider keys/signed URLs.

## Images in Cards

Optional up to five PNG/JPEG/WebP images, max 2 MiB decoded each, supplied in `images[]` as Base64 ([ADR 0016](adr/0016-bilder-in-lernkarten.en.md)). Server validates Base64, size, and magic signature. `CardImageService` stores bytes in configured `IMAGE_STORAGE_PROVIDER=database|local|azureblob|s3|sharepoint` ([ADR 0017](adr/0017-konfigurierbare-bildspeicher.en.md)). Database `card_images` retains metadata (provider, key, MIME, size, SHA-256, alt text, caption, sort order).

```text
Browser / MCP ──► GET /api/images/{id} ──► Authorization (private/shared/global)
                                              │
                                      Image cache (RAM + file, remote only)
                                              │ Hit: no storage access
                                              ▼
                             IImageStorageProvider ◄── IStorageAccessBroker (read-only SAS/presign, 60 s)
                  database │ local │ azureblob │ s3 │ sharepoint   (SafeHttp: no redirects, SSRF protection, size cap)
```

Clients retrieve images exclusively through `/api/images/{id}` ([ADR 0018](adr/0018-zentraler-image-proxy-und-storage-zugriff.en.md)); card JSON exposes only the proxy URL, never provider, key, SAS, or cloud URL. `FlashCardView` uses `loading="lazy"`. List endpoints serve metadata only; Base64 requires `?include_image_data=true`. Provider migration must be explicitly triggered by admin at `/api/admin/images/migrate`, preserving the source.

## Certificates and Blockchain Anchoring

After a passed, session-verified module quiz, `QuizService` calls `CertificateService` to issue a signed Open Badge 3.0 ([ADR 0021](adr/0021-zertifikate-und-open-badges.en.md)). The badge verification hash is queued; background `AnchorService` groups credentials in a Merkle tree, applies strict cost guard `AnchorCostGuard` (≤EUR 0.001/credential), and anchors the root in a zero-value Polygon transaction ([ADR 0022](adr/0022-polygon-verankerung-und-kostenschranke.en.md)). Without blockchain, approval, or a safe cost estimate, the badge remains valid and anchoring remains pending.

```text
QuizService (passed) ─► CertificateService ─► Badge (VC-JWT, ES256) ─► Queue (chain_status=pending)
AnchorWorker ─► AnchorService: batch + Merkle root ─► AnchorCostGuard (twice) ─► IAnchorSigner ─► IChainClient (JSON-RPC) ─► Confirmation/finality ─► On-chain verification
```

## Card Editor and Rich Text

The card editor (`/module/{id}/edit`) is a UI layer above existing services ([ADR 0020](adr/0020-sichere-visuelle-bearbeitung-formatierter-lerninhalte.en.md)): `CardRepository` handles card CRUD, conflict checks, duplication/order/archive; `CardImageService` handles images; `QuizService`/`QuizPoolService` handle questions. Formatted texts use a constrained markup subset within existing fields. Browser `RichTextEditor` + `rte.js` serialize it to markup, and `RichText.ToHtml` safely renders it server-side (`RichTextView` and `FlashCardView`). There is no second persistence implementation or new table.

```text
Browser (contenteditable) ─► rte.js: DOM → markup ─► ModuleEditor ─► CardRepository / QuizService ─► Database
                                                        │ CardJson.Validate (+ RichText.Validate)
Learning/preview ◄── RichText.ToHtml (fully escaped, fixed element allowlist) ◄── Markup in text fields
```

## Quiz Engine

Card and module quizzes use one engine ([ADR 0019](adr/0019-einheitliches-quizsystem.en.md)). `QuizService.StartSessionAsync` selects questions using per-module settings (stored pool → optional AI generation → offline questions from cards), creates a server-side `quiz_sessions` with answers, and `SubmitQuizAsync` grades choices locally or free text with AI/heuristic fallback, persisting result and pass status.

```text
CardQuizPanel (card) ┐                    ┌ Pool quiz_questions (card_json projection of quiz[] · ai · editor)
QuizPanel (module)   ├─► QuizRunner ─► QuizService ─► AI (CompleteJsonAsync, optional) ─► Pool (owner only)
REST /api/quiz/*     ┘                    └ Session quiz_sessions ─► grading ─► quiz_results (+ session, passed, pass_percent, module_version)
```

Terms: **Quiz configuration** (`quiz_configs` per module/version), **question pool** (`quiz_questions`), **card JSON** (`quiz[]` fixed questions only), **quiz session** (`quiz_sessions`, valid 4 h, cleaned after 24 h), **quiz result** (`quiz_results`, retained). Card `quiz[]` is canonical import/export and source of truth for embedded questions; AI questions reside only in the pool. Published global versions freeze config/active questions; learners never edit the pool.

## AI Integration

`OpenAiClient` uses Chat Completions over HTTP, including Azure OpenAI or local compatible endpoints through `OPENAI_BASE_URL`. Without `OPENAI_API_KEY`, `ChatService` and `QuizService` use a clearly labeled offline mode (questions from cards and keyword-heuristic grading), allowing development without AI credentials.

## Docker Image Distribution

Both services are additionally published to Docker Hub (`joergb23/learny-web` and `joergb23/learny-mcp`; amd64/arm64). `deploy/docker/` runs Web/PostgreSQL and optionally MCP; Web remains one unit (ADR 0003) and MCP a distinct API client (ADR 0007). See [ADR 0024](adr/0024-docker-hub-veroeffentlichung.en.md).

## MCP Token Protection Against Database Access (ADR 0025)

MCP tokens and one-use codes are stored only as keyed HMACs with row MAC, using `MCP_TOKEN_KEY` from `.env`. MCP OAuth clients are encrypted using AES-256-GCM in `mcp_oauth_clients`. An attacker with database-only read/write cannot retrieve, create, or tamper with working credentials (`McpTokenProtector`, `McpLinkService`, `McpOAuthClientStore`).

## Topics, Learning Paths, and Final Exams

A topic ([ADR 0027](adr/0027-themen-lernpfade-versionierung-und-abschlusspruefungen.en.md)) orders existing modules, configures module/final exams, and publishes immutable versions. Unchanged modules reuse frozen copies by content hash; changed modules receive new copies.

```text
Draft (topics, topic_modules, topic_exam_configs) ─ Publish ─► topic_versions ─► topic_version_modules ─► frozen copies (scope=topic)
Owner ─► Share / assign          Owner ─► Submit ─► pending_approval ─► Administrator: approve | reject
Learner ─► TopicPage ─► StartTopicExamSessionAsync ─► QuizService (session, grading) ─► topic_exam_attempts ─► progress
```

Services: `TopicService` (+ `.Versions`, `.Exams`), `CardRepository.Topics`, `QuizService.Topics`; REST `Api/TopicEndpoints.cs`. MCP can read (`list_topics`, `get_topic`, `get_topic_exam_structure`) and manage own drafts using JSON (ADR 0028). No second quiz engine or topic credentials.

### Modules/Topics as JSON, Cloning, MCP Management (ADR 0028)

Draft topics reference **owned private modules only** (`TopicService.CanUseModuleInTopicAsync` applied across UI/REST/MCP/import). Clone global modules into owned modules first using `ModuleTransferService.CloneGlobalAsync`.

```text
Module ─ Export ─► learny-module (cards+images+quiz+config+pool) ─ Import/clone ─► new private module (all-or-nothing)
Topic draft ─ Export ─► learny-topic (module names, order, exams, topic questions) ─ Import/update ─► draft (never published)
MCP tool ─► REST (/api/topics/import, /api/topics/{id}/definition, /api/modules/import|clone|export) ─► same services
```

Schemas: `/api/schema/import/module`, `/api/schema/import/topic` (`Api/TransferSchema.cs`); templates `wwwroot/templates/module-template.json`, `topic-template.json`. MCP tokens may import topics and update owned drafts through `TokenMayReadTopics`, but cannot publish, share, assign, or administer.

### Topic Access, Mandatory Assignments and Topic Administration (ADR 0029)

```text
Published topic ─► immutable version ─► frozen module copies (scope=topic)
 Study access: ownership │ share (all versions) │ assignment (specific version) │ active global version
 Mandatory: only assignment with mandatory=1 (admin/topic admin) ─► Home "My mandatory topics"
 Status: card state + topic exams ─► not_started | in_progress | completed (+ overdue for unfinished mandatory)
 Management: /topic-admin (admin/topic admin assigns; auditor/topic admin/admin reads status) ─ /api/topic-admin/* (never MCP tokens)
```

The original private module stays protected; revocation removes only derived access. IIS deployment stops the MCP HTTP server (scheduled task or Windows service) before replacing its files, restarts afterward, verifies process and health endpoint, and fails if restart fails.
