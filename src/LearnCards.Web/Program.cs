using LearnCards.Web.Api;
using LearnCards.Web.Auth;
using LearnCards.Web.Components;
using LearnCards.Web.Domain;
using LearnCards.Web.Data;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;
using LearnCards.Web.Services.Certificates;
using LearnCards.Web.Services.ImageStorage;
using LearnCards.Web.Services.Pki;
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
builder.Services.AddSingleton<McpTokenProtector>();   // ADR 0025
builder.Services.AddSingleton<McpLinkService>();
builder.Services.AddSingleton<McpOAuthClientStore>();
builder.Services.AddSingleton<SharingService>();
builder.Services.AddSingleton<PdfVerificationService>();   // Upload-Prüfung signierter Zertifikats-PDFs (ADR 0033)
builder.Services.AddSingleton<LearnCards.Web.Services.Certificates.Ots.IOtsClient, LearnCards.Web.Services.Certificates.Ots.NativeOtsClient>();   // nativer OpenTimestamps-Client (ADR 0040)
builder.Services.AddSingleton<LearnCards.Web.Services.Certificates.Ots.OtsMaintenanceService>();
builder.Services.AddSingleton<CertificatePdfService>();   // signierte Zertifikats-PDFs (ADR 0033)
builder.Services.AddSingleton<PdfSigningProvider>();   // PDF-Signaturzertifikat (ADR 0033)
builder.Services.AddSingleton<PkiKeyStore>();   // Learny-PKI (ADR 0035)
builder.Services.AddSingleton<PkiService>();
builder.Services.AddSingleton<PkiVerifier>();
builder.Services.AddHostedService<PkiMaintenanceService>();
builder.Services.AddSingleton<TopicCredentialService>();   // Themenabschluss: Master-Badge + CLR (ADR 0032)
builder.Services.AddSingleton<CompetencyService>();   // Kompetenzmodell und Prüfungssnapshots (ADR 0031)
builder.Services.AddSingleton<ModuleVersionService>();   // Modulversionen (ADR 0030)
builder.Services.AddSingleton<ModuleTransferService>();   // Modul-JSON, Klonen (ADR 0028)
builder.Services.AddSingleton<TopicService>();   // Themen (ADR 0027)
// Zertifikate, Open Badges und Blockchain-Verankerung (ADR 0021/0022)
builder.Services.AddSingleton<BadgeSigner>();
builder.Services.AddSingleton<ChainSettingsStore>();
builder.Services.AddSingleton<IChainClientFactory, ChainClientFactory>();
builder.Services.AddSingleton<IPriceFeed, HttpPriceFeed>();
builder.Services.AddSingleton<IAnchorSigner>(sp =>
{
    var b = sp.GetRequiredService<AppConfig>().Blockchain;
    return b.Simulate || b.TestSigner
        ? new PseudoAnchorSigner(b.WalletAddress.Length > 0 ? b.WalletAddress : "0x0000000000000000000000000000000000000001")   // nur Simulation/Test (Konfigurationsprüfung: lokaler RPC)
        : new UnavailableAnchorSigner(b.WalletAddress);
});
builder.Services.AddSingleton<CertificateService>();
builder.Services.AddSingleton<CredentialRegisterService>();   // Ausstellungsregister (ADR 0037)
builder.Services.AddSingleton<AnchorService>();
builder.Services.AddHostedService<AnchorWorker>();

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

if (app.Environment.IsDevelopment() && string.IsNullOrEmpty(cfg.Blockchain.PublicBaseUrl))
{
    // Entwicklung ohne PUBLIC_BASE_URL: Badge-URLs zeigen auf die tatsächlich genutzte Adresse (Host/Port der Anfrage).
    var certSvc = app.Services.GetRequiredService<CertificateService>();
    app.Use(async (ctx, next) => { certSvc.ObservedOrigin ??= $"{ctx.Request.Scheme}://{ctx.Request.Host}"; await next(); });
}
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
var badgeSigner = app.Services.GetRequiredService<BadgeSigner>();   // Schlüssel laden bzw. beim ersten Start erzeugen (ADR 0021)
if (badgeSigner.GeneratedKeyFile) app.Logger.LogWarning("Badge-Signaturschlüssel wurde lokal in {File} erzeugt. Für Produktion BADGE_SIGNING_KEY_PEM aus einer Secret-Verwaltung setzen und die Datei sichern (ohne den Schlüssel sind ausgestellte Badges nicht mehr prüfbar).", cfg.Blockchain.BadgeKeyFile);
if (badgeSigner.GeneratedKeyFile && (await db.QueryAsync("SELECT id FROM certificates LIMIT 1")).Count > 0)
{
    // Es gibt bereits Zertifikate, aber der Schlüssel musste neu erzeugt werden: Schlüsselverlust, kein Neubeginn. Den frisch erzeugten Schlüssel samt Marker wieder entfernen
    // (sonst würde der nächste Start ihn stillschweigend verwenden) und abbrechen.
    try { File.Delete(cfg.Blockchain.BadgeKeyFile); File.Delete(BadgeSigner.MarkerPath(cfg.Blockchain.BadgeKeyFile)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    throw new InvalidOperationException("Es existieren bereits Zertifikate, der Badge-Signaturschlüssel fehlt aber. Es wird kein neuer Schlüssel verwendet, damit ausgestellte Badges gültig bleiben. Schlüssel aus der Sicherung wiederherstellen (docs/iis-deployment.md).");
}
// Learny-PKI (ADR 0035): beim allerersten Start einmalig einrichten (nie bei späteren Starts); --pki-init richtet sie ausdrücklich ein und beendet das Programm
var pki = app.Services.GetRequiredService<PkiService>();
var pkiFallbackUrl = app.Environment.IsDevelopment()
    ? (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5000").Split(';')[0].Replace("0.0.0.0", "localhost").Replace("+", "localhost").Replace("*", "localhost")
    : null;
if (args.Contains("--pki-init"))
{
    var pkiResult = await pki.InitializeAsync(null, cfg.Pki.PublicUrl.Length > 0 ? cfg.Pki.PublicUrl : pkiFallbackUrl, system: true);
    var rootOutIdx = Array.IndexOf(args, "--root-out");
    if (rootOutIdx >= 0 && rootOutIdx + 1 < args.Length)
    {
        var target = Path.GetFullPath(args[rootOutIdx + 1]);
        var keyStore = app.Services.GetRequiredService<PkiKeyStore>();
        File.Copy(keyStore.RootPath, target, overwrite: false);
        if (keyStore.ReadRootPassphrase() is { } rp && cfg.Pki.RootPassphrase.Length == 0) File.WriteAllText(target + ".passphrase", rp);
        keyStore.RemoveRoot();
        Console.WriteLine($"PKI eingerichtet. Root-Schlüssel (verschlüsselt) nach {target} geschrieben und vom Server entfernt.");
    }
    else Console.WriteLine("PKI eingerichtet. Der Root-Schlüssel liegt noch unter " + Path.Combine(cfg.Pki.KeyDir, PkiKeyStore.RootDir) + " — offline sichern und in Administration → PKI entfernen.");
    Console.WriteLine($"Status: {pkiResult.State}");
    return;
}
var pkiAuto = await pki.TryAutoInitializeAsync(pkiFallbackUrl);
if (pkiAuto.Length > 0) app.Logger.LogInformation("PKI: {Message}", pkiAuto);
var pdfSigning = app.Services.GetRequiredService<PdfSigningProvider>();   // konfiguriertes PDF-Signaturzertifikat beim Start prüfen (ADR 0033); unbrauchbar = Startabbruch
pdfSigning.EnsureValidAtStartup();
if (pdfSigning.Configured) app.Logger.LogInformation("PDF-Signatur: Zertifikat {Thumbprint}{Dev}.", pdfSigning.Thumbprint, pdfSigning.IsDevCertificate ? " (selbstsigniertes Entwicklungszertifikat, nicht vertrauenswürdig)" : "");
else app.Logger.LogWarning("PDF-Signatur: kein Signaturzertifikat konfiguriert (PDF_SIGNING_CERT_FILE / PDF_SIGNING_CERT_PFX_BASE64) — signierte Zertifikats-PDFs sind nicht verfügbar.");
var imageService = app.Services.GetRequiredService<CardImageService>();
var convertedImages = await imageService.MigrateLegacyInlineImagesAsync();
if (convertedImages > 0) app.Logger.LogInformation("Bilder von {Count} Karten in den Bildspeicher überführt (images_json bleibt unverändert).", convertedImages);
var primaryHealth = await imageService.Registry.Primary.CheckHealthAsync();
app.Logger.Log(primaryHealth.Ok ? LogLevel.Information : LogLevel.Warning, "Bildspeicher '{Provider}': {Message} ({Ms} ms){Cache}",
    imageService.Registry.PrimaryName, primaryHealth.Message, (int)primaryHealth.Latency.TotalMilliseconds,
    imageService.Cache.Enabled ? ", Cache aktiv" : ", Cache aus");
if (cfg.AuthMode == "dev" && !app.Environment.IsDevelopment())
    app.Logger.LogWarning("ACHTUNG: AUTH_MODE=dev außerhalb von Development — jeder erhält ohne Passwort Administratorrechte. Nur zum lokalen Ausprobieren, nie in Produktion.");
app.Logger.LogInformation("LearnCards gestartet — DB-Provider: {Provider}, Auth-Modus: {Auth}, OpenAI: {OpenAi}, .env: {Env}",
    db.ProviderName, cfg.AuthMode, cfg.OpenAiConfigured ? "konfiguriert" : "nicht konfiguriert", EnvLoader.LoadedFrom ?? "—");

app.Run();
