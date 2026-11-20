# 0027 — Topics, Learning Paths, Versioning, and Final Exams

- **Status:** Partially superseded by [0028](0028-themenmodule-json-und-mcp-verwaltung.en.md) (item 2: own private modules only; item 10: write-enabled MCP access to own topic drafts). Item 5 (assignments by owner) is superseded by [0029](0029-themenzugriff-pflichtzuweisungen-und-themenadministration.en.md): only admins/topic admins assign specific versions. All remaining decisions still apply.
- **Date:** 2026-10-25
- **Motivation:** Model Topic → Learning Modules → Cards/Module Exams → Final Exam (e.g., “Microsoft 365 Administration” using Entra, Exchange, Teams), without duplicating modules.
- **Related:** ADRs [0004](0004-datenbank-abstraktion.en.md), [0011](0011-mehrbenutzerarchitektur.en.md), [0015](0015-freigaben-globale-lernmodule.en.md), [0019](0019-einheitliches-quizsystem.en.md), [0021](0021-zertifikate-und-open-badges.en.md), [0022](0022-polygon-verankerung-und-kostenschranke.en.md).

## Decision

1. **Data model** (only `CREATE TABLE IF NOT EXISTS`; SQLite and PostgreSQL): `topics` (draft, owner, `active|archived`, `draft_revision`/`published_revision`), `topic_modules` (topic ↔ module relation with `sort_order`; a module can belong to many topics, with no foreign key to `modules`), `topic_exam_configs` (JSON for module/final exams), `topic_questions` (topic-specific questions), `topic_versions` (immutable versions with `snapshot_json`, `version_number`, `global_status`), `topic_version_modules` (version → frozen module copy and `content_hash`), `topic_shares`, `topic_assignments`, `topic_user_state` (last viewed version/activity), `topic_exam_attempts` (attempt ↔ quiz session ↔ topic version).
2. **Modules are not duplicated merely to assign them to a topic.** Drafts reference original modules in `topic_modules`. Initially, owners could add their **own** modules and **actively published global** modules, never modules merely shared with them. **ADR 0028 supersedes this:** topic drafts now accept only owned private modules; clone a global module into a private module first.
3. **Versioning:** Draft changes never alter published versions. Publishing creates a new strictly increasing version (`MAX+1`, `UNIQUE(topic_id, version_number)`; a `building` status row prevents concurrent publication). Calculate content hashes from active cards/questions per module. **Unchanged modules reuse the same frozen copy**, preserving learning progress and passed exams. Changed modules get new frozen copies (`scope='topic'`, no owner). No version is created when `draft_revision == published_revision` and hashes match. Older versions remain readable and launchable.
4. **Access:** Frozen topic modules are accessible only through `ResolveModuleAccessAsync` with `topic` context and `CanLearnTopicModuleAsync`: owner, share recipients, assignees, or all authenticated users for an active global version. Learning is possible only through the topic; ordinary `/quiz/start` for a topic copy returns 403. Submissions without sessions are rejected. Admin access does not bypass private ownership. Roles are DB-validated, user ID derives only from claims.
5. **Sharing/assignment:** `topic_shares` grants learning access to a **published** version; `topic_assignments` marks an assigned learning task and allows progress visibility to the assigner. Both are revocable and results are preserved. Grant/revoke/submit/approve/reject/withdraw actions are audited by `UserService.WriteAuditAsync`. **Assignment authority is superseded by ADR 0029:** only admin/topic admin, for a specific version.
6. **Global topics:** Owner submits a version (`pending_approval`, at most one open submission per topic). Only an administrator approves/rejects (atomic `UPDATE` prevents duplicate decisions). Approval supersedes the previous version; **rejection leaves the prior active version untouched**. Withdrawal sets `deactivated`. Administrators review only frozen versions, never private originals.
7. **Exams reuse the quiz engine** ([0019](0019-einheitliches-quizsystem.en.md)), never a second engine. `QuizService.StartTopicExamSessionAsync` creates a standard server-side session (stored questions only; `Origin=fixed`, `Strategy=none`, works without OpenAI). Grade exclusively with `SubmitQuizAsync`/`GradeSessionAsync`; never expose answers before submission. `topic_exam_attempts` ties each attempt to topic, **version**, and module. Module exams configure question count, passing threshold, retry, and minimum completion percentage for the next module. Final exams configure question count and distribution (`even|proportional|manual`), optional topic questions, and prerequisites (all modules completed / module exams passed). If the question pool is insufficient, report “only N matching questions available” rather than inventing questions.
8. **Progress:** Track per user/version started/completed modules, module exams, final exam, attempts, best result, last activity, and “new version available.” A changed module copy is pending again; unchanged completed copies remain passed.
9. **Certificates:** No topic certificates or blockchain transactions are created. For `topic` context, `QuizService` skips issuance; ordinary module certificates under ADR 0021 remain unaffected. `topic_exam_attempts` supplies a verified basis for a possible future topic credential, requiring a new ADR.
10. **API/MCP:** REST routes under `/api/topics*` and `/api/admin/topics*` with service-side owner/admin authorization. Originally MCP tokens could **only read** through `list_topics`, `get_topic`, `get_topic_exam_structure`; shares, assignments, progress, attempts, user search, administration, and all global actions are forbidden (`TokenMayReadTopics`). **ADR 0028 adds write tools for the user's own drafts.**

## Consequences

- New tables, no changes to existing tables; card JSON/imports remain compatible (topics are not part of card imports).
- Storage grows only for changed frozen module copies due to hash reuse.
- The owner may keep editing modules; learners see the effect only after publication.
- Deleting a module removes draft references (`topic_modules`) but published copies remain. Topics are archived rather than deleted.

## Limitations

- Grading cross-module free-text questions relies on their stored explanations.
- Topic certificates and anchoring are deliberately unimplemented (item 9).

## Guardrails for Changes

- Never modify published versions or their frozen module copies; generate new numbers only on publication.
- Run exams exclusively through `StartTopicExamSessionAsync` and existing grading; never expose answers before submission.
- Only add another user's module if it is actively global (historical ADR 0027 rule); **current ADR 0028 requires an owned private copy**. Never bypass `ResolveModuleAccessAsync`/`CanLearnTopicModuleAsync` for topic copies.
- Never expose global actions or user assignments through MCP.
- Tests: `TopicE2ETests` (T1–T80), `tests/browser/topics-browser-check.js`.
