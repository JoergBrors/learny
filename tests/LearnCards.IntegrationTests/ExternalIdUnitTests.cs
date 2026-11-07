using LearnCards.Web.Auth;
using LearnCards.Web.Data;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;

/// <summary>
/// Entra External ID als zweiter Anbieter (ADR 0048): Konfiguration (Trennung der Anbieter, fail-closed), Einmaligkeit des state in der Datenbank
/// und der Anmeldepfad ohne Benutzeranlage. Der vollständige HTTP-Ablauf steht in <see cref="OidcFlowTests"/>.
/// </summary>
public static class ExternalIdUnitTests
{
    private const string WfIssuer = "https://login.microsoftonline.com/aaaaaaaa-1111-2222-3333-444444444444/v2.0";
    private const string ExtTenant = "bbbbbbbb-1111-2222-3333-444444444444";
    private const string Secret = "geheim-123";

    private static Func<string, string> Env(Dictionary<string, string> d) => k => d.TryGetValue(k, out var v) ? v : "";
    private static Dictionary<string, string> Good() => new()
    {
        ["EXTID_ENABLED"] = "1", ["EXTID_AUTHORITY"] = $"https://{ExtTenant}.ciamlogin.com/{ExtTenant}/v2.0", ["EXTID_TENANT_ID"] = ExtTenant,
        ["EXTID_CLIENT_ID"] = "ext-app", ["EXTID_CLIENT_SECRET"] = Secret,
    };

    private static string? Throws(Func<ExternalIdSettings> f)
    {
        try { f(); return null; } catch (InvalidOperationException e) { return e.Message; }
    }

    public static void Run(Action<bool, string> check)
    {
        var off = ExternalIdSettings.Load(Env(new()), WfIssuer, "wf-app", false);
        check(!off.Enabled, "External ID: ohne EXTID_ENABLED ist der zweite Anbieter aus");
        check(!ExternalIdSettings.Load(Env(new() { ["EXTID_AUTHORITY"] = "kaputt", ["EXTID_CLIENT_ID"] = "x" }), WfIssuer, "wf-app", false).Enabled,
            "External ID: ohne EXTID_ENABLED werden übrige EXTID_*-Werte nicht ausgewertet (kein Einfluss auf den Workforce-Start)");

        var ok = ExternalIdSettings.Load(Env(Good()), WfIssuer, "wf-app", false);
        check(ok.Enabled && ok.TenantId == ExtTenant && ok.Scope == "openid profile email", "External ID: vollständige Konfiguration wird geladen (Standard-Scope)");

        foreach (var missing in new[] { "EXTID_AUTHORITY", "EXTID_TENANT_ID", "EXTID_CLIENT_ID", "EXTID_CLIENT_SECRET" })
        {
            var d = Good(); d.Remove(missing);
            var msg = Throws(() => ExternalIdSettings.Load(Env(d), WfIssuer, "wf-app", false));
            check(msg is not null && !msg.Contains(Secret), $"External ID: aktiviert, aber {missing} fehlt → Startabbruch (fail-closed, ohne Geheimnis in der Meldung)");
        }
        var plain = Good(); plain["EXTID_CLIENT_ID"] = "YOUR_CLIENT_ID";
        check(Throws(() => ExternalIdSettings.Load(Env(plain), WfIssuer, "wf-app", false)) is not null, "External ID: Platzhalter als Client-ID → Startabbruch");

        var sameAuthority = Good(); sameAuthority["EXTID_AUTHORITY"] = WfIssuer;
        check(Throws(() => ExternalIdSettings.Load(Env(sameAuthority), WfIssuer, "wf-app", false)) is not null, "External ID: gleiche Authority wie der Workforce-Issuer → Startabbruch");
        var sameClient = Good(); sameClient["EXTID_CLIENT_ID"] = "wf-app";
        check(Throws(() => ExternalIdSettings.Load(Env(sameClient), WfIssuer, "wf-app", false)) is not null, "External ID: gleiche Client-ID wie der Workforce-Client → Startabbruch");
        var sameTenant = Good(); sameTenant["EXTID_TENANT_ID"] = "aaaaaaaa-1111-2222-3333-444444444444";
        check(Throws(() => ExternalIdSettings.Load(Env(sameTenant), WfIssuer, "wf-app", false)) is not null, "External ID: gleicher Tenant wie der Workforce-Tenant → Startabbruch (getrennte Tenants)");
        var badTenant = Good(); badTenant["EXTID_TENANT_ID"] = "kein-guid";
        check(Throws(() => ExternalIdSettings.Load(Env(badTenant), WfIssuer, "wf-app", false)) is not null, "External ID: Tenant-ID muss eine GUID sein");
        var http = Good(); http["EXTID_AUTHORITY"] = "http://idp.example.org/t/v2.0";
        check(Throws(() => ExternalIdSettings.Load(Env(http), WfIssuer, "wf-app", true)) is not null && Throws(() => ExternalIdSettings.Load(Env(http), WfIssuer, "wf-app", false)) is not null,
            "External ID: http nur auf Loopback und nur in Development");
        var loop = Good(); loop["EXTID_AUTHORITY"] = "http://127.0.0.1:9999/t/v2.0";
        check(Throws(() => ExternalIdSettings.Load(Env(loop), WfIssuer, "wf-app", true)) is null && Throws(() => ExternalIdSettings.Load(Env(loop), WfIssuer, "wf-app", false)) is not null,
            "External ID: http://127.0.0.1 in Development erlaubt, in Produktion nicht");
        var noOpenid = Good(); noOpenid["EXTID_SCOPE"] = "profile email";
        check(Throws(() => ExternalIdSettings.Load(Env(noOpenid), WfIssuer, "wf-app", false)) is not null, "External ID: Scope ohne openid → Startabbruch");

        var cfg = new AppConfig { OidcIssuer = WfIssuer, OidcClientId = "wf-app", OidcClientSecret = "wf-secret", ExternalId = ok };
        var wf = new OidcService(cfg, new NoHttp());
        var extSvc = new ExternalOidcService(cfg, new NoHttp());
        check(wf.ProviderName == "workforce" && extSvc.ProviderName == "external", "External ID: beide Anbieter sind getrennte Dienste mit eigenem Namen und eigener Konfiguration");
    }

    private sealed class NoHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => throw new InvalidOperationException("kein Netzwerk im Test"); }

    public static async Task RunAsync(Action<bool, string> check, string tmp)
    {
        var dir = Path.Combine(tmp, "extid-unit"); Directory.CreateDirectory(dir);
        await using var db = new SqliteDatabase(Path.Combine(dir, "t.db"));
        await db.InitAsync();

        // Einmaligkeit des state über die Datenbank
        var store = new OidcStateStore(db);
        var t0 = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        check(await store.TryConsumeAsync("state-A", t0) && !await store.TryConsumeAsync("state-A", t0.AddSeconds(5)), "State-Store: derselbe state ist nur einmal einlösbar");
        check(await store.TryConsumeAsync("state-B", t0), "State-Store: ein anderer state ist unabhängig einlösbar");
        // zweite „Instanz“ auf derselben Datenbank
        var other = new OidcStateStore(db);
        check(!await other.TryConsumeAsync("state-A", t0.AddMinutes(1)) && !await other.TryConsumeAsync("state-B", t0.AddMinutes(1)), "State-Store: eine zweite Instanz auf derselben Datenbank kann einen verbrauchten state nicht erneut einlösen");
        var rows = await db.QueryAsync("SELECT state_hash FROM oidc_consumed_states");
        check(rows.Count == 2 && rows.All(r => r["state_hash"]!.Length == 64 && !r["state_hash"]!.Contains("state-")), "State-Store: gespeichert wird nur der SHA-256-Hash, nie der state");
        check(await store.TryConsumeAsync("state-A", t0.AddHours(1)), "State-Store: abgelaufene Einträge werden bereinigt (nach Ablauf wäre der Wert nicht mehr gültig)");
        var parallel = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.TryConsumeAsync("state-P", t0.AddHours(2))));
        check(parallel.Count(x => x) == 1, "State-Store: bei parallelem Einlösen gewinnt genau ein Aufruf");

        // Anmeldepfad ohne Benutzeranlage
        var cfg = new AppConfig();
        var users = new UserService(db, cfg);
        check(await users.SignInKnownIdentityAsync("https://ext.example/v2.0", "unbekannt") is null && (await db.QueryAsync("SELECT COUNT(*) AS n FROM users"))[0]["n"] == "0",
            "SignInKnownIdentity: unbekannte Identität → kein Benutzer angelegt");
        var wfUser = await users.SignInAsync("https://wf.example/v2.0", "wf-sub", "Workforce Person", "person@example.org", new[] { "Admin.All" });
        var wfRoles = string.Join(",", wfUser.Roles);
        // externe Identität mit gleicher E-Mail und gleichem sub, aber anderem Anbieter
        check(await users.SignInKnownIdentityAsync("https://ext.example/v2.0", "wf-sub") is null && (await db.QueryAsync("SELECT COUNT(*) AS n FROM users"))[0]["n"] == "1",
            "SignInKnownIdentity: gleicher sub bei anderem Anbieter ist eine andere Identität (keine Verknüpfung)");
        var extId = Guid.NewGuid().ToString(); var now = DateTime.UtcNow;
        await db.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id,'Ext','ext@example.org','active',1,@n,@n)", new (string, object?)[] { ("id", extId), ("n", now.AddDays(-3)) });
        await db.ExecuteAsync("INSERT INTO user_identities (provider, subject, user_id, created_at) VALUES ('https://ext.example/v2.0','e-sub',@u,@n)", new (string, object?)[] { ("u", extId), ("n", now) });
        await db.ExecuteAsync("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@u,'user',NULL,@n)", new (string, object?)[] { ("u", extId), ("n", now) });
        var known = await users.SignInKnownIdentityAsync("https://ext.example/v2.0", "e-sub");
        var after = await users.GetAsync(extId);
        check(known?.Id == extId && after!.Email == "ext@example.org" && after.DisplayName == "Ext" && after.LastLoginAt > now.AddDays(-1) && after.Roles.SequenceEqual(new[] { "user" }),
            "SignInKnownIdentity: bekannte Identität → eigene interne ID, Login-Zeit fortgeschrieben, E-Mail/Name/Rollen unverändert");
        check((await users.GetAsync(wfUser.Id))!.Roles.SequenceEqual(wfUser.Roles) && string.Join(",", (await users.GetAsync(wfUser.Id))!.Roles) == wfRoles && wfUser.IsAdmin,
            "Bestandsschutz: Rollen und Zuordnung des Workforce-Benutzers bleiben unverändert");
        await db.ExecuteAsync("UPDATE users SET status='disabled' WHERE id=@u", new (string, object?)[] { ("u", extId) });
        check(await users.SignInKnownIdentityAsync("https://ext.example/v2.0", "e-sub") is { IsActive: false }, "SignInKnownIdentity: deaktivierter Benutzer wird als inaktiv gemeldet (keine Sitzung)");
        var wfAgain = await users.SignInAsync("https://wf.example/v2.0", "wf-sub", "Workforce Person", "person@example.org", new[] { "Admin.All" });
        check(wfAgain.Id == wfUser.Id && (await db.QueryAsync("SELECT COUNT(*) AS n FROM user_identities WHERE user_id=@u", new (string, object?)[] { ("u", wfUser.Id) }))[0]["n"] == "1",
            "Bestandsschutz: erneute Workforce-Anmeldung → gleiche interne Benutzer-ID, genau eine Identität");
    }
}
