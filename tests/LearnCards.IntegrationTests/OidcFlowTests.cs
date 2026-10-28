using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

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
        public string Issuer => Base + "/tenant/v2.0";
        private readonly HttpListener _l = new();
        private readonly RSA _key = RSA.Create(2048), _otherKey = RSA.Create(2048);
        private readonly Dictionary<string, Grant> _grants = new();
        public bool LastPkceOk; public int TokenCalls;
        private readonly CancellationTokenSource _cts = new();

        public MockIdp(int port)
        {
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
                if (g is null || g.Used || g.Expires < DateTime.UtcNow || f["client_id"] != ClientId || f["client_secret"] != ClientSecret
                    || f["grant_type"] != "authorization_code" || f["redirect_uri"] != g.RedirectUri)
                { status = 400; body = "{\"error\":\"invalid_grant\"}"; LastPkceOk = false; }
                else if (challenge != g.Challenge) { status = 400; body = "{\"error\":\"invalid_grant\",\"error_description\":\"PKCE\"}"; LastPkceOk = false; }
                else
                {
                    g.Used = true; LastPkceOk = true;
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var claims = new Dictionary<string, object>
                    {
                        ["iss"] = Issuer, ["aud"] = ClientId, ["sub"] = g.Sub, ["nonce"] = g.Nonce, ["iat"] = now, ["exp"] = now + 3600,
                        ["name"] = "Test Benutzer " + g.Sub, ["email"] = g.Sub + "@example.org", ["roles"] = new[] { "Admin.All" },
                    };
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
        var port = freePort();
        var url = $"http://127.0.0.1:{port}";
        var psi = new ProcessStartInfo("dotnet", $"\"{webDll}\"") { WorkingDirectory = tmp, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var kv in baseEnv) psi.Environment[kv.Key] = kv.Value;
        void Env(string k, string v) => psi.Environment[k] = v;
        var dir = Path.Combine(tmp, "oidc-flow"); Directory.CreateDirectory(dir);
        Env("ASPNETCORE_URLS", url); Env("ASPNETCORE_ENVIRONMENT", "Development");
        Env("AUTH_MODE", "oidc"); Env("OIDC_ISSUER", idp.Issuer); Env("OIDC_CLIENT_ID", ClientId); Env("OIDC_CLIENT_SECRET", ClientSecret);
        Env("DB_PROVIDER", "sqlite"); Env("SQLITE_PATH", Path.Combine(dir, "oidc.db"));
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

            // 5. Protokollierung
            string logText; lock (log) logText = log.ToString();
            check(!codes.Any(c => logText.Contains(c)) && !logText.Contains(ClientSecret) && !logText.Contains("code_verifier"),
                "OIDC-Ablauf: Authorization Codes, Client-Secret und Verifier erscheinen nicht im Anwendungslog");
        }
        finally
        {
            try { if (!proc.HasExited) { proc.Kill(true); proc.WaitForExit(5000); } } catch { }
        }
    }
}
