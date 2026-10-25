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
            "Create a single learning card in the LearnCards platform. Use only source-grounded facts and preserve the canonical import structure. Optional images[]: each entry needs mime_type (image/png, image/jpeg or image/webp) and data_base64 (plain standard Base64 of the real image file, no 'data:' prefix); alt_text and caption are optional. Limits: max 5 images per card, max 2 MiB per decoded image; prefer small images (< 200 KiB) because Base64 inside tool arguments consumes model context. Only include images you were actually given; never invent Base64 data. Call get_import_schema for the exact, current rules.",
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
            "Import multiple learning cards at once. Always consult get_import_schema first and keep answers grounded in the provided official sources. Cards may contain images[] (mime_type, data_base64, alt_text, caption) as described in the schema: real files only, standard Base64 without a 'data:' prefix, max 5 images per card and 2 MiB per decoded image. Many or large images make the request very large — import such cards in small batches; the server reports rejected cards with a reason in the result. With overwrite_existing=true an omitted images field keeps existing images, [] removes them.",
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
        app.MapGet(prefix + "/authorize", (HttpRequest request, HttpResponse response, ProprietaryOAuthService auth, McpSettings cfg) =>
        {
            if (!auth.IsEnabled)
                return Results.NotFound();

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

        app.MapPost(prefix + "/register", async (HttpRequest request, ProprietaryOAuthService auth) =>
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
