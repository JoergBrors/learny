using System.Text.Json;
using System.Text.Json.Nodes;
using LearnCards.Web.Api;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;
using LearnCards.Web.Services.ImageStorage;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// ADR 0028, Abschlussprüfung des Transfers (im Prozess, SQLite): Fehlerinjektion an jeder Schreibstelle von Modul- und Themenimport (keine Teilbestände),
/// Modulreferenzen (Eindeutigkeit, Parallelität, kein Überschreiben) und Altbestand mit globalen Modulen in Themenentwürfen (Ersetzen durch eigene Kopie).
/// </summary>
public static class TransferIntegrityUnitTests
{
    /// <summary>Datenbank-Decorator: lässt die n-te schreibende Anweisung mit dem Suchtext scheitern (DELETE nie — Aufräumen muss gelingen).</summary>
    private sealed class FaultDb : IDatabase
    {
        public readonly IDatabase Inner;
        public string? Match; public int Skip; public int Hits;
        public FaultDb(IDatabase inner) => Inner = inner;
        public string ProviderName => Inner.ProviderName;
        public Task InitAsync(CancellationToken ct = default) => Inner.InitAsync(ct);
        public Task<List<Dictionary<string, string?>>> QueryAsync(string sql, IReadOnlyList<(string Name, object? Value)>? args = null, CancellationToken ct = default) => Inner.QueryAsync(sql, args, ct);
        public Task<int> ExecuteAsync(string sql, IReadOnlyList<(string Name, object? Value)>? args = null, CancellationToken ct = default)
        {
            if (Match is not null && !sql.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) && sql.Contains(Match, StringComparison.Ordinal) && Skip-- <= 0)
            {
                Hits++;
                Match = null;   // einmaliger Fehler: Aufräumen und Wiederherstellen müssen danach gelingen
                throw new InvalidOperationException("injizierter Fehler: " + Match);
            }
            return Inner.ExecuteAsync(sql, args, ct);
        }
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
        public void Arm(string match, int skip = 0) { Match = match; Skip = skip; Hits = 0; }
        public void Disarm() => Match = null;
    }

    private sealed class Env
    {
        public SqliteDatabase Raw = null!; public FaultDb Db = null!; public CardRepository Repo = null!; public ModuleTransferService Modules = null!; public TopicService Topics = null!; public QuizPoolService Pool = null!;
        public static async Task<Env> CreateAsync(string dir)
        {
            Directory.CreateDirectory(dir);
            var e = new Env { Raw = new SqliteDatabase(Path.Combine(dir, "t.db")) };
            await e.Raw.InitAsync();
            e.Db = new FaultDb(e.Raw);
            var cfg = new AppConfig();
            var users = new UserService(e.Db, cfg);
            var images = new CardImageService(e.Db, new ImageStorageRegistry(cfg, e.Db), new NullImageCache(), cfg, NullLogger<CardImageService>.Instance);
            e.Pool = new QuizPoolService(e.Db);
            e.Repo = new CardRepository(e.Db, images, e.Pool);
            e.Modules = new ModuleTransferService(e.Repo, images, e.Pool, users);
            e.Topics = new TopicService(e.Db, e.Repo, users, e.Pool, null!, e.Modules);   // Prüfungsstart (QuizService) wird hier nicht benötigt
            return e;
        }
        public async Task<string> AddUserAsync(string name)
        {
            var id = Guid.NewGuid().ToString();
            await Raw.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id, @n, '', 'active', 1, @t, @t)",
                new (string, object?)[] { ("id", id), ("n", name), ("t", DateTime.UtcNow) });
            return id;
        }
        public async Task<string> CountsAsync(string owner)
        {
            var parts = new List<string>();
            foreach (var (label, sql) in new[]
            {
                ("modules", "SELECT COUNT(*) AS n FROM modules WHERE owner_user_id = @o"), ("cards", "SELECT COUNT(*) AS n FROM cards WHERE owner_user_id = @o"),
                ("images", "SELECT COUNT(*) AS n FROM card_images"), ("blobs", "SELECT COUNT(*) AS n FROM image_blobs"), ("quiz", "SELECT COUNT(*) AS n FROM quiz_questions"),
                ("configs", "SELECT COUNT(*) AS n FROM quiz_configs"), ("topics", "SELECT COUNT(*) AS n FROM topics WHERE owner_user_id = @o"), ("topic_modules", "SELECT COUNT(*) AS n FROM topic_modules"),
                ("topic_questions", "SELECT COUNT(*) AS n FROM topic_questions"), ("topic_configs", "SELECT COUNT(*) AS n FROM topic_exam_configs"),
            })
                parts.Add($"{label}={(await Raw.QueryAsync(sql, new[] { ("o", (object?)owner) }))[0]["n"]}");
            return string.Join(",", parts);
        }
    }

    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAQCAIAAAD4YuoOAAAAI0lEQVR42mPg94mhKWIAYq3ls2mERi0YtWDUglELRpIFNEUAOleXE3NVou0AAAAASUVORK5CYII=";

    private static ModuleDefinition FullModule(string name, string? reference = null) => new()
    {
        Name = name, Description = "d", ReferenceId = reference,
        Cards =
        {
            new CardJson { Category = "K", Term = "A", Question = "Q?", Definition = "D", Images = new() { new CardImage { MimeType = "image/png", DataBase64 = Png, AltText = "x", SortOrder = 1 } },
                Quiz = { new CardQuizQuestion { Type = "single_choice", Question = "Frage A?", Options = new() { "1", "2", "3", "4" }, CorrectIndex = 1, Explanation = "e" } } },
            new CardJson { Category = "K", Term = "B", Question = "Q?", Definition = "D",
                Quiz = { new CardQuizQuestion { Type = "single_choice", Question = "Frage B?", Options = new() { "1", "2", "3", "4" }, CorrectIndex = 0, Explanation = "e" } } },
            new CardJson { Category = "K", Term = "C", Question = "Q?", Definition = "D" },
        },
        QuizConfig = new QuizConfig { Enabled = true, QuestionCount = 3, PassPercent = 70 },
        PoolQuestions = { new ModulePoolQuestionDefinition { CardIndex = 0, Type = "single_choice", Question = "Poolfrage?", Options = new() { "a", "b", "c", "d" }, CorrectIndexes = new() { 2 }, Explanation = "c" } },
    };

    public static async Task RunAsync(Action<bool, string> check, string tmp)
    {
        Console.WriteLine("\nTransfer-Integrität im Prozess: Fehlerinjektion, Referenzen, Altbestand (ADR 0028)");
        var e = await Env.CreateAsync(Path.Combine(tmp, "transfer-integrity"));
        var u1 = await e.AddUserAsync("ti-owner"); var u2 = await e.AddUserAsync("ti-other");

        // ─── Modulimport: Fehler an jeder Schreibstelle hinterlässt nichts ───
        var moduleFaults = new (string Label, string Match, int Skip)[]
        {
            ("Modulanlage", "INSERT INTO modules", 0), ("Kartenanlage (zweite Karte)", "INSERT INTO cards", 1), ("Bildübernahme (Metadaten)", "INSERT INTO card_images", 0),
            ("Bildübernahme (Bytes)", "INSERT INTO image_blobs", 0), ("Quizübernahme (Karten-Quiz)", "INSERT INTO quiz_questions", 0), ("Quizübernahme (Pool-Frage)", "INSERT INTO quiz_questions", 2),
            ("Quizkonfiguration", "quiz_configs", 0),
        };
        var before = await e.CountsAsync(u1);
        foreach (var (label, match, skip) in moduleFaults)
        {
            e.Db.Arm(match, skip);
            var failed = false;
            try { await e.Modules.ImportAsync(u1, FullModule("TI-Modul")); } catch (InvalidOperationException) { failed = true; }
            e.Db.Disarm();
            var hit = e.Db.Hits;
            check(failed && hit == 1 && await e.CountsAsync(u1) == before, $"I1 Modulimport: Fehler bei {label} → Rollback ohne Teilbestand" + (failed && hit == 1 ? "" : $" (failed={failed}, hits={hit})"));
        }
        var ok = await e.Modules.ImportAsync(u1, FullModule("TI-Modul"));
        check(ok.Name == "TI-Modul" && ok.ActiveCount == 3, "I2 nach den Rollbacks bleibt kein Namenskonflikt zurück (Import gelingt unter dem Originalnamen)");

        // ─── Themenimport/-update: Fehler an jeder Schreibstelle ───
        var modB = await e.Modules.ImportAsync(u1, FullModule("TI-Zweit"));
        string TopicJson(string name, bool embedded, bool withQ = true)
        {
            var def = new TopicDefinition { Name = name, Description = "t" };
            def.Modules.Add(new TopicDefinitionModule { ModuleRef = e.Repo.EnsureReferenceIdAsync(ok.Id, u1).GetAwaiter().GetResult(), ModuleName = "TI-Modul", Exam = new TopicModuleExamConfig { Enabled = true, QuestionCount = 2 } });
            def.Modules.Add(new TopicDefinitionModule { ModuleName = "TI-Zweit" });
            if (embedded) def.Modules.Add(new TopicDefinitionModule { ModuleName = "TI-Eingebettet", Module = FullModule("TI-Eingebettet") });
            def.ExamConfig.FinalExam = new TopicFinalExamConfig { Enabled = true, QuestionCount = 3, PassPercent = 60, Distribution = "even" };
            if (withQ) { def.TopicQuestions.Add(new TopicDefinitionQuestion { Question = "Themenfrage 1?", Options = new() { "x", "y", "z" }, CorrectIndexes = new() { 1 }, Explanation = "y" }); def.TopicQuestions.Add(new TopicDefinitionQuestion { Question = "Themenfrage 2?", Options = new() { "x", "y", "z" }, CorrectIndexes = new() { 0 }, Explanation = "x" }); }
            return TransferJson.ToJson(def).ToJsonString();
        }
        var topicBefore = await e.CountsAsync(u1);
        var topicFaults = new (string Label, string Match, int Skip)[]
        {
            ("Themenanlage", "INSERT INTO topics", 0), ("Modulzuordnung (zweites Modul)", "INSERT INTO topic_modules", 1), ("Prüfungskonfiguration (Anlage)", "topic_exam_configs", 0),
            ("Prüfungskonfiguration (Anwenden)", "topic_exam_configs", 2), ("Themenfrage (zweite)", "INSERT INTO topic_questions", 1),
            ("eingebettetes Modul: Karte", "INSERT INTO cards", 2),
        };
        foreach (var (label, match, skip) in topicFaults)
        {
            e.Db.Arm(match, skip);
            var failed = false;
            try { await e.Topics.CreateFromDefinitionAsync(u1, TopicJson("TI-Thema", embedded: true)); } catch (InvalidOperationException) { failed = true; }
            e.Db.Disarm();
            check(failed && await e.CountsAsync(u1) == topicBefore, $"I3 Themenimport: Fehler bei {label} → weder Thema noch eingebettete Module bleiben zurück");
        }
        var created = await e.Topics.CreateFromDefinitionAsync(u1, TopicJson("TI-Thema", embedded: true));
        var detail = await e.Topics.GetDetailAsync(u1, created.Id, null);
        check(detail.Modules.Count == 3 && detail.Modules[2].Name == "TI-Eingebettet" && detail.Version == 0, "I4 nach den Rollbacks gelingt der Import (3 Module, Entwurf)");

        // Update: Fehler stellen den vorherigen Entwurf wieder her
        var snapshotJson = TransferJson.ToJson(await e.Topics.ExportAsync(u1, created.Id, includeModules: false)).ToJsonString();
        var updateDef = JsonNode.Parse(TopicJson("TI-Thema", embedded: false, withQ: true))!;
        updateDef["description"] = "geändert";
        updateDef["topic_questions"]![0]!["question"] = "Neue Themenfrage?";
        foreach (var (label, match, skip) in new[] { ("Themenfragen", "INSERT INTO topic_questions", 1), ("Modulzuordnung", "INSERT INTO topic_modules", 1), ("Prüfungskonfiguration", "topic_exam_configs", 0) })
        {
            e.Db.Arm(match, skip);
            var failed = false;
            try { await e.Topics.UpdateFromDefinitionAsync(u1, created.Id, updateDef.ToJsonString()); } catch (InvalidOperationException) { failed = true; }
            e.Db.Disarm();
            var afterJson = TransferJson.ToJson(await e.Topics.ExportAsync(u1, created.Id, includeModules: false)).ToJsonString();
            check(failed && afterJson == snapshotJson, $"I5 Themenupdate: Fehler bei {label} → vorheriger Entwurf (Module, Prüfungen, Themenfragen, Beschreibung) bleibt unverändert");
        }
        // unbekannte Referenz im Update ändert nichts und wird nie über den Namen aufgelöst
        var badRef = JsonNode.Parse(snapshotJson)!;
        badRef["modules"]![0]!["module_ref"] = Guid.NewGuid().ToString();
        var badRefFailed = false; string badRefMessage = "";
        try { await e.Topics.UpdateFromDefinitionAsync(u1, created.Id, badRef.ToJsonString()); } catch (ArgumentException ex) { badRefFailed = true; badRefMessage = ex.Message; }
        check(badRefFailed && badRefMessage.Contains("module_ref") && TransferJson.ToJson(await e.Topics.ExportAsync(u1, created.Id, false)).ToJsonString() == snapshotJson, "I6 unbekannte module_ref trotz passendem Namen: Fehler, keine stille Auflösung über module_name, Entwurf unverändert");

        // ─── Modulreferenzen ───
        var refModule = await e.Modules.ImportAsync(u1, FullModule("TI-Ref"));
        var refs = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => e.Repo.EnsureReferenceIdAsync(refModule.Id, u1)));
        var rows = await e.Raw.QueryAsync("SELECT reference_id FROM modules WHERE id = @id", new[] { ("id", (object?)refModule.Id) });
        check(refs.Distinct().Count() == 1 && Guid.TryParse(refs[0], out _) && rows[0]["reference_id"] == refs[0], "I7 zwölf parallele Vergaben derselben Modulinstanz liefern dieselbe GUID (persistiert, keine Doppelvergabe)");
        check(await e.Repo.EnsureReferenceIdAsync(refModule.Id, u1) == refs[0], "I8 eine vorhandene Referenz wird nie überschrieben");
        var foreignErr = false; try { await e.Repo.EnsureReferenceIdAsync(refModule.Id, u2); } catch (KeyNotFoundException) { foreignErr = true; }
        check(foreignErr && !await e.Repo.TryAssignReferenceIdAsync(refModule.Id, u2, Guid.NewGuid().ToString()) && (await e.Raw.QueryAsync("SELECT reference_id FROM modules WHERE id = @id", new[] { ("id", (object?)refModule.Id) }))[0]["reference_id"] == refs[0],
            "I9 fremde Module: Referenz weder lesbar noch vergebbar oder änderbar");
        var other = await e.Modules.ImportAsync(u1, FullModule("TI-Ref-2"));
        var clash = false;
        try { await e.Raw.ExecuteAsync("UPDATE modules SET reference_id = @r WHERE id = @id", new (string, object?)[] { ("r", refs[0]), ("id", other.Id) }); } catch (Exception) { clash = true; }
        var otherOwner = await e.Modules.ImportAsync(u2, FullModule("TI-Ref-3"));
        var sameRefOtherOwnerOk = true;
        try { await e.Raw.ExecuteAsync("UPDATE modules SET reference_id = @r WHERE id = @id", new (string, object?)[] { ("r", refs[0]), ("id", otherOwner.Id) }); } catch (Exception) { sameRefOtherOwnerOk = false; }
        check(clash && sameRefOtherOwnerOk, "I10 Unique-Index: dieselbe Referenz ist je Eigentümer nur einmal, bei verschiedenen Eigentümern erlaubt");
        var other4 = await e.Modules.ImportAsync(u1, FullModule("TI-Ref-4"));
        var assigns = await Task.WhenAll(new[] { other.Id, other4.Id }.Select(async id => { try { return (bool?)await e.Repo.TryAssignReferenceIdAsync(id, u1, "11111111-1111-1111-1111-111111111111"); } catch (Exception) { return null; } }));
        check(assigns.Count(a => a == true) == 1 && assigns.All(a => a is not null), "I11 paralleles Vergeben derselben Referenz an zwei Module: genau eines gewinnt, kein Fehler");
        var embedded = FullModule("TI-Parallel", "22222222-2222-2222-2222-222222222222");
        var parallel = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => e.Modules.ImportOrReuseAsync(u1, embedded)));
        var withRef = (await e.Raw.QueryAsync("SELECT id FROM modules WHERE owner_user_id = @o AND reference_id = '22222222-2222-2222-2222-222222222222'", new[] { ("o", (object?)u1) }));
        check(parallel.Count(p => p.Created) == 1 && parallel.Select(p => p.Module.Id).Distinct().Count() == 1 && withRef.Count == 1, "I12 acht parallele Importe eingebetteter Module mit derselben Referenz erzeugen genau ein Modul, die übrigen verwenden es");
        var cardsBefore = (await e.Repo.ListAllCardsOfModuleRawAsync(parallel[0].Module.Id)).Count;
        var changed = FullModule("TI-Parallel anders", "22222222-2222-2222-2222-222222222222"); changed.Cards.RemoveAt(2);
        var reused = await e.Modules.ImportOrReuseAsync(u1, changed);
        check(!reused.Created && reused.Module.Name == "TI-Parallel" && (await e.Repo.ListAllCardsOfModuleRawAsync(reused.Module.Id)).Count == cardsBefore, "I13 Wiederverwendung überschreibt das vorhandene Modul nicht (Name und Karten bleiben)");
        var otherAcct = await e.Modules.ImportOrReuseAsync(u2, embedded);
        check(otherAcct.Created && otherAcct.Module.OwnerUserId == u2 && (await e.Repo.FindOwnModuleByReferenceAsync(u2, "22222222-2222-2222-2222-222222222222"))?.Id == otherAcct.Module.Id, "I14 in einem anderen Benutzerkonto wird dieselbe Referenz unabhängig aufgelöst (eigenes Modul)");
        var dup = await e.Modules.ImportAsync(u1, FullModule("TI-Doppel", "22222222-2222-2222-2222-222222222222"));
        check(dup.ReferenceId is null && dup.Id != parallel[0].Module.Id && (await e.Repo.FindOwnModuleByReferenceAsync(u1, "22222222-2222-2222-2222-222222222222"))!.Id == parallel[0].Module.Id, "I15 neuer Import mit belegter Referenz: neues Modul ohne Referenz, bestehende Zuordnung bleibt");
        var badGuid = false; try { ModuleTransferService.Parse(TransferJson.ToJson(FullModule("TI-X", "keine-guid")).ToJsonString()); } catch (ArgumentException) { badGuid = true; }
        var badTopicGuid = false;
        try { TopicService.ParseDefinition(JsonNode.Parse(TopicJson("TI-Y", false))!.AsObject().Also(o => o["modules"]![0]!["module_ref"] = "x").ToJsonString()); } catch (ArgumentException) { badTopicGuid = true; }
        var dupRef = false;
        try { TopicService.ParseDefinition(JsonNode.Parse(TopicJson("TI-Z", false))!.AsObject().Also(o => o["modules"]![1]!["module_ref"] = o["modules"]![0]!["module_ref"]!.ToString()).ToJsonString()); } catch (ArgumentException) { dupRef = true; }
        check(badGuid && badTopicGuid && dupRef, "I16 ungültige und doppelte GUIDs werden schon beim Parsen abgewiesen");

        // ─── Altbestand: Entwurf mit globalem Modul ───
        var legacyOwner = await e.AddUserAsync("ti-legacy");
        var own = await e.Modules.ImportAsync(legacyOwner, FullModule("TI-Eigen"));
        var g = await e.Modules.ImportAsync(legacyOwner, FullModule("TI-Global"));
        await e.Raw.ExecuteAsync("UPDATE modules SET scope = 'global', global_status = 'active', owner_user_id = NULL, publication_id = @p, global_version = 1, source_owner_name = 'Altbestand', reference_id = NULL WHERE id = @id",
            new (string, object?)[] { ("p", Guid.NewGuid().ToString()), ("id", g.Id) });
        var topic = await e.Topics.CreateAsync(legacyOwner, "TI-Altbestand", "");
        await e.Topics.AddModuleAsync(legacyOwner, topic.Id, own.Id);
        await e.Topics.SaveExamConfigAsync(legacyOwner, topic.Id, new TopicExamConfig { ModuleExams = { [own.Id] = new TopicModuleExamConfig { Enabled = true, QuestionCount = 1 } }, FinalExam = new TopicFinalExamConfig { Enabled = false } });
        var v1 = await e.Topics.PublishAsync(legacyOwner, topic.Id);
        var v1Json = (await e.Raw.QueryAsync("SELECT snapshot_json FROM topic_versions WHERE id = @id", new[] { ("id", (object?)v1.Id) }))[0]["snapshot_json"];
        var v1Mods = string.Join(",", (await e.Raw.QueryAsync("SELECT module_id FROM topic_version_modules WHERE version_id = @v ORDER BY position", new[] { ("v", (object?)v1.Id) })).Select(r => r["module_id"]));
        var v1Cards = (await e.Raw.QueryAsync("SELECT COUNT(*) AS n FROM cards"))[0]["n"];
        // Altbestand erzeugen (wie vor ADR 0028): globales Modul direkt im Entwurf, mit Prüfungskonfiguration
        await e.Raw.ExecuteAsync("INSERT INTO topic_modules (topic_id, module_id, sort_order, added_at) VALUES (@t, @m, 1, @n)", new (string, object?)[] { ("t", topic.Id), ("m", g.Id), ("n", DateTime.UtcNow) });
        var cfgNow = await e.Topics.LoadExamConfigAsync(topic.Id);
        cfgNow.ModuleExams[g.Id] = new TopicModuleExamConfig { Enabled = true, QuestionCount = 2, PassPercent = 55 };
        await e.Raw.ExecuteAsync("UPDATE topic_exam_configs SET config_json = @j WHERE topic_id = @t", new (string, object?)[] { ("j", JsonSerializer.Serialize(cfgNow, AppJson.Options)), ("t", topic.Id) });
        var legacyDetail = await e.Topics.GetDetailAsync(legacyOwner, topic.Id, null);
        check(legacyDetail.Modules.Count == 2 && legacyDetail.Modules[1].RequiresOwnCopy && !legacyDetail.Modules[0].RequiresOwnCopy && legacyDetail.Warnings.Any(w => w.Contains("eigene Kopie")), "I17 Altbestand: Entwurf mit globalem Modul bleibt lesbar und zeigt die Warnung (RequiresOwnCopy)");
        var publishBlocked = false; try { await e.Topics.PublishAsync(legacyOwner, topic.Id); } catch (ArgumentException ex) { publishBlocked = ex.Message.Contains("eigene Kopie"); }
        var addBlocked = false; try { await e.Topics.AddModuleAsync(legacyOwner, topic.Id, g.Id); } catch (ArgumentException) { addBlocked = true; }
        check(publishBlocked && addBlocked, "I18 Altbestand: Veröffentlichen und erneutes Zuordnen globaler Module sind gesperrt");
        var globalCards = (await e.Repo.ListAllCardsOfModuleRawAsync(g.Id)).Select(c => c.Id).OrderBy(x => x).ToList();
        var fremd = await e.AddUserAsync("ti-fremd");
        var foreign = false; try { await e.Topics.ReplaceWithOwnCloneAsync(fremd, topic.Id, g.Id); } catch (KeyNotFoundException) { foreign = true; }
        var ownReplace = false; try { await e.Topics.ReplaceWithOwnCloneAsync(legacyOwner, topic.Id, own.Id); } catch (ArgumentException) { ownReplace = true; }
        check(foreign && ownReplace, "I19 Ersetzen: fremdes Thema (404) und eigenes Modul (400) werden abgelehnt");
        var clone = await e.Topics.ReplaceWithOwnCloneAsync(legacyOwner, topic.Id, g.Id);
        var after = await e.Topics.GetDetailAsync(legacyOwner, topic.Id, null);
        var cloneRef = await e.Repo.EnsureReferenceIdAsync(clone.Id, legacyOwner);
        var cfgAfter = await e.Topics.LoadExamConfigAsync(topic.Id);
        check(clone.OwnerUserId == legacyOwner && clone.Scope == "private" && clone.Id != g.Id && after.Modules[1].ModuleId == clone.Id && !after.Modules[1].RequiresOwnCopy && after.Modules[1].Name == "TI-Global"
              && Guid.TryParse(cloneRef, out _) && cfgAfter.ModuleExams.ContainsKey(clone.Id) && !cfgAfter.ModuleExams.ContainsKey(g.Id) && cfgAfter.ModuleExams[clone.Id].PassPercent == 55,
            "I20 Ersetzen: eigener Klon (Eigentümer, privat, neue ID, eigene Referenz) an gleicher Position, Prüfungskonfiguration wandert mit");
        var globalAfter = (await e.Raw.QueryAsync("SELECT scope, global_status, owner_user_id FROM modules WHERE id = @id", new[] { ("id", (object?)g.Id) }))[0];
        check(globalAfter["scope"] == "global" && globalAfter["global_status"] == "active" && globalAfter["owner_user_id"] is null or "" && (await e.Repo.ListAllCardsOfModuleRawAsync(g.Id)).Select(c => c.Id).OrderBy(x => x).SequenceEqual(globalCards), "I21 das globale Original bleibt unverändert");
        var v1JsonAfter = (await e.Raw.QueryAsync("SELECT snapshot_json FROM topic_versions WHERE id = @id", new[] { ("id", (object?)v1.Id) }))[0]["snapshot_json"];
        var v1ModsAfter = string.Join(",", (await e.Raw.QueryAsync("SELECT module_id FROM topic_version_modules WHERE version_id = @v ORDER BY position", new[] { ("v", (object?)v1.Id) })).Select(r => r["module_id"]));
        check(v1JsonAfter == v1Json && v1ModsAfter == v1Mods, "I22 die veröffentlichte Version 1 bleibt unverändert (Snapshot und Modulzuordnung)");
        var v2 = await e.Topics.PublishAsync(legacyOwner, topic.Id);
        var v2Mods = (await e.Raw.QueryAsync("SELECT module_id FROM topic_version_modules WHERE version_id = @v AND role = 'module' ORDER BY position", new[] { ("v", (object?)v2.Id) })).Select(r => r["module_id"]).ToList();
        check(v2.VersionNumber == 2 && v2Mods.Count == 2 && v1Mods.Split(',').Contains(v2Mods[0]) && (await e.Raw.QueryAsync("SELECT 1 FROM topic_version_modules WHERE version_id = @v AND module_id = @m", new (string, object?)[] { ("v", v1.Id), ("m", v2Mods[1]) })).Count == 0,
            "I23 danach lässt sich Version 2 veröffentlichen; das unveränderte eigene Modul teilt die eingefrorene Kopie mit Version 1, das neue erhält eine eigene");

        // ─── Lernstatus und Überfälligkeit (ADR 0029): Fälligkeit nur bei offener Pflichtzuweisung ───
        var adminU = await e.AddUserAsync("ti-admin");
        await e.Raw.ExecuteAsync("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@u, 'admin', 'test', @t)", new (string, object?)[] { ("u", adminU), ("t", DateTime.UtcNow) });
        var learner = await e.AddUserAsync("ti-learner"); var volunteer = await e.AddUserAsync("ti-volunteer");
        await e.Raw.ExecuteAsync("UPDATE topic_versions SET global_status = 'active' WHERE id = @id", new (string, object?)[] { ("id", v2.Id) });
        var mand = await e.Topics.AssignAsync(adminU, topic.Id, learner, null, true, DateTime.UtcNow.AddDays(3));
        var vol = await e.Topics.AssignAsync(adminU, topic.Id, volunteer, null, false, DateTime.UtcNow.AddDays(3));
        check(mand.Mandatory && mand.TopicVersion == 2 && !mand.Overdue && !vol.Mandatory, "I24 Zuweisung: konkrete Version (aktive globale Version 2), verpflichtend bzw. freiwillig, Fälligkeit in der Zukunft ist nicht überfällig");
        await e.Raw.ExecuteAsync("UPDATE topic_assignments SET due_at = @d", new (string, object?)[] { ("d", DateTime.UtcNow.AddDays(-2)) });
        var overdue = await e.Topics.ListStatusAsync(adminU, new TopicService.StatusFilter { Status = "overdue" });
        var learnerOv = await e.Topics.ListOverviewAsync(learner);
        var volunteerOv = await e.Topics.ListOverviewAsync(volunteer);
        check(overdue.Count == 1 && overdue[0].UserId == learner && overdue[0].Overdue && overdue[0].Status == "not_started" && learnerOv.Mandatory.Count == 1 && learnerOv.Mandatory[0].Overdue
              && volunteerOv.Mandatory.Count == 0 && !volunteerOv.Assigned[0].Overdue, "I25 Überfälligkeit entsteht nur bei offener Pflichtzuweisung mit abgelaufener Fälligkeit; freiwillige Zuweisungen werden nie überfällig");
        var denied = false; try { await e.Topics.ListStatusAsync(learner); } catch (UnauthorizedAccessException) { denied = true; }
        var deniedAssign = false; try { await e.Topics.AssignAsync(learner, topic.Id, volunteer); } catch (UnauthorizedAccessException) { deniedAssign = true; }
        check(denied && deniedAssign, "I26 Rollenprüfung im Dienst: ohne Rolle weder Status noch Zuweisung (UnauthorizedAccessException)");
        _ = v1Cards;
    }

    private static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
