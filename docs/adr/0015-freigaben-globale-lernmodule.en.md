# 0015 — User Sharing, Global Learning Modules, and Result Attribution

- **Status:** Accepted (supplemented by [0029](0029-themenzugriff-pflichtzuweisungen-und-themenadministration.en.md): topic-specific module permissions and mandatory assignments)
- **Date:** 2026-10-24
- **Builds on:** [0011](0011-mehrbenutzerarchitektur.en.md) (ownership), [0012](0012-rollen-aus-entra-app-roles.en.md) (roles), [0013](0013-mcp-benutzergebundene-anmeldung.en.md) (MCP)
- **Clarifies:** “There is no shared card collection.” Private collections remain isolated, but explicitly managed **shared** and **global** areas are also available.
- **Supplemented by:** [0016](0016-bilder-in-lernkarten.en.md) (images are copied into shared/global copies)
- **Originally open:** Session/operating-mode hardening ([0014](0014-sitzungs-und-betriebsmodus-haertung.en.md), proposed); findings F1–F4 of the [review report](../reviews/2026-10-24-mehrbenutzer-pruefbericht.en.md) were still open when this ADR was written. Consult later ADR 0026 for partial mitigation.

## Context

After ADR 0011, modules and cards were exclusively private. Users need to share entire modules with selected learners, while administrators must be able to publish reviewed modules for everyone without weakening ownership filters, disclosing private results, or deleting historical results.

## Decision

### Three Scopes

| Scope | Content owner | Who can learn? | Who can modify? |
|---|---|---|---|
| **PRIVATE** | One user (`owner_user_id`) | Owner only | Owner only |
| **SHARED** | Original owner (same module, no copy) | Owner and active recipients | Owner only |
| **GLOBAL** | No owner (`owner_user_id IS NULL`; provenance snapshot) | All users, while the version is active | **Nobody** through user/MCP paths; administrators manage activation, deactivation, and replacement |

Repository mutation methods (`UpdateCardAsync`, `SetArchivedAsync`, `DeleteCardAsync`, `DeleteModuleAsync`, `CreateCardAsync`, etc.) retain their **ownership filters**. All reads of others' content use a **single authorization decision**: `CardRepository.ResolveModuleAccessAsync(userId, moduleId)` → (`private` | `shared` | `global`, `CanEdit`), or “not found”. `ListReadableCardsAsync`, `GetReadableCardAsync`, quiz, chat, and learning-status flows use it.

### Personal Sharing (Entire Module)

- Table `module_shares` stores module, recipient, sharer, timestamp, and `revoked_at`; module+recipient is unique. Sharing always covers the **entire module**, including active cards and static quizzes; per-card sharing is deliberately excluded.
- Only the **owner** may grant/revoke sharing (checked server-side, never by trusting supplied owner IDs). Recipients must be active users, self-sharing is forbidden, and recipients are found through search (at least 3 characters, active users only, name and email only).
- Recipients may read, study, take quizzes, update their learning status, and chat; they may **not** modify, archive, delete, overwrite, reshare, or submit content. Only the owner sees archived cards.
- Shares convey **no personal data**: learning status, chat histories, and results remain scoped to each learner (`user_sub`); owners cannot view a recipient's records.
- **Revocation** sets `revoked_at` and immediately removes access. Previous recipient results **remain** in their history. Sharing again reactivates the entry.

### Submission and Global Publication

- An owner **submits a module** (`module_submissions`: pending/published/rejected). Submission immediately creates an immutable **staged snapshot** with new IDs (`modules.scope = 'global'`, `global_status = 'staged'`; active cards and static quizzes included). Later edits to the original do not affect the submission.
- Administrators see **only the staged copy** (preview), never the private original; the admin role confers **no read access to others' private content**.
- Only an **administrator** (server-side `RequireRoleAsync`) can **publish or reject**. Decisions are claimed atomically so two admins cannot process the same submission twice. Rejection discards the staged copy while leaving the private/shared original unchanged. Publication cannot be triggered through an arbitrary request field.
- Provenance and author are saved as a **snapshot** (`source_module_id`, `source_owner_user_id`, `source_owner_name`, `published_at`, `published_by`).

### Global Publication Versions

- A **publication** has a stable `publication_id`; each published version occupies its **own row in `modules`** with a distinct module ID, `global_version`, and `global_status`: `staged` (submitted) → `active` → `superseded` (replaced) or `deactivated` (withdrawn). The original private module ID is **never** reused as a global identity.
- Only the **active** version can be studied. An **update** is another owner submission, creating a new version under the same `publication_id`; approval sets the previous version to `superseded`. Until then, the global copy stays unchanged even if the original changes.
- **Withdrawal:** An administrator deactivates a version or reactivates the latest deactivated version. Under the original decision nothing is deleted; results and old versions remain (see deletion addendum below).
- Users cannot edit, delete, share, or resubmit global modules.

### Learning Results

- `quiz_results` gains `context` (`private` | `shared` | `global`) and continues to reference a **specific module ID**—for global modules, the **specific version**. Foreign keys and quiz/slide logic remain unchanged.
- Results **always belong to the learner** (`user_sub`). An owner cannot see recipients' results on shared modules.
- Every learner can see their own history across all contexts and cannot see anyone else's personal history.
- For **global results only** (`context = 'global'`), administrators and auditors may see (a) per-version aggregates (attempts, learners, average) and (b) a detail list with learner name, category, score, grade, and timestamp—**without answers or feedback text**. Each detail access is audited (`publication.results.read`). Chats and learning status remain private and are not considered “learning results” for this ADR.
- **Protect existing results:** Because results reference modules/cards through foreign keys, deleting a module or card returns HTTP 409 whenever **other users** have results associated with it; archiving is still allowed. The original policy disallowed deleting published versions (only `superseded`/`deactivated`); see the 2026-10-25 addendum for an admin-only permanent deletion option.

### Roles and Permissions

| Action | `user` | `admin` | `auditor` |
|---|:---:|:---:|:---:|
| Edit/share/revoke/submit own modules | Yes | Yes | Yes |
| Study shared/global modules, including quizzes | Yes | Yes | Yes |
| View own results | Yes | Yes | Yes |
| Read someone else's private/personal content and results | No | No | No |
| View submissions and staged-copy previews | No | Yes | No |
| Publish/reject/deactivate/reactivate | No | Yes | No |
| View all global module versions and statuses | No | Yes | Yes (read-only) |
| View global results (aggregates and detail) | No | Yes | Yes (read-only) |
| Read publication audit (`publication.*`, `submission.*`) | No | Yes (full audit) | Yes (only these actions) |
| Manage users/roles | No | Yes | No |

**Auditor** is the existing `auditor` role, not a new role. Administrative actions check roles against the database on the server (`RequireRoleAsync`/`RequireAnyRoleAsync`).

### Audit Log (`audit_log`)

Events: `share.grant`, `share.revoke`, `submission.create`, `publication.reject`, `publication.approve`, `publication.update` (new version), `publication.deactivate`, `publication.activate`, `publication.results.read`. Store actor, target ID, and timestamp; **never** tokens, answers, or result contents.

### API and MCP

- New paths: `/api/sharing/*` (sharing/submissions; browser session only), `/api/admin/submissions|publications/*` (admin only), `/api/global/*` (admin/auditor read-only). `GET /api/modules/?scope=private|shared|global|all` defaults to `private` for backward compatibility. `GET /api/cards/?module_id=` and `GET /api/cards/{id}` also read shared/global content.
- **MCP:** ADR 0013 still applies. Tokens can call only `/api/modules`, `/api/cards`, `/api/import`, `/api/schema`, `/api/mcp/whoami`, including **read access** to shared/global modules (`list_modules` with `scope`, `list_cards`). Sharing, submitting, publishing, and results are **not** available through MCP, even with an admin's token. Mutations (`add_card`, `import_cards`, `archive_card`) affect only owned content. The MCP server has no database access.

### Migration

Idempotent, with no data loss: new tables `module_shares`, `module_submissions`; new `modules` columns (`scope` with `DEFAULT 'private'`, `publication_id`, `global_version`, `global_status`, `source_module_id`, `source_owner_user_id`, `source_owner_name`, `published_at`, `published_by`), and `quiz_results.context` (`DEFAULT 'private'`) via `PostgresDatabase.MigrateSharingAsync` or `SqliteDatabase.MigrateSharing`; create `ix_modules_scope` and `ix_modules_publication` afterward (`Schema.SharingIndexStatements`). Existing modules, cards, and results remain `private`. Unassigned legacy content (`owner_user_id IS NULL`) means only `scope = 'private'`; global modules are excluded.

## Addendum 2026-10-25: Choose Replacement and Delete

- During approval, an administrator chooses **which publication to replace**: default (updates replace the author's own prior versions; first publication is independent), “publish as new” (`replace_publication_id=new`), or a specified existing publication (`POST /api/admin/submissions/{id}/approve` with `{"replace_publication_id": "<ID>"}`). The submission becomes the next version of the chosen publication and previous versions become `superseded`. An unknown target returns 404 and leaves the submission pending.
- Administrators may **permanently delete** a global version (`DELETE /api/admin/global-modules/{moduleId}`): cards, unreferenced images, and learning results of that version are removed (UI confirmation required); the author's private original is untouched. Audit event: `publication.delete`. Deactivation remains the less destructive option.

## Consequences

- Global publication is **copy-based**: it duplicates cards (storage cost), providing clean versioning, results, withdrawal, and unchanged learning/quiz/slide logic.
- Deleting cards/modules with other users' results is blocked to protect their records.
- User search reveals display name and email of active users to authenticated users (minimum 3 search characters, maximum 10 matches), a deliberate privacy trade-off needed for directed sharing.
- Global result details contain names; access is limited to admins/auditors and audited.
- The original review reported security issues F1–F4 and required implementing ADR 0014 **before production multi-user use**, particularly F1 (silent administrator-enabled development-login fallback could expose publication workflows). Later ADR 0026 partially addresses the operating-mode problem; review current implementation separately.

## Guardrails for Changes

- Read other users' modules **only** through `ResolveModuleAccessAsync` or derived `*Readable*` methods; mutations always use `ownerId`. Never implement “admin can do everything” shortcuts.
- For new global features, check `status`/`scope` in every query; `staged`, `superseded`, and `deactivated` are **not** available for study. Extend `tests/LearnCards.IntegrationTests` (“sharing, publication, and global results”).
- Never enable sharing/publication through MCP tokens or `X-MCP-Key` without a dedicated ADR.
- Never share personal status, chat, or results; minimize and audit result-detail access.
- The original rule was to never delete published versions or reuse IDs; the 2026-10-25 addendum adds an explicit admin-only deletion exception. IDs must not be reused.

## Affected Code

- Data: `Data/Schema.cs`, `PostgresDatabase.cs` (`MigrateSharingAsync`), `SqliteDatabase.cs` (`MigrateSharing`)
- Services: `Services/SharingService.cs`, `CardRepository.Sharing.cs`, `CardRepository.cs`, `QuizService.cs`, `ChatService.cs`, `UserService.cs` (`RequireAnyRoleAsync`, `SearchUsersAsync`, `ListPublicationAuditAsync`)
- API: `Api/ApiEndpoints.cs` (`MapSharingEndpoints`, `MapGlobalReviewEndpoints`, admin publication)
- UI: `Components/Pages/Home.razor`, `ModulePage.razor`, `Admin.razor`, `Profile.razor`, `Help.razor`, `Components/Shared/SharePanel.razor`
- MCP: `LearnCards.McpServer/Program.cs` (`list_modules` with `scope`)
