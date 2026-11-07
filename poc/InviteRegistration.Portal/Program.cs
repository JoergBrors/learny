using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using InviteRegistration;
using InviteRegistration.Portal;
using LearnCards.Web.Data;

// Start des Registrierungs-PoC (Administrations- und Benutzerportal). Konfiguration ausschließlich über Umgebungsvariablen (siehe docs/entra-external-id/poc-portal.md).
// Außerhalb von Development: HTTPS und Workforce-OIDC sind Pflicht; fehlende Angaben führen zum Startabbruch (ADR 0026), nie zu einem stillen Rückfall.

string Env(string name, string? fallback = null) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback ?? throw new InvalidOperationException($"Umgebungsvariable {name} fehlt.");
string? OptEnv(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

var development = string.Equals(Environment.GetEnvironmentVariable("PORTAL_MODE"), "development", StringComparison.OrdinalIgnoreCase);
var url = Env("PORTAL_URL", development ? "https://localhost:5001" : null);
var publicBase = Env("PORTAL_PUBLIC_BASE_URL", url);
var dataDir = Path.GetFullPath(Env("PORTAL_DATA_DIR", Path.Combine(AppContext.BaseDirectory, "data")));
Directory.CreateDirectory(dataDir);

// Token-HMAC-Schlüssel: muss über Neustarts gleich bleiben. Nur in Development wird er bei Bedarf lokal erzeugt.
byte[] tokenKey;
if (OptEnv("PORTAL_INVITE_TOKEN_KEY") is { } tk) tokenKey = Convert.FromBase64String(tk);
else if (development)
{
    var f = Path.Combine(dataDir, "invite-token.key");
    if (!File.Exists(f)) File.WriteAllText(f, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    tokenKey = Convert.FromBase64String(File.ReadAllText(f).Trim());
}
else throw new InvalidOperationException("PORTAL_INVITE_TOKEN_KEY (Base64, mindestens 32 Byte) ist Pflicht.");

var db = new SqliteDatabase(Path.Combine(dataDir, "portal.db"));

// Anmeldung der Lernenden: Microsoft Entra External ID (Code + PKCE, form_post); Issuer und Schlüssel kommen aus der Discovery.
var externalTenant = Env("PORTAL_ENTRA_TENANT_ID");
var externalAuthority = Env("PORTAL_ENTRA_AUTHORITY");
var claimsStore = OptEnv("PORTAL_CLAIMS_ENABLED") == "1" ? new LearnerClaimsStore() : null;   // Claims der Anmeldung für die Sitzung (ADR 0047)
var tokenViewer = development && OptEnv("PORTAL_SHOW_TOKEN") != "0" ? new IdTokenStore() : null;   // nur Development
var userOidc = new EntraOidcFlow(new HttpClient(), new EntraOidcOptions
{
    Authority = externalAuthority, ClientId = Env("PORTAL_ENTRA_CLIENT_ID"),
    // Ohne Secret (nur Development) startet das Portal; die echte Entra-Anmeldung schlägt dann fail-closed fehl (Code-Austausch nicht möglich).
    ClientSecret = development ? (OptEnv("PORTAL_ENTRA_CLIENT_SECRET") ?? "") : Env("PORTAL_ENTRA_CLIENT_SECRET"),
    RedirectUri = Env("PORTAL_ENTRA_REDIRECT", publicBase.TrimEnd('/') + "/auth/callback"), ExpectedTenantId = externalTenant,
    EmailVerifiedFallback = EmailVerificationPolicies.OtpOrLocalAccount,   // E-Mail-OTP und lokale Konten des External Tenants; föderierte Anbieter bleiben gesperrt
    ForceReauthentication = OptEnv("PORTAL_FORCE_REAUTH") != "0",
    // Optional überschreibbar (z. B. PORTAL_ENTRA_SCOPE="openid profile email"); <client-id>/.default wurde getestet und von Entra abgelehnt (AADSTS1100001).
    Scope = OptEnv("PORTAL_ENTRA_SCOPE") ?? "openid profile email",
    OnIdTokenValidated = tokenViewer is null && claimsStore is null ? null : (sub, jwt) => { tokenViewer?.Put(sub, jwt); claimsStore?.Put(sub, jwt); },
    OnFailure = code => _ = new InviteEvents(db).LogAsync(EventTypes.RegistrationRejected, null, null, "oidc:" + code),   // nur Kurzcode, nie Token oder Antwort
});

// Administratoranmeldung: getrennter, ausdrücklich vertrauter Workforce-Issuer (z. B. dieselbe App wie Learny). Administrator ist, wer die konfigurierte App-Rolle
// (PORTAL_ADMIN_ROLES, z. B. Admin.All) im ID-Token hat oder dessen Subject in PORTAL_ADMIN_SUBJECTS steht. Die Rolle admin wird nur hier vergeben.
IOidcFlow? adminOidc = null; AdminBootstrap? bootstrap = null;
if (OptEnv("PORTAL_ADMIN_AUTHORITY") is { } adminAuthority)
{
    var adminSecret = OptEnv("PORTAL_ADMIN_CLIENT_SECRET");
    if (adminSecret is null && !development) throw new InvalidOperationException("PORTAL_ADMIN_CLIENT_SECRET fehlt (Pflicht außerhalb von Development).");
    if (adminSecret is null) Console.WriteLine("Hinweis: PORTAL_ADMIN_CLIENT_SECRET fehlt: Die Administratoranmeldung über Microsoft ist aus (nur lokaler Entwicklungszugang).");
    else
    {
        var adminIssuer = Env("PORTAL_ADMIN_ISSUER", adminAuthority);   // bei Workforce-v2 entspricht der Issuer der Authority
        adminOidc = new EntraOidcFlow(new HttpClient(), new EntraOidcOptions
        {
            Authority = adminAuthority, ClientId = Env("PORTAL_ADMIN_CLIENT_ID"), ClientSecret = adminSecret,
            RedirectUri = Env("PORTAL_ADMIN_REDIRECT", publicBase.TrimEnd('/') + "/admin/callback"), ExpectedTenantId = Env("PORTAL_ADMIN_TENANT_ID"), ForceReauthentication = false,
        });
        var externalIssuers = new[] { $"https://{externalTenant}.ciamlogin.com/{externalTenant}/v2.0" };
        string[] List(string name) => (OptEnv(name) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bootstrap = new AdminBootstrap(db, adminIssuer, List("PORTAL_ADMIN_SUBJECTS"), externalIssuers, null, List("PORTAL_ADMIN_ROLES"));
        if (bootstrap.AllowedCount == 0) throw new InvalidOperationException("Weder PORTAL_ADMIN_ROLES noch PORTAL_ADMIN_SUBJECTS ist gesetzt: Niemand könnte Administrator werden.");
    }
}

// Mailversand: im Portal einrichtbar (App-Registrierung + Zertifikat, Seite „Mail-Einrichtung“). Eine Einrichtung per Umgebung (PORTAL_MAIL_ENABLED=1) bleibt als Rückfall.
ExchangeOnlineMailService? envMail = null;
if (OptEnv("PORTAL_MAIL_ENABLED") == "1")
{
    using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
    store.Open(OpenFlags.ReadOnly);
    var cert = store.Certificates.Find(X509FindType.FindByThumbprint, Env("MAIL_CERT_THUMBPRINT"), false).OfType<X509Certificate2>().FirstOrDefault() ?? throw new InvalidOperationException("Mail-Zertifikat nicht gefunden.");
    envMail = new ExchangeOnlineMailService(new HttpClient(), new ExchangeMailOptions
    {
        Enabled = true, TenantId = Env("MAIL_TENANT_ID"), ForbiddenTenantIds = new[] { externalTenant }, ClientId = Env("MAIL_CLIENT_ID"), ClientCertificate = cert,
        SenderMailbox = Env("MAIL_SENDER", "learny@brors.link"),
        AllowedRecipients = (OptEnv("PORTAL_MAIL_ALLOWED_RECIPIENTS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    });
}
var mailSettings = new MailSettingsService(db, Path.Combine(dataDir, "mail"), tokenKey,
    new MailDefaults(OptEnv("MAIL_TENANT_ID") ?? "", OptEnv("MAIL_CLIENT_ID") ?? "", OptEnv("MAIL_SENDER") ?? "learny@brors.link"), new[] { externalTenant });
var configurableMailer = new ConfigurableMailer(mailSettings, envMail);
IInvitationMailer mailer = configurableMailer;

// OTP-Extension (Entra erzeugt und validiert den Code): eigener Port, nur dieser Pfad, kein Zugriff auf die Verwaltung.
OtpSendHandler? otp = null;
if (OptEnv("PORTAL_OTP_ENABLED") == "1")
{
    var otpOpt = new OtpExtensionOptions
    {
        Authority = externalAuthority, ExpectedTenantId = externalTenant,
        Audiences = Env("OTP_EXT_AUDIENCES").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Audit = (_, result) => new InviteEvents(db).LogAsync(EventTypes.OtpSend, null, null, result),   // „Status:Code“, ohne Adresse und Code
    };
    var invitations = new InvitationService(db, new InvitationOptions { BaseUrl = publicBase, TokenKey = tokenKey });
    otp = new OtpSendHandler(new ExtensionTokenValidator(new HttpClient(), otpOpt), configurableMailer, new InvitationOtpRecipientPolicy(invitations), otpOpt);
}

// Token-Anreicherung (OnTokenIssuanceStart): ergänzt learny_*-Claims; gleicher Extension-Host, eigener Pfad. Claims sind nur ein Hinweis, der Zugriff wird weiter gegen die Datenbank geprüft.
TokenClaimsHandler? claimsHandler = null;
if (OptEnv("PORTAL_CLAIMS_ENABLED") == "1")
{
    var claimsOpt = new OtpExtensionOptions
    {
        Authority = externalAuthority, ExpectedTenantId = externalTenant,
        Audiences = Env("OTP_EXT_AUDIENCES").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    };
    claimsHandler = new TokenClaimsHandler(new ExtensionTokenValidator(new HttpClient(), claimsOpt), db, new ContentAccess(db), new TokenClaimsOptions
    {
        ExpectedClientAppId = Env("PORTAL_ENTRA_CLIENT_ID"),
        Audit = (_, result) => new InviteEvents(db).LogAsync(EventTypes.TokenClaims, null, null, result),
    });
}

var portal = PortalHost.Build(new PortalOptions
{
    Url = url, PublicBaseUrl = publicBase, Db = db, KeyDir = Path.Combine(dataDir, "keys"), TokenKey = tokenKey, UserOidc = userOidc, Mailer = mailer, MailSettings = mailSettings, TokenViewer = tokenViewer, ClaimsStore = claimsStore,
    AdminOidc = adminOidc, AdminBootstrap = bootstrap, Development = development, DevAdminKey = development ? OptEnv("PORTAL_DEV_ADMIN_KEY") : null,
    OtpHandler = otp, ClaimsHandler = claimsHandler, OtpUrl = Env("PORTAL_OTP_URL", "http://127.0.0.1:5080"), SeedDemoData = OptEnv("PORTAL_SEED_DEMO") == "1",
});
await portal.StartAsync();
Console.WriteLine($"Learny-Registrierungs-PoC läuft auf {url} (Modus: {(development ? "development" : "production")}). Daten: {dataDir}");
if (claimsHandler is not null) Console.WriteLine("Token-Anreicherung (nur dieser Pfad): " + Env("PORTAL_OTP_URL", "http://127.0.0.1:5080") + "/extensions/token-claims");
if (otp is not null) Console.WriteLine("OTP-Endpunkt (nur dieser Pfad): " + Env("PORTAL_OTP_URL", "http://127.0.0.1:5080") + "/extensions/otp-send");
Console.WriteLine("Beenden mit Strg+C.");
var done = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
await done.Task;
await portal.StopAsync();
