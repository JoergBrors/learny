# Tests

Tests require no external NuGet packages ([ADR 0002](adr/0002-keine-nuget-pakete.en.md)). A console application launches `LearnCards.Web` in a separate process with a temporary database and tests its behavior over HTTP.

## Integration Tests (Multi-user, Roles, Data Isolation)

```bash
dotnet build LearnCards.slnx
dotnet run --project tests/LearnCards.IntegrationTests --no-build
```

Tests also start the MCP server (HTTP with OAuth). Override DLL locations through `TEST_WEB_DLL`/`TEST_MCP_DLL`, e.g., when a debugger locks the default output. The web app uses a free local port, development authentication, temporary SQLite and Data Protection keys. Four scenarios are used: administrator `local-dev-user`, users `alice` and `bob`, and Alice with the auditor role.

Coverage includes:

- Unauthorized requests return 401; every identity has a distinct internal ID.
- Bob cannot view, modify, archive, delete, quiz, or chat using Alice's content (404 or empty results).
- Manipulated IDs and SQL fragments have no effect.
- Different users may have same-named modules; importing a foreign card ID never overwrites it.
- Learning results are isolated per user and cannot be deleted by others.
- Role security: no self-promotion, auditor lacks admin rights, last admin protection, audit log.
- A shared `X-MCP-Key` never grants user data access, client-supplied user IDs do not change identity, and key-based admin API access is denied.
- Disabled accounts immediately lose access.
- **Card images (ADR 0016):** Import and round-trip valid PNGs unchanged; multiple images; missing/empty `images`; reject excessive count, malformed Base64, `data:` prefix, wrong/unsupported MIME type, nonimage content, images over 2 MiB (exactly 2 MiB accepted), overly long alt text. Verify update semantics (omitted = retain; `[]` = remove, including PUT), batch-import errors, live schema/example, `demoodule/demo.json`, images in shared/global copies, and MCP `add_card`, `import_cards`, `get_import_schema`.
- **MCP card tools (`CardToolsTests.cs`):** `get_card` (all fields, quiz, Base64 images, no storage keys; owned/shared/global readable; foreign 404); `update_card` (text, sources, quizzes, order, image omission/IDs/replacement/deletion/invalid input, 409 conflicts, shared/global 403, foreign 404); `export_cards` (active/archived, stable ordering, batching large modules, round-trip export → import → identical export including images/quizzes, overwrite by ID), tool listing/schema, missing/invalid tokens, service key without data access.
- **Image storage, proxy, SAS (ADRs 0017–0018):** Serverless component tests: local provider path security, atomic operations, permissions; database provider; configuration validation; LRU/file/TTL/integrity caching; official AWS **SigV4 vector**; SSRF address checks; secret redaction; `StorageUrlGuard` rejection cases; SAS `sp=r`/`sr=b`/TTL/blob path, presigned URLs, rejecting altered storage keys/foreign broker URLs, no redirects, size limits, unavailable storage; SharePoint using a Graph mock with ETag revalidation, foreign download URLs, token reuse, HTTP 429. Identical end-to-end `ImageScenario` per provider: store, byte-identical fetch, ETag/304, no redirect, other user 404, anonymous 401, replace/remove/delete and storage cleanup, no SAS/cloud URLs returned. Run with database, local, **Azure Blob/Azurite** (skipped if `127.0.0.1:10000` unavailable; logs must not contain `sig=` or account key), and SharePoint Graph mock (15 parallel cache hits make no Graph/token calls). Also verify database → local migration (dry-run, cursor batches, idempotence, audit, admin-only, 400 for unavailable target) and `IMAGE_PROXY_ENABLED=false` → 503. Not automated: real Azure user-delegation SAS, real S3, real Microsoft Graph.
- **Quiz engine (ADR 0019):** Default config and invalid-combination validation (400); pool projection of card `quiz[]` (source of truth, updates, duplicates); card/module quizzes use same grading; single choice, multiple response (exact set), free text; start without solutions, one-time submit (409), foreign session (404); choice disabled, choice-only failures instead of silently changing types, limits, scope, thresholds, hiding answers, disabling retry/quiz, deactivating questions without erasing results; authorization (foreign 404, sharing recipient cannot edit pool/config but may take quiz); frozen global configs/questions (even admins 403); old sessionless path; saved result session/passed/threshold/module version; idempotent startup pool migration from `quiz[]`. With **OpenAI mock** (`MockOpenAi`, `OPENAI_BASE_URL`), validate generation (reject bad questions, persist/reuse), strategies `none`/`on_demand`/`prefer_stored`/`mix`, origins `fixed`/`ai`, ephemeral questions for learners, AI free-text grading and fallback on AI errors. Actual OpenAI responses cannot be verified automatically.
- **Editor API, examples, templates, schema, MCP, Help (ADR 0019):** Create/edit/associate/activate/deactivate questions with validation/permissions/duplicate prevention; `card_json` questions only edited through cards; editor questions survive card saves. Validate **all** files in `wwwroot/templates/` and `demoodule/` and the live schema example: only declared fields, valid quiz/image structures (indexes, counts, limits), real import and unchanged quiz questions, legacy formats without `quiz` or `type`. Live schema lists all three question types; MCP `add_card` input schema/tool descriptions match. Help links precisely to available templates; downloads contain the expected files, required terminology appears, future features are not falsely advertised. Sessions expire (410) and are cleaned after 24 hours without removing results; active sessions survive restarts.
- **Certificates, Merkle, cost ceiling, anchoring (ADRs 0021–0022; `BlockchainUnitTests`):** Offline controllable fake chain: Merkle trees (1–100 leaves, deterministic results, tampering and domain separation), config defaults and invalid >EUR 0.001 limits, never implicit mainnet; cost guard allows cheap and blocks expensive (>EUR 0.001), distributes batch costs, rejects stale/missing prices/unknown gas, applies safety margins and transaction fee bounds. Verify badge structures/signatures/tampering/`alg:none`/foreign issuers/idempotent issuance/revocation/privacy; pending/batching/one transaction/finality/no duplicate anchors under concurrency/restart/revocation/network switch/blockchain off. **On mainnet, even six retries must never send when cost per credential exceeds EUR 0.001**, and no transaction may be sent with unknown cost, stale/missing quote, wrong chain ID, low balance, absent approval/signer. Test RPC failures, dropped transactions, receipt status 0, empty faucet; auditor global-only and admins without private content. Real Amoy/mainnet transactions and related UI are not automated (signing solution pending).
- **Card editor and rich text (ADR 0020):** `RichText` component tests cover formatting/escaping/plain-text compatibility and XSS vectors: scripts, `img/onerror`, SVG, iframes, styles, event handlers, forbidden URL schemes (`javascript:`, `data:`, protocol-relative, FTP, mailto, relative, file), credentials/quotes in links, markup embedded in URLs, placeholder characters, pathological inputs, and length limits. Server tests cover POST/PUT bounds, markup round-trip, displaying attacks as plain text, image retention/metadata/order/limits/foreign `image_id`, duplication (images, `quiz[]`, editor questions), ordering, archive/restore, conflict detection (`X-Card-Updated-At`, 409), permissions (recipients/foreign users/admins/global), owner-only editor, formatting templates, live `rich_text_rules`, MCP descriptions.
- **Editor browser test:** `node tests/browser/editor-browser-check.js <output-directory> <LearnCards.Web.dll> <Web-project-directory>` (Node 22+, Chrome/Edge). Checks card creation; bold/lists/links including `javascript:` rejection; movable/resizable persistent preview and reset/mobile bottom positioning; image upload/alt/caption; static multiple-response quiz; saving; pool editor questions and active state; duplicates/archive/order; unsaved changes across card/page navigation and `beforeunload`; conflicts; malicious text; mobile 390 px, dark mode, JavaScript errors.
- **Quiz browser test:** `node tests/browser/quiz-browser-check.js <output-directory> <LearnCards.Web.dll> <Web-project-directory>` (Node 22+, Chrome/Edge built-ins). Launches a development-mode SQLite app, creates test data, uses Chrome DevTools Protocol to verify desktop/mobile (390 px), light/dark mode, configuration auto-disabling/errors/saves, single choice/multiple response/free text for card/module, retries, disabled quiz, JS errors. Saves screenshots.
- **Sharing and global modules (ADR 0015, scenarios 1–11):** Private stays private; owner-only sharing with active recipients, no self-sharing; recipients can read/study/quiz but not mutate; results belong to learners; only admins submit-review/publish/reject as specified; global versions remain stable when originals change, updates create versions, deactivate/reactivate; admins and auditors see minimized audited global results, others cannot; revocation preserves results; HTTP 409 deletion protection; MCP access is limited even for admin tokens, manipulated IDs return 404.
- **MCP links and OAuth:** Personal tokens limited to own data and data routes, revocation and invalid/foreign tokens; consent uses allowlisted redirects, one-use codes exchanged only with the service key. Real MCP HTTP OAuth end-to-end: reject insecure registration redirects, authorize → Learny consent → CSRF-protected callback → token with PKCE/single-use code, per-user `whoami`, `list_modules`, `list_cards`, `add_card`, `archive_card`, immediate 401 after revoke/disable.
- **IdP roles:** Simulated `Admin.All` claim grants admin, removing it on later login revokes IdP-granted admin, unmapped values ignored, manual roles retained.

Exit code 0 indicates all checks pass (historical count: **273 checks**; PostgreSQL previously failed one until review finding F4 was fixed). Failed tests output server logs.

### Run against PostgreSQL

```bash
docker run -d --rm --name lc-it-pg -p 55432:5432 -e POSTGRES_PASSWORD=itpass -e POSTGRES_USER=learncards -e POSTGRES_DB=learncards_it postgres:16-alpine

TEST_DB_PROVIDER=postgres POSTGRES_HOST=127.0.0.1 POSTGRES_PORT=55432 POSTGRES_USER=learncards \
POSTGRES_PASSWORD=itpass POSTGRES_DB=learncards_it POSTGRES_SSLMODE=disable \
dotnet run --project tests/LearnCards.IntegrationTests --no-build

docker stop lc-it-pg
```

Tests create users/modules; **never** run against production. In PowerShell set variables using `$env:NAME = "..."`.

## Manually Verify Legacy Database Migration

1. Check out the pre-ADR-0011 version (`git worktree add <path> <commit>`), build and run using SQLite/PostgreSQL. Import cards with development login, change card state/theme, and submit a quiz.
2. Start the new application version against **the same database** (first login as development admin).
3. Expect `/api/me` to show zero modules; `/api/admin/unowned-modules` lists the legacy module. Following `POST /api/admin/modules/{id}/owner`, cards, card state, theme, and quiz history reappear. A second restart makes no further changes.
4. For SQLite on Windows see below.

## Windows / SQLite Note

On Windows, the application loads system `winsqlite3.dll` ahead of `sqlite3.dll`. A foreign incompatible `sqlite3.dll` in the `PATH` (e.g., installed by AWS CLI) previously caused startup crash 0xC0000005 when the order was reversed.

## Not Automated

- Blazor pages `/`, `/profile`, `/admin`, `/module/{id}` were originally checked only by HTTP rendering (page loads, content, non-admin redirected from admin). Interactive actions such as saving display name, role buttons, assignments should be tested manually: `dotnet run` from `src/LearnCards.Web`, login, inspect `/profile` and `/admin`.
- Actual Entra ID OIDC login (development login only tested).
- Load testing and simultaneous interactive browser sessions. See the [review report](reviews/2026-10-24-mehrbenutzer-pruefbericht.md); its personal-data, concurrency, logout/account-switching, and key-matrix checks are covered in the “review report” integration-test section.
- Known historical PostgreSQL failure: concurrent first save of learning status could return HTTP 500 (finding F4); expected until corrected.

## Docker Configuration (ADR 0024)

`DockerConfigTests` ensure that `deploy/docker/docker-compose.yml`, `.env.example`, README environment-variable table, and code agree, that templates contain no secrets, defaults are safe (`AUTH_MODE=oidc`, no published PostgreSQL port, localhost binding, named volumes), and workflow Actions are pinned to SHA and use repository secrets.

## MCP Token Protection (ADR 0025)

`McpTokenUnitTests` (M1–M44) use direct database access to test forged rows/codes, altered `user_id`/expiration/revocation, exchanged hashes, tampered/copied OAuth clients, key rotation/loss, legacy migration/deadline, and mandatory `MCP_TOKEN_KEY`. The end-to-end test restarts MCP and verifies persisted registrations survive. Integration tests set their own test value for `MCP_TOKEN_KEY`.

## Topics (ADR 0027)

`TopicE2ETests` (T1–T80) run in integration tests: creation/order/many-to-many references, restrictions on foreign modules, strictly increasing versions/concurrent publishing/no new version without changes/hash reuse/progress carry-over, shares/assignments/revocation, isolation, admin global approval/rejection/withdrawal, exam configuration/execution (distribution, too-small pool, retries, unlocking, offline), progress, audit, read-only MCP token limits. Browser: `node tests/browser/topics-browser-check.js <output-directory> <LearnCards.Web.dll> <Web-project-directory>` (Chrome/Edge overview, details, exams, editor, publication, desktop/mobile, light/dark, focus).

## Topic Modules, JSON, and MCP (ADR 0028)

`TopicTransferTests` (T81–T132; run at end because they publish a global module): own-modules-only topic enforcement (global, shared, foreign, fabricated IDs), cloning card/image bytes/quiz/name collisions without modifying originals and only from active global, module JSON round-trip across users (images, pool, config, no internal storage data, eight rollback errors), topic JSON export without IDs/import as draft/unknown global foreign or embedded modules/nine rollback errors, update drafts without publishing (old frozen version unchanged, new version only after publish), MCP schema/create/update/export/import/clone/module transfer and blocked actions, service key without data access, audit. Template/schema tests also import `module-template.json`, `topic-template.json` and schema examples.

### Transfer Final Verification (ADR 0028)

- `TransferIntegrityUnitTests` (I1–I23, in-process SQLite): `FaultDb` injects a one-time failure at the n-th write during module/topic import/update. Verify module references (12 concurrent allocations, never overwritten, foreign modules, owner-specific unique indexes, concurrent imports, reuse without overwriting, cross-account cases, occupied references, invalid/duplicate GUIDs). Legacy draft with global module fixture: show warning, block publication, replace with owned clone and independent reference, keep original/published version unchanged, publish version 2.
- `TransferHardeningTests` (H1–H17, HTTP/MCP): concurrent same-reference imports, owner scoping, large payloads (27 MB module/16 images, 30 MB limit/413, image/card limits), access matrix for all nine MCP tools (missing/invalid token, foreign user, manipulated fields, service key without access, MCP updates never publish).
- `modules.reference_id` migration: SQLite main tests drop column/index then start twice and verify stable references and unique index. PostgreSQL was manually tested by removing column/index and restarting twice on the existing database.
- Browser `tests/browser/topics-browser-check.js` (65 checks) adds module/topic JSON upload, reference errors, name rendering, Help links, templates.

## Topic Sharing, Mandatory Assignments, Topic Admin, Progress (ADR 0029)

- `TopicGovernanceTests` (G1–G40, HTTP, at run end): `themen_admin` role admin-only grant and immediate revocation, personal sharing of all version modules including images/exams while preserving original-module privacy and independent shares, global topic visible to all and optional, withdrawal, assignment role/version/deadline/mandatory-vs-optional and dashboard appearance, progress tracking (auditor/topic admin/admin, minimized fields, filters), module usage, MCP restrictions, audit.
- `TransferIntegrityUnitTests` I24–I26: assign active global version, overdue only when mandatory and unfinished, service role enforcement.
- `WorkflowStructureTests` (W1–W9): static IIS workflow validation (stop service/task before file replacement, wait; restart afterward; never start nonexistent service or stdio mode; health retry; fail on startup errors; no secrets). No actual Windows IIS runner execution.
- Browser topic checks include mandatory home section, Start/Continue, topic module page, role-visible assignment management, editor without assignments, and module usage.
