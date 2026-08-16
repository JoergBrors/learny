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

    app.MapGet("/.well-known/oauth-protected-resource", (HttpRequest request, ProprietaryOAuthService auth, McpSettings cfg) =>
    {
        if (!auth.IsEnabled) return Results.NotFound();
        return Results.Json(ProtectedResourceMetadata(request, cfg, null));
    });

    app.MapGet("/.well-known/oauth-protected-resource/{**resourcePath}", (HttpRequest request, ProprietaryOAuthService auth, McpSettings cfg, string? resourcePath) =>
    {
        if (!auth.IsEnabled) return Results.NotFound();
        return Results.Json(ProtectedResourceMetadata(request, cfg, resourcePath));
    });

    app.MapGet("/.well-known/oauth-authorization-server", (HttpRequest request, ProprietaryOAuthService auth, McpSettings cfg) =>
    {
        if (!auth.IsEnabled) return Results.NotFound();
        return Results.Json(auth.AuthorizationServerMetadata(MetadataIssuer(request, cfg)));
    });

    app.MapGet("/.well-known/openid-configuration", (HttpRequest request, ProprietaryOAuthService auth, McpSettings cfg) =>
    {
        if (!auth.IsEnabled) return Results.NotFound();
        return Results.Json(auth.AuthorizationServerMetadata(MetadataIssuer(request, cfg)));
    });

    app.MapGet("/oauth/authorize", (HttpRequest request, ProprietaryOAuthService auth, McpSettings cfg) =>
    {
        if (!auth.IsEnabled)
            return Results.NotFound();

        var query = request.Query;
        var responseType = query["response_type"].FirstOrDefault();
        if (!string.Equals(responseType, "code", StringComparison.Ordinal))
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

        return Results.Content(RenderConsentPage(authorizeRequest), "text/html; charset=utf-8");
    }).AllowAnonymous();

    app.MapPost("/oauth/authorize", async (HttpRequest request, ProprietaryOAuthService auth, McpSettings cfg) =>
    {
        if (!auth.IsEnabled)
            return Results.NotFound();

        IFormCollection form;
        if (request.HasFormContentType)
            form = await request.ReadFormAsync();
        else
            return Results.BadRequest("invalid_request");

        var action = form["action"].FirstOrDefault();
        var clientId = form["client_id"].FirstOrDefault() ?? "";
        var redirectUri = form["redirect_uri"].FirstOrDefault() ?? "";
        var state = form["state"].FirstOrDefault();
        if (!string.Equals(action, "approve", StringComparison.Ordinal))
            return Results.Redirect(AppendAuthorizeResult(redirectUri, null, state, "access_denied"));

        OAuthAuthorizeRequest authorizeRequest;
        try
        {
            authorizeRequest = auth.ValidateAuthorizationRequest(
                clientId,
                redirectUri,
                form["scope"].FirstOrDefault(),
                state,
                form["code_challenge"].FirstOrDefault(),
                form["code_challenge_method"].FirstOrDefault(),
                form["resource"].FirstOrDefault(),
                MetadataIssuer(request, cfg));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Redirect(AppendAuthorizeResult(redirectUri, null, state, ex.Message));
        }

        var code = auth.IssueAuthorizationCode(authorizeRequest);
        return Results.Redirect(AppendAuthorizeResult(redirectUri, code, authorizeRequest.State, null));
    }).AllowAnonymous();

    app.MapPost("/oauth/register", async (HttpRequest request, ProprietaryOAuthService auth) =>
    {
        if (!auth.IsEnabled)
            return Results.NotFound();

        JsonObject? body;
        try
        {
            body = await JsonNode.ParseAsync(request.Body) as JsonObject;
        }
        catch
        {
            return Results.Json(new { error = "invalid_client_metadata" }, statusCode: 400);
        }

        if (body is null)
            return Results.Json(new { error = "invalid_client_metadata" }, statusCode: 400);

        var redirectUris = body["redirect_uris"]?.AsArray()
            .Select(n => n?.GetValue<string>() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray() ?? Array.Empty<string>();

        var scopes = body["scope"]?.GetValue<string>()?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? Array.Empty<string>();

        var tokenEndpointAuthMethod = body["token_endpoint_auth_method"]?.GetValue<string>() ?? "client_secret_post";
        var applicationType = body["application_type"]?.GetValue<string>() ?? "web";
        var clientName = body["client_name"]?.GetValue<string>();

        try
        {
            var client = auth.RegisterClient(clientName, redirectUris, tokenEndpointAuthMethod, scopes, applicationType);
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
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 400);
        }
    }).AllowAnonymous();

    app.MapGet("/authorize", (HttpRequest request, ProprietaryOAuthService auth, McpSettings cfg) =>
    {
        if (!auth.IsEnabled)
            return Results.NotFound();

        var query = request.Query;
        var responseType = query["response_type"].FirstOrDefault();
        if (!string.Equals(responseType, "code", StringComparison.Ordinal))
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

        return Results.Content(RenderConsentPage(authorizeRequest), "text/html; charset=utf-8");
    }).AllowAnonymous();

    app.MapPost("/authorize", async (HttpRequest request, ProprietaryOAuthService auth, McpSettings cfg) =>
    {
        if (!auth.IsEnabled)
            return Results.NotFound();

        IFormCollection form;
        if (request.HasFormContentType)
            form = await request.ReadFormAsync();
        else
            return Results.BadRequest("invalid_request");

        var action = form["action"].FirstOrDefault();
        var clientId = form["client_id"].FirstOrDefault() ?? "";
        var redirectUri = form["redirect_uri"].FirstOrDefault() ?? "";
        var state = form["state"].FirstOrDefault();
        if (!string.Equals(action, "approve", StringComparison.Ordinal))
            return Results.Redirect(AppendAuthorizeResult(redirectUri, null, state, "access_denied"));

        OAuthAuthorizeRequest authorizeRequest;
        try
        {
            authorizeRequest = auth.ValidateAuthorizationRequest(
                clientId,
                redirectUri,
                form["scope"].FirstOrDefault(),
                state,
                form["code_challenge"].FirstOrDefault(),
                form["code_challenge_method"].FirstOrDefault(),
                form["resource"].FirstOrDefault(),
                MetadataIssuer(request, cfg));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Redirect(AppendAuthorizeResult(redirectUri, null, state, ex.Message));
        }

        var code = auth.IssueAuthorizationCode(authorizeRequest);
        return Results.Redirect(AppendAuthorizeResult(redirectUri, code, authorizeRequest.State, null));
    }).AllowAnonymous();

    app.MapPost("/register", async (HttpRequest request, ProprietaryOAuthService auth) =>
    {
        if (!auth.IsEnabled)
            return Results.NotFound();

        JsonObject? body;
        try
        {
            body = await JsonNode.ParseAsync(request.Body) as JsonObject;
        }
        catch
        {
            return Results.Json(new { error = "invalid_client_metadata" }, statusCode: 400);
        }

        if (body is null)
            return Results.Json(new { error = "invalid_client_metadata" }, statusCode: 400);

        var redirectUris = body["redirect_uris"]?.AsArray()
            .Select(n => n?.GetValue<string>() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray() ?? Array.Empty<string>();

        var scopes = body["scope"]?.GetValue<string>()?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? Array.Empty<string>();

        var tokenEndpointAuthMethod = body["token_endpoint_auth_method"]?.GetValue<string>() ?? "client_secret_post";
        var applicationType = body["application_type"]?.GetValue<string>() ?? "web";
        var clientName = body["client_name"]?.GetValue<string>();

        try
        {
            var client = auth.RegisterClient(clientName, redirectUris, tokenEndpointAuthMethod, scopes, applicationType);
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
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 400);
        }
    }).AllowAnonymous();

    app.MapPost("/oauth/token", async (HttpRequest request, ProprietaryOAuthService auth) =>
    {
        if (!auth.IsEnabled)
            return Results.NotFound();

        string? clientId;
        string? clientSecret;
        string? scope;
        string? grantType;
        string? code;
        string? redirectUri;
        string? codeVerifier;
        string? resource;

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            clientId = form["client_id"].FirstOrDefault();
            clientSecret = form["client_secret"].FirstOrDefault();
            scope = form["scope"].FirstOrDefault();
            grantType = form["grant_type"].FirstOrDefault();
            code = form["code"].FirstOrDefault();
            redirectUri = form["redirect_uri"].FirstOrDefault();
            codeVerifier = form["code_verifier"].FirstOrDefault();
            resource = form["resource"].FirstOrDefault();
        }
        else
        {
            var body = await JsonNode.ParseAsync(request.Body);
            clientId = body?["client_id"]?.GetValue<string>();
            clientSecret = body?["client_secret"]?.GetValue<string>();
            scope = body?["scope"]?.GetValue<string>();
            grantType = body?["grant_type"]?.GetValue<string>();
            code = body?["code"]?.GetValue<string>();
            redirectUri = body?["redirect_uri"]?.GetValue<string>();
            codeVerifier = body?["code_verifier"]?.GetValue<string>();
            resource = body?["resource"]?.GetValue<string>();
        }

        try
        {
            OAuthTokenResult token = grantType switch
            {
                "client_credentials" => auth.IssueClientCredentialsToken(clientId ?? "", clientSecret ?? "", scope),
                "authorization_code" => auth.ExchangeAuthorizationCode(
                    clientId ?? "",
                    clientSecret,
                    code ?? "",
                    redirectUri ?? "",
                    codeVerifier,
                    resource,
                    MetadataIssuer(request, settings)),
                _ => throw new InvalidOperationException("unsupported_grant_type"),
            };
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

    app.MapPost("/token", async (HttpRequest request, ProprietaryOAuthService auth) =>
    {
        if (!auth.IsEnabled)
            return Results.NotFound();

        string? clientId;
        string? clientSecret;
        string? scope;
        string? grantType;
        string? code;
        string? redirectUri;
        string? codeVerifier;
        string? resource;

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            clientId = form["client_id"].FirstOrDefault();
            clientSecret = form["client_secret"].FirstOrDefault();
            scope = form["scope"].FirstOrDefault();
            grantType = form["grant_type"].FirstOrDefault();
            code = form["code"].FirstOrDefault();
            redirectUri = form["redirect_uri"].FirstOrDefault();
            codeVerifier = form["code_verifier"].FirstOrDefault();
            resource = form["resource"].FirstOrDefault();
        }
        else
        {
            var body = await JsonNode.ParseAsync(request.Body);
            clientId = body?["client_id"]?.GetValue<string>();
            clientSecret = body?["client_secret"]?.GetValue<string>();
            scope = body?["scope"]?.GetValue<string>();
            grantType = body?["grant_type"]?.GetValue<string>();
            code = body?["code"]?.GetValue<string>();
            redirectUri = body?["redirect_uri"]?.GetValue<string>();
            codeVerifier = body?["code_verifier"]?.GetValue<string>();
            resource = body?["resource"]?.GetValue<string>();
        }

        try
        {
            OAuthTokenResult token = grantType switch
            {
                "client_credentials" => auth.IssueClientCredentialsToken(clientId ?? "", clientSecret ?? "", scope),
                "authorization_code" => auth.ExchangeAuthorizationCode(
                    clientId ?? "",
                    clientSecret,
                    code ?? "",
                    redirectUri ?? "",
                    codeVerifier,
                    resource,
                    MetadataIssuer(request, settings)),
                _ => throw new InvalidOperationException("unsupported_grant_type"),
            };
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
    }).AllowAnonymous();

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
        if (!AuthorizeHttpRequest(request, response, auth, settings))
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

static bool AuthorizeHttpRequest(HttpRequest request, HttpResponse response, ProprietaryOAuthService oauth, McpSettings settings)
{
    if (!oauth.IsEnabled)
        return true;

    var header = request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        || !oauth.TryValidateBearerToken(header[7..].Trim(), out _, out _))
    {
        response.StatusCode = 401;
        response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{ProtectedResourceMetadataUrl(request, settings, "mcp")}\"";
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
        ["scopes_supported"] = new JsonArray(settings.OAuth.Clients
            .SelectMany(c => c.Scopes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => (JsonNode?)JsonValue.Create(s)!)
            .ToArray()),
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
    if (!string.IsNullOrWhiteSpace(code))
        query.Add("code=" + Uri.EscapeDataString(code));
    if (!string.IsNullOrWhiteSpace(state))
        query.Add("state=" + Uri.EscapeDataString(state));
    if (!string.IsNullOrWhiteSpace(error))
        query.Add("error=" + Uri.EscapeDataString(error));
    uri.Query = string.Join("&", query);
    return uri.Uri.ToString();
}

static string RenderConsentPage(OAuthAuthorizeRequest request)
{
    var scopes = string.Join(", ", request.Scopes);
    var clientName = string.IsNullOrWhiteSpace(request.Client.ClientName) ? request.Client.ClientId : request.Client.ClientName;
    return $$"""
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <title>LearnCards MCP Authorization</title>
      <style>
        body { font-family: Segoe UI, Arial, sans-serif; background: #f6f7fb; color: #1f2937; margin: 0; padding: 32px; }
        .card { max-width: 560px; margin: 40px auto; background: white; border-radius: 18px; padding: 28px; box-shadow: 0 20px 50px rgba(15, 23, 42, 0.12); }
        h1 { margin-top: 0; font-size: 28px; }
        p { line-height: 1.5; }
        .meta { background: #f8fafc; border: 1px solid #e5e7eb; border-radius: 12px; padding: 16px; margin: 20px 0; }
        .meta strong { display: inline-block; min-width: 90px; }
        .actions { display: flex; gap: 12px; margin-top: 24px; }
        button { border: 0; border-radius: 12px; padding: 12px 18px; font-size: 16px; cursor: pointer; }
        .primary { background: #0f766e; color: white; }
        .secondary { background: #e5e7eb; color: #111827; }
      </style>
    </head>
    <body>
      <div class="card">
        <h1>Authorize LearnCards MCP Access</h1>
        <p>The client <strong>{{clientName}}</strong> wants to access your LearnCards MCP server.</p>
        <div class="meta">
          <div><strong>Client</strong> {{request.Client.ClientId}}</div>
          <div><strong>Scopes</strong> {{scopes}}</div>
          <div><strong>Resource</strong> {{request.Resource}}</div>
          <div><strong>Redirect</strong> {{request.RedirectUri}}</div>
        </div>
        <form method="post" action="/oauth/authorize">
          <input type="hidden" name="client_id" value="{{request.Client.ClientId}}">
          <input type="hidden" name="redirect_uri" value="{{request.RedirectUri}}">
          <input type="hidden" name="scope" value="{{string.Join(' ', request.Scopes)}}">
          <input type="hidden" name="state" value="{{request.State}}">
          <input type="hidden" name="code_challenge" value="{{request.CodeChallenge}}">
          <input type="hidden" name="code_challenge_method" value="{{request.CodeChallengeMethod}}">
          <input type="hidden" name="resource" value="{{request.Resource}}">
          <div class="actions">
            <button class="primary" type="submit" name="action" value="approve">Allow</button>
            <button class="secondary" type="submit" name="action" value="deny">Deny</button>
          </div>
        </form>
      </div>
    </body>
    </html>
    """;
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
