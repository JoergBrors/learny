using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using InviteRegistration;

// Härtung des Exchange-Mailservices: Tenant, Absender, Empfänger, Zertifikat, Timeouts, Retry-After, keine Geheimnisse in Ausgaben.
static partial class ExtendedTests
{
    static async Task MailHardeningTests(Action<string, bool, string?> check, string tmp, byte[] key)
    {
        Console.WriteLine("Exchange-Mailservice: Härtung (Tenant, Empfänger, Zertifikat, Timeout, Retry-After)");
        const string External = "5fce2b3c-8e95-49b5-9cfd-fc223b7e8f63";
        const string Workforce = "db88f9b6-c89a-41a3-a21d-b61458e234ff";
        const string Sender = "learny@brors.link";
        var mail = new InvitationMail("test@example.org", "https://poc.example.org/invite/SECRETLINK", DateTime.UtcNow.AddHours(72));
        ExchangeMailOptions Opt(string tenant = Workforce, X509Certificate2? cert = null, string secret = "", string[]? allowed = null, string? sender = null, TimeSpan? timeout = null) => new()
        {
            Enabled = true, TenantId = tenant, ForbiddenTenantIds = new[] { External }, ClientId = "mail-app", ClientCertificate = cert, ClientSecret = secret,
            SenderMailbox = sender ?? Sender, AllowedRecipients = allowed ?? new[] { "test@example.org" }, RequestTimeout = timeout ?? TimeSpan.FromSeconds(5),
        };
        HttpResponseMessage Route(HttpRequestMessage r, int sendStatus, Action<HttpResponseMessage>? tweak = null)
        {
            if (r.RequestUri!.Host == "login.microsoftonline.com") return TokenOk();
            var res = new HttpResponseMessage((HttpStatusCode)sendStatus); tweak?.Invoke(res); return res;
        }

        // Tenant und Absender
        {
            var h = new StubHandler { Respond = (r, _) => Route(r, 202) };
            var r = await new ExchangeOnlineMailService(new HttpClient(h), Opt(External, secret: "s")).SendAsync(mail);
            check("External-ID-Tenant als Mail-Tenant verboten → wrong_tenant, kein HTTP-Aufruf", r.Code == "wrong_tenant" && h.Calls.Count == 0, r.Code);
            var ok = await new ExchangeOnlineMailService(new HttpClient(h), Opt(secret: "s")).SendAsync(mail);
            check("Workforce-Tenant: Tokenabruf am Workforce-Endpunkt, Versand über learny@brors.link", ok.Outcome == MailOutcome.Accepted
                && h.Calls[0].Url == $"https://login.microsoftonline.com/{Workforce}/oauth2/v2.0/token"
                && h.Calls[1].Url == "https://graph.microsoft.com/v1.0/users/learny%40brors.link/sendMail", string.Join(" | ", h.Calls.Select(c => c.Url)));
            var bad = await new ExchangeOnlineMailService(new HttpClient(h), Opt(secret: "s", sender: "kein-absender")).SendAsync(mail);
            check("Ungültiges Absenderpostfach → mail_not_configured", bad.Code == "mail_not_configured", null);
            using var doc = JsonDocument.Parse(h.Calls[1].Body);
            check("Absender ist nicht Teil der Nachricht (kein 'from' aus Anfragedaten)", !doc.RootElement.GetProperty("message").TryGetProperty("from", out _), null);
        }

        // Empfänger-Allowlist
        {
            var h = new StubHandler { Respond = (r, _) => Route(r, 202) };
            var svc = new ExchangeOnlineMailService(new HttpClient(h), Opt(secret: "s"));
            var other = await svc.SendAsync(mail with { RecipientEmail = "fremd@example.net" });
            check("Nicht erlaubter Empfänger → recipient_not_allowed, kein HTTP-Aufruf", other.Code == "recipient_not_allowed" && h.Calls.Count == 0, other.Code);
            var empty = await new ExchangeOnlineMailService(new HttpClient(h), Opt(secret: "s", allowed: Array.Empty<string>())).SendAsync(mail);
            check("Leere Allowlist → niemand erlaubt (fail-closed)", empty.Code == "recipient_not_allowed" && h.Calls.Count == 0, null);
            var dom = await new ExchangeOnlineMailService(new HttpClient(h), Opt(secret: "s", allowed: new[] { "@example.org" })).SendAsync(mail);
            check("Domainregel @example.org erlaubt Adresse der Domain", dom.Outcome == MailOutcome.Accepted, dom.Code);
            var sneaky = await new ExchangeOnlineMailService(new HttpClient(new StubHandler { Respond = (r, _) => Route(r, 202) }), Opt(secret: "s", allowed: new[] { "@example.org" }))
                .SendAsync(mail with { RecipientEmail = "x@evil-example.org" });
            check("Domainregel trifft keine Fremddomain mit gleichem Suffix", sneaky.Code == "recipient_not_allowed", null);
        }

        // Zertifikatsauthentifizierung (kein Secret, Assertion prüfbar)
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=Learny Invitation Mailer Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            var h = new StubHandler { Respond = (r, _) => Route(r, 202) };
            var r = await new ExchangeOnlineMailService(new HttpClient(h), Opt(cert: cert)).SendAsync(mail);
            var body = h.Calls[0].Body;
            check("Zertifikat: Versand erfolgreich, Tokenabruf mit client_assertion statt client_secret", r.Outcome == MailOutcome.Accepted && body.Contains("client_assertion=") && !body.Contains("client_secret")
                && body.Contains("client_assertion_type=urn%3Aietf%3Aparams%3Aoauth%3Aclient-assertion-type%3Ajwt-bearer"), body);
            var assertion = Uri.UnescapeDataString(body.Split('&').First(x => x.StartsWith("client_assertion=")).Split('=', 2)[1]);
            var p = assertion.Split('.');
            static byte[] D(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }
            using var hdr = JsonDocument.Parse(D(p[0])); using var pl = JsonDocument.Parse(D(p[1]));
            check("Assertion: RS256, x5t#S256 des Zertifikats, aud=Tokenendpunkt, iss=sub=Client-ID, exp ≤ 10 min",
                hdr.RootElement.GetProperty("alg").GetString() == "RS256" && hdr.RootElement.GetProperty("x5t#S256").GetString() == Convert.ToBase64String(cert.GetCertHash(HashAlgorithmName.SHA256)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
                && pl.RootElement.GetProperty("aud").GetString() == $"https://login.microsoftonline.com/{Workforce}/oauth2/v2.0/token"
                && pl.RootElement.GetProperty("iss").GetString() == "mail-app" && pl.RootElement.GetProperty("sub").GetString() == "mail-app"
                && pl.RootElement.GetProperty("exp").GetInt64() - pl.RootElement.GetProperty("nbf").GetInt64() <= 600, null);
            check("Assertion mit dem öffentlichen Zertifikatsschlüssel verifizierbar", cert.GetRSAPublicKey()!.VerifyData(Encoding.ASCII.GetBytes(p[0] + "." + p[1]), D(p[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), null);
            var both = await new ExchangeOnlineMailService(new HttpClient(h), Opt(cert: cert, secret: "s")).SendAsync(mail);
            var neither = await new ExchangeOnlineMailService(new HttpClient(h), Opt()).SendAsync(mail);
            check("Zertifikat und Secret gleichzeitig oder keines → mail_not_configured", both.Code == "mail_not_configured" && neither.Code == "mail_not_configured", null);
            using var pubOnly = new X509Certificate2(cert.Export(X509ContentType.Cert));
            var noKey = await new ExchangeOnlineMailService(new HttpClient(new StubHandler { Respond = (rr, _) => Route(rr, 202) }), Opt(cert: pubOnly)).SendAsync(mail);
            check("Zertifikat ohne privaten Schlüssel → auth_failed, nichts gesendet", noKey.Code == "auth_failed" && noKey.Outcome == MailOutcome.Rejected, noKey.Code);
        }

        // Timeouts
        {
            var slowSend = new StubHandler { Respond = (r, _) => Route(r, 202), DelayFor = r => r.RequestUri!.Host == "graph.microsoft.com" ? 2000 : 0 };
            var r1 = await new ExchangeOnlineMailService(new HttpClient(slowSend), Opt(secret: "s", timeout: TimeSpan.FromMilliseconds(200))).SendAsync(mail);
            check("Zeitüberschreitung beim Senden → Ambiguous/timeout", r1.Outcome == MailOutcome.Ambiguous && r1.Code == "timeout", r1.Code);
            var slowToken = new StubHandler { Respond = (r, _) => Route(r, 202), DelayFor = r => r.RequestUri!.Host == "login.microsoftonline.com" ? 2000 : 0 };
            var r2 = await new ExchangeOnlineMailService(new HttpClient(slowToken), Opt(secret: "s", timeout: TimeSpan.FromMilliseconds(200))).SendAsync(mail);
            check("Zeitüberschreitung beim Tokenabruf → Rejected/auth_timeout (nichts gesendet)", r2.Outcome == MailOutcome.Rejected && r2.Code == "auth_timeout" && !slowToken.Calls.Any(c => c.Url.Contains("graph.microsoft.com")), r2.Code);
        }

        // Retry-After
        {
            var h = new StubHandler { Respond = (r, _) => Route(r, 429, res => res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120))) };
            var r = await new ExchangeOnlineMailService(new HttpClient(h), Opt(secret: "s")).SendAsync(mail);
            check("HTTP 429 mit Retry-After: 120 → Rejected/throttled, RetryAfter 120 s", r.Outcome == MailOutcome.Rejected && r.Code == "throttled" && r.RetryAfter == TimeSpan.FromSeconds(120), r.RetryAfter?.ToString());
            var huge = new StubHandler { Respond = (rr, _) => Route(rr, 429, res => res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromDays(3))) };
            var rh = await new ExchangeOnlineMailService(new HttpClient(huge), Opt(secret: "s")).SendAsync(mail);
            check("Retry-After wird auf 1 h begrenzt", rh.RetryAfter == TimeSpan.FromHours(1), rh.RetryAfter?.ToString());
            var s503 = new StubHandler { Respond = (rr, _) => Route(rr, 503, res => res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30))) };
            var r5 = await new ExchangeOnlineMailService(new HttpClient(s503), Opt(secret: "s")).SendAsync(mail);
            check("HTTP 503 mit Retry-After → Ambiguous, RetryAfter 30 s", r5.Outcome == MailOutcome.Ambiguous && r5.RetryAfter == TimeSpan.FromSeconds(30), null);

            // Versandstatus: Retry-After sperrt den Slot bis zum Zeitpunkt
            var now = DateTime.UtcNow; var clock = now;
            var e = await NewEnv(tmp, key, () => clock);
            var inv = await e.Inv.CreateAsync(e.Admin, "retry@y.test", TargetProfiles.B2cLearner);
            e.Mailer.Results.Enqueue(new(MailOutcome.Rejected, "throttled", TimeSpan.FromSeconds(120)));
            var first = await e.Delivery.SendAsync(e.Admin, inv.InvitationId, inv.Token, inv.Link, true);
            var tooEarly = await e.Delivery.SendAsync(e.Admin, inv.InvitationId, inv.Token, inv.Link, true);
            clock = now.AddSeconds(121);
            var later = await e.Delivery.SendAsync(e.Admin, inv.InvitationId, inv.Token, inv.Link, true);
            check("Retry-After im Versandzustand: zu früh → InProgress (kein Mailer-Aufruf), danach erlaubt", first.Decision == SendDecision.Failed && first.RetryAfter == TimeSpan.FromSeconds(120)
                && tooEarly.Decision == SendDecision.InProgress && e.Mailer.Sent.Count == 2 && later.Decision == SendDecision.Sent && await InvStatus(e.Db, inv.InvitationId) == "pending", $"{first.Decision}/{tooEarly.Decision}/{later.Decision}/{e.Mailer.Sent.Count}");
        }

        // Keine Geheimnisse in Ausgaben
        {
            const string Secret = "TOP-SECRET-CLIENT-SECRET";
            var h = new StubHandler { Respond = (r, _) => r.RequestUri!.Host == "login.microsoftonline.com" ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_client\",\"hint\":\"" + Secret + "\"}") } : Route(r, 403) };
            var sb = new StringWriter(); var old = Console.Out; Console.SetOut(sb);
            MailSendResult r1, r2;
            try
            {
                r1 = await new ExchangeOnlineMailService(new HttpClient(h), Opt(secret: Secret)).SendAsync(mail);
                var h2 = new StubHandler { Respond = (r, _) => Route(r, 403) };
                r2 = await new ExchangeOnlineMailService(new HttpClient(h2), Opt(secret: Secret)).SendAsync(mail);
            }
            finally { Console.SetOut(old); }
            var all = sb + r1.Code + r2.Code;
            check("Keine Geheimnisse, Tokens oder Links in Konsole und Fehlercodes", !all.Contains(Secret) && !all.Contains("tok-123") && !all.Contains("SECRETLINK"), all);
        }
    }
}
