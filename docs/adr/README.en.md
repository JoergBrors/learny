# Architecture Decision Records (ADRs)

Every ADR documents **one** architecture-shaping decision for LearnCards: context, decision, consequences, and **guardrails for changes**. These records are the authoritative explanation of *how and why* the system works, for developers and AI assistants alike (see [AGENTS.md](../../AGENTS.md)).

## Index

| No. | Title | Status |
|---|---|---|
| [0001](0001-adr-verwenden.en.md) | Document Architecture Decisions as ADRs | Accepted |
| [0002](0002-keine-nuget-pakete.en.md) | No External NuGet Packages | Accepted |
| [0003](0003-eine-deploybare-einheit.en.md) | Blazor Server and REST API in One Unit | Accepted |
| [0004](0004-datenbank-abstraktion.en.md) | IDatabase: SQLite Locally, PostgreSQL in Production | Accepted |
| [0005](0005-authentifizierung.en.md) | Authentication: OIDC, Development Login, X-MCP-Key | Accepted |
| [0006](0006-kanonisches-karten-json.en.md) | Canonical Card JSON (snake_case) and Live Schema | Accepted |
| [0007](0007-mcp-server.en.md) | Standalone MCP Server as an API Client | Accepted |
| [0008](0008-ki-integration-und-fallback.en.md) | AI Integration with Offline Fallback and Token Budget | Accepted |
| [0009](0009-deployment-wege.en.md) | Deployment: IIS by Tag, Docker, Azure | Accepted |
| [0010](0010-folienmodus-und-karten-quiz.en.md) | Slide Mode and Static Card Quizzes | Accepted (quiz logic partly replaced by 0019) |
| [0011](0011-mehrbenutzerarchitektur.en.md) | Multi-user Architecture: Identity, Ownership, Roles, Migration | Accepted; supplemented by 0029 |
| [0012](0012-rollen-aus-entra-app-roles.en.md) | Roles from Entra ID App Roles | Accepted; supplemented by 0029 |
| [0013](0013-mcp-benutzergebundene-anmeldung.en.md) | MCP: User-bound OAuth Login and Personal Tokens | Accepted |
| [0014](0014-sitzungs-und-betriebsmodus-haertung.en.md) | Session and Operating-mode Hardening | **Proposed** (not fully implemented) |
| [0015](0015-freigaben-globale-lernmodule.en.md) | User Sharing, Global Learning Modules, and Results | Accepted; supplemented by 0029 |
| [0016](0016-bilder-in-lernkarten.en.md) | Images in Flashcards (Base64) | Accepted |
| [0017](0017-konfigurierbare-bildspeicher.en.md) | Configurable Image Storage, Cache, and Migration | Accepted |
| [0018](0018-zentraler-image-proxy-und-storage-zugriff.en.md) | Central Image Proxy and SAS Broker | Accepted |
| [0019](0019-einheitliches-quizsystem.en.md) | Unified Quiz Engine, Configuration and Persistent Question Pool | Accepted |
| [0020](0020-sichere-visuelle-bearbeitung-formatierter-lerninhalte.en.md) | Safe Visual Editing of Formatted Learning Content | Accepted |
| [0021](0021-zertifikate-und-open-badges.en.md) | Certificates and Signed Open Badges 3.0 | Accepted (UI partly pending) |
| [0022](0022-polygon-verankerung-und-kostenschranke.en.md) | Polygon Anchoring, Merkle Batching, Hard Cost Ceiling | Accepted (signing solution pending) |
| [0023](0023-secrets-und-badge-schluessel-unter-iis.en.md) | Secrets and Badge Keys under IIS | Accepted (IIS test pending) |
| [0024](0024-docker-hub-veroeffentlichung.en.md) | Publishing Docker Hub Images with Compose Installation | Accepted (first release pending) |
| [0025](0025-sichere-speicherung-von-mcp-tokens.en.md) | Secure Storage of MCP Tokens, Codes, and OAuth Clients | Accepted and implemented |
| [0026](0026-betriebsmodus-fail-closed.en.md) | Fail-closed Operating Mode; Mandatory ENV_FILE | Accepted |
| [0027](0027-themen-lernpfade-versionierung-und-abschlusspruefungen.en.md) | Topics, Learning Paths, Versioning, and Final Exams | Partially superseded by 0028 and 0029 |
| [0028](0028-themenmodule-json-und-mcp-verwaltung.en.md) | Owned Topic Modules, Portable JSON, and MCP Management | Accepted |
| [0029](0029-themenzugriff-pflichtzuweisungen-und-themenadministration.en.md) | Topic Access, Mandatory Assignments, Delegated Topic Admins | Accepted |

Review reports motivating ADRs are under [docs/reviews/](../reviews/), currently including the [multi-user review](../reviews/2026-10-24-mehrbenutzer-pruefbericht.md).

## Creating an ADR

1. Assign the next number and copy [template.en.md](template.en.md) to `NNNN-short-title.en.md` (and prepare the corresponding original-language document).
2. Update the index above.
3. **Do not rewrite historical decisions.** When a decision changes, write a new ADR and mark the previous one as `Superseded by NNNN`. Clarifications and typo corrections are acceptable.
