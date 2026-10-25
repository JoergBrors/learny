using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;
using LearnCards.Web.Services.ImageStorage;

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
        { "/api/modules", "/api/cards", "/api/images", "/api/import", "/api/schema", "/api/mcp/whoami" };

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
        MapSharingEndpoints(secured);
        MapGlobalReviewEndpoints(secured);

        // ─── Module ─────────────────────────────────────────────────────────
        // scope: private (Standard, kompatibel) | shared (mir freigegeben) | global (veröffentlicht) | all — ADR 0015
        secured.MapGet("/modules/", async (CardRepository repo, HttpContext http, string? scope) =>
        {
            var uid = Uid(http);
            var s = (scope ?? "private").Trim().ToLowerInvariant();
            if (s is not ("private" or "shared" or "global" or "all"))
                return Results.Json(new { detail = "scope muss private, shared, global oder all sein." }, statusCode: 400);
            var list = new List<ModuleInfo>();
            if (s is "private" or "all") list.AddRange(await repo.ListModulesAsync(uid));
            if (s is "shared" or "all") list.AddRange(await repo.ListSharedWithMeAsync(uid));
            if (s is "global" or "all") list.AddRange(await repo.ListGlobalModulesAsync());
            return Results.Ok(list.Select(ModuleDto));
        });

        secured.MapPost("/modules/", async (ModuleCreateRequest req, CardRepository repo, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.Json(new { detail = "'name' ist erforderlich" }, statusCode: 400);
            var m = await repo.CreateModuleAsync(req, Uid(http));
            return Results.Json(ModuleDto(m), statusCode: 201);
        });

        secured.MapDelete("/modules/{moduleId}", async (string moduleId, CardRepository repo, HttpContext http) =>
        {
            try
            {
                return await repo.DeleteModuleAsync(moduleId, Uid(http)) == 0
                    ? Results.Json(new { detail = "Module not found" }, statusCode: 404)
                    : Results.NoContent();
            }
            catch (InvalidOperationException e) { return Results.Json(new { detail = e.Message }, statusCode: 409); }
        });

        // ─── Karten ─────────────────────────────────────────────────────────
        // Mit module_id: Karten eines lesbaren Moduls (eigen, freigegeben, global); ohne module_id bzw. mit archived=true: nur eigene Karten.
        secured.MapGet("/cards/", async (CardRepository repo, HttpContext http, string? module_id, string? category, bool archived = false) =>
        {
            var uid = Uid(http);
            if (!string.IsNullOrEmpty(module_id) && !archived)
                return Results.Ok((await repo.ListReadableCardsAsync(uid, module_id, category)).Select(CardDto));
            return Results.Ok((await repo.ListCardsAsync(uid, module_id, category, archived)).Select(CardDto));
        });

        // Standard: Bild-Metadaten mit URL (/api/images/{id}); mit include_image_data=true zusätzlich die Bilddaten als Base64 (Export/Sicherung).
        secured.MapGet("/cards/{cardId}", async (string cardId, CardRepository repo, CardImageService images, HttpContext http, bool include_image_data = false) =>
        {
            if (await repo.GetReadableCardAsync(cardId, Uid(http)) is not { } card)
                return Results.Json(new { detail = "Card not found" }, statusCode: 404);
            if (!include_image_data) return Results.Ok(CardDto(card));
            try
            {
                var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var img in card.Images)
                    data[img.ImageId] = Convert.ToBase64String((await images.GetContentAsync(img, http.RequestAborted)).Data);
                return Results.Ok(CardDto(card, data));
            }
            catch (ImageStorageException e) { return ImageError(e); }
        });

        // Bildinhalt — unabhängig vom konfigurierten Bildspeicher immer dieselbe Antwort (ADR 0017).
        // Berechtigung: wie die Karte (eigen, freigegeben, global); der Cache liegt dahinter und umgeht sie nie.
        secured.MapGet("/images/{imageId}", async (string imageId, CardRepository repo, CardImageService images, HttpContext http) =>
        {
            if (!cfg.ImageStorage.ProxyEnabled)
                return Results.Json(new { detail = "Der Bild-Proxy ist deaktiviert (IMAGE_PROXY_ENABLED=false).", code = "proxy_disabled" }, statusCode: 503);
            var meta = await repo.GetReadableImageAsync(imageId, Uid(http));
            if (meta is null) return Results.Json(new { detail = "Image not found" }, statusCode: 404);

            var etag = $"\"{meta.Checksum}\"";
            http.Response.Headers.CacheControl = "private, max-age=3600";   // nie von gemeinsamen Caches/Proxys speichern lassen
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers.ETag = etag;
            if (http.Request.Headers.IfNoneMatch.ToString().Contains(etag, StringComparison.Ordinal))
                return Results.StatusCode(304);   // Berechtigung ist geprüft; kein Providerzugriff nötig
            try
            {
                var content = await images.GetContentAsync(meta, http.RequestAborted);
                return Results.Bytes(content.Data, meta.MimeType);
            }
            catch (ImageStorageException e)
            {
                http.Response.Headers.Remove("ETag");
                http.Response.Headers.CacheControl = "no-store";
                return ImageError(e);
            }
        });

        secured.MapPost("/cards/", async (CardJson data, CardRepository repo, HttpContext http) =>
        {
            var (ok, error) = data.Validate();
            if (!ok) return Results.Json(new { detail = error }, statusCode: 400);
            try
            {
                var card = await repo.CreateCardAsync(data, Uid(http));
                return Results.Json(CardDto(card), statusCode: 201);
            }
            catch (ArgumentException e) { return Results.Json(new { detail = e.Message }, statusCode: 400); }
            catch (ImageStorageException e) { return ImageError(e); }
        });

        secured.MapPut("/cards/{cardId}", async (string cardId, CardJson data, CardRepository repo, HttpContext http) =>
        {
            try
            {
                return await repo.UpdateCardAsync(cardId, data, Uid(http)) is { } card
                    ? Results.Ok(CardDto(card))
                    : Results.Json(new { detail = "Card not found" }, statusCode: 404);
            }
            catch (ArgumentException e) { return Results.Json(new { detail = e.Message }, statusCode: 400); }
            catch (ImageStorageException e) { return ImageError(e); }
        });

        secured.MapPatch("/cards/{cardId}/archive", async (string cardId, CardRepository repo, HttpContext http) =>
            await repo.SetArchivedAsync(cardId, true, Uid(http)) is { } card
                ? Results.Ok(CardDto(card))
                : Results.Json(new { detail = "Card not found" }, statusCode: 404));

        secured.MapPatch("/cards/{cardId}/restore", async (string cardId, CardRepository repo, HttpContext http) =>
            await repo.SetArchivedAsync(cardId, false, Uid(http)) is { } card
                ? Results.Ok(CardDto(card))
                : Results.Json(new { detail = "Card not found" }, statusCode: 404));

        secured.MapDelete("/cards/{cardId}", async (string cardId, CardRepository repo, HttpContext http) =>
        {
            try
            {
                return await repo.DeleteCardAsync(cardId, Uid(http)) == 0
                    ? Results.Json(new { detail = "Card not found" }, statusCode: 404)
                    : Results.NoContent();
            }
            catch (InvalidOperationException e) { return Results.Json(new { detail = e.Message }, statusCode: 409); }
        });

        // ─── Import ─────────────────────────────────────────────────────────
        secured.MapPost("/import/cards", async (CardImportBatch batch, CardRepository repo, HttpContext http) =>
        {
            var result = await repo.ImportAsync(batch.Cards, batch.OverwriteExisting, Uid(http));
            return Results.Ok(new { created = result.Created, updated = result.Updated, skipped = result.Skipped, errors = result.Errors });
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

            var result = await repo.ImportAsync(cards, overwrite_existing, Uid(http));
            return Results.Ok(new { created = result.Created, updated = result.Updated, skipped = result.Skipped, errors = result.Errors });
        }).DisableAntiforgery();

        // Kartenquiz: startet über dieselbe Quiz-Engine wie das Modulquiz (ADR 0019). Antwort enthält die Sitzung; Lösungen gibt es erst nach der Abgabe.
        secured.MapPost("/cards/{cardId}/quiz", async (string cardId, QuizService quiz, CardRepository repo, HttpContext http) =>
        {
            try
            {
                var card = await repo.GetReadableCardAsync(cardId, Uid(http))
                    ?? throw new InvalidOperationException("Die ausgewählte Karte wurde nicht gefunden.");
                var start = await quiz.StartSessionAsync(card.ModuleId, "", cardId, 0, Uid(http), http.RequestAborted);
                return Results.Ok(StartDto(start, cardId));
            }
            catch (QuizRuleException e) { return Results.Json(new { detail = e.Message }, statusCode: e.Status); }
            catch (InvalidOperationException e) { return Results.Json(new { detail = e.Message }, statusCode: 400); }
        });

        // ─── Quiz ───────────────────────────────────────────────────────────
        secured.MapPost("/quiz/start", async (QuizStartRequest req, QuizService quiz, HttpContext http) =>
        {
            try
            {
                var start = await quiz.StartSessionAsync(req.ModuleId, req.Category, string.IsNullOrWhiteSpace(req.CardId) ? null : req.CardId, req.NumQuestions, Uid(http), http.RequestAborted);
                return Results.Ok(StartDto(start, req.CardId));
            }
            catch (QuizRuleException e) { return Results.Json(new { detail = e.Message }, statusCode: e.Status); }
            catch (InvalidOperationException e) { return Results.Json(new { detail = e.Message }, statusCode: 404); }
        });

        secured.MapPost("/quiz/submit", async (QuizSubmitRequest req, QuizService quiz, HttpContext http) =>
        {
            try { return Results.Ok(await quiz.SubmitQuizAsync(req, Uid(http), http.RequestAborted)); }
            catch (QuizRuleException e) { return Results.Json(new { detail = e.Message }, statusCode: e.Status); }
            catch (InvalidOperationException e) { return Results.Json(new { detail = e.Message }, statusCode: 404); }
        });

        // Quizkonfiguration je Modul: lesen darf jeder, der das Modul lernen darf; ändern nur der Eigentümer (freigegeben/global = schreibgeschützt)
        secured.MapGet("/modules/{moduleId}/quiz-config", (string moduleId, QuizService quiz, HttpContext http) =>
            Guard(async () => Results.Ok(QuizConfigDto(await quiz.GetConfigViewAsync(moduleId, Uid(http), http.RequestAborted)))));

        secured.MapPut("/modules/{moduleId}/quiz-config", (string moduleId, QuizConfig config, QuizService quiz, HttpContext http) =>
            Guard(async () => Results.Ok(QuizConfigDto(await quiz.SaveConfigAsync(moduleId, config, Uid(http), http.RequestAborted)))));

        // Fragenpool (enthält Lösungen → nur Eigentümer)
        secured.MapGet("/modules/{moduleId}/quiz-questions", (string moduleId, QuizService quiz, HttpContext http, string? card_id, string? origin, bool? active) =>
            Guard(async () => Results.Ok((await quiz.ListQuestionsAsync(moduleId, Uid(http), card_id, origin, active, http.RequestAborted)).Select(PoolQuestionDto))));

        // Fragen im Pool anlegen/ändern (Eigentümer; neue Fragen haben source=editor). Fragen aus quiz[] der Karte (source=card_json) werden über die Karte geändert.
        secured.MapPost("/modules/{moduleId}/quiz-questions", (string moduleId, QuizQuestionInput input, QuizService quiz, HttpContext http) =>
            Guard(async () => Results.Json(PoolQuestionDto(await quiz.CreateQuestionAsync(moduleId, input, Uid(http), http.RequestAborted)), statusCode: 201)));

        secured.MapPut("/modules/{moduleId}/quiz-questions/{questionId}", (string moduleId, string questionId, QuizQuestionInput input, QuizService quiz, HttpContext http) =>
            Guard(async () => Results.Ok(PoolQuestionDto(await quiz.UpdateQuestionAsync(moduleId, questionId, input, Uid(http), http.RequestAborted)))));

        secured.MapPut("/modules/{moduleId}/quiz-questions/{questionId}/active", (string moduleId, string questionId, StatusUpdateRequest req, QuizService quiz, HttpContext http) =>
            Guard(async () => { await quiz.SetQuestionActiveAsync(moduleId, questionId, req.Active, Uid(http), http.RequestAborted); return Results.NoContent(); }));

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
            if (await repo.GetReadableCardAsync(cardId, sub) is null)
                return Results.Json(new { detail = "Card not found" }, statusCode: 404);
            var state = await repo.GetUserCardStateAsync(sub, cardId) ?? new UserCardState { UserSub = sub, CardId = cardId };
            return Results.Ok(new { is_checked = state.IsChecked, marked_review = state.MarkedReview, updated_at = state.UpdatedAt });
        });

        secured.MapPut("/cards/{cardId}/state", async (string cardId, CardStateUpdateRequest req, CardRepository repo, HttpContext http) =>
        {
            var sub = Uid(http);
            if (await repo.GetReadableCardAsync(cardId, sub) is null)
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

    /// <summary>
    /// Persönliche Freigaben und Einreichung zur globalen Veröffentlichung (ADR 0015). Nur per Browser-Anmeldung: der Pfad
    /// <c>/api/sharing</c> steht nicht in <see cref="TokenAllowedPrefixes"/>, MCP-Tokens erhalten dort 403.
    /// </summary>
    private static void MapSharingEndpoints(RouteGroupBuilder secured)
    {
        var sh = secured.MapGroup("/sharing");

        sh.MapGet("/users", async (string? q, HttpContext http, SharingService sharing) =>
            Results.Ok((await sharing.SearchRecipientsAsync(Uid(http), q)).Select(u => new { id = u.Id, display_name = u.DisplayName, email = u.Email })));

        sh.MapGet("/outgoing", async (HttpContext http, SharingService sharing) =>
            Results.Ok((await sharing.ListOutgoingSharesAsync(Uid(http))).Select(ShareDto)));

        sh.MapGet("/modules/{moduleId}/shares", (string moduleId, HttpContext http, SharingService sharing) =>
            Guard(async () => Results.Ok((await sharing.ListSharesAsync(Uid(http), moduleId)).Select(ShareDto))));

        sh.MapPost("/modules/{moduleId}/shares", (string moduleId, ShareCreateRequest req, HttpContext http, SharingService sharing) =>
            Guard(async () => { await sharing.GrantShareAsync(Uid(http), moduleId, req.RecipientUserId); return Results.NoContent(); }));

        sh.MapDelete("/modules/{moduleId}/shares/{recipientId}", (string moduleId, string recipientId, HttpContext http, SharingService sharing) =>
            Guard(async () => await sharing.RevokeShareAsync(Uid(http), moduleId, recipientId)
                ? Results.NoContent()
                : Results.Json(new { detail = "Freigabe nicht gefunden." }, statusCode: 404)));

        sh.MapGet("/modules/{moduleId}/submissions", (string moduleId, HttpContext http, SharingService sharing) =>
            Guard(async () => Results.Ok((await sharing.ListOwnSubmissionsAsync(Uid(http), moduleId)).Select(SubmissionDto))));

        sh.MapPost("/modules/{moduleId}/submissions", (string moduleId, HttpContext http, SharingService sharing) =>
            Guard(async () => Results.Json(SubmissionDto(await sharing.SubmitAsync(Uid(http), moduleId)), statusCode: 201)));
    }

    /// <summary>Lesende Einsicht in globale Module und Ergebnisse für Administrator und Auditor (nur Browser-Anmeldung; Rolle wird im Service gegen die DB geprüft).</summary>
    private static void MapGlobalReviewEndpoints(RouteGroupBuilder secured)
    {
        var g = secured.MapGroup("/global");

        g.MapGet("/modules", (HttpContext http, SharingService sharing) =>
            Guard(async () => Results.Ok((await sharing.ListGlobalModulesForReviewAsync(Uid(http))).Select(ModuleDto))));

        g.MapGet("/results", (HttpContext http, SharingService sharing, string? publication_id) =>
            Guard(async () => Results.Ok((await sharing.GetGlobalResultSummariesAsync(Uid(http), publication_id)).Select(r => new
            {
                module_id = r.ModuleId, publication_id = r.PublicationId, name = r.Name, version = r.Version, status = r.Status,
                attempts = r.Attempts, learners = r.Learners, average_percent = r.AveragePercent,
            }))));

        g.MapGet("/results/{moduleId}", (string moduleId, HttpContext http, SharingService sharing) =>
            Guard(async () => Results.Ok((await sharing.GetGlobalResultRowsAsync(Uid(http), moduleId)).Select(r => new
            {
                id = r.ResultId, learner = r.LearnerName, category = r.Category, score = r.Score, max_score = r.MaxScore, grade = r.Grade, completed_at = r.CompletedAt,
            }))));

        g.MapGet("/audit", (HttpContext http, UserService users, int limit = 100) =>
            Guard(async () => Results.Ok(await users.ListPublicationAuditAsync(Uid(http), limit))));
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

        // Bildspeicher: Status und ausdrücklich gestartete Migration (ADR 0017) — nie automatisch, die Quelle wird nie gelöscht.
        admin.MapGet("/images/storage", async (CardImageService images, ImageMigrationService migration, AppConfig config, HttpContext http) =>
        {
            var providers = new List<object>();
            foreach (var name in images.Registry.ConfiguredNames.OrderBy(n => n))
            {
                var h = await images.Registry.Get(name).CheckHealthAsync(http.RequestAborted);
                providers.Add(new { name, primary = name == images.Registry.PrimaryName, ok = h.Ok, message = h.Message, latency_ms = (long)h.Latency.TotalMilliseconds });
            }
            return Results.Ok(new { primary = images.Registry.PrimaryName, proxy_enabled = config.ImageStorage.ProxyEnabled, providers, image_counts = await migration.CountByProviderAsync(), cache = images.Cache.Stats });
        });

        admin.MapPost("/images/migrate", (ImageMigrateRequest req, HttpContext http, ImageMigrationService migration, UserService users) =>
            Guard(async () =>
            {
                var result = await migration.MigrateAsync(req.Source, req.Target, req.Limit, req.Cursor, req.DryRun, http.RequestAborted);
                if (!req.DryRun)
                    await users.WriteAuditAsync(Uid(http), "images.migrate", "", $"{req.Source}->{req.Target} migriert={result.Migrated} fehler={result.Failed}");
                return Results.Ok(result);
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

        // ─── Veröffentlichung (ADR 0015) ───
        admin.MapGet("/submissions", (HttpContext http, SharingService sharing, string? status) =>
            Guard(async () => Results.Ok((await sharing.ListSubmissionsAsync(Uid(http), status)).Select(SubmissionDto))));

        admin.MapGet("/submissions/{id}/preview", (string id, HttpContext http, SharingService sharing) =>
            Guard(async () =>
            {
                var (sub, module, cards) = await sharing.PreviewSubmissionAsync(Uid(http), id);
                return Results.Ok(new { submission = SubmissionDto(sub), module = ModuleDto(module), cards = cards.Select(CardDto) });
            }));

        // Optionaler Body {"replace_publication_id": "<Veröffentlichungs-ID>" | "new"}: welche Veröffentlichung ersetzt wird (ohne Body: Standardverhalten)
        admin.MapPost("/submissions/{id}/approve", (string id, ApproveRequest? req, HttpContext http, SharingService sharing) =>
            Guard(async () => { await sharing.ApproveAsync(Uid(http), id, string.IsNullOrWhiteSpace(req?.ReplacePublicationId) ? null : req!.ReplacePublicationId); return Results.NoContent(); }));

        // Löscht eine globale Modulversion endgültig (inkl. Ergebnissen); Audit "publication.delete"
        admin.MapDelete("/global-modules/{moduleId}", (string moduleId, HttpContext http, SharingService sharing) =>
            Guard(async () => { await sharing.DeleteGlobalVersionAsync(Uid(http), moduleId); return Results.NoContent(); }));

        admin.MapPost("/submissions/{id}/reject", (string id, SubmissionRejectRequest req, HttpContext http, SharingService sharing) =>
            Guard(async () => { await sharing.RejectAsync(Uid(http), id, req.Reason); return Results.NoContent(); }));

        admin.MapPost("/publications/{publicationId}/deactivate", (string publicationId, HttpContext http, SharingService sharing) =>
            Guard(async () => { await sharing.SetPublicationActiveAsync(Uid(http), publicationId, false); return Results.NoContent(); }));

        admin.MapPost("/publications/{publicationId}/activate", (string publicationId, HttpContext http, SharingService sharing) =>
            Guard(async () => { await sharing.SetPublicationActiveAsync(Uid(http), publicationId, true); return Results.NoContent(); }));

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

    private static object StartDto(QuizStartResponse r, string? cardId) => new
    {
        session_id = r.SessionId, module_id = r.ModuleId, category = r.Category, card_id = string.IsNullOrWhiteSpace(cardId) ? "" : cardId,
        pass_percent = r.PassPercent, show_correct_answers = r.ShowCorrectAnswers, notices = r.Notices, questions = r.Questions,
    };

    private static object QuizConfigDto(QuizConfigView v) => new
    {
        module_id = v.ModuleId, config = v.Config, can_edit = v.CanEdit, ai_available = v.AiAvailable, module_version = v.ModuleVersion, context = v.Context,
    };

    private static object PoolQuestionDto(QuizPoolQuestion q) => new
    {
        id = q.Id, module_id = q.ModuleId, card_id = q.CardId, type = q.Type, question = q.Question, options = q.Options, correct_indexes = q.CorrectIndexes,
        explanation = q.Explanation, origin = q.Origin, source = q.Source, active = q.Active, module_version = q.ModuleVersion, created_at = q.CreatedAt,
    };

    private static object ModuleDto(ModuleInfo m) => new
    {
        id = m.Id, name = m.Name, description = m.Description, icon = m.Icon, color = m.Color,
        card_count = m.CardCount, active_count = m.ActiveCount, created_at = m.CreatedAt,
        context = m.Context, scope = m.Scope, owner_name = m.OwnerName,
        publication_id = m.PublicationId, global_version = m.GlobalVersion, global_status = m.GlobalStatus,
        source_owner_name = m.SourceOwnerName, published_at = m.PublishedAt,
    };

    private static object ShareDto(ShareInfo s) => new
    {
        module_id = s.ModuleId, module_name = s.ModuleName, recipient_user_id = s.RecipientUserId,
        recipient_name = s.RecipientName, recipient_email = s.RecipientEmail, created_at = s.CreatedAt,
    };

    private static object SubmissionDto(SubmissionInfo s) => new
    {
        id = s.Id, publication_id = s.PublicationId, kind = s.Kind, source_module_id = s.SourceModuleId, staged_module_id = s.StagedModuleId,
        submitter = s.SubmitterName, module_name = s.ModuleName, card_count = s.CardCount, version = s.Version,
        status = s.Status, reason = s.Reason, submitted_at = s.SubmittedAt, decided_at = s.DecidedAt,
    };

    /// <summary>
    /// Einheitliche Abbildung von Speicherfehlern auf HTTP-Antworten (provider-unabhängig, ADR 0017): 404 nicht vorhanden, 502 Speicher/Integrität/Berechtigung,
    /// 503 nicht konfiguriert/nicht erreichbar, 504 Zeitüberschreitung. Der Text nennt den Provider, aber keine Schlüssel oder Zugangsdaten.
    /// </summary>
    private static IResult ImageError(ImageStorageException e) => e.Kind switch
    {
        ImageStorageError.NotFound => Results.Json(new { detail = e.Message, code = "image_not_found" }, statusCode: 404),
        ImageStorageError.Timeout => Results.Json(new { detail = $"Zeitüberschreitung beim Bildspeicher '{e.Provider}'.", code = "image_storage_timeout" }, statusCode: 504),
        ImageStorageError.NotConfigured => Results.Json(new { detail = e.Message, code = "image_storage_not_configured" }, statusCode: 503),
        ImageStorageError.Unavailable => Results.Json(new { detail = $"Der Bildspeicher '{e.Provider}' ist nicht erreichbar oder überlastet.", code = "image_storage_unavailable" }, statusCode: 503),
        ImageStorageError.Unauthorized => Results.Json(new { detail = $"Der Bildspeicher '{e.Provider}' hat den Zugriff der Anwendung abgelehnt (Konfiguration/Berechtigung prüfen).", code = "image_storage_unauthorized" }, statusCode: 502),
        ImageStorageError.IntegrityFailed => Results.Json(new { detail = e.Message, code = "image_integrity_failed" }, statusCode: 502),
        _ => Results.Json(new { detail = "Ungültige Bildspeicher-Anfrage.", code = "image_storage_invalid" }, statusCode: 400),
    };

    /// <summary>Karte mit Bild-<b>Metadaten</b> und URL (ohne Bilddaten) — für Listen und Schreibantworten.</summary>
    private static object CardDto(Card c) => CardDto(c, null);

    /// <summary>Wie oben; mit <paramref name="base64ByImageId"/> zusätzlich <c>data_base64</c> je Bild (nur Einzelabruf mit include_image_data=true).</summary>
    private static object CardDto(Card c, Dictionary<string, string>? base64ByImageId) => new
    {
        id = c.Id, module_id = c.ModuleId, category = c.Category, term = c.Term,
        question = c.Question, definition = c.Definition, how_it_works = c.HowItWorks,
        context = c.Context, key_fact = c.KeyFact, reference_answer = c.ReferenceAnswer,
        chat_prompt = c.ChatPrompt, official_sources = c.OfficialSources,
        slide_number = c.SlideNumber, target_time_sec = c.TargetTimeSec, quiz = c.Quiz,
        images = c.Images.Select(i => base64ByImageId is not null && base64ByImageId.TryGetValue(i.ImageId, out var b64)
            ? (object)new { image_id = i.ImageId, url = "/api/images/" + i.ImageId, mime_type = i.MimeType, size_bytes = i.SizeBytes, checksum = i.Checksum, alt_text = i.AltText, caption = i.Caption, sort_order = i.SortOrder, data_base64 = b64 }
            : new { image_id = i.ImageId, url = "/api/images/" + i.ImageId, mime_type = i.MimeType, size_bytes = i.SizeBytes, checksum = i.Checksum, alt_text = i.AltText, caption = i.Caption, sort_order = i.SortOrder }),
        archived = c.Archived, sort_order = c.SortOrder, created_at = c.CreatedAt, updated_at = c.UpdatedAt,
    };
}

public class ApproveRequest { public string? ReplacePublicationId { get; set; } }
public class ImageMigrateRequest { public string Source { get; set; } = ""; public string Target { get; set; } = ""; public int Limit { get; set; } = 200; public string? Cursor { get; set; } public bool DryRun { get; set; } }
public class DisplayNameUpdateRequest { public string DisplayName { get; set; } = ""; }
public class RolesUpdateRequest { public List<string> Roles { get; set; } = new(); }
public class StatusUpdateRequest { public bool Active { get; set; } }
public class OwnerAssignRequest { public string OwnerUserId { get; set; } = ""; }
public class ShareCreateRequest { public string RecipientUserId { get; set; } = ""; }
public class SubmissionRejectRequest { public string Reason { get; set; } = ""; }
public class McpCodeExchangeRequest { public string Code { get; set; } = ""; }
public class McpLinkCreateRequest { public string Name { get; set; } = ""; public int Days { get; set; } = 90; }
public class McpConsentRequest
{
    public string ReturnUrl { get; set; } = "";
    public string State { get; set; } = "";
    public string ClientName { get; set; } = "";
    public bool Approve { get; set; }
}
