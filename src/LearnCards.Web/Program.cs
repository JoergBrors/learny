using LearnCards.Web.Api;
using LearnCards.Web.Auth;
using LearnCards.Web.Components;
using LearnCards.Web.Domain;
using LearnCards.Web.Data;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;
using LearnCards.Web.Services.ImageStorage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

// .env laden (Repo-Wurzel oder Arbeitsverzeichnis); echte Umgebungsvariablen haben Vorrang
EnvLoader.Load();

var builder = WebApplication.CreateBuilder(args);
var cfg = AppConfig.Load();

// ─── Dienste ────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(cfg);
builder.Services.AddHttpClient();

builder.Services.AddSingleton<IDatabase>(_ => cfg.DbProvider == DbProvider.Postgres
    ? new PostgresDatabase(cfg.Postgres)
    : new SqliteDatabase(cfg.SqlitePath));
// Bildspeicher (ADR 0017): Registry (primärer Provider + weitere konfigurierte), Cache, Bilddienst
builder.Services.AddSingleton<ImageStorageRegistry>();
builder.Services.AddSingleton<IImageCache>(sp => ImageCacheFactory.Create(cfg.ImageStorage, out FileImageCache? _));
builder.Services.AddSingleton<CardImageService>();
builder.Services.AddSingleton<QuizPoolService>();
builder.Services.AddSingleton<ImageMigrationService>();
builder.Services.AddSingleton<CardRepository>();
builder.Services.AddSingleton<OpenAiClient>();
builder.Services.AddSingleton<QuizService>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<OidcService>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<McpLinkService>();
builder.Services.AddSingleton<SharingService>();

// DataProtection-Schlüssel persistieren (Login-Cookies überleben Neustarts; ./data auch als Docker-Volume)
var keysDir = Path.GetFullPath(Environment.GetEnvironmentVariable("DATA_PROTECTION_DIR") ?? "data/keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .SetApplicationName("LearnCards")
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "lc.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(10);
        options.LoginPath = "/login";
        // Bei jeder Anfrage: Benutzer existiert und ist aktiv; Rollen frisch aus der DB (nicht aus dem Cookie).
        // Alte Cookies ohne "uid" (vor ADR 0011) werden abgelehnt → erneuter Login.
        options.Events.OnValidatePrincipal = async ctx =>
        {
            var users = ctx.HttpContext.RequestServices.GetRequiredService<UserService>();
            var user = await users.GetAsync(ctx.Principal.GetUserId());
            if (user is null || !user.IsActive)
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }
            if (ctx.Principal?.Identity is System.Security.Claims.ClaimsIdentity identity)
            {
                foreach (var old in identity.FindAll(System.Security.Claims.ClaimTypes.Role).ToList()) identity.RemoveClaim(old);
                foreach (var role in user.Roles) identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role));
            }
        };
        options.Events.OnRedirectToLogin = ctx =>
        {
            // API-Aufrufe bekommen 401 statt HTML-Redirect
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = 401;
                return Task.CompletedTask;
            }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

// JSON global auf snake_case (kompatibel zum Python-Original und Karten-JSON-Format)
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Hinter Reverse Proxy (nginx / Azure) korrektes Schema/Host für OIDC-Redirects
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedFor,
};
fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapAuthEndpoints();
app.MapLearnCardsApi();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ─── Datenbank initialisieren ──────────────────────────────────────────────
var db = app.Services.GetRequiredService<IDatabase>();
await db.InitAsync();
// Bilder aus der früheren Speicherung (images_json, ADR 0016) einmalig in card_images überführen — idempotent, ohne Datenverlust
var syncedQuizCards = await app.Services.GetRequiredService<CardRepository>().SyncLegacyQuizAsync();   // quiz[] → Fragenpool (ADR 0019)
if (syncedQuizCards > 0) app.Logger.LogInformation("Quiz: {Count} Karten in den Fragenpool übernommen.", syncedQuizCards);
await app.Services.GetRequiredService<QuizService>().CleanupSessionsIfDueAsync(force: true);   // abgelaufene Quizsitzungen entfernen (ADR 0019)
var imageService = app.Services.GetRequiredService<CardImageService>();
var convertedImages = await imageService.MigrateLegacyInlineImagesAsync();
if (convertedImages > 0) app.Logger.LogInformation("Bilder von {Count} Karten in den Bildspeicher überführt (images_json bleibt unverändert).", convertedImages);
var primaryHealth = await imageService.Registry.Primary.CheckHealthAsync();
app.Logger.Log(primaryHealth.Ok ? LogLevel.Information : LogLevel.Warning, "Bildspeicher '{Provider}': {Message} ({Ms} ms){Cache}",
    imageService.Registry.PrimaryName, primaryHealth.Message, (int)primaryHealth.Latency.TotalMilliseconds,
    imageService.Cache.Enabled ? ", Cache aktiv" : ", Cache aus");
app.Logger.LogInformation("LearnCards gestartet — DB-Provider: {Provider}, Auth-Modus: {Auth}, OpenAI: {OpenAi}, .env: {Env}",
    db.ProviderName, cfg.AuthMode, cfg.OpenAiConfigured ? "konfiguriert" : "nicht konfiguriert", EnvLoader.LoadedFrom ?? "—");

app.Run();
