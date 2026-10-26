# 0006 — Canonical Card JSON (snake_case) and Live Schema

- **Status:** Accepted
- **Date:** 2026-10-24
- **Supplemented by:** [0016](0016-bilder-in-lernkarten.md) (optional `images[]` field with Base64 images), [0011](0011-mehrbenutzerarchitektur.md) (cards have an owner; JSON deliberately has **no** ownership field), [0015](0015-freigaben-globale-lernmodule.md) (card JSON is unchanged; global copies retain all card fields including quizzes)

## Context

Cards are created through UI imports, the REST API, MCP tools, and AI-generated files. All paths must use the same format.

## Decision

- JSON uses **snake_case** globally (`ConfigureHttpJsonOptions` in `Program.cs`).
- Card fields: `module`, `category`, `term`, `question`, `definition`, `how_it_works`, `context`, `key_fact`, `reference_answer`, `chat_prompt`, `official_sources[]` (`title`, `url`, `publisher`), `slide_number`, `target_time_sec`, `quiz[]`, `images[]` (optional; ADR 0016), `archived`, `sort_order`.
- Import accepts an array or `{ "cards": [...] }`.
- The machine-readable schema is centrally generated in `Api/CardImportSchema.cs` and served live: `GET /api/schema/import/cards`; MCP tool `get_import_schema`, HTTP `/schema/import/cards`.
- User-facing templates: `src/LearnCards.Web/wwwroot/templates/*.json`, documented on the Help page.

## Consequences

- Adding a field affects several places; missing one causes UI, API, and MCP to diverge.

## Guardrails for Changes

**Checklist** for a new or modified card field:

1. `Domain/Models.cs` (`Card`, `CardJson`) and `Data/Schema.cs` (see [0004](0004-datenbank-abstraktion.en.md))
2. `Services/CardRepository.cs` (reading, writing, importing)
3. `Api/CardImportSchema.cs` (schema and description)
4. MCP tools `add_card`/`import_cards` in `LearnCards.McpServer/Program.cs`, if the input schema is affected
5. Templates under `wwwroot/templates/`, `Components/Pages/Help.razor`, and the README example; for image fields also `Domain/CardImages.cs`, MCP tool descriptions, and ADR 0016
6. Rendering in `FlashCardView.razor` (and possibly `ModulePage.razor`)

- Imported cards always belong to the importing user; `owner_user_id`/`user_sub` in JSON are ignored. A supplied `id` is retained only if it does not already exist globally; otherwise a new ID is created.
- Do not rename or remove existing fields; new fields must be optional so older files remain importable.
- `demoodule/*.json` must remain importable.

## Affected Code

- `Domain/Models.cs`, `Api/CardImportSchema.cs`, `Services/CardRepository.cs`, `wwwroot/templates/*`, MCP `Program.cs`

## Addendum 2026-10-26 (ADR 0020)

The text fields `definition`, `how_it_works`, `context`, `key_fact`, and `reference_answer` remain strings and additionally support a small formatting syntax (Markdown subset, no HTML) with a length limit of 20,000 characters. See [0020](0020-sichere-visuelle-bearbeitung-formatierter-lerninhalte.md) and `rich_text_rules` in the live schema. **No fields** were added, renamed, or removed; therefore, the “new field” checklist does not apply.
