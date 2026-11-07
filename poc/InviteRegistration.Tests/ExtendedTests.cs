using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InviteRegistration;
using LearnCards.Web.Data;

// Versand-, Exchange- und HTTP-Tests (ADR 0043/0044). Werden aus Program.cs nach den 68 Einladungstests ausgeführt.
static partial class ExtendedTests
{
    const string Issuer = "https://tenant.ciamlogin.com/t/v2.0";
    const string AdminKey = "poc-admin-key-for-tests";

    sealed class FakeMailer : IInvitationMailer
    {
        public ConcurrentQueue<MailSendResult> Results { get; } = new();
        public ConcurrentQueue<InvitationMail> Sent { get; } = new();
        public int DelayMs { get; set; }
        public bool Throw { get; set; }
        public async Task<MailSendResult> SendAsync(InvitationMail mail, CancellationToken ct = default)
        {
            Sent.Enqueue(mail);
            if (DelayMs > 0) await Task.Delay(DelayMs, ct);
            if (Throw) throw new HttpRequestException("boom");
            return Results.TryDequeue(out var r) ? r : new(MailOutcome.Accepted, "accepted");
        }
    }

    sealed class FakeOidc : IOidcFlow
    {
        public ConcurrentDictionary<string, VerifiedIdentity?> Codes { get; } = new();
        readonly ConcurrentDictionary<string, string> _challenges = new();
        public string BuildAuthorizeUrl(string state, string nonce, string codeChallenge)
        {
            _challenges[nonce] = codeChallenge;
            return $"https://idp.test/authorize?state={Uri.EscapeDataString(state)}&nonce={Uri.EscapeDataString(nonce)}";
        }
        public Task<VerifiedIdentity?> ExchangeAndValidateAsync(string code, string codeVerifier, string nonce, CancellationToken ct)
        {
            if (!Codes.TryRemove(code, out var id) || id is null) return Task.FromResult<VerifiedIdentity?>(null);   // Code nur einmal
            var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Task.FromResult(_challenges.TryGetValue(nonce, out var c) && c == challenge ? id : null);   // PKCE und nonce müssen passen
        }
    }

    sealed record Env(IDatabase Db, string Path, InvitationService Inv, InvitationDeliveryService Delivery, FakeMailer Mailer, string Admin, string A, string B, string C, byte[] Key);

    static async Task<Env> NewEnv(string tmp, byte[] key, Func<DateTime>? clock = null, int leaseSeconds = 60, int maxAttempts = 5)
    {
        var path = System.IO.Path.Combine(tmp, Guid.NewGuid().ToString("N")[..8] + ".db");
        var db = new SqliteDatabase(path); await db.InitAsync();
        var now = DateTime.UtcNow;
        var admin = Guid.NewGuid().ToString();
        await db.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id,'Admin','admin@x.test','active',0,@n,@n)", new (string, object?)[] { ("id", admin), ("n", now) });
        await db.ExecuteAsync("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@u,'admin',NULL,@n)", new (string, object?)[] { ("u", admin), ("n", now) });
        var inv = new InvitationService(db, new InvitationOptions { BaseUrl = "https://poc.example.org", TokenKey = key, Clock = clock ?? (() => DateTime.UtcNow) });
        await inv.InitAsync();
        var (a, b, c) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        await db.ExecuteAsync("INSERT INTO poc_modules (id,name,scope) VALUES (@a,'A global','global'),(@b,'B global','global'),(@c,'C shared','shared')", new (string, object?)[] { ("a", a), ("b", b), ("c", c) });
        var mailer = new FakeMailer();
        return new Env(db, path, inv, new InvitationDeliveryService(db, inv, mailer, clock, leaseSeconds, maxAttempts), mailer, admin, a, b, c, key);
    }

    static VerifiedIdentity Id(string email, string? sub = null, bool verified = true) => new(Issuer, sub ?? Guid.NewGuid().ToString(), email, verified);
    static async Task<long> Count(IDatabase db, string sql, params (string, object?)[] a) => long.Parse((await db.QueryAsync(sql, a))[0]["n"]!);
    static async Task<string> InvStatus(IDatabase db, string id) => (await db.QueryAsync("SELECT status FROM invitations WHERE invitation_id=@i", new (string, object?)[] { ("i", id) }))[0]["status"]!;
    static Task<long> NonAdminUsers(IDatabase db) => Count(db, "SELECT COUNT(*) AS n FROM users WHERE id NOT IN (SELECT user_id FROM user_roles)");

    public static async Task RunAsync(Action<string, bool, string?> check, string tmp, byte[] key)
    {
        await DeliveryTests(check, tmp, key);
        await ExchangeTests(check);
        await OidcTests(check);
        await MailHardeningTests(check, tmp, key);
        await OtpExtensionTests(check, tmp, key);
        await TokenClaimsTests(check, tmp, key);
        BrandingTests(check);
        await PortalTests(check, tmp, key);
        await PortalRoleTests(check, tmp);
        await MailSettingsTests(check, tmp);
        await HttpTests(check, tmp, key);
    }

    // ─── Versandstatus getrennt vom Einladungsstatus ───────────────────

    static async Task DeliveryTests(Action<string, bool, string?> check, string tmp, byte[] key)
    {
        Console.WriteLine("Versand: getrennte Zustände, Wiederholung, Rotation");
        {
            var e = await NewEnv(tmp, key);
            var inv = await e.Inv.CreateAsync(e.Admin, "v@y.test", TargetProfiles.B2cLearner, new[] { e.A });
            check("Neue Einladung: Versandstatus pending, kein Versand", await e.Delivery.GetStatusAsync(inv.InvitationId) == "pending" && e.Mailer.Sent.IsEmpty, null);
            check("Ohne Bestätigung kein Versand", (await e.Delivery.SendAsync(e.Admin, inv.InvitationId, inv.Token, inv.Link, false)).Decision == SendDecision.NotConfirmed && e.Mailer.Sent.IsEmpty, null);
            var r = await e.Delivery.SendAsync(e.Admin, inv.InvitationId, inv.Token, inv.Link, true);
            check("Bestätigter Versand → submitted, Einladung weiter pending", r.Decision == SendDecision.Sent && await e.Delivery.GetStatusAsync(inv.InvitationId) == "submitted"
                && await InvStatus(e.Db, inv.InvitationId) == "pending" && e.Mailer.Sent.Single().Link == inv.Link, r.Decision.ToString());
            check("Versandtabelle/Audit enthalten keinen Klartexttoken", await Count(e.Db,
                "SELECT (SELECT COUNT(*) FROM invitation_deliveries WHERE last_error_code=@t OR status=@t) + (SELECT COUNT(*) FROM invitation_audit WHERE reason LIKE @l OR event=@t) AS n", ("t", inv.Token), ("l", "%" + inv.Token + "%")) == 0, null);
            check("submitted ≠ eingelöst: Einlösung weiter möglich", (await e.Inv.RedeemAsync(inv.Token, Id("v@y.test"))).Success, null);
        }
        {
            var e = await NewEnv(tmp, key);
            var inv = await e.Inv.CreateAsync(e.Admin, "f@y.test", TargetProfiles.B2cLearner);
            e.Mailer.Results.Enqueue(new(MailOutcome.Rejected, "permission_denied"));
            var r = await e.Delivery.SendAsync(e.Admin, inv.InvitationId, inv.Token, inv.Link, true);
            check("Fehlgeschlagener Versand → failed, Einladung unverbraucht", r.Decision == SendDecision.Failed && await e.Delivery.GetStatusAsync(inv.InvitationId) == "failed"
                && await InvStatus(e.Db, inv.InvitationId) == "pending" && await e.Inv.IsRedeemableAsync(inv.Token), null);
            var r2 = await e.Delivery.SendAsync(e.Admin, inv.InvitationId, inv.Token, inv.Link, true);
            check("Wiederholung desselben Links nach failed → submitted, weiterhin genau eine Einladung", r2.Decision == SendDecision.Sent && await Count(e.Db, "SELECT COUNT(*) AS n FROM invitations") == 1, null);
        }
        {
            var e = await NewEnv(tmp, key);
            var inv = await e.Inv.CreateAsync(e.Admin, "u@y.test", TargetProfiles.B2cLearner);
            e.Mailer.Results.Enqueue(new(MailOutcome.Ambiguous, "timeout"));
            var r = await e.Delivery.SendAsync(e.Admin, inv.InvitationId, inv.Token, inv.Link, true);
            check("Unklares Ergebnis → unknown, nicht verbraucht", r.Decision == SendDecision.Unknown && await e.Delivery.GetStatusAsync(inv.InvitationId) == "unknown" && await InvStatus(e.Db, inv.InvitationId) == "pending", null);
            var r2 = await e.Delivery.SendAsync(e.Admin, inv.InvitationId, inv.Token, inv.Link, true);
            check("Erneuter Versand aus unknown mit demselben Link (kein zweiter aktiver Link)", r2.Decision == SendDecision.Sent && e.Mailer.Sent.All(m => m.Link == inv.Link) && await Count(e.Db, "SELECT COUNT(*) AS n FROM invitations") == 1, null);
            e.Mailer.Throw = true;
            var inv2 = await e.Inv.CreateAsync(e.Admin, "x@y.test", TargetProfiles.B2cLearner);
            var r3 = await e.Delivery.SendAsync(e.Admin, inv2.InvitationId, inv2.Token, inv2.Link, true);
            check("Ausnahme im Mailer → unknown, Einladung unverbraucht", r3.Decision == SendDecision.Unknown && await InvStatus(e.Db, inv2.InvitationId) == "pending", null);
        }
        {
            var e = await NewEnv(tmp, key);
            var inv = await e.Inv.CreateAsync(e.Admin, "r@y.test", TargetProfiles.B2cLearner, new[] { e.A });
            await e.Delivery.SendAsync(e.Admin, inv.InvitationId, inv.Token, inv.Link, true);
            var again = await e.Delivery.SendAsync(e.Admin, inv.InvitationId, inv.Token, inv.Link, true);
            check("Zweiter Versand nach submitted wird verweigert (nur über Rotation)", again.Decision == SendDecision.RequiresRotation && e.Mailer.Sent.Count == 1, null);
            var rot = await e.Inv.RotateAsync(e.Admin, inv.InvitationId);
            check("Rotation liefert neuen Link derselben Einladung", rot is not null && rot.InvitationId == inv.InvitationId && rot.Token != inv.Token && await Count(e.Db, "SELECT COUNT(*) AS n FROM invitations") == 1, null);
            check("Alter Link sofort ungültig", !await e.Inv.IsRedeemableAsync(inv.Token) && (await e.Inv.RedeemAsync(inv.Token, Id("r@y.test"))).Outcome == RedeemOutcome.UnknownToken, null);
            check("Alter Token kann nicht mehr versendet werden", (await e.Delivery.SendAsync(e.Admin, inv.InvitationId, inv.Token, inv.Link, true)).Decision == SendDecision.NotSendable, null);
            check("Rotation setzt Versandstatus zurück, Versand des neuen Links möglich", await e.Delivery.GetStatusAsync(inv.InvitationId) == "pending"
                && (await e.Delivery.SendAsync(e.Admin, inv.InvitationId, rot!.Token, rot.Link, true)).Decision == SendDecision.Sent, null);
            check("Neuer Link löst genau einmal ein", (await e.Inv.RedeemAsync(rot!.Token, Id("r@y.test"))).Success && !(await e.Inv.RedeemAsync(rot.Token, Id("r@y.test"))).Success, null);
            check("Rotation nach Einlösung nicht möglich", await e.Inv.RotateAsync(e.Admin, inv.InvitationId) is null, null);
            var denied = false; try { await e.Inv.RotateAsync(Guid.NewGuid().ToString(), inv.InvitationId); } catch (UnauthorizedAccessException) { denied = true; }
            check("Rotation nur durch Administrator", denied, null);
        }
        {
            var e = await NewEnv(tmp, key);
            var inv = await e.Inv.CreateAsync(e.Admin, "p@y.test", TargetProfiles.B2cLearner);
            e.Mailer.DelayMs = 300;
            var rs = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => e.Delivery.SendAsync(e.Admin, inv.InvitationId, inv.Token, inv.Link, true))));
            check("10 parallele Versandversuche → genau ein Mailer-Aufruf", e.Mailer.Sent.Count == 1 && rs.Count(r => r.Decision == SendDecision.Sent) == 1, e.Mailer.Sent.Count.ToString());
            check("Übrige Versuche: InProgress oder RequiresRotation", rs.Where(r => r.Decision != SendDecision.Sent).All(r => r.Decision is SendDecision.InProgress or SendDecision.RequiresRotation), null);
        }
        {
            var e = await NewEnv(tmp, key, maxAttempts: 3);
            var inv = await e.Inv.CreateAsync(e.Admin, "m@y.test", TargetProfiles.B2cLearner);
            for (var i = 0; i < 3; i++) e.Mailer.Results.Enqueue(new(MailOutcome.Rejected, "throttled"));
            for (var i = 0; i < 3; i++) await e.Delivery.SendAsync(e.Admin, inv.InvitationId, inv.Token, inv.Link, true);
            check("Versuchsgrenze erreicht → TooManyAttempts, kein weiterer Mailer-Aufruf", (await e.Delivery.SendAsync(e.Admin, inv.InvitationId, inv.Token, inv.Link, true)).Decision == SendDecision.TooManyAttempts && e.Mailer.Sent.Count == 3, null);
        }
        {
            var now = DateTime.UtcNow; var clock = now;
            var e = await NewEnv(tmp, key, () => clock);
            var exp = await e.Inv.CreateAsync(e.Admin, "e@y.test", TargetProfiles.B2cLearner);
            clock = now.AddHours(73);
            check("Abgelaufene Einladung wird nicht versendet", (await e.Delivery.SendAsync(e.Admin, exp.InvitationId, exp.Token, exp.Link, true)).Decision == SendDecision.NotSendable && e.Mailer.Sent.IsEmpty, null);
            check("Rotation abgelaufener Einladung nicht möglich", await e.Inv.RotateAsync(e.Admin, exp.InvitationId) is null, null);
            clock = now;
            var rev = await e.Inv.CreateAsync(e.Admin, "w@y.test", TargetProfiles.B2cLearner);
            await e.Inv.RevokeAsync(e.Admin, rev.InvitationId);
            check("Widerrufene Einladung wird nicht versendet", (await e.Delivery.SendAsync(e.Admin, rev.InvitationId, rev.Token, rev.Link, true)).Decision == SendDecision.NotSendable, null);
            var acc = await e.Inv.CreateAsync(e.Admin, "a@y.test", TargetProfiles.B2cLearner);
            await e.Inv.RedeemAsync(acc.Token, Id("a@y.test"));
            check("Eingelöste Einladung wird nicht versendet", (await e.Delivery.SendAsync(e.Admin, acc.InvitationId, acc.Token, acc.Link, true)).Decision == SendDecision.NotSendable, null);
        }
        {
            var e = await NewEnv(tmp, key);
            var inv = await e.Inv.CreateAsync(e.Admin, "s@y.test", TargetProfiles.B2cLearner);
            var denied = false; try { await e.Delivery.SendAsync(Guid.NewGuid().ToString(), inv.InvitationId, inv.Token, inv.Link, true); } catch (UnauthorizedAccessException) { denied = true; }
            check("Nicht-Administrator kann nicht senden", denied && e.Mailer.Sent.IsEmpty, null);
            var inv2 = await e.Inv.CreateAsync(e.Admin, "bereits@y.test", TargetProfiles.B2cLearner);
            await e.Delivery.SendAsync(e.Admin, inv2.InvitationId, inv2.Token, inv2.Link, true);
            await e.Inv.RedeemAsync((await e.Inv.CreateAsync(e.Admin, "bereits@y.test", TargetProfiles.B2cLearner)).Token, new VerifiedIdentity(Issuer, "known", "bereits@y.test", true));
            var users = await NonAdminUsers(e.Db);
            var r = await e.Inv.RedeemAsync(inv2.Token, new VerifiedIdentity(Issuer, "known", "bereits@y.test", true));
            check("Bestehender Benutzer löst versendete Einladung nicht ein", r.Outcome == RedeemOutcome.IdentityAlreadyRegistered && await NonAdminUsers(e.Db) == users && await InvStatus(e.Db, inv2.InvitationId) == "pending", null);
        }
        {
            // Rotation, Widerruf und Einlösung konkurrieren: nie zwei Gewinner.
            var okRotateAndRedeem = 0; var bothFailed = 0; var users = 0L;
            for (var round = 0; round < 15; round++)
            {
                var e = await NewEnv(tmp, key);
                var inv = await e.Inv.CreateAsync(e.Admin, "race@y.test", TargetProfiles.B2cLearner, new[] { e.A });
                var rotT = Task.Run(() => e.Inv.RotateAsync(e.Admin, inv.InvitationId));
                var redT = Task.Run(() => e.Inv.RedeemAsync(inv.Token, Id("race@y.test")));
                await Task.WhenAll(rotT, redT);
                var rotated = rotT.Result is not null; var redeemed = redT.Result.Success;
                if (rotated && redeemed) okRotateAndRedeem++;
                if (!rotated && !redeemed) bothFailed++;
                users += await NonAdminUsers(e.Db);
                if (rotated && !redeemed && !await e.Inv.IsRedeemableAsync(rotT.Result!.Token)) bothFailed++;
                var e2 = await NewEnv(tmp, key);
                var inv2 = await e2.Inv.CreateAsync(e2.Admin, "race@y.test", TargetProfiles.B2cLearner);
                var rvT = Task.Run(() => e2.Inv.RevokeAsync(e2.Admin, inv2.InvitationId));
                var rd2 = Task.Run(() => e2.Inv.RedeemAsync(inv2.Token, Id("race@y.test")));
                await Task.WhenAll(rvT, rd2);
                if (rvT.Result && rd2.Result.Success) okRotateAndRedeem++;
                users += await NonAdminUsers(e2.Db);
            }
            check("Rotation/Widerruf vs. Einlösung: nie beide erfolgreich, nie ein Benutzer ohne Gewinner (15 Runden)", okRotateAndRedeem == 0 && bothFailed == 0 && users <= 30, $"both={okRotateAndRedeem} none={bothFailed} users={users}");
        }
    }

    // ─── Exchange-Online-Mailservice (Graph, ohne Netzwerk) ────────────

    sealed class StubHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body, string? Auth)> Calls { get; } = new();
        public Func<HttpRequestMessage, string, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.Accepted);
        public Func<HttpRequestMessage, int> DelayFor { get; set; } = _ => 0;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            if (DelayFor(req) is > 0 and var d) await Task.Delay(d, ct);
            lock (Calls) Calls.Add((req.Method, req.RequestUri!.ToString(), body, req.Headers.Authorization?.ToString()));
            return Respond(req, body);
        }
    }

    static HttpResponseMessage TokenOk() => new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"tok-123\",\"expires_in\":3600}", Encoding.UTF8, "application/json") };

    static async Task ExchangeTests(Action<string, bool, string?> check)
    {
        Console.WriteLine("Exchange-Online-Mailservice");
        const string Secret = "super-secret-value";
        ExchangeMailOptions Opt(bool enabled = true, string secret = Secret) => new()
        { Enabled = enabled, TenantId = "tenant-1", ClientId = "client-1", ClientSecret = secret, SenderMailbox = "learny-invite@example.org", AllowedRecipients = new[] { "lernende@example.org" } };
        var mail = new InvitationMail("lernende@example.org", "https://poc.example.org/invite/ABC", DateTime.UtcNow.AddHours(72));
        HttpResponseMessage Route(HttpRequestMessage r, int sendStatus) =>
            r.RequestUri!.Host == "login.microsoftonline.com" ? TokenOk() : new HttpResponseMessage((HttpStatusCode)sendStatus);

        {
            var h = new StubHandler();
            var svc = new ExchangeOnlineMailService(new HttpClient(h), Opt(false));
            var r = await svc.SendAsync(mail);
            check("Standardmäßig deaktiviert: kein Versand, kein HTTP-Aufruf", r.Outcome == MailOutcome.Rejected && r.Code == "mail_disabled" && h.Calls.Count == 0, null);
            var r2 = await new ExchangeOnlineMailService(new HttpClient(h), Opt(secret: "")).SendAsync(mail);
            check("Unvollständige Konfiguration → abgelehnt (fail-closed)", r2.Code == "mail_not_configured" && h.Calls.Count == 0, null);
        }
        {
            var h = new StubHandler { Respond = (r, _) => Route(r, 202) };
            var svc = new ExchangeOnlineMailService(new HttpClient(h), Opt());
            var r = await svc.SendAsync(mail);
            var tokenCall = h.Calls[0]; var sendCall = h.Calls[1];
            check("Erfolg: 202 → Accepted", r.Outcome == MailOutcome.Accepted, r.Code);
            check("Tokenabruf: Client-Credentials, .default-Scope, Tenant-Endpunkt", tokenCall.Url == "https://login.microsoftonline.com/tenant-1/oauth2/v2.0/token"
                && tokenCall.Body.Contains("grant_type=client_credentials") && tokenCall.Body.Contains("scope=https%3A%2F%2Fgraph.microsoft.com%2F.default"), tokenCall.Body);
            check("Versand nur über das konfigurierte Postfach", sendCall.Url == "https://graph.microsoft.com/v1.0/users/learny-invite%40example.org/sendMail" && sendCall.Auth == "Bearer tok-123", sendCall.Url);
            using var doc = JsonDocument.Parse(sendCall.Body);
            check("Nachricht: Empfänger, Link, saveToSentItems=false", doc.RootElement.GetProperty("saveToSentItems").GetBoolean() == false
                && doc.RootElement.GetProperty("message").GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString() == "lernende@example.org"
                && sendCall.Body.Contains("/invite/ABC"), null);
            await svc.SendAsync(mail);
            check("Token wird zwischengespeichert (ein Tokenabruf bei zwei Sendungen)", h.Calls.Count(c => c.Url.Contains("oauth2")) == 1, null);
        }
        foreach (var (status, expectOutcome, expectCode) in new[] {
            (403, MailOutcome.Rejected, "permission_denied"), (401, MailOutcome.Rejected, "unauthorized"), (404, MailOutcome.Rejected, "mailbox_not_found"),
            (429, MailOutcome.Rejected, "throttled"), (400, MailOutcome.Rejected, "rejected_400"), (500, MailOutcome.Ambiguous, "server_500"), (503, MailOutcome.Ambiguous, "server_503") })
        {
            var h = new StubHandler { Respond = (r, _) => Route(r, status) };
            var r = await new ExchangeOnlineMailService(new HttpClient(h), Opt()).SendAsync(mail);
            check($"Graph {status} → {expectOutcome}/{expectCode}", r.Outcome == expectOutcome && r.Code == expectCode, r.Outcome + "/" + r.Code);
            check($"Graph {status}: Fehlercode enthält weder Secret, Token noch Link", !r.Code.Contains(Secret) && !r.Code.Contains("tok-123") && !r.Code.Contains("ABC"), null);
        }
        {
            var h = new StubHandler { Respond = (r, _) => r.RequestUri!.Host == "login.microsoftonline.com" ? TokenOk() : new HttpResponseMessage(HttpStatusCode.Unauthorized) };
            var svc = new ExchangeOnlineMailService(new HttpClient(h), Opt());
            await svc.SendAsync(mail); await svc.SendAsync(mail);
            check("401 verwirft den zwischengespeicherten Token (neuer Abruf)", h.Calls.Count(c => c.Url.Contains("oauth2")) == 2, null);
        }
        {
            var h = new StubHandler { Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_client\"}") } };
            var r = await new ExchangeOnlineMailService(new HttpClient(h), Opt()).SendAsync(mail);
            check("Tokenfehler → Rejected auth_failed, kein sendMail-Aufruf", r.Code == "auth_failed" && h.Calls.Count == 1, null);
            var h2 = new StubHandler { Respond = (r2, _) => throw new HttpRequestException("net") };
            var h3 = new StubHandler { Respond = (r3, _) => r3.RequestUri!.Host.Contains("login") ? TokenOk() : throw new TaskCanceledException("timeout") };
            var r3 = await new ExchangeOnlineMailService(new HttpClient(h3), Opt()).SendAsync(mail);
            check("Zeitüberschreitung beim Senden → Ambiguous", r3.Outcome == MailOutcome.Ambiguous && r3.Code == "timeout", null);
        }
        {
            var h = new StubHandler { Respond = (r, _) => Route(r, 202) };
            var svc = new ExchangeOnlineMailService(new HttpClient(h), Opt());
            var bad1 = await svc.SendAsync(mail with { Link = "https://x/invite/A\r\nBcc: evil@x.test" });
            var bad2 = await svc.SendAsync(mail with { RecipientEmail = "kein-email" });
            check("Header-Injektion und ungültige Empfänger abgelehnt", bad1.Code == "invalid_message" && bad2.Code == "invalid_message" && h.Calls.Count == 0, null);
        }
    }

    // ─── HTTP-Schicht mit Kestrel (Loopback, ohne Entra) ───────────────

    sealed record Browser(HttpClient Http, Dictionary<string, string> Jar)
    {
        /// <summary>Cookie-Pfade (RFC 6265): ein Cookie wird nur an passende Pfade gesendet, wie im echten Browser.</summary>
        public Dictionary<string, string> Paths { get; init; } = new();
        static bool PathMatches(string cookiePath, string requestPath) =>
            requestPath == cookiePath || (requestPath.StartsWith(cookiePath, StringComparison.Ordinal) && (cookiePath.EndsWith('/') || requestPath[cookiePath.Length] == '/'));
        public async Task<HttpResponseMessage> Send(HttpMethod m, string url, HttpContent? content = null, Action<HttpRequestMessage>? tweak = null, bool sendCookies = true)
        {
            var req = new HttpRequestMessage(m, url) { Content = content };
            var reqPath = new Uri(Http.BaseAddress!, url).AbsolutePath;
            var sendable = Jar.Where(kv => PathMatches(Paths.GetValueOrDefault(kv.Key, "/"), reqPath)).ToList();
            if (sendCookies && sendable.Count > 0) req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", sendable.Select(kv => kv.Key + "=" + kv.Value)));
            tweak?.Invoke(req);
            var res = await Http.SendAsync(req);
            if (res.Headers.TryGetValues("Set-Cookie", out var cs))
                foreach (var c in cs)
                {
                    var nv = c.Split(';')[0]; var i = nv.IndexOf('=');
                    var path = c.Split(';').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("path=", StringComparison.OrdinalIgnoreCase))?[5..] ?? "/";
                    if (c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)) { Jar.Remove(nv[..i]); Paths.Remove(nv[..i]); }
                    else { Jar[nv[..i]] = nv[(i + 1)..]; Paths[nv[..i]] = path; }
                }
            return res;
        }
        public Task<HttpResponseMessage> Get(string url) => Send(HttpMethod.Get, url);
        public Task<HttpResponseMessage> Post(string url, params (string, string)[] form) => Send(HttpMethod.Post, url, new FormUrlEncodedContent(form.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));
    }

    static Browser NewBrowser(string baseUrl) =>
        new(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(baseUrl) }, new Dictionary<string, string>());

    static string TokenFromLink(string link) => link[(link.LastIndexOf('/') + 1)..];
    static string Query(Uri u, string key) => System.Web.HttpUtility.ParseQueryString(u.Query)[key]!;

    static async Task HttpTests(Action<string, bool, string?> check, string tmp, byte[] key)
    {
        Console.WriteLine("HTTP-Schicht (Einladung → Entra-Fake → Callback → Dashboard)");
        var e = await NewEnv(tmp, key);
        var oidc = new FakeOidc();
        int port; using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        var url = $"http://127.0.0.1:{port}";
        await using var app = InviteApp.Create(new InviteAppOptions { Url = url, Db = e.Db, Invitations = e.Inv, Delivery = e.Delivery, Oidc = oidc, AdminKey = AdminKey, AdminUserId = e.Admin });
        await app.StartAsync();

        async Task<(string Id, string Link)> CreateAndSend(Browser admin, string email, string profile, string[] mods, bool send = true)
        {
            var res = await admin.Send(HttpMethod.Post, "/admin/invitations", JsonBody(new { email, profile, moduleIds = mods }), r => r.Headers.Add("X-Admin-Key", AdminKey));
            var id = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("invitationId").GetString()!;
            if (send)
            {
                await admin.Send(HttpMethod.Post, $"/admin/invitations/{id}/send", JsonBody(new { confirm = true }), r => r.Headers.Add("X-Admin-Key", AdminKey));
            }
            return (id, send && !e.Mailer.Sent.IsEmpty ? e.Mailer.Sent.Last().Link : "");
        }
        static StringContent JsonBody(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
        var admin = NewBrowser(url);

        // Administration
        var noKey = await admin.Send(HttpMethod.Post, "/admin/invitations", JsonBody(new { email = "a@b.test", profile = "B2C_LEARNER" }));
        check("Admin-Endpunkt ohne Schlüssel → 401", noKey.StatusCode == HttpStatusCode.Unauthorized, null);
        var badProfile = await admin.Send(HttpMethod.Post, "/admin/invitations", JsonBody(new { email = "a@b.test", profile = "ADMIN" }), r => r.Headers.Add("X-Admin-Key", AdminKey));
        check("Zielprofil ADMIN über HTTP → 400", badProfile.StatusCode == HttpStatusCode.BadRequest, null);

        var created = await admin.Send(HttpMethod.Post, "/admin/invitations", JsonBody(new { email = "Lernende@Example.org", profile = "B2C_LEARNER", moduleIds = new[] { e.A, e.C } }), r => r.Headers.Add("X-Admin-Key", AdminKey));
        var createdBody = await created.Content.ReadAsStringAsync();
        var invId = JsonDocument.Parse(createdBody).RootElement.GetProperty("invitationId").GetString()!;
        check("Erstellung: 200, Antwort ohne Link/Token, Versand pending, keine Mail", created.StatusCode == HttpStatusCode.OK && !createdBody.Contains("/invite/") && !createdBody.Contains("token", StringComparison.OrdinalIgnoreCase)
            && e.Mailer.Sent.IsEmpty && await e.Delivery.GetStatusAsync(invId) == "pending", createdBody);

        var unconfirmed = await admin.Send(HttpMethod.Post, $"/admin/invitations/{invId}/send", JsonBody(new { confirm = false }), r => r.Headers.Add("X-Admin-Key", AdminKey));
        check("Versand ohne Bestätigung → 400, keine Mail", unconfirmed.StatusCode == HttpStatusCode.BadRequest && e.Mailer.Sent.IsEmpty, null);
        var sent = await admin.Send(HttpMethod.Post, $"/admin/invitations/{invId}/send", JsonBody(new { confirm = true }), r => r.Headers.Add("X-Admin-Key", AdminKey));
        check("Bestätigter Versand → 200, genau eine Mail, Status submitted", sent.StatusCode == HttpStatusCode.OK && e.Mailer.Sent.Count == 1 && await e.Delivery.GetStatusAsync(invId) == "submitted", await sent.Content.ReadAsStringAsync());
        var link = e.Mailer.Sent.Single().Link;
        check("Mail enthält HTTPS-Link /invite/<token>", link.StartsWith("https://poc.example.org/invite/") && TokenFromLink(link).Length == 43, link);
        var again = await admin.Send(HttpMethod.Post, $"/admin/invitations/{invId}/send", JsonBody(new { confirm = true }), r => r.Headers.Add("X-Admin-Key", AdminKey));
        check("Wiederholter Versand → 409 (Link nicht mehr verfügbar), keine zweite Mail", again.StatusCode == HttpStatusCode.Conflict && e.Mailer.Sent.Count == 1, null);
        var status = await admin.Send(HttpMethod.Get, $"/admin/invitations/{invId}", null, r => r.Headers.Add("X-Admin-Key", AdminKey));
        var statusBody = await status.Content.ReadAsStringAsync();
        check("Statusabfrage zeigt beide Zustände, nie Token", statusBody.Contains("\"status\":\"pending\"") && statusBody.Contains("\"deliveryStatus\":\"submitted\"") && !statusBody.Contains(TokenFromLink(link)), statusBody);

        // Benutzerfluss
        var token = TokenFromLink(link);
        var user = NewBrowser(url);
        var open = await user.Get("/invite/" + token);
        check("GET /invite/<token>: 302/303 auf token-lose Adresse, kein Versand", (int)open.StatusCode is 302 or 303 && open.Headers.Location!.ToString() == "/invite-start" && e.Mailer.Sent.Count == 1, open.Headers.Location?.ToString());
        check("Sicherheitsheader (no-store, no-referrer)", open.Headers.CacheControl?.NoStore == true && open.Headers.TryGetValues("Referrer-Policy", out var rp) && rp.Single() == "no-referrer", null);
        var bad = await NewBrowser(url).Get("/invite/" + new string('x', 43));
        check("Unbekannter Link → 400 mit generischer Meldung", bad.StatusCode == HttpStatusCode.BadRequest && (await bad.Content.ReadAsStringAsync()) == "Die Einladung ist ungültig oder nicht einlösbar.", null);

        var start = await user.Get("/invite-start");
        var authUri = start.Headers.Location!;
        var oidcSet = start.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("inv.oidc="));
        check("Weiterleitung zu Entra (state, nonce) ohne Token in URL", authUri.Host == "idp.test" && !authUri.ToString().Contains(token) && Query(authUri, "state").Length > 20, authUri.ToString());
        check("OIDC-Cookie: HttpOnly, SameSite=None, Pfad /auth/callback", oidcSet.Contains("httponly", StringComparison.OrdinalIgnoreCase) && oidcSet.Contains("samesite=none", StringComparison.OrdinalIgnoreCase) && oidcSet.Contains("path=/auth/callback", StringComparison.OrdinalIgnoreCase), oidcSet);
        check("Cookie enthält kein Klartexttoken (geschützt)", !oidcSet.Contains(token), null);
        check("Einladung nach Start noch pending (nichts verbraucht)", await InvStatus(e.Db, invId) == "pending", null);

        var state = Query(authUri, "state");
        var cookieCopy = new Dictionary<string, string>(user.Jar);   // für Replay-Test
        var pathCopy = new Dictionary<string, string>(user.Paths);
        oidc.Codes["code-1"] = Id("lernende@example.org", "sub-lernende");
        var cb = await user.Post("/auth/callback", ("code", "code-1"), ("state", state));
        check("Callback (form_post) → Weiterleitung /dashboard, Sitzung gesetzt", (int)cb.StatusCode is 302 or 303 && cb.Headers.Location!.ToString() == "/dashboard" && user.Jar.ContainsKey("inv.sess"), cb.Headers.Location?.ToString());
        check("Einladung accepted, genau 1 Benutzer", await InvStatus(e.Db, invId) == "accepted" && await NonAdminUsers(e.Db) == 1, null);
        var dash = await user.Get("/dashboard");
        var dashBody = await dash.Content.ReadAsStringAsync();
        check("B2C-Dashboard zeigt nur A und C", dashBody.Contains(e.A) && dashBody.Contains(e.C) && !dashBody.Contains(e.B), dashBody);
        check("Modul A erlaubt (200), C erlaubt (200), B verweigert (403)",
            (await user.Get("/modules/" + e.A)).StatusCode == HttpStatusCode.OK && (await user.Get("/modules/" + e.C)).StatusCode == HttpStatusCode.OK && (await user.Get("/modules/" + e.B)).StatusCode == HttpStatusCode.Forbidden, null);
        check("Ohne Sitzung kein Zugriff (401), gefälschtes Cookie ebenfalls", (await NewBrowser(url).Get("/dashboard")).StatusCode == HttpStatusCode.Unauthorized
            && (await new Browser(user.Http, new() { ["inv.sess"] = "forged" }).Get("/dashboard")).StatusCode == HttpStatusCode.Unauthorized, null);

        // Wiederholung / zweiter Browser
        oidc.Codes["code-2"] = Id("lernende@example.org", "sub-lernende");
        var replay = await new Browser(user.Http, new Dictionary<string, string>(cookieCopy)) { Paths = pathCopy }.Post("/auth/callback", ("code", "code-2"), ("state", state));
        check("Wiederholung des Callbacks (altes Cookie) → fehlgeschlagen, kein zweiter Benutzer", replay.Headers.Location!.ToString() == "/invite-failed" && await NonAdminUsers(e.Db) == 1, null);
        check("Zweiter Browser mit demselben Link → ungültig", (await NewBrowser(url).Get("/invite/" + token)).StatusCode == HttpStatusCode.BadRequest, null);
        var noCookie = await NewBrowser(url).Post("/auth/callback", ("code", "x"), ("state", "y"));
        check("Callback ohne Korrelationscookie → fehlgeschlagen", noCookie.Headers.Location!.ToString() == "/invite-failed", null);

        // Fehlerpfade ohne Verbrauch
        async Task<(Browser B, string State)> Begin(string tokenValue)
        {
            var b = NewBrowser(url);
            await b.Get("/invite/" + tokenValue);
            var s = await b.Get("/invite-start");
            return (b, Query(s.Headers.Location!, "state"));
        }
        {
            var (id2, link2) = await CreateAndSend(admin, "zwei@example.org", "B2B_LEARNER", new[] { e.A });
            var t2 = TokenFromLink(link2);
            var (b1, s1) = await Begin(t2);
            var wrongState = await b1.Post("/auth/callback", ("code", "c"), ("state", "falsch"));
            check("Falscher state → fehlgeschlagen, Einladung unverbraucht", wrongState.Headers.Location!.ToString() == "/invite-failed" && await InvStatus(e.Db, id2) == "pending", null);

            var (b2, s2) = await Begin(t2);
            var err = await b2.Post("/auth/callback", ("error", "access_denied"), ("code", "c"), ("state", s2));
            check("Fehler vom Provider → fehlgeschlagen, unverbraucht", err.Headers.Location!.ToString() == "/invite-failed" && await InvStatus(e.Db, id2) == "pending", null);

            var (b3, s3) = await Begin(t2);
            var invalidToken = await b3.Post("/auth/callback", ("code", "unbekannt"), ("state", s3));
            check("Ungültiges ID-Token/Code → fehlgeschlagen, unverbraucht", invalidToken.Headers.Location!.ToString() == "/invite-failed" && await InvStatus(e.Db, id2) == "pending", null);

            var (b4, s4) = await Begin(t2);
            oidc.Codes["c4"] = Id("falsch@example.org", "sub-x");
            var wrongMail = await b4.Post("/auth/callback", ("code", "c4"), ("state", s4));
            check("Falsche E-Mail → fehlgeschlagen, unverbraucht, kein Benutzer", wrongMail.Headers.Location!.ToString() == "/invite-failed" && await InvStatus(e.Db, id2) == "pending", null);

            var (b5, s5) = await Begin(t2);
            oidc.Codes["c5"] = new VerifiedIdentity(Issuer, "sub-lernende", "zwei@example.org", true);   // Identität existiert bereits
            var existing = await b5.Post("/auth/callback", ("code", "c5"), ("state", s5));
            check("Bereits registrierte Identität → fehlgeschlagen, Einladung bleibt pending", existing.Headers.Location!.ToString() == "/invite-failed" && await InvStatus(e.Db, id2) == "pending", null);

            var (b6, s6) = await Begin(t2);
            oidc.Codes["c6"] = Id("zwei@example.org", verified: false);
            var unverified = await b6.Post("/auth/callback", ("code", "c6"), ("state", s6));
            check("Nicht bestätigte E-Mail → fehlgeschlagen, unverbraucht", unverified.Headers.Location!.ToString() == "/invite-failed" && await InvStatus(e.Db, id2) == "pending", null);

            // zwei Browser parallel
            var (pa, sa) = await Begin(t2); var (pb, sb) = await Begin(t2);
            oidc.Codes["ca"] = Id("zwei@example.org", "par-a"); oidc.Codes["cb"] = Id("zwei@example.org", "par-b");
            var outcomes = await Task.WhenAll(Task.Run(() => pa.Post("/auth/callback", ("code", "ca"), ("state", sa))), Task.Run(() => pb.Post("/auth/callback", ("code", "cb"), ("state", sb))));
            check("Identischer Link in zwei Browsern gleichzeitig → genau ein Erfolg", outcomes.Count(o => o.Headers.Location!.ToString() == "/dashboard") == 1 && await NonAdminUsers(e.Db) == 2, string.Join(",", outcomes.Select(o => o.Headers.Location)));
        }

        // Link nicht mehr verfügbar → Rotation
        {
            var inv = await e.Inv.CreateAsync(e.Admin, "rot@example.org", TargetProfiles.B2cLearner, new[] { e.A });   // ohne HTTP-Erstellung: Klartext nicht im Speicher des Servers
            var send = await admin.Send(HttpMethod.Post, $"/admin/invitations/{inv.InvitationId}/send", JsonBody(new { confirm = true }), r => r.Headers.Add("X-Admin-Key", AdminKey));
            check("Link nicht im Speicher → 409 link_unavailable (kein stilles Neuerzeugen)", send.StatusCode == HttpStatusCode.Conflict && (await send.Content.ReadAsStringAsync()).Contains("link_unavailable"), null);
            var before = e.Mailer.Sent.Count;
            var rot = await admin.Send(HttpMethod.Post, $"/admin/invitations/{inv.InvitationId}/rotate", null, r => r.Headers.Add("X-Admin-Key", AdminKey));
            var send2 = await admin.Send(HttpMethod.Post, $"/admin/invitations/{inv.InvitationId}/send", JsonBody(new { confirm = true }), r => r.Headers.Add("X-Admin-Key", AdminKey));
            var newLink = e.Mailer.Sent.Last().Link;
            check("Rotation + Versand → neuer Link, alter ungültig, eine Einladung", rot.StatusCode == HttpStatusCode.OK && send2.StatusCode == HttpStatusCode.OK && e.Mailer.Sent.Count == before + 1
                && newLink != inv.Link && (await NewBrowser(url).Get("/invite/" + inv.Token)).StatusCode == HttpStatusCode.BadRequest && (await NewBrowser(url).Get("/invite/" + TokenFromLink(newLink))).StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther, null);
            var revoke = await admin.Send(HttpMethod.Post, $"/admin/invitations/{inv.InvitationId}/revoke", null, r => r.Headers.Add("X-Admin-Key", AdminKey));
            check("Widerruf über HTTP → Link ungültig", revoke.StatusCode == HttpStatusCode.OK && (await NewBrowser(url).Get("/invite/" + TokenFromLink(newLink))).StatusCode == HttpStatusCode.BadRequest, null);
        }
        {
            // fehlgeschlagener Versand über HTTP: Einladung bleibt nutzbar, Wiederholung mit demselben Link
            var created2 = await admin.Send(HttpMethod.Post, "/admin/invitations", JsonBody(new { email = "fail@example.org", profile = "B2C_LEARNER", moduleIds = new[] { e.A } }), r => r.Headers.Add("X-Admin-Key", AdminKey));
            var id3 = JsonDocument.Parse(await created2.Content.ReadAsStringAsync()).RootElement.GetProperty("invitationId").GetString()!;
            e.Mailer.Results.Enqueue(new(MailOutcome.Rejected, "permission_denied"));
            var s1 = await admin.Send(HttpMethod.Post, $"/admin/invitations/{id3}/send", JsonBody(new { confirm = true }), r => r.Headers.Add("X-Admin-Key", AdminKey));
            check("Versandfehler über HTTP → 502, failed, Einladung pending", s1.StatusCode == HttpStatusCode.BadGateway && await e.Delivery.GetStatusAsync(id3) == "failed" && await InvStatus(e.Db, id3) == "pending", null);
            var s2 = await admin.Send(HttpMethod.Post, $"/admin/invitations/{id3}/send", JsonBody(new { confirm = true }), r => r.Headers.Add("X-Admin-Key", AdminKey));
            check("Wiederholung mit demselben Link → 200, submitted", s2.StatusCode == HttpStatusCode.OK && await e.Delivery.GetStatusAsync(id3) == "submitted", null);
        }
        await app.StopAsync();
    }
}
