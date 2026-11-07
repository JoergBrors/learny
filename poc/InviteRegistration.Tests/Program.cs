using InviteRegistration;
using LearnCards.Web.Data;

// Paketfreie Tests der einladungspflichtigen Registrierung (ADR 0042).
// Start: dotnet run --project poc/InviteRegistration.Tests   (Exit-Code 0 = alles bestanden)

var failures = new List<string>();
var passed = 0;
void Check(string name, bool ok, string? detail = null)
{
    if (ok) { passed++; Console.WriteLine($"  ok   {name}"); }
    else { failures.Add(name); Console.WriteLine($"  FAIL {name} {detail}"); }
}

var tmpRoot = Path.Combine(Path.GetTempPath(), "inv-poc-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmpRoot);
var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
const string Issuer = "https://tenant.ciamlogin.com/t/v2.0";

async Task<Env> NewEnv(Func<DateTime>? clock = null, int hours = 72)
{
    var path = Path.Combine(tmpRoot, Guid.NewGuid().ToString("N")[..8] + ".db");
    var db = new SqliteDatabase(path);
    await db.InitAsync();
    var now = DateTime.UtcNow;
    var adminId = Guid.NewGuid().ToString();
    await db.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id,'Admin','admin@x.test','active',0,@n,@n)",
        new (string, object?)[] { ("id", adminId), ("n", now) });
    await db.ExecuteAsync("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@u,'admin',NULL,@n)", new (string, object?)[] { ("u", adminId), ("n", now) });
    var svc = New(db, clock, hours);
    await svc.InitAsync();
    var (a, b, c) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
    await db.ExecuteAsync("INSERT INTO poc_modules (id,name,scope) VALUES (@a,'A global','global'),(@b,'B global','global'),(@c,'C shared','shared')",
        new (string, object?)[] { ("a", a), ("b", b), ("c", c) });
    return new Env(path, db, svc, adminId, a, b, c, clock, hours);
}
InvitationService New(IDatabase db, Func<DateTime>? clock, int hours) =>
    new(db, new InvitationOptions { BaseUrl = "https://poc.example.org", TokenKey = key, ValidityHours = hours, Clock = clock ?? (() => DateTime.UtcNow) });

VerifiedIdentity Id(string email, string? sub = null, bool verified = true) => new(Issuer, sub ?? Guid.NewGuid().ToString(), email, verified);
async Task<long> Count(IDatabase db, string sql, params (string, object?)[] a) => long.Parse((await db.QueryAsync(sql, a))[0]["n"]!);
async Task<string> InvStatus(IDatabase db, string id) => (await db.QueryAsync("SELECT status FROM invitations WHERE invitation_id=@i", new (string, object?)[] { ("i", id) }))[0]["status"]!;
async Task<long> NonAdminUsers(IDatabase db) => await Count(db, "SELECT COUNT(*) AS n FROM users WHERE id NOT IN (SELECT user_id FROM user_roles)");

Console.WriteLine("Anzeigename aus der Einladung");
{
    var e = await NewEnv();
    var named = await e.Svc.CreateAsync(e.Admin, "mit.name@example.org", TargetProfiles.B2cLearner, new[] { e.A }, new InvitationMeta("Joerg Zwei", null, null, 72));
    var plain = await e.Svc.CreateAsync(e.Admin, "ohne.name@example.org", TargetProfiles.B2cLearner, new[] { e.A });
    await e.Svc.RedeemAsync(named.Token, Id("mit.name@example.org")); await e.Svc.RedeemAsync(plain.Token, Id("ohne.name@example.org"));
    async Task<string> NameOf(string mail) => (await e.Db.QueryAsync("SELECT display_name FROM users WHERE email=@m", new (string, object?)[] { ("m", mail) }))[0]["display_name"]!;
    Check("Benutzername = Anzeigename der Einladung", await NameOf("mit.name@example.org") == "Joerg Zwei");
    Check("Ohne Anzeigename: Teil der Adresse vor dem @", await NameOf("ohne.name@example.org") == "ohne.name");
    var dup = false; try { await e.Svc.CreateAsync(e.Admin, "Mit.Name@Example.org", TargetProfiles.B2cLearner, new[] { e.A }); } catch (AccountExistsException) { dup = true; }
    Check("Einladung an eine Adresse mit vorhandenem Konto wird abgelehnt (Groß-/Kleinschreibung egal)", dup && await Count(e.Db, "SELECT COUNT(*) AS n FROM invitations WHERE recipient_email='mit.name@example.org'") == 1);
    Check("Andere Adresse bleibt einladbar", (await e.Svc.CreateAsync(e.Admin, "neu@example.org", TargetProfiles.B2cLearner, new[] { e.A })).Token.Length == 43);
}

Console.WriteLine("Gültige Einladung, neue Identität");
{
    var e = await NewEnv();
    var inv = await e.Svc.CreateAsync(e.Admin, "Lernende@Example.org", TargetProfiles.B2cLearner, new[] { e.A, e.C });
    Check("Link hat Format https://…/invite/<token>", inv.Link == "https://poc.example.org/invite/" + inv.Token);
    Check("Token ≥ 256 Bit (32 Byte, Base64url)", inv.Token.Length == 43 && Convert.FromBase64String(inv.Token.Replace('-', '+').Replace('_', '/') + "=").Length == 32);
    Check("Vorprüfung ok, verbraucht nichts", await e.Svc.IsRedeemableAsync(inv.Token) && await InvStatus(e.Db, inv.InvitationId) == "pending");
    var r = await e.Svc.RedeemAsync(inv.Token, Id("lernende@example.org"));
    Check("Einlösung erfolgreich", r.Success && r.Profile == TargetProfiles.B2cLearner, r.Outcome.ToString());
    Check("Einladung accepted mit accepted_user_id", await InvStatus(e.Db, inv.InvitationId) == "accepted"
        && (await e.Db.QueryAsync("SELECT accepted_user_id, accepted_at FROM invitations"))[0]["accepted_user_id"] == r.UserId);
    Check("Profil aus Einladung gespeichert", await Count(e.Db, "SELECT COUNT(*) AS n FROM user_profiles WHERE user_id=@u AND profile='B2C_LEARNER'", ("u", r.UserId)) == 1);
    Check("Inhaltsrechte aus Einladung kopiert (2)", await Count(e.Db, "SELECT COUNT(*) AS n FROM module_grants WHERE user_id=@u", ("u", r.UserId)) == 2);
    Check("Kein Klartexttoken in irgendeiner Tabelle", await Count(e.Db,
        "SELECT (SELECT COUNT(*) FROM invitations WHERE token_hash=@t OR recipient_email=@t) + (SELECT COUNT(*) FROM invitation_audit WHERE reason=@t OR event=@t) AS n", ("t", inv.Token)) == 0);
    Check("Gespeicherter Hash ≠ Token, 64 Hex", (await e.Db.QueryAsync("SELECT token_hash FROM invitations"))[0]["token_hash"] is { Length: 64 } h && h != inv.Token);
    Check("Wiederholung des Callbacks → ungültig", (await e.Svc.RedeemAsync(inv.Token, Id("lernende@example.org"))).Outcome == RedeemOutcome.AlreadyUsed);
    Check("Wiederholung mit gleicher Identität → ungültig, genau 1 Benutzer", (await e.Svc.RedeemAsync(inv.Token, new VerifiedIdentity(Issuer, (await e.Db.QueryAsync("SELECT subject FROM user_identities"))[0]["subject"]!, "lernende@example.org", true))).Success == false
        && await NonAdminUsers(e.Db) == 1);
    Check("Link nach Einlösung nicht mehr vorprüfbar", !await e.Svc.IsRedeemableAsync(inv.Token));
}

Console.WriteLine("Fehlende/unbekannte/abgelaufene/widerrufene Links");
{
    var e = await NewEnv();
    Check("Fehlender Link (null)", (await e.Svc.RedeemAsync(null, Id("a@b.test"))).Outcome == RedeemOutcome.MissingToken);
    Check("Fehlender Link (leer)", (await e.Svc.RedeemAsync("  ", Id("a@b.test"))).Outcome == RedeemOutcome.MissingToken);
    Check("Unbekannter Link", (await e.Svc.RedeemAsync("x" + new string('y', 42), Id("a@b.test"))).Outcome == RedeemOutcome.UnknownToken);
    Check("Überlanger Link", (await e.Svc.RedeemAsync(new string('a', 5000), Id("a@b.test"))).Outcome == RedeemOutcome.UnknownToken);
    Check("Alle Fehler: dieselbe öffentliche Meldung", (await e.Svc.RedeemAsync(null, Id("a@b.test"))).PublicMessage == (await e.Svc.RedeemAsync("zzz", Id("a@b.test"))).PublicMessage);
    Check("Kein Benutzer ohne Einladung angelegt", await NonAdminUsers(e.Db) == 0);

    var now = DateTime.UtcNow;
    var clock = now;
    var e2 = await NewEnv(() => clock);
    var inv = await e2.Svc.CreateAsync(e2.Admin, "x@y.test", TargetProfiles.B2bLearner);
    clock = now.AddHours(72).AddSeconds(1);
    Check("Abgelaufener Link (72 h Standard)", (await e2.Svc.RedeemAsync(inv.Token, Id("x@y.test"))).Outcome == RedeemOutcome.Expired && !await e2.Svc.IsRedeemableAsync(inv.Token));
    Check("Kein Benutzer bei Ablauf", await NonAdminUsers(e2.Db) == 0);

    var clock3 = now;
    var e3 = await NewEnv(() => clock3, hours: 1);
    var inv3 = await e3.Svc.CreateAsync(e3.Admin, "x@y.test", TargetProfiles.B2bLearner);
    Check("Gültigkeit konfigurierbar (1 h)", (inv3.ExpiresAt - now).TotalMinutes is > 59 and < 61);

    var e4 = await NewEnv();
    var inv4 = await e4.Svc.CreateAsync(e4.Admin, "x@y.test", TargetProfiles.B2bLearner);
    Check("Widerruf durch Administrator", await e4.Svc.RevokeAsync(e4.Admin, inv4.InvitationId));
    Check("Widerrufener Link ungültig", (await e4.Svc.RedeemAsync(inv4.Token, Id("x@y.test"))).Outcome == RedeemOutcome.Revoked && await NonAdminUsers(e4.Db) == 0);
    var inv5 = await e4.Svc.CreateAsync(e4.Admin, "z@y.test", TargetProfiles.B2bLearner);
    await e4.Svc.RedeemAsync(inv5.Token, Id("z@y.test"));
    Check("Verbrauchte Einladung nicht widerrufbar/rücksetzbar", !await e4.Svc.RevokeAsync(e4.Admin, inv5.InvitationId) && await InvStatus(e4.Db, inv5.InvitationId) == "accepted");
}

Console.WriteLine("Verbrauchter Link bleibt ungültig (auch nach Deaktivierung/Identitätsänderung)");
{
    var e = await NewEnv();
    var inv = await e.Svc.CreateAsync(e.Admin, "u@y.test", TargetProfiles.B2cLearner);
    var r = await e.Svc.RedeemAsync(inv.Token, Id("u@y.test"));
    await e.Db.ExecuteAsync("UPDATE users SET status='disabled' WHERE id=@u", new (string, object?)[] { ("u", r.UserId) });
    Check("Nach Deaktivierung weiter ungültig", (await e.Svc.RedeemAsync(inv.Token, Id("u@y.test"))).Outcome == RedeemOutcome.AlreadyUsed);
    await e.Db.ExecuteAsync("DELETE FROM user_identities WHERE user_id=@u", new (string, object?)[] { ("u", r.UserId) });
    Check("Nach Entfernen der Identität weiter ungültig", (await e.Svc.RedeemAsync(inv.Token, Id("u@y.test"))).Outcome == RedeemOutcome.AlreadyUsed);
}

Console.WriteLine("Vorhandene Benutzer, E-Mail-Bindung");
{
    var e = await NewEnv();
    var inv = await e.Svc.CreateAsync(e.Admin, "ex@y.test", TargetProfiles.B2cLearner, new[] { e.A });
    var sub = "existing-sub";
    var first = await e.Svc.CreateAsync(e.Admin, "ex@y.test", TargetProfiles.B2cLearner);
    await e.Svc.RedeemAsync(first.Token, new VerifiedIdentity(Issuer, sub, "ex@y.test", true));
    var users = await NonAdminUsers(e.Db);
    var r = await e.Svc.RedeemAsync(inv.Token, new VerifiedIdentity(Issuer, sub, "ex@y.test", true));
    Check("Bestehende Identität kann Einladung nicht einlösen", r.Outcome == RedeemOutcome.IdentityAlreadyRegistered);
    Check("Einladung bleibt unverbraucht, kein neuer Benutzer, keine neuen Rechte", await InvStatus(e.Db, inv.InvitationId) == "pending" && await NonAdminUsers(e.Db) == users
        && await Count(e.Db, "SELECT COUNT(*) AS n FROM module_grants") == 0);

    var other = await e.Svc.RedeemAsync(inv.Token, new VerifiedIdentity(Issuer, "other-sub", "ex@y.test", true));
    Check("Gleiche E-Mail, andere Identität: separater Benutzer, keine automatische Verknüpfung",
        other.Success && other.UserId != (await e.Db.QueryAsync("SELECT user_id FROM user_identities WHERE subject=@s", new (string, object?)[] { ("s", sub) }))[0]["user_id"]
        && await Count(e.Db, "SELECT COUNT(*) AS n FROM user_identities WHERE user_id=(SELECT user_id FROM user_identities WHERE subject=@s)", ("s", sub)) == 1);

    var inv2 = await e.Svc.CreateAsync(e.Admin, "bound@y.test", TargetProfiles.B2bLearner);
    Check("Falsche E-Mail nicht einlösbar", (await e.Svc.RedeemAsync(inv2.Token, Id("andere@y.test"))).Outcome == RedeemOutcome.EmailMismatch);
    Check("Fehlende E-Mail nicht einlösbar", (await e.Svc.RedeemAsync(inv2.Token, Id(null!))).Outcome == RedeemOutcome.EmailMismatch);
    Check("Nicht verifizierte E-Mail nicht einlösbar", (await e.Svc.RedeemAsync(inv2.Token, Id("bound@y.test", verified: false))).Outcome == RedeemOutcome.EmailNotVerified);
    Check("Einladung nach Fehlversuchen weiter einlösbar (nichts verbraucht)", await InvStatus(e.Db, inv2.InvitationId) == "pending"
        && (await e.Svc.RedeemAsync(inv2.Token, Id("BOUND@y.test "))).Success);
    Check("Leere Identität abgewiesen", (await e.Svc.RedeemAsync(inv2.Token, new VerifiedIdentity("", "", "a@b.test", true))).Outcome == RedeemOutcome.InvalidIdentity);
}

Console.WriteLine("Parallele Einlösung");
{
    var e = await NewEnv();
    var inv = await e.Svc.CreateAsync(e.Admin, "p@y.test", TargetProfiles.B2cLearner, new[] { e.A });
    // zwei unabhängige Datenbankverbindungen/Dienstinstanzen auf derselben Datei (zwei „Browser“/Prozesse)
    var db2 = new SqliteDatabase(e.Path); await db2.InitAsync();
    var svc2 = New(db2, null, 72);
    var tasks = Enumerable.Range(0, 24).Select(i => Task.Run(() => (i % 2 == 0 ? e.Svc : svc2).RedeemAsync(inv.Token, Id("p@y.test", "par-" + i)))).ToArray();
    var results = await Task.WhenAll(tasks);
    Check("Genau 1 Erfolg bei 24 parallelen Einlösungen (verschiedene Identitäten)", results.Count(r => r.Success) == 1, results.Count(r => r.Success).ToString());
    Check("Genau 1 Benutzer, 1 Profil, 1 Identität", await NonAdminUsers(e.Db) == 1 && await Count(e.Db, "SELECT COUNT(*) AS n FROM user_profiles") == 1 && await Count(e.Db, "SELECT COUNT(*) AS n FROM user_identities") == 1);
    Check("Verlierer erhalten Ungültig", results.Where(r => !r.Success).All(r => r.Outcome is RedeemOutcome.AlreadyUsed or RedeemOutcome.IdentityAlreadyRegistered));

    var inv2 = await e.Svc.CreateAsync(e.Admin, "q@y.test", TargetProfiles.B2cLearner);
    var same = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => (i % 2 == 0 ? e.Svc : svc2).RedeemAsync(inv2.Token, new VerifiedIdentity(Issuer, "same-sub", "q@y.test", true)))));
    Check("Gleiche Identität parallel: höchstens 1 Erfolg, 1 Benutzer", same.Count(r => r.Success) == 1
        && await Count(e.Db, "SELECT COUNT(*) AS n FROM user_identities WHERE subject='same-sub'") == 1);
    await db2.DisposeAsync();
}

Console.WriteLine("Fehler bei Benutzeranlage/Profil/Rechten");
foreach (var step in new[] { "create_user", "assign_profile", "grant_content" })
{
    var e = await NewEnv();
    var inv = await e.Svc.CreateAsync(e.Admin, "f@y.test", TargetProfiles.B2cLearner, new[] { e.A });
    e.Svc.FaultInjector = s => s == step ? throw new InvalidOperationException("injected") : Task.CompletedTask;
    var r = await e.Svc.RedeemAsync(inv.Token, Id("f@y.test", "fault-sub"));
    Check($"[{step}] Fehler → Registrierung abgelehnt", r.Outcome == RedeemOutcome.ProvisioningFailed);
    Check($"[{step}] nichts angelegt (Benutzer, Identität, Profil, Rechte)", await NonAdminUsers(e.Db) == 0
        && await Count(e.Db, "SELECT COUNT(*) AS n FROM user_identities") == 0 && await Count(e.Db, "SELECT COUNT(*) AS n FROM user_profiles") == 0
        && await Count(e.Db, "SELECT COUNT(*) AS n FROM module_grants") == 0);
    Check($"[{step}] Einladung zurück auf pending, anschließend erneut einlösbar", await InvStatus(e.Db, inv.InvitationId) == "pending"
        && (e.Svc.FaultInjector = null) == null && (await e.Svc.RedeemAsync(inv.Token, Id("f@y.test", "fault-sub"))).Success);
}
{
    // Kompensation scheitert selbst: Einladung bleibt verbraucht (fail-closed), halber Benutzer hat keinen Zugriff.
    var e = await NewEnv();
    var inv = await e.Svc.CreateAsync(e.Admin, "g@y.test", TargetProfiles.B2cLearner, new[] { e.A });
    e.Svc.FaultInjector = async s =>
    {
        if (s != "assign_profile") return;
        await e.Db.ExecuteAsync("CREATE TRIGGER no_delete BEFORE DELETE ON users BEGIN SELECT RAISE(ABORT,'blocked'); END");
        throw new InvalidOperationException("injected");
    };
    var r = await e.Svc.RedeemAsync(inv.Token, Id("g@y.test", "g-sub"));
    var uid = (await e.Db.QueryAsync("SELECT user_id FROM user_identities WHERE subject='g-sub'")).FirstOrDefault()?["user_id"];
    Check("Fehlgeschlagene Kompensation: Einladung bleibt accepted", !r.Success && await InvStatus(e.Db, inv.InvitationId) == "accepted");
    Check("Halb angelegter Benutzer ohne Profil hat keinen Inhaltszugriff", uid is not null && !await new ContentAccess(e.Db).CanReadModuleAsync(uid, e.A));
}

Console.WriteLine("Keine Privilegieneskalation, Administrationsschutz");
{
    var e = await NewEnv();
    foreach (var bad in new[] { "ADMIN", "admin", "B2C_LEARNER; DROP", "", "B2B_LEARNER " })
    {
        var threw = false;
        try { await e.Svc.CreateAsync(e.Admin, "a@b.test", bad); } catch (ArgumentException) { threw = true; }
        Check($"Zielprofil '{bad}' abgelehnt", threw);
    }
    var nonAdmin = Guid.NewGuid().ToString();
    await e.Db.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id,'U','u@x.test','active',0,@n,@n)", new (string, object?)[] { ("id", nonAdmin), ("n", DateTime.UtcNow) });
    var denied = false;
    try { await e.Svc.CreateAsync(nonAdmin, "a@b.test", TargetProfiles.B2cLearner); } catch (UnauthorizedAccessException) { denied = true; }
    Check("Nicht-Administrator kann keine Einladung erzeugen", denied);
    var denied2 = false;
    var any = await e.Svc.CreateAsync(e.Admin, "a@b.test", TargetProfiles.B2bLearner);
    try { await e.Svc.RevokeAsync(nonAdmin, any.InvitationId); } catch (UnauthorizedAccessException) { denied2 = true; }
    Check("Nicht-Administrator kann nicht widerrufen", denied2);
    var r = await e.Svc.RedeemAsync(any.Token, Id("a@b.test"));
    Check("Eingeladener erhält keine Rolle (user_roles unverändert, nur Admin)", await Count(e.Db, "SELECT COUNT(*) AS n FROM user_roles WHERE user_id=@u", ("u", r.UserId)) == 0
        && await Count(e.Db, "SELECT COUNT(*) AS n FROM user_roles") == 1);
    var modThrew = false;
    try { await e.Svc.CreateAsync(e.Admin, "m@b.test", TargetProfiles.B2cLearner, new[] { Guid.NewGuid().ToString() }); } catch (ArgumentException) { modThrew = true; }
    Check("Unbekanntes Modul nicht zuweisbar", modThrew);
}

Console.WriteLine("Zugriff nur auf zugewiesene Module (A global+zugewiesen, B global, C shared+zugewiesen)");
{
    var e = await NewEnv();
    var inv = await e.Svc.CreateAsync(e.Admin, "c@y.test", TargetProfiles.B2cLearner, new[] { e.A, e.C });
    var r = await e.Svc.RedeemAsync(inv.Token, Id("c@y.test"));
    var acc = new ContentAccess(e.Db);
    Check("A erlaubt", await acc.CanReadModuleAsync(r.UserId!, e.A));
    Check("B verweigert", !await acc.CanReadModuleAsync(r.UserId!, e.B));
    Check("C erlaubt", await acc.CanReadModuleAsync(r.UserId!, e.C));
    Check("Liste enthält nur A und C", (await acc.ListReadableModulesAsync(r.UserId!)).OrderBy(x => x).SequenceEqual(new[] { e.A, e.C }.OrderBy(x => x)));
    Check("Unbekannter Benutzer: nichts", (await acc.ListReadableModulesAsync(Guid.NewGuid().ToString())).Count == 0);
}

Console.WriteLine("Konfiguration (fail-closed)");
{
    var e = await NewEnv();
    static bool Throws(Action a) { try { a(); return false; } catch (InvalidOperationException) { return true; } }
    Check("HTTP außerhalb von localhost abgelehnt", Throws(() => _ = new InvitationService(e.Db, new InvitationOptions { BaseUrl = "http://poc.example.org", TokenKey = key })));
    Check("HTTP auf localhost erlaubt", !Throws(() => _ = new InvitationService(e.Db, new InvitationOptions { BaseUrl = "http://localhost:5000", TokenKey = key })));
    Check("Zu kurzer Schlüssel abgelehnt", Throws(() => _ = new InvitationService(e.Db, new InvitationOptions { BaseUrl = "https://x.test", TokenKey = new byte[8] })));
    Check("Gültigkeit außerhalb 1–720 h abgelehnt", Throws(() => _ = new InvitationService(e.Db, new InvitationOptions { BaseUrl = "https://x.test", TokenKey = key, ValidityHours = 0 })));
    var tokens = new HashSet<string>();
    for (var i = 0; i < 50; i++) tokens.Add((await e.Svc.CreateAsync(e.Admin, $"t{i}@y.test", TargetProfiles.B2bLearner)).Token);
    Check("50 Tokens, alle verschieden", tokens.Count == 50);
}

await ExtendedTests.RunAsync(Check, tmpRoot, key);

try { Directory.Delete(tmpRoot, true); } catch { /* Windows kann SQLite-Dateien kurz halten */ }
Console.WriteLine($"\n{passed} bestanden, {failures.Count} fehlgeschlagen");
foreach (var f in failures) Console.WriteLine("  - " + f);
return failures.Count == 0 ? 0 : 1;

sealed record Env(string Path, IDatabase Db, InvitationService Svc, string Admin, string A, string B, string C, Func<DateTime>? Clock, int Hours);
