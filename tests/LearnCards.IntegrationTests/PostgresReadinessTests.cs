using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;
using LearnCards.Web.Services.Mail;
using LearnCards.Web.Services.ImageStorage;

/// <summary>
/// Vorbereitete PostgreSQL-Prüfung für ADR 0004/0049: Schema-Idempotenz, <c>ExecuteBatchAsync</c> (Commit, Rollback, erwartete Zeilenzahl), <c>ON CONFLICT</c>, Anspruch auf Einladung und
/// Versandslot unter Parallelität. Läuft <b>nur</b>, wenn eine leere Wegwerf-Datenbank konfiguriert ist (LEARNY_TEST_PG_HOST, LEARNY_TEST_PG_DB, LEARNY_TEST_PG_USER,
/// LEARNY_TEST_PG_PASSWORD, optional LEARNY_TEST_PG_PORT). Ohne diese Variablen wird nichts geprüft und das ausdrücklich als „NICHT AUSGEFÜHRT“ gemeldet — es entsteht keine
/// bestandene Prüfung und keine Aussage über PostgreSQL-Kompatibilität.
/// </summary>
public static class PostgresReadinessTests
{
    public static async Task RunAsync(Action<bool, string> check, string tmp)
    {
        var host = Environment.GetEnvironmentVariable("LEARNY_TEST_PG_HOST");
        if (string.IsNullOrWhiteSpace(host))
        {
            Console.WriteLine("  --  PostgreSQL-Prüfung NICHT AUSGEFÜHRT (LEARNY_TEST_PG_HOST nicht gesetzt) — die Kompatibilität mit PostgreSQL ist weiterhin NICHT belegt.");
            return;
        }
        Console.WriteLine("PostgreSQL (echte Instanz)");
        var db = new PostgresDatabase(new PostgresSettings
        {
            Host = host, Port = int.TryParse(Environment.GetEnvironmentVariable("LEARNY_TEST_PG_PORT"), out var p) ? p : 5432,
            Database = Environment.GetEnvironmentVariable("LEARNY_TEST_PG_DB") ?? "", User = Environment.GetEnvironmentVariable("LEARNY_TEST_PG_USER") ?? "",
            Password = Environment.GetEnvironmentVariable("LEARNY_TEST_PG_PASSWORD") ?? "", SslMode = Environment.GetEnvironmentVariable("LEARNY_TEST_PG_SSL") ?? "prefer",
        });
        await db.InitAsync();
        await db.InitAsync();
        check(true, "PostgreSQL: Schema und Migrationen sind wiederholbar (idempotent)");

        // atomare Gruppe
        var id = Guid.NewGuid().ToString(); var now = DateTime.UtcNow;
        await db.ExecuteBatchAsync(new[]
        {
            new DbStatement("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id,'PG',@em,'active',1,@n,@n)", new (string, object?)[] { ("id", id), ("em", "pg@example.org"), ("n", now) }, 1),
            new DbStatement("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@u,'admin',NULL,@n)", new (string, object?)[] { ("u", id), ("n", now) }, 1),
        });
        check((await db.QueryAsync("SELECT COUNT(*) AS n FROM user_roles WHERE user_id=@u", new (string, object?)[] { ("u", id) }))[0]["n"] == "1", "PostgreSQL: ExecuteBatchAsync schreibt eine Gruppe atomar");
        var rolled = false;
        try { await db.ExecuteBatchAsync(new[] { new DbStatement("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@u,'auditor',NULL,@n)", new (string, object?)[] { ("u", id), ("n", now) }, 1), new DbStatement("INSERT INTO tabelle_gibt_es_nicht (x) VALUES (1)") }); }
        catch (Exception) { rolled = true; }
        check(rolled && (await db.QueryAsync("SELECT COUNT(*) AS n FROM user_roles WHERE user_id=@u AND role='auditor'", new (string, object?)[] { ("u", id) }))[0]["n"] == "0", "PostgreSQL: Fehler in der Gruppe → Rollback, nichts bleibt zurück");
        var conflict = false;
        try { await db.ExecuteBatchAsync(new[] { new DbStatement("UPDATE users SET display_name='x' WHERE id=@u", new (string, object?)[] { ("u", Guid.NewGuid().ToString()) }, 1) }); }
        catch (DbBatchConflictException) { conflict = true; }
        check(conflict, "PostgreSQL: unerwartete Zeilenzahl → DbBatchConflictException");

        // ON CONFLICT (Zuweisungen) und Anspruch unter Parallelität
        var users = new UserService(db, new AppConfig());
        var events = new InviteEvents(db);
        var settings = new InviteSettings { TokenKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), LinkBaseUrl = "https://learny.test" };
        var invites = InvitationService.Create(db, users, settings, events);
        var created = await invites.CreateAsync(id, "pg-kunde@example.org", LearnerProfiles.B2cLearner);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => invites.RedeemAsync(created.Token, new VerifiedExternalIdentity("https://pg.example/v2.0", "pg-sub", "pg-kunde@example.org", true))));
        check(results.Count(r => r.Success) == 1 && (await db.QueryAsync("SELECT COUNT(*) AS n FROM users WHERE email=@em", new (string, object?)[] { ("em", "pg-kunde@example.org") }))[0]["n"] == "1", "PostgreSQL: acht parallele Einlösungen → genau ein Benutzer");

        var learners = new LearnerAdminService(db, users, null!, events);
        var module = Guid.NewGuid().ToString();
        await db.ExecuteAsync("INSERT INTO modules (id, name, scope, publication_id, global_version, global_status, created_at, updated_at) VALUES (@m,'PG-Modul','global',@p,1,'active',@n,@n)", new (string, object?)[] { ("m", module), ("p", Guid.NewGuid().ToString()), ("n", now) });
        var target = results.First(r => r.Success).UserId!;
        await learners.AssignModuleAsync(id, target, module);
        await learners.RevokeModuleAsync(id, target, module);
        await learners.AssignModuleAsync(id, target, module);
        check((await db.QueryAsync("SELECT COUNT(*) AS n FROM module_grants WHERE user_id=@u AND module_id=@m AND revoked_at IS NULL", new (string, object?)[] { ("u", target), ("m", module) }))[0]["n"] == "1", "PostgreSQL: ON CONFLICT reaktiviert eine Modulzuweisung");

        var mails = 0;
        var mailer = new CountingMailer(() => Interlocked.Increment(ref mails));
        var delivery = new InvitationDeliveryService(db, users, invites, mailer, events);
        var c2 = await invites.CreateAsync(id, "pg-zwei@example.org", LearnerProfiles.B2cLearner);
        var sends = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => delivery.SendAsync(id, c2.InvitationId, c2.Token, true)));
        check(sends.Count(s => s.Decision == SendDecision.Sent) == 1 && mails == 1, "PostgreSQL: acht parallele Versandanfragen → genau eine Mail (Slot in der Datenbank)");

        await CoAuthorsAsync(db, users, check);
    }

    /// <summary>Co-Author-Modus (ADR 0052) auf echter PostgreSQL-Instanz: Schema, Schlüssel, CASCADE, Idempotenz und die wesentlichen Service-Operationen.</summary>
    private static async Task CoAuthorsAsync(PostgresDatabase db, UserService users, Action<bool, string> check)
    {
        var cfg = new AppConfig();
        var images = new CardImageService(db, new ImageStorageRegistry(cfg, db), new NullImageCache(), cfg, Microsoft.Extensions.Logging.Abstractions.NullLogger<CardImageService>.Instance);
        var pool = new QuizPoolService(db);
        var repo = new CardRepository(db, images, pool);
        var modules = new ModuleTransferService(repo, images, pool, users, new CompetencyService(db, repo, users));
        var topics = new TopicService(db, repo, users, pool, null!, modules, new ModuleVersionService(db, repo, users));
        async Task<string> User(string name)
        {
            var id = Guid.NewGuid().ToString(); var n = DateTime.UtcNow;
            await db.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id,@n,@e,'active',1,@t,@t)", new (string, object?)[] { ("id", id), ("n", name), ("e", name.ToLowerInvariant() + "-" + id[..6] + "@example.org"), ("t", n) });
            await db.ExecuteAsync("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@u,'user',NULL,@t)", new (string, object?)[] { ("u", id), ("t", n) });
            return id;
        }
        var owner = await User("PgOwner"); var co = await User("PgCo"); var other = await User("PgOther");
        var existing = await topics.CreateAsync(owner, "Bestehendes PG-Thema", "");   // „bestehendes“ Thema ohne Co-Autoren
        await db.InitAsync();   // erneute Initialisierung (Migration einer vorhandenen Datenbank) lässt Themen unverändert
        check((await db.QueryAsync("SELECT COUNT(*) AS n FROM topics WHERE id=@t AND owner_user_id=@o", new (string, object?)[] { ("t", existing.Id), ("o", owner) }))[0]["n"] == "1"
            && (await db.QueryAsync("SELECT COUNT(*) AS n FROM topic_coauthors WHERE topic_id=@t", new (string, object?)[] { ("t", existing.Id) }))[0]["n"] == "0", "PostgreSQL: topic_coauthors idempotent angelegt, bestehendes Thema unverändert");
        var cols = await db.QueryAsync("SELECT constraint_type FROM information_schema.table_constraints WHERE table_name='topic_coauthors'");
        check(cols.Any(r => r["constraint_type"] == "PRIMARY KEY") && cols.Count(r => r["constraint_type"] == "FOREIGN KEY") >= 2, "PostgreSQL: topic_coauthors hat Primärschlüssel und Fremdschlüssel");
        var del = await db.QueryAsync("SELECT DISTINCT rc.delete_rule FROM information_schema.referential_constraints rc JOIN information_schema.table_constraints tc ON tc.constraint_name=rc.constraint_name WHERE tc.table_name='topic_coauthors'");
        check(del.Count == 1 && del[0]["delete_rule"] == "CASCADE", "PostgreSQL: Fremdschlüssel mit ON DELETE CASCADE");

        var added = await topics.AddCoAuthorAsync(owner, existing.Id, co);
        await topics.AddCoAuthorAsync(owner, existing.Id, co);
        check(added.UserId == co && (await topics.GetCollaborationAsync(owner, existing.Id)).CoAuthors.Count == 1 && await topics.GetTopicRoleAsync(co, existing.Id) == TopicRole.CoAuthor && await topics.GetTopicRoleAsync(other, existing.Id) == TopicRole.None, "PostgreSQL: Co-Author hinzufügen (idempotent) und Rollenentscheidung");
        var ov = await topics.ListOverviewAsync(co);
        check(ov.CoAuthored.Any(t => t.Topic.Id == existing.Id), "PostgreSQL: Themenübersicht „Gemeinsam bearbeitet“");
        await topics.UpdateAsync(co, existing.Id, "Von Co geändert", "x", existing.DraftRevision);
        var revoked = false;
        try { await topics.UpdateAsync(co, existing.Id, "Veraltet", "x", existing.DraftRevision); } catch (InvalidOperationException ex) when (ex.Message.StartsWith("Konflikt")) { revoked = true; }
        check(revoked, "PostgreSQL: veraltete Entwurfsrevision → Konflikt");
        await topics.RemoveCoAuthorAsync(owner, existing.Id, co);
        check(await topics.GetTopicRoleAsync(co, existing.Id) == TopicRole.None, "PostgreSQL: Entzug wirkt sofort");
        await topics.AddCoAuthorAsync(owner, existing.Id, co);
        await db.ExecuteAsync("DELETE FROM topics WHERE id=@t", new (string, object?)[] { ("t", existing.Id) });   // nur Wegwerf-Datenbank
        check((await db.QueryAsync("SELECT COUNT(*) AS n FROM topic_coauthors WHERE topic_id=@t", new (string, object?)[] { ("t", existing.Id) }))[0]["n"] == "0", "PostgreSQL: Löschen des Themas entfernt Co-Author-Einträge (CASCADE)");
    }

    private sealed class CountingMailer : IInvitationMailer
    {
        private readonly Action _count;
        public CountingMailer(Action count) => _count = count;
        public async Task<MailSendResult> SendAsync(InvitationMail mail, CancellationToken ct = default) { _count(); await Task.Delay(100, ct); return new(MailOutcome.Accepted, "accepted"); }
    }
}
