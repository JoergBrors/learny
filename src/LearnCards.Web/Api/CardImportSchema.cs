using System.Text.Json.Nodes;

namespace LearnCards.Web.Api;

public static class CardImportSchema
{
    public static JsonObject Document() => new()
    {
        ["schema_version"] = "2026-10-26",
        ["name"] = "learncards-import-batch",
        ["grounding_rules"] = new JsonArray(
            "Use only facts contained in the cards themselves and their official sources.",
            "Do not invent missing fields or unsupported facts.",
            "If a source-backed answer cannot be formed, ask for clarification instead of guessing.",
            "Only add images that were actually provided to you. Never invent, guess or fabricate Base64 image data."),
        ["rich_text_rules"] = new JsonArray(
            "The text fields definition, how_it_works, context, key_fact and reference_answer accept a small, safe formatting syntax (Markdown subset). Plain text without markup stays valid and unchanged. Never send HTML — it is shown as plain text.",
            "Paragraphs: blank line. Line break: single newline. Headings: '# ', '## ', '### ' at line start.",
            "Emphasis: **bold**, *italic* or _italic_, ++underline++ (combinations such as **_both_** work).",
            "Lists: '- ' for bullet lists, '1. ' for numbered lists at line start.",
            "Links: [text](https://example.com) — only http and https are rendered as links.",
            "Escape a literal marker with a backslash: \\*, \\+, \\[, \\], \\#, \\-.",
            "Each of these fields is limited to 20000 characters and must not contain control characters (except newline and tab)."),
        ["quiz_rules"] = new JsonArray(
            "quiz[] holds the FIXED quiz questions of a card; it is optional (omit it or send [] for none). Old imports that only use single_choice stay valid.",
            "single_choice (default type): exactly 4 non-empty options and a 0-based correct_index.",
            "multiple_response: 3 to 6 non-empty options and correct_indexes (all correct options, 0-based, unique, at least one). A learner answer counts only if exactly this set is selected.",
            "free_text: no options and no correct_index/correct_indexes; explanation is the model answer used for grading.",
            "Every entry needs question and explanation. No other fields are accepted or required.",
            "AI-generated questions and questions created in the editor are stored by the server in the module question pool; they are not part of this import format."),
        ["batch_schema"] = BatchSchema(),
        ["card_schema"] = CardSchema(),
        ["example"] = ExampleBatch(),
    };

    private static JsonObject BatchSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("cards"),
        ["properties"] = new JsonObject
        {
            ["cards"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Canonical LearnCards import payload.",
                ["items"] = CardSchema(),
            },
            ["overwrite_existing"] = new JsonObject
            {
                ["type"] = "boolean",
                ["default"] = false,
                ["description"] = "Updates cards with matching ids when true.",
            },
        },
    };

    private static JsonObject CardSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("module", "category", "term", "question", "definition"),
        ["properties"] = new JsonObject
        {
            ["id"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional stable card id. If omitted, the server creates one.",
            },
            ["module"] = Property("string", "Module name"),
            ["category"] = Property("string", "Category or learning track"),
            ["term"] = Property("string", "Term shown on the card"),
            ["question"] = Property("string", "Prompt shown in learn and quiz mode"),
            ["definition"] = Property("string", "Correct compact answer. Supports a small formatting syntax (see rich_text_rules); plain text stays valid. Max 20000 characters."),
            ["how_it_works"] = Property("string", "Technical explanation. Supports a small formatting syntax (see rich_text_rules); plain text stays valid. Max 20000 characters."),
            ["context"] = Property("string", "Operational or architectural context. Supports a small formatting syntax (see rich_text_rules); plain text stays valid. Max 20000 characters."),
            ["key_fact"] = Property("string", "One memorable fact or exam gotcha. Supports a small formatting syntax (see rich_text_rules); plain text stays valid. Max 20000 characters."),
            ["reference_answer"] = Property("string", "Source-bound reference solution for quiz grading. Supports a small formatting syntax (see rich_text_rules); plain text stays valid. Max 20000 characters."),
            ["chat_prompt"] = Property("string", "Optional system prompt bound to this card"),
            ["slide_number"] = new JsonObject
            {
                ["type"] = "integer",
                ["nullable"] = true,
                ["description"] = "Optional slide position when the card belongs to a presentation deck.",
            },
            ["target_time_sec"] = new JsonObject
            {
                ["type"] = "integer",
                ["nullable"] = true,
                ["description"] = "Optional target speaking time in seconds for slide mode.",
            },
            ["quiz"] = new JsonObject
            {
                ["type"] = "array",
                ["nullable"] = true,
                ["description"] = "Optional quiz questions of this card (fixed questions, stored in the module question pool). Default type is single_choice. multiple_response and free_text are optional additions.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("type", "question", "explanation"),
                    ["properties"] = new JsonObject
                    {
                        ["type"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("single_choice", "multiple_response", "free_text"),
                        },
                        ["question"] = Property("string", "Question text"),
                        ["options"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["maxItems"] = 6,
                            ["description"] = "single_choice: exactly 4 options. multiple_response: 3 to 6 options. free_text: omit.",
                            ["items"] = new JsonObject { ["type"] = "string" },
                        },
                        ["correct_index"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["minimum"] = 0,
                            ["maximum"] = 3,
                            ["description"] = "single_choice only: 0-based index of the correct option.",
                        },
                        ["correct_indexes"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "multiple_response only: 0-based indexes of all correct options (at least one, unique).",
                            ["items"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 5 },
                        },
                        ["explanation"] = Property("string", "Short explanation shown after answering (for free_text: the model answer)"),
                    },
                },
            },
            ["images"] = ImagesSchema(),
            ["official_sources"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Official source directory. Prefer Microsoft Learn or vendor documentation.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("title", "url"),
                    ["properties"] = new JsonObject
                    {
                        ["title"] = Property("string", "Display title of the official source"),
                        ["url"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["format"] = "uri",
                            ["pattern"] = "^https://",
                            ["description"] = "Absolute HTTPS URL",
                        },
                        ["publisher"] = Property("string", "Publisher, e.g. Microsoft Learn"),
                    },
                },
            },
            ["archived"] = new JsonObject
            {
                ["type"] = "boolean",
                ["default"] = false,
            },
            ["sort_order"] = new JsonObject
            {
                ["type"] = "integer",
                ["default"] = 0,
            },
        },
    };

    /// <summary>Beschreibt <c>images[]</c>; Limits kommen aus <see cref="Domain.CardImageLimits"/> (ADR 0016).</summary>
    private static JsonObject ImagesSchema() => new()
    {
        ["type"] = "array",
        ["nullable"] = true,
        ["maxItems"] = Domain.CardImageLimits.MaxImagesPerCard,
        ["description"] =
            $"Optional images shown on the card (at most {Domain.CardImageLimits.MaxImagesPerCard}). Omit the field or send [] for no images. " +
            "On import with overwrite_existing=true, an omitted images field keeps the existing images; [] removes them. " +
            $"Each image must be a real file of type {string.Join(", ", Domain.CardImageLimits.AllowedMimeTypes)}, at most {Domain.CardImageLimits.MaxDecodedBytes / (1024 * 1024)} MiB decoded " +
            "(prefer small images, e.g. below 200 KiB: large Base64 strings consume model context and may exceed client or request limits). " +
            "data_base64 is plain standard Base64 of the file bytes WITHOUT a 'data:' prefix. mime_type must match the real image type. Never invent Base64 data.",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("mime_type", "data_base64"),
            ["properties"] = new JsonObject
            {
                ["mime_type"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(Domain.CardImageLimits.AllowedMimeTypes.Select(m => (JsonNode)m).ToArray()),
                },
                ["data_base64"] = new JsonObject
                {
                    ["type"] = "string",
                    ["contentEncoding"] = "base64",
                    ["description"] = "Standard Base64 of the image file (no data: URI prefix).",
                    ["maxLength"] = Domain.CardImageLimits.MaxBase64Chars,
                },
                ["alt_text"] = new JsonObject
                {
                    ["type"] = "string",
                    ["maxLength"] = Domain.CardImageLimits.MaxTextLength,
                    ["description"] = "Alternative text describing the image (accessibility). Strongly recommended.",
                },
                ["caption"] = new JsonObject
                {
                    ["type"] = "string",
                    ["maxLength"] = Domain.CardImageLimits.MaxTextLength,
                    ["description"] = "Optional caption shown below the image.",
                },
            },
        },
    };

    private static JsonObject ExampleBatch() => new()
    {
        ["cards"] = new JsonArray(new JsonObject
        {
            ["module"] = "Azure Container",
            ["category"] = "Container",
            ["term"] = ":latest-Problem / SHA-Digest Pinning",
            ["question"] = "Warum ist :latest in Produktion gefährlich und was ist die sichere Alternative?",
            ["definition"] = "Bewegliche Tags wie :latest sind nicht reproduzierbar.",
            ["how_it_works"] = "Ein Digest pinnt exakt das ausgelieferte Image.",
            ["context"] = "Wichtig für CI/CD, Rollbacks und Incident Analysis.",
            ["key_fact"] = "Deployments sollten auf **immutable Digests** zeigen.",
            ["reference_answer"] = "In Produktion sollte kein beweglicher Tag wie :latest verwendet werden, weil sich der referenzierte Inhalt ändern kann. Stattdessen wird das Image per SHA-Digest gepinnt, damit Deployments reproduzierbar, nachvollziehbar und sicher bleiben.",
            ["slide_number"] = 3,
            ["target_time_sec"] = 90,
            ["quiz"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "single_choice",
                    ["question"] = "Warum ist ein SHA-Digest in CI/CD stabiler als :latest?",
                    ["options"] = new JsonArray(
                        "Weil :latest nur lokal funktioniert",
                        "Weil ein Digest exakt denselben Image-Inhalt referenziert",
                        "Weil Digests automatisch gepatcht werden",
                        "Weil Digests keine Registry benötigen"),
                    ["correct_index"] = 1,
                    ["explanation"] = "Ein Digest referenziert ein unveränderliches Artefakt und macht Deployments reproduzierbar.",
                },
                new JsonObject
                {
                    ["type"] = "multiple_response",
                    ["question"] = "Welche Aussagen zu Image-Referenzen sind richtig?",
                    ["options"] = new JsonArray(
                        "Ein Digest ist unveränderlich",
                        "Ein Tag kann auf ein anderes Image zeigen",
                        "Ein Digest wird bei jedem Push neu vergeben, ohne dass sich der Inhalt ändert",
                        "Ein Tag ist reproduzierbarer als ein Digest"),
                    ["correct_indexes"] = new JsonArray(0, 1),
                    ["explanation"] = "Digests sind inhaltsbasiert und unveränderlich; Tags sind bewegliche Verweise.",
                },
                new JsonObject
                {
                    ["type"] = "free_text",
                    ["question"] = "Erkläre in zwei Sätzen, warum Produktion Images per Digest pinnen sollte.",
                    ["explanation"] = "Musterlösung: Ein Digest referenziert genau einen Image-Inhalt. Deployments bleiben dadurch reproduzierbar und nachvollziehbar.",
                }),
            ["images"] = new JsonArray(new JsonObject
            {
                ["mime_type"] = "image/png",
                ["data_base64"] = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAQCAIAAAD4YuoOAAAAI0lEQVR42mPg94mhKWIAYq3ls2mERi0YtWDUglELRpIFNEUAOleXE3NVou0AAAAASUVORK5CYII=",
                ["alt_text"] = "Farbige Beispielfläche (32 × 16 Pixel)",
                ["caption"] = "Beispielbild",
            }),
            ["official_sources"] = new JsonArray(new JsonObject
            {
                ["title"] = "Best practices for container image management",
                ["url"] = "https://learn.microsoft.com/azure/container-registry/container-registry-image-tag-version",
                ["publisher"] = "Microsoft Learn",
            }),
        }),
        ["overwrite_existing"] = false,
    };

    private static JsonObject Property(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };
}
