using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using LearnCards.Web.Auth;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;

/// <summary>
/// Vollständiger OIDC-Ablauf (ADR 0005/0036) gegen einen lokalen Test-Identity-Provider (HttpListener, RS256 mit Testschlüsseln, keine Pakete):
/// Login-Challenge → Authorize-URL → Formular-POST → Token-Austausch (PKCE) → JWKS-Signatur → Issuer/Audience/Exp/Nonce → Benutzer, Sitzung, Rollen.
/// Die Anwendung läuft dafür als zweiter Prozess im OIDC-Modus (Development, damit http://127.0.0.1 als Redirect-URI zulässig ist).
/// </summary>
public static class OidcFlowTests
{
    private const string ClientId = "it-client", ClientSecret = "it-client-secret";

    private sealed class Grant
    {
        public string Nonce = "", Challenge = "", RedirectUri = "", Sub = "";
        public DateTime Expires = DateTime.UtcNow.AddMinutes(5);
        public bool Used;
        public Action<Dictionary<string, object>>? Mod;
        public bool WrongKey;
    }

    private sealed class MockIdp : IDisposable
    {
        public readonly string Base;
        private readonly string _clientId, _clientSecret, _tenantPath;
        private readonly string? _tid;
        public string Issuer => Base + "/" + _tenantPath + "/v2.0";
        private readonly HttpListener _l = new();
        private readonly RSA _key = RSA.Create(2048), _otherKey = RSA.Create(2048);
        private readonly Dictionary<string, Grant> _grants = new();
        public bool LastPkceOk; public int TokenCalls;
        private readonly CancellationTokenSource _cts = new();

        public MockIdp(int port, string clientId = ClientId, string clientSecret = ClientSecret, string tenantPath = "tenant", string? tid = null)
        {
            _clientId = clientId; _clientSecret = clientSecret; _tenantPath = tenantPath; _tid = tid;
            Base = $"http://127.0.0.1:{port}";
            _l.Prefixes.Add(Base + "/");
            _l.Start();
            _ = Task.Run(async () => { while (!_cts.IsCancellationRequested) { try { Handle(await _l.GetContextAsync()); } catch { } } });
        }

        public string NewCode(Grant g) { var c = "code-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); lock (_grants) _grants[c] = g; return c; }

        private static string B64(byte[] d) => Convert.ToBase64String(d).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private string Jwt(Dictionary<string, object> claims, bool wrongKey)
        {
            var h = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\",\"kid\":\"k1\"}"));
            var p = B64(JsonSerializer.SerializeToUtf8Bytes(claims));
            var sig = (wrongKey ? _otherKey : _key).SignData(Encoding.ASCII.GetBytes(h + "." + p), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return h + "." + p + "." + B64(sig);
        }

        private void Handle(HttpListenerContext ctx)
        {
            string body; int status = 200;
            var path = ctx.Request.Url!.AbsolutePath;
            if (path.EndsWith("/.well-known/openid-configuration"))
                body = JsonSerializer.Serialize(new { issuer = Issuer, authorization_endpoint = Base + "/tenant/oauth2/v2.0/authorize", token_endpoint = Base + "/tenant/oauth2/v2.0/token", jwks_uri = Base + "/tenant/keys" });
            else if (path.EndsWith("/keys"))
            {
                var ps = _key.ExportParameters(false);
                body = JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", use = "sig", kid = "k1", alg = "RS256", n = B64(ps.Modulus!), e = B64(ps.Exponent!) } } });
            }
            else if (path.EndsWith("/token") && ctx.Request.HttpMethod == "POST")
            {
                TokenCalls++;
                using var r = new StreamReader(ctx.Request.InputStream);
                var f = HttpUtility.ParseQueryString(r.ReadToEnd());
                Grant? g; lock (_grants) _grants.TryGetValue(f["code"] ?? "", out g);
                var verifier = f["code_verifier"] ?? "";
                var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
                if (g is null || g.Used || g.Expires < DateTime.UtcNow || f["client_id"] != _clientId || f["client_secret"] != _clientSecret
                    || f["grant_type"] != "authorization_code" || f["redirect_uri"] != g.RedirectUri)
                { status = 400; body = "{\"error\":\"invalid_grant\"}"; LastPkceOk = false; }
                else if (challenge != g.Challenge) { status = 400; body = "{\"error\":\"invalid_grant\",\"error_description\":\"PKCE\"}"; LastPkceOk = false; }
                else
                {
                    g.Used = true; LastPkceOk = true;
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var claims = new Dictionary<string, object>
                    {
                        ["iss"] = Issuer, ["aud"] = _clientId, ["sub"] = g.Sub, ["nonce"] = g.Nonce, ["iat"] = now, ["exp"] = now + 3600,
                        ["name"] = "Test Benutzer " + g.Sub, ["email"] = g.Sub + "@example.org", ["roles"] = new[] { "Admin.All" },
                    };
                    if (_tid is not null) { claims["tid"] = _tid; claims["idp"] = "mail"; claims["ver"] = "2.0"; }
                    g.Mod?.Invoke(claims);
                    body = JsonSerializer.Serialize(new { token_type = "Bearer", access_token = "at-" + g.Sub, id_token = Jwt(claims, g.WrongKey) });
                }
            }
            else { status = 404; body = "{}"; }
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status; ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(bytes); ctx.Response.Close();
        }

        public void Dispose() { _cts.Cancel(); try { _l.Stop(); _l.Close(); } catch { } _key.Dispose(); _otherKey.Dispose(); }
    }

    public static async Task RunAsync(Action<bool, string> check, IDictionary<string, string?> baseEnv, string webDll, string tmp, Func<int> freePort)
    {
        using var idp = new MockIdp(freePort());
        const string ExtTid = "11111111-2222-3333-4444-555555555555", ExtClient = "ext-client", ExtSecret = "ext-client-secret";
        using var ext = new MockIdp(freePort(), ExtClient, ExtSecret, ExtTid, ExtTid);
        var port = freePort();
        var url = $"http://127.0.0.1:{port}";
        var psi = new ProcessStartInfo("dotnet", $"\"{webDll}\"") { WorkingDirectory = tmp, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var kv in baseEnv) psi.Environment[kv.Key] = kv.Value;
        void Env(string k, string v) => psi.Environment[k] = v;
        var dir = Path.Combine(tmp, "oidc-flow"); Directory.CreateDirectory(dir);
        Env("ASPNETCORE_URLS", url); Env("ASPNETCORE_ENVIRONMENT", "Development");
        Env("AUTH_MODE", "oidc"); Env("OIDC_ISSUER", idp.Issuer); Env("OIDC_CLIENT_ID", ClientId); Env("OIDC_CLIENT_SECRET", ClientSecret);
        Env("EXTID_ENABLED", "1"); Env("EXTID_AUTHORITY", ext.Issuer); Env("EXTID_TENANT_ID", ExtTid); Env("EXTID_CLIENT_ID", ExtClient); Env("EXTID_CLIENT_SECRET", ExtSecret);
        var inviteKeyB64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); Env("INVITE_TOKEN_KEY", inviteKeyB64);
        // Mailversand und OTP-Extension (Phase C): nur die Endpunkt-Authentifizierung wird über HTTP geprüft; es wird nie gesendet
        Env("MAIL_ENABLED", "1"); Env("MAIL_TENANT_ID", "db88f9b6-c89a-41a3-a21d-b61458e234ff"); Env("MAIL_CLIENT_ID", "aaaaaaaa-0000-4000-8000-000000000001"); Env("MAIL_SENDER", "learny@brors.link");
        Env("MAIL_CLIENT_SECRET", "mail-secret-nur-test"); Env("MAIL_ALLOWED_RECIPIENTS", "*"); Env("EXTID_OTP_ENABLED", "1"); Env("EXTID_OTP_AUDIENCES", "api-app");
        var dbPath = Path.Combine(dir, "oidc.db");
        Env("DB_PROVIDER", "sqlite"); Env("SQLITE_PATH", dbPath);
        Env("DATA_PROTECTION_DIR", Path.Combine(dir, "keys")); Env("PKI_KEY_DIR", Path.Combine(dir, "pki")); Env("PKI_PUBLIC_URL", url);
        Env("PUBLIC_BASE_URL", ""); Env("MCP_PUBLIC_URL", url + "/mcp-unused");

        var log = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
        var codes = new List<string>();
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(30) };
            var ready = false;
            for (var i = 0; i < 120 && !ready && !proc.HasExited; i++)
            {
                try { ready = (await http.GetAsync("/health")).IsSuccessStatusCode; } catch { await Task.Delay(250); }
            }
            if (!ready) { check(false, "OIDC-Ablauf: Testinstanz im OIDC-Modus wurde nicht bereit\n" + log); return; }

            static string Cookies(HttpResponseMessage r) => string.Join("; ", (r.Headers.TryGetValues("Set-Cookie", out var v) ? v : [])
                .Select(c => c.Split(';')[0]).Where(c => c.IndexOf('=') is > 0 and var i && i < c.Length - 1));

            // Login-Challenge → Authorize-URL und Korrelationscookie
            async Task<(string State, string Nonce, string Challenge, string Redirect, string Cookie, string Authorize)> StartLogin()
            {
                var r = await http.GetAsync("/auth/login?returnUrl=/profile");
                var loc = r.Headers.Location!.OriginalString;
                var q = HttpUtility.ParseQueryString(new Uri(loc).Query);
                return (q["state"]!, q["nonce"]!, q["code_challenge"]!, q["redirect_uri"]!, Cookies(r), loc);
            }

            // IdP bestätigt den Benutzer und leitet per Formular-POST zurück
            async Task<(HttpResponseMessage Res, string Code)> Callback(string cookie, string state, string code)
            {
                codes.Add(code);
                var req = new HttpRequestMessage(HttpMethod.Post, "/auth/callback")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["state"] = state, ["session_state"] = "ss" }),
                };
                if (cookie.Length > 0) req.Headers.Add("Cookie", cookie);
                return (await http.SendAsync(req), code);
            }

            string Loc(HttpResponseMessage r) => r.Headers.Location?.OriginalString ?? "";
            bool Failed(HttpResponseMessage r, string part) => r.StatusCode == HttpStatusCode.SeeOther && Loc(r).StartsWith("/login?error=") && !Cookies(r).Contains("lc.auth=")
                && Uri.UnescapeDataString(Loc(r).Replace('+', ' ')).Contains(part, StringComparison.OrdinalIgnoreCase);

            Grant NewGrant(string sub, (string State, string Nonce, string Challenge, string Redirect, string Cookie, string Authorize) l, Action<Dictionary<string, object>>? mod = null) =>
                new() { Sub = sub, Nonce = l.Nonce, Challenge = l.Challenge, RedirectUri = l.Redirect, Mod = mod };

            // 1. Gültiger Ablauf
            var login = await StartLogin();
            check(login.Authorize.Contains("response_mode=form_post") && login.Authorize.Contains("code_challenge_method=S256") && login.Authorize.StartsWith(idp.Base + "/tenant/oauth2/v2.0/authorize?"),
                "OIDC-Ablauf: Login leitet mit form_post und PKCE S256 zum Identity Provider");
            check(login.Redirect == url + "/auth/callback", "OIDC-Ablauf: Redirect-URI ist die Callback-Adresse der Anwendung");
            var sub = "user-" + Guid.NewGuid().ToString("N")[..8];
            var ok = await Callback(login.Cookie, login.State, idp.NewCode(NewGrant(sub, login)));
            var session = Cookies(ok.Res);
            check(ok.Res.StatusCode == HttpStatusCode.SeeOther && Loc(ok.Res) == "/profile" && session.Contains("lc.auth=") && ok.Res.Headers.CacheControl?.NoStore == true,
                "OIDC-Ablauf: gültiger Callback → 303 auf returnUrl, Sitzungscookie, no-store");
            check(idp.LastPkceOk, "OIDC-Ablauf: Token-Austausch mit korrektem PKCE-Verifier akzeptiert");
            var me = new HttpRequestMessage(HttpMethod.Get, "/api/me"); me.Headers.Add("Cookie", session);
            var meRes = await http.SendAsync(me);
            using var meJson = JsonDocument.Parse(await meRes.Content.ReadAsStringAsync());
            var id1 = meJson.RootElement.GetProperty("id").GetString();
            check(meRes.IsSuccessStatusCode && meJson.RootElement.GetProperty("email").GetString() == sub + "@example.org"
                  && meJson.RootElement.GetProperty("roles").EnumerateArray().Any(r => r.GetString() == "admin"),
                "OIDC-Ablauf: Benutzer angelegt, Sitzung gültig, Entra-Rolle Admin.All → admin synchronisiert");

            // 2. Zweiter Login desselben Subjekts → gleiche interne Benutzer-ID, Rolle entzogen
            var login2 = await StartLogin();
            var again = await Callback(login2.Cookie, login2.State, idp.NewCode(NewGrant(sub, login2, c => c["roles"] = Array.Empty<string>())));
            var me2 = new HttpRequestMessage(HttpMethod.Get, "/api/me"); me2.Headers.Add("Cookie", Cookies(again.Res));
            using var me2Json = JsonDocument.Parse(await (await http.SendAsync(me2)).Content.ReadAsStringAsync());
            check(me2Json.RootElement.GetProperty("id").GetString() == id1 && !me2Json.RootElement.GetProperty("roles").EnumerateArray().Any(r => r.GetString() == "admin"),
                "OIDC-Ablauf: gleiches Subjekt → gleiche interne Benutzer-ID; fehlende Entra-Rolle entzieht admin");

            // 3. Negative Fälle
            var l = await StartLogin();
            check(Failed((await Callback(l.Cookie, "manipuliert", idp.NewCode(NewGrant("x1", l)))).Res, "State"), "OIDC-Negativ: manipulierter state abgewiesen");

            l = await StartLogin();
            check(Failed((await Callback(l.Cookie, l.State, idp.NewCode(NewGrant("x2", l, c => c["nonce"] = "fremd")))).Res, "Nonce"), "OIDC-Negativ: manipulierte nonce abgewiesen");

            l = await StartLogin();
            var g = NewGrant("x3", l); g.WrongKey = true;
            check(Failed((await Callback(l.Cookie, l.State, idp.NewCode(g))).Res, "Signatur"), "OIDC-Negativ: ungültige Token-Signatur abgewiesen");

            l = await StartLogin();
            check(Failed((await Callback(l.Cookie, l.State, idp.NewCode(NewGrant("x4", l, c => c["iss"] = "https://evil.example/v2.0")))).Res, "Issuer"), "OIDC-Negativ: falscher Issuer abgewiesen");

            l = await StartLogin();
            check(Failed((await Callback(l.Cookie, l.State, idp.NewCode(NewGrant("x5", l, c => c["aud"] = "anderer-client")))).Res, "Audience"), "OIDC-Negativ: falsche Audience abgewiesen");

            l = await StartLogin();
            check(Failed((await Callback(l.Cookie, l.State, idp.NewCode(NewGrant("x6", l, c => c["exp"] = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds())))).Res, "abgelaufen"),
                "OIDC-Negativ: abgelaufenes ID-Token abgewiesen");

            l = await StartLogin();
            check(Failed((await Callback(l.Cookie, l.State, idp.NewCode(NewGrant("x7", l, c => c.Remove("exp"))))).Res, "abgelaufen"),
                "OIDC-Negativ: ID-Token ohne exp abgewiesen");

            l = await StartLogin();
            var wrongChallenge = NewGrant("x8", l); wrongChallenge.Challenge = "falsche-challenge";
            check(Failed((await Callback(l.Cookie, l.State, idp.NewCode(wrongChallenge))).Res, "Token-Endpoint"), "OIDC-Negativ: PKCE-Verifier passt nicht zur Challenge → Token-Austausch scheitert");

            l = await StartLogin();
            var expiredCode = NewGrant("x9", l); expiredCode.Expires = DateTime.UtcNow.AddMinutes(-1);
            check(Failed((await Callback(l.Cookie, l.State, idp.NewCode(expiredCode))).Res, "Token-Endpoint"), "OIDC-Negativ: abgelaufener Authorization Code abgewiesen");

            l = await StartLogin();
            var firstCode = idp.NewCode(NewGrant("x10", l));
            var first = await Callback(l.Cookie, l.State, firstCode);
            var replay = await Callback(l.Cookie, l.State, firstCode);
            check(first.Res.StatusCode == HttpStatusCode.SeeOther && Loc(first.Res) == "/profile" && Failed(replay.Res, "bereits verwendet"), "OIDC-Negativ: wiederholter Callback (gleiches Cookie, gleicher state) abgewiesen");

            l = await StartLogin();
            check(Failed((await Callback("", l.State, idp.NewCode(NewGrant("x11", l)))).Res, "abgelaufen"), "OIDC-Negativ: Callback ohne Korrelationscookie abgewiesen (Login-CSRF)");

            // 4. Parallele Logins: unabhängig, aber nicht untereinander austauschbar
            var a = await StartLogin(); var b = await StartLogin();
            var crossCode = idp.NewCode(NewGrant("x12", b));
            var cross = await Callback(a.Cookie, b.State, crossCode);
            var resA = await Callback(a.Cookie, a.State, idp.NewCode(NewGrant("par-a-" + sub, a)));
            var resB = await Callback(b.Cookie, b.State, idp.NewCode(NewGrant("par-b-" + sub, b)));
            check(Failed(cross.Res, "State"), "OIDC-Parallel: state von Login B mit Cookie von Login A abgewiesen");
            check(Loc(resA.Res) == "/profile" && Loc(resB.Res) == "/profile" && Cookies(resA.Res).Contains("lc.auth=") && Cookies(resB.Res).Contains("lc.auth="),
                "OIDC-Parallel: zwei gleichzeitige Logins verlaufen unabhängig erfolgreich");

            // 5. Entra External ID als zweiter, getrennter Anbieter (ADR 0048) + Bestandsschutz der Workforce-Benutzer
            async Task<(string State, string Nonce, string Challenge, string Redirect, string Cookie, string Authorize)> StartExt()
            {
                var r = await http.GetAsync("/auth/external/login?returnUrl=/profile");
                var loc = r.Headers.Location!.OriginalString;
                var q = HttpUtility.ParseQueryString(new Uri(loc).Query);
                return (q["state"]!, q["nonce"]!, q["code_challenge"]!, q["redirect_uri"]!, Cookies(r), loc);
            }
            async Task<HttpResponseMessage> ExtCallback(string cookie, string state, string code, string method = "POST")
            {
                if (code.Length > 0) codes.Add(code);
                var req = new HttpRequestMessage(new HttpMethod(method), "/auth/external/callback");
                if (method == "POST") req.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["state"] = state });
                if (cookie.Length > 0) req.Headers.Add("Cookie", cookie);
                return await http.SendAsync(req);
            }
            Grant ExtGrant(string sub, (string State, string Nonce, string Challenge, string Redirect, string Cookie, string Authorize) l, Action<Dictionary<string, object>>? mod = null) =>
                new() { Sub = sub, Nonce = l.Nonce, Challenge = l.Challenge, RedirectUri = l.Redirect, Mod = mod };
            await using var appDb = new SqliteDatabase(dbPath);
            await appDb.InitAsync();   // öffnet die Verbindung; das Schema ist idempotent (CREATE IF NOT EXISTS)
            async Task<long> Count(string sql, params (string, object?)[] a) => long.Parse((await appDb.QueryAsync(sql, a))[0]["n"]!);

            var e1 = await StartExt();
            var eq = HttpUtility.ParseQueryString(new Uri(e1.Authorize).Query);
            check(e1.Authorize.StartsWith(ext.Base + "/tenant/oauth2/v2.0/authorize?") && eq["client_id"] == ExtClient && eq["prompt"] == "login" && eq["response_mode"] == "form_post" && eq["code_challenge_method"] == "S256"
                  && e1.Redirect == url + "/auth/external/callback" && e1.Cookie.Contains("lc.oidc.ext.state=") && !e1.Cookie.Contains("lc.oidc.state="),
                "External ID: eigener Login-Pfad, eigene Client-ID, prompt=login, form_post/PKCE, eigener Rücksprung und eigenes Korrelationscookie");
            var wf = await StartLogin();
            check(wf.Authorize.StartsWith(idp.Base + "/") && HttpUtility.ParseQueryString(new Uri(wf.Authorize).Query)["client_id"] == ClientId && !wf.Authorize.Contains("prompt=login") && wf.Redirect == url + "/auth/callback",
                "Bestandsschutz: Workforce-Login verwendet weiter denselben Issuer, dieselbe Client-ID und denselben Rücksprung");

            // Unbekannte externe Identität: keine Anlage, keine Anmeldung (Registrierung nur per Einladung)
            var usersBefore = await Count("SELECT COUNT(*) AS n FROM users");
            var unknown = await ExtCallback(e1.Cookie, e1.State, ext.NewCode(ExtGrant("ext-unknown", e1)));
            check(Failed(unknown, "Einladung") && await Count("SELECT COUNT(*) AS n FROM users") == usersBefore && await Count("SELECT COUNT(*) AS n FROM user_identities WHERE provider=@p", ("p", ext.Issuer)) == 0,
                "External ID: unbekannte Identität wird abgewiesen — kein Benutzer, keine Identität, keine Sitzung");

            // Gleiche E-Mail wie ein Workforce-Benutzer, aber andere Identität: keine Zusammenführung
            var mergeEmail = sub + "@example.org";
            var wfUid = (await appDb.QueryAsync("SELECT user_id FROM user_identities WHERE provider=@p AND subject=@s", new (string, object?)[] { ("p", idp.Issuer), ("s", sub) }))[0]["user_id"]!;
            var identitiesBefore = await Count("SELECT COUNT(*) AS n FROM user_identities WHERE user_id=@u", ("u", wfUid));
            var e2 = await StartExt();
            var sameMail = await ExtCallback(e2.Cookie, e2.State, ext.NewCode(ExtGrant("ext-same-mail", e2, c => c["email"] = mergeEmail)));
            check(Failed(sameMail, "Einladung") && await Count("SELECT COUNT(*) AS n FROM user_identities WHERE user_id=@u", ("u", wfUid)) == identitiesBefore && await Count("SELECT COUNT(*) AS n FROM users") == usersBefore,
                "External ID: gleiche E-Mail wie ein Workforce-Benutzer führt zu keiner Verknüpfung oder Anmeldung als dieser Benutzer");

            // Gleiches Subject bei anderem Anbieter ist eine andere Identität
            var e3 = await StartExt();
            var sameSub = await ExtCallback(e3.Cookie, e3.State, ext.NewCode(ExtGrant(sub, e3)));
            check(Failed(sameSub, "Einladung"), "External ID: gleiches sub wie ein Workforce-Benutzer meldet nicht als dieser an (Identität = Issuer + sub)");

            // Bekannte externe Identität (so entsteht sie später durch die Einladungseinlösung)
            var extUid = Guid.NewGuid().ToString(); var now = DateTime.UtcNow;
            await appDb.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id,'Externer Test','ext@example.org','active',1,@n,@n)", new (string, object?)[] { ("id", extUid), ("n", now) });
            await appDb.ExecuteAsync("INSERT INTO user_identities (provider, subject, user_id, created_at, kind) VALUES (@p,@s,@u,@n,'external')", new (string, object?)[] { ("p", ext.Issuer), ("s", "ext-known"), ("u", extUid), ("n", now) });
            await appDb.ExecuteAsync("INSERT INTO user_profiles (user_id, profile, source_invitation_id, created_at) VALUES (@u,'B2C_LEARNER',NULL,@n)", new (string, object?)[] { ("u", extUid), ("n", now) });
            await appDb.ExecuteAsync("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@u,'user',NULL,@n)", new (string, object?)[] { ("u", extUid), ("n", now) });
            var e4 = await StartExt();
            var extOk = await Callback_Ext(e4, "ext-known", c => { });
            async Task<HttpResponseMessage> Callback_Ext((string State, string Nonce, string Challenge, string Redirect, string Cookie, string Authorize) l, string extSub, Action<Dictionary<string, object>> mod) =>
                await ExtCallback(l.Cookie, l.State, ext.NewCode(ExtGrant(extSub, l, mod)));
            var extSession = Cookies(extOk);
            var meExt = new HttpRequestMessage(HttpMethod.Get, "/api/me"); meExt.Headers.Add("Cookie", extSession);
            using var meExtJson = JsonDocument.Parse(await (await http.SendAsync(meExt)).Content.ReadAsStringAsync());
            check(extOk.StatusCode == HttpStatusCode.SeeOther && Loc(extOk) == "/profile" && extSession.Contains("lc.auth=") && meExtJson.RootElement.GetProperty("id").GetString() == extUid,
                "External ID: bekannte externe Identität meldet sich an und erhält ihre eigene interne Benutzer-ID");
            check(!meExtJson.RootElement.GetProperty("roles").EnumerateArray().Any(r => r.GetString() is "admin" or "auditor" or "themen_admin") && await Count("SELECT COUNT(*) AS n FROM user_roles WHERE user_id=@u AND role<>'user'", ("u", extUid)) == 0,
                "External ID: Rolle Admin.All im Token wird nie übernommen (keine Rolleneskalation über den zweiten Anbieter)");

            // Prüfungen des External-ID-Tokens: Tenant, Audience, Issuer, Signatur, Wiederholung
            var tenantBad = await StartExt();
            check(Failed(await Callback_Ext(tenantBad, "ext-known", c => c["tid"] = "99999999-9999-9999-9999-999999999999"), "nicht möglich"),
                "External ID: falscher Tenant (tid) abgewiesen");
            var tenantMissing = await StartExt();
            check(Failed(await Callback_Ext(tenantMissing, "ext-known", c => c.Remove("tid")), "nicht möglich"), "External ID: fehlender tid abgewiesen");
            var audBad = await StartExt();
            check(Failed(await Callback_Ext(audBad, "ext-known", c => c["aud"] = ClientId), "nicht möglich"), "External ID: Audience des Workforce-Clients wird nicht akzeptiert");
            var issBad = await StartExt();
            check(Failed(await Callback_Ext(issBad, "ext-known", c => c["iss"] = idp.Issuer), "nicht möglich"), "External ID: Token mit Workforce-Issuer wird nicht akzeptiert");
            var sigBad = await StartExt();
            var sigGrant = ExtGrant("ext-known", sigBad); sigGrant.WrongKey = true;
            check(Failed(await ExtCallback(sigBad.Cookie, sigBad.State, ext.NewCode(sigGrant)), "nicht möglich"), "External ID: ungültige Signatur abgewiesen");
            var expBad = await StartExt();
            check(Failed(await Callback_Ext(expBad, "ext-known", c => c["exp"] = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds()), "nicht möglich"), "External ID: abgelaufenes Token abgewiesen");
            var nonceBad = await StartExt();
            check(Failed(await Callback_Ext(nonceBad, "ext-known", c => c["nonce"] = "fremd"), "nicht möglich"), "External ID: falsche nonce abgewiesen");
            var diag = (await appDb.QueryAsync("SELECT detail FROM audit_log WHERE action='login.external.failed' ORDER BY created_at DESC LIMIT 1"));
            check(diag.Count == 1 && diag[0]["detail"]!.StartsWith("stage=id_token") && diag[0]["detail"]!.Contains("Nonce") && !diag[0]["detail"]!.Contains("ext-known") && diag[0]["detail"]!.Length < 260,
                "External ID: der Abweisungsgrund steht mit Stufe im Audit (ohne Subject, Code oder Token) — sichtbar in Administration → Übersicht → Protokoll");
            var foreignCode = await StartExt();   // Code des Workforce-IdP am External-Rücksprung: dem anderen Anbieter unbekannt
            check(Failed(await ExtCallback(foreignCode.Cookie, foreignCode.State, idp.NewCode(NewGrant("x", await StartLogin()))), "nicht möglich"),
                "External ID: ein Authorization Code des Workforce-Anbieters ist am External-Rücksprung wirkungslos");

            var replayL = await StartExt();
            var replayCode = ext.NewCode(ExtGrant("ext-known", replayL));
            var firstExt = await ExtCallback(replayL.Cookie, replayL.State, replayCode);
            var replayExt = await ExtCallback(replayL.Cookie, replayL.State, replayCode);
            check(Loc(firstExt) == "/profile" && Failed(replayExt, "bereits verwendet"), "External ID: Wiederholung desselben Rücksprungs (Replay) abgewiesen");
            check(Failed(await ExtCallback("", replayL.State, ext.NewCode(ExtGrant("ext-known", replayL))), "abgelaufen"), "External ID: Callback ohne Korrelationscookie abgewiesen (Login-CSRF)");
            check(Loc(await ExtCallback("", "", "", "GET")).StartsWith("/login?error="), "External ID: GET-Rücksprung wird nicht verarbeitet");

            // Deaktivierter externer Benutzer
            await appDb.ExecuteAsync("UPDATE users SET status='disabled' WHERE id=@u", new (string, object?)[] { ("u", extUid) });
            var disabledL = await StartExt();
            check(Failed(await Callback_Ext(disabledL, "ext-known", c => { }), "Zugang gesperrt"), "External ID: deaktivierter Benutzer wird abgewiesen");

            // Der Workforce-Benutzer ist von allem unberührt
            var meAfter = new HttpRequestMessage(HttpMethod.Get, "/api/me"); meAfter.Headers.Add("Cookie", session);
            check(await Count("SELECT COUNT(*) AS n FROM users WHERE id=@u", ("u", wfUid)) == 1 && (await http.SendAsync(meAfter)).StatusCode != HttpStatusCode.Unauthorized,
                "Bestandsschutz: bestehende Workforce-Sitzung und -Benutzer bleiben unverändert");
            check(await Count("SELECT COUNT(*) AS n FROM oidc_consumed_states") >= 1, "External ID: verbrauchte state-Werte liegen als Hash in der Datenbank (instanzübergreifende Einmaligkeit)");

            // 5b. Einladungspflichtige Registrierung über External ID (ADR 0049)
            var inviteSettings = new InviteSettings { TokenKey = Convert.FromBase64String(inviteKeyB64), LinkBaseUrl = url };
            var invites = InvitationService.Create(appDb, new UserService(appDb, new AppConfig()), inviteSettings, new InviteEvents(appDb));
            var invAdmin = Guid.NewGuid().ToString(); var invModule = Guid.NewGuid().ToString(); var nowInv = DateTime.UtcNow;
            await appDb.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id,'Einladungs-Admin','inv-admin@example.org','active',1,@n,@n)", new (string, object?)[] { ("id", invAdmin), ("n", nowInv) });
            await appDb.ExecuteAsync("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@u,'user',NULL,@n),(@u,'admin',NULL,@n)", new (string, object?)[] { ("u", invAdmin), ("n", nowInv) });
            await appDb.ExecuteAsync("INSERT INTO modules (id, name, scope, publication_id, global_version, global_status, created_at, updated_at) VALUES (@m,'Einladungs-Modul','global',@p,1,'active',@n,@n)", new (string, object?)[] { ("m", invModule), ("p", Guid.NewGuid().ToString()), ("n", nowInv) });

            async Task<(string Authorize, string Cookie, string State, HttpResponseMessage Res)> OpenInvite(string token)
            {
                codes.Add(token);
                var r = await http.PostAsync("/invite/start", new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("token", token) }));   // Token nur im POST-Body (ADR 0049)
                var loc = r.Headers.Location?.OriginalString ?? "";
                var q = loc.StartsWith("http") ? HttpUtility.ParseQueryString(new Uri(loc).Query) : HttpUtility.ParseQueryString("");
                return (loc, Cookies(r), q["state"] ?? "", r);
            }
            async Task<HttpResponseMessage> InviteCallback(string token, string extSub, Action<Dictionary<string, object>>? mod = null)
            {
                var o = await OpenInvite(token);
                var q = HttpUtility.ParseQueryString(new Uri(o.Authorize).Query);
                var g = new Grant { Sub = extSub, Nonce = q["nonce"]!, Challenge = q["code_challenge"]!, RedirectUri = q["redirect_uri"]!, Mod = mod };
                return await ExtCallback(o.Cookie, o.State, ext.NewCode(g));
            }
            bool Inv(HttpResponseMessage r) => Failed(r, "ungültig oder nicht einlösbar");
            async Task<string> InvStatus(string id) => (await appDb.QueryAsync("SELECT status FROM invitations WHERE invitation_id=@i", new (string, object?)[] { ("i", id) }))[0]["status"]!;

            // gültige Einladung → Registrierung, Profil und Zuweisung aus dem Einladungsdatensatz
            var inv1 = await invites.CreateAsync(invAdmin, "neu@example.org", "B2C_LEARNER", new[] { invModule });
            var open1 = await OpenInvite(inv1.Token);
            check(open1.Authorize.StartsWith(ext.Base + "/tenant/oauth2/v2.0/authorize?") && open1.Authorize.Contains("prompt=login") && !open1.Authorize.Contains(inv1.Token) && open1.Cookie.Contains("lc.oidc.ext.state=") && !open1.Cookie.Contains(inv1.Token),
                "Einladung: Link leitet mit prompt=login zu Entra; der Einladungstoken steht weder in der Authorize-URL noch im Cookie im Klartext");
            // Schutz des Einladungslinks: Token im Fragment, nie in Pfad/Query/Referrer; die Seite selbst enthält keinen Token
            check(inv1.Link == url + "/invite#" + inv1.Token, "Einladungslink: Token steht im Fragment (/invite#<token>), das nie an Server, IIS oder Proxy gesendet wird");
            var invPage = await http.GetAsync("/invite");
            var invPageBody = await invPage.Content.ReadAsStringAsync();
            check(invPage.StatusCode == HttpStatusCode.OK && invPage.Headers.GetValues("Referrer-Policy").Contains("no-referrer") && invPage.Headers.GetValues("Cache-Control").Any(v => v.Contains("no-store"))
                  && invPageBody.Contains("/invite.js") && !invPageBody.Contains(inv1.Token) && invPage.Headers.Contains("Content-Security-Policy"),
                "Einladungsseite: tokenlos, no-store, Referrer-Policy no-referrer, eigene CSP, Skript aus eigener Quelle");
            var invJs = await (await http.GetAsync("/invite.js")).Content.ReadAsStringAsync();
            check(invJs.Contains("location.hash") && invJs.Contains("replaceState") && invJs.Contains("/invite/start") && invJs.Contains("'post'"),
                "invite.js: liest das Fragment, entfernt es aus der Adressleiste und sendet den Token per POST");
            var eventsBeforeLegacy = await Count("SELECT COUNT(*) AS n FROM invitation_events WHERE invitation_id=@i AND event_type IN ('invitation.opened','registration.started')", ("i", inv1.InvitationId));
            var legacy = await http.GetAsync("/invite/" + inv1.Token);
            check(Loc(legacy).StartsWith("/login?error=") && !legacy.Headers.Contains("Set-Cookie") && await Count("SELECT COUNT(*) AS n FROM invitation_events WHERE invitation_id=@i AND event_type IN ('invitation.opened','registration.started')", ("i", inv1.InvitationId)) == eventsBeforeLegacy,
                "Alte Linkform /invite/<token> (Token im Pfad) wird nicht mehr verarbeitet: keine Weiterleitung zu Entra, kein Cookie, kein Ereignis [" + (int)legacy.StatusCode + " " + Loc(legacy) + " cookie=" + legacy.Headers.Contains("Set-Cookie") + "]");
            check((await http.PostAsync("/invite/start", new StringContent("{\"token\":\"x\"}", Encoding.UTF8, "application/json"))).Headers.Location?.OriginalString.StartsWith("/login?error=") == true,
                "Einladung: POST ohne Formularfeld wird abgewiesen");
            var usersBeforeInv = await Count("SELECT COUNT(*) AS n FROM users");
            var reg = await InviteCallback(inv1.Token, "inv-sub-1", c => { c["email"] = "neu@example.org"; });
            var regSession = Cookies(reg);
            var meReg = new HttpRequestMessage(HttpMethod.Get, "/api/me"); meReg.Headers.Add("Cookie", regSession);
            using var meRegJson = JsonDocument.Parse(await (await http.SendAsync(meReg)).Content.ReadAsStringAsync());
            var newUid = meRegJson.RootElement.GetProperty("id").GetString()!;
            check(reg.StatusCode == HttpStatusCode.SeeOther && Loc(reg) == "/start" && regSession.Contains("lc.auth=") && meRegJson.RootElement.GetProperty("email").GetString() == "neu@example.org",
                "Einladung: gültige Einladung + bestätigte E-Mail → Benutzer angelegt und angemeldet");
            check(await Count("SELECT COUNT(*) AS n FROM users") == usersBeforeInv + 1
                  && await Count("SELECT COUNT(*) AS n FROM user_identities WHERE user_id=@u AND kind='external' AND provider=@p", ("u", newUid), ("p", ext.Issuer)) == 1
                  && await Count("SELECT COUNT(*) AS n FROM user_profiles WHERE user_id=@u AND profile='B2C_LEARNER' AND source_invitation_id=@i", ("u", newUid), ("i", inv1.InvitationId)) == 1
                  && await Count("SELECT COUNT(*) AS n FROM module_grants WHERE user_id=@u AND module_id=@m AND revoked_at IS NULL", ("u", newUid), ("m", invModule)) == 1
                  && await InvStatus(inv1.InvitationId) == "accepted",
                "Einladung: Identität (Art external), Profil B2C_LEARNER, Modulzuweisung und Einladungsstatus entstehen gemeinsam");
            check(await Count("SELECT COUNT(*) AS n FROM user_roles WHERE user_id=@u AND role<>'user'", ("u", newUid)) == 0 && !meRegJson.RootElement.GetProperty("roles").EnumerateArray().Any(r => r.GetString() is "admin" or "auditor" or "themen_admin"),
                "Einladung: das Token enthält Admin.All, die neue externe Identität erhält trotzdem nur die Rolle user");
            var evTypes = (await appDb.QueryAsync("SELECT event_type, category FROM invitation_events WHERE invitation_id=@i", new (string, object?)[] { ("i", inv1.InvitationId) })).ToList();
            var evNames = evTypes.Select(e => e["event_type"]).ToHashSet();
            check(new[] { "invitation.created", "invitation.opened", "registration.started", "authentication.completed", "invitation.accepted", "user.created", "profile.assigned", "content.assigned" }.All(evNames.Contains)
                  && evTypes.All(e => !(e["category"] ?? "").Contains(inv1.Token)),
                "Einladung: Ereignisprotokoll enthält die beobachteten Schritte (ohne Token)");

            // Wiederverwendung, Widerruf, Ablauf, falsche E-Mail, lokales Konto, bestehende Identität
            check(Loc((await OpenInvite(inv1.Token)).Res).StartsWith("/login?error="), "Einladung: ein bereits eingelöster Link wird abgewiesen");
            var invRevoked = await invites.CreateAsync(invAdmin, "widerruf@example.org", "B2C_LEARNER");
            await invites.RevokeAsync(invAdmin, invRevoked.InvitationId);
            check(Loc((await OpenInvite(invRevoked.Token)).Res).StartsWith("/login?error="), "Einladung: widerrufene Einladung wird abgewiesen");
            var invExpired = await invites.CreateAsync(invAdmin, "abgelaufen@example.org", "B2C_LEARNER");
            await appDb.ExecuteAsync("UPDATE invitations SET expires_at=@e WHERE invitation_id=@i", new (string, object?)[] { ("i", invExpired.InvitationId), ("e", DateTime.UtcNow.AddHours(-1)) });
            check(Loc((await OpenInvite(invExpired.Token)).Res).StartsWith("/login?error="), "Einladung: abgelaufene Einladung wird abgewiesen");
            check(Loc((await OpenInvite("kein-bekannter-token-0123456789abcdef")).Res).StartsWith("/login?error=") && Loc((await OpenInvite("zu-kurz")).Res).StartsWith("/login?error="), "Einladung: unbekannter oder fehlerhafter Token wird abgewiesen");

            var invMail = await invites.CreateAsync(invAdmin, "richtig@example.org", "B2C_LEARNER");
            var usersBeforeBad = await Count("SELECT COUNT(*) AS n FROM users");
            check(Inv(await InviteCallback(invMail.Token, "inv-sub-2", c => { c["email"] = "falsch@example.org"; })) && await Count("SELECT COUNT(*) AS n FROM users") == usersBeforeBad && await InvStatus(invMail.InvitationId) == "pending",
                "Einladung: falsche E-Mail → abgewiesen, kein Benutzer, Einladung bleibt gültig");
            check(Inv(await InviteCallback(invMail.Token, "inv-sub-2", c => { c["email"] = "richtig@example.org"; c.Remove("idp"); })) && await Count("SELECT COUNT(*) AS n FROM users") == usersBeforeBad,
                "Einladung: Token ohne idp=mail (z. B. lokales Konto) gilt nicht als bestätigte E-Mail → abgewiesen");
            check(Inv(await InviteCallback(invMail.Token, "inv-sub-2", c => { c["email"] = "richtig@example.org"; c["ver"] = "1.0"; })) && await Count("SELECT COUNT(*) AS n FROM users") == usersBeforeBad,
                "Einladung: Tokenversion ≠ 2.0 gilt nicht als bestätigt → abgewiesen");
            check(Failed(await InviteCallback(invMail.Token, "inv-sub-2", c => { c["email"] = "richtig@example.org"; c["tid"] = "99999999-9999-9999-9999-999999999999"; }), "nicht möglich") && await InvStatus(invMail.InvitationId) == "pending",
                "Einladung: falscher Tenant → abgewiesen, Einladung bleibt gültig");
            var okAfter = await InviteCallback(invMail.Token, "inv-sub-2", c => { c["email"] = "richtig@example.org"; });
            check(Loc(okAfter) == "/start" && Cookies(okAfter).Contains("lc.auth=") && await InvStatus(invMail.InvitationId) == "accepted", "Einladung: nach Fehlversuchen löst die richtige Anmeldung die Einladung ein");

            var invAgain = await invites.CreateAsync(invAdmin, "zweite@example.org", "B2B_LEARNER");
            check(Inv(await InviteCallback(invAgain.Token, "inv-sub-1", c => { c["email"] = "zweite@example.org"; })) && await InvStatus(invAgain.InvitationId) == "pending",
                "Einladung: bereits registrierte Identität (Issuer+sub) kann keine zweite Einladung einlösen, die Einladung bleibt gültig");

            // Anmeldung bereits registrierter Benutzer: nur mit externem Profil
            var relog = await StartExt();
            var relogRes = await ExtCallback(relog.Cookie, relog.State, ext.NewCode(ExtGrant("inv-sub-1", relog)));
            check(Loc(relogRes) == "/profile" && Cookies(relogRes).Contains("lc.auth="), "Externe Anmeldung: bereits registrierter Benutzer meldet sich ohne Einladung an");
            await appDb.ExecuteAsync("DELETE FROM user_profiles WHERE user_id=@u", new (string, object?)[] { ("u", newUid) });
            var noProf = await StartExt();
            check(Failed(await ExtCallback(noProf.Cookie, noProf.State, ext.NewCode(ExtGrant("inv-sub-1", noProf))), "nicht vollständig"),
                "Externe Anmeldung: externe Identität ohne Lernprofil wird abgewiesen (keine Bestandsberechtigung)");
            await appDb.ExecuteAsync("INSERT INTO user_profiles (user_id, profile, source_invitation_id, created_at) VALUES (@u,'B2C_LEARNER',@i,@n)", new (string, object?)[] { ("u", newUid), ("i", inv1.InvitationId), ("n", DateTime.UtcNow) });
            await appDb.ExecuteAsync("UPDATE user_identities SET kind='workforce' WHERE provider=@p AND subject='inv-sub-1'", new (string, object?)[] { ("p", ext.Issuer) });
            var wrongKind = await StartExt();
            check(Failed(await ExtCallback(wrongKind.Cookie, wrongKind.State, ext.NewCode(ExtGrant("inv-sub-1", wrongKind))), "nicht vollständig"),
                "Externe Anmeldung: eine Identität des External-Issuers mit falscher Art wird abgewiesen");
            await appDb.ExecuteAsync("UPDATE user_identities SET kind='external' WHERE provider=@p AND subject='inv-sub-1'", new (string, object?)[] { ("p", ext.Issuer) });

            // Mail-Zertifikat: nur für Administratoren über die Browser-Anmeldung
            var certDlAnon = await http.GetAsync("/api/admin/mail/certificate.cer");
            var certDlReq = new HttpRequestMessage(HttpMethod.Get, "/api/admin/mail/certificate.cer"); certDlReq.Headers.Add("Cookie", session);
            check(certDlAnon.StatusCode == HttpStatusCode.Unauthorized && (await http.SendAsync(certDlReq)).StatusCode == HttpStatusCode.Forbidden, "Mail-Zertifikat: Download ohne Anmeldung 401, als normaler Benutzer 403");
            // Externe Lernprofile: kein MCP-Zugang über die API (403 statt Fehler), weder Token noch Verknüpfung
            var mcpReq = new HttpRequestMessage(HttpMethod.Post, "/api/mcp/links") { Content = new StringContent("{\"name\":\"x\",\"days\":30}", Encoding.UTF8, "application/json") };
            mcpReq.Headers.Add("Cookie", regSession);
            var mcpRes = await http.SendAsync(mcpReq);
            check(mcpRes.StatusCode == HttpStatusCode.Forbidden && await Count("SELECT COUNT(*) AS n FROM mcp_links WHERE user_id=@u", ("u", newUid)) == 0, "Externes Lernprofil: POST /api/mcp/links → 403, kein MCP-Zugang entsteht");
            // 5c. OTP-Extension: eigene Endpunkt-Authentifizierung (Bearer der Extension), nie das Benutzer-Cookie
            async Task<HttpResponseMessage> OtpPost(string? bearer, string body, string? cookie = null)
            {
                var m = new HttpRequestMessage(HttpMethod.Post, "/extensions/otp-send") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (bearer is not null) m.Headers.TryAddWithoutValidation("Authorization", bearer);
                if (cookie is not null) m.Headers.Add("Cookie", cookie);
                return await http.SendAsync(m);
            }
            const string otpBody = "{\"type\":\"microsoft.graph.authenticationEvent.emailOtpSend\",\"data\":{\"otpContext\":{\"identifier\":\"neu@example.org\",\"oneTimeCode\":\"48151623\"}}}";
            codes.Add("48151623");
            var otpNo = await OtpPost(null, otpBody);
            var otpNoBody = await otpNo.Content.ReadAsStringAsync();
            check(otpNo.StatusCode == HttpStatusCode.Unauthorized && otpNoBody.Length == 0 && !otpNo.Headers.Contains("Set-Cookie"), "OTP-Endpunkt: ohne Authentifizierung → 401 ohne Inhalt [" + (int)otpNo.StatusCode + " body=" + otpNoBody + " loc=" + otpNo.Headers.Location + "]");
            check((await OtpPost(null, otpBody, session)).StatusCode == HttpStatusCode.Unauthorized, "OTP-Endpunkt: eine Benutzersitzung (Cookie) ersetzt die Endpunkt-Authentifizierung nicht");
            check((await OtpPost("Bearer abc.def.ghi", otpBody)).StatusCode == HttpStatusCode.Unauthorized, "OTP-Endpunkt: ungültiges Bearer-Token → 401");
            check((await OtpPost("Bearer abc.def.ghi", new string(' ', 20000))).StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.Unauthorized, "OTP-Endpunkt: übergroße Anfrage wird begrenzt");
            check((await http.GetAsync("/extensions/otp-send")).StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, "OTP-Endpunkt: nur POST (GET liefert keine Funktion)");

            // 6. Protokollierung
            string logText; lock (log) logText = log.ToString();
            var leaks = new List<string>();
            if (codes.FirstOrDefault(c => logText.Contains(c)) is { } leakedCode) leaks.Add("Authorization Code in: " + string.Join(" | ", logText.Split(new[] { '\n' }).Where(l => l.Contains(leakedCode)).Select(l => l.Length > 260 ? l[..260] : l)));
            if (logText.Contains(ClientSecret)) leaks.Add("Workforce-Secret");
            if (logText.Contains(ExtSecret)) leaks.Add("External-Secret");
            if (logText.Contains("code_verifier")) leaks.Add("Verifier");
            check(leaks.Count == 0, "OIDC-Ablauf: Authorization Codes, Client-Secret und Verifier erscheinen nicht im Anwendungslog" + (leaks.Count > 0 ? " (gefunden: " + string.Join(", ", leaks) + ")" : ""));
        }
        finally
        {
            try { if (!proc.HasExited) { proc.Kill(true); proc.WaitForExit(5000); } } catch { }
        }
    }
}
