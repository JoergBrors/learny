using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;
using LearnCards.Web.Services.ImageStorage;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// ADR 0049 (Phase B), im Prozess auf SQLite: Bestandsschutz des Bestandsprofils, einladungspflichtige Registrierung mit atomarer Einlösung (Anspruch, Parallelität,
/// Fehlerinjektion, unbekannter Commit-Ausgang), Zugriffsregeln externer Lernprofile für Module und Themen, Zuweisung und Entzug, Migration und Transaktionssemantik.
/// PostgreSQL ist hiermit <b>nicht</b> belegt (siehe docs/database-schema.md, offener Prüfpunkt).
/// </summary>
public static class InvitationUnitTests
{
    private const string WfIssuer = "https://wf.example/v2.0", ExtIssuer = "https://ext.example/v2.0";

    /// <summary>Datenbank-Hülle: kann in der nächsten atomaren Gruppe eine fehlerhafte Anweisung einschieben oder nach dem Commit einen Verbindungsabbruch melden.</summary>
    internal sealed class FaultBatchDb : IDatabase
    {
        private readonly IDatabase _inner;
        public int? FailAt; public bool ThrowAfterCommit;
        public FaultBatchDb(IDatabase inner) => _inner = inner;
        public string ProviderName => _inner.ProviderName;
        public Task InitAsync(CancellationToken ct = default) => _inner.InitAsync(ct);
        public Task<List<Dictionary<string, string?>>> QueryAsync(string sql, IReadOnlyList<(string Name, object? Value)>? args = null, CancellationToken ct = default) => _inner.QueryAsync(sql, args, ct);
        public Task<int> ExecuteAsync(string sql, IReadOnlyList<(string Name, object? Value)>? args = null, CancellationToken ct = default) => _inner.ExecuteAsync(sql, args, ct);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
        public async Task<int[]> ExecuteBatchAsync(IReadOnlyList<DbStatement> statements, CancellationToken ct = default)
        {
            if (FailAt is int k)
            {
                var list = statements.ToList();
                list.Insert(Math.Min(k, list.Count), new DbStatement("INSERT INTO tabelle_gibt_es_nicht (x) VALUES (1)"));
                statements = list; FailAt = null;
            }
            var r = await _inner.ExecuteBatchAsync(statements, ct);
            if (ThrowAfterCommit) { ThrowAfterCommit = false; throw new IOException("Verbindung beim Commit verloren (simuliert)"); }
            return r;
        }
    }

    internal sealed class Env
    {
        public SqliteDatabase Raw = null!; public FaultBatchDb Db = null!; public AppConfig Cfg = null!; public UserService Users = null!; public CardRepository Repo = null!;
        public TopicService Topics = null!; public InvitationService Invites = null!; public LearnerAdminService Learners = null!; public InviteEvents Events = null!;
        public string Admin = "", TopicAdmin = "", Auditor = "";
        public DateTime Now = DateTime.UtcNow;

        public static async Task<Env> CreateAsync(string dir)
        {
            Directory.CreateDirectory(dir);
            var e = new Env { Raw = new SqliteDatabase(Path.Combine(dir, "t.db")) };
            await e.Raw.InitAsync();
            e.Db = new FaultBatchDb(e.Raw);
            e.Cfg = new AppConfig { Invitations = new InviteSettings { TokenKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), LinkBaseUrl = "https://learny.test" } };
            e.Users = new UserService(e.Db, e.Cfg);
            var images = new CardImageService(e.Db, new ImageStorageRegistry(e.Cfg, e.Db), new NullImageCache(), e.Cfg, NullLogger<CardImageService>.Instance);
            var pool = new QuizPoolService(e.Db);
            e.Repo = new CardRepository(e.Db, images, pool);
            var modules = new ModuleTransferService(e.Repo, images, pool, e.Users, new CompetencyService(e.Db, e.Repo, e.Users));
            e.Topics = new TopicService(e.Db, e.Repo, e.Users, pool, null!, modules, new ModuleVersionService(e.Db, e.Repo, e.Users));
            e.Events = new InviteEvents(e.Db);
            e.Invites = InvitationService.Create(e.Db, e.Users, e.Cfg.Invitations, e.Events, e.Topics);
            e.Invites.UseClock(() => e.Now);
            e.Learners = new LearnerAdminService(e.Db, e.Users, e.Topics, e.Events);
            e.Admin = await e.AddWorkforceUserAsync("Admin", "admin@example.org", Roles.Admin);
            e.TopicAdmin = await e.AddWorkforceUserAsync("Themen-Admin", "ta@example.org", Roles.TopicAdmin);
            e.Auditor = await e.AddWorkforceUserAsync("Auditor", "au@example.org", Roles.Auditor);
            return e;
        }

        public async Task<string> AddWorkforceUserAsync(string name, string email, params string[] roles)
        {
            var id = Guid.NewGuid().ToString(); var now = DateTime.UtcNow;
            await Raw.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id,@n,@e,'active',1,@t,@t)", new (string, object?)[] { ("id", id), ("n", name), ("e", email), ("t", now) });
            await Raw.ExecuteAsync("INSERT INTO user_identities (provider, subject, user_id, created_at) VALUES (@p,@s,@u,@t)", new (string, object?)[] { ("p", WfIssuer), ("s", "sub-" + id), ("u", id), ("t", now) });
            foreach (var r in new[] { Roles.User }.Concat(roles)) await Raw.ExecuteAsync("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@u,@r,NULL,@t)", new (string, object?)[] { ("u", id), ("r", r), ("t", now) });
            return id;
        }

        public async Task<string> AddGlobalModuleAsync(string name, string? publication = null, bool active = true)
        {
            var id = Guid.NewGuid().ToString();
            await Raw.ExecuteAsync("INSERT INTO modules (id, name, scope, publication_id, global_version, global_status, created_at, updated_at) VALUES (@id,@n,'global',@p,1,@s,@t,@t)",
                new (string, object?)[] { ("id", id), ("n", name), ("p", publication ?? Guid.NewGuid().ToString()), ("s", active ? "active" : "replaced"), ("t", DateTime.UtcNow) });
            return id;
        }

        public async Task<string> AddPrivateModuleAsync(string owner, string name)
        {
            var id = Guid.NewGuid().ToString();
            await Raw.ExecuteAsync("INSERT INTO modules (id, name, owner_user_id, created_at, updated_at) VALUES (@id,@n,@o,@t,@t)", new (string, object?)[] { ("id", id), ("n", name), ("o", owner), ("t", DateTime.UtcNow) });
            return id;
        }

        public Task ShareAsync(string moduleId, string owner, string recipient) =>
            Raw.ExecuteAsync("INSERT INTO module_shares (id, module_id, recipient_user_id, shared_by_user_id, created_at) VALUES (@id,@m,@r,@o,@t)", new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("m", moduleId), ("r", recipient), ("o", owner), ("t", DateTime.UtcNow) });

        /// <summary>Veröffentlichtes Thema mit einem eingefrorenen Modul je Version (optional mit Eigentümer; global oder nicht).</summary>
        public async Task<(string TopicId, string ModuleId)> AddTopicAsync(string name, bool global, int versions = 1, string? owner = null)
        {
            var topicId = Guid.NewGuid().ToString(); var now = DateTime.UtcNow; string lastModule = "";
            await Raw.ExecuteAsync("INSERT INTO topics (id, owner_user_id, name, description, status, draft_revision, published_revision, global_version, created_at, updated_at) VALUES (@t,@o,@n,'','active',1,@v,@g,@now,@now)",
                new (string, object?)[] { ("t", topicId), ("o", owner), ("n", name), ("v", versions), ("g", global ? versions : 0), ("now", now) });
            for (var v = 1; v <= versions; v++)
            {
                var vid = Guid.NewGuid().ToString(); lastModule = Guid.NewGuid().ToString();
                await Raw.ExecuteAsync("INSERT INTO modules (id, name, scope, created_at, updated_at) VALUES (@m,@n,'topic',@now,@now)", new (string, object?)[] { ("m", lastModule), ("n", $"{name} v{v}"), ("now", now) });
                await Raw.ExecuteAsync("INSERT INTO topic_versions (id, topic_id, version_number, status, global_status, name, created_at, published_at) VALUES (@v,@t,@n,'published',@gs,@name,@now,@now)",
                    new (string, object?)[] { ("v", vid), ("t", topicId), ("n", v), ("gs", global && v == versions ? "active" : global ? "superseded" : "none"), ("name", name), ("now", now) });
                await Raw.ExecuteAsync("INSERT INTO topic_version_modules (version_id, module_id, role, position) VALUES (@v,@m,'module',0)", new (string, object?)[] { ("v", vid), ("m", lastModule) });
            }
            return (topicId, lastModule);
        }

        public async Task<string> TopicModuleOfVersionAsync(string topicId, int version) =>
            (await Raw.QueryAsync("SELECT vm.module_id FROM topic_version_modules vm JOIN topic_versions v ON v.id=vm.version_id WHERE v.topic_id=@t AND v.version_number=@n", new (string, object?)[] { ("t", topicId), ("n", version) }))[0]["module_id"]!;

        public async Task<long> CountAsync(string sql, params (string, object?)[] a) => long.Parse((await Raw.QueryAsync(sql, a))[0]["n"]!);
    }

    internal static VerifiedExternalIdentity Id(string email, string? sub = null, bool verified = true, string issuer = ExtIssuer) => new(issuer, sub ?? Guid.NewGuid().ToString(), email, verified);

    public static async Task RunAsync(Action<bool, string> check, string tmp)
    {
        await BatchSemanticsAsync(check, Path.Combine(tmp, "inv-batch"));
        await MigrationAsync(check, Path.Combine(tmp, "inv-migrate"));
        await BestandsschutzAsync(check, Path.Combine(tmp, "inv-bestand"));
        await RegistrationAsync(check, Path.Combine(tmp, "inv-register"));
        await TransactionAsync(check, Path.Combine(tmp, "inv-tx"));
        await AccessAsync(check, Path.Combine(tmp, "inv-access"));
        await AdminOperationsAsync(check, Path.Combine(tmp, "inv-admin"));
    }

    // ─── Atomare Gruppen ───────────────────────────────────────────────

    private static async Task BatchSemanticsAsync(Action<bool, string> check, string dir)
    {
        Directory.CreateDirectory(dir);
        await using var db = new SqliteDatabase(Path.Combine(dir, "b.db"));
        await db.InitAsync();
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS bt (k TEXT PRIMARY KEY, v INTEGER NOT NULL)");
        var ok = await db.ExecuteBatchAsync(new[] { new DbStatement("INSERT INTO bt (k, v) VALUES (@k, 1)", new (string, object?)[] { ("k", "a") }, 1), new DbStatement("INSERT INTO bt (k, v) VALUES (@k, 2)", new (string, object?)[] { ("k", "b") }, 1) });
        check(ok.SequenceEqual(new[] { 1, 1 }) && (await db.QueryAsync("SELECT COUNT(*) AS n FROM bt"))[0]["n"] == "2", "Atomare Gruppe: alle Anweisungen werden gemeinsam übernommen");
        var failed = false;
        try { await db.ExecuteBatchAsync(new[] { new DbStatement("INSERT INTO bt (k, v) VALUES ('c', 3)"), new DbStatement("INSERT INTO bt (k, v) VALUES ('c', 4)") }); }
        catch (InvalidOperationException) { failed = true; }
        check(failed && (await db.QueryAsync("SELECT COUNT(*) AS n FROM bt WHERE k='c'"))[0]["n"] == "0", "Atomare Gruppe: scheitert eine Anweisung, wird alles zurückgerollt (nichts bleibt zurück)");
        DbBatchConflictException? conflict = null;
        try { await db.ExecuteBatchAsync(new[] { new DbStatement("INSERT INTO bt (k, v) VALUES ('d', 5)"), new DbStatement("UPDATE bt SET v=9 WHERE k='gibt-es-nicht'", null, 1) }); }
        catch (DbBatchConflictException ex) { conflict = ex; }
        check(conflict is { Index: 1 } && (await db.QueryAsync("SELECT COUNT(*) AS n FROM bt WHERE k='d'"))[0]["n"] == "0", "Atomare Gruppe: abweichende Zeilenzahl (erwartet 1) → DbBatchConflictException und Rollback");
        check((await db.ExecuteBatchAsync(new[] { new DbStatement("INSERT INTO bt (k, v) VALUES ('e', 6)", null, 1) })).Length == 1 && (await db.QueryAsync("SELECT v FROM bt WHERE k='e'"))[0]["v"] == "6", "Atomare Gruppe: die Verbindung ist nach einem Rollback weiter nutzbar");
        var parallel = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            try { await db.ExecuteBatchAsync(new[] { new DbStatement("INSERT INTO bt (k, v) VALUES ('same', 1)", null, 1) }); return true; } catch (InvalidOperationException) { return false; }
        }));
        check(parallel.Count(x => x) == 1, "Atomare Gruppe: bei parallelen Gruppen mit demselben Schlüssel gewinnt genau eine");
    }

    // ─── Migration ─────────────────────────────────────────────────────

    private static async Task MigrationAsync(Action<bool, string> check, string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "legacy.db");
        // Altbestand: users/user_identities ohne Spalte „kind“, ein Workforce-Benutzer mit Rolle und Identität
        await using (var legacy = new SqliteDatabase(path))
        {
            await legacy.InitAsync();   // legt das aktuelle Schema an; die Spalte wird danach entfernt, um eine Altdatenbank nachzubilden
            await legacy.ExecuteAsync("DROP TABLE user_identities");
            await legacy.ExecuteAsync("CREATE TABLE user_identities (provider TEXT NOT NULL, subject TEXT NOT NULL, user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE, created_at TEXT NOT NULL, PRIMARY KEY (provider, subject))");
            await legacy.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES ('11111111-1111-1111-1111-111111111111','Alt','alt@example.org','active',1,'2026-01-01T00:00:00.000Z','2026-01-01T00:00:00.000Z')");
            await legacy.ExecuteAsync("INSERT INTO user_identities (provider, subject, user_id, created_at) VALUES ('https://wf.example/v2.0','alt-sub','11111111-1111-1111-1111-111111111111','2026-01-01T00:00:00.000Z')");
            await legacy.ExecuteAsync("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES ('11111111-1111-1111-1111-111111111111','admin','idp','2026-01-01T00:00:00.000Z')");
        }
        await using var db = new SqliteDatabase(path);
        await db.InitAsync();
        var kind = (await db.QueryAsync("SELECT kind FROM user_identities WHERE subject='alt-sub'"))[0]["kind"];
        check(kind == "workforce", "Migration: bestehende Identität erhält Art „workforce“ (DEFAULT), Daten bleiben erhalten");
        var tables = await db.QueryAsync("SELECT name FROM sqlite_master WHERE type='table'");
        var names = tables.Select(t => t["name"]).ToHashSet();
        check(new[] { "invitations", "invitation_module_grants", "invitation_topic_grants", "invitation_deliveries", "invitation_meta", "invitation_events", "user_profiles", "module_grants" }.All(names.Contains),
            "Migration: die neuen Tabellen für Einladungen, Profile und Modulzuweisungen entstehen aus dem Altbestand");
        await db.ExecuteAsync("INSERT INTO invitations (invitation_id, recipient_email, token_hash, target_profile, created_by, created_at, expires_at) VALUES ('22222222-2222-2222-2222-222222222222','x@example.org','h','B2C_LEARNER','11111111-1111-1111-1111-111111111111','2026-01-01T00:00:00.000Z','2027-01-01T00:00:00.000Z')");
        await db.InitAsync();   // zweiter Lauf: idempotent, nichts geht verloren
        check((await db.QueryAsync("SELECT COUNT(*) AS n FROM invitations"))[0]["n"] == "1" && (await db.QueryAsync("SELECT COUNT(*) AS n FROM users"))[0]["n"] == "1"
              && (await db.QueryAsync("SELECT role FROM user_roles WHERE user_id='11111111-1111-1111-1111-111111111111' AND role='admin'")).Count == 1,
            "Migration: zweiter Lauf ist idempotent; Benutzer, Rollen und Einladungen bleiben unverändert");
    }

    // ─── Bestandsschutz: Bestandsprofil ────────────────────────────────

    private static async Task BestandsschutzAsync(Action<bool, string> check, string dir)
    {
        var e = await Env.CreateAsync(dir);
        var owner = await e.AddWorkforceUserAsync("Eigentümer", "owner@example.org");
        var reader = await e.AddWorkforceUserAsync("Leser", "reader@example.org");
        var sharedWith = await e.AddWorkforceUserAsync("Empfänger", "recv@example.org");
        var priv = await e.AddPrivateModuleAsync(owner, "Privat");
        var shared = await e.AddPrivateModuleAsync(owner, "Geteilt"); await e.ShareAsync(shared, owner, sharedWith);
        var global = await e.AddGlobalModuleAsync("Global");
        var globalOff = await e.AddGlobalModuleAsync("Zurückgezogen", active: false);
        var (globalTopic, globalTopicModule) = await e.AddTopicAsync("Globales Thema", global: true);

        async Task<string> Acc(string user, string module) => (await e.Repo.ResolveModuleAccessAsync(user, module)) is { } a ? a.Context + (a.CanEdit ? "+edit" : "") : "none";
        check(await e.Repo.GetLearnerProfileAsync(owner) is null && !await e.Repo.IsRestrictedLearnerAsync(owner) && !await e.Repo.IsRestrictedLearnerAsync(e.Admin), "Bestandsprofil: Workforce-Benutzer ohne Profileintrag sind nicht eingeschränkt");
        check(await Acc(owner, priv) == "private+edit" && await Acc(reader, priv) == "none" && await Acc(sharedWith, shared) == "shared" && await Acc(reader, shared) == "none",
            "Bestandsprofil: eigenes Modul, fremdes privates Modul und persönliche Freigabe verhalten sich wie vor Phase B");
        check(await Acc(reader, global) == "global" && await Acc(reader, globalOff) == "none" && await Acc(e.Admin, priv) == "none" && await Acc(e.Auditor, priv) == "none",
            "Bestandsprofil: aktive globale Module ohne Zuweisung lesbar, zurückgezogene nicht; kein „Admin liest alles“");
        check(await Acc(reader, globalTopicModule) == "topic", "Bestandsprofil: globale Themen bleiben ohne Zuweisung lernbar");
        check((await e.Repo.ListGlobalModulesAsync(reader)).Any(m => m.Id == global) && (await e.Repo.ListGlobalModulesAsync(reader)).Count == (await e.Repo.ListGlobalModulesAsync()).Count
              && (await e.Repo.ListSharedWithMeAsync(sharedWith)).Count == 1, "Bestandsprofil: Listen globaler und geteilter Module unverändert");
        var created = await e.Repo.CreateModuleAsync(new ModuleCreateRequest { Name = "Neu vom Bestandsbenutzer" }, reader);
        check(created.Id.Length > 0, "Bestandsprofil: eigene Module können weiter angelegt werden");
        check((await e.Topics.ListOverviewAsync(reader)).Global.Any(s => s.Topic.Id == globalTopic), "Bestandsprofil: globales Thema erscheint in der Themenübersicht");
        check(new[] { e.Admin, e.TopicAdmin, e.Auditor }.All(async_ => true)
              && (await e.Users.GetAsync(e.Admin))!.IsAdmin && (await e.Users.GetAsync(e.TopicAdmin))!.IsTopicAdmin && (await e.Users.GetAsync(e.Auditor))!.IsAuditor,
            "Bestandsprofil: Rollen Admin, Themen-Admin und Auditor bleiben unverändert");

        // Zuweisungen an Bestandsbenutzer: möglich, aber ohne Änderung des Zugriffsmodells
        var before = await Acc(reader, global);
        await e.Learners.AssignModuleAsync(e.Admin, reader, global);
        var topicAssign = await e.Learners.AssignTopicAsync(e.TopicAdmin, reader, globalTopic, 1, mandatory: true, dueAt: DateTime.UtcNow.AddDays(10));
        check(await Acc(reader, global) == before && await Acc(reader, priv) == "none" && !await e.Repo.IsRestrictedLearnerAsync(reader) && topicAssign.Mandatory,
            "Bestandsprofil: Modul- und Themenzuweisung (auch Pflicht) ändern weder Profil noch Zugriff und entziehen nichts");
        var overview = await e.Learners.GetOverviewAsync(e.Admin, reader);
        check(overview is { Profile: null, HasWorkforceIdentity: true, HasExternalIdentity: false } && overview.ModuleGrants.Count == 1 && overview.TopicAssignments.Count == 1 && (await e.Users.GetAsync(reader))!.Roles.SequenceEqual(new[] { Roles.User }),
            "Bestandsprofil: Übersicht zeigt Profil „Bestand“, Zuweisungen; es werden keine Rollen vergeben");
        await e.Learners.RevokeModuleAsync(e.Admin, reader, global);
        check(await Acc(reader, global) == "global", "Bestandsprofil: Widerruf einer Zuweisung entzieht das bisherige globale Leserecht nicht");
    }

    // ─── Einladung und atomare Einlösung ───────────────────────────────

    private static async Task RegistrationAsync(Action<bool, string> check, string dir)
    {
        var e = await Env.CreateAsync(dir);
        var global = await e.AddGlobalModuleAsync("A");
        var privateModule = await e.AddPrivateModuleAsync(e.Admin, "Privat");
        var learner = await e.AddWorkforceUserAsync("Lernende", "bekannt@example.org");

        async Task<bool> Throws<T>(Func<Task> f) where T : Exception { try { await f(); return false; } catch (T) { return true; } }
        check(await Throws<UnauthorizedAccessException>(() => e.Invites.CreateAsync(learner, "a@example.org", "B2C_LEARNER")) && await Throws<UnauthorizedAccessException>(() => e.Invites.CreateAsync(e.TopicAdmin, "a@example.org", "B2C_LEARNER"))
              && await Throws<UnauthorizedAccessException>(() => e.Invites.CreateAsync(e.Auditor, "a@example.org", "B2C_LEARNER")), "Einladung: nur Administratoren (Rolle gegen die Datenbank) erstellen Einladungen");
        check(await Throws<ArgumentException>(() => e.Invites.CreateAsync(e.Admin, "a@example.org", "admin")) && await Throws<ArgumentException>(() => e.Invites.CreateAsync(e.Admin, "a@example.org", Roles.Admin))
              && await Throws<ArgumentException>(() => e.Invites.CreateAsync(e.Admin, "a@example.org", "themen_admin")) && !Roles.All.Contains(LearnerProfiles.B2cLearner) && !Roles.All.Contains(LearnerProfiles.B2bLearner),
            "Einladung: nie für Administrator-, Auditor- oder Themen-Admin-Rollen; Lernprofile sind keine Rollen");
        check(await Throws<ArgumentException>(() => e.Invites.CreateAsync(e.Admin, "a@example.org", "B2C_LEARNER", new[] { privateModule })) && await Throws<ArgumentException>(() => e.Invites.CreateAsync(e.Admin, "a@example.org", "B2C_LEARNER", new[] { "kein-guid" })),
            "Einladung: nur aktive globale Module sind zuweisbar");
        check(await Throws<AccountExistsException>(() => e.Invites.CreateAsync(e.Admin, "Bekannt@Example.org", "B2C_LEARNER")), "Einladung: für eine Adresse mit vorhandenem Konto wird keine Einladung erstellt (Groß-/Kleinschreibung egal)");

        var inv = await e.Invites.CreateAsync(e.Admin, "Neu@Example.org", "B2C_LEARNER", new[] { global }, null, new InvitationMeta("Neue Lernende"));
        check(inv.Link == "https://learny.test/invite#" + inv.Token && inv.Token.Length == 43, "Einladung: Link /invite#<token> (Fragment), 256-Bit-Token");
        var stored = await e.Raw.QueryAsync("SELECT token_hash FROM invitations WHERE invitation_id=@i", new (string, object?)[] { ("i", inv.InvitationId) });
        var dump = string.Join("|", (await e.Raw.QueryAsync("SELECT * FROM invitation_events")).SelectMany(r => r.Values)) + string.Join("|", (await e.Raw.QueryAsync("SELECT * FROM audit_log")).SelectMany(r => r.Values))
                   + string.Join("|", (await e.Raw.QueryAsync("SELECT * FROM invitations")).SelectMany(r => r.Values)) + string.Join("|", (await e.Raw.QueryAsync("SELECT * FROM invitation_meta")).SelectMany(r => r.Values));
        check(stored[0]["token_hash"]!.Length == 64 && stored[0]["token_hash"] != inv.Token && !dump.Contains(inv.Token), "Einladung: gespeichert wird nur der HMAC (kein Klartexttoken in Einladung, Ereignissen oder Audit)");

        // Fehlerfälle der Einlösung
        var users0 = await e.CountAsync("SELECT COUNT(*) AS n FROM users");
        async Task<RedeemOutcome> Redeem(string? token, VerifiedExternalIdentity i) => (await e.Invites.RedeemAsync(token, i)).Outcome;
        check(await Redeem(null, Id("neu@example.org")) == RedeemOutcome.MissingToken && await Redeem("unbekannter-token-0123456789abcdef0123", Id("neu@example.org")) == RedeemOutcome.UnknownToken
              && await Redeem(inv.Token, Id("neu@example.org", verified: false)) == RedeemOutcome.EmailNotVerified && await Redeem(inv.Token, Id("andere@example.org")) == RedeemOutcome.EmailMismatch
              && await Redeem(inv.Token, Id(null!)) == RedeemOutcome.EmailMismatch && await Redeem(inv.Token, new VerifiedExternalIdentity("", "s", "neu@example.org", true)) == RedeemOutcome.InvalidIdentity,
            "Einlösung: fehlender/unbekannter Token, unbestätigte oder falsche E-Mail, ungültige Identität werden abgewiesen");
        check(await e.CountAsync("SELECT COUNT(*) AS n FROM users") == users0 && await e.CountAsync("SELECT COUNT(*) AS n FROM user_profiles") == 0 && await e.CountAsync("SELECT COUNT(*) AS n FROM invitations WHERE status='pending'") == 1,
            "Einlösung: Ablehnungen legen nichts an; die Einladung bleibt gültig");
        var events = (await e.Events.ListAsync(inv.InvitationId)).Where(x => x.Type == InviteEventTypes.RegistrationRejected).Select(x => x.Category).ToList();
        check(events.Contains("EmailNotVerified") && events.Contains("EmailMismatch"), "Einlösung: Ablehnungsgründe stehen im Ereignisprotokoll (nur Kategorie)");
        check(await Redeem(inv.Token, new VerifiedExternalIdentity(WfIssuer, "sub-" + learner, "neu@example.org", true)) == RedeemOutcome.IdentityAlreadyRegistered, "Einlösung: bestehende Identität (Issuer+sub) wird abgewiesen");

        // Erfolg
        var subject = "ext-sub-1";
        var ok = await e.Invites.RedeemAsync(inv.Token, Id("NEU@example.org", subject));
        var uid = ok.UserId!;
        check(ok.Success && ok.Profile == "B2C_LEARNER" && await e.Repo.GetLearnerProfileAsync(uid) == "B2C_LEARNER", "Einlösung: gültige Einladung → Benutzer mit Profil aus dem Einladungsdatensatz");
        var user = await e.Users.GetAsync(uid);
        check(user!.DisplayName == "Neue Lernende" && user.Email == "neu@example.org" && user.Roles.SequenceEqual(new[] { Roles.User }) && await e.Users.GetIdentityKindAsync(ExtIssuer, subject) == IdentityKinds.External,
            "Einlösung: Anzeigename aus der Einladung, nur Rolle user, Identität der Art external");
        check(await e.CountAsync("SELECT COUNT(*) AS n FROM module_grants WHERE user_id=@u AND module_id=@m AND revoked_at IS NULL AND source_invitation_id=@i", ("u", uid), ("m", global), ("i", inv.InvitationId)) == 1
              && await e.CountAsync("SELECT COUNT(*) AS n FROM invitations WHERE invitation_id=@i AND status='accepted' AND accepted_user_id=@u", ("i", inv.InvitationId), ("u", uid)) == 1, "Einlösung: Modulzuweisung und Einladungsstatus entstehen gemeinsam");
        var evNames = (await e.Events.ListAsync(inv.InvitationId)).Select(x => x.Type).ToHashSet();
        check(new[] { InviteEventTypes.Created, InviteEventTypes.Accepted, InviteEventTypes.UserCreated, InviteEventTypes.ProfileAssigned, InviteEventTypes.ContentAssigned }.All(evNames.Contains), "Einlösung: Ereignisse erstellt, eingelöst, Benutzer angelegt, Profil und Lernrechte zugeordnet");
        check(await Redeem(inv.Token, Id("neu@example.org", "ext-sub-zwei")) == RedeemOutcome.AlreadyUsed && await e.CountAsync("SELECT COUNT(*) AS n FROM users WHERE LOWER(email)='neu@example.org'") == 1, "Einlösung: höchstens einmal — die zweite Einlösung wird abgewiesen");

        // Widerruf, Ablauf, Rotation
        var revoked = await e.Invites.CreateAsync(e.Admin, "w@example.org", "B2B_LEARNER");
        check(await e.Invites.RevokeAsync(e.Admin, revoked.InvitationId) && await Redeem(revoked.Token, Id("w@example.org")) == RedeemOutcome.Revoked && !await e.Invites.RevokeAsync(e.Admin, revoked.InvitationId), "Einlösung: widerrufene Einladung nicht einlösbar (Widerruf idempotent)");
        var expiring = await e.Invites.CreateAsync(e.Admin, "e@example.org", "B2B_LEARNER", null, null, new InvitationMeta(ValidityHours: 1));
        e.Now = e.Now.AddHours(2);
        check(await Redeem(expiring.Token, Id("e@example.org")) == RedeemOutcome.Expired && !await e.Invites.IsRedeemableAsync(expiring.Token), "Einlösung: abgelaufene Einladung nicht einlösbar");
        e.Now = e.Now.AddHours(-2);
        var rot = await e.Invites.CreateAsync(e.Admin, "r@example.org", "B2B_LEARNER");
        var rotated = await e.Invites.RotateAsync(e.Admin, rot.InvitationId);
        check(rotated is not null && rotated.Token != rot.Token && await Redeem(rot.Token, Id("r@example.org")) == RedeemOutcome.UnknownToken && await Redeem(rotated.Token, Id("r@example.org")) == RedeemOutcome.Success,
            "Einlösung: nach der Rotation ist der alte Link wirkungslos, der neue löst genau diese Einladung ein");

        // Manipulierte Daten und Konten mit gleicher Adresse
        var tampered = await e.Invites.CreateAsync(e.Admin, "t@example.org", "B2C_LEARNER");
        await e.Raw.ExecuteAsync("UPDATE invitations SET target_profile='admin' WHERE invitation_id=@i", new (string, object?)[] { ("i", tampered.InvitationId) });
        var usersBeforeTamper = await e.CountAsync("SELECT COUNT(*) AS n FROM users");
        check(await Redeem(tampered.Token, Id("t@example.org")) == RedeemOutcome.ProvisioningFailed && await e.CountAsync("SELECT COUNT(*) AS n FROM users") == usersBeforeTamper && await e.CountAsync("SELECT COUNT(*) AS n FROM invitations WHERE invitation_id=@i AND status='pending'", ("i", tampered.InvitationId)) == 1,
            "Einlösung: manipuliertes Profil (z. B. admin) wird nie als Rolle vergeben — nichts angelegt");
        var late = await e.Invites.CreateAsync(e.Admin, "late@example.org", "B2C_LEARNER");
        await e.AddWorkforceUserAsync("Später angelegt", "late@example.org");
        check(await Redeem(late.Token, Id("late@example.org")) == RedeemOutcome.IdentityAlreadyRegistered, "Einlösung: ein inzwischen vorhandenes Konto mit derselben Adresse verhindert eine zweite Identität (keine Verknüpfung über die E-Mail)");

        // Parallele Einlösung
        var par = await e.Invites.CreateAsync(e.Admin, "par@example.org", "B2C_LEARNER", new[] { global });
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => e.Invites.RedeemAsync(par.Token, Id("par@example.org", "par-sub-" + i))));
        check(results.Count(r => r.Success) == 1 && await e.CountAsync("SELECT COUNT(*) AS n FROM users WHERE LOWER(email)='par@example.org'") == 1 && await e.CountAsync("SELECT COUNT(*) AS n FROM user_identities WHERE subject LIKE 'par-sub-%'") == 1
              && await e.CountAsync("SELECT COUNT(*) AS n FROM module_grants g JOIN user_identities i ON i.user_id=g.user_id WHERE i.subject LIKE 'par-sub-%'") == 1,
            "Einlösung: acht parallele Einlösungen derselben Einladung → genau ein Benutzer mit genau einer Identität und einer Zuweisung");
        var sameId = await e.Invites.CreateAsync(e.Admin, "x1@example.org", "B2C_LEARNER"); var sameId2 = await e.Invites.CreateAsync(e.Admin, "x1@example.org", "B2C_LEARNER");
        var twin = await Task.WhenAll(e.Invites.RedeemAsync(sameId.Token, Id("x1@example.org", "twin-sub")), e.Invites.RedeemAsync(sameId2.Token, Id("x1@example.org", "twin-sub")));
        check(twin.Count(r => r.Success) == 1 && await e.CountAsync("SELECT COUNT(*) AS n FROM user_identities WHERE subject='twin-sub'") == 1,
            "Einlösung: dieselbe Identität über zwei Einladungen parallel → genau eine Registrierung");
    }

    // ─── Transaktionssicherheit ────────────────────────────────────────

    private static async Task TransactionAsync(Action<bool, string> check, string dir)
    {
        var e = await Env.CreateAsync(dir);
        var m1 = await e.AddGlobalModuleAsync("M1"); var m2 = await e.AddGlobalModuleAsync("M2");
        var (topic, _) = await e.AddTopicAsync("Thema", global: true);
        async Task<string> Snapshot() => string.Join(",", await Task.WhenAll(new[] { "users", "user_identities", "user_roles", "user_profiles", "module_grants", "topic_assignments" }.Select(async t => await e.CountAsync($"SELECT COUNT(*) AS n FROM {t}"))));
        var inv = await e.Invites.CreateAsync(e.Admin, "tx@example.org", "B2C_LEARNER", new[] { m1, m2 }, new[] { new InvitationTopicGrant(topic, 1, Mandatory: true, DueAt: e.Now.AddDays(5)) });
        var baseline = await Snapshot();
        var statementCount = 5 + 2 + 1 + 4 + 1;   // Anspruch, Benutzer, Identität, Rolle, Profil | 2 Module | 1 Thema | 4 Ereignisse | Audit
        var allClean = true;
        for (var k = 0; k <= statementCount; k++)   // an jeder Stelle der Einlösung ein Fehler
        {
            e.Db.FailAt = k;
            var r = await e.Invites.RedeemAsync(inv.Token, Id("tx@example.org", "tx-sub"));
            allClean &= r.Outcome == RedeemOutcome.ProvisioningFailed && await Snapshot() == baseline && await e.CountAsync("SELECT COUNT(*) AS n FROM invitations WHERE invitation_id=@i AND status='pending' AND accepted_user_id IS NULL", ("i", inv.InvitationId)) == 1;
        }
        check(allClean, $"Transaktion: ein Fehler an jeder der {statementCount + 1} Stellen der Einlösung hinterlässt weder Benutzer, Identität, Profil, Rechte noch einen verbrauchten Anspruch");
        check(await e.Invites.IsRedeemableAsync(inv.Token), "Transaktion: die Einladung bleibt nach Fehlern gültig und einlösbar");
        var good = await e.Invites.RedeemAsync(inv.Token, Id("tx@example.org", "tx-sub"));
        check(good.Success && await e.CountAsync("SELECT COUNT(*) AS n FROM module_grants WHERE user_id=@u", ("u", good.UserId!)) == 2
              && (await e.Raw.QueryAsync("SELECT mandatory, topic_version, status FROM topic_assignments WHERE user_id=@u", new (string, object?)[] { ("u", good.UserId!) })) is [{ } a] && a["mandatory"] == "1" && a["topic_version"] == "1" && a["status"] == "active",
            "Transaktion: der Wiederholungsversuch ohne Fehler legt alles vollständig an (Module, Thema in fester Version, Pflicht aus der Einladung)");

        // Verbindungsabbruch beim Commit: Ausgang wird nachgeprüft
        var inv2 = await e.Invites.CreateAsync(e.Admin, "commit@example.org", "B2B_LEARNER");
        e.Db.ThrowAfterCommit = true;
        var unknownOutcome = await e.Invites.RedeemAsync(inv2.Token, Id("commit@example.org", "commit-sub"));
        check(unknownOutcome.Success && await e.CountAsync("SELECT COUNT(*) AS n FROM users WHERE LOWER(email)='commit@example.org'") == 1,
            "Transaktion: Verbindungsabbruch nach dem Commit → der Ausgang wird nachgeprüft und als Erfolg erkannt (idempotent, kein zweiter Benutzer)");
        check((await e.Invites.RedeemAsync(inv2.Token, Id("commit@example.org", "commit-sub"))).Outcome == RedeemOutcome.AlreadyUsed || (await e.Invites.RedeemAsync(inv2.Token, Id("commit@example.org", "commit-sub"))).Outcome == RedeemOutcome.IdentityAlreadyRegistered,
            "Transaktion: wiederholter Callback nach erfolgreicher Registrierung wird abgewiesen");
        // Prozessneustart: neue Dienstinstanzen auf derselben Datenbank sehen denselben Zustand
        var restarted = InvitationService.Create(e.Raw, new UserService(e.Raw, e.Cfg), e.Cfg.Invitations, new InviteEvents(e.Raw));
        check((await restarted.RedeemAsync(inv.Token, Id("tx@example.org", "tx-sub-2"))).Outcome == RedeemOutcome.AlreadyUsed, "Transaktion: nach einem Neustart (neue Dienstinstanz) bleibt die Einladung verbraucht");
    }

    // ─── Zugriffsregeln der Lernprofile ────────────────────────────────

    private static async Task AccessAsync(Action<bool, string> check, string dir)
    {
        var e = await Env.CreateAsync(dir);
        var pubA = Guid.NewGuid().ToString();
        var modA = await e.AddGlobalModuleAsync("Modul A", pubA); var modB = await e.AddGlobalModuleAsync("Modul B");
        var author = await e.AddWorkforceUserAsync("Themenautor", "author@example.org");
        var (topicG, topicGModule) = await e.AddTopicAsync("Globales Thema", global: true, owner: author);
        var (topicP, topicPModule) = await e.AddTopicAsync("Zugewiesenes Thema", global: false, owner: e.Admin);
        var (topicV, _) = await e.AddTopicAsync("Versioniert", global: false, versions: 2, owner: e.Admin);

        async Task<string> NewLearner(string email, string profile, params string[] modules)
        {
            var inv = await e.Invites.CreateAsync(e.Admin, email, profile, modules);
            return (await e.Invites.RedeemAsync(inv.Token, Id(email))).UserId!;
        }
        async Task<string> Acc(string user, string module) => (await e.Repo.ResolveModuleAccessAsync(user, module)) is { } a ? a.Context : "none";

        var b2c = await NewLearner("b2c@example.org", "B2C_LEARNER", modA);
        var b2b = await NewLearner("b2b@example.org", "B2B_LEARNER", modA);
        check(await Acc(b2c, modA) == "global" && await Acc(b2c, modB) == "none" && await Acc(b2b, modA) == "global" && await Acc(b2b, modB) == "none", "B2C/B2B: Zugriff nur auf das zugewiesene Modul A, nicht auf Modul B");
        check(await e.Repo.IsRestrictedLearnerAsync(b2c) && await Acc(b2c, Guid.NewGuid().ToString()) == "none" && await Acc(b2c, "kein-guid") == "none", "B2C: unbekannte oder manipulierte Modul-IDs ergeben keinen Zugriff");
        var visible = await e.Repo.ListGlobalModulesAsync(b2c);
        check(visible.Count == 1 && visible[0].Id == modA && (await e.Repo.ListGlobalModulesAsync()).Count == 2, "B2C: die Liste globaler Module zeigt nur zugewiesene Module (keine Metadaten anderer)");
        check(await Acc(b2c, topicGModule) == "none" && await Acc(b2c, topicPModule) == "none" && (await e.Topics.ListOverviewAsync(b2c)) is { } ov0 && ov0.Global.Count == 0 && ov0.Own.Count == 0 && ov0.Shared.Count == 0 && ov0.Assigned.Count == 0,
            "B2C: ein globales Thema ist ohne ausdrückliche Zuweisung weder lernbar noch sichtbar");

        // Zuweisung, Entzug
        await e.Learners.AssignModuleAsync(e.Admin, b2c, modB);
        check(await Acc(b2c, modB) == "global", "B2C: ausdrückliche Modulzuweisung ermöglicht den Zugriff sofort");
        check(await e.Learners.RevokeModuleAsync(e.Admin, b2c, modB) && await Acc(b2c, modB) == "none" && await Acc(b2c, modA) == "global", "B2C: Widerruf entzieht den Zugriff sofort, andere Zuweisungen bleiben");
        await e.Raw.ExecuteAsync("UPDATE modules SET global_status='deactivated' WHERE id=@m", new (string, object?)[] { ("m", modA) });
        check(await Acc(b2c, modA) == "none", "B2C: auch mit Zuweisung kein Zugriff auf ein zurückgezogenes globales Modul");
        await e.Raw.ExecuteAsync("UPDATE modules SET global_status='active' WHERE id=@m", new (string, object?)[] { ("m", modA) });
        var modA2 = await e.AddGlobalModuleAsync("Modul A v2", pubA);   // neue Version derselben Veröffentlichung
        await e.Raw.ExecuteAsync("UPDATE modules SET global_status='replaced' WHERE id=@m", new (string, object?)[] { ("m", modA) });
        check(await Acc(b2c, modA2) == "global" && await Acc(b2c, modA) == "none", "B2C: die Zuweisung folgt der Veröffentlichung auf die aktive globale Version");

        // Eigenes, geteiltes
        var sharer = await e.AddWorkforceUserAsync("Teilende", "share@example.org");
        var shared = await e.AddPrivateModuleAsync(sharer, "Geteiltes Modul"); await e.ShareAsync(shared, sharer, b2c);
        check(await Acc(b2c, shared) == "none" && (await e.Repo.ListSharedWithMeAsync(b2c)).Count == 0, "B2C: „geteilt“ allein genügt nicht (keine Zuweisung → kein Zugriff, keine Anzeige)");
        await e.Raw.ExecuteAsync("INSERT INTO module_grants (user_id, module_id, granted_at) VALUES (@u,@m,@t)", new (string, object?)[] { ("u", b2c), ("m", shared), ("t", DateTime.UtcNow) });
        check(await Acc(b2c, shared) == "shared", "B2C: Freigabe des Eigentümers plus ausdrückliche Zuweisung ermöglicht den Zugriff");
        var ownModule = await e.AddPrivateModuleAsync(b2c, "Eigenes");
        var createFailed = false; try { await e.Repo.CreateModuleAsync(new ModuleCreateRequest { Name = "Versuch" }, b2c); } catch (UnauthorizedAccessException) { createFailed = true; }
        var topicFailed = false; try { await e.Topics.CreateAsync(b2c, "Versuch", null); } catch (UnauthorizedAccessException) { topicFailed = true; }
        check(await Acc(b2c, ownModule) == "none" && createFailed && topicFailed, "B2C: keine eigenen Module oder Themen (Anlegen verweigert, Eigentum gibt keinen Zugriff)");

        // Themen: ausdrückliche Zuweisung an eine konkrete Version
        var asg = await e.Learners.AssignTopicAsync(e.TopicAdmin, b2c, topicG, 1, mandatory: false);
        check(await Acc(b2c, topicGModule) == "topic" && !asg.Mandatory && await Acc(b2c, topicPModule) == "none", "B2C: zugewiesenes (freiwilliges) globales Thema wird lernbar, andere Themen nicht");
        var ov = await e.Topics.ListOverviewAsync(b2c);
        check(ov.Assigned.Count == 1 && ov.Mandatory.Count == 0 && ov.Global.Count == 0, "B2C: Themenübersicht zeigt nur das zugewiesene Thema; ohne ausdrückliche Pflichtzuweisung keine Pflicht");
        await e.Learners.AssignTopicAsync(e.TopicAdmin, b2c, topicG, 1, mandatory: true, dueAt: DateTime.UtcNow.AddDays(3));
        check((await e.Topics.ListOverviewAsync(b2c)).Mandatory.Count == 1, "B2C: Pflicht entsteht nur durch die ausdrückliche mandatory-Zuweisung (ADR 0029)");
        check(await e.Learners.RevokeTopicAssignmentAsync(e.TopicAdmin, asg.Id) && await Acc(b2c, topicGModule) == "none" && (await e.Topics.ListOverviewAsync(b2c)).Assigned.Count == 0, "B2C: Entzug der Themenzuweisung beendet das Lernrecht sofort");
        await e.Learners.AssignTopicAsync(e.Admin, b2c, topicV, 1, mandatory: false);   // eigenes Thema des Administrators: alle veröffentlichten Versionen zuweisbar
        var v1Module = await e.TopicModuleOfVersionAsync(topicV, 1); var v2Module = await e.TopicModuleOfVersionAsync(topicV, 2);
        check(await Acc(b2c, v1Module) == "topic" && await Acc(b2c, v2Module) == "none", "B2C: die Zuweisung gilt für die konkrete Version (Version 1 ja, die neuere Version 2 nein)");
        var inviteWithTopic = await e.Invites.CreateAsync(e.Admin, "topic@example.org", "B2B_LEARNER", null, new[] { new InvitationTopicGrant(topicP, null, Mandatory: true, DueAt: DateTime.UtcNow.AddDays(7)) });
        var viaInvite = (await e.Invites.RedeemAsync(inviteWithTopic.Token, Id("topic@example.org"))).UserId!;
        check(await Acc(viaInvite, topicPModule) == "topic" && (await e.Topics.ListOverviewAsync(viaInvite)).Mandatory.Count == 1, "B2B: Themenzuweisung aus der Einladung (Pflicht, Version festgelegt) wirkt nach der Registrierung");

        // Externe Identität ohne Profileintrag: nie Bestandsberechtigung
        var orphan = await e.AddWorkforceUserAsync("Waise", "orphan@example.org");
        await e.Raw.ExecuteAsync("UPDATE user_identities SET kind='external' WHERE user_id=@u", new (string, object?)[] { ("u", orphan) });
        check(await e.Repo.IsRestrictedLearnerAsync(orphan) && await Acc(orphan, modA2) == "none" && await Acc(orphan, topicGModule) == "none", "Fail-closed: externe Identität ohne Profileintrag erhält keine Bestandsberechtigung");

        // Zugewiesene Rechte verleihen keine Rollen
        check((await e.Users.GetAsync(b2c))!.Roles.SequenceEqual(new[] { Roles.User }) && (await e.Users.GetAsync(b2b))!.Roles.SequenceEqual(new[] { Roles.User }), "Zuweisungen und Profile verleihen keine Rollen");
        var denied = false; try { await e.Learners.AssignModuleAsync(b2c, b2b, modA2); } catch (UnauthorizedAccessException) { denied = true; }
        check(denied, "Rechteentzug/-vergabe: eine externe Identität kann sich oder anderen nichts zuweisen (Rollenprüfung gegen die Datenbank)");
    }

    // ─── Verwaltungsoperationen ────────────────────────────────────────

    private static async Task AdminOperationsAsync(Action<bool, string> check, string dir)
    {
        var e = await Env.CreateAsync(dir);
        var mod = await e.AddGlobalModuleAsync("M"); var priv = await e.AddPrivateModuleAsync(e.Admin, "P");
        var target = await e.AddWorkforceUserAsync("Ziel", "ziel@example.org");
        async Task<bool> Denied(Func<Task> f) { try { await f(); return false; } catch (UnauthorizedAccessException) { return true; } }
        check(await Denied(() => e.Learners.AssignModuleAsync(e.TopicAdmin, target, mod)) && await Denied(() => e.Learners.AssignModuleAsync(e.Auditor, target, mod)) && await Denied(() => e.Learners.AssignModuleAsync(target, target, mod))
              && await Denied(() => e.Learners.RevokeModuleAsync(e.TopicAdmin, target, mod)) && await Denied(() => e.Learners.ListExternalLearnersAsync(e.Auditor)) && await Denied(() => e.Invites.ListAsync(e.TopicAdmin)) && await Denied(() => e.Invites.RevokeAsync(target, Guid.NewGuid().ToString())),
            "Verwaltung: Modulzuweisung und Einladungen nur für Administratoren (Rolle gegen die Datenbank), Lernendenübersicht für Admin und Themen-Admin");
        var bad = false; try { await e.Learners.AssignModuleAsync(e.Admin, target, priv); } catch (ArgumentException) { bad = true; }
        var nobody = false; try { await e.Learners.AssignModuleAsync(e.Admin, Guid.NewGuid().ToString(), mod); } catch (KeyNotFoundException) { nobody = true; }
        check(bad && nobody, "Verwaltung: nur aktive globale Module und vorhandene aktive Benutzer sind zuweisbar");
        await e.Learners.AssignModuleAsync(e.Admin, target, mod); await e.Learners.AssignModuleAsync(e.Admin, target, mod);
        check(await e.CountAsync("SELECT COUNT(*) AS n FROM module_grants WHERE user_id=@u", ("u", target)) == 1, "Verwaltung: wiederholtes Zuweisen ist idempotent (eine Zeile)");
        check(await e.Learners.RevokeModuleAsync(e.Admin, target, mod) && !await e.Learners.RevokeModuleAsync(e.Admin, target, mod) && (await e.Learners.ListModuleGrantsAsync(e.Admin, target))[0] is { Active: false, RevokedAt: not null },
            "Verwaltung: Widerruf bleibt nachvollziehbar (revoked_at), wiederholter Widerruf ändert nichts");
        await e.Learners.AssignModuleAsync(e.Admin, target, mod);
        check((await e.Learners.ListModuleGrantsAsync(e.Admin, target))[0].Active, "Verwaltung: erneutes Zuweisen reaktiviert einen widerrufenen Eintrag");
        var inv = await e.Invites.CreateAsync(e.Admin, "liste@example.org", "B2B_LEARNER", new[] { mod });
        var list = await e.Invites.ListAsync(e.Admin);
        check(list.Count == 1 && list[0].Status == "pending" && list[0].DeliveryStatus == "pending" && list[0].ModuleCount == 1 && list[0].InvitationId == inv.InvitationId && (await e.Invites.ListAsync(e.Admin, new InvitationFilter("accepted"))).Count == 0 && (await e.Invites.ListAsync(e.Admin, new InvitationFilter(null, "LISTE"))).Count == 1,
            "Verwaltung: Einladungsübersicht mit Status, Versandstatus und Zuweisungen (Filter nach Status und Adresse)");
        var learner = (await e.Invites.RedeemAsync(inv.Token, Id("liste@example.org"))).UserId!;
        var learners = await e.Learners.ListExternalLearnersAsync(e.TopicAdmin);
        var ov = await e.Learners.GetOverviewAsync(e.Admin, learner);
        check(learners.Count == 1 && learners[0].Profile == "B2B_LEARNER" && ov is { Profile: "B2B_LEARNER", HasExternalIdentity: true, HasWorkforceIdentity: false } && ov.ModuleGrants.Count == 1 && (await e.Learners.GetOverviewAsync(e.Admin, "kein-guid")) is null,
            "Verwaltung: Lernendenübersicht und Benutzerstatus zeigen Profil, Identitätsart und Zuweisungen");
        var events = (await e.Events.ListAsync(null, target)).Select(x => x.Type).ToList();
        check(events.Contains(InviteEventTypes.ContentAssigned) && events.Contains(InviteEventTypes.ContentRevoked), "Verwaltung: Zuweisung und Entzug stehen im Ereignisprotokoll");
        var auditDump = string.Join("|", (await e.Raw.QueryAsync("SELECT action, detail FROM audit_log")).SelectMany(r => r.Values));
        check(auditDump.Contains("module.grant") && auditDump.Contains("invitation.create") && auditDump.Contains("invitation.redeem") && !auditDump.Contains(inv.Token), "Verwaltung: Audit enthält Zuweisungen, Einladung und Einlösung — ohne Token");
    }
}
