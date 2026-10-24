using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;

namespace LearnCards.Web.Api;

/// <summary>
/// REST-API unter /api — routen- und formatkompatibel zum FastAPI-Original.
/// Zugriff: eingeloggte Benutzer (Cookie) ODER MCP-/Automation-Clients mit X-MCP-Key-Header.
/// Alle Daten sind auf den authentifizierten Benutzer beschränkt (ADR 0011).
/// </summary>
public static class ApiEndpoints
{
    private const string UserItem = "lc.user";
    private const string ViaKeyItem = "lc.via-key";
    private const string ViaTokenItem = "lc.via-token";

    /// <summary>Routen, die ein MCP-Token nutzen darf (Karten-/Modulverwaltung und Schema, kein Profil, keine Administration).</summary>
    private static readonly string[] TokenAllowedPrefixes =
        { "/api/modules", "/api/cards", "/api/import", "/api/schema", "/api/mcp/whoami" };

    public static void MapLearnCardsApi(this WebApplication app)
    {
        var cfg = app.Services.GetRequiredService<AppConfig>();

        app.MapGet("/health", () => Results.Json(new { status = "ok", version = "2.0.0", engine = ".NET 10" }))
            .AllowAnonymous();

        var api = app.MapGroup("/api");

        // Authentifizierung + Benutzerkontext (ADR 0005/0011/0013). Drei Wege, nur der erste und zweite tragen einen Benutzer:
        //  1. Cookie-Login (Browser): Benutzer aus dem serverseitig gesetzten Claim "uid", bei jeder Anfrage gegen die DB geprüft.
        //  2. Authorization: Bearer lcm_… (MCP-Zugang): benutzergebundenes, widerrufbares Token; nur Daten-Routen (siehe TokenAllowedPrefixes).
        //  3. X-MCP-Key: reine Dienstauthentifizierung des MCP-Servers (Code-Tausch, Schema). Trägt KEINEN Benutzer und keinen Datenzugriff.
        // Eine vom Client gelieferte Benutzer-ID wird nie ausgewertet.
        api.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var users = http.RequestServices.GetRequiredService<UserService>();

            if (http.User.Identity?.IsAuthenticated == true)
            {
                var user = await users.GetAsync(http.User.GetUserId());
                if (user is null || !user.IsActive)
                    return Results.Json(new { detail = "Nicht autorisiert — Benutzer unbekannt oder gesperrt." }, statusCode: 401);
                http.Items[UserItem] = user;
                return await next(ctx);
            }

            var authHeader = http.Request.Headers.Authorization.ToString();
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var links = http.RequestServices.GetRequiredService<McpLinkService>();
                var valid = await links.ValidateAsync(authHeader[7..].Trim());
                if (valid is null)
                    return Results.Json(new { detail = "Ungültiges, abgelaufenes oder widerrufenes MCP-Token." }, statusCode: 401);
                var path = http.Request.Path.Value ?? "";
                if (!TokenAllowedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    return Results.Json(new { detail = "Dieser Endpunkt ist für MCP-Tokens nicht verfügbar." }, statusCode: 403);
                http.Items[UserItem] = valid.Value.User;
                http.Items[ViaTokenItem] = true;
                return await next(ctx);
            }

            var mcpKey = http.Request.Headers["X-MCP-Key"].FirstOrDefault();
            if (!KeyMatches(cfg.McpApiKey, mcpKey))
                return Results.Json(new { detail = "Nicht autorisiert — Login, MCP-Token oder gültiger Dienstschlüssel erforderlich." }, statusCode: 401);

            http.Items[ViaKeyItem] = true;
            return await next(ctx);
        });

        // Benutzerunabhängig: Import-Schema; Code-Tausch nur für den MCP-Server (Dienstschlüssel)
        api.MapGet("/schema/import/cards", () => Results.Ok(CardImportSchema.Document()));

        api.MapPost("/mcp/link/exchange", async (McpCodeExchangeRequest req, HttpContext http, McpLinkService links) =>
        {
            if (!http.Items.ContainsKey(ViaKeyItem))
                return Results.Json(new { detail = "Nur der MCP-Server (Dienstschlüssel) darf Codes tauschen." }, statusCode: 403);
            var result = await links.ExchangeCodeAsync(req.Code);
            if (result is null)
                return Results.Json(new { detail = "Code ungültig, abgelaufen oder bereits verwendet." }, statusCode: 400);
            var (token, link) = result.Value;
            return Results.Ok(new
            {
                access_token = token, token_type = "Bearer",
                expires_in = (int)Math.Max(1, (link.ExpiresAt - DateTime.UtcNow).TotalSeconds), expires_at = link.ExpiresAt,
            });
        });

        var secured = api.MapGroup(string.Empty);
        secured.AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.Items[UserItem] is AppUser
                ? await next(ctx)
                : Results.Json(new { detail = "Dieser Endpunkt benötigt einen angemeldeten Benutzer (Dienstschlüssel allein genügt nicht)." }, statusCode: 403));

        // Prüft ein MCP-Token bzw. die Sitzung (vom MCP-Server vor jedem /mcp-Aufruf genutzt)
        secured.MapGet("/mcp/whoami", (HttpContext http) =>
        {
            var u = CurrentUser(http);
            return Results.Ok(new { user_id = u.Id, display_name = u.DisplayName, via = http.Items.ContainsKey(ViaTokenItem) ? "mcp-token" : "session" });
        });

        MapMcpLinkEndpoints(secured);

        // ─── Eigenes Profil ─────────────────────────────────────────────────
        secured.MapGet("/me", async (HttpContext http, CardRepository repo) =>
        {
            var u = CurrentUser(http);
            var summary = await repo.GetOwnedSummaryAsync(u.Id);
            return Results.Ok(new
            {
                id = u.Id, display_name = u.DisplayName, email = u.Email, status = u.Status, roles = u.Roles,
                created_at = u.CreatedAt, last_login_at = u.LastLoginAt,
                content = new { modules = summary.Modules, active_cards = summary.ActiveCards, archived_cards = summary.ArchivedCards },
            });
        });

        // Einzige selbst änderbare Profilangabe: Anzeigename. Rollen/Status nur über /admin.
        secured.MapPut("/me", async (DisplayNameUpdateRequest req, HttpContext http, UserService users) =>
        {
            try { await users.SetDisplayNameAsync(Uid(http), req.DisplayName); return Results.NoContent(); }
            catch (ArgumentException e) { return Results.Json(new { detail = e.Message }, statusCode: 400); }
        });

        MapAdminEndpoints(secured);

        // ─── Module ─────────────────────────────────────────────────────────
        secured.MapGet("/modules/", async (CardRepository repo, HttpContext http) => Results.Ok(
            (await repo.ListModulesAsync(Uid(http))).Select(ModuleDto)));

        secured.MapPost("/modules/", async (ModuleCreateRequest req, CardRepository repo, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.Json(new { detail = "'name' ist erforderlich" }, statusCode: 400);
            var m = await repo.CreateModuleAsync(req, Uid(http));
            return Results.Json(ModuleDto(m), statusCode: 201);
        });

        secured.MapDelete("/modules/{moduleId}", async (string moduleId, CardRepository repo, HttpContext http) =>
            await repo.DeleteModuleAsync(moduleId, Uid(http)) == 0
                ? Results.Json(new { detail = "Module not found" }, statusCode: 404)
                : Results.NoContent());

        // ─── Karten ─────────────────────────────────────────────────────────
        secured.MapGet("/cards/", async (CardRepository repo, HttpContext http, string? module_id, string? category, bool archived = false) =>
            Results.Ok((await repo.ListCardsAsync(Uid(http), module_id, category, archived)).Select(CardDto)));

        secured.MapGet("/cards/{cardId}", async (string cardId, CardRepository repo, HttpContext http) =>
            await repo.GetCardAsync(cardId, Uid(http)) is { } card
                ? Results.Ok(CardDto(card))
                : Results.Json(new { detail = "Card not found" }, statusCode: 404));

        secured.MapPost("/cards/", async (CardJson data, CardRepository repo, HttpContext http) =>
        {
            var (ok, error) = data.Validate();
            if (!ok) return Results.Json(new { detail = error }, statusCode: 400);
            var card = await repo.CreateCardAsync(data, Uid(http));
            return Results.Json(CardDto(card), statusCode: 201);
        });

        secured.MapPut("/cards/{cardId}", async (string cardId, CardJson data, CardRepository repo, HttpContext http) =>
            await repo.UpdateCardAsync(cardId, data, Uid(http)) is { } card
                ? Results.Ok(CardDto(card))
                : Results.Json(new { detail = "Card not found" }, statusCode: 404));

        secured.MapPatch("/cards/{cardId}/archive", async (string cardId, CardRepository repo, HttpContext http) =>
            await repo.SetArchivedAsync(cardId, true, Uid(http)) is { } card
                ? Results.Ok(CardDto(card))
                : Results.Json(new { detail = "Card not found" }, statusCode: 404));

        secured.MapPatch("/cards/{cardId}/restore", async (string cardId, CardRepository repo, HttpContext http) =>
            await repo.SetArchivedAsync(cardId, false, Uid(http)) is { } card
                ? Results.Ok(CardDto(card))
                : Results.Json(new { detail = "Card not found" }, statusCode: 404));

        secured.MapDelete("/cards/{cardId}", async (string cardId, CardRepository repo, HttpContext http) =>
            await repo.DeleteCardAsync(cardId, Uid(http)) == 0
                ? Results.Json(new { detail = "Card not found" }, statusCode: 404)
                : Results.NoContent());

        // ─── Import ─────────────────────────────────────────────────────────
        secured.MapPost("/import/cards", async (CardImportBatch batch, CardRepository repo, HttpContext http) =>
        {
            var (created, updated, skipped) = await repo.ImportAsync(batch.Cards, batch.OverwriteExisting, Uid(http));
            return Results.Ok(new { created, updated, skipped });
        });

        secured.MapPost("/import/cards/file", async (HttpRequest request, HttpContext http, CardRepository repo, bool overwrite_existing = false) =>
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

            var (created, updated, skipped) = await repo.ImportAsync(cards, overwrite_existing, Uid(http));
            return Results.Ok(new { created, updated, skipped });
        }).DisableAntiforgery();

        secured.MapPost("/cards/{cardId}/quiz", async (string cardId, QuizService quiz, HttpContext http) =>
        {
            try
            {
                var questions = await quiz.GetCardQuizAsync(cardId, Uid(http));
                return Results.Ok(new { card_id = cardId, questions });
            }
            catch (InvalidOperationException e)
            {
                return Results.Json(new { detail = e.Message }, statusCode: 400);
            }
        });

        // ─── Quiz ───────────────────────────────────────────────────────────
        secured.MapPost("/quiz/start", async (QuizStartRequest req, QuizService quiz, HttpContext http) =>
        {
            try
            {
                var questions = await quiz.StartQuizAsync(req.ModuleId, req.Category, req.NumQuestions, Uid(http), req.CardId);
                return Results.Ok(new { questions, module_id = req.ModuleId, category = req.Category, card_id = req.CardId });
            }
            catch (InvalidOperationException e) { return Results.Json(new { detail = e.Message }, statusCode: 404); }
        });

        secured.MapPost("/quiz/submit", async (QuizSubmitRequest req, QuizService quiz, HttpContext http) =>
        {
            try { return Results.Ok(await quiz.SubmitQuizAsync(req, Uid(http))); }
            catch (InvalidOperationException e) { return Results.Json(new { detail = e.Message }, statusCode: 404); }
        });

        secured.MapGet("/quiz/history", async (CardRepository repo, HttpContext http, string? module_id, string? card_id) =>
            Results.Ok(await repo.QuizHistoryAsync(Uid(http), module_id, card_id)));

        secured.MapDelete("/quiz/history/{quizId}", async (string quizId, QuizService quiz, HttpContext http) =>
            await quiz.DeleteHistoryEntryAsync(Uid(http), quizId) == 0
                ? Results.Json(new { detail = "Quiz history entry not found" }, statusCode: 404)
                : Results.NoContent());

        // ─── User Preferences / Card State / Chat History ──────────────────
        secured.MapGet("/preferences/", async (CardRepository repo, HttpContext http) =>
            Results.Ok(new { theme = await repo.GetThemeAsync(Uid(http)) }));

        secured.MapPut("/preferences/theme", async (ThemeUpdateRequest req, CardRepository repo, HttpContext http) =>
        {
            var theme = req.Theme.Trim().ToLowerInvariant();
            if (theme is not "dark" and not "light")
                return Results.Json(new { detail = "theme must be 'light' or 'dark'" }, statusCode: 400);
            await repo.SaveThemeAsync(Uid(http), theme);
            return Results.NoContent();
        });

        secured.MapGet("/cards/{cardId}/state", async (string cardId, CardRepository repo, HttpContext http) =>
        {
            var sub = Uid(http);
            if (await repo.GetCardAsync(cardId, sub) is null)
                return Results.Json(new { detail = "Card not found" }, statusCode: 404);
            var state = await repo.GetUserCardStateAsync(sub, cardId) ?? new UserCardState { UserSub = sub, CardId = cardId };
            return Results.Ok(new { is_checked = state.IsChecked, marked_review = state.MarkedReview, updated_at = state.UpdatedAt });
        });

        secured.MapPut("/cards/{cardId}/state", async (string cardId, CardStateUpdateRequest req, CardRepository repo, HttpContext http) =>
        {
            var sub = Uid(http);
            if (await repo.GetCardAsync(cardId, sub) is null)
                return Results.Json(new { detail = "Card not found" }, statusCode: 404);
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

        secured.MapGet("/chat/history", async (string card_id, CardRepository repo, HttpContext http, int limit = 50) =>
            Results.Ok(await repo.GetChatHistoryAsync(Uid(http), card_id, limit)));

        // ─── Chat (Server-Sent Events, kompatibel zum Original) ─────────────
        secured.MapPost("/chat/", async (ChatRequest req, ChatService chat, HttpContext http) =>
        {
            var card = await chat.GetCardAsync(req.CardId, Uid(http));
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

    /// <summary>Verwaltung eigener MCP-Zugänge — nur per Browser-Anmeldung (ein MCP-Token kann keine weiteren Zugänge erzeugen).</summary>
    private static void MapMcpLinkEndpoints(RouteGroupBuilder secured)
    {
        var g = secured.MapGroup("/mcp/links");
        g.AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.Items.ContainsKey(ViaTokenItem)
                ? Results.Json(new { detail = "MCP-Zugänge können nur über die Browser-Anmeldung verwaltet werden." }, statusCode: 403)
                : await next(ctx));

        g.MapGet("/", async (HttpContext http, McpLinkService links) =>
            Results.Ok((await links.ListAsync(Uid(http))).Select(LinkDto)));

        // Persönliches Token (z. B. für stdio); das Token wird nur in dieser Antwort ausgegeben
        g.MapPost("/", async (McpLinkCreateRequest req, HttpContext http, McpLinkService links) =>
        {
            var days = Math.Clamp(req.Days <= 0 ? 90 : req.Days, 1, 365);
            var (token, link) = await links.CreateLinkAsync(Uid(http), req.Name, "token", TimeSpan.FromDays(days));
            return Results.Json(new { token, link = LinkDto(link) }, statusCode: 201);
        });

        g.MapDelete("/{linkId}", async (string linkId, HttpContext http, McpLinkService links) =>
            await links.RevokeAsync(Uid(http), linkId)
                ? Results.NoContent()
                : Results.Json(new { detail = "MCP-Zugang nicht gefunden." }, statusCode: 404));

        // Zustimmung zur OAuth-Verknüpfung (dieselbe Logik wie die Seite /mcp/connect)
        secured.MapPost("/mcp/link/authorize", async (McpConsentRequest req, HttpContext http, McpLinkService links) =>
        {
            if (http.Items.ContainsKey(ViaTokenItem))
                return Results.Json(new { detail = "Nur über die Browser-Anmeldung." }, statusCode: 403);
            try
            {
                var url = await links.CompleteConsentAsync(Uid(http), req.ReturnUrl, req.State, req.ClientName, req.Approve);
                return Results.Ok(new { redirect_url = url });
            }
            catch (ArgumentException e) { return Results.Json(new { detail = e.Message }, statusCode: 400); }
        });
    }

    private static object LinkDto(McpLink l) => new
    {
        id = l.Id, name = l.Name, kind = l.Kind, token_hint = l.TokenHint, created_at = l.CreatedAt, expires_at = l.ExpiresAt,
        last_used_at = l.LastUsedAt, revoked_at = l.RevokedAt, active = l.IsActive,
    };

    /// <summary>
    /// Administration (Rolle "admin", serverseitig gegen die DB geprüft). Nur per Cookie-Login erreichbar —
    /// der gemeinsame X-MCP-Key darf keine Benutzer- oder Rollenverwaltung ausführen.
    /// </summary>
    private static void MapAdminEndpoints(RouteGroupBuilder secured)
    {
        var admin = secured.MapGroup("/admin");
        admin.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (http.Items.ContainsKey(ViaKeyItem) || http.Items.ContainsKey(ViaTokenItem))
                return Results.Json(new { detail = "Administrative Funktionen sind nur über die Browser-Anmeldung verfügbar." }, statusCode: 403);
            try
            {
                await http.RequestServices.GetRequiredService<UserService>().RequireRoleAsync(Uid(http), Roles.Admin);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new { detail = "Administratorrechte erforderlich." }, statusCode: 403);
            }
            return await next(ctx);
        });

        admin.MapGet("/users", async (UserService users) => Results.Ok((await users.ListAsync()).Select(UserDto)));

        admin.MapPut("/users/{userId}/roles", (string userId, RolesUpdateRequest req, HttpContext http, UserService users) =>
            Guard(async () =>
            {
                await users.SetRolesAsync(Uid(http), userId, req.Roles);
                return Results.NoContent();
            }));

        admin.MapPut("/users/{userId}/status", (string userId, StatusUpdateRequest req, HttpContext http, UserService users) =>
            Guard(async () =>
            {
                await users.SetStatusAsync(Uid(http), userId, req.Active);
                return Results.NoContent();
            }));

        admin.MapGet("/unowned-modules", async (CardRepository repo) =>
            Results.Ok((await repo.ListUnownedModulesAsync()).Select(ModuleDto)));

        admin.MapPost("/modules/{moduleId}/owner", (string moduleId, OwnerAssignRequest req, HttpContext http, UserService users, CardRepository repo) =>
            Guard(async () =>
            {
                var target = await users.GetAsync(req.OwnerUserId) ?? throw new KeyNotFoundException("Benutzer nicht gefunden.");
                var error = await repo.AssignUnownedModuleAsync(moduleId, target.Id);
                switch (error)
                {
                    case "not_found": return Results.Json(new { detail = "Module not found" }, statusCode: 404);
                    case "already_owned": return Results.Json(new { detail = "Das Modul hat bereits einen Eigentümer." }, statusCode: 409);
                    case "name_conflict": return Results.Json(new { detail = "Der Benutzer besitzt bereits ein Modul mit diesem Namen." }, statusCode: 409);
                }
                await users.WriteAuditAsync(Uid(http), "module.assign-owner", moduleId, $"owner={target.Id}");
                return Results.NoContent();
            }));

        admin.MapGet("/audit", async (HttpContext http, UserService users, int limit = 100) =>
            Results.Ok(await users.ListAuditAsync(Uid(http), limit)));
    }

    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (UnauthorizedAccessException e) { return Results.Json(new { detail = e.Message }, statusCode: 403); }
        catch (KeyNotFoundException e) { return Results.Json(new { detail = e.Message }, statusCode: 404); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        { return Results.Json(new { detail = e.Message }, statusCode: 400); }
    }

    private static AppUser CurrentUser(HttpContext http) =>
        http.Items[UserItem] as AppUser ?? throw new InvalidOperationException("Kein Benutzerkontext.");

    private static string Uid(HttpContext http) => CurrentUser(http).Id;

    private static bool KeyMatches(string expected, string? provided) =>
        !string.IsNullOrEmpty(expected) && provided is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided));

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

    private static object UserDto(AppUser u) => new
    {
        id = u.Id, display_name = u.DisplayName, email = u.Email, status = u.Status, roles = u.Roles,
        created_at = u.CreatedAt, last_login_at = u.LastLoginAt,
    };

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
        slide_number = c.SlideNumber, target_time_sec = c.TargetTimeSec, quiz = c.Quiz,
        archived = c.Archived, sort_order = c.SortOrder, created_at = c.CreatedAt, updated_at = c.UpdatedAt,
    };
}

public class DisplayNameUpdateRequest { public string DisplayName { get; set; } = ""; }
public class RolesUpdateRequest { public List<string> Roles { get; set; } = new(); }
public class StatusUpdateRequest { public bool Active { get; set; } }
public class OwnerAssignRequest { public string OwnerUserId { get; set; } = ""; }
public class McpCodeExchangeRequest { public string Code { get; set; } = ""; }
public class McpLinkCreateRequest { public string Name { get; set; } = ""; public int Days { get; set; } = 90; }
public class McpConsentRequest
{
    public string ReturnUrl { get; set; } = "";
    public string State { get; set; } = "";
    public string ClientName { get; set; } = "";
    public bool Approve { get; set; }
}
