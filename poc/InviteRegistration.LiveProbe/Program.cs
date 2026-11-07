using Microsoft.AspNetCore.Builder;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using InviteRegistration;
using LearnCards.Web.Data;

// Manueller Livetest: echte Anmeldung gegen Entra External ID, echte Tokenprüfung, temporäre SQLite-Datenbank, kein Mailversand.
// Pflicht-Umgebung: PROBE_EMAIL (E-Mail des Testkontos), ENTRA_CLIENT_SECRET, ENTRA_TENANT_ID, ENTRA_SUBDOMAIN, ENTRA_CLIENT_ID.
// Ausgabe: Einladungslink (lokal, einmalig), Claim-Namen und ausgewählte unkritische Claim-Werte, Ergebnis der Einlösung. Nie Tokens/Secret.

string Need(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v : throw new InvalidOperationException($"{n} fehlt.");
var email = Need("PROBE_EMAIL");
var tenant = Need("ENTRA_TENANT_ID"); var sub = Need("ENTRA_SUBDOMAIN"); var clientId = Need("ENTRA_CLIENT_ID");
const string baseUrl = "https://localhost:5001";
var tmp = Path.Combine(Path.GetTempPath(), "inv-probe-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmp);

var db = new SqliteDatabase(Path.Combine(tmp, "probe.db")); await db.InitAsync();
var admin = Guid.NewGuid().ToString(); var now = DateTime.UtcNow;
await db.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id,'Probe-Admin','admin@probe.test','active',0,@n,@n)", new (string, object?)[] { ("id", admin), ("n", now) });
await db.ExecuteAsync("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@u,'admin',NULL,@n)", new (string, object?)[] { ("u", admin), ("n", now) });
var inv = new InvitationService(db, new InvitationOptions { BaseUrl = baseUrl, TokenKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32) });
await inv.InitAsync();
var modA = Guid.NewGuid().ToString(); var modB = Guid.NewGuid().ToString();
await db.ExecuteAsync("INSERT INTO poc_modules (id,name,scope) VALUES (@a,'A global','global'),(@b,'B global','global')", new (string, object?)[] { ("a", modA), ("b", modB) });
var invitation = await inv.CreateAsync(admin, email, TargetProfiles.B2cLearner, new[] { modA });

var shown = new HashSet<string> { "iss", "aud", "sub", "tid", "ver", "email", "email_verified", "idp", "amr", "acr", "preferred_username", "name", "emails", "oid", "iat", "exp", "nonce" };
var flow = new EntraOidcFlow(new HttpClient(), new EntraOidcOptions
{
    Authority = $"https://{sub}.ciamlogin.com/{tenant}/v2.0", ClientId = clientId, ClientSecret = Need("ENTRA_CLIENT_SECRET"), RedirectUri = baseUrl + "/auth/callback",
    ExpectedTenantId = tenant,
    EmailVerifiedFallback = EmailVerificationPolicies.OtpIdpOnly,
    OnClaimsObserved = claims =>
    {
        Console.WriteLine("\n[Token] Claims im ID-Token:");
        foreach (var (k, v) in claims.OrderBy(c => c.Key))
            Console.WriteLine($"  {k,-20} {(k is "sub" or "oid" or "nonce" or "aud" or "tid" or "iat" or "exp" ? "(vorhanden)" : shown.Contains(k) ? v.ToString() : "(vorhanden)")}");
    },
});

// Mailmodus (--send-mail): Versand der Einladung ueber den ExchangeOnlineMailService; das Flag ist die ausdrueckliche Administratorbestaetigung.
var sendMail = args.Contains("--send-mail");
IInvitationMailer mailer = new NoMailer();
if (sendMail)
{
    using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
    store.Open(OpenFlags.ReadOnly);
    var cert = store.Certificates.Find(X509FindType.FindByThumbprint, Need("MAIL_CERT_THUMBPRINT"), false).OfType<X509Certificate2>().FirstOrDefault() ?? throw new InvalidOperationException("Zertifikat nicht gefunden.");
    mailer = new ExchangeOnlineMailService(new HttpClient(), new ExchangeMailOptions
    {
        Enabled = true, TenantId = Need("MAIL_TENANT_ID"), ForbiddenTenantIds = new[] { tenant }, ClientId = Need("MAIL_CLIENT_ID"), ClientCertificate = cert,
        SenderMailbox = Need("MAIL_SENDER"), AllowedRecipients = new[] { email },
    });
}
var delivery = new InvitationDeliveryService(db, inv, mailer);
await using var app = InviteApp.Create(new InviteAppOptions { Url = baseUrl, Db = db, Invitations = inv, Delivery = delivery, Oidc = flow, AdminKey = Guid.NewGuid().ToString("N"), AdminUserId = admin });
await app.StartAsync();

// OTP-Extension (--otp-extension): eigener, schlanker Host NUR fuer POST /extensions/otp-send (fuer den oeffentlichen Tunnel). Setzt --send-mail voraus (gleicher Mailservice).
WebApplication? otpApp = null;
if (args.Contains("--otp-extension"))
{
    if (!sendMail) throw new InvalidOperationException("--otp-extension setzt --send-mail voraus (gleicher Mailservice, gleiche Mail-App).");
    var otpOpt = new OtpExtensionOptions
    {
        Authority = $"https://{sub}.ciamlogin.com/{tenant}/v2.0",
        Audiences = Need("OTP_EXT_AUDIENCES").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        ExpectedTenantId = tenant,
        Audit = (ev, reason) => { Console.WriteLine($"[OTP] {reason}"); return Task.CompletedTask; },   // nur Statuscode und Ergebnis, nie Code/Adresse/Token
    };
    var otpHandler = new OtpSendHandler(new ExtensionTokenValidator(new HttpClient(), otpOpt), (IOtpMailer)mailer, new InvitationOtpRecipientPolicy(inv), otpOpt);
    otpApp = InviteApp.CreateOtpOnly("http://127.0.0.1:5080", otpHandler);
    await otpApp.StartAsync();
    Console.WriteLine("OTP-Endpunkt (nur dieser Pfad): http://127.0.0.1:5080/extensions/otp-send");
}

Console.WriteLine($"Live-Probe läuft auf {baseUrl}. Testkonto-E-Mail: {email}");
if (sendMail)
{
    var sent = await delivery.SendAsync(admin, invitation.InvitationId, invitation.Token, invitation.Link, confirmed: true);
    Console.WriteLine($"Einladung per E-Mail ueber {Need("MAIL_SENDER")} an {email}: {sent.Decision} ({sent.Code}), Versandstatus={await delivery.GetStatusAsync(invitation.InvitationId)}");
    Console.WriteLine("Der Link steht nur in der E-Mail (nicht hier). Mail oeffnen und den Link in einem privaten Browserfenster aufrufen.\n");
}
else
{
    Console.WriteLine("Einladungslink (nur lokal, einmalig gültig) in einem privaten Browserfenster öffnen:\n");
    Console.WriteLine("  " + invitation.Link + "\n");
}
Console.WriteLine("Registrierung/Anmeldung mit dem E-Mail-Einmalcode durchführen. Beenden mit Strg+C.\n");

var last = "";
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
while (!cts.IsCancellationRequested)
{
    var rows = await db.QueryAsync("SELECT event, reason FROM invitation_audit WHERE event IN ('redeemed','redeem_failed') ORDER BY at DESC LIMIT 1");
    var st = (await db.QueryAsync("SELECT status FROM invitations"))[0]["status"];
    var cur = $"{st}/{(rows.Count > 0 ? rows[0]["event"] + ":" + rows[0]["reason"] : "-")}";
    if (cur != last) { Console.WriteLine($"[Status] Einladung={st}, letztes Ergebnis={(rows.Count > 0 ? rows[0]["event"] + " " + rows[0]["reason"] : "-")}"); last = cur; }
    try { await Task.Delay(1500, cts.Token); } catch (OperationCanceledException) { }
}
await app.StopAsync();
if (otpApp is not null) await otpApp.StopAsync();
try { Directory.Delete(tmp, true); } catch { }

sealed class NoMailer : IInvitationMailer
{
    public Task<MailSendResult> SendAsync(InvitationMail mail, CancellationToken ct = default) => Task.FromResult(new MailSendResult(MailOutcome.Rejected, "probe_no_mail"));
}
