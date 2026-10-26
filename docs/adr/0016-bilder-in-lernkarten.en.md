# 0016 — Images as Part of Flashcards

- **Status:** Accepted — storage partly superseded by [0017](0017-konfigurierbare-bildspeicher.en.md)/[0018](0018-zentraler-image-proxy-und-storage-zugriff.md); format and limits still apply.
- **Date:** 2026-10-24
- **Numbering note:** The change request referred to “ADR 0011,” but that number was already assigned to multi-user architecture (0011–0015), so this decision is ADR 0016.
- **Supplements:** [0006](0006-kanonisches-karten-json.en.md) (card JSON), [0004](0004-datenbank-abstraktion.en.md) (schema), [0007](0007-mcp-server.en.md) (MCP tools).

## Context

Flashcards previously contained only text. Diagrams and screenshots should be imported (REST, MCP, file import) and displayed as optional parts of a card, without external file storage, new dependencies (ADR 0002), or disrupting existing cards, import files, and offline operation. Multimodal LLMs may populate the field following the live schema, but the application must never assume that an LLM generates images.

## Decision

### Card Format (Backward-compatible Extension of ADR 0006)

Introduce an **optional** `images[]` field in card JSON; each element contains:

| Field | Required | Meaning |
|---|:---:|---|
| `mime_type` | Yes | `image/png`, `image/jpeg`, or `image/webp`; must match the actual file |
| `data_base64` | Yes | Standard Base64 of the actual image file, **without** the `data:` prefix |
| `alt_text` | No (recommended) | Alternative text, maximum 300 characters |
| `caption` | No | Image caption, maximum 300 characters |

- **Initial product limits** (centralized in `Domain/CardImages.cs`, `CardImageLimits`): up to **5 images** per card and **2 MiB** decoded per image. Keep the live schema, MCP descriptions, and help text consistent with these limits.
- **Missing-field semantics:** Missing or empty `images` means no images. When **overwriting** (`overwrite_existing`, `PUT`), omitting the `images` field preserves existing images; `"images": []` deliberately removes them (`CardJson.Images` is nullable: `null` means the field was omitted).

### Validation (Server Side, `CardImageValidator`)

Validate in this order with numbered, readable errors (“Image 2: …”): image count → allowed MIME types → text lengths → reject `data:` prefix → Base64 text length (**before** decoding to prevent excessive allocation) → valid standard Base64 → decoded size → **file signature** (PNG, JPEG, WebP) matches `mime_type`. Whitespace and line breaks within Base64 are tolerated, stripped, and normalized for storage.

Validation is performed by `CardJson.Validate()` (imports/POST) and as a final line of defense in `CardRepository.CreateCardAsync/UpdateCardAsync` (also covering PUT and MCP). Images are not decoded into pixels, resized, or re-encoded (no image library required).

### Storage

`cards.images_json TEXT NOT NULL DEFAULT '[]'` stores an array of images including Base64, migrated idempotently in `PostgresDatabase.MigrateCardContentColumnsAsync` or `SqliteDatabase.InitAsync`. Existing cards receive `'[]'`. Only parameterized queries are used. Global publication copies (ADR 0015) include images; shared modules reference the same records. **This inline storage design is partially superseded by ADR 0017.**

### API and Schema

- `GET /api/schema/import/cards` describes `images[]` and its fields, limits, and the rule “never invent Base64”; its example contains a tiny valid PNG and can itself be imported.
- **Single-card reads:** `GET /api/cards/{id}` originally returned complete image contents; **lists** and write responses returned only metadata (`mime_type`, `size_bytes`, `alt_text`, `caption`) to avoid bloating list requests such as MCP `list_cards`. ADR 0017 later changes full byte retrieval to opt-in `?include_image_data=true`.
- Import responses additionally include `errors[]` (up to 20 rejected cards and reasons). One invalid card does not prevent other cards from importing (it is `skipped`).

### MCP

The MCP server contains **no image logic**: `add_card`/`import_cards` forward `images` unchanged to the API; validation and error messages originate there. Tool descriptions and the `add_card` input schema explain Base64 (no prefix, real file bytes, limits) and instruct LLMs **not to fabricate Base64 data**. `get_import_schema` returns the live schema unchanged. `import_cards` reports rejected cards with reasons.

### Display

`FlashCardView` originally rendered stored images directly as `data:` URIs (`<figure><img><figcaption>`) without additional fetches: on the **front**, below the question, and on the **back** only when flipped (no duplicate image data at rest). MIME types were revalidated when rendering, and external image URLs were never fetched. Images were responsive (`max-width: 100%`, bounded height), on a light background to keep transparent diagrams readable in dark mode. Slide mode, quiz, shared and global modules used the same component and displayed images too. Images are **never** included in LLM prompts (chat, quiz), and the offline fallback is unaffected. Delivery has since evolved under ADRs 0017–0018.

## Consequences

- **Size/performance:** A card may contain roughly 13 MB of Base64 (5 × 2 MiB × 4/3). At the time, the application loaded cards for a module, including images, into Blazor circuit memory, which is noticeable with many large images. Possible future work: lazy-load images for the currently displayed card or move them to a separate table/object store (subsequently addressed by ADR 0017).
- **Import files and request sizes:** ASP.NET Core's default body limit is about 30 MB (28.6 MiB); IIS (`maxAllowedContentLength`) uses the same default. UI file import accepts up to 28 MiB. This applies to the web application and MCP HTTP transport (derived from framework defaults, not measured). The stdio transport imposes no server-side line-length limit; MCP client/model limits (context, output size) were **not verified** and are practically much lower. Therefore use small images (under 200 KiB), few per card, and small import batches.
- Security: untrusted Base64 is used only as a `data:` URI in an `<img>` with a verified type (no SVG, scripts, or HTML). **SVG is deliberately not supported.**
- Images increase database and backup/export size. No image resizing or deduplication was provided in this initial design.

## Guardrails for Changes

- Change permitted types and limits **only** in `CardImageLimits`; then verify the live schema, MCP descriptions, help text, this ADR, and tests. New types (e.g., GIF, SVG) require a security review and a new ADR.
- **Every** new card-write path must run `CardImageValidator` (preferably through the repository); never store or serve unvalidated images.
- List/overview endpoints must not return Base64; complete bytes are available only through explicit single-card retrieval. Never include image data in logs, audit records, or LLM prompts.
- Preserve “omitting `images` keeps existing images,” otherwise imports without the field would destroy images.
- Do not render arbitrary external image URLs or unvalidated HTML/SVG from card data.

## Affected Code

- `Domain/CardImages.cs` (model, limits, validator), `Domain/Models.cs` (`Card.Images`, `CardJson.Images`)
- `Data/Schema.cs`, `PostgresDatabase.cs`, `SqliteDatabase.cs` (`images_json`)
- `Services/CardRepository.cs` (write/read/import and `ImportResult.Errors`), `CardRepository.Sharing.cs` (copy)
- `Api/ApiEndpoints.cs` (metadata vs. full-read DTOs and error handling), `Api/CardImportSchema.cs` (live schema)
- `LearnCards.McpServer/Program.cs` (tool descriptions, `add_card` schema, import errors)
- `Components/Shared/FlashCardView.razor`, `wwwroot/app.css`, `Components/Pages/Help.razor`, `wwwroot/templates/full-card-template.json`
