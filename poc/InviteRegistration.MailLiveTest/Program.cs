using System.Security.Cryptography.X509Certificates;
using InviteRegistration;

// Manueller Livetest des ExchangeOnlineMailService. OHNE --send passiert nichts (Trockenlauf, keine Netzwerkaufrufe).
// Umgebung: MAIL_TENANT_ID, MAIL_CLIENT_ID, MAIL_CERT_THUMBPRINT (CurrentUser\My), MAIL_SENDER, MAIL_OTHER_MAILBOX, MAIL_TEST_RECIPIENT.
// Test A: Versand ueber das konfigurierte Postfach an den freigegebenen Testempfaenger -> erwartet: Accepted (202).
// Test B: derselbe Service Principal versucht, ueber ein ANDERES Postfach zu senden -> erwartet: Rejected permission_denied (403).
// Der Testlink ist ein Platzhalter ohne Einladung; es werden keine Tokens/Secrets ausgegeben.

string Need(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v : throw new InvalidOperationException($"{n} fehlt.");
var send = args.Contains("--send");
var tenant = Need("MAIL_TENANT_ID"); var client = Need("MAIL_CLIENT_ID"); var sender = Need("MAIL_SENDER");
var other = Need("MAIL_OTHER_MAILBOX"); var recipient = Need("MAIL_TEST_RECIPIENT"); var thumb = Need("MAIL_CERT_THUMBPRINT");
const string ExternalTenant = "5fce2b3c-8e95-49b5-9cfd-fc223b7e8f63";

Console.WriteLine($"Tenant {tenant}, App {client}, Absender {sender}, anderes Postfach {other}, Empfaenger {recipient}");
if (!send) { Console.WriteLine("TROCKENLAUF: nichts gesendet. Mit --send wuerden Test A und Test B ausgefuehrt."); return 0; }

using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
store.Open(OpenFlags.ReadOnly);
var cert = store.Certificates.Find(X509FindType.FindByThumbprint, thumb, false).OfType<X509Certificate2>().FirstOrDefault() ?? throw new InvalidOperationException("Zertifikat nicht gefunden.");
var http = new HttpClient();
ExchangeOnlineMailService Svc(string mailbox) => new(http, new ExchangeMailOptions
{
    Enabled = true, TenantId = tenant, ForbiddenTenantIds = new[] { ExternalTenant }, ClientId = client, ClientCertificate = cert,
    SenderMailbox = mailbox, AllowedRecipients = new[] { recipient },
});
var mail = new InvitationMail(recipient, "https://localhost:5001/invite/TESTLINK-OHNE-GUELTIGKEIT", DateTime.UtcNow.AddHours(1));

var a = await Svc(sender).SendAsync(mail);
Console.WriteLine($"Test A (erlaubtes Postfach {sender}): {a.Outcome}/{a.Code}  -> {(a.Outcome == MailOutcome.Accepted ? "BESTANDEN" : "FEHLGESCHLAGEN")}");
var b = await Svc(other).SendAsync(mail);
var bOk = b.Outcome == MailOutcome.Rejected && b.Code == "permission_denied";
Console.WriteLine($"Test B (anderes Postfach {other}): {b.Outcome}/{b.Code}  -> {(bOk ? "BESTANDEN (verweigert)" : b.Outcome == MailOutcome.Accepted ? "SICHERHEITSFEHLER: Versand ueber fremdes Postfach moeglich!" : "FEHLGESCHLAGEN (unerwartetes Ergebnis)")}");
return a.Outcome == MailOutcome.Accepted && bOk ? 0 : 1;
