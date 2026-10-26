# 0019 — Unified Quiz Engine, Configurable Question Types, and Persistent Question Pool

- **Status:** Accepted
- **Date:** 2026-10-25
- **Partially supersedes:** [0010](0010-folienmodus-und-karten-quiz.en.md), namely separate grading for static client-side card quizzes and AI module quizzes. Slide mode (`slide_number`, `target_time_sec`) and `quiz[]` fields remain unchanged.
- **Supplements:** [0006](0006-kanonisches-karten-json.en.md) (`quiz[]` remains the import/export contract), [0008](0008-ki-integration-und-fallback.en.md) (offline fallback is required), [0011](0011-mehrbenutzerarchitektur.en.md), [0015](0015-freigaben-globale-lernmodule.en.md) (access and frozen versions)
- **Prepares for:** Task 2 (WYSIWYG editor), Task 3 (certificates/auditors)

## Context (Existing System, Analyzed)

| Aspect | Previous Card Quiz | Previous Module Quiz |
|---|---|---|
| Question generation | Static from `cards.quiz_json` or generated per request (`GetCardQuizAsync`, not stored) | AI (`StartQuizAsync`, free text, not stored); offline directly `card.question` |
| Question storage | Only card `quiz_json`; AI questions ephemeral | None |
| Grading | Client-side `QuizRunner` (count correct choices) | Server-side `SubmitQuizAsync` (AI prompt or keyword heuristic) |
| Result persistence | No | Yes (`quiz_results`) |
| Configuration | None | Only question count in dialog |

Consequences: two grading implementations, answers visible in the browser (no verifiable result), generated questions never reused, and question types limited to single choice/free text.

## Decision

1. **One engine:** `QuizService` is the only business implementation of question selection, sessions, answer validation, grading, and saving results. `CardQuizPanel` and `QuizPanel` merely launch the shared `QuizRunner`.
2. **Server-side sessions:** `POST /api/quiz/start` and `POST /api/cards/{id}/quiz` create a `quiz_sessions` record, keeping solutions **only server-side** and sending questions without answers. `POST /api/quiz/submit` grades against the session; submissions are single-use (409 on repeat) and other users' sessions return 404. Without `session_id` (legacy clients), a submission is handled as a pure free-text session using the same grading.
3. **Question types:** `single_choice`, `multiple_response` (all-or-nothing; exact matching set), `free_text`. Choice questions grade locally and exactly (10 points). Free text uses AI, or a local heuristic when AI is absent/fails. Results: points, percent, grade (A–F), and **passed** (percent ≥ passing threshold).
4. **Per-module quiz configuration** (`quiz_configs` JSON, compatible defaults when absent): `enabled`, `allow_multiple_choice`, `origin` (`fixed|ai|mixed`), `strategy` (`none|on_demand|prefer_stored|mix`), `single_choice`/`multiple_response`/`free_text`, `question_count` (1–20), `shuffle_questions`, `shuffle_answers`, `scope` (`card|module|both`), `pass_percent` (1–100), `allow_retry`, `show_correct_answers`. **Invalid combinations are rejected server-side with reasons** (e.g., choice format enabled when choices are disabled; quiz enabled with no format; fixed-only questions combined with generation). UI automatically disables incompatible options.
5. **Persistent question pool:** `quiz_questions` per module or frozen module version; optional card association. Store question type, text, options, correct indexes, explanation/model answer, `origin` (`manual|ai`), `source`, `active`, `module_version`, fingerprint, timestamps. Fingerprints (card, type, normalized text, options, correct options) prevent duplicates through a per-module unique index.
6. **Exactly one source of truth per question:**
   - `source = card_json`: maintain the question in card `quiz[]` (canonical import/export, ADR 0006). The pool contains only a **projection**, precisely synchronized on every card save/import (add newly created, remove deleted, preserve unchanged row and active state). Editing these questions directly in the pool is forbidden (“edit through card”).
   - `source = ai`: AI-generated, only in the pool. `source = editor`: only in pool (Task 2).
   - Never remove `cards.quiz_json`. On startup an idempotent migration (`cards.quiz_synced`) projects existing `quiz[]` into the pool.
7. **Selection and generation:** Begin with stored active questions of allowed origins/types. Then follow `strategy`: `none` = stored only; `on_demand` = generate missing questions; `prefer_stored` = generate only when no matching questions are stored; `mix` ≈ half stored, half new. Generation uses `OpenAiClient.CompleteJsonAsync` with a token budget based on count. Validate each AI question (type, options count, correct indexes, source card in the selection), deduplicate, discard invalid questions, and save valid questions to the pool **only for owned modules**. **Offline fallback:** Without AI or on AI failure, derive missing free-text questions from cards **only if free text is allowed**. Settings must never silently switch to disallowed types; otherwise return a clear error/notice in `notices`.
8. **Question counts:** Module quiz: `min(question_count, requested count)` from checked cards. Card quiz: `min(question_count, max(number of stored card questions, 3))`. `scope` may forbid card/module quizzes (403). If `allow_retry=false`, subsequent starts return 409. If `show_correct_answers=false`, results and history omit correct options and explanations.
9. **Access and versioning:** Only owners of private modules may edit config/pool (`ResolveModuleAccessAsync.CanEdit`). Share recipients and all learners may read configuration/take quizzes, but cannot read or modify the pool (403, because it contains solutions). Unknown modules return 404. Submission copies configuration and **active** pool questions into the frozen version (new IDs and updated card associations), after which neither learners nor admins can alter them (403). Questions generated by learners of shared/global modules remain **ephemeral, in the session only**, leaving the pool unchanged. Existing saved results are preserved; new results additionally store `session_id`, `passed`, `pass_percent`, `module_version`.
10. **Backward-compatible card JSON:** `single_choice` with `correct_index` and exactly four options remains unchanged. Add optional `multiple_response` (3–6 options, `correct_indexes`) and `free_text` (only `explanation` = model answer). Import schema (`/api/schema/import/cards`) remains backward compatible (required fields relaxed to `type`, `question`, `explanation`).

## Security Implications / Intentional Changes

- `POST /api/cards/{id}/quiz` no longer exposes `correct_index`/`explanation` before submission (previously available client-side). This is required for verified results (Task 3).
- `GET /api/cards/{id}` and card lists still contain full `quiz[]`, including solutions, as part of the export contract, including for learners in shared/global modules. Certification must reassess this in Task 3.

## Interfaces for Tasks 2 and 3 (QuizService)

Task 2 (editor): `GetConfigViewAsync`, `SaveConfigAsync`, `ListQuestionsAsync(moduleId, user, cardId?, origin?, active?)`, `CreateQuestionAsync`, `UpdateQuestionAsync` (not for `card_json`), `SetQuestionActiveAsync`, validation in `QuizPoolService.Validate`. REST: `GET|PUT /api/modules/{id}/quiz-config`, `GET|POST /api/modules/{id}/quiz-questions`, `PUT …/quiz-questions/{qid}`, `PUT …/quiz-questions/{qid}/active`.

Task 3 (credentials): `QuizService.GetVerifiedResultAsync(resultId, user)` → `VerifiedQuizResult` (result ID, session, user, module, **module version**, score, percent, passing threshold, passed, timestamp), only for session-backed results, never legacy data. **No certificates are issued by this interface itself.**

## Rejected Alternatives

- *Pool as the only source of truth, including `quiz[]`*: Would break card JSON and all imports (REST, MCP, files). A projection keeps the contract stable and separates ownership per question.
- *Stateless grading using client-provided solutions*: Tamperable; worthless for credentials.
- *Partial credit for multiple response*: Deliberately omitted from Task 1 (all-or-nothing is unambiguous); can be configurable later.

## Guardrails for Changes

- Grade only in `QuizService.GradeSessionAsync`, never UI components.
- Solutions leave the server only in results if `show_correct_answers` allows it (and in owner-facing/export card JSON).
- For new question types, update `QuizTypes`, `CardQuizQuestion.Validate`, `QuizPoolService.Validate`, `QuizService` selection/grading, `QuizRunner`, `CardImportSchema`, templates, and Help together.
- Change pool entries derived from `card_json` only through `SyncCardQuizAsync`.

## Addendum 2026-10-25: Stabilization before Editor (Task 1b)

**Editor interfaces (Task 2)** are fully available without a second question-management system:
`GET|PUT /api/modules/{id}/quiz-config`; `GET /api/modules/{id}/quiz-questions` (filters `card_id`, `origin`, `active`); `POST /api/modules/{id}/quiz-questions` (new `source=editor`, `origin=manual`, optional `card_id` in same module); `PUT /api/modules/{id}/quiz-questions/{qid}` (type, text, options, correct indexes, explanation, associated card; reject `card_json` questions—“edit through card”); `PUT …/{qid}/active`. Only private-module owners (recipients 403, foreign 404, global versions 403). Invalid input and duplicate questions return 400.

**Source of truth verified:** Saving a card (`SyncCardQuizAsync`) modifies only its `source=card_json` questions. Editor and AI questions remain untouched, even when associated with the same card (tested).

**Owner-generated AI questions** persist and are reused (`on_demand`, `prefer_stored`, `mix`). Generation for recipients and global learners intentionally remains ephemeral. Persisting personal learner questions requires a separate design decision (e.g., user-specific pool that does not modify frozen versions).

**Session cleanup:** Sessions are valid for 4 hours (submissions then return 410). Sessions older than 24 hours (submitted/expired) are deleted at startup and at most every 10 minutes when starting quizzes, in bounded batches (≤4 × 500). Valid sessions and all saved `quiz_results` remain intact (results have no session foreign key).

**Security boundary (important for Task 3):** Current quizzes are **not automatically tamper-proof certification exams**. Solutions may be visible before taking an exam:
1. `GET /api/cards/{id}` and `GET /api/cards/?module_id=…` return `quiz[]` including `correct_index`, `correct_indexes`, `explanation` to all users allowed to read a card—owners, sharing recipients, **and global learners** (ADR 0006 import/export contract). The same API routes are allowed for MCP tokens, though `list_cards` itself returns metadata only.
2. `GET /api/modules/{id}/quiz-questions` reveals answers to the **owner** (learners receive 403).
3. Model free-text answers (`explanation`, `reference_answer`) are present in card JSON.
4. `/quiz/start` and `/cards/{id}/quiz` responses do not expose answers (tested); results expose answers only when `show_correct_answers` allows it. History stores the sanitized version.
5. Sessions may be restarted repeatedly when retries are allowed; learners know questions after the first attempt.

Task 3 must therefore decide at minimum whether to hide card JSON answers from learners (separate exam question pool from learning content), limit attempts, randomize/regenerate per attempt, and use only `GetVerifiedResultAsync` for credentials.

## Addendum 2026-10-26: Used by the Card Editor (Task 2)

The visual card editor ([ADR 0020](0020-sichere-visuelle-bearbeitung-formatierter-lerninhalte.en.md)) uses existing APIs unchanged (`ListQuestionsAsync`, `CreateQuestionAsync`, `UpdateQuestionAsync`, `SetQuestionActiveAsync`, configuration). The only addition is `QuizPoolService.CopyCardQuestionsAsync` (copy editor/AI questions when duplicating a card). `card_json` questions are edited and saved as part of their card; `ai`/`editor` questions are saved separately and immediately. UI labels distinguish the two. Grading and the visibility of answers in card JSON are unchanged.

## Open Questions

- Learners can see `quiz[]` solutions within shared/global card JSON (ADR 0006); certification quizzes may need to hide them.
- Partial credit for multiple response and question weighting.
- Session cleanup is implemented (see addendum); fixed retention (4 hours valid, 24 hours cleanup) is not configurable.

## Affected Code

`Domain/Quiz.cs`, `Domain/Models.cs` (`CardQuizQuestion`, DTOs), `Services/QuizService.cs`, `Services/QuizPoolService.cs`, `Services/CardRepository*.cs` (sync, publication copy), `Data/Schema.cs`/`PostgresDatabase.cs`/`SqliteDatabase.cs`, `Api/ApiEndpoints.cs`, `Api/CardImportSchema.cs`, `Components/Shared/QuizRunner.razor`, `QuizPanel.razor`, `CardQuizPanel.razor`, `QuizConfigPanel.razor`, `Components/Pages/ModulePage.razor`, `Help.razor`, `wwwroot/app.css`.
