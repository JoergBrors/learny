using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InviteRegistration;

// Tokenprüfung des echten OIDC-Flows (EntraOidcFlow) ohne Netzwerk: lokal signierte Tokens gegen simulierte Discovery/JWKS.
static partial class ExtendedTests
{
    static async Task OidcTests(Action<string, bool, string?> check)
    {
        Console.WriteLine("EntraOidcFlow (Tokenprüfung)");
        const string Auth = "https://t.ciamlogin.com/tid/v2.0";
        const string Iss = "https://tid.ciamlogin.com/tid/v2.0";
        using var rsa = RSA.Create(2048);
        using var other = RSA.Create(2048);
        var pub = rsa.ExportParameters(false);
        static string B64u(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        static string Jwt(object payload, RSA key, string alg = "RS256", string kid = "k1")
        {
            var h = B64u(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg, kid, typ = "JWT" })));
            var p = B64u(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
            return h + "." + p + "." + B64u(key.SignData(Encoding.ASCII.GetBytes(h + "." + p), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        var now = DateTime.UtcNow;
        long Ts(DateTime d) => new DateTimeOffset(d).ToUnixTimeSeconds();
        Dictionary<string, object> Claims() => new() { ["iss"] = Iss, ["aud"] = "client-1", ["sub"] = "sub-1", ["nonce"] = "n1", ["exp"] = Ts(now.AddHours(1)), ["email"] = "a@b.test", ["email_verified"] = true };
        static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
        string? tokenResponse = null;
        var h = new StubHandler();
        h.Respond = (r, body) =>
        {
            var u = r.RequestUri!.ToString();
            if (u.EndsWith("/.well-known/openid-configuration")) return Json(new { issuer = Iss, token_endpoint = "https://t.ciamlogin.com/tid/oauth2/v2.0/token", jwks_uri = "https://t.ciamlogin.com/tid/discovery/v2.0/keys" });
            if (u.EndsWith("/keys")) return Json(new { keys = new[] { new { kty = "RSA", kid = "k1", n = B64u(pub.Modulus!), e = B64u(pub.Exponent!) } } });
            if (u.EndsWith("/token")) return tokenResponse is null ? new HttpResponseMessage(HttpStatusCode.BadRequest) : Json(new { id_token = tokenResponse });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var observed = new List<string>();
        EntraOidcFlow Flow(Func<IReadOnlyDictionary<string, JsonElement>, bool>? fb = null) => new(new HttpClient(h), new EntraOidcOptions
        { Authority = Auth, ClientId = "client-1", ClientSecret = "s3cret", RedirectUri = "https://localhost:5001/auth/callback", EmailVerifiedFallback = fb, OnClaimsObserved = c => observed.AddRange(c.Keys) });

        var flow = Flow();
        var url = flow.BuildAuthorizeUrl("st", "n1", "chal");
        check("Authorize-URL: Code-Flow, form_post, PKCE S256, kein Secret", url.StartsWith("https://t.ciamlogin.com/tid/oauth2/v2.0/authorize?") && url.Contains("response_mode=form_post")
            && url.Contains("code_challenge_method=S256") && url.Contains("response_type=code") && !url.Contains("s3cret"), url);

        tokenResponse = Jwt(Claims(), rsa);
        var id = await flow.ExchangeAndValidateAsync("code", "verifier", "n1", default);
        check("Gültiges Token → Identität (iss, sub, E-Mail, bestätigt)", id is { Issuer: Iss, Subject: "sub-1", Email: "a@b.test", EmailVerified: true }, id?.ToString());
        var withRoles = Claims(); withRoles["roles"] = new[] { "Admin.All", "Users" };
        tokenResponse = Jwt(withRoles, rsa);
        var idr = await Flow().ExchangeAndValidateAsync("code", "verifier", "n1", default);
        check("Claim roles wird als Rollenliste übernommen (Admin.All, Users)", idr?.Roles is { Count: 2 } rr && rr.Contains("Admin.All") && rr.Contains("Users"), null);
        tokenResponse = Jwt(Claims(), rsa);
        check("Ohne roles-Claim ist die Rollenliste leer", (await Flow().ExchangeAndValidateAsync("code", "verifier", "n1", default))?.Roles is { Count: 0 }, null);
        check("Token-Austausch sendet PKCE-Verifier, Secret und redirect_uri an den Tokenendpunkt", h.Calls.Any(c => c.Url.EndsWith("/token") && c.Body.Contains("code_verifier=verifier") && c.Body.Contains("client_secret=s3cret") && c.Body.Contains("redirect_uri=")), null);
        check("Diagnose sieht nur Claim-Namen", observed.Contains("sub") && observed.Contains("email_verified"), null);

        async Task Rejects(string name, object payload, RSA? key = null, string alg = "RS256", string kid = "k1", string nonce = "n1")
        {
            tokenResponse = Jwt(payload, key ?? rsa, alg, kid);
            check("Abgelehnt: " + name, await Flow().ExchangeAndValidateAsync("code", "v", nonce, default) is null, null);
        }
        await Rejects("falsche nonce", Claims(), nonce: "andere");
        await Rejects("falsche Audience", new Dictionary<string, object>(Claims()) { ["aud"] = "anderer-client" });
        await Rejects("falscher Issuer", new Dictionary<string, object>(Claims()) { ["iss"] = "https://evil.test/tid/v2.0" });
        await Rejects("abgelaufen", new Dictionary<string, object>(Claims()) { ["exp"] = Ts(now.AddHours(-1)) });
        var noExp = Claims(); noExp.Remove("exp");
        await Rejects("ohne exp", noExp);
        await Rejects("nbf in der Zukunft", new Dictionary<string, object>(Claims()) { ["nbf"] = Ts(now.AddHours(1)) });
        await Rejects("falsche Signatur (anderer Schlüssel)", Claims(), key: other);
        await Rejects("unbekannte kid", Claims(), kid: "unbekannt");
        await Rejects("Algorithmus HS256", Claims(), alg: "HS256");
        await Rejects("mehrere Audiences", new Dictionary<string, object>(Claims()) { ["aud"] = new[] { "client-1", "x" } });
        var noSub = Claims(); noSub.Remove("sub");
        await Rejects("ohne sub", noSub);
        var tampered = Jwt(Claims(), rsa).Split('.');
        tampered[1] = B64u(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object>(Claims()) { ["sub"] = "admin" })));
        tokenResponse = string.Join('.', tampered);
        check("Abgelehnt: manipulierter Payload", await Flow().ExchangeAndValidateAsync("code", "v", "n1", default) is null, null);
        tokenResponse = null;
        check("Abgelehnt: Tokenendpunkt antwortet mit Fehler", await Flow().ExchangeAndValidateAsync("code", "v", "n1", default) is null, null);

        var noVerified = Claims(); noVerified.Remove("email_verified");
        tokenResponse = Jwt(noVerified, rsa);
        check("Ohne email_verified: standardmäßig nicht bestätigt (fail-closed)", (await Flow().ExchangeAndValidateAsync("c", "v", "n1", default))?.EmailVerified == false, null);
        check("Mit ausdrücklichem Fallback: bestätigt", (await Flow(_ => true).ExchangeAndValidateAsync("c", "v", "n1", default))?.EmailVerified == true, null);
        var viaOtp = Claims(); viaOtp.Remove("email_verified"); viaOtp["idp"] = "mail"; viaOtp["ver"] = "2.0"; viaOtp["tid"] = "tid";
        tokenResponse = Jwt(viaOtp, rsa);
        check("Richtlinie OtpIdpOnly: idp=mail ohne email_verified → bestätigt", (await Flow(EmailVerificationPolicies.OtpIdpOnly).ExchangeAndValidateAsync("c", "v", "n1", default))?.EmailVerified == true, null);
        var viaGoogle = Claims(); viaGoogle.Remove("email_verified"); viaGoogle["idp"] = "google.com";
        tokenResponse = Jwt(viaGoogle, rsa);
        check("Richtlinie OtpIdpOnly: anderer Anbieter → nicht bestätigt", (await Flow(EmailVerificationPolicies.OtpIdpOnly).ExchangeAndValidateAsync("c", "v", "n1", default))?.EmailVerified == false, null);
        tokenResponse = Jwt(noVerified, rsa);
        check("Richtlinie OtpIdpOnly: ohne idp → nicht bestätigt", (await Flow(EmailVerificationPolicies.OtpIdpOnly).ExchangeAndValidateAsync("c", "v", "n1", default))?.EmailVerified == false, null);
        var wrongVer = new Dictionary<string, object>(viaOtp) { ["ver"] = "1.0" };
        tokenResponse = Jwt(wrongVer, rsa);
        check("Richtlinie OtpIdpOnly: Tokenversion ≠ 2.0 → nicht bestätigt", (await Flow(EmailVerificationPolicies.OtpIdpOnly).ExchangeAndValidateAsync("c", "v", "n1", default))?.EmailVerified == false, null);
        var localAcc = Claims(); localAcc.Remove("email_verified"); localAcc["ver"] = "2.0"; localAcc["tid"] = "tid";
        tokenResponse = Jwt(localAcc, rsa);
        check("Richtlinie OtpOrLocalAccount: lokales Konto (kein idp, ver 2.0, email) → bestätigt", (await Flow(EmailVerificationPolicies.OtpOrLocalAccount).ExchangeAndValidateAsync("c", "v", "n1", default))?.EmailVerified == true, null);
        check("Richtlinie OtpIdpOnly bleibt für lokale Konten streng", (await Flow(EmailVerificationPolicies.OtpIdpOnly).ExchangeAndValidateAsync("c", "v", "n1", default))?.EmailVerified == false, null);
        tokenResponse = Jwt(viaGoogle, rsa);
        check("Richtlinie OtpOrLocalAccount: föderierter Anbieter (idp gesetzt) → nicht bestätigt", (await Flow(EmailVerificationPolicies.OtpOrLocalAccount).ExchangeAndValidateAsync("c", "v", "n1", default))?.EmailVerified == false, null);
        var localViaUpn = new Dictionary<string, object>(localAcc); localViaUpn.Remove("email"); localViaUpn["preferred_username"] = "lokal@example.org";
        tokenResponse = Jwt(localViaUpn, rsa);
        check("Richtlinie OtpOrLocalAccount: Adresse nur in preferred_username → bestätigt", (await Flow(EmailVerificationPolicies.OtpOrLocalAccount).ExchangeAndValidateAsync("c", "v", "n1", default))?.EmailVerified == true, null);
        var localNoEmail = new Dictionary<string, object>(localAcc); localNoEmail.Remove("email"); localNoEmail.Remove("preferred_username"); localNoEmail.Remove("emails");
        tokenResponse = Jwt(localNoEmail, rsa);
        check("Richtlinie OtpOrLocalAccount: lokales Konto ohne E-Mail → nicht bestätigt", (await Flow(EmailVerificationPolicies.OtpOrLocalAccount).ExchangeAndValidateAsync("c", "v", "n1", default))?.EmailVerified == false, null);
        var localOldVer = new Dictionary<string, object>(localAcc) { ["ver"] = "1.0" };
        tokenResponse = Jwt(localOldVer, rsa);
        check("Richtlinie OtpOrLocalAccount: Tokenversion ≠ 2.0 → nicht bestätigt", (await Flow(EmailVerificationPolicies.OtpOrLocalAccount).ExchangeAndValidateAsync("c", "v", "n1", default))?.EmailVerified == false, null);
        EntraOidcFlow FlowTid(string tid) => new(new HttpClient(h), new EntraOidcOptions
        { Authority = Auth, ClientId = "client-1", ClientSecret = "s3cret", RedirectUri = "https://localhost:5001/auth/callback", ExpectedTenantId = tid, EmailVerifiedFallback = EmailVerificationPolicies.OtpIdpOnly });
        tokenResponse = Jwt(viaOtp, rsa);
        check("Erwartete Tenant-ID passt (tid) → Identität bestätigt", (await FlowTid("tid").ExchangeAndValidateAsync("c", "v", "n1", default))?.EmailVerified == true, null);
        check("Abweichende Tenant-ID (tid) → Token abgelehnt", await FlowTid("anderer-tenant").ExchangeAndValidateAsync("c", "v", "n1", default) is null, null);
        static bool Throws(Action a) { try { a(); return false; } catch (InvalidOperationException) { return true; } }
        check("HTTP-Authority und HTTP-Redirect (nicht localhost) abgelehnt",
            Throws(() => _ = new EntraOidcFlow(new HttpClient(), new EntraOidcOptions { Authority = "http://x/v2.0", ClientId = "c", ClientSecret = "s", RedirectUri = "https://l/cb" }))
            && Throws(() => _ = new EntraOidcFlow(new HttpClient(), new EntraOidcOptions { Authority = Auth, ClientId = "c", ClientSecret = "s", RedirectUri = "http://evil.test/cb" })), null);
    }
}
