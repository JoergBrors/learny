using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using InviteRegistration;
using InviteRegistration.Portal;
using LearnCards.Web.Data;

// Administrations- und Benutzerportal (ADR 0046): echtes Kestrel, Fake-Identitätsanbieter, Fake-Mailer, SQLite-Dateien (Neustart, mehrere Verbindungen).
static partial class ExtendedTests
{
    const string AdminIssuer = "https://login.microsoftonline.com/wf-tenant/v2.0";

    sealed class Portal
    {
        public PortalApp App = default!; public string Url = ""; public IDatabase Db = default!; public string DbPath = ""; public string KeyDir = "";
        public FakeOidc UserOidc = new(); public FakeOidc AdminOidc = new(); public FakeMailer Mailer = new(); public int OtpPort;
        public byte[] TokenKey = default!;
    }

    static int FreePort() { using var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return ((IPEndPoint)l.LocalEndpoint).Port; }

    static async Task<Portal> StartPortal(string tmp, byte[]? tokenKey = null, string? dbPath = null, string? keyDir = null, bool dev = true, string? devKey = null,
        int sensitivePerMinute = 1000, bool seed = true, OtpSendHandler? otp = null, string[]? adminRoles = null, string[]? adminSubjects = null, Func<IDatabase, string, MailSettingsService>? mail = null)
    {
        var p = new Portal { TokenKey = tokenKey ?? System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), DbPath = dbPath ?? System.IO.Path.Combine(tmp, "portal-" + Guid.NewGuid().ToString("N")[..8] + ".db"), KeyDir = keyDir ?? System.IO.Path.Combine(tmp, "keys-" + Guid.NewGuid().ToString("N")[..8]) };
        var port = FreePort(); p.Url = $"http://127.0.0.1:{port}"; p.OtpPort = otp is null ? FreePort() : FreePort();
        p.Db = new SqliteDatabase(p.DbPath);
        var bootstrap = new AdminBootstrap(p.Db, AdminIssuer, adminSubjects ?? new[] { "admin-sub" }, new[] { Issuer }, null, adminRoles);
        p.App = PortalHost.Build(new PortalOptions
        {
            Url = p.Url, PublicBaseUrl = p.Url, Db = p.Db, KeyDir = p.KeyDir, TokenKey = p.TokenKey, UserOidc = p.UserOidc, Mailer = p.Mailer,
            AdminOidc = p.AdminOidc, AdminBootstrap = bootstrap, Development = dev, DevAdminKey = devKey, SensitivePerMinute = sensitivePerMinute, SeedDemoData = seed,
            OtpHandler = otp, OtpUrl = $"http://127.0.0.1:{p.OtpPort}", MailSettings = mail?.Invoke(p.Db, p.KeyDir),
        });
        await p.App.StartAsync();
        return p;
    }

    static string FormToken(string html)
    {
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"") is { Success: true } a ? a : Regex.Match(html, "value=\"([^\"]+)\"[^>]*name=\"__RequestVerificationToken\"");
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : "";
    }

    static async Task<(string Token, string Html)> PageToken(Browser b, string url)
    {
        var res = await b.Get(url); var html = await res.Content.ReadAsStringAsync();
        return (FormToken(html), html);
    }

    static async Task<Browser> AdminLogin(Portal p, string code = "acode")
    {
        var b = NewBrowser(p.Url);
        p.AdminOidc.Codes[code] = new VerifiedIdentity(AdminIssuer, "admin-sub", "admin@wf.test", true);
        var start = await b.Get("/admin/login");
        var state = Query(start.Headers.Location!, "state");
        await b.Post("/admin/callback", ("code", code), ("state", state));
        return b;
    }

    static async Task<string> ModuleId(IDatabase db, string namePart) => (await db.QueryAsync("SELECT id FROM poc_modules WHERE name LIKE @n", new (string, object?)[] { ("n", "%" + namePart + "%") }))[0]["id"]!;

    static async Task<(string Id, string Location)> CreateInvitation(Portal p, Browser admin, string email, string profile, string[] modules, string display = "", int hours = 72)
    {
        var (tok, _) = await PageToken(admin, "/admin/invitations/new");
        var form = new List<(string, string)> { ("__RequestVerificationToken", tok), ("email", email), ("profile", profile), ("validityHours", hours.ToString()), ("displayName", display), ("description", ""), ("note", "") };
        form.AddRange(modules.Select(m => ("moduleIds", m)));
        var res = await admin.Post("/admin/actions/invitations", form.ToArray());
        var loc = res.Headers.Location?.ToString() ?? "";
        var id = Regex.Match(loc, "/admin/invitations/([0-9a-f-]{36})") is { Success: true } mm ? mm.Groups[1].Value : "";
        return (id, loc);
    }

    static async Task<string> AdminAction(Browser admin, string pageUrl, string action, params (string, string)[] extra)
    {
        var (tok, _) = await PageToken(admin, pageUrl);
        var res = await admin.Post(action, new[] { ("__RequestVerificationToken", tok) }.Concat(extra).ToArray());
        return res.Headers.Location?.ToString() ?? ((int)res.StatusCode).ToString();
    }

    static async Task<Browser> Register(Portal p, string link, string email, string sub, string code)
    {
        var b = NewBrowser(p.Url);
        await b.Get(new Uri(link).AbsolutePath);
        var start = await b.Get("/invite-start");
        var state = Query(start.Headers.Location!, "state");
        p.UserOidc.Codes[code] = new VerifiedIdentity(Issuer, sub, email, true);
        await b.Post("/auth/callback", ("code", code), ("state", state));
        return b;
    }

    static async Task PortalTests(Action<string, bool, string?> check, string tmp, byte[] key)
    {
        Console.WriteLine("Portal (Administration, Registrierung, Benutzerverwaltung)");
        var p = await StartPortal(tmp, seed: true, devKey: "dev-key-123");
        var modA = await ModuleId(p.Db, "Modul A"); var modB = await ModuleId(p.Db, "Modul B"); var modC = await ModuleId(p.Db, "Modul C");

        // ── Zugang: ohne Anmeldung nichts, nur ausdrücklich zugelassene Workforce-Identität ──
        var anon = NewBrowser(p.Url);
        var r0 = await anon.Get("/admin");
        check("Ohne Anmeldung: /admin leitet zur Administratoranmeldung (kein Zugriff)", (int)r0.StatusCode is 302 or 401 or 403 && (r0.Headers.Location?.ToString().Contains("/admin/signin") ?? true), r0.StatusCode + " " + r0.Headers.Location);
        foreach (var path in new[] { "/admin/invitations", "/admin/users", "/admin/events", "/admin/assignments", "/admin/invitations/new" })
            check($"Ohne Anmeldung gesperrt: {path}", (int)(await anon.Get(path)).StatusCode is 302 or 401 or 403, null);
        check("Öffentliche Administrator-Anmeldeseite erreichbar und ohne Geheimnisse", (await anon.Get("/admin/signin")).StatusCode == HttpStatusCode.OK, null);

        var admin = await AdminLogin(p);
        var home = await admin.Get("/admin"); var homeHtml = await home.Content.ReadAsStringAsync();
        check("Administrator (Workforce-OIDC, Subject auf der Bootstrap-Liste) sieht die Übersicht", home.StatusCode == HttpStatusCode.OK && homeHtml.Contains("Übersicht") && homeHtml.Contains("Einladungen gesamt"), home.StatusCode.ToString());
        check("Rolle admin steht nur für diese eine Identität in user_roles", await Count(p.Db, "SELECT COUNT(*) AS n FROM user_roles WHERE role='admin'") == 1, null);

        var stranger = NewBrowser(p.Url); p.AdminOidc.Codes["x1"] = new VerifiedIdentity(AdminIssuer, "other-sub", "admin@wf.test", true);
        var st = await stranger.Get("/admin/login"); var sr = await stranger.Post("/admin/callback", ("code", "x1"), ("state", Query(st.Headers.Location!, "state")));
        check("Gleiche E-Mail, anderes Subject → keine Administration (nicht allein per E-Mail)", sr.Headers.Location!.ToString() == "/admin/denied" && (int)(await stranger.Get("/admin")).StatusCode is 302 or 401 or 403, null);
        var wrongIssuer = NewBrowser(p.Url); p.AdminOidc.Codes["x2"] = new VerifiedIdentity(Issuer, "admin-sub", "admin@wf.test", true);
        var wi = await wrongIssuer.Get("/admin/login"); var wr = await wrongIssuer.Post("/admin/callback", ("code", "x2"), ("state", Query(wi.Headers.Location!, "state")));
        check("Richtiges Subject, aber External-ID-Issuer → abgelehnt (getrennte Issuer)", wr.Headers.Location!.ToString() == "/admin/denied", null);
        check("Abgewiesene Administratoranmeldungen werden protokolliert", await Count(p.Db, "SELECT COUNT(*) AS n FROM invitation_events WHERE event_type='admin.login_rejected'") >= 2, null);
        var devBad = NewBrowser(p.Url); var (dt, _) = await PageToken(devBad, "/admin/signin");
        check("Lokaler Entwicklungszugang: falscher Schlüssel → abgelehnt", (await devBad.Post("/admin/dev-login", ("__RequestVerificationToken", dt), ("key", "falsch"))).Headers.Location!.ToString() == "/admin/denied", null);
        var devOk = NewBrowser(p.Url); var (dt2, _) = await PageToken(devOk, "/admin/signin");
        await devOk.Post("/admin/dev-login", ("__RequestVerificationToken", dt2), ("key", "dev-key-123"));
        check("Lokaler Entwicklungszugang: richtiger Schlüssel (Development, Loopback) → Administration", (await devOk.Get("/admin")).StatusCode == HttpStatusCode.OK, null);
        check("Dev-Login ohne CSRF-Token → 400", (await NewBrowser(p.Url).Post("/admin/dev-login", ("key", "dev-key-123"))).StatusCode == HttpStatusCode.BadRequest, null);
        static bool Throws(Action a) { try { a(); return false; } catch (InvalidOperationException) { return true; } }
        var d0 = new SqliteDatabase(System.IO.Path.Combine(tmp, "x.db"));
        PortalOptions Opt(bool dev, string url, string? devKey, IOidcFlow? admin) => new() { Url = url, PublicBaseUrl = url, Db = d0, KeyDir = System.IO.Path.Combine(tmp, "kx"), TokenKey = key, UserOidc = p.UserOidc, Mailer = p.Mailer, Development = dev, DevAdminKey = devKey, AdminOidc = admin };
        check("Außerhalb von Development: kein Dev-Login, HTTP und fehlendes Admin-OIDC verhindern den Start (ADR 0026)",
            Throws(() => PortalHost.Build(Opt(false, "https://x.test", "k", p.AdminOidc))) && Throws(() => PortalHost.Build(Opt(false, "http://x.test", null, p.AdminOidc))) && Throws(() => PortalHost.Build(Opt(false, "https://x.test", null, null))), null);
        check("Administrator-Issuer darf nicht der External-ID-Issuer sein", Throws(() => _ = new AdminBootstrap(p.Db, Issuer, new[] { "a" }, new[] { Issuer })), null);

        // ── Einladung erstellen, Versand, Verwaltung ──
        var (tokNew, formHtml) = await PageToken(admin, "/admin/invitations/new");
        check("Formular enthält CSRF-Token, Profile und Module, keine Rollenauswahl", tokNew.Length > 20 && formHtml.Contains("B2C_LEARNER") && formHtml.Contains("B2B_LEARNER") && formHtml.Contains("Modul A")
            && !formHtml.Contains("Auditor-Rolle auswählen") && !Regex.IsMatch(formHtml, "name=\"role\"", RegexOptions.IgnoreCase), null);
        var noCsrf = await admin.Post("/admin/actions/invitations", ("email", "x@y.test"), ("profile", "B2C_LEARNER"));
        check("Erstellen ohne CSRF-Token → 400, nichts angelegt", noCsrf.StatusCode == HttpStatusCode.BadRequest && await Count(p.Db, "SELECT COUNT(*) AS n FROM invitations") == 0, noCsrf.StatusCode.ToString());
        var (id1, loc1) = await CreateInvitation(p, admin, "Neu.Lernende@Example.org", "B2C_LEARNER", new[] { modA }, "<b>Anna</b>");
        check("Einladung erstellt (B2C, Modul A), Weiterleitung zur Detailseite", id1.Length == 36 && loc1.Contains("m=created"), loc1);
        var det = await (await admin.Get($"/admin/invitations/{id1}")).Content.ReadAsStringAsync();
        check("Detailseite: Profil, Status offen/nicht versendet, Modul A, kein Token/Link", det.Contains("B2C-Lernende") && det.Contains("offen") && det.Contains("nicht versendet") && det.Contains("Modul A") && !det.Contains("/invite/") && !det.Contains("Bearer"), null);
        check("Anzeigename wird kodiert ausgegeben (kein HTML aus Eingaben)", det.Contains("&lt;b&gt;Anna&lt;/b&gt;") && !det.Contains("<b>Anna</b>"), null);
        check("Versand ist nach dem Erstellen nicht ausgelöst", p.Mailer.Sent.IsEmpty, null);

        var bad = await CreateInvitation(p, admin, "kein-email", "B2C_LEARNER", Array.Empty<string>());
        var adm = await CreateInvitation(p, admin, "a@y.test", "ADMIN", Array.Empty<string>());
        var unk = await CreateInvitation(p, admin, "b@y.test", "B2C_LEARNER", new[] { Guid.NewGuid().ToString() });
        var longV = await CreateInvitation(p, admin, "c@y.test", "B2C_LEARNER", Array.Empty<string>(), hours: 9999);
        check("Ungültige Adresse, Profil ADMIN, unbekanntes Modul und Gültigkeit 9999 h werden abgewiesen", new[] { bad, adm, unk, longV }.All(x => x.Location.Contains("/admin/invitations/new") && x.Location.Contains("m=invalid")) && await Count(p.Db, "SELECT COUNT(*) AS n FROM invitations") == 1, null);

        var link0 = p.Mailer.Sent.Count;
        var r1 = await AdminAction(admin, $"/admin/invitations/{id1}", $"/admin/actions/invitations/{id1}/send");
        check("Senden ohne Bestätigung → keine Mail", r1.Contains("m=not_confirmed") && p.Mailer.Sent.Count == link0, r1);
        p.Mailer.Results.Enqueue(new(MailOutcome.Rejected, "permission_denied"));
        var r2 = await AdminAction(admin, $"/admin/invitations/{id1}", $"/admin/actions/invitations/{id1}/send", ("confirm", "yes"));
        check("Versandfehler: Meldung, Einladung bleibt offen, Versandstatus fehlgeschlagen", r2.Contains("m=send_failed") && await InvStatus(p.Db, id1) == "pending" && await PS(p).Delivery.GetStatusAsync(id1) == "failed", r2);
        var home2 = await (await admin.Get("/admin")).Content.ReadAsStringAsync();
        check("Übersicht zählt den fehlgeschlagenen Versand", Regex.IsMatch(home2, "<b>1</b><span>Fehlgeschlagene Versandversuche"), null);
        var r3 = await AdminAction(admin, $"/admin/invitations/{id1}", $"/admin/actions/invitations/{id1}/send", ("confirm", "yes"));
        check("Erneuter Versand mit demselben Link (keine zweite aktive Einladung)", r3.Contains("m=sent") && p.Mailer.Sent.Count == link0 + 2 && await Count(p.Db, "SELECT COUNT(*) AS n FROM invitations") == 1, r3);
        var link1 = p.Mailer.Sent.Last().Link;
        var r4 = await AdminAction(admin, $"/admin/invitations/{id1}", $"/admin/actions/invitations/{id1}/send", ("confirm", "yes"));
        check("Nach erfolgreichem Versand ist der Klartext verworfen → Neuausstellung nötig", r4.Contains("m=link_unavailable"), r4);
        var r5 = await AdminAction(admin, $"/admin/invitations/{id1}", $"/admin/actions/invitations/{id1}/rotate");
        check("Neuausstellung ohne Bestätigung abgewiesen", r5.Contains("m=not_confirmed"), r5);
        var r6 = await AdminAction(admin, $"/admin/invitations/{id1}", $"/admin/actions/invitations/{id1}/rotate", ("confirm", "yes"));
        await AdminAction(admin, $"/admin/invitations/{id1}", $"/admin/actions/invitations/{id1}/send", ("confirm", "yes"));
        var link2 = p.Mailer.Sent.Last().Link;
        check("Neuausstellung: neuer Link gesendet, alter Link sofort ungültig", r6.Contains("m=rotated") && link2 != link1 && (await NewBrowser(p.Url).Get(new Uri(link1).AbsolutePath)).StatusCode == HttpStatusCode.BadRequest, null);
        var after = await (await admin.Get($"/admin/invitations/{id1}")).Content.ReadAsStringAsync();
        check("Versandstatus „angenommen“ getrennt vom Einladungsstatus „offen“", after.Contains("angenommen") && after.Contains("offen"), null);

        // ── Registrierung über die (simulierte) Entra-External-ID-Anmeldung ──
        var tl0 = await PS(p).Events.ListAsync(id1);
        var open = NewBrowser(p.Url);
        var o1 = await open.Get(new Uri(link2).AbsolutePath);
        check("Linkaufruf: Weiterleitung auf token-lose Adresse, nichts verbraucht", (int)o1.StatusCode is 302 && o1.Headers.Location!.ToString() == "/invite-start" && await InvStatus(p.Db, id1) == "pending", null);
        var st2 = await open.Get("/invite-start");
        check("Weiterleitung zu Entra mit state, ohne Token in der URL", st2.Headers.Location!.Host == "idp.test" && !st2.Headers.Location.ToString().Contains(new Uri(link2).Segments.Last()), null);
        var stateU = Query(st2.Headers.Location!, "state");
        var wrongMail = new VerifiedIdentity(Issuer, "sub-new", "andere@example.org", true);
        p.UserOidc.Codes["u0"] = wrongMail;
        var rw = await open.Post("/auth/callback", ("code", "u0"), ("state", stateU));
        check("Falsche E-Mail → abgewiesen, Einladung bleibt offen, kein Benutzer", rw.Headers.Location!.ToString() == "/invite-failed" && await InvStatus(p.Db, id1) == "pending" && await NonAdminUsers(p.Db) == 0, null);
        var user = await Register(p, link2, "neu.lernende@example.org", "sub-neu", "u1");
        var dash = await user.Get("/dashboard"); var dashHtml = await dash.Content.ReadAsStringAsync();
        check("Nach Registrierung: Dashboard mit Willkommen, Profil und nur Modul A", dash.StatusCode == HttpStatusCode.OK && dashHtml.Contains("Willkommen bei Learny") && dashHtml.Contains("B2C-Lernende") && dashHtml.Contains("Modul A") && !dashHtml.Contains("Modul B") && !dashHtml.Contains("Modul C"), dash.StatusCode.ToString());
        check("Direkter Zugriff: Modul A erlaubt, Modul B und C verweigert (403)", (await user.Get("/modules/" + modA)).StatusCode == HttpStatusCode.OK && (await user.Get("/modules/" + modB)).StatusCode == HttpStatusCode.Forbidden && (await user.Get("/modules/" + modC)).StatusCode == HttpStatusCode.Forbidden, null);
        var inv2 = await (await p.Db.QueryAsync("SELECT status, accepted_user_id FROM invitations WHERE invitation_id=@i", new (string, object?)[] { ("i", id1) })).ToAsyncSingle();
        var uid = inv2["accepted_user_id"]!;
        check("Einladung eingelöst, Profil B2C aus dem Einladungsdatensatz, keine Rolle in user_roles", inv2["status"] == "accepted" && await Count(p.Db, "SELECT COUNT(*) AS n FROM user_profiles WHERE user_id=@u AND profile='B2C_LEARNER'", ("u", uid)) == 1
            && await Count(p.Db, "SELECT COUNT(*) AS n FROM user_roles WHERE user_id=@u", ("u", uid)) == 0, null);
        var tl = await (await admin.Get($"/admin/invitations/{id1}")).Content.ReadAsStringAsync();
        var types = (await PS(p).Events.ListAsync(id1)).OrderBy(e => e.At).Select(e => e.Type).ToList();
        string[] expected = { EventTypes.Created, EventTypes.SendRequested, EventTypes.Submitted, EventTypes.Opened, EventTypes.RegistrationStarted, EventTypes.AuthenticationCompleted, EventTypes.Accepted, EventTypes.UserCreated, EventTypes.ProfileAssigned, EventTypes.ContentAssigned };
        check("Timeline enthält alle beobachteten Ereignisse in plausibler Reihenfolge", expected.All(t => types.Contains(t)) && types.IndexOf(EventTypes.Created) < types.IndexOf(EventTypes.Submitted) && types.IndexOf(EventTypes.Opened) < types.IndexOf(EventTypes.Accepted)
            && types.IndexOf(EventTypes.AuthenticationCompleted) < types.IndexOf(EventTypes.Accepted), string.Join(",", types));
        check("Timeline-Seite: deutsche Beschriftungen, Hinweis auf beobachtete Serveraufrufe, kein Link/Token/Code", tl.Contains("Einladung eingelöst") && tl.Contains("Serveraufruf") && !tl.Contains("/invite/") && !tl.Contains(new Uri(link2).Segments.Last()), null);
        check("Ereignisprotokoll in der Datenbank enthält weder Linktoken noch Codes", await Count(p.Db, "SELECT COUNT(*) AS n FROM invitation_events WHERE category LIKE @t OR event_type LIKE @t", ("t", "%" + new Uri(link2).Segments.Last() + "%")) == 0
            && await Count(p.Db, "SELECT COUNT(*) AS n FROM invitation_events WHERE category IN ('u1','u0')") == 0, null);
        check("Derselbe Link danach ungültig (HTML-Seite mit der vorgesehenen Meldung)", await (await NewBrowser(p.Url).Get(new Uri(link2).AbsolutePath)).Content.ReadAsStringAsync() is var inval && inval.Contains("Diese Einladung ist ungültig, abgelaufen oder wurde bereits verwendet."), null);
        check("Detailseite zeigt jetzt „eingelöst“ und den Link zum Benutzer", (await (await admin.Get($"/admin/invitations/{id1}")).Content.ReadAsStringAsync()) is var d2 && d2.Contains("eingelöst") && d2.Contains("/admin/users/" + uid), null);

        // ── Bestehende Identität, Anmeldung, Fehlerfälle ──
        var (id2, _) = await CreateInvitation(p, admin, "zweite@example.org", "B2B_LEARNER", new[] { modA });
        await AdminAction(admin, $"/admin/invitations/{id2}", $"/admin/actions/invitations/{id2}/send", ("confirm", "yes"));
        var link3 = p.Mailer.Sent.Last().Link;
        var b3 = NewBrowser(p.Url); await b3.Get(new Uri(link3).AbsolutePath); var s3 = await b3.Get("/invite-start");
        p.UserOidc.Codes["u2"] = new VerifiedIdentity(Issuer, "sub-neu", "zweite@example.org", true);   // bereits registrierte Identität
        var r7 = await b3.Post("/auth/callback", ("code", "u2"), ("state", Query(s3.Headers.Location!, "state")));
        check("Bestehende Identität kann keine Neuregistrierungseinladung einlösen", r7.Headers.Location!.ToString() == "/invite-failed" && await InvStatus(p.Db, id2) == "pending" && await NonAdminUsers(p.Db) == 1, null);
        check("Abweisung ist mit Kategorie protokolliert (ohne Details nach außen)", await Count(p.Db, "SELECT COUNT(*) AS n FROM invitation_events WHERE invitation_id=@i AND event_type='registration.rejected' AND category='IdentityAlreadyRegistered'", ("i", id2)) == 1, null);
        var b4 = NewBrowser(p.Url); await b4.Get(new Uri(link3).AbsolutePath); var s4 = await b4.Get("/invite-start");
        p.UserOidc.Codes["u3"] = new VerifiedIdentity(Issuer, "sub-unv", "zweite@example.org", false);
        var r8 = await b4.Post("/auth/callback", ("code", "u3"), ("state", Query(s4.Headers.Location!, "state")));
        check("Nicht bestätigte E-Mail (fehlende frische OTP-Verifikation) → abgewiesen", r8.Headers.Location!.ToString() == "/invite-failed" && await InvStatus(p.Db, id2) == "pending", null);

        var loginB = NewBrowser(p.Url); var l1 = await loginB.Get("/login?returnUrl=https://evil.example/");
        p.UserOidc.Codes["u4"] = new VerifiedIdentity(Issuer, "sub-neu", "neu.lernende@example.org", true);
        var rlogin = await loginB.Post("/auth/callback", ("code", "u4"), ("state", Query(l1.Headers.Location!, "state")));
        check("Anmeldung eines registrierten Benutzers (/login) → Dashboard; returnUrl wird ignoriert (kein offener Redirect)", l1.Headers.Location!.Host == "idp.test" && rlogin.Headers.Location!.ToString() == "/dashboard", null);
        var loginU = NewBrowser(p.Url); var l2 = await loginU.Get("/login");
        p.UserOidc.Codes["u5"] = new VerifiedIdentity(Issuer, "unbekannt", "fremd@example.org", true);
        var rl2 = await loginU.Post("/auth/callback", ("code", "u5"), ("state", Query(l2.Headers.Location!, "state")));
        check("Unbekannte Identität ohne Einladung → keine Anmeldung, kein Benutzer", rl2.Headers.Location!.ToString() == "/invite-failed" && await NonAdminUsers(p.Db) == 1 && (int)(await loginU.Get("/dashboard")).StatusCode is 302 or 401 or 403, null);

        // ── Fremde Daten, Rechte, IDOR ──
        check("Lernende können keine Administrationsseiten abrufen", (int)(await user.Get("/admin")).StatusCode is 302 or 401 or 403 && (int)(await user.Get($"/admin/invitations/{id1}")).StatusCode is 302 or 401 or 403 && (int)(await user.Get("/admin/events")).StatusCode is 302 or 401 or 403, null);
        var (tkU, _) = await PageToken(user, "/profile");
        var forged = await user.Post("/admin/actions/invitations", ("__RequestVerificationToken", tkU), ("email", "x@y.test"), ("profile", "B2C_LEARNER"));
        check("Lernende können keine Einladungen erstellen (auch nicht mit gültigem CSRF-Token)", (int)forged.StatusCode is 302 or 401 or 403 && await Count(p.Db, "SELECT COUNT(*) AS n FROM invitations") == 2, forged.StatusCode.ToString());
        check("Kein Endpunkt für Profilwechsel oder Rollenvergabe", (int)(await admin.Post($"/admin/actions/users/{uid}/profile", ("profile", "B2B_LEARNER"))).StatusCode is 404 or 405 or 400 && (int)(await admin.Post($"/admin/actions/users/{uid}/role", ("role", "admin"))).StatusCode is 404 or 405 or 400, null);
        var swallowed = false; try { await PS(p).Admin.ChangeProfileAsync("x", uid, "B2B_LEARNER"); } catch (NotSupportedException) { swallowed = true; }
        check("Profilwechsel B2C ↔ B2B ist im Dienst ausdrücklich nicht vorgesehen", swallowed, null);
        check("Nur Administratoren haben die Rolle admin; Eingeladene nie", await Count(p.Db, "SELECT COUNT(*) AS n FROM user_roles WHERE role<>'admin'") == 0 && await Count(p.Db, "SELECT COUNT(*) AS n FROM user_roles r JOIN user_profiles p ON p.user_id=r.user_id") == 0, null);

        // ── Benutzerverwaltung und Lernzuweisungen ──
        var usersHtml = await (await admin.Get("/admin/users")).Content.ReadAsStringAsync();
        check("Benutzerliste: E-Mail, Profil, Status, Modul A", usersHtml.Contains("neu.lernende@example.org") && usersHtml.Contains("B2C-Lernende") && usersHtml.Contains("aktiv") && usersHtml.Contains("Modul A"), null);
        var asg = await AdminAction(admin, "/admin/users/" + uid, $"/admin/actions/users/{uid}/assign", ("moduleId", modC));
        check("Admin weist Modul C nachträglich zu → Benutzer sieht C sofort, direkter Zugriff erlaubt", asg.Contains("m=assigned") && (await (await user.Get("/dashboard")).Content.ReadAsStringAsync()).Contains("Modul C") && (await user.Get("/modules/" + modC)).StatusCode == HttpStatusCode.OK, asg);
        var asgB = await AdminAction(admin, "/admin/users/" + uid, $"/admin/actions/users/{uid}/assign", ("moduleId", Guid.NewGuid().ToString()));
        check("Unbekanntes Modul nicht zuweisbar", asgB.Contains("m=invalid"), asgB);
        var rev = await AdminAction(admin, "/admin/users/" + uid, $"/admin/actions/users/{uid}/revoke", ("moduleId", modA));
        var dashRev = await (await user.Get("/dashboard")).Content.ReadAsStringAsync();
        check("Entzug von Modul A wirkt sofort (Dashboard und direkter Aufruf), B bleibt gesperrt", rev.Contains("m=revoked_content") && !dashRev.Contains("Modul A") && (await user.Get("/modules/" + modA)).StatusCode == HttpStatusCode.Forbidden && (await user.Get("/modules/" + modB)).StatusCode == HttpStatusCode.Forbidden, null);
        var matrix = await (await admin.Get("/admin/assignments")).Content.ReadAsStringAsync();
        check("Lernzuweisungs-Übersicht zeigt Benutzer und Module", matrix.Contains("neu.lernende@example.org") && matrix.Contains("Modul C") && matrix.Contains("zugewiesen"), null);
        check("Zuweisen/Entziehen ist im Ereignisprotokoll erfasst", await Count(p.Db, "SELECT COUNT(*) AS n FROM invitation_events WHERE user_id=@u AND event_type IN ('content.assigned','content.revoked') AND actor_user_id IS NOT NULL", ("u", uid)) >= 2, null);

        var deact0 = await AdminAction(admin, "/admin/users/" + uid, $"/admin/actions/users/{uid}/deactivate");
        var deact = await AdminAction(admin, "/admin/users/" + uid, $"/admin/actions/users/{uid}/deactivate", ("confirm", "yes"));
        check("Deaktivierung braucht Bestätigung und sperrt sofort (Sitzung, Dashboard, Module)", deact0.Contains("m=not_confirmed") && deact.Contains("m=deactivated") && (int)(await user.Get("/dashboard")).StatusCode is 302 or 401 or 403 && (int)(await user.Get("/modules/" + modC)).StatusCode is 302 or 401 or 403, null);
        var loginDis = NewBrowser(p.Url); var l3 = await loginDis.Get("/login"); p.UserOidc.Codes["u6"] = new VerifiedIdentity(Issuer, "sub-neu", "neu.lernende@example.org", true);
        check("Deaktivierter Benutzer kann sich nicht erneut anmelden", (await loginDis.Post("/auth/callback", ("code", "u6"), ("state", Query(l3.Headers.Location!, "state")))).Headers.Location!.ToString() == "/invite-failed", null);
        await AdminAction(admin, "/admin/users/" + uid, $"/admin/actions/users/{uid}/reactivate");
        var loginRe = NewBrowser(p.Url); var l4 = await loginRe.Get("/login"); p.UserOidc.Codes["u7"] = new VerifiedIdentity(Issuer, "sub-neu", "neu.lernende@example.org", true);
        check("Nach Reaktivierung wieder anmeldbar, Rechte unverändert (nur Modul C)", (await loginRe.Post("/auth/callback", ("code", "u7"), ("state", Query(l4.Headers.Location!, "state")))).Headers.Location!.ToString() == "/dashboard"
            && (await loginRe.Get("/modules/" + modC)).StatusCode == HttpStatusCode.OK && (await loginRe.Get("/modules/" + modA)).StatusCode == HttpStatusCode.Forbidden, null);

        // ── Widerruf, Filter, Sicherheitsheader ──
        var (id3, _) = await CreateInvitation(p, admin, "widerruf@example.org", "B2C_LEARNER", new[] { modB });
        await AdminAction(admin, $"/admin/invitations/{id3}", $"/admin/actions/invitations/{id3}/send", ("confirm", "yes"));
        var link4 = p.Mailer.Sent.Last().Link;
        var rv0 = await AdminAction(admin, $"/admin/invitations/{id3}", $"/admin/actions/invitations/{id3}/revoke");
        var rv = await AdminAction(admin, $"/admin/invitations/{id3}", $"/admin/actions/invitations/{id3}/revoke", ("confirm", "yes"));
        check("Widerruf braucht Bestätigung; danach ist der Link ungültig", rv0.Contains("m=not_confirmed") && rv.Contains("m=revoked") && (await NewBrowser(p.Url).Get(new Uri(link4).AbsolutePath)).StatusCode == HttpStatusCode.BadRequest, null);
        var fil = await (await admin.Get("/admin/invitations?status=revoked")).Content.ReadAsStringAsync();
        var fil2 = await (await admin.Get("/admin/invitations?q=widerruf")).Content.ReadAsStringAsync();
        check("Filter und Suche: Status „widerrufen“ und Suchtext", fil.Contains("widerruf@example.org") && !fil.Contains("zweite@example.org") && fil2.Contains("widerruf@example.org") && !fil2.Contains("neu.lernende@example.org"), null);
        var hdr = await admin.Get("/admin");
        check("Sicherheitsheader: no-store, CSP, X-Frame-Options, Referrer-Policy", hdr.Headers.CacheControl?.NoStore == true && hdr.Headers.Contains("Content-Security-Policy") && hdr.Headers.GetValues("X-Frame-Options").Single() == "DENY" && hdr.Headers.GetValues("Referrer-Policy").Single() == "no-referrer", null);
        var cookies = hdr.Headers.TryGetValues("Set-Cookie", out var sc) ? sc.ToList() : new List<string>();
        var adminLoginResp = await (NewBrowser(p.Url)).Get("/admin/signin");
        check("Learny-Branding: Stylesheet mit Markenfarben aus dem Bestand, deutsche Seite", (await anon.Get("/portal.css")).Content.ReadAsStringAsync().Result.Contains("#196F67") && (await adminLoginResp.Content.ReadAsStringAsync()).Contains("lang=\"de\""), null);

        // ── Neustart und mehrere Datenbankverbindungen ──
        var (id4, _) = await CreateInvitation(p, admin, "neustart@example.org", "B2C_LEARNER", new[] { modA });
        await AdminAction(admin, $"/admin/invitations/{id4}", $"/admin/actions/invitations/{id4}/send", ("confirm", "yes"));
        var link5 = p.Mailer.Sent.Last().Link;
        var eventsBefore = await Count(p.Db, "SELECT COUNT(*) AS n FROM invitation_events");
        var usersBefore = await NonAdminUsers(p.Db);
        await p.App.StopAsync(); await p.App.DisposeAsync();
        var p2 = await StartPortal(tmp, p.TokenKey, p.DbPath, p.KeyDir, seed: false);
        p2.Mailer = p.Mailer;
        var adminB = NewBrowser(p2.Url); foreach (var kv in admin.Jar) adminB.Jar[kv.Key] = kv.Value; foreach (var kv in admin.Paths) adminB.Paths[kv.Key] = kv.Value;
        var userB = NewBrowser(p2.Url); foreach (var kv in loginRe.Jar) userB.Jar[kv.Key] = kv.Value; foreach (var kv in loginRe.Paths) userB.Paths[kv.Key] = kv.Value;
        check("Neustart: Administrator- und Benutzersitzung bleiben gültig (Data-Protection-Schlüssel persistiert)", (await adminB.Get("/admin/users")).StatusCode == HttpStatusCode.OK && (await userB.Get("/dashboard")).StatusCode == HttpStatusCode.OK, null);
        check("Neustart: Benutzer, Ereignisse, Einladungsstatus und Rechte bleiben erhalten", await NonAdminUsers(p2.Db) == usersBefore && await Count(p2.Db, "SELECT COUNT(*) AS n FROM invitation_events") >= eventsBefore
            && await InvStatus(p2.Db, id1) == "accepted" && (await userB.Get("/modules/" + modC)).StatusCode == HttpStatusCode.OK, null);
        check("Neustart: bereits versendeter Link bleibt gültig (HMAC-Schlüssel stabil, Persistenz in der Datenbank)", (await NewBrowser(p2.Url).Get(new Uri(link5).AbsolutePath)).StatusCode is HttpStatusCode.Found or HttpStatusCode.Redirect, null);
        var rOld = await AdminAction(adminB, $"/admin/invitations/{id4}", $"/admin/actions/invitations/{id4}/send", ("confirm", "yes"));
        check("Neustart: Klartextlink nicht mehr im Speicher → Neuausstellung statt stillem Neuversand", rOld.Contains("m=link_unavailable"), rOld);

        // zwei Portalinstanzen auf derselben Datenbankdatei: Einlösung höchstens einmal
        var p3 = await StartPortal(tmp, p.TokenKey, p.DbPath, p.KeyDir, seed: false);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
        {
            var host = i % 2 == 0 ? p2 : p3;
            var b = NewBrowser(host.Url);
            await b.Get(new Uri(link5).AbsolutePath);
            var s = await b.Get("/invite-start");
            if (s.Headers.Location is null) return "invalid";   // Link bereits verbraucht
            host.UserOidc.Codes["par" + i] = new VerifiedIdentity(Issuer, "par-sub-" + i, "neustart@example.org", true);
            var r = await b.Post("/auth/callback", ("code", "par" + i), ("state", Query(s.Headers.Location!, "state")));
            return r.Headers.Location?.ToString();
        })));
        check("Zwei Portalinstanzen, eine Datenbank, 12 parallele Einlösungen → genau ein Erfolg", results.Count(x => x == "/dashboard") == 1 && await InvStatus(p2.Db, id4) == "accepted" && await NonAdminUsers(p2.Db) == usersBefore + 1, string.Join(",", results));
        await p3.App.StopAsync(); await p3.App.DisposeAsync();

        // ── Rate Limit, CSRF, OTP-Endpunkt getrennt ──
        var rl = await StartPortal(tmp, sensitivePerMinute: 5, seed: false);
        var limited = new List<HttpStatusCode>();
        var bb = NewBrowser(rl.Url);
        for (var i = 0; i < 9; i++) limited.Add((await bb.Get("/invite/" + new string('x', 43))).StatusCode);
        check("Rate Limiting auf Einladungslink-Aufrufen: nach 5 Anfragen pro Minute 429", limited.Take(5).All(s => s == HttpStatusCode.BadRequest) && limited.Skip(5).All(s => s == HttpStatusCode.TooManyRequests), string.Join(",", limited));
        check("Unlimitierte Seiten bleiben erreichbar (Startseite)", (await bb.Get("/")).StatusCode == HttpStatusCode.OK, null);
        await rl.App.StopAsync(); await rl.App.DisposeAsync();

        var noOtp = await StartPortal(tmp, seed: false);
        var refused = false;
        try { await new HttpClient { Timeout = TimeSpan.FromSeconds(3) }.PostAsync($"http://127.0.0.1:{noOtp.OtpPort}/extensions/otp-send", new StringContent("{}")); } catch (HttpRequestException) { refused = true; } catch (TaskCanceledException) { refused = true; }
        check("Fehlender OTP-Endpunkt: kein Listener auf dem OTP-Port, im Hauptportal ebenfalls nicht vorhanden (404)", refused && (await new HttpClient().PostAsync($"{noOtp.Url}/extensions/otp-send", new StringContent("{}"))).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, null);
        await noOtp.App.StopAsync(); await noOtp.App.DisposeAsync();

        var oOpt = new OtpExtensionOptions { Authority = "https://t.ciamlogin.com/tid/v2.0", Audiences = new[] { "api-app" }, ExpectedTenantId = "tid" };
        var withOtp = await StartPortal(tmp, seed: false, otp: new OtpSendHandler(new ExtensionTokenValidator(new HttpClient(), oOpt), new FakeOtpMailer(), new AllowNone(), oOpt));
        var otpHttp = new HttpClient();
        check("OTP-Endpunkt auf eigenem Port ohne Token: 401; Verwaltung dort nicht erreichbar (404); im Hauptportal fehlt der OTP-Pfad",
            (await otpHttp.PostAsync($"http://127.0.0.1:{withOtp.OtpPort}/extensions/otp-send", new StringContent("{}"))).StatusCode == HttpStatusCode.Unauthorized
            && (await otpHttp.GetAsync($"http://127.0.0.1:{withOtp.OtpPort}/admin")).StatusCode == HttpStatusCode.NotFound
            && (await otpHttp.PostAsync($"{withOtp.Url}/extensions/otp-send", new StringContent("{}"))).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, null);
        await withOtp.App.StopAsync(); await withOtp.App.DisposeAsync();
        await p2.App.StopAsync(); await p2.App.DisposeAsync();
    }

    sealed class FakeOtpMailer : IOtpMailer { public Task<MailSendResult> SendOtpAsync(OtpMail mail, CancellationToken ct = default) => Task.FromResult(new MailSendResult(MailOutcome.Accepted, "accepted")); }
    sealed class AllowNone : IOtpRecipientPolicy { public Task<bool> IsAllowedAsync(string email, CancellationToken ct) => Task.FromResult(false); }

    static async Task PortalRoleTests(Action<string, bool, string?> check, string tmp)
    {
        Console.WriteLine("Portal: Administration über App-Rolle (dieselbe Entra-App wie Learny, Rolle Admin.All)");
        var p = await StartPortal(tmp, seed: false, adminRoles: new[] { "Admin.All" }, adminSubjects: Array.Empty<string>());
        async Task<(Browser B, string Loc)> Login(PortalApp? _, Portal pp, string sub, string issuer, string[]? roles)
        {
            var b = NewBrowser(pp.Url); var code = "r" + Guid.NewGuid().ToString("N")[..8];
            pp.AdminOidc.Codes[code] = new VerifiedIdentity(issuer, sub, "person@wf.test", true, roles);
            var start = await b.Get("/admin/login");
            var res = await b.Post("/admin/callback", ("code", code), ("state", Query(start.Headers.Location!, "state")));
            return (b, res.Headers.Location?.ToString() ?? "");
        }
        var ok = await Login(null, p, "learny-admin", AdminIssuer, new[] { "Admin.All", "Users" });
        check("App-Rolle Admin.All im ID-Token des vertrauten Workforce-Issuers → Administration", ok.Loc == "/admin" && (await ok.B.Get("/admin")).StatusCode == HttpStatusCode.OK, ok.Loc);
        check("Rolle admin wird nur für diese Identität angelegt", await Count(p.Db, "SELECT COUNT(*) AS n FROM user_roles WHERE role='admin'") == 1, null);
        var users = await Login(null, p, "learny-user", AdminIssuer, new[] { "Users" });
        check("Nur Rolle Users (kein Admin.All) → abgelehnt", users.Loc == "/admin/denied" && (int)(await users.B.Get("/admin")).StatusCode is 302 or 401 or 403, users.Loc);
        var none = await Login(null, p, "learny-none", AdminIssuer, null);
        check("Ohne roles-Claim → abgelehnt", none.Loc == "/admin/denied", null);
        var ext = await Login(null, p, "learny-ext", Issuer, new[] { "Admin.All" });
        check("Rolle Admin.All, aber External-ID-Issuer → abgelehnt (Adminrechte nie aus dem External Tenant)", ext.Loc == "/admin/denied", null);
        var lower = await Login(null, p, "learny-case", AdminIssuer, new[] { "admin.all" });
        check("Rollenname wird exakt verglichen (admin.all ≠ Admin.All)", lower.Loc == "/admin/denied", null);
        var first = await Login(null, p, "rev-sub", AdminIssuer, new[] { "Admin.All" });
        check("Administrator mit Rolle angemeldet", first.Loc == "/admin" && (await first.B.Get("/admin/users")).StatusCode == HttpStatusCode.OK, null);
        var again = await Login(null, p, "rev-sub", AdminIssuer, new[] { "Users" });
        check("Rolle in Entra entzogen: erneute Anmeldung abgelehnt, bestehende Sitzung sofort ungültig, Rolle in der Datenbank entfernt",
            again.Loc == "/admin/denied" && (int)(await first.B.Get("/admin/users")).StatusCode is 302 or 401 or 403
            && await Count(p.Db, "SELECT COUNT(*) AS n FROM user_roles r JOIN user_identities i ON i.user_id=r.user_id WHERE i.subject='rev-sub' AND r.role='admin'") == 0, again.Loc);
        var regained = await Login(null, p, "rev-sub", AdminIssuer, new[] { "Admin.All" });
        check("Rolle wieder vergeben: Anmeldung wieder möglich (gleicher interner Benutzer, keine Doppelanlage)", regained.Loc == "/admin"
            && await Count(p.Db, "SELECT COUNT(*) AS n FROM user_identities WHERE subject='rev-sub'") == 1, null);
        check("Ablehnungen sind protokolliert (ohne Details nach außen)", await Count(p.Db, "SELECT COUNT(*) AS n FROM invitation_events WHERE event_type='admin.login_rejected'") >= 4, null);
        await p.App.StopAsync(); await p.App.DisposeAsync();

        // Rollen nur, wenn konfiguriert; Subject-Liste bleibt als Bootstrap möglich
        var q = await StartPortal(tmp, seed: false, adminRoles: null, adminSubjects: new[] { "s1" });
        var viaSub = await Login(null, q, "s1", AdminIssuer, null);
        var roleOnly = await Login(null, q, "s2", AdminIssuer, new[] { "Admin.All" });
        check("Ohne konfigurierte Rolle zählt der Claim nicht; die Subject-Bootstrap-Liste funktioniert weiterhin", viaSub.Loc == "/admin" && roleOnly.Loc == "/admin/denied", null);
        var empty = false; try { _ = new AdminBootstrap(q.Db, AdminIssuer, Array.Empty<string>(), new[] { Issuer }); } catch (Exception) { empty = true; }
        check("Bootstrap ohne Rollen und Subjects kann niemanden zulassen (AllowedCount = 0)", !empty && new AdminBootstrap(q.Db, AdminIssuer, Array.Empty<string>(), new[] { Issuer }).AllowedCount == 0, null);
        await q.App.StopAsync(); await q.App.DisposeAsync();
    }

    static PortalServices PS(Portal p) => (PortalServices)p.App.Main.Services.GetService(typeof(PortalServices))!;

    static async Task<Dictionary<string, string?>> ToAsyncSingle(this List<Dictionary<string, string?>> rows) { await Task.Yield(); return rows[0]; }
}
