# LearnCards (.NET 10)

LearnCards is a flashcard learning platform combining a Blazor Web App (interactive server rendering), built-in REST API, PostgreSQL/SQLite access, OpenID Connect login (Entra ID), and an independent MCP server. It is a complete C#/.NET 10 port of the original FastAPI/React/Python solution, **without external NuGet packages**, allowing offline builds. SQLite/PostgreSQL drivers are implemented directly; OIDC and OpenAI integration use built-in `HttpClient`.

## Architecture

- `src/LearnCards.Web/`: Blazor Web App (.NET 10 interactive server components), REST API at `/api`, OIDC endpoints at `/auth/*`, deployed as one unit.
- `src/LearnCards.McpServer/`: Independent MCP server over `stdio`/HTTP, calling Web API **on behalf of a user** (Learny OAuth or personal token; [ADR 0013](docs/adr/0013-mcp-benutzergebundene-anmeldung.en.md)). Shared `X-MCP-Key` is service-only and cannot access user data.
- `nginx/`: Optional TLS-terminating reverse proxy for Docker.
- `docker-compose.yml`: PostgreSQL + Web, optional MCP and nginx profiles.

See [Architecture](docs/architecture.en.md).

## Users, Roles, and Privacy

LearnCards supports multiple users ([ADR 0011](docs/adr/0011-mehrbenutzerarchitektur.en.md)):

- Each user has an internal ID, profile (`/profile`), and **private modules/cards**. Other users cannot access them. Learning status, chats, quiz results, and theme are user-specific.
- **Sharing and global modules** ([ADR 0015](docs/adr/0015-freigaben-globale-lernmodule.en.md)): Owners grant selected users revocable study-only access and may submit modules for global publication. Admins review and publish frozen, versioned copies. Tabs distinguish *My modules*, *Shared with me*, *Global modules*. Results always belong to learners; admins/auditors only have read access to global results.
- Roles `user`, `admin` (user management/publication at `/admin`), and `auditor` (read-only global records/results), assignable in-app or through Entra app roles (`Admin.All` → `admin`, [ADR 0012](docs/adr/0012-rollen-aus-entra-app-roles.en.md)).
- MCP access is user-scoped and revocable from profile.
- Security review: [multi-user report](docs/reviews/2026-10-24-mehrbenutzer-pruefbericht.md); notably configure `AUTH_MODE=oidc` explicitly for production.

## Database: SQLite Locally, PostgreSQL in Production

- **Local, no Docker:** `DB_PROVIDER=auto` (default), no `POSTGRES_HOST` → SQLite `data/learncards.db`.
- **Docker/Linux host:** `docker-compose.yml` selects `DB_PROVIDER=postgres` and starts PostgreSQL.
- **Azure Web App:** External PostgreSQL (e.g., Azure Database for PostgreSQL Flexible Server), `DB_PROVIDER=postgres`, `POSTGRES_SSLMODE=require`; see [Azure Deployment](docs/azure-deployment.en.md).

Both providers are custom implementations with no ORM/NuGet dependency and create/update schema on startup.

## Quick Start — Local Development

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
cp .env.example .env     # Set your values or use an already supplied local .env
cd src/LearnCards.Web
dotnet run
```

Open `http://localhost:5000`. Without complete Entra ID configuration, `AUTH_MODE=auto` in Development uses development login; the development user is an **administrator** and this must never be exposed in production (`AUTH_MODE=oidc`). Without `OPENAI_API_KEY`, chat/quizzes offer a clearly labeled offline fallback.

Alternatively use VS Code debug profiles **App (SQLite)** or **App (PostgreSQL Docker)** from `.vscode/launch.json` (latter uses `docker-compose.dev.yml`; see [Local Development](docs/local-development.en.md)).

Start the MCP server separately for Claude Desktop:

```bash
cd src/LearnCards.McpServer
dotnet run
```

> **Multi-user (ADRs 0011/0013):** MCP clients act only in the user's account. HTTP uses **OAuth** with Learny login/consent; stdio uses a **personal token** via `LEARNCARDS_MCP_TOKEN` generated at `/profile`. Shared `MCP_API_KEY` serves only server-to-server code exchange and cannot access data. Revoke connections from profile. On initial multi-user upgrade configure `ADMIN_SUBJECTS` and assign unowned legacy modules at `/admin`.

For stdio in IIS deployments, the pipeline creates `learncards-mcp.cmd` and `claude-desktop.learncards.json` so the MCP client can start the process. Independent HTTP-mode settings live in `src/LearnCards.McpServer/mcpsettings.example.json`, copied as `mcpsettings.json` next to the executable. The server supports stdio, HTTP `/mcp`, OAuth endpoints `/.well-known/oauth-authorization-server`, `/oauth/authorize`, `/oauth/register`, `/oauth/token` (Learny login, per-user PKCE), and schema at `/metadata` or MCP `get_import_schema`.

Production-like IIS/Windows example: `src/LearnCards.McpServer/mcpsettings.iis.example.json` uses `upstreamApi.baseUrl=http://localhost/api`, disables stdio, enables HTTP at `http://127.0.0.1:8787`, enables OAuth; `oauth.issuer` must equal public MCP URL and Web needs matching `MCP_PUBLIC_URL` (ADR 0013).

Repository deployment templates under `deploy/iis/` include `deployment.json` (nonsecret values), `web.env` (Web settings), `mcpsettings.json` (MCP HTTP/OAuth). GitHub Variables and Secrets provide live values.

## Docker — Prebuilt Images from Docker Hub

Install without source code via `joergb23/learny-web` and `joergb23/learny-mcp` (linux/amd64, arm64), using [deploy/docker/](deploy/docker/README.en.md) with Compose, `.env.example`, and variables (also `learny-docker-<version>.zip` on GitHub Releases). See [ADR 0024](docs/adr/0024-docker-hub-veroeffentlichung.en.md).

## Docker — Build from Source (Linux)

```bash
docker compose up --build -d                        # db + web
docker compose --profile production up --build -d   # add nginx TLS
docker compose --profile mcp run --rm mcp-server     # interactive MCP stdio
```

Web runs at `http://<server>:8080`, or `https://<server>` with nginx; put certificates at `nginx/certs/cert.pem` and `key.pem`.

## Azure Web App

Deploy unchanged as Azure App Service Linux (code or container) with an **external** PostgreSQL database. See [Azure guide](docs/azure-deployment.en.md).

## Local URLs

- Application/API: `http://localhost:5000`
- Health: `http://localhost:5000/health`

## Card JSON

Imports accept an array of cards or `{"cards":[...]}`. For exam-oriented cards, optional fields include `reference_answer`, `official_sources`, `slide_number`, `target_time_sec`, and static `quiz[]`:

```json
{
  "cards": [
    {
      "module": "Azure Container",
      "category": "Container",
      "term": ":latest issue / SHA digest pinning",
      "question": "Why is :latest dangerous in production, and what is safer?",
      "definition": "Moving tags such as :latest are not reproducible.",
      "how_it_works": "A digest pins the exact published image.",
      "context": "Important for CI/CD, rollback, and incident analysis.",
      "key_fact": "Deployments should use immutable digests.",
      "reference_answer": "A moving tag such as :latest can change its underlying contents. Pin the image by SHA digest for reproducible, traceable and secure deployments.",
      "slide_number": 3,
      "target_time_sec": 90,
      "quiz": [
        {
          "type": "single_choice",
          "question": "Why is an SHA digest more stable in CI/CD than :latest?",
          "options": [
            "Because :latest only works locally",
            "Because the digest references identical image content",
            "Because digests are automatically patched",
            "Because digests need no registry"
          ],
          "correct_index": 1,
          "explanation": "A digest identifies an immutable artifact and makes deployment reproducible."
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

## Images in Cards

Optional up to five PNG/JPEG/WebP images, each no larger than 2 MiB decoded, embedded as Base64 in `images` and rendered on cards without an AI key or required external storage:

```json
"images": [
  {
    "mime_type": "image/png",
    "data_base64": "iVBORw0KGgoAAAANSUhEUgAAACAAAAAQCAIAAAD4YuoOAAAAI0lEQVR42mPg94mhKWIAYq3ls2mERi0YtWDUglELRpIFNEUAOleXE3NVou0AAAAASUVORK5CYII=",
    "alt_text": "Colored sample area (32 × 16 pixels)",
    "caption": "Sample image"
  }
]
```

`data_base64` must contain standard Base64 **without** `data:` prefix (PowerShell: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("image.png"))`). Omitted `images` means no images initially, or retains existing images on overwrite; `"images":[]` removes them. Invalid images reject their card with an explanation. Limits and AI guidelines: Help and [ADR 0016](docs/adr/0016-bilder-in-lernkarten.en.md).

### Formatted Text

`definition`, `how_it_works`, `context`, `key_fact`, `reference_answer` support safe limited Markdown-like formatting (**never HTML**): `**bold**`, `*italic*`, `++underline++`, `# heading`, `- list`, `1. numbered`, `[text](https://…)`; blank line separates paragraphs. Plain text stays valid. Live schema `rich_text_rules` and Help explain it; only server-safe rendering is used ([ADR 0020](docs/adr/0020-sichere-visuelle-bearbeitung-formatierter-lerninhalte.en.md)). The browser editor is under module page → **Edit Cards** (owned modules only).

### Quiz Questions (`quiz[]`)

Optional; missing or `[]` means no fixed questions. Older `single_choice` files without `type` remain valid. Each entry requires `question` and `explanation`.

| `type` | Fields | Grading |
|---|---|---|
| `single_choice` (default) | Exactly four `options`, zero-based `correct_index` | One correct option |
| `multiple_response` | 3–6 options, distinct zero-based `correct_indexes` | Exact set only |
| `free_text` | No options, `explanation` as model answer | AI or local heuristic |

```json
"quiz": [
  { "type": "single_choice", "question": "Which answer is correct?", "options": ["A", "B", "C", "D"], "correct_index": 1, "explanation": "B is correct." },
  { "type": "multiple_response", "question": "Which are prime numbers?", "options": ["2", "3", "4", "6"], "correct_indexes": [0, 1], "explanation": "2 and 3." },
  { "type": "free_text", "question": "Explain this term.", "explanation": "Model answer." }
]
```

`quiz[]` holds **fixed** questions. AI-generated and editor-generated questions live in the module question pool ([ADR 0019](docs/adr/0019-einheitliches-quizsystem.en.md)), outside import JSON. Fetch the exact current schema via `GET /api/schema/import/cards` or MCP `get_import_schema`. Templates are on the Help page.

## Demo Files

`demoodule/demo.json` provides a compact PRINCE2 project-management dataset with sample images and all three quiz types. Import directly from the home page. Downloadable minimal, full, slide, and image templates are under `src/LearnCards.Web/wwwroot/templates/` and linked from Help.

## Documentation

- [Architecture](docs/architecture.en.md)
- [ADRs](docs/adr/README.en.md)
- [Developing with AI Assistants](docs/ai-development.en.md) · [AGENTS](AGENTS.en.md)
- [Local Development, VS Code, PostgreSQL, MCP](docs/local-development.en.md)
- [Database Schema](docs/database-schema.md)
- [Tests](docs/testing.en.md)
- [Multi-user Review Report](docs/reviews/2026-10-24-mehrbenutzer-pruefbericht.md)
- [GPT Project Prompt](docs/gpt-project-prompt.en.md)
- [Azure Deployment](docs/azure-deployment.en.md)
- [IIS Deployment via GitHub Actions](docs/iis-deployment.md)
- [Migration from Python](docs/migration-notes.en.md)

## Design

A custom learning-focused palette (muted teal/sage brand color, warm off-white, subtle amber accent for “Key Facts”), with full dark mode; see `src/LearnCards.Web/wwwroot/app.css`.

## MCP Tokens and Key (ADR 0025)

`MCP_TOKEN_KEY` is **mandatory** (Base64 ≥32 bytes, generate with `openssl rand -base64 32`, store in `.env`/secret manager only). It protects MCP tokens, one-use codes, and persisted OAuth clients in the DB via HMAC, row MAC, and AES-256-GCM. Database-only attackers cannot obtain, forge, or tamper with credentials. See [ADR 0025](docs/adr/0025-sichere-speicherung-von-mcp-tokens.en.md).

## Topics and Learning Paths

Under **Topics** (`/topics`), group learning modules into a path, configure module/final exams, publish immutable versions, share/assign to learners, and submit global topics for admin approval ([ADR 0027](docs/adr/0027-themen-lernpfade-versionierung-und-abschlusspruefungen.en.md)). MCP clients can read topic lists/details/exam structure and manage their own drafts through JSON (ADR 0028).

### Modules and Topics as JSON, Cloning, MCP

- **Modules** (`/`) contain flashcards/quizzes; **Topics** (`/topics`) organize learning paths from **owned** modules. Clone global modules into owned private copies before adding to topics.
- `learny-module` JSON exports/imports cards, images, quizzes, config, and pool questions. `learny-topic` JSON contains stable `module_ref` plus display name, order, exams, topic questions, optionally embedded module definitions. Imports create private modules/drafts, **never publish**. Schemas: `GET /api/schema/import/module` and `/api/schema/import/topic`; templates `/templates/module-template.json`, `/templates/topic-template.json`.
- **Cards via MCP:** `get_card` returns full details, `quiz[]`, Base64 images. `update_card` edits **owned private** cards only; fetch `get_import_schema`/`get_card` first; omitted `images` retains, `images=[]` removes; `expected_updated_at` controls conflict detection. `export_cards` batches a module's canonical `{"cards":[…]}` JSON (default 50, max 200 cards or ~4 MiB image data per call) using `next_offset` until `has_more=false`, directly importable with `import_cards`. REST: `GET/PUT /api/cards/{id}`, `GET /api/modules/{id}/export/cards`.
- MCP tools include `get_topic_import_schema`, `create_topic`, `import_topic`, `update_topic`, `export_topic`, `get_module_import_schema`, `export_module`, `import_module`, `clone_global_module`, and read-only `list_topics`, `get_topic`, `get_topic_exam_structure`. Only own content; no publication, shares, assignments, or administration. See [ADR 0028](docs/adr/0028-themenmodule-json-und-mcp-verwaltung.en.md).

### Sharing, Mandatory Topics, and Topic Administration

- **Global** = visible/optional for everyone, **shared** = explicitly allowed recipients, **assigned** = specific version by admin/topic admin, **mandatory** = a flagged assignment. Only explicit mandatory assignments appear as “My mandatory topics” on home.
- Learners with topic access can open all version modules and exams; original private modules remain inaccessible.
- **Topic Admin** (`themen_admin`, granted by admins) can assign topics and view progress under `/topic-admin`; auditors can read progress. See [ADR 0029](docs/adr/0029-themenzugriff-pflichtzuweisungen-und-themenadministration.en.md).
- IIS deployment restarts and validates MCP HTTP service after file replacement; see [IIS deployment](docs/iis-deployment.md).
