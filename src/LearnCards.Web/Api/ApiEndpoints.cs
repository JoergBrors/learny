using System.Text.Json;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;

namespace LearnCards.Web.Api;

/// <summary>
/// REST-API unter /api — routen- und formatkompatibel zum FastAPI-Original.
/// Zugriff: eingeloggte Benutzer (Cookie) ODER MCP-/Automation-Clients mit X-MCP-Key-Header.
/// </summary>
public static class ApiEndpoints
{
    public static void MapLearnCardsApi(this WebApplication app)
    {
        var cfg = app.Services.GetRequiredService<AppConfig>();

        app.MapGet("/health", () => Results.Json(new { status = "ok", version = "2.0.0", engine = ".NET 10" }))
            .AllowAnonymous();

        var api = app.MapGroup("/api");
        api.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var mcpKey = http.Request.Headers["X-MCP-Key"].FirstOrDefault();
            var keyOk = !string.IsNullOrEmpty(cfg.McpApiKey) && mcpKey == cfg.McpApiKey;
            var userOk = http.User.Identity?.IsAuthenticated == true;
            if (!keyOk && !userOk)
                return Results.Json(new { detail = "Nicht autorisiert — Login oder gültiger X-MCP-Key erforderlich." }, statusCode: 401);
            return await next(ctx);
        });

        // ─── Module ─────────────────────────────────────────────────────────
        api.MapGet("/modules/", async (CardRepository repo) => Results.Ok(
            (await repo.ListModulesAsync()).Select(ModuleDto)));

        api.MapPost("/modules/", async (ModuleCreateRequest req, CardRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.Json(new { detail = "'name' ist erforderlich" }, statusCode: 400);
            var m = await repo.CreateModuleAsync(req);
            return Results.Json(ModuleDto(m), statusCode: 201);
        });

        api.MapDelete("/modules/{moduleId}", async (string moduleId, CardRepository repo) =>
            await repo.DeleteModuleAsync(moduleId) == 0
                ? Results.Json(new { detail = "Module not found" }, statusCode: 404)
                : Results.NoContent());

        // ─── Karten ─────────────────────────────────────────────────────────
        api.MapGet("/cards/", async (CardRepository repo, string? module_id, string? category, bool archived = false) =>
            Results.Ok((await repo.ListCardsAsync(module_id, category, archived)).Select(CardDto)));

        api.MapGet("/cards/{cardId}", async (string cardId, CardRepository repo) =>
            await repo.GetCardAsync(cardId) is { } card
                ? Results.Ok(CardDto(card))
                : Results.Json(new { detail = "Card not found" }, statusCode: 404));

        api.MapPost("/cards/", async (CardJson data, CardRepository repo) =>
        {
            var (ok, error) = data.Validate();
            if (!ok) return Results.Json(new { detail = error }, statusCode: 400);
            var card = await repo.CreateCardAsync(data);
            return Results.Json(CardDto(card), statusCode: 201);
        });

        api.MapPut("/cards/{cardId}", async (string cardId, CardJson data, CardRepository repo) =>
            await repo.UpdateCardAsync(cardId, data) is { } card
                ? Results.Ok(CardDto(card))
                : Results.Json(new { detail = "Card not found" }, statusCode: 404));

        api.MapPatch("/cards/{cardId}/archive", async (string cardId, CardRepository repo) =>
            await repo.SetArchivedAsync(cardId, true) is { } card
                ? Results.Ok(CardDto(card))
                : Results.Json(new { detail = "Card not found" }, statusCode: 404));

        api.MapPatch("/cards/{cardId}/restore", async (string cardId, CardRepository repo) =>
            await repo.SetArchivedAsync(cardId, false) is { } card
                ? Results.Ok(CardDto(card))
                : Results.Json(new { detail = "Card not found" }, statusCode: 404));

        api.MapDelete("/cards/{cardId}", async (string cardId, CardRepository repo) =>
            await repo.DeleteCardAsync(cardId) == 0
                ? Results.Json(new { detail = "Card not found" }, statusCode: 404)
                : Results.NoContent());

        // ─── Import ─────────────────────────────────────────────────────────
        api.MapPost("/import/cards", async (CardImportBatch batch, CardRepository repo) =>
        {
            var (created, updated, skipped) = await repo.ImportAsync(batch.Cards, batch.OverwriteExisting);
            return Results.Ok(new { created, updated, skipped });
        });

        api.MapPost("/import/cards/file", async (HttpRequest request, CardRepository repo, bool overwrite_existing = false) =>
        {
            if (!request.HasFormContentType)
                return Results.Json(new { detail = "multipart/form-data mit Feld 'file' erwartet" }, statusCode: 400);
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null)
                return Results.Json(new { detail = "Feld 'file' fehlt" }, statusCode: 400);

            using var reader = new StreamReader(file.OpenReadStream());
            var content = await reader.ReadToEndAsync();

            List<CardJson> cards;
            try { cards = ParseCardsJson(content); }
            catch (JsonException e) { return Results.Json(new { detail = $"Invalid JSON: {e.Message}" }, statusCode: 400); }
            catch (InvalidOperationException e) { return Results.Json(new { detail = e.Message }, statusCode: 400); }

            var (created, updated, skipped) = await repo.ImportAsync(cards, overwrite_existing);
            return Results.Ok(new { created, updated, skipped });
        }).DisableAntiforgery();

        // ─── Quiz ───────────────────────────────────────────────────────────
        api.MapPost("/quiz/start", async (QuizStartRequest req, QuizService quiz, HttpContext http) =>
        {
            try
            {
                var sub = http.User.FindFirst("sub")?.Value ?? "mcp-client";
                var questions = await quiz.StartQuizAsync(req.ModuleId, req.Category, req.NumQuestions, sub);
                return Results.Ok(new { questions, module_id = req.ModuleId, category = req.Category });
            }
            catch (InvalidOperationException e) { return Results.Json(new { detail = e.Message }, statusCode: 404); }
        });

        api.MapPost("/quiz/submit", async (QuizSubmitRequest req, QuizService quiz, HttpContext http) =>
        {
            var sub = http.User.FindFirst("sub")?.Value ?? "mcp-client";
            var result = await quiz.SubmitQuizAsync(req, sub);
            return Results.Ok(result);
        });

        api.MapGet("/quiz/history", async (CardRepository repo, HttpContext http, string? module_id) =>
        {
            var sub = http.User.FindFirst("sub")?.Value ?? "mcp-client";
            return Results.Ok(await repo.QuizHistoryAsync(sub, module_id));
        });

        // ─── User Preferences / Card State / Chat History ──────────────────
        api.MapGet("/preferences/", async (CardRepository repo, HttpContext http) =>
        {
            var sub = http.User.FindFirst("sub")?.Value ?? "mcp-client";
            var theme = await repo.GetThemeAsync(sub);
            return Results.Ok(new { theme });
        });

        api.MapPut("/preferences/theme", async (ThemeUpdateRequest req, CardRepository repo, HttpContext http) =>
        {
            var sub = http.User.FindFirst("sub")?.Value ?? "mcp-client";
            var theme = req.Theme.Trim().ToLowerInvariant();
            if (theme is not "dark" and not "light")
                return Results.Json(new { detail = "theme must be 'light' or 'dark'" }, statusCode: 400);
            await repo.SaveThemeAsync(sub, theme);
            return Results.NoContent();
        });

        api.MapGet("/cards/{cardId}/state", async (string cardId, CardRepository repo, HttpContext http) =>
        {
            var sub = http.User.FindFirst("sub")?.Value ?? "mcp-client";
            var state = await repo.GetUserCardStateAsync(sub, cardId) ?? new UserCardState { UserSub = sub, CardId = cardId };
            return Results.Ok(new { is_checked = state.IsChecked, marked_review = state.MarkedReview, updated_at = state.UpdatedAt });
        });

        api.MapPut("/cards/{cardId}/state", async (string cardId, CardStateUpdateRequest req, CardRepository repo, HttpContext http) =>
        {
            var sub = http.User.FindFirst("sub")?.Value ?? "mcp-client";
            await repo.SaveUserCardStateAsync(new UserCardState
            {
                UserSub = sub,
                CardId = cardId,
                IsChecked = req.IsChecked,
                MarkedReview = req.MarkedReview,
                UpdatedAt = DateTime.UtcNow,
            });
            return Results.NoContent();
        });

        api.MapGet("/chat/history", async (string card_id, CardRepository repo, HttpContext http, int limit = 50) =>
        {
            var sub = http.User.FindFirst("sub")?.Value ?? "mcp-client";
            return Results.Ok(await repo.GetChatHistoryAsync(sub, card_id, limit));
        });

        // ─── Chat (Server-Sent Events, kompatibel zum Original) ─────────────
        api.MapPost("/chat/", async (ChatRequest req, ChatService chat, HttpContext http) =>
        {
            var card = await chat.GetCardAsync(req.CardId);
            if (card is null)
            {
                http.Response.StatusCode = 404;
                await http.Response.WriteAsJsonAsync(new { detail = "Card not found" });
                return;
            }

            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";

            await foreach (var token in chat.StreamAsync(card, req.Messages, http.RequestAborted))
            {
                var json = JsonSerializer.Serialize(new { content = token });
                await http.Response.WriteAsync($"data: {json}\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
            }
            await http.Response.WriteAsync("data: [DONE]\n\n", http.RequestAborted);
        });
    }

    /// <summary>Akzeptiert ein JSON-Array von Karten oder ein Objekt mit "cards"-Schlüssel.</summary>
    public static List<CardJson> ParseCardsJson(string content)
    {
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        JsonElement arr = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("cards", out var c) && c.ValueKind == JsonValueKind.Array => c,
            _ => throw new InvalidOperationException("Expected JSON array or object with 'cards' key"),
        };
        return arr.Deserialize<List<CardJson>>(AppJson.Options) ?? new List<CardJson>();
    }

    private static object ModuleDto(ModuleInfo m) => new
    {
        id = m.Id, name = m.Name, description = m.Description, icon = m.Icon, color = m.Color,
        card_count = m.CardCount, active_count = m.ActiveCount, created_at = m.CreatedAt,
    };

    private static object CardDto(Card c) => new
    {
        id = c.Id, module_id = c.ModuleId, category = c.Category, term = c.Term,
        question = c.Question, definition = c.Definition, how_it_works = c.HowItWorks,
        context = c.Context, key_fact = c.KeyFact, reference_answer = c.ReferenceAnswer,
        chat_prompt = c.ChatPrompt, official_sources = c.OfficialSources,
        archived = c.Archived, sort_order = c.SortOrder, created_at = c.CreatedAt, updated_at = c.UpdatedAt,
    };
}
