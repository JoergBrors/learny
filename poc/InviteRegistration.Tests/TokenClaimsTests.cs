using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InviteRegistration;
using LearnCards.Web.Data;

// Token-Anreicherung (OnTokenIssuanceStart): Claims nur als Hinweis, nie ein Anmeldefehler, keine Personendaten.
static partial class ExtendedTests
{
    static async Task TokenClaimsTests(Action<string, bool, string?> check, string tmp, byte[] key)
    {
        Console.WriteLine("Token-Anreicherung (OnTokenIssuanceStart)");
        const string Auth = "https://t.ciamlogin.com/tid/v2.0";
        const string Iss = "https://tid.ciamlogin.com/tid/v2.0";
        const string Caller = "99045fe1-7639-4a75-9d4a-577b6ca3810f";
        const string PortalApp = "cb54157f-bcfb-4e78-9c81-b63ee7a0a03d";
        const string Oid = "aaaaaaaa-1111-2222-3333-444444444444";
        var tm = new MsgTime();
        using var rsa = RSA.Create(2048); var pub = rsa.ExportParameters(false);
        static string B64u(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        static string Jwt(object payload, RSA k)
        {
            var h = B64u(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "RS256", kid = "k1", typ = "JWT" })));
            var p = B64u(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
            return h + "." + p + "." + B64u(k.SignData(Encoding.ASCII.GetBytes(h + "." + p), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        long Ts(DateTime d) => new DateTimeOffset(d).ToUnixTimeSeconds();
        static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
        string Good() => "Bearer " + Jwt(new Dictionary<string, object> { ["iss"] = Iss, ["aud"] = "api-app", ["azp"] = Caller, ["tid"] = "tid", ["ver"] = "2.0", ["exp"] = Ts(tm.Now.AddHours(1)), ["nbf"] = Ts(tm.Now.AddMinutes(-1)) }, rsa);
        string Body(string? oid, string? mail, string app = PortalApp, string type = "microsoft.graph.authenticationEvent.tokenIssuanceStart") =>
            "{\"type\":\"" + type + "\",\"data\":{\"tenantId\":\"tid\",\"authenticationContext\":{\"correlationId\":\"c\",\"clientServicePrincipal\":{\"appId\":\"" + app + "\"},\"user\":{\"id\":" + (oid is null ? "null" : "\"" + oid + "\"") + ",\"mail\":" + (mail is null ? "null" : "\"" + mail + "\"") + ",\"displayName\":\"GEHEIMER NAME\"}}}}";

        var disc = new StubHandler
        {
            Respond = (r, _) => r.RequestUri!.ToString().EndsWith("/keys")
                ? Json(new { keys = new[] { new { kty = "RSA", kid = "k1", n = B64u(pub.Modulus!), e = B64u(pub.Exponent!) } } })
                : Json(new { issuer = Iss, jwks_uri = "https://t.ciamlogin.com/tid/discovery/v2.0/keys" }),
        };
        var env = await NewEnv(tmp, key);
        var db = env.Db;
        // Lernende registrieren (Objekt-ID wird festgehalten)
        var inv = await env.Inv.CreateAsync(env.Admin, "lerner@y.test", TargetProfiles.B2cLearner, new[] { env.A });
        var red = await env.Inv.RedeemAsync(inv.Token, new VerifiedIdentity(Iss, "sub-1", "lerner@y.test", true, null, null, Oid));
        var uid = red.UserId!;
        check("Entra-Objekt-ID wird bei der Registrierung festgehalten", await Count(db, "SELECT COUNT(*) AS n FROM user_entra_oids WHERE oid=@o AND user_id=@u", ("o", Oid), ("u", uid)) == 1, null);
        var other = await env.Inv.CreateAsync(env.Admin, "zweiter@y.test", TargetProfiles.B2bLearner, new[] { env.B });
        var red2 = await env.Inv.RedeemAsync(other.Token, new VerifiedIdentity(Iss, "sub-2", "zweiter@y.test", true, null, null, "bbbbbbbb-1111-2222-3333-444444444444"));
        await env.Inv.RecordObjectIdAsync(red2.UserId!, Oid);
        check("Eine vergebene Objekt-ID wird nie einem anderen Benutzer zugeordnet", await Count(db, "SELECT COUNT(*) AS n FROM user_entra_oids WHERE oid=@o AND user_id=@u", ("o", Oid), ("u", red2.UserId!)) == 0, null);

        var audits = new List<string>();
        TokenClaimsHandler NewHandler(int max = 20) => new(new ExtensionTokenValidator(new HttpClient(disc), new OtpExtensionOptions
        { Authority = Auth, Audiences = new[] { "api-app" }, ExpectedTenantId = "tid", Clock = () => tm.Now }),
            db, new ContentAccess(db), new TokenClaimsOptions { ExpectedClientAppId = PortalApp, MaxModules = max, Audit = (_, r) => { audits.Add(r); return Task.CompletedTask; } });
        var h = NewHandler();
        JsonElement ClaimsOf(OtpHttpResult r)
        {
            using var d = JsonDocument.Parse(r.Json!);
            return d.RootElement.GetProperty("data").GetProperty("actions")[0].GetProperty("claims").Clone();
        }

        var ok = await h.HandleAsync(Good(), Body(Oid, null), default);
        var c = ClaimsOf(ok);
        check("Gültiger Aufruf: 200 mit Profil, Status, Modulen und Berechtigungsversion", ok.Status == 200 && c.GetProperty("learny_profile").GetString() == "B2C_LEARNER" && c.GetProperty("learny_status").GetString() == "active"
            && c.GetProperty("learny_modules").GetArrayLength() == 1 && c.GetProperty("learny_modules_n").GetString() == "1" && c.GetProperty("learny_uid").GetString() == uid, ok.Json);
        check("Antwortformat entspricht dem Vertrag (provideClaimsForToken)", ok.Json!.Contains("microsoft.graph.onTokenIssuanceStartResponseData") && ok.Json.Contains("microsoft.graph.tokenIssuanceStart.provideClaimsForToken"), null);
        check("Keine Personendaten in den Claims (kein Name, keine Adresse, keine Rolle)", !ok.Json.Contains("GEHEIMER") && !ok.Json.Contains("lerner@y.test") && !ok.Json.Contains("admin") && !c.EnumerateObject().Any(p => !p.Name.StartsWith("learny_")), null);
        var v1 = c.GetProperty("learny_grants_v").GetString();
        check("Berechtigungsversion ist deterministisch und gleich der berechneten", v1 == TokenClaimsHandler.Version("B2C_LEARNER", "active", new[] { env.A }), v1);

        check("Nur unterstützte Claim-Typen (Text und Text-Liste, keine Zahl/Boolean/JSON)", c.EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.String || (p.Value.ValueKind == JsonValueKind.Array && p.Value.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String))), ok.Json);
        check("Gesamtgröße der Claims unter 3 KB", Encoding.UTF8.GetByteCount(c.GetRawText()) < 3072, null);

        // Änderung der Berechtigungen → neue Version
        await db.ExecuteAsync("INSERT INTO module_grants (user_id, module_id, granted_by, granted_at) VALUES (@u,@m,NULL,@n)", new (string, object?)[] { ("u", uid), ("m", env.B), ("n", DateTime.UtcNow) });
        var c2 = ClaimsOf(await h.HandleAsync(Good(), Body(Oid, null), default));
        check("Modul zugewiesen → neue Version und zwei Module", c2.GetProperty("learny_grants_v").GetString() != v1 && c2.GetProperty("learny_modules_n").GetString() == "2", null);
        await db.ExecuteAsync("DELETE FROM module_grants WHERE user_id=@u AND module_id=@m", new (string, object?)[] { ("u", uid), ("m", env.B) });
        check("Modul entzogen → Version wieder wie zuvor", ClaimsOf(await h.HandleAsync(Good(), Body(Oid, null), default)).GetProperty("learny_grants_v").GetString() == v1, null);

        // Zuordnung über die Adresse, wenn keine Objekt-ID bekannt ist
        var byMail = ClaimsOf(await h.HandleAsync(Good(), Body("cccccccc-1111-2222-3333-444444444444", "Lerner@Y.test"), default));
        check("Unbekannte Objekt-ID, aber bekannte Tenant-Adresse → Zuordnung über die Adresse", byMail.TryGetProperty("learny_uid", out var bu) && bu.GetString() == uid, null);

        // Unbekannt / fremde App / ungültig: nie ein Fehler für die Anmeldung
        var unknown = await h.HandleAsync(Good(), Body("dddddddd-1111-2222-3333-444444444444", "niemand@y.test"), default);
        check("Unbekannter Benutzer (z. B. erste Anmeldung) → 200 mit leeren Claims", unknown.Status == 200 && ClaimsOf(unknown).EnumerateObject().Count() == 0, null);
        var otherApp = await h.HandleAsync(Good(), Body(Oid, null, app: "99999999-9999-9999-9999-999999999999"), default);
        check("Fremde Client-App → 200 mit leeren Claims", otherApp.Status == 200 && ClaimsOf(otherApp).EnumerateObject().Count() == 0, null);
        check("Ohne/mit falschem Entra-Token → 401, nie Claims", (await h.HandleAsync(null, Body(Oid, null), default)).Status == 401 && (await h.HandleAsync("Bearer x.y.z", Body(Oid, null), default)).Status == 401
            && (await h.HandleAsync(Good(), Body(Oid, null), default)).Status == 200, null);
        check("Falscher Ereignistyp / kaputter Body → 400", (await h.HandleAsync(Good(), Body(Oid, null, type: "microsoft.graph.authenticationEvent.emailOtpSend"), default)).Status == 400 && (await h.HandleAsync(Good(), "{", default)).Status == 400, null);

        // Deaktiviert: Status sichtbar, keine Module
        await db.ExecuteAsync("UPDATE users SET status='disabled' WHERE id=@u", new (string, object?)[] { ("u", uid) });
        var dis = ClaimsOf(await h.HandleAsync(Good(), Body(Oid, null), default));
        check("Deaktivierter Benutzer: Status disabled, keine Module", dis.GetProperty("learny_status").GetString() == "disabled" && dis.GetProperty("learny_modules_n").GetString() == "0", null);
        await db.ExecuteAsync("UPDATE users SET status='active' WHERE id=@u", new (string, object?)[] { ("u", uid) });

        // Tokengröße: viele Module → nur die Anzahl
        var small = ClaimsOf(await NewHandler(max: 0).HandleAsync(Good(), Body(Oid, null), default));
        check("Über dem Limit: keine Modulliste, nur Anzahl", !small.TryGetProperty("learny_modules", out _) && small.GetProperty("learny_modules_n").GetString() == "1", null);

        // Übersicht aus den Claims (Cache): Parser, Einmaligkeit, Marke und Ungültigmachen
        static string Tok(object payload) => "h." + B64u(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))) + ".s";
        var single = LearnerClaimsStore.Parse(Tok(new Dictionary<string, object> { ["learny_uid"] = "u1", ["learny_grants_v"] = "abc", ["learny_modules"] = "m1" }));
        var many = LearnerClaimsStore.Parse(Tok(new Dictionary<string, object> { ["learny_uid"] = "u1", ["learny_modules"] = new[] { "m1", "m2" } }));
        var none = LearnerClaimsStore.Parse(Tok(new Dictionary<string, object> { ["learny_uid"] = "u1", ["learny_modules_n"] = "0" }));
        var listOmitted = LearnerClaimsStore.Parse(Tok(new Dictionary<string, object> { ["learny_uid"] = "u1", ["learny_modules_n"] = "25" }));
        check("Claims lesen: einzelnes Modul (Text) und Liste", single is { Modules: { Count: 1 } } && single.Modules![0] == "m1" && single.GrantsVersion == "abc" && many?.Modules?.Count == 2, null);
        check("Claims lesen: ausdrücklich keine Module = leere Liste; Liste weggelassen = unbekannt (Datenbank); ohne learny_uid = keine Claims",
            none?.Modules?.Count == 0 && listOmitted is { Modules: null } && LearnerClaimsStore.Parse(Tok(new Dictionary<string, object> { ["sub"] = "x" })) is null && LearnerClaimsStore.Parse("kaputt") is null, null);
        var store = new LearnerClaimsStore(() => tm.Now, TimeSpan.FromMinutes(10));
        store.Put("sub-1", Tok(new Dictionary<string, object> { ["learny_uid"] = uid, ["learny_modules"] = "m1" }));
        check("Claims der Anmeldung sind einmalig abholbar", store.Take("sub-1") is { } && store.Take("sub-1") is null, null);
        store.Put("sub-2", Tok(new Dictionary<string, object> { ["learny_uid"] = uid }));
        tm.Now = tm.Now.AddMinutes(11);
        check("Claims verfallen nach 10 Minuten", store.Take("sub-2") is null, null);

        var overview = new LearnerOverview(db, new ContentAccess(db));
        var mark = await overview.CurrentVersionAsync(uid);
        check("Aktuelle Marke = berechnete Marke", mark == TokenClaimsHandler.Version("B2C_LEARNER", "active", new[] { env.A }), mark);
        await db.ExecuteAsync("INSERT INTO module_grants (user_id, module_id, granted_by, granted_at) VALUES (@u,@m,NULL,@n)", new (string, object?)[] { ("u", uid), ("m", env.B), ("n", DateTime.UtcNow) });
        check("Ohne Ungültigmachen bleibt die gemerkte Marke (keine Datenbankabfrage)", await overview.CurrentVersionAsync(uid) == mark, null);
        overview.Invalidate(uid);
        var mark2 = await overview.CurrentVersionAsync(uid);
        check("Nach Ungültigmachen neue Marke: Claims der Anmeldung gelten nicht mehr", mark2 != mark && mark2 == TokenClaimsHandler.Version("B2C_LEARNER", "active", new[] { env.A, env.B }), mark2);
        check("Modulname kommt zwischengespeichert aus dem Katalog", (await overview.ModuleAsync(env.A)) is { Name: "A global" } && (await overview.ModuleAsync("unbekannt")) is null, null);

        check("Audit enthält nur Status und Ergebnis (keine IDs, Adressen, Claims)", audits.Count > 5 && audits.All(a => System.Text.RegularExpressions.Regex.IsMatch(a, @"^\d{3}:[a-z_]+$")), string.Join(",", audits.Take(3)));
    }
}
