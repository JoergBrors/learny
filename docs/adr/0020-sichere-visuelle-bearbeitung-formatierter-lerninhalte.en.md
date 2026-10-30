# 0020 — Safe Visual Editing of Formatted Learning Content (Rich Text and Card Editor)

- **Status:** Accepted
- **Date:** 2026-10-26
- **Supplements:** [0006](0006-kanonisches-karten-json.en.md) (optional formatting syntax in existing fields, no new fields), [0016](0016-bilder-in-lernkarten.en.md)/[0017](0017-konfigurierbare-bildspeicher.en.md)/[0018](0018-zentraler-image-proxy-und-storage-zugriff.en.md) (images), [0019](0019-einheitliches-quizsystem.en.md) (reuse quiz engine and pool)
- **Related:** [0002](0002-keine-nuget-pakete.en.md) (no editor library), [0003](0003-eine-deploybare-einheit.en.md), [0011](0011-mehrbenutzerarchitektur.en.md), [0015](0015-freigaben-globale-lernmodule.en.md)

## Context

Cards were created only by JSON import/REST, and `definition`, `how_it_works`, `context`, `key_fact`, `reference_answer` held plain text. Users need to edit cards, formatted text, images, and quiz questions in the browser without dependencies, duplicated persistence, or XSS risks. Browsers' `contenteditable` editors produce HTML, which must never be stored or rendered without validation.

## Decision

1. **Constrained custom markup rather than HTML:** Store formatted text in **existing fields** as plain text with a small Markdown subset (`RichText`, `Domain/RichText.cs`): paragraphs (blank lines), line breaks, headings `# ## ###`, `**bold**`, `*italic*`/`_italic_`, `++underlined++`, lists `- ` and `1. `, links `[text](https://…)` (HTTP/HTTPS only), and backslash escaping. **No new database or JSON fields.** Existing plain text remains valid. Avoid mistakenly formatting `C++/C++`, `2 * 3 * 4`, or `snake_case`.
2. **Render only server-side from markup:** `RichText.ToHtml` first HTML-encodes all text, then creates only `p, br, strong, em, u, h4–h6, ul, ol, li, a`. Links must use absolute HTTP(S) URLs without credentials, quotes, whitespace, or control characters; add `rel="noopener noreferrer nofollow"` and `target="_blank"`. Extract URL contents before markup highlighting so markup in URLs cannot alter addresses. Scripts, event handlers, `javascript:`/`data:` URLs, SVG, embedded resources, iframes, and styles cannot render; they appear as text. Remove internal private-use placeholder characters from input. `RichTextView`, `FlashCardView`, and editor preview share the same rendering (CSS class `rt`).
3. **Input limits:** Maximum 20,000 characters per field; disallow control characters except line breaks/tabs (`RichText.Validate`, called by `CardJson.Validate` for POST, PUT, and import). Links ≤2,000 characters.
4. **Visual editor without library:** `RichTextEditor` (Blazor) + `wwwroot/rte.js` (`contenteditable`, `execCommand` for paragraphs, bold, italics, underline, lists, links, clearing formatting). Serialize **into markup while typing**, never HTML; the application re-renders markup server-side. Treat clipboard pastes and drag-and-drop as **plain text**; never import foreign HTML. Serialization preserves only known formats, including nested formats, and discards everything else. Validate links as HTTP(S) in browser **and** server.
5. **Card editor:** `/module/{id}/edit`, `ModuleEditor.razor`, owner only. Create, edit, duplicate, archive/restore and reorder cards; edit all fields, sources, slide mode, images (upload, preview, alt text, caption, order, confirmed removal), and a movable nonmodal live-preview overlay (position/size in `localStorage`, reset with ⟲, bottom sheet on narrow screens). Support discard and reuse `QuizConfigPanel`. No new persistence: save through `CardRepository.CreateCardAsync/UpdateCardAsync`; images through `CardImageService` with import-equivalent validation; questions through `QuizService`/`QuizPoolService`. New repository methods: `DuplicateCardAsync`, `ReorderCardsAsync`, `ToCardJson`, optional conflict detection in `UpdateCardAsync`. REST: `POST /api/cards/{id}/duplicate`, `PUT /api/modules/{id}/cards/order`, `X-Card-Updated-At` header for `PUT /api/cards/{id}`. PUT now validates like POST using `CardJson.Validate`.
6. **Images:** On save, the editor sends the entire list (existing images via `image_id`, new ones with content) to preserve images while changing captions/order. Existing images use only `GET /api/images/{id}` (ADR 0018); unsaved uploads alone may appear in the local preview as a temporary `data:` URL.
7. **Quiz questions preserve the single source of truth (ADR 0019):** Questions in card `quiz[]` (`source=card_json`) are edited in the card editor and saved **with the card**, synchronized via the existing write path. Questions with `source=ai` or `editor` are persisted immediately as separate records through `QuizService` (create/edit/associate card/activate/deactivate). The interface clearly distinguishes both sections and save behavior. Never edit one question in two places. No new grading or generation paths.
8. **Unsaved changes and conflicts:** Continuously compare a draft snapshot against the loaded version (show “unsaved changes”). Confirm navigation on card changes, internal link clicks (capture listener in `rte.js`, independent of Blazor navigation interception), Blazor `NavigationLock`, and native browser close/reload warnings. For concurrent edits, `UpdateCardAsync` compares the loaded `updated_at` with current value and throws `CardConflictException` (REST 409 `card_conflict`). The editor warns, does not overwrite, and offers reload. Without the header, “last write wins” still applies to imports/existing clients. Limitation: check and update are not a single atomic SQL statement, leaving a small race window.
9. **Permissions:** Only owners of private modules may edit (SQL `owner_user_id` filter; other users see an access warning). Share recipients, unrelated users, admins of someone else's private modules, and global versions receive 404/403. Global versions remain immutable.

## Rejected Alternatives

- *Store HTML and sanitize on output:* error-prone without a library; parser mistakes could introduce XSS. Custom markup is never interpreted as HTML.
- *External editor library (Quill, TipTap):* violates ADR 0002 and increases attack/maintenance surface.
- *New `definition_html` field:* breaks canonical JSON and duplicates maintenance.
- *Separate rich text for every question:* Quiz questions stay plain text to preserve unambiguous assessments.

## Consequences

- Existing cards change appearance only if text accidentally contains markup (e.g., leading `- ` lists or `**…**`); single line breaks now remain visible.
- AI prompts and grading see the human-readable markup; `RichText.ToPlainText` provides a plain-text view.
- The editor requires interactive Blazor with JavaScript for `contenteditable`; without JavaScript, only viewing is available.

## Guardrails for Changes

- When adding formatting, update `RichText` rules/output, `rte.js` serialization, `RichTextEditor`, `.rt` CSS, `CardImportSchema.rich_text_rules`, MCP descriptions, Help, and tests **together**. Never render user HTML without validation (`MarkupString` only with `RichText.ToHtml`).
- All write paths invoke `CardJson.Validate`, including `RichText.Validate`.
- Do not create another card/quiz persistence implementation inside the editor.

## Open Questions

- Optimistic concurrency is not fully atomic (see above); consider `UPDATE … WHERE updated_at = …`.
- Browser tests used headless Chrome; Safari/Firefox and real touch devices were not tested.
- `document.execCommand` is deprecated but broadly available; a replacement would be localized to `rte.js`.
- Answers remain visible in card JSON (ADR 0019); certification requires a separate integrity decision. The editor does not change that.

## Affected Code

`Domain/RichText.cs`, `Domain/Models.cs` (`CardJson.Validate`), `Services/CardRepository.cs`, `Services/QuizPoolService.cs` (`CopyCardQuestionsAsync`), `Api/ApiEndpoints.cs`, `Api/CardImportSchema.cs`, `LearnCards.McpServer/Program.cs`, `Components/Pages/ModuleEditor.razor`, `Components/Shared/RichTextEditor.razor`, `RichTextView.razor`, `QuizQuestionForm.razor`, `FlashCardView.razor`, `ModulePage.razor`, `wwwroot/rte.js`, `wwwroot/app.css`, `wwwroot/templates/full-card-template.json`, `Components/Pages/Help.razor`, `tests/browser/editor-browser-check.js`.
