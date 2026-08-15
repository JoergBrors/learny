// LearnCards MCP Server (.NET 10)
// Stellt Tools zum Erstellen und Verwalten von Lernkarten über das Model Context Protocol bereit.
// Verbindung von Claude Desktop oder jedem anderen MCP-Client via stdio-Transport.
using System.Text.Json.Nodes;
using LearnCards.McpServer;

EnvLoader.Load();

var apiBase = Environment.GetEnvironmentVariable("LEARNCARDS_API_URL") ?? "http://localhost:5000/api";
var apiKey = Environment.GetEnvironmentVariable("LEARNCARDS_MCP_API_KEY")
             ?? Environment.GetEnvironmentVariable("MCP_API_KEY")
             ?? "change-me-mcp-key";

using var api = new LearnCardsApiClient(apiBase, apiKey);
var server = new McpStdioServer("learncards", "2.0.0");

// ─── add_card ─────────────────────────────────────────────────────────────
server.RegisterTool(
    new ToolDefinition("add_card",
        "Create a single learning card in the LearnCards platform. The card is immediately available " +
        "for learning. Use this to add new concepts, terms, or facts to a module.",
        """
        {
          "type": "object",
          "required": ["module", "category", "term", "question", "definition"],
          "properties": {
            "module": {"type": "string", "description": "Module name (created if it doesn't exist)"},
            "category": {"type": "string", "description": "Thematic group, e.g. 'Netzwerk', 'Security', 'IAM'"},
            "term": {"type": "string", "description": "The term or concept on the card front"},
            "question": {"type": "string", "description": "Question the learner should answer"},
            "definition": {"type": "string", "description": "Correct answer / definition"},
            "how_it_works": {"type": "string", "description": "Technical mechanics explanation"},
            "context": {"type": "string", "description": "Domain-specific context and gotchas"},
            "key_fact": {"type": "string", "description": "One memorable L400 fact or exam gotcha"},
            "reference_answer": {"type": "string", "description": "Reference solution used in quiz mode. Must stay factual and source-bound."},
            "official_sources": {
              "type": "array",
              "description": "Official source directory for this card. Prefer Microsoft Learn or other vendor docs only.",
              "items": {
                "type": "object",
                "properties": {
                  "title": {"type": "string"},
                  "url": {"type": "string"},
                  "publisher": {"type": "string"}
                }
              }
            },
            "chat_prompt": {"type": "string", "description": "System prompt for the AI chatbot on this card. Should make the AI act as an expert on this specific term."},
            "sort_order": {"type": "integer", "description": "Display order within category", "default": 0}
          }
        }
        """),
    async (args, ct) =>
    {
        try
        {
            var card = await api.PostAsync("cards/", args, ct);
            var term = card?["term"]?.GetValue<string>() ?? "?";
            var id = card?["id"]?.GetValue<string>() ?? "?";
            return new ToolCallResult($"Card created: {term} (id: {id})");
        }
        catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", IsError: true); }
    });

// ─── import_cards ───────────────────────────────────────────────────────────
server.RegisterTool(
    new ToolDefinition("import_cards",
        "Import multiple learning cards at once. Accepts a JSON array of card objects. " +
        "Existing cards are skipped unless overwrite_existing is true.",
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
            return new ToolCallResult($"Import complete: {created} created, {updated} updated, {skipped} skipped.");
        }
        catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", IsError: true); }
    });

// ─── list_modules ───────────────────────────────────────────────────────────
server.RegisterTool(
    new ToolDefinition("list_modules", "List all learning modules with their card counts.",
        """{"type": "object", "properties": {}}"""),
    async (_, ct) =>
    {
        try
        {
            var modules = await api.GetAsync("modules/", ct) as JsonArray;
            if (modules is null || modules.Count == 0) return new ToolCallResult("No modules found.");
            var lines = modules.Select(m =>
                $"• {m?["name"]?.GetValue<string>()} — {m?["active_count"]?.GetValue<int>()} active / {m?["card_count"]?.GetValue<int>()} total cards (id: {m?["id"]?.GetValue<string>()})");
            return new ToolCallResult(string.Join("\n", lines));
        }
        catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", IsError: true); }
    });

// ─── list_cards ───────────────────────────────────────────────────────────
server.RegisterTool(
    new ToolDefinition("list_cards", "List cards in a module, optionally filtered by category. Includes official source count so clients can prefer source-backed cards.",
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
        catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", IsError: true); }
    });

// ─── archive_card / restore_card ────────────────────────────────────────────
server.RegisterTool(
    new ToolDefinition("archive_card", "Archive a card (hides it from active learning, retains it in the archive).",
        """{"type": "object", "required": ["card_id"], "properties": {"card_id": {"type": "string"}}}"""),
    async (args, ct) =>
    {
        try
        {
            var id = args["card_id"]?.GetValue<string>() ?? "";
            await api.PatchAsync($"cards/{Uri.EscapeDataString(id)}/archive", ct);
            return new ToolCallResult($"Card {id} archived.");
        }
        catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", IsError: true); }
    });

server.RegisterTool(
    new ToolDefinition("restore_card", "Restore a previously archived card to active learning.",
        """{"type": "object", "required": ["card_id"], "properties": {"card_id": {"type": "string"}}}"""),
    async (args, ct) =>
    {
        try
        {
            var id = args["card_id"]?.GetValue<string>() ?? "";
            await api.PatchAsync($"cards/{Uri.EscapeDataString(id)}/restore", ct);
            return new ToolCallResult($"Card {id} restored.");
        }
        catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", IsError: true); }
    });

await server.RunAsync();
