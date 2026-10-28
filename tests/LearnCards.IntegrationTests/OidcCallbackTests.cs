using System.Net;
using Microsoft.AspNetCore.Http;
using LearnCards.Web.Auth;

/// <summary>OIDC form_post (ADR 0036): Formular-Prüfung, Cookie-Optionen, Redirect-URI, Einmaligkeit des state, Authorize-URL und HTTP-Verhalten des Callbacks.</summary>
public static class OidcCallbackTests
{
    private static IFormCollection Form(params (string Key, string[] Values)[] fields) =>
        new FormCollection(fields.ToDictionary(f => f.Key, f => new Microsoft.Extensions.Primitives.StringValues(f.Values)));

    public static void Run(Action<bool, string> check)
    {
        // Authorize-URL: form_post statt query, PKCE/State/Nonce bleiben
        var st = OidcService.CreateState("/");
        var url = OidcService.BuildAuthorizeUrl("https://login.example.org/authorize", "cid", "openid profile", st, "https://learny.example.org/auth/callback");
        check(url.Contains("response_mode=form_post") && !url.Contains("response_mode=query"), "OIDC: Authorize-URL nutzt response_mode=form_post");
        check(url.Contains("response_type=code") && url.Contains("code_challenge_method=S256") && url.Contains("state=") && url.Contains("nonce="), "OIDC: Authorization Code Flow mit PKCE S256, state und nonce bleibt erhalten");
        check(!url.Contains("response_type=id_token") && !url.Contains("response_type=token"), "OIDC: kein Implicit Flow");

        // Formular
        var ok = OidcCallback.ParseForm(Form(("code", new[] { "abc" }), ("state", new[] { "s1" }), ("session_state", new[] { "x" })));
        check(ok.Form is { Code: "abc", State: "s1" } && ok.Problem is null, "OIDC-Formular: gültig (unbekannte Felder wie session_state werden ignoriert)");
        check(OidcCallback.ParseForm(Form(("state", new[] { "s1" }))).Form is null, "OIDC-Formular: fehlender code → abgewiesen");
        check(OidcCallback.ParseForm(Form(("code", new[] { "abc" }))).Form is null, "OIDC-Formular: fehlender state → abgewiesen");
        check(OidcCallback.ParseForm(Form(("code", new[] { "a", "b" }), ("state", new[] { "s1" }))).Form is null, "OIDC-Formular: doppelter code → abgewiesen");
        check(OidcCallback.ParseForm(Form(("code", new[] { "a" }), ("state", new[] { "s1", "s2" }))).Form is null, "OIDC-Formular: doppelter state → abgewiesen");
        check(OidcCallback.ParseForm(Form(("code", new[] { new string('a', OidcCallback.MaxCode + 1) }), ("state", new[] { "s1" }))).Form is null, "OIDC-Formular: überlanger code → abgewiesen");
        check(OidcCallback.ParseForm(Form(("code", new[] { "a" }), ("state", new[] { new string('s', OidcCallback.MaxState + 1) }))).Form is null, "OIDC-Formular: überlanger state → abgewiesen");
        var err = OidcCallback.ParseForm(Form(("error", new[] { "access_denied" }), ("error_description", new[] { "<script>x</script>" })));
        check(err.Form is { Error: "access_denied" }, "OIDC-Formular: Fehlerantwort des Providers wird erkannt");
        check(!OidcCallback.SafeErrorText("access_denied").Contains('<') && OidcCallback.SafeErrorText("<script>") == "Anmeldung abgelehnt", "OIDC: Fehlertext enthält nie freien Providertext");

        // Cookie
        var c = OidcCallback.StateCookieOptions();
        check(c.SameSite == SameSiteMode.None && c.Secure && c.HttpOnly && c.Path == "/auth/callback" && c.MaxAge <= TimeSpan.FromMinutes(15),
            "OIDC-Cookie: SameSite=None, Secure, HttpOnly, enger Pfad, kurze Lebensdauer");

        // Redirect-URI
        var host = new HostString("evil.example");
        check(OidcCallback.ResolveRedirectUri("https://learny.example.org/", "https", host, false) == "https://learny.example.org/auth/callback", "OIDC: PUBLIC_BASE_URL hat Vorrang vor dem Host-Header");
        check(OidcCallback.ResolveRedirectUri(null, "http", new HostString("learny.example.org"), false) is null, "OIDC: ohne HTTPS wird in Produktion kein Redirect-URI gebildet");
        check(OidcCallback.ResolveRedirectUri("http://learny.example.org", "https", host, false) is null, "OIDC: http-PUBLIC_BASE_URL wird in Produktion abgelehnt");
        check(OidcCallback.ResolveRedirectUri(null, "http", new HostString("localhost", 5000), true) == "http://localhost:5000/auth/callback", "OIDC: Entwicklung darf http://localhost nutzen");

        // Einmaligkeit
        var s = "state-" + Guid.NewGuid().ToString("N");
        check(OidcCallback.TryConsume(s) && !OidcCallback.TryConsume(s), "OIDC: state ist nur einmal verwendbar (Wiederholung abgewiesen)");
        check(OidcCallback.TryConsume(s + "-b"), "OIDC: anderer state bleibt gültig");
    }

    /// <summary>HTTP-Verhalten gegen den laufenden Server (Dev-Modus; ohne Provider erreichbar sind nur die Abweisungen).</summary>
    public static async Task RunHttpAsync(Action<bool, string> check, Func<HttpClient> newClient)
    {
        using var http = newClient();
        string Loc(HttpResponseMessage r) => r.Headers.Location?.OriginalString ?? "";

        var get = await http.GetAsync("/auth/callback?code=geheim&state=x");
        check(Loc(get).StartsWith("/login?error=") && !Loc(get).Contains("geheim"), "Callback: GET mit code in der URL wird nicht verarbeitet");

        FormUrlEncodedContent form(params (string, string)[] kv) => new(kv.Select(p => new KeyValuePair<string, string>(p.Item1, p.Item2)));

        var noCookie = await http.PostAsync("/auth/callback", form(("code", "c"), ("state", "s")));
        check(noCookie.StatusCode == HttpStatusCode.SeeOther && Loc(noCookie).StartsWith("/login?error=") && noCookie.Headers.CacheControl?.NoStore == true,
            "Callback: POST ohne Korrelationscookie → 303 nach /login, Cache-Control: no-store");

        var json = await http.PostAsync("/auth/callback", new StringContent("{\"code\":\"c\"}", System.Text.Encoding.UTF8, "application/json"));
        check(json.StatusCode == HttpStatusCode.SeeOther && Loc(json).StartsWith("/login?error="), "Callback: POST mit falschem Content-Type abgewiesen");

        var missing = await http.PostAsync("/auth/callback", form(("state", "s")));
        check(missing.StatusCode == HttpStatusCode.SeeOther && Loc(missing).StartsWith("/login?error="), "Callback: POST ohne code abgewiesen");

        var dup = await http.PostAsync("/auth/callback", new StringContent("code=a&code=b&state=s", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));
        check(dup.StatusCode == HttpStatusCode.SeeOther && Loc(dup).StartsWith("/login?error=") && !Loc(dup).Contains("code=a"), "Callback: doppelte Parameter abgewiesen");

        var badReq = new HttpRequestMessage(HttpMethod.Post, "/auth/callback") { Content = form(("code", "c"), ("state", "s")) };
        badReq.Headers.Add("Cookie", "lc.oidc.state=kein-gueltiger-wert");
        var bad = await http.SendAsync(badReq);
        check(bad.StatusCode == HttpStatusCode.SeeOther && Loc(bad).Contains("State-Cookie"), "Callback: manipuliertes Korrelationscookie abgewiesen");

        var denied = await http.PostAsync("/auth/callback", form(("error", "access_denied"), ("error_description", "<b>x</b>")));
        check(Loc(denied).Contains("access_denied") && !Loc(denied).Contains("%3Cb"), "Callback: Providerfehler wird ohne freien Providertext angezeigt");

    }
}
