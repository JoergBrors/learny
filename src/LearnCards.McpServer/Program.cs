using System.Net;
using System.Text.Json.Nodes;
using LearnCards.McpServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

EnvLoader.Load();

var settings = McpSettings.Load();
if (settings.OAuth.Enabled && string.IsNullOrWhiteSpace(settings.OAuth.SigningKey))
    throw new InvalidOperationException("OAuth ist aktiviert, aber 'oauth.signingKey' fehlt in mcpsettings.json.");

using var api = new LearnCardsApiClient(settings.UpstreamApi.BaseUrl, settings.UpstreamApi.ApiKey);
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
        new ToolDefinition("add_card",
            "Create a single learning card in the LearnCards platform. Use only source-grounded facts and preserve the canonical import structure.",
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
            "Import multiple learning cards at once. Always consult get_import_schema first and keep answers grounded in the provided official sources.",
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
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("list_modules",
            "List all learning modules with their card counts.",
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
            catch (McpApiException ex) { return new ToolCallResult($"API error {ex.StatusCode}: {ex.Message}", true); }
        });

    engine.RegisterTool(
        new ToolDefinition("list_cards",
            "List cards in a module, optionally filtered by category. Includes official source count so clients can prefer source-backed cards.",
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
    builder.Services.AddSingleton(engine);
    builder.Services.AddSingleton(api);
    builder.Services.AddSingleton(settings);
    builder.Services.AddSingleton(oauth);

    var app = builder.Build();

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

    app.MapGet("/.well-known/oauth-authorization-server", (HttpRequest request, ProprietaryOAuthService auth, McpSettings cfg) =>
    {
        if (!auth.IsEnabled) return Results.NotFound();
        return Results.Json(auth.AuthorizationServerMetadata(MetadataIssuer(request, cfg)));
    });

    app.MapPost("/oauth/token", async (HttpRequest request, ProprietaryOAuthService auth) =>
    {
        if (!auth.IsEnabled)
            return Results.NotFound();

        string? clientId;
        string? clientSecret;
        string? scope;
        string? grantType;

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            clientId = form["client_id"].FirstOrDefault();
            clientSecret = form["client_secret"].FirstOrDefault();
            scope = form["scope"].FirstOrDefault();
            grantType = form["grant_type"].FirstOrDefault();
        }
        else
        {
            var body = await JsonNode.ParseAsync(request.Body);
            clientId = body?["client_id"]?.GetValue<string>();
            clientSecret = body?["client_secret"]?.GetValue<string>();
            scope = body?["scope"]?.GetValue<string>();
            grantType = body?["grant_type"]?.GetValue<string>();
        }

        if (!string.Equals(grantType, "client_credentials", StringComparison.Ordinal))
            return Results.Json(new { error = "unsupported_grant_type" }, statusCode: 400);

        try
        {
            var token = auth.IssueClientCredentialsToken(clientId ?? "", clientSecret ?? "", scope);
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
            return Results.Json(new { error = ex.Message }, statusCode: 401);
        }
    });

    app.MapGet("/metadata", async (HttpRequest request, McpServerEngine server, ProprietaryOAuthService auth, LearnCardsApiClient client, CancellationToken ct) =>
    {
        JsonNode? schema = null;
        try { schema = await client.GetAsync("schema/import/cards", ct); } catch { }
        var authMetadata = auth.IsEnabled
            ? auth.AuthorizationServerMetadata(MetadataIssuer(request, settings))
            : new JsonObject { ["mode"] = "none" };
        return Results.Json(server.CreateServerMetadata(authMetadata, schema));
    });

    app.MapGet("/schema/import/cards", async (LearnCardsApiClient client, CancellationToken ct) =>
    {
        var schema = await client.GetAsync("schema/import/cards", ct);
        return Results.Text(schema?.ToJsonString(new() { WriteIndented = true }) ?? "{}", "application/json");
    });

    app.MapPost("/mcp", async (HttpRequest request, HttpResponse response, McpServerEngine server, ProprietaryOAuthService auth, CancellationToken ct) =>
    {
        if (!AuthorizeHttpRequest(request, response, auth))
            return Results.Empty;

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

static bool AuthorizeHttpRequest(HttpRequest request, HttpResponse response, ProprietaryOAuthService oauth)
{
    if (!oauth.IsEnabled)
        return true;

    var header = request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        || !oauth.TryValidateBearerToken(header[7..].Trim(), out _, out _))
    {
        response.StatusCode = 401;
        response.Headers.WWWAuthenticate = "Bearer";
        return false;
    }

    return true;
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
        "definition": {"type": "string", "description": "Correct answer or definition"},
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
        "sort_order": {"type": "integer", "description": "Display order within category", "default": 0}
      }
    }
    """;
