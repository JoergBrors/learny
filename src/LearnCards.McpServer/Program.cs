using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json.Nodes;
using LearnCards.McpServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

EnvLoader.Load();

const string PendingCookie = "lc_mcp_pending";

var settings = McpSettings.Load();
// ADR 0013: Daten-Aufrufe laufen immer mit dem Token des Benutzers (HTTP: Bearer-Header, stdio: LEARNCARDS_MCP_TOKEN);
// der Dienstschlüssel (apiKey) dient nur dem Tausch von Anmelde-Codes und dem öffentlichen Schema.
using var api = new LearnCardsApiClient(settings.UpstreamApi.BaseUrl, settings.UpstreamApi.ApiKey, settings.UpstreamApi.Token);
var engine = new McpServerEngine(settings.ServerName, settings.ServerVersion);
var oauth = new ProprietaryOAuthService(settings.OAuth);

RegisterCoreTools(engine, api, settings);

var runTasks = new List<Task>();
if (settings.Transports.Http.Enabled)
    runTasks.Add(RunHttpServerAsync(engine, api, settings, oauth));
if (settings.Transports.Stdio.Enabled)
    runTasks.Add(new McpStdioServer(engine).RunAsync());

if (runTasks.Count == 0)
    throw new InvalidOperationException("Mindestens ein MCP-Transport muss aktiviert sein (stdio oder http).");

await Task.WhenAll(runTasks);

static JsonObject? ParseJsonObject(string? text)
{
    if (string.IsNullOrWhiteSpace(text)) return null;
    try { return JsonNode.Parse(text) as JsonObject; } catch (System.Text.Json.JsonException) { return null; }
}

static string TopicResult(string what, JsonNode? topic) =>
    $"{what}: \"{topic?["name"]?.GetValue<string>()}\" (id: {topic?["id"]?.GetValue<string>()}). Nothing was published; check the result with get_topic.";

static void RegisterCoreTools(McpServerEngine engine, LearnCardsApiClient api, McpSettings settings)
{
    engine.RegisterTool(
        new ToolDefinition("get_import_schema",
            "Return the live LearnCards import schema from the upstream server. Use this before creating or importing cards. Do not invent fields that are not present in this schema.",
            """{"type": "object", "properties": {}}"""),
        async (_, ct) =>
        {
            try
            {
                var schema = await api.GetAsync("schema/import/cards", ct);
                return new ToolCallResult(schema?.ToJsonString(new() { WriteIndented = true }) ?? "{}");
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("whoami",
            "Show which LearnCards user this MCP connection acts as. All tools only see this user's own modules and cards.",
            """{"type": "object", "properties": {}}"""),
        async (_, ct) =>
        {
            try
            {
                var me = await api.GetAsync("mcp/whoami", ct);
                return new ToolCallResult($"Connected as {me?["display_name"]?.GetValue<string>() ?? "?"} (user id: {me?["user_id"]?.GetValue<string>() ?? "?"}).");
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("add_card",
            "Create a single learning card in the LearnCards platform. Use only source-grounded facts and preserve the canonical import structure. Optional images[]: each entry needs mime_type (image/png, image/jpeg or image/webp) and data_base64 (plain standard Base64 of the real image file, no 'data:' prefix); alt_text and caption are optional. Limits: max 5 images per card, max 2 MiB per decoded image; prefer small images (< 200 KiB) because Base64 inside tool arguments consumes model context. Only include images you were actually given; never invent Base64 data. Call get_import_schema for the exact, current rules."+
            " Text fields (definition, how_it_works, context, key_fact, reference_answer) accept a small formatting syntax: **bold**, *italic*, ++underline++, '# ' headings, '- ' and '1. ' lists, [text](https://…) links; plain text stays valid; never send HTML (it is shown as plain text)."+
            " Optional quiz[]: fixed quiz questions of the card. type single_choice (default; exactly 4 options + 0-based correct_index), multiple_response (3-6 options + correct_indexes = all correct 0-based indexes) or free_text (no options; explanation = model answer); every entry needs question and explanation. Omit quiz or send [] for none; AI-generated questions are managed by the server, never send them here.",
            CardUpsertSchema()),
        async (args, ct) =>
        {
            try
            {
                var card = await api.PostAsync("cards/", args, ct);
                var term = card?["term"]?.GetValue<string>() ?? "?";
                var id = card?["id"]?.GetValue<string>() ?? "?";
                return new ToolCallResult($"Card created: {term} (id: {id})");
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("import_cards",
            "Import multiple learning cards at once. Always consult get_import_schema first and keep answers grounded in the provided official sources. Cards may contain images[] (mime_type, data_base64, alt_text, caption) as described in the schema: real files only, standard Base64 without a 'data:' prefix, max 5 images per card and 2 MiB per decoded image. Many or large images make the request very large — import such cards in small batches; the server reports rejected cards with a reason in the result. With overwrite_existing=true an omitted images field keeps existing images, [] removes them."+
            " Text fields (definition, how_it_works, context, key_fact, reference_answer) accept a small formatting syntax: **bold**, *italic*, ++underline++, '# ' headings, '- ' and '1. ' lists, [text](https://…) links; plain text stays valid; never send HTML (it is shown as plain text)."+
            " Optional quiz[]: fixed quiz questions of the card. type single_choice (default; exactly 4 options + 0-based correct_index), multiple_response (3-6 options + correct_indexes = all correct 0-based indexes) or free_text (no options; explanation = model answer); every entry needs question and explanation. Omit quiz or send [] for none; AI-generated questions are managed by the server, never send them here.",
            """
            {
              "type": "object",
              "required": ["cards"],
              "properties": {
                "cards": {"type": "array", "description": "Array of card objects in canonical LearnCards JSON format", "items": {"type": "object"}},
                "overwrite_existing": {"type": "boolean", "default": false, "description": "If true, cards with matching IDs are updated"}
              }
            }
            """),
        async (args, ct) =>
        {
            try
            {
                var body = new JsonObject
                {
                    ["cards"] = args["cards"]?.DeepClone() ?? new JsonArray(),
                    ["overwrite_existing"] = args["overwrite_existing"]?.DeepClone() ?? false,
                };
                var res = await api.PostAsync("import/cards", body, ct);
                var created = res?["created"]?.GetValue<int>() ?? 0;
                var updated = res?["updated"]?.GetValue<int>() ?? 0;
                var skipped = res?["skipped"]?.GetValue<int>() ?? 0;
                var errors = res?["errors"] as JsonArray;
                var detail = errors is { Count: > 0 }
                    ? "\nRejected cards:\n" + string.Join("\n", errors.Select(e => "- " + e?.GetValue<string>()))
                    : "";
                return new ToolCallResult($"Import complete: {created} created, {updated} updated, {skipped} skipped.{detail}");
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("list_modules",
            "List the learning modules this connection may use, with card counts. scope: private (default, your own modules), shared (modules other users shared with you), global (published by an administrator) or all. Shared and global modules are read-only.",
            """
            {
              "type": "object",
              "properties": {
                "scope": {"type": "string", "enum": ["private", "shared", "global", "all"], "default": "private", "description": "Which modules to list"}
              }
            }
            """),
        async (args, ct) =>
        {
            try
            {
                var scope = args["scope"]?.GetValue<string>() is { Length: > 0 } sc ? sc : "private";
                var modules = await api.GetAsync($"modules/?scope={Uri.EscapeDataString(scope)}", ct) as JsonArray;
                if (modules is null || modules.Count == 0) return new ToolCallResult("No modules found.");
                var lines = modules.Select(m =>
                {
                    var context = m?["context"]?.GetValue<string>() ?? "private";
                    var owner = m?["owner_name"]?.GetValue<string>() is { Length: > 0 } o ? $", from: {o}" : "";
                    var version = m?["global_version"]?.GetValue<int>() is int v ? $", version {v}" : "";
                    return $"• {m?["name"]?.GetValue<string>()} [{context}{owner}{version}] — {m?["active_count"]?.GetValue<int>()} active / {m?["card_count"]?.GetValue<int>()} total cards (id: {m?["id"]?.GetValue<string>()})";
                });
                return new ToolCallResult(string.Join("\n", lines));
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    // ─── Themen (ADR 0027): nur lesend, im Kontext des angemeldeten Benutzers ───
    engine.RegisterTool(
        new ToolDefinition("list_topics",
            "List the topics (learning paths of several modules with exams) this connection may use: your own, shared with you, assigned to you and global topics, with module, card and question counts.",
            """
            {
              "type": "object",
              "properties": {
                "include_archived": {"type": "boolean", "default": false}
              }
            }
            """),
        async (args, ct) =>
        {
            try
            {
                var archived = args["include_archived"]?.GetValue<bool>() == true ? "true" : "false";
                if (await api.GetAsync($"topics/?include_archived={archived}", ct) is not JsonObject overview) return new ToolCallResult("No topics found.");
                var lines = new List<string>();
                foreach (var (section, label) in new[] { ("own", "own"), ("shared", "shared with you"), ("assigned", "assigned to you"), ("global", "global") })
                    foreach (var t in overview[section]?.AsArray() ?? new JsonArray())
                    {
                        var topic = t?["topic"]; var stats = t?["stats"];
                        lines.Add($"• {topic?["name"]?.GetValue<string>()} [{label}, version {t?["version"]?.GetValue<int>()}] — {stats?["modules"]?.GetValue<int>()} modules, {stats?["cards"]?.GetValue<int>()} cards, {stats?["choice_questions"]?.GetValue<int>()} multiple-choice questions, final exam {stats?["final_exam_questions"]?.GetValue<int>()} questions (id: {topic?["id"]?.GetValue<string>()})");
                    }
                return new ToolCallResult(lines.Count == 0 ? "No topics found." : string.Join("\n", lines));
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("get_topic",
            "Get a topic you may read: description, version, status, the contained modules in learning order (with module ids) and counts. Without version: your draft (own topics) or the newest version you may learn.",
            """
            {
              "type": "object",
              "required": ["topic_id"],
              "properties": {
                "topic_id": {"type": "string"},
                "version": {"type": "integer", "description": "Published version number (optional)"}
              }
            }
            """),
        async (args, ct) =>
        {
            try
            {
                var id = args["topic_id"]?.GetValue<string>() ?? "";
                var version = args["version"]?.GetValue<int>() is int v && v > 0 ? $"?version={v}" : "";
                if (await api.GetAsync($"topics/{Uri.EscapeDataString(id)}{version}", ct) is not JsonObject d) return new ToolCallResult("Topic not found.", true);
                var lines = new List<string>
                {
                    $"{d["topic"]?["name"]?.GetValue<string>()} — {(d["version"]?.GetValue<int>() == 0 ? "draft" : "version " + d["version"]?.GetValue<int>())}, section {d["section"]?.GetValue<string>()}, global status {d["global_status"]?.GetValue<string>()}",
                    d["topic"]?["description"]?.GetValue<string>() ?? "",
                    $"Cards: {d["stats"]?["cards"]?.GetValue<int>()}, multiple-choice questions: {d["stats"]?["choice_questions"]?.GetValue<int>()}, final exam questions: {d["stats"]?["final_exam_questions"]?.GetValue<int>()}",
                    "Modules (in order):",
                };
                foreach (var m in d["modules"]?.AsArray() ?? new JsonArray())
                    lines.Add($"  {m?["position"]?.GetValue<int>() + 1}. {m?["name"]?.GetValue<string>()} — {m?["cards"]?.GetValue<int>()} cards, {m?["pool_questions"]?.GetValue<int>()} questions (module id: {m?["module_id"]?.GetValue<string>()})");
                return new ToolCallResult(string.Join("\n", lines));
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("get_topic_exam_structure",
            "Get the exam structure of a topic you may read: module exams (enabled, question count, pass percent, retries) and the final exam (question count, pass percent, distribution, unlock rules). Solutions are never returned.",
            """
            {
              "type": "object",
              "required": ["topic_id"],
              "properties": {
                "topic_id": {"type": "string"},
                "version": {"type": "integer", "description": "Published version number (optional)"}
              }
            }
            """),
        async (args, ct) =>
        {
            try
            {
                var id = args["topic_id"]?.GetValue<string>() ?? "";
                var version = args["version"]?.GetValue<int>() is int v && v > 0 ? $"?version={v}" : "";
                if (await api.GetAsync($"topics/{Uri.EscapeDataString(id)}{version}", ct) is not JsonObject d) return new ToolCallResult("Topic not found.", true);
                var lines = new List<string> { $"Exam structure of {d["topic"]?["name"]?.GetValue<string>()} ({(d["version"]?.GetValue<int>() == 0 ? "draft" : "version " + d["version"]?.GetValue<int>())}):" };
                foreach (var m in d["modules"]?.AsArray() ?? new JsonArray())
                {
                    var e = m?["exam"];
                    lines.Add(e?["enabled"]?.GetValue<bool>() == true
                        ? $"  Module exam {m?["name"]?.GetValue<string>()}: {e?["question_count"]?.GetValue<int>()} questions, pass at {e?["pass_percent"]?.GetValue<int>()} %, retries {(e?["allow_retry"]?.GetValue<bool>() == true ? "allowed" : "not allowed")}"
                        : $"  Module exam {m?["name"]?.GetValue<string>()}: not active");
                }
                var f = d["final_exam"];
                lines.Add(f?["enabled"]?.GetValue<bool>() == true
                    ? $"  Final exam: {f?["question_count"]?.GetValue<int>()} questions ({f?["distribution"]?.GetValue<string>()} distribution), pass at {f?["pass_percent"]?.GetValue<int>()} %, available pool {d["stats"]?["final_exam_pool"]?.GetValue<int>()} questions"
                    : "  Final exam: not active");
                return new ToolCallResult(string.Join("\n", lines));
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    // ─── Themen- und Modulverwaltung per JSON (ADR 0028): nur eigene Themenentwürfe und eigene private Module ───
    engine.RegisterTool(
        new ToolDefinition("get_topic_import_schema",
            "Return the live JSON schema, rules and an example of the portable topic definition (format learny-topic). Call this before create_topic, update_topic or import_topic. Do not invent fields that are not in the schema.",
            """{"type": "object", "properties": {}}"""),
        async (_, ct) =>
        {
            try { return new ToolCallResult((await api.GetAsync("schema/import/topic", ct))?.ToJsonString(new() { WriteIndented = true }) ?? "{}"); }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("get_module_import_schema",
            "Return the live JSON schema, rules and an example of the portable full module definition (format learny-module: cards with images and quiz, quiz configuration, extra pool questions). The plain card batch format stays available through get_import_schema.",
            """{"type": "object", "properties": {}}"""),
        async (_, ct) =>
        {
            try { return new ToolCallResult((await api.GetAsync("schema/import/module", ct))?.ToJsonString(new() { WriteIndented = true }) ?? "{}"); }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    const string TopicDefinitionArg = """
        {"type": "object", "description": "Portable topic definition, exactly as described by get_topic_import_schema (format 'learny-topic', schema_version 1). Modules are referenced by the name of one of YOUR OWN private modules (use list_modules). Global modules must be cloned first (clone_global_module).", "required": ["format", "schema_version", "name", "modules"]}
        """;

    engine.RegisterTool(
        new ToolDefinition("create_topic",
            "Create a NEW private topic (learning path) as a DRAFT from a validated JSON definition. Only your own private modules can be referenced (by name); nothing is published and nobody gets access. Fails without partial changes if the JSON, a module reference or an exam setting is invalid. Workflow: get_topic_import_schema → list_modules → create_topic → get_topic.",
            "{\"type\": \"object\", \"required\": [\"definition\"], \"properties\": {\"definition\": " + TopicDefinitionArg + "}}"),
        async (args, ct) =>
        {
            try
            {
                if (args["definition"] is not JsonObject def) return new ToolCallResult("'definition' (object) is required. Call get_topic_import_schema.", true);
                return new ToolCallResult(TopicResult("Topic created as draft", await api.PostAsync("topics/import", def.DeepClone(), ct)));
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("import_topic",
            "Import a portable topic definition given as JSON TEXT (for example the output of export_topic) and create a private DRAFT topic from it. Same rules and validation as create_topic. Definitions with embedded modules (export_topic with include_modules=true) create the modules as new private modules of yours.",
            """
            {"type": "object", "required": ["definition_json"], "properties": {"definition_json": {"type": "string", "description": "The complete learny-topic JSON document as text."}}}
            """),
        async (args, ct) =>
        {
            try
            {
                if (ParseJsonObject(args["definition_json"]?.GetValue<string>()) is not { } def) return new ToolCallResult("'definition_json' must be a JSON object as text.", true);
                return new ToolCallResult(TopicResult("Topic imported as draft", await api.PostAsync("topics/import", def, ct)));
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("update_topic",
            "Replace the DRAFT of one of YOUR OWN topics with a complete JSON definition (name, description, modules in order, exams, topic questions). Published versions are never changed and nothing is published — a new version only arises when the owner publishes in the web UI. Omitted modules are removed from the draft. On errors the previous draft stays untouched.",
            "{\"type\": \"object\", \"required\": [\"topic_id\", \"definition\"], \"properties\": {\"topic_id\": {\"type\": \"string\"}, \"definition\": " + TopicDefinitionArg + "}}"),
        async (args, ct) =>
        {
            try
            {
                var id = args["topic_id"]?.GetValue<string>() ?? "";
                if (args["definition"] is not JsonObject def) return new ToolCallResult("'definition' (object) is required. Call get_topic_import_schema.", true);
                return new ToolCallResult(TopicResult("Topic draft updated", await api.PutAsync($"topics/{Uri.EscapeDataString(id)}/definition", def.DeepClone(), ct)));
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("export_topic",
            "Export one of YOUR OWN topics (draft) as a portable JSON definition: modules in order, module and final exams, topic questions. No ids, owners, versions, shares, assignments or results. With include_modules=true the complete module definitions (cards, images, quiz) are embedded.",
            """
            {"type": "object", "required": ["topic_id"], "properties": {"topic_id": {"type": "string"}, "include_modules": {"type": "boolean", "default": false}}}
            """),
        async (args, ct) =>
        {
            try
            {
                var id = args["topic_id"]?.GetValue<string>() ?? "";
                var inc = args["include_modules"]?.GetValue<bool>() == true ? "true" : "false";
                return new ToolCallResult((await api.GetAsync($"topics/{Uri.EscapeDataString(id)}/export?include_modules={inc}", ct))?.ToJsonString(new() { WriteIndented = true }) ?? "{}");
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("clone_global_module",
            "Clone an active GLOBAL module into a new private module of yours ('Name (Kopie)') with all cards, images, card quiz questions and quiz configuration. The original stays unchanged and nothing is shared. Afterwards the copy can be used in topics.",
            """
            {"type": "object", "required": ["module_id"], "properties": {"module_id": {"type": "string", "description": "Id of a global module (list_modules with scope=global)."}}}
            """),
        async (args, ct) =>
        {
            try
            {
                var id = args["module_id"]?.GetValue<string>() ?? "";
                var m = await api.PostAsync($"modules/{Uri.EscapeDataString(id)}/clone", new JsonObject(), ct);
                return new ToolCallResult($"Cloned into your own module \"{m?["name"]?.GetValue<string>()}\" ({m?["card_count"]?.GetValue<int>()} cards, id: {m?["id"]?.GetValue<string>()}).");
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("export_module",
            "Export one of YOUR OWN modules (or an active global module) as a complete portable JSON definition (format learny-module): cards with images, fixed quiz questions, quiz configuration and extra pool questions. Images are Base64 and can make the result very large.",
            """
            {"type": "object", "required": ["module_id"], "properties": {"module_id": {"type": "string"}}}
            """),
        async (args, ct) =>
        {
            try
            {
                var id = args["module_id"]?.GetValue<string>() ?? "";
                return new ToolCallResult((await api.GetAsync($"modules/{Uri.EscapeDataString(id)}/export", ct))?.ToJsonString(new() { WriteIndented = true }) ?? "{}");
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("import_module",
            "Create a NEW private module of yours from a complete module definition (format learny-module, see get_module_import_schema). Never overwrites an existing module (a name clash becomes 'Name (Import)'). All ids are new; the import is all-or-nothing. Use import_cards for plain card batches.",
            """
            {"type": "object", "required": ["definition"], "properties": {"definition": {"type": "object", "description": "learny-module definition, exactly as described by get_module_import_schema.", "required": ["format", "schema_version", "name", "cards"]}}}
            """),
        async (args, ct) =>
        {
            try
            {
                if (args["definition"] is not JsonObject def) return new ToolCallResult("'definition' (object) is required. Call get_module_import_schema.", true);
                var m = await api.PostAsync("modules/import", def.DeepClone(), ct);
                return new ToolCallResult($"Module \"{m?["name"]?.GetValue<string>()}\" created ({m?["card_count"]?.GetValue<int>()} cards, id: {m?["id"]?.GetValue<string>()}).");
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    // ─── Karten vollständig lesen, ändern und exportieren (ADR 0007 Ergänzung): nur über die bestehenden API-Endpunkte ───
    engine.RegisterTool(
        new ToolDefinition("get_card",
            "Read ONE learning card completely (module name, category, term, question, definition, how_it_works, context, key_fact, reference_answer, chat_prompt, official_sources, slide_number, target_time_sec, quiz[], images[], archived, sort_order, updated_at, plus module_context = private|shared|global|topic and can_edit). Works for cards of your own modules, modules shared with you, global modules and topic modules you may learn; other users' private cards are 'not found'. By default images[] include real Base64 data (data_base64) so the card can be edited and written back with update_card; set include_image_data=false for metadata only. Internal storage keys and storage URLs are never returned. Archived cards are only visible for your own modules.",
            """
            {
              "type": "object",
              "required": ["card_id"],
              "properties": {
                "card_id": {"type": "string", "description": "Card id (from list_cards or export_cards)."},
                "include_image_data": {"type": "boolean", "default": true, "description": "true (default): images[] contain data_base64. false: metadata only (image_id, mime_type, size_bytes, alt_text, caption, sort_order)."}
              }
            }
            """),
        async (args, ct) =>
        {
            try
            {
                var id = args["card_id"]?.GetValue<string>() ?? "";
                var data = args["include_image_data"]?.GetValue<bool>() == false ? "false" : "true";
                var card = await api.GetAsync($"cards/{Uri.EscapeDataString(id)}?include_image_data={data}", ct);
                return new ToolCallResult(card?.ToJsonString(new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "{}");
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("update_card",
            "Update ONE existing card in one of YOUR OWN private modules (cards of shared, global or topic modules and other users' cards cannot be changed — you get a 404 error, code card_read_only for readable cards). Call get_import_schema first and never invent fields. Workflow: get_card → edit → update_card. card_definition is the COMPLETE new card in the canonical card JSON (category, term, question, definition, how_it_works, context, key_fact, reference_answer, chat_prompt, official_sources, slide_number, target_time_sec, quiz, images, archived, sort_order); fields you leave out are reset to their empty defaults, so always send the whole card as returned by get_card (the fields id, module, context, can_edit, created_at, updated_at in it are ignored; the card stays in its module). Text fields accept the safe formatting syntax of the schema (never HTML). quiz[] replaces the fixed quiz questions of the card (omit or [] = none). IMAGES: omit the images field = existing images are KEPT; images=[] = ALL images are REMOVED (only when explicitly wanted); images=[...] replaces the images — entries need mime_type + data_base64 (real PNG/JPEG/WebP, max 5 per card, max 2 MiB each) or only an image_id of an existing image of this card to keep it. archived and sort_order are part of the card: send archived=false/true deliberately. For concurrent edits pass expected_updated_at (updated_at from get_card): if the card changed meanwhile you get a conflict error — re-read the card and merge instead of overwriting.",
            """
            {
              "type": "object",
              "required": ["card_id", "card_definition"],
              "properties": {
                "card_id": {"type": "string"},
                "card_definition": {"type": "object", "description": "Complete canonical card JSON exactly as described by get_import_schema (card_schema). 'module' is optional here.", "required": ["category", "term", "question", "definition"]},
                "expected_updated_at": {"type": "string", "description": "Optional: updated_at value from get_card. Enables conflict detection (409)."}
              }
            }
            """),
        async (args, ct) =>
        {
            try
            {
                var id = args["card_id"]?.GetValue<string>() ?? "";
                if (args["card_definition"] is not JsonObject def) return new ToolCallResult("'card_definition' (object) is required. Call get_import_schema and get_card first.", true);
                var headers = args["expected_updated_at"]?.GetValue<string>() is { Length: > 0 } exp ? new Dictionary<string, string> { ["X-Card-Updated-At"] = exp } : null;
                var card = await api.PutAsync($"cards/{Uri.EscapeDataString(id)}", def.DeepClone(), ct, headers);
                return new ToolCallResult($"Card updated: {card?["term"]?.GetValue<string>()} (id: {card?["id"]?.GetValue<string>()}, images: {card?["images"]?.AsArray().Count ?? 0}, quiz questions: {card?["quiz"]?.AsArray().Count ?? 0}, updated_at: {card?["updated_at"]?.ToJsonString().Trim('"')}). Use get_card to verify.");
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("export_cards",
            "Export the cards of a module (your own, or an active global module) as canonical card JSON {\"cards\":[…]} that can be fed back into import_cards (pass the cards array unchanged). Every card contains all schema fields incl. official_sources, rich-text, quiz[], images[] with real Base64 data, sort_order and archived. Card ids are included for your own modules (import_cards with overwrite_existing=true then updates the same cards; without it existing ids are skipped), global modules export without ids. No owner information is exported. Large modules are exported in BATCHES: each call returns at most `limit` cards (default 50, max 200) and stops earlier when the images of the batch exceed about 4 MiB of Base64 (at least one card is always returned). The result's \"batch\" object reports total, offset, count, has_more and next_offset — repeat the call with offset=next_offset until has_more is false. Order is stable (sort_order, category, term). Archived cards are only included with include_archived=true for your own modules. Modules shared with you by other users cannot be exported. Call get_import_schema for the exact format.",
            """
            {
              "type": "object",
              "required": ["module_id"],
              "properties": {
                "module_id": {"type": "string"},
                "include_archived": {"type": "boolean", "default": false, "description": "Also export archived cards (own modules only). Archived cards keep archived=true."},
                "offset": {"type": "integer", "minimum": 0, "default": 0, "description": "Start position; use next_offset of the previous batch."},
                "limit": {"type": "integer", "minimum": 1, "maximum": 200, "default": 50, "description": "Maximum cards per batch."}
              }
            }
            """),
        async (args, ct) =>
        {
            try
            {
                var id = args["module_id"]?.GetValue<string>() ?? "";
                var query = $"modules/{Uri.EscapeDataString(id)}/export/cards?include_archived={(args["include_archived"]?.GetValue<bool>() == true ? "true" : "false")}";
                if (args["offset"]?.GetValue<int>() is int off && off > 0) query += $"&offset={off}";
                if (args["limit"]?.GetValue<int>() is int lim && lim > 0) query += $"&limit={lim}";
                if (await api.GetAsync(query, ct) is not JsonObject r) return new ToolCallResult("Module not found.", true);
                var result = new JsonObject
                {
                    ["cards"] = r["cards"]?.DeepClone() ?? new JsonArray(),
                    ["batch"] = new JsonObject
                    {
                        ["module"] = r["module"]?.DeepClone(), ["total"] = r["total"]?.DeepClone(), ["offset"] = r["offset"]?.DeepClone(), ["count"] = r["count"]?.DeepClone(),
                        ["has_more"] = r["has_more"]?.DeepClone(), ["next_offset"] = r["next_offset"]?.DeepClone(),
                        ["note"] = "Pass the 'cards' array to import_cards. Repeat export_cards with offset=next_offset while has_more is true.",
                    },
                };
                return new ToolCallResult(result.ToJsonString(new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("list_cards",
            "List cards in a module you may read (your own, shared with you, or global), optionally filtered by category. Includes official source count so clients can prefer source-backed cards. Archived cards are only listed for your own modules.",
            """
            {
              "type": "object",
              "required": ["module_id"],
              "properties": {
                "module_id": {"type": "string"},
                "category": {"type": "string", "description": "Filter by category (optional)"},
                "archived": {"type": "boolean", "default": false}
              }
            }
            """),
        async (args, ct) =>
        {
            try
            {
                var moduleId = args["module_id"]?.GetValue<string>() ?? "";
                var query = $"cards/?module_id={Uri.EscapeDataString(moduleId)}";
                if (args["category"]?.GetValue<string>() is { Length: > 0 } cat) query += $"&category={Uri.EscapeDataString(cat)}";
                if (args["archived"]?.GetValue<bool>() is true) query += "&archived=true";

                var cards = await api.GetAsync(query, ct) as JsonArray;
                if (cards is null || cards.Count == 0) return new ToolCallResult("No cards found.");
                var lines = cards.Select(c =>
                {
                    var sourceCount = c?["official_sources"]?.AsArray().Count ?? 0;
                    var hasReference = !string.IsNullOrWhiteSpace(c?["reference_answer"]?.GetValue<string>());
                    return $"• [{c?["category"]?.GetValue<string>()}] {c?["term"]?.GetValue<string>()} (id: {c?["id"]?.GetValue<string>()}, sources: {sourceCount}, reference: {(hasReference ? "yes" : "no")})";
                });
                return new ToolCallResult(string.Join("\n", lines));
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("archive_card",
            "Archive a card (hides it from active learning, retains it in the archive).",
            """{"type": "object", "required": ["card_id"], "properties": {"card_id": {"type": "string"}}}"""),
        async (args, ct) =>
        {
            try
            {
                var id = args["card_id"]?.GetValue<string>() ?? "";
                await api.PatchAsync($"cards/{Uri.EscapeDataString(id)}/archive", ct);
                return new ToolCallResult($"Card {id} archived.");
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("restore_card",
            "Restore a previously archived card to active learning.",
            """{"type": "object", "required": ["card_id"], "properties": {"card_id": {"type": "string"}}}"""),
        async (args, ct) =>
        {
            try
            {
                var id = args["card_id"]?.GetValue<string>() ?? "";
                await api.PatchAsync($"cards/{Uri.EscapeDataString(id)}/restore", ct);
                return new ToolCallResult($"Card {id} restored.");
            }
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });
}

static async Task RunHttpServerAsync(McpServerEngine engine, LearnCardsApiClient api, McpSettings settings, ProprietaryOAuthService oauth)
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls(settings.Transports.Http.Urls.ToArray());
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("mcp-http", policy =>
            policy
                .AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod());
    });
    builder.Services.AddSingleton(engine);
    builder.Services.AddSingleton(api);
    builder.Services.AddSingleton(settings);
    builder.Services.AddSingleton(oauth);

    var app = builder.Build();
    app.UseCors("mcp-http");

    app.MapGet("/health", (McpSettings cfg) => Results.Json(new
    {
        status = "ok",
        name = cfg.ServerName,
        version = cfg.ServerVersion,
        transports = new
        {
            stdio = cfg.Transports.Stdio.Enabled,
            http = cfg.Transports.Http.Enabled,
        },
    }));

    // Startseite: statt 404 auf "/" eine kurze Übersicht der Endpunkte (nützlich beim Debuggen im Browser)
    app.MapGet("/", (McpSettings cfg) => Results.Json(new
    {
        name = cfg.ServerName,
        version = cfg.ServerVersion,
        endpoints = new
        {
            health = "/health",
            mcp = "/mcp (POST, JSON-RPC, Authorization: Bearer <Learny-Token>)",
            metadata = "/metadata",
            import_schema = "/schema/import/cards",
            oauth_metadata = "/.well-known/oauth-authorization-server",
        },
    }));

    // ─── OAuth-Discovery ────────────────────────────────────────────────
    app.MapGet("/.well-known/oauth-protected-resource", (HttpRequest request, ProprietaryOAuthService auth, McpSettings cfg) =>
        auth.IsEnabled ? Results.Json(ProtectedResourceMetadata(request, cfg, null)) : Results.NotFound());

    app.MapGet("/.well-known/oauth-protected-resource/{**resourcePath}", (HttpRequest request, ProprietaryOAuthService auth, McpSettings cfg, string? resourcePath) =>
        auth.IsEnabled ? Results.Json(ProtectedResourceMetadata(request, cfg, resourcePath)) : Results.NotFound());

    app.MapGet("/.well-known/oauth-authorization-server", (HttpRequest request, ProprietaryOAuthService auth, McpSettings cfg) =>
        auth.IsEnabled ? Results.Json(auth.AuthorizationServerMetadata(MetadataIssuer(request, cfg))) : Results.NotFound());

    app.MapGet("/.well-known/openid-configuration", (HttpRequest request, ProprietaryOAuthService auth, McpSettings cfg) =>
        auth.IsEnabled ? Results.Json(auth.AuthorizationServerMetadata(MetadataIssuer(request, cfg))) : Results.NotFound());

    // ─── OAuth: Authorize → Learny-Anmeldung/Zustimmung → Callback → Token ─────
    // Die Pfade ohne "/oauth"-Präfix bleiben für Clients erhalten, die keine Metadaten auswerten.
    foreach (var prefix in new[] { "/oauth", "" })
    {
        app.MapGet(prefix + "/authorize", async (HttpRequest request, HttpResponse response, ProprietaryOAuthService auth, McpSettings cfg, CancellationToken ct) =>
        {
            if (!auth.IsEnabled)
                return Results.NotFound();
            await auth.EnsureClientAsync(request.Query["client_id"].FirstOrDefault(), api, ct);   // nach einem Neustart: Client aus Learny nachladen (ADR 0025)

            var query = request.Query;
            if (!string.Equals(query["response_type"].FirstOrDefault(), "code", StringComparison.Ordinal))
                return Results.BadRequest("unsupported_response_type");

            OAuthAuthorizeRequest authorizeRequest;
            try
            {
                authorizeRequest = auth.ValidateAuthorizationRequest(
                    query["client_id"].FirstOrDefault() ?? "",
                    query["redirect_uri"].FirstOrDefault() ?? "",
                    query["scope"].FirstOrDefault(),
                    query["state"].FirstOrDefault(),
                    query["code_challenge"].FirstOrDefault(),
                    query["code_challenge_method"].FirstOrDefault(),
                    query["resource"].FirstOrDefault(),
                    MetadataIssuer(request, cfg));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }

            // Die Anmeldung erfolgt bei Learny (Entra ID). Das Cookie bindet die Rückleitung an genau diesen Browser
            // (Schutz davor, dass ein Angreifer sein Autorisierungsverfahren mit der Zustimmung eines Opfers abschließt).
            var pendingId = auth.CreatePending(authorizeRequest);
            response.Cookies.Append(PendingCookie, pendingId, new CookieOptions
            {
                HttpOnly = true,
                Secure = request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(10),
                Path = "/",
            });

            var clientName = string.IsNullOrWhiteSpace(authorizeRequest.Client.ClientName) ? authorizeRequest.Client.ClientId : authorizeRequest.Client.ClientName;
            var connectUrl = settings.UpstreamApi.WebPublicUrl.TrimEnd('/') + "/mcp/connect"
                + "?return=" + Uri.EscapeDataString(MetadataIssuer(request, cfg) + "/oauth/callback")
                + "&state=" + Uri.EscapeDataString(pendingId)
                + "&client=" + Uri.EscapeDataString(clientName ?? "");
            return Results.Redirect(connectUrl);
        });

        app.MapPost(prefix + "/register", async (HttpRequest request, ProprietaryOAuthService auth, CancellationToken ct) =>
        {
            if (!auth.IsEnabled)
                return Results.NotFound();

            JsonObject? body;
            try { body = await JsonNode.ParseAsync(request.Body) as JsonObject; }
            catch { return Results.Json(new { error = "invalid_client_metadata" }, statusCode: 400); }
            if (body is null)
                return Results.Json(new { error = "invalid_client_metadata" }, statusCode: 400);

            try
            {
                var redirectUris = body["redirect_uris"]?.AsArray()
                    .Select(n => n?.GetValue<string>() ?? "")
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToArray() ?? Array.Empty<string>();
                var scopes = body["scope"]?.GetValue<string>()?
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    ?? Array.Empty<string>();

                var client = auth.RegisterClient(
                    body["client_name"]?.GetValue<string>(),
                    redirectUris,
                    body["token_endpoint_auth_method"]?.GetValue<string>() ?? "client_secret_post",
                    scopes,
                    body["application_type"]?.GetValue<string>() ?? "web");
                if (client.TokenEndpointAuthMethod == "none")
                {
                    // Dauerhaft (verschlüsselt) in Learny speichern, damit der Client einen Neustart überlebt; bei Ausfall bleibt der Client im Speicher gültig
                    try { await api.StoreOAuthClientAsync(client, ct); }
                    catch (Exception ex) when (ex is McpApiException or HttpRequestException or TaskCanceledException)
                    {
                        app.Logger.LogWarning("OAuth-Client {ClientId} konnte nicht dauerhaft gespeichert werden ({Type}); er gilt bis zum Neustart.", client.ClientId, ex.GetType().Name);
                    }
                }
                return Results.Json(new
                {
                    client_id = client.ClientId,
                    client_secret = client.ClientSecret ?? "",
                    client_secret_expires_at = 0,
                    client_id_issued_at = client.IssuedAt.ToUnixTimeSeconds(),
                    redirect_uris = client.RedirectUris,
                    token_endpoint_auth_method = client.TokenEndpointAuthMethod,
                    grant_types = new[] { "authorization_code" },
                    response_types = new[] { "code" },
                    scope = string.Join(' ', client.Scopes),
                    application_type = client.ApplicationType,
                    client_name = client.ClientName,
                }, statusCode: 201);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException or FormatException)
            {
                return Results.Json(new { error = ex is InvalidOperationException ? ex.Message : "invalid_client_metadata" }, statusCode: 400);
            }
        });

        app.MapPost(prefix + "/token", async (HttpRequest request, HttpResponse response, ProprietaryOAuthService auth, McpSettings cfg) =>
        {
            if (!auth.IsEnabled)
                return Results.NotFound();

            response.Headers.CacheControl = "no-store";
            string? ReadField(IFormCollection? form, JsonNode? json, string name) =>
                form is not null ? form[name].FirstOrDefault() : json?[name]?.GetValue<string>();

            IFormCollection? formData = null;
            JsonNode? jsonData = null;
            try
            {
                if (request.HasFormContentType) formData = await request.ReadFormAsync();
                else jsonData = await JsonNode.ParseAsync(request.Body);
            }
            catch { return Results.Json(new { error = "invalid_request" }, statusCode: 400); }

            if (ReadField(formData, jsonData, "grant_type") != "authorization_code")
                return Results.Json(new { error = "unsupported_grant_type" }, statusCode: 400);

            try
            {
                var token = auth.ExchangeAuthorizationCode(
                    ReadField(formData, jsonData, "client_id") ?? "",
                    ReadField(formData, jsonData, "client_secret"),
                    ReadField(formData, jsonData, "code") ?? "",
                    ReadField(formData, jsonData, "redirect_uri") ?? "",
                    ReadField(formData, jsonData, "code_verifier"),
                    ReadField(formData, jsonData, "resource"),
                    MetadataIssuer(request, cfg));
                return Results.Json(new
                {
                    access_token = token.AccessToken,
                    token_type = token.TokenType,
                    expires_in = token.ExpiresIn,
                    scope = token.Scope,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: ex.Message == "invalid_client" ? 401 : 400);
            }
        });
    }

    // Rückleitung von Learny nach der Zustimmung des Benutzers: Code gegen das benutzergebundene Token tauschen,
    // danach eigenen Autorisierungscode an den MCP-Client ausgeben.
    app.MapGet("/oauth/callback", async (HttpRequest request, HttpResponse response, ProprietaryOAuthService auth, LearnCardsApiClient client, CancellationToken ct) =>
    {
        if (!auth.IsEnabled)
            return Results.NotFound();

        var state = request.Query["state"].FirstOrDefault() ?? "";
        var cookie = request.Cookies[PendingCookie] ?? "";
        response.Cookies.Delete(PendingCookie, new CookieOptions { Path = "/" });

        // Die Zustimmung muss im selben Browser enden, der die Autorisierung begonnen hat
        if (state.Length == 0 || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(state), System.Text.Encoding.UTF8.GetBytes(cookie)))
            return Results.Text("Ungültige oder abgelaufene Autorisierungssitzung. Bitte die Verbindung im MCP-Client erneut starten.", statusCode: 400);

        var pending = auth.TakePending(state);
        if (pending is null)
            return Results.Text("Autorisierungssitzung unbekannt oder abgelaufen. Bitte die Verbindung im MCP-Client erneut starten.", statusCode: 400);

        if (request.Query["error"].FirstOrDefault() is { Length: > 0 } error)
            return Results.Redirect(AppendAuthorizeResult(pending.RedirectUri, null, pending.State, error == "access_denied" ? "access_denied" : "server_error"));

        try
        {
            var exchanged = await client.ExchangeLinkCodeAsync(request.Query["code"].FirstOrDefault() ?? "", ct);
            var userToken = exchanged?["access_token"]?.GetValue<string>();
            var expiresIn = exchanged?["expires_in"]?.GetValue<int>() ?? 0;
            if (string.IsNullOrEmpty(userToken))
                return Results.Redirect(AppendAuthorizeResult(pending.RedirectUri, null, pending.State, "server_error"));

            var code = auth.IssueAuthorizationCode(pending, userToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
            return Results.Redirect(AppendAuthorizeResult(pending.RedirectUri, code, pending.State, null));
        }
        catch (McpApiException)
        {
            return Results.Redirect(AppendAuthorizeResult(pending.RedirectUri, null, pending.State, "access_denied"));
        }
    });

    app.MapGet("/metadata", async (HttpRequest request, McpServerEngine server, ProprietaryOAuthService auth, LearnCardsApiClient client, CancellationToken ct) =>
    {
        JsonNode? schema = null;
        try { schema = await client.GetAsync("schema/import/cards", ct); } catch { }
        var authMetadata = auth.IsEnabled
            ? auth.AuthorizationServerMetadata(MetadataIssuer(request, settings))
            : new JsonObject { ["mode"] = "bearer-token" };
        return Results.Json(server.CreateServerMetadata(authMetadata, schema));
    });

    app.MapGet("/schema/import/cards", async (LearnCardsApiClient client, CancellationToken ct) =>
    {
        var schema = await client.GetAsync("schema/import/cards", ct);
        return Results.Text(schema?.ToJsonString(new() { WriteIndented = true }) ?? "{}", "application/json");
    });

    app.MapPost("/mcp", async (HttpRequest request, HttpResponse response, McpServerEngine server, LearnCardsApiClient client, CancellationToken ct) =>
    {
        // Jeder Aufruf benötigt ein gültiges, benutzergebundenes Learny-Token; es wird vorab gegen die API geprüft,
        // damit widerrufene/abgelaufene Zugänge sofort mit 401 (→ erneute Anmeldung im Client) abgewiesen werden.
        var token = BearerToken(request);
        if (token is null || !await TokenIsValidAsync(client, token, ct))
        {
            response.StatusCode = 401;
            response.Headers.WWWAuthenticate = settings.OAuth.Enabled
                ? $"Bearer resource_metadata=\"{ProtectedResourceMetadataUrl(request, settings, "mcp")}\""
                : "Bearer";
            return Results.Empty;
        }
        McpUserContext.SetHttpRequest(token);

        JsonNode? rpcRequest;
        try
        {
            rpcRequest = await JsonNode.ParseAsync(request.Body, cancellationToken: ct);
        }
        catch
        {
            return Results.Json(new { jsonrpc = "2.0", error = new { code = -32700, message = "Parse error" } }, statusCode: 400);
        }

        var rpcResponse = await server.HandleRequestAsync(rpcRequest, ct);
        if (rpcResponse is null)
            return Results.StatusCode((int)HttpStatusCode.Accepted);

        return Results.Text(rpcResponse.ToJsonString(), "application/json");
    });

    await app.RunAsync();
}

static string? BearerToken(HttpRequest request)
{
    var header = request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && header.Length > 7 ? header[7..].Trim() : null;
}

static async Task<bool> TokenIsValidAsync(LearnCardsApiClient client, string token, CancellationToken ct)
{
    try
    {
        await client.WhoAmIAsync(token, ct);
        return true;
    }
    catch (McpApiException)
    {
        return false;
    }
    catch (HttpRequestException)
    {
        return false;   // Learny nicht erreichbar → kein Zugriff (fail closed)
    }
}

static string BaseUrl(HttpRequest request) => $"{request.Scheme}://{request.Host}";

static string MetadataIssuer(HttpRequest request, McpSettings settings)
{
    if (Uri.TryCreate(settings.OAuth.Issuer, UriKind.Absolute, out var issuer)
        && (issuer.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || issuer.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
    {
        return issuer.ToString().TrimEnd('/');
    }

    return BaseUrl(request);
}

static JsonObject ProtectedResourceMetadata(HttpRequest request, McpSettings settings, string? resourcePath)
{
    var issuer = MetadataIssuer(request, settings);
    var normalizedPath = string.IsNullOrWhiteSpace(resourcePath) ? "mcp" : resourcePath.Trim('/');
    var resource = issuer.TrimEnd('/') + "/" + normalizedPath;
    return new JsonObject
    {
        ["resource"] = resource,
        ["authorization_servers"] = new JsonArray(issuer),
        ["bearer_methods_supported"] = new JsonArray("header"),
        ["scopes_supported"] = new JsonArray("mcp"),
    };
}

static string ProtectedResourceMetadataUrl(HttpRequest request, McpSettings settings, string? resourcePath)
{
    var issuer = MetadataIssuer(request, settings);
    return string.IsNullOrWhiteSpace(resourcePath)
        ? issuer.TrimEnd('/') + "/.well-known/oauth-protected-resource"
        : issuer.TrimEnd('/') + "/.well-known/oauth-protected-resource/" + resourcePath.Trim('/');
}

static string AppendAuthorizeResult(string redirectUri, string? code, string? state, string? error)
{
    var uri = new UriBuilder(redirectUri);
    var query = new List<string>();
    if (!string.IsNullOrWhiteSpace(uri.Query))
        query.Add(uri.Query.TrimStart('?'));
    if (!string.IsNullOrWhiteSpace(code))
        query.Add("code=" + Uri.EscapeDataString(code));
    if (!string.IsNullOrWhiteSpace(state))
        query.Add("state=" + Uri.EscapeDataString(state));
    if (!string.IsNullOrWhiteSpace(error))
        query.Add("error=" + Uri.EscapeDataString(error));
    uri.Query = string.Join("&", query);
    return uri.Uri.ToString();
}

static string CardUpsertSchema() =>
    """
    {
      "type": "object",
      "required": ["module", "category", "term", "question", "definition"],
      "properties": {
        "module": {"type": "string", "description": "Module name (created if it doesn't exist)"},
        "category": {"type": "string", "description": "Thematic group, e.g. Netzwerk, Security, IAM"},
        "term": {"type": "string", "description": "The term or concept on the card front"},
        "question": {"type": "string", "description": "Question the learner should answer"},
        "definition": {"type": "string", "description": "Correct answer or definition. May use the formatting syntax (bold, italic, underline, headings, lists, https links) described by get_import_schema."},
        "how_it_works": {"type": "string", "description": "Technical mechanics explanation"},
        "context": {"type": "string", "description": "Domain-specific context and gotchas"},
        "key_fact": {"type": "string", "description": "One memorable fact or exam gotcha"},
        "reference_answer": {"type": "string", "description": "Reference solution used in quiz mode. Must stay factual and source-bound."},
        "official_sources": {
          "type": "array",
          "description": "Official source directory for this card. Prefer Microsoft Learn or other vendor docs only.",
          "items": {
            "type": "object",
            "required": ["title", "url"],
            "properties": {
              "title": {"type": "string"},
              "url": {"type": "string", "pattern": "^https://"},
              "publisher": {"type": "string"}
            }
          }
        },
        "chat_prompt": {"type": "string", "description": "System prompt for the AI chatbot on this card."},
        "slide_number": {"type": "integer", "minimum": 1, "description": "Optional order in slide mode"},
        "target_time_sec": {"type": "integer", "minimum": 1, "description": "Optional target speaking time in seconds (slide mode)"},
        "quiz": {
          "type": "array",
          "description": "Optional fixed quiz questions of this card. Default type is single_choice. Same rules as get_import_schema.",
          "items": {
            "type": "object",
            "required": ["question", "explanation"],
            "properties": {
              "type": {"type": "string", "enum": ["single_choice", "multiple_response", "free_text"], "default": "single_choice"},
              "question": {"type": "string"},
              "options": {"type": "array", "maxItems": 6, "items": {"type": "string"}, "description": "single_choice: exactly 4. multiple_response: 3 to 6. free_text: omit."},
              "correct_index": {"type": "integer", "minimum": 0, "maximum": 3, "description": "single_choice only (0-based)"},
              "correct_indexes": {"type": "array", "items": {"type": "integer", "minimum": 0, "maximum": 5}, "description": "multiple_response only: all correct options (0-based, unique)"},
              "explanation": {"type": "string", "description": "Explanation shown after answering; for free_text the model answer"}
            }
          }
        },
        "images": {
          "type": "array",
          "maxItems": 5,
          "description": "Optional images (max 5, each max 2 MiB decoded; prefer < 200 KiB). Only real images you were given — never invent Base64.",
          "items": {
            "type": "object",
            "required": ["mime_type", "data_base64"],
            "properties": {
              "mime_type": {"type": "string", "enum": ["image/png", "image/jpeg", "image/webp"], "description": "Must match the real image type"},
              "data_base64": {"type": "string", "description": "Plain standard Base64 of the image file, WITHOUT a 'data:' prefix"},
              "alt_text": {"type": "string", "maxLength": 300, "description": "Alternative text describing the image (accessibility)"},
              "caption": {"type": "string", "maxLength": 300, "description": "Optional caption"}
            }
          }
        },
        "sort_order": {"type": "integer", "description": "Display order within category", "default": 0}
      }
    }
    """;
