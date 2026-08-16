using System.Text.Json.Nodes;

namespace LearnCards.Web.Api;

public static class CardImportSchema
{
    public static JsonObject Document() => new()
    {
        ["schema_version"] = "2026-08-16",
        ["name"] = "learncards-import-batch",
        ["grounding_rules"] = new JsonArray(
            "Use only facts contained in the cards themselves and their official sources.",
            "Do not invent missing fields or unsupported facts.",
            "If a source-backed answer cannot be formed, ask for clarification instead of guessing."),
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
            ["definition"] = Property("string", "Correct compact answer"),
            ["how_it_works"] = Property("string", "Technical explanation"),
            ["context"] = Property("string", "Operational or architectural context"),
            ["key_fact"] = Property("string", "One memorable fact or exam gotcha"),
            ["reference_answer"] = Property("string", "Source-bound reference solution for quiz grading"),
            ["chat_prompt"] = Property("string", "Optional system prompt bound to this card"),
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
            ["key_fact"] = "Deployments sollten auf immutable Digests zeigen.",
            ["reference_answer"] = "In Produktion sollte kein beweglicher Tag wie :latest verwendet werden, weil sich der referenzierte Inhalt ändern kann. Stattdessen wird das Image per SHA-Digest gepinnt, damit Deployments reproduzierbar, nachvollziehbar und sicher bleiben.",
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
