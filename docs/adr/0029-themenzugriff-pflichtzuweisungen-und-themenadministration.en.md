# 0029 — Topic-specific Module Access, Mandatory Assignments, and Delegated Topic Administration

- **Status:** Accepted (supersedes item 5 of [0027](0027-themen-lernpfade-versionierung-und-abschlusspruefungen.en.md): owners no longer assign learners; supplements [0011](0011-mehrbenutzerarchitektur.en.md), [0012](0012-rollen-aus-entra-app-roles.en.md), [0015](0015-freigaben-globale-lernmodule.en.md), and [0028](0028-themenmodule-json-und-mcp-verwaltung.en.md))
- **Date:** 2026-10-26
- **Motivation:** Clicking “Learn” in a topic module showed “Page not found.” Global/shared topics were not clearly distinguished from mandatory assignments, assignments appeared in the topic editor, and neither a mandatory-task indicator nor administrator progress views existed.
- **Related:** ADRs 0004, 0005, 0009, 0011, 0012, 0013, 0015, 0019, 0023, 0027, and 0028.

## Root Cause of the Module Display Error

1. The “Learn” link pointed to `/modules/{id}` while the actual route is `/module/{id}`, resulting in “Page not found” for **every user**, including the owner.
2. The module page did not recognize `topic` context: it labeled copies “Private,” showed global-module text, and offered module-level “Start Quiz,” intentionally prohibited for topic modules (403).
3. Authorization itself (`ResolveModuleAccessAsync` → `CanLearnTopicModuleAsync`) was already correct and remains the **only** access authority. Frozen copies load by internal ID, never `reference_id`; renaming the source module does not affect published versions.

## Decision

1. **Distinct concepts:** *Global* = active admin-approved topic version, visible and **optionally** learnable by all signed-in users. *Shared* = an owner allows selected users to study a topic (`topic_shares`, all published versions). *Assigned* = admin/topic admin assigns a topic to a user at a **specific version** (`topic_assignments`). *Mandatory* = `mandatory = 1`. *Optional assignment* = `mandatory = 0`. Being global or shared **never** implies an obligation; a topic may be global yet mandatory only for selected users.
2. **Derived learning access:** Anyone who can study a topic (ownership, sharing, assigned version, or active global version) may read **all modules in that version** in topic context (cards, images, card status, module/final exams through the topic). Access extends only to frozen copies (`scope = 'topic'`); original private modules remain protected against reading, export, and cloning. Assignments grant only the specified version (`topic_assignments.topic_version`; NULL legacy assignment means latest published version). Shares remain topic-wide across published versions.
3. **Revocation and withdrawal:** Revoking a share/assignment removes only the derived access. Independent module shares, shares through other topics, results (`topic_exam_attempts`, card state), and published versions remain unchanged. Withdrawing a global version removes general visibility; **explicitly assigned** users retain access to their assigned version until revoked.
4. **`themen_admin` role:** New internal role (display name “Topic Admin”), granted/revoked only by administrators via `UserService.SetRolesAsync` in user management; may be mapped via `OIDC_ROLE_MAP` (`<Entra-App-Role>=themen_admin`). Never granted automatically. Every role check is database-backed and server-side (`RequireAnyRoleAsync`); revocation applies to the next action.
5. **Permissions:** Regular users manage their own topics/modules, learn global/shared topics and topics assigned to them, and view only **their own** progress. Topic admins additionally assign/revoke and read learning progress, but cannot read others' private topics/modules, publish globally, or manage users/roles. Admins have all those privileges plus existing global publication and user/role administration. Auditors may **read** learning progress in addition to prior read-only permissions; no assignment/publication/private content/exam answers.
6. **Assignable topics:** Only topics with an **active global version** (assign that version), the actor's **own** published topics, or topics **explicitly shared with the actor**. Foreign private, archived, or unpublished topics cannot be assigned and return 404 even if requests are manipulated. Assignments specify user, topic, **concrete version**, mandatory flag, timestamp, `assigned_by`, and optional future `due_at`. Reassigning updates the record. Editing a draft later does not change the frozen assigned version. Audit `topic.assign`/`topic.unassign`.
7. **Administration separate from editor:** The topic editor no longer manages user assignments; UI and `/api/topics/{id}/assignments` are removed. Use `/topic-admin` for assignments (admin/topic admin) and progress (also auditor), backed by `/api/topic-admin/*` (`topics`, `users`, `assignments`, `status`). This route deliberately sits outside `/api/topics*` and `TokenAllowedPrefixes`: **MCP tokens cannot use it**, regardless of role, and `MCP_API_KEY` does not grant data access.
8. **Learning status:** Per user/topic/version, based on existing card state and exam results (no second engine): `not_started`, `in_progress` (a card was studied or exam attempted), `completed` (all modules complete and all enabled module/final exams passed). Merely opening a page does not count (`seen_version` is not activity). **Overdue** is a flag on unfinished *mandatory* assignments after due date, not a separate status. Completed mandatory topics are not open obligations. Activity from hash-reused frozen modules counts toward new versions (ADR 0027). `CompletedAt` is the time of the last learning-relevant action at completion.
9. **Mandatory dashboard:** The home page shows “My mandatory topics” using only the signed-in user's server-derived unfinished mandatory assignments, overdue first, with name, version, status, progress, deadline, and “Start”/“Continue”. Completed obligations appear only in history (`mandatory_done`, “Assigned to me”). No assignments → no mandatory section; unrelated functions are never blocked.
10. **Progress reporting and privacy:** `ListStatusAsync` (auditor/topic admin/admin) returns only display name, topic, version, relationship, status, progress, dates—**no email**, card content, chats, answers, or individual exam scores. Includes assigned learners and voluntary activity on **active global** topics; never unrelated private topics. Maximum 500 rows.
11. **Module usage:** An owned module displays which **owned** topics use it (draft, published versions, global activity), linking to topics. Use stable IDs (`topic_modules`, `topic_version_modules.source_module_id`) so renames do not affect lookup. Never disclose other people's topics. Topic modules display their origin (topic/version) and “frozen version” notice.
12. **IIS deployment of MCP server:** The HTTP MCP server runs as a **scheduled task** (`MCP_SERVICE_NAME`, launched as SYSTEM), optionally as a Windows service with the same name. The workflow detects the mode, stops the service/task **before** replacing files, waits up to 45 seconds for process exit (otherwise aborts without file replacement), backs up/restores `mcpsettings.json`, restarts exactly the pre-existing mode (service: `Start-Service`, `Running`, process check; task: create/`/Run` and process check within 45 seconds), then checks the health URL up to 12 times. Startup errors fail the job. A nonexistent service is never created or started. In stdio mode (HTTP disabled), start nothing. Never log secrets.

## Consequences

- `topic_assignments` gains `topic_version INTEGER NULL`, `mandatory INTEGER NOT NULL DEFAULT 1`, `due_at TEXT NULL`, with idempotent migrations in SQLite and PostgreSQL. Legacy assignments remain mandatory without a fixed version. No new tables or environment variables.
- The owner-facing assignment API is removed (tests T49–T53 adjusted). Assignment requires an admin/topic-admin role and an assignable topic.
- Users assigned to a global version retain access if that version is withdrawn, until the assignment is revoked.

## Limitations

- Personal shares cover an entire topic, not a specific version (only assignments are version-bound).
- Role changes affect server operations immediately for existing sessions, but navigation updates at the next login.
- Rolling back MCP binaries is manual from the runner backup (`backup-mcp`); IIS workflow steps were statically reviewed only (no Windows runner tested).

## Guardrails for Changes

- All topic-context module reads go through `ResolveModuleAccessAsync`/`CanLearnTopicModuleAsync`; never duplicate authorization or implement “admin can read everything.”
- Handle assignment/progress only through `TopicService.Assignments.cs` with service-level role validation. Keep new endpoints outside `TokenAllowedPrefixes`/`TokenMayReadTopics`.
- Global publication never sets `mandatory`; obligations require explicit assignment.
- Minimize progress reporting for privacy; never return answers, content, or email addresses.
- Tests: `TopicGovernanceTests` (G1–G40), `TransferIntegrityUnitTests` (I24–I26), `WorkflowStructureTests` (W1–W9), `tests/browser/topics-browser-check.js`.
