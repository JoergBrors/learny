using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InviteRegistration;
using LearnCards.Web.Data;

// OnOtpSend-Extension (ADR 0045): Tokenprüfung, Schema, Empfängerregel, Rate Limit, Versand über Exchange, Fehlerverhalten, keine Codes in Ausgaben.
static partial class ExtendedTests
{
    sealed class MsgTime { public DateTime Now = DateTime.UtcNow; }

    static async Task OtpExtensionTests(Action<string, bool, string?> check, string tmp, byte[] key)
    {
        Console.WriteLine("OnOtpSend-Extension (Entra → Learny → Exchange)");
        const string Auth = "https://t.ciamlogin.com/tid/v2.0";
        const string Iss = "https://tid.ciamlogin.com/tid/v2.0";
        const string Caller = "99045fe1-7639-4a75-9d4a-577b6ca3810f";
        const string Code = "48151623";
        var tm = new MsgTime();
        using var rsa = RSA.Create(2048); using var other = RSA.Create(2048);
        var pub = rsa.ExportParameters(false);
        static string B64u(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        static string Jwt(object payload, RSA k, string alg = "RS256")
        {
            var h = B64u(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg, kid = "k1", typ = "JWT" })));
            var p = B64u(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
            return h + "." + p + "." + B64u(k.SignData(Encoding.ASCII.GetBytes(h + "." + p), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        long Ts(DateTime d) => new DateTimeOffset(d).ToUnixTimeSeconds();
        Dictionary<string, object> Claims() => new() { ["iss"] = Iss, ["aud"] = "api-app", ["azp"] = Caller, ["tid"] = "tid", ["ver"] = "2.0", ["exp"] = Ts(tm.Now.AddHours(1)), ["nbf"] = Ts(tm.Now.AddMinutes(-1)) };
        static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
        string Good() => "Bearer " + Jwt(Claims(), rsa);
        string Body(string email = "invitee@y.test", string code = Code, string codeName = "oneTimeCode", string type = "microsoft.graph.authenticationEvent.emailOtpSend") =>
            "{\"type\":\"" + type + "\",\"source\":\"/tenants/tid/applications/app\",\"data\":{\"@odata.type\":\"microsoft.graph.onOtpSendCalloutData\",\"tenantId\":\"tid\"," +
            "\"authenticationContext\":{\"correlationId\":\"c\",\"requestType\":\"signUp\",\"client\":{\"locale\":\"de-DE\"}},\"otpContext\":{\"identifier\":\"" + email + "\",\"" + codeName + "\":\"" + code + "\"}}}";

        var disc = new StubHandler
        {
            Respond = (r, _) => r.RequestUri!.ToString().EndsWith("/keys")
                ? Json(new { keys = new[] { new { kty = "RSA", kid = "k1", n = B64u(pub.Modulus!), e = B64u(pub.Exponent!) } } })
                : Json(new { issuer = Iss, jwks_uri = "https://t.ciamlogin.com/tid/discovery/v2.0/keys" }),
        };
        var env = await NewEnv(tmp, key);
        await env.Inv.CreateAsync(env.Admin, "invitee@y.test", TargetProfiles.B2cLearner, new[] { env.A });
        await env.Db.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id,'Bekannt','bekannt@y.test','active',0,@n,@n)", new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("n", DateTime.UtcNow) });
        await env.Db.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id,'Gesperrt','gesperrt@y.test','disabled',0,@n,@n)", new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("n", DateTime.UtcNow) });

        int graphStatus = 202; int graphDelay = 0; Action<HttpResponseMessage>? graphTweak = null;
        var graph = new StubHandler
        {
            Respond = (r, _) => r.RequestUri!.Host == "login.microsoftonline.com" ? TokenOk() : new HttpResponseMessage((HttpStatusCode)graphStatus).Also(graphTweak),
            DelayFor = r => r.RequestUri!.Host == "graph.microsoft.com" ? graphDelay : 0,
        };
        ExchangeMailOptions MailOpt(bool enabled = true) => new()
        {
            Enabled = enabled, TenantId = "db88f9b6-c89a-41a3-a21d-b61458e234ff", ForbiddenTenantIds = new[] { "5fce2b3c-8e95-49b5-9cfd-fc223b7e8f63" }, ClientId = "mail-app", ClientSecret = "mail-secret",
            SenderMailbox = "learny@brors.link", AllowedRecipients = Array.Empty<string>(),   // Allowlist der Einladungsmails darf den OTP-Versand nicht steuern
        };
        OtpSendHandler NewHandler(bool mailEnabled = true, int perRecipient = 3, TimeSpan? sendTimeout = null, Func<string, string, Task>? audit = null)
        {
            var o = new OtpExtensionOptions
            {
                Authority = Auth, Audiences = new[] { "api-app", "api://otp.example.org/api-app" }, ExpectedTenantId = "tid", Clock = () => tm.Now,
                PerRecipientPer10Minutes = perRecipient, SendTimeout = sendTimeout ?? TimeSpan.FromSeconds(5), Audit = audit,
            };
            return new OtpSendHandler(new ExtensionTokenValidator(new HttpClient(disc), o), new ExchangeOnlineMailService(new HttpClient(graph), MailOpt(mailEnabled)), new InvitationOtpRecipientPolicy(env.Inv), o);
        }
        int GraphSends() => graph.Calls.Count(c => c.Url.Contains("/sendMail"));
        void Reset() { graph.Calls.Clear(); graphStatus = 202; graphDelay = 0; graphTweak = null; }

        // Gültiger Aufruf
        {
            Reset(); var h = NewHandler();
            var r = await h.HandleAsync(Good(), Body(), default);
            var send = graph.Calls.Single(c => c.Url.Contains("/sendMail"));
            check("Gültiger Aufruf → 200 mit continueWithDefaultBehavior", r.Status == 200 && r.Json!.Contains("microsoft.graph.OnOtpSendResponseData") && r.Json.Contains("microsoft.graph.OtpSend.continueWithDefaultBehavior"), r.Status + r.Json);
            check("Absender ausschließlich learny@brors.link (Graph-Pfad)", send.Url == "https://graph.microsoft.com/v1.0/users/learny%40brors.link/sendMail", send.Url);
            using var d = JsonDocument.Parse(send.Body);
            var msg = d.RootElement.GetProperty("message");
            var html = msg.GetProperty("body").GetProperty("content").GetString()!;
            check("Betreff 'Ihr Learny-Anmeldecode', Empfänger korrekt, nicht in Gesendete Elemente", msg.GetProperty("subject").GetString() == "Ihr Learny-Anmeldecode"
                && msg.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString() == "invitee@y.test" && d.RootElement.GetProperty("saveToSentItems").GetBoolean() == false, null);
            check("Der Entra-Code wird unverändert übergeben (HTML, deutsch, genau einmal)", msg.GetProperty("body").GetProperty("contentType").GetString() == "HTML" && html.Split(Code).Length == 2 && html.Contains("Ihr Anmeldecode lautet") && html.Contains("Ihr Learny-Team"), null);
            check("Mail: keine Tracking-Pixel, Bilder oder fremde Links", !html.Contains("<img", StringComparison.OrdinalIgnoreCase) && !html.Contains("http://") && !html.Contains("href=", StringComparison.OrdinalIgnoreCase), null);
            check("Token der Mail-App stammt vom Workforce-Tenant (nicht vom External Tenant)", graph.Calls[0].Url.Contains("db88f9b6-c89a-41a3-a21d-b61458e234ff"), graph.Calls[0].Url);
            var again = await h.HandleAsync(Good(), Body(), default);
            check("Wiederholter identischer OTP-Aufruf nach Erfolg → 200, nur eine Mail", again.Status == 200 && GraphSends() == 1, GraphSends().ToString());
        }

        // Authentifizierung der Extension
        {
            Reset(); var h = NewHandler();
            async Task<bool> Denied(string name, string? authz)
            {
                var r = await h.HandleAsync(authz, Body(), default);
                check($"Abgelehnt (401): {name}", r.Status == 401 && r.Json is null && GraphSends() == 0, r.Status.ToString());
                return r.Status == 401;
            }
            await Denied("fehlendes Bearer-Token", null);
            await Denied("ohne 'Bearer'-Präfix", Jwt(Claims(), rsa));
            await Denied("falscher Issuer", "Bearer " + Jwt(new Dictionary<string, object>(Claims()) { ["iss"] = "https://evil.test/tid/v2.0" }, rsa));
            await Denied("falsche Audience", "Bearer " + Jwt(new Dictionary<string, object>(Claims()) { ["aud"] = "fremde-api" }, rsa));
            await Denied("falsche aufrufende App (azp)", "Bearer " + Jwt(new Dictionary<string, object>(Claims()) { ["azp"] = "00000000-0000-0000-0000-000000000000" }, rsa));
            await Denied("falscher Tenant (tid)", "Bearer " + Jwt(new Dictionary<string, object>(Claims()) { ["tid"] = "anderer" }, rsa));
            await Denied("abgelaufenes Token", "Bearer " + Jwt(new Dictionary<string, object>(Claims()) { ["exp"] = Ts(tm.Now.AddHours(-1)) }, rsa));
            await Denied("falsche Signatur", "Bearer " + Jwt(Claims(), other));
            await Denied("Algorithmus HS256", "Bearer " + Jwt(Claims(), rsa, "HS256"));
            await Denied("Müll als Token", "Bearer abc.def.ghi");
            var v1 = Claims(); v1.Remove("azp"); v1["appid"] = Caller;
            var okV1 = await h.HandleAsync("Bearer " + Jwt(v1, rsa), Body(), default);
            check("V1-Token mit appid der Aufrufer-App wird akzeptiert", okV1.Status == 200, okV1.Status.ToString());
        }

        // Request-Schema
        {
            Reset();
            async Task Bad(string name, string body, int expect = 400)
            {
                var r = await NewHandler().HandleAsync(Good(), body, default);
                check($"{expect}: {name}", r.Status == expect && GraphSends() == 0, r.Status.ToString());
            }
            await Bad("falscher Ereignistyp", Body(type: "microsoft.graph.authenticationEvent.tokenIssuanceStart"));
            await Bad("kein JSON", "nicht json");
            await Bad("otpContext fehlt", "{\"type\":\"microsoft.graph.authenticationEvent.emailOtpSend\",\"data\":{}}");
            await Bad("ungültige Empfängeradresse", Body(email: "kein-email"));
            await Bad("Code mit Markup", Body(code: "12<b>34"));
            await Bad("Code zu kurz", Body(code: "123"));
            await Bad("Code fehlt (anderer Feldname)", Body(codeName: "code"));
            await Bad("Body über 8 KB", Body() + new string(' ', 9000), 413);
            var lower = await NewHandler().HandleAsync(Good(), Body(codeName: "onetimecode"), default);
            check("Feldname 'onetimecode' (Schreibweise der Microsoft-Beispiele) wird akzeptiert", lower.Status == 200, lower.Status.ToString());
        }

        // Empfängerregel (kein offener Mailversand)
        {
            async Task<(int, int)> Try(string email) { Reset(); var r = await NewHandler().HandleAsync(Good(), Body(email: email), default); return (r.Status, GraphSends()); }
            check("Adresse mit gültiger Einladung erhält den Code", await Try("invitee@y.test") is (200, 1), null);
            check("Aktiver Learny-Benutzer erhält den Code (erneute Anmeldung)", await Try("bekannt@y.test") is (200, 1), null);
            check("Unbekannte Adresse → 403, keine Mail", await Try("fremd@y.test") is (403, 0), null);
            check("Deaktivierter Benutzer → 403, keine Mail", await Try("gesperrt@y.test") is (403, 0), null);
            var rev = await env.Inv.CreateAsync(env.Admin, "widerrufen@y.test", TargetProfiles.B2cLearner); await env.Inv.RevokeAsync(env.Admin, rev.InvitationId);
            check("Widerrufene Einladung → 403, keine Mail", await Try("widerrufen@y.test") is (403, 0), null);
            var expired = await env.Inv.CreateAsync(env.Admin, "abgelaufen@y.test", TargetProfiles.B2cLearner);
            await env.Db.ExecuteAsync("UPDATE invitations SET expires_at=@d WHERE invitation_id=@i", new (string, object?)[] { ("d", DateTime.UtcNow.AddHours(-1)), ("i", expired.InvitationId) });
            check("Abgelaufene Einladung → 403, keine Mail", await Try("abgelaufen@y.test") is (403, 0), null);
            check("Groß-/Kleinschreibung der Adresse unerheblich", await Try("INVITEE@Y.TEST") is (200, 1), null);
        }

        // Rate Limit
        {
            Reset(); var h = NewHandler(perRecipient: 3);
            var rs = new List<OtpHttpResult>();
            for (var i = 0; i < 4; i++) rs.Add(await h.HandleAsync(Good(), Body(code: "1000000" + i), default));
            check("4. Aufruf für dieselbe Adresse innerhalb von 10 Minuten → 429 mit Retry-After", rs.Take(3).All(r => r.Status == 200) && rs[3].Status == 429 && rs[3].RetryAfter is { } ra && ra > TimeSpan.Zero, string.Join(",", rs.Select(r => r.Status)));
            check("Begrenzter Aufruf sendet keine Mail", GraphSends() == 3, GraphSends().ToString());
            tm.Now = tm.Now.AddMinutes(11);
            var later = await h.HandleAsync(Good(), Body(code: "20000000"), default);
            check("Nach Ablauf des Fensters wieder erlaubt", later.Status == 200, later.Status.ToString());
        }

        // Graph-Fehler, Timeout, Konfiguration
        {
            async Task<OtpHttpResult> With(int status, Action<HttpResponseMessage>? tweak = null, int delay = 0, bool enabled = true, TimeSpan? timeout = null)
            {
                Reset(); graphStatus = status; graphTweak = tweak; graphDelay = delay;
                return await NewHandler(enabled, perRecipient: 100, sendTimeout: timeout).HandleAsync(Good(), Body(code: Random.Shared.Next(10000000, 99999999).ToString()), default);
            }
            var r403 = await With(403);
            check("Graph 403 → 502, kein 'continue' (keine erfolgreiche Zustellung behauptet)", r403.Status == 502 && r403.Json is null, r403.Status.ToString());
            var r401 = await With(401);
            check("Graph 401 → 502, kein 'continue'", r401.Status == 502 && r401.Json is null, null);
            var r429 = await With(429, res => res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60)));
            check("Graph 429 mit Retry-After → 502 und Retry-After 60 s weitergegeben", r429.Status == 502 && r429.RetryAfter == TimeSpan.FromSeconds(60), r429.RetryAfter?.ToString());
            var r503 = await With(503);
            check("Graph 5xx → 504 (unklar), kein 'continue'", r503.Status == 504 && r503.Json is null, r503.Status.ToString());
            var rTo = await With(202, delay: 2000, timeout: TimeSpan.FromMilliseconds(200));
            check("Zeitüberschreitung beim Versand → 504, kein 'continue'", rTo.Status == 504 && rTo.Json is null, rTo.Status.ToString());
            var rOff = await With(202, enabled: false);
            check("Mailservice deaktiviert/nicht konfiguriert → 502, kein HTTP-Aufruf an Graph", rOff.Status == 502 && graph.Calls.Count == 0, rOff.Status.ToString());

            // Wiederholung nach Fehler wird wirklich versendet (kein Dedupe bei Misserfolg)
            Reset(); graphStatus = 403; var h = NewHandler(perRecipient: 100);
            await h.HandleAsync(Good(), Body(), default);
            graphStatus = 202;
            var retry = await h.HandleAsync(Good(), Body(), default);
            check("Wiederholter OTP-Aufruf nach Fehlschlag wird versendet", retry.Status == 200 && GraphSends() == 2, GraphSends().ToString());
        }

        // Keine Codes in Logs, Audit, Datenbank, Fehlerantworten
        {
            Reset(); const string Secret = "93817265";
            var audit = new List<string>();
            var sb = new StringWriter(); var oldOut = Console.Out; var oldErr = Console.Error; Console.SetOut(sb); Console.SetError(sb);
            var results = new List<OtpHttpResult>();
            try
            {
                var h = NewHandler(perRecipient: 100, audit: (ev, reason) => { audit.Add(ev + ":" + reason); return Task.CompletedTask; });
                results.Add(await h.HandleAsync(Good(), Body(code: Secret), default));
                graphStatus = 403; results.Add(await h.HandleAsync(Good(), Body(code: Secret.Replace("9", "8")), default));
                results.Add(await h.HandleAsync("Bearer kaputt", Body(code: Secret), default));
                results.Add(await h.HandleAsync(Good(), Body(email: "fremd@y.test", code: Secret), default));
            }
            finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
            var all = sb + string.Join("|", audit) + string.Join("|", results.Select(r => r.Json));
            var rows = await env.Db.QueryAsync("SELECT (SELECT COUNT(*) FROM invitation_audit WHERE reason LIKE @c OR event LIKE @c) + (SELECT COUNT(*) FROM invitations WHERE token_hash LIKE @c OR recipient_email LIKE @c) AS n", new (string, object?)[] { ("c", "%" + Secret + "%") });
            check("OTP-Code nicht in Konsole, Audit-Haken, Antworten und Datenbank", !all.Contains(Secret) && rows[0]["n"] == "0", all);
            check("Audit enthält nur Ereignis und Ergebniscode", audit.Count == 4 && audit.All(a => a.StartsWith("otp_send:") && a.Length < 40), string.Join(",", audit));
        }

        // HTTP-Endpunkt (Kestrel)
        {
            int port; using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
            Reset();
            var oidc = new FakeOidc();
            await using var app = InviteApp.Create(new InviteAppOptions { Url = $"http://127.0.0.1:{port}", Db = env.Db, Invitations = env.Inv, Delivery = env.Delivery, Oidc = oidc, AdminKey = "k", AdminUserId = env.Admin, OtpHandler = NewHandler(perRecipient: 100) });
            await app.StartAsync();
            var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            async Task<HttpResponseMessage> Post(string? authz, string body)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "/extensions/otp-send") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (authz is not null) req.Headers.TryAddWithoutValidation("Authorization", authz);
                return await http.SendAsync(req);
            }
            check("HTTP: ohne Token → 401, keine Mail", (await Post(null, Body())).StatusCode == HttpStatusCode.Unauthorized && GraphSends() == 0, null);
            var ok = await Post(Good(), Body());
            check("HTTP: mit Entra-Token → 200 + continue, genau eine Mail", ok.StatusCode == HttpStatusCode.OK && (await ok.Content.ReadAsStringAsync()).Contains("continueWithDefaultBehavior") && GraphSends() == 1, null);
            check("HTTP: überlanger Body → 413", (await Post(Good(), new string('x', 20000))).StatusCode == HttpStatusCode.RequestEntityTooLarge, null);
            check("HTTP: GET nicht erlaubt (405)", (await http.GetAsync("/extensions/otp-send")).StatusCode == HttpStatusCode.MethodNotAllowed, null);
            await app.StopAsync();

            int port2; using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port2 = ((IPEndPoint)l.LocalEndpoint).Port; }
            await using var off = InviteApp.Create(new InviteAppOptions { Url = $"http://127.0.0.1:{port2}", Db = env.Db, Invitations = env.Inv, Delivery = env.Delivery, Oidc = oidc, AdminKey = "k", AdminUserId = env.Admin });
            await off.StartAsync();
            check("Ohne konfigurierten Handler ist der Endpunkt nicht vorhanden (404, kein offener Mailversand)", (await new HttpClient().PostAsync($"http://127.0.0.1:{port2}/extensions/otp-send", new StringContent("{}"))).StatusCode == HttpStatusCode.NotFound, null);
            await off.StopAsync();
        }

        // Einladungsmail bleibt unverändert und an die Allowlist gebunden
        {
            Reset();
            var svc = new ExchangeOnlineMailService(new HttpClient(graph), MailOpt());
            var inv = await svc.SendAsync(new InvitationMail("invitee@y.test", "https://x/invite/T", DateTime.UtcNow.AddHours(1)));
            check("Einladungsmail: leere Allowlist weiterhin 'recipient_not_allowed' (OTP-Pfad hat sie nicht umgangen)", inv.Code == "recipient_not_allowed" && GraphSends() == 0, inv.Code);
            var otp = await svc.SendOtpAsync(new OtpMail("invitee@y.test", Code));
            check("OTP-Versand über denselben Dienst und dasselbe Postfach", otp.Outcome == MailOutcome.Accepted && GraphSends() == 1, otp.Code);
            var bad = await svc.SendOtpAsync(new OtpMail("invitee@y.test", "<script>"));
            check("Ungültiger Code wird nie versendet", bad.Code == "invalid_message" && GraphSends() == 1, bad.Code);
        }
    }

    static T Also<T>(this T value, Action<T>? action) { action?.Invoke(value); return value; }
}
