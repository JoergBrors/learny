using LearnCards.Web.Api;
using LearnCards.Web.Auth;
using LearnCards.Web.Components;
using LearnCards.Web.Data;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;
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
builder.Services.AddSingleton<CardRepository>();
builder.Services.AddSingleton<OpenAiClient>();
builder.Services.AddSingleton<QuizService>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<OidcService>();

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
app.Logger.LogInformation("LearnCards gestartet — DB-Provider: {Provider}, Auth-Modus: {Auth}, OpenAI: {OpenAi}, .env: {Env}",
    db.ProviderName, cfg.AuthMode, cfg.OpenAiConfigured ? "konfiguriert" : "nicht konfiguriert", EnvLoader.LoadedFrom ?? "—");

app.Run();
