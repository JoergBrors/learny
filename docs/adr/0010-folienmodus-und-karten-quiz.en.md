# 0010 — Slide Mode and Static Card Quizzes

- **Status:** Accepted — quiz logic partially superseded by [0019](0019-einheitliches-quizsystem.en.md) (slide mode and `quiz[]` fields remain valid)
- **Date:** 2026-10-24
- **Supplemented by:** [0016](0016-bilder-in-lernkarten.en.md) (images also appear in slide mode), [0011](0011-mehrbenutzerarchitektur.en.md) (cards and their static quizzes belong to the card owner), [0015](0015-freigaben-globale-lernmodule.en.md) (slide mode and card quizzes also work in shared and global modules)

## Context

In addition to conventional flashcard learning, the system should support slide decks for training/exam preparation with time targets and per-page quizzes. LLM-generated quizzes ([0008](0008-ki-integration-und-fallback.en.md)) are not reliable enough for fixed slides and require an API key.

## Decision

- Cards can contain `slide_number`, `target_time_sec`, and a static `quiz[]` (`CardQuizQuestion`, e.g., `single_choice` with `correct_index` and `explanation`); these fields are part of canonical JSON ([0006](0006-kanonisches-karten-json.en.md)).
- `CardQuizPanel.razor` runs the static quiz directly on the card (slide mode), without an LLM.
- Module-wide AI-powered quizzes (`QuizPanel`/`QuizRunner`, `/api/quiz/*`) remain separate; `POST /api/cards/{id}/quiz` creates questions for a single card.
- Example: `demoodule/kvno_folien_lernkarten.json`; template: `wwwroot/templates/slide-deck-template.json`.

## Consequences

- Two quiz types with different sources (static vs. LLM) and separate evaluation.

## Guardrails for Changes

- Static quizzes must never depend on an LLM call.
- When adding question types, update `CardQuizQuestion`, schema (`CardImportSchema.cs`), template, `CardQuizPanel.razor`, and Help page together.
- `correct_index` is zero-based; retain validation during import.

## Affected Code

- `Domain/Models.cs`, `Components/Shared/CardQuizPanel.razor`, `FlashCardView.razor`, `Api/CardImportSchema.cs`, `wwwroot/templates/slide-deck-template.json`

## Addendum 2026-10-25

The separation described in this ADR between card quizzes (static, client-side evaluation) and module quizzes (AI, free text) has been superseded by the **unified quiz engine** ([ADR 0019](0019-einheitliches-quizsystem.en.md)): shared sessions, grading, configuration, and result storage.

`CardQuizPanel` remains an entry-point component. `quiz[]` remains the canonical import/export contract (now also supporting optional `multiple_response` and `free_text`); questions are additionally managed in the question pool.
