using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;
using LearnCards.Web.Services.ImageStorage;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Lern- und Prüfungsvorschau für Content-Co-Autoren (ADR 0054): Berechtigung (Eigentümer, direkter Modul-Co-Author, Themen-Co-Author), Entwurfsstand, Testauswertung mit der regulären Bewertung,
/// strikte Datenisolierung (nichts wird gespeichert), Entzug vor Start und vor Abgabe, reguläre Lernrechte unverändert. Identisch auf SQLite (im Prozess) und PostgreSQL.
/// </summary>
public static class PreviewTests
{
    private static async Task<bool> Denied(Func<Task> f)
    {
        try { await f(); return false; }
        catch (Exception e) when (e is KeyNotFoundException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or QuizRuleException or CardRepository.CardConflictException) { return true; }
    }

    private static QuizQuestionInput Q(string cardId, string text) => new()
    {
        CardId = cardId, Type = QuizTypes.SingleChoice, Question = text, Options = new() { "Richtig", "B", "C", "D" }, CorrectIndexes = new() { 0 }, Explanation = "Weil A.",
    };

    private static QuizQuestionInput QT(string text) { var q = Q("", text); q.CardId = null; return q; }

    /// <summary>Baut die Dienste für SQLite/PostgreSQL mit einem <b>echten</b> QuizService (Vorschau braucht ihn).</summary>
    public static (TopicService Topics, CardRepository Repo, QuizService Quiz, QuizPoolService Pool) BuildServices(IDatabase db, UserService users)
    {
        var cfg = new AppConfig();
        var images = new CardImageService(db, new ImageStorageRegistry(cfg, db), new NullImageCache(), cfg, NullLogger<CardImageService>.Instance);
        var pool = new QuizPoolService(db);
        var repo = new CardRepository(db, images, pool);
        var comp = new CompetencyService(db, repo, users);
        var modules = new ModuleTransferService(repo, images, pool, users, comp);
        var quiz = QuizPoolCoAuthorTests.Build(repo, pool, comp, db, users);
        return (new TopicService(db, repo, users, pool, quiz, modules, new ModuleVersionService(db, repo, users)), repo, quiz, pool);
    }

    public static async Task RunAsync(Action<bool, string> check, string tmp)
    {
        Console.WriteLine("\nLern- und Prüfungsvorschau (ADR 0054), im Prozess auf SQLite");
        var e = await InvitationUnitTests.Env.CreateAsync(Path.Combine(tmp, "preview"));
        var (topics, repo, quiz, pool) = BuildServices(e.Db, e.Users);
        var ext = await e.AddWorkforceUserAsync("Extern Ela", "ela-p@example.org");
        await e.Raw.ExecuteAsync("UPDATE user_identities SET kind='external' WHERE user_id=@u", new (string, object?)[] { ("u", ext) });
        var n = 0;
        await CoreAsync("SQLite", e.Db, topics, repo, quiz, pool, async name => await e.AddWorkforceUserAsync(name, $"{name.ToLowerInvariant().Replace(' ', '.')}-pv{++n}@example.org"), ext, check);
    }

    private static readonly string[] IsolationTables = { "quiz_sessions", "quiz_results", "user_card_states", "topic_exam_attempts", "certificates", "quiz_questions" };

    private static async Task<string> Snapshot(IDatabase db)
    {
        var parts = new List<string>();
        foreach (var t in IsolationTables) parts.Add(t + "=" + (await db.QueryAsync($"SELECT COUNT(*) AS n FROM {t}"))[0]["n"]);
        return string.Join(",", parts);
    }

    private static QuizSubmitRequest Answers(QuizStartResponse s) => new()
    {
        ModuleId = s.ModuleId, SessionId = s.SessionId, Category = s.Category,
        Answers = s.Questions.Select(q => new QuizAnswerItem { Question = q.Question, QuestionId = q.QuestionId, SourceCardId = q.SourceCardId, SelectedIndexes = QuizTypes.IsChoice(q.Type) ? new() { q.Options.IndexOf("Richtig") } : new(), UserAnswer = "x" }).ToList(),
    };

    public static async Task CoreAsync(string label, IDatabase db, TopicService topics, CardRepository repo, QuizService quiz, QuizPoolService pool, Func<string, Task<string>> newUser, string? restricted, Action<bool, string> check)
    {
        var p = label + " ";
        var owner = await newUser("Pia Preview"); var co = await newUser("Cleo Co"); var direct = await newUser("Dirk Direkt"); var outsider = await newUser("Olli Out");
        var tag = Guid.NewGuid().ToString("N")[..6];
        var (n1, n2, n3) = ("PvA-" + tag, "PvB-" + tag, "PvC-" + tag);
        var m1 = (await repo.CreateModuleAsync(new ModuleCreateRequest { Name = n1 }, owner)).Id;   // nur Thema 1
        var m2 = (await repo.CreateModuleAsync(new ModuleCreateRequest { Name = n2 }, owner)).Id;   // Thema 1 und 2 (Mehrfachzuordnung)
        var m3 = (await repo.CreateModuleAsync(new ModuleCreateRequest { Name = n3 }, owner)).Id;   // ohne Thema, direkte Freigabe
        var cards = new Dictionary<string, Card>();
        foreach (var (m, nm) in new[] { (m1, n1), (m2, n2), (m3, n3) })
        {
            cards[m] = await repo.CreateCardAsync(new CardJson { Module = nm, Category = "K", Term = "Karte " + nm, Question = "Frage?", Definition = "Definition" }, owner);
            foreach (var i in new[] { 1, 2, 3 }) await quiz.CreateQuestionAsync(m, Q(cards[m].Id, $"Frage {nm} {i}"), owner);
            var cfg = (await quiz.GetConfigViewAsync(m, owner)).Config.Clone();
            cfg.Origin = QuizConfig.OriginFixed; cfg.Strategy = QuizConfig.StrategyNone; cfg.FreeText = false; cfg.ShuffleQuestions = false; cfg.QuestionCount = 3; cfg.Scope = QuizConfig.ScopeBoth;
            await quiz.SaveConfigAsync(m, cfg, owner);
        }
        var t1 = await topics.CreateAsync(owner, "PvT1 " + tag, ""); var t2 = await topics.CreateAsync(owner, "PvT2 " + tag, "");
        await topics.AddModuleAsync(owner, t1.Id, m1); await topics.AddModuleAsync(owner, t1.Id, m2); await topics.AddModuleAsync(owner, t2.Id, m2);
        await topics.AddCoAuthorAsync(owner, t1.Id, co);
        await topics.AddModuleCoAuthorAsync(owner, m3, direct);
        var baseline = await Snapshot(db);

        // ─── Berechtigung ───
        var s1 = await topics.StartModulePreviewQuizAsync(owner, m1, "", null, 3);
        check(s1.Questions.Count == 3 && s1.SessionId.Length > 0, p + "P1 Eigentümer startet das Modulquiz im Testmodus (Anzahl wie konfiguriert)");
        var r1 = await topics.SubmitPreviewAsync(owner, Answers(s1));
        check(r1.Percentage == 100 && r1.Passed == true && string.IsNullOrEmpty(r1.ResultId) && string.IsNullOrEmpty(r1.CertificateId), p + "P2 Testauswertung mit der regulären Bewertung (100 %, bestanden), ohne Ergebnis-ID und ohne Zertifikat");
        check(r1.GradedAnswers.All(g => g.CorrectIndexes.Count > 0 && g.Explanation.Length > 0), p + "P3 Richtige Lösungen und Erklärungen werden angezeigt (Quizkonfiguration)");
        var after = await Snapshot(db);
        check(after == baseline, p + $"P4 Datenisolierung: keine Sitzung, kein Ergebnis, kein Fortschritt, kein Versuch, kein Zertifikat, Fragenpool unverändert ({after})");
        check(await Denied(() => topics.SubmitPreviewAsync(owner, Answers(s1))), p + "P5 Eine Vorschausitzung lässt sich nur einmal abgeben");

        var sC = await topics.StartModulePreviewQuizAsync(co, m1, "", null, 3);
        check(sC.Questions.Count == 3, p + "P6 Themen-Co-Author testet ein Modul seines Themenentwurfs (abgeleitetes Recht)");
        check(await Denied(() => topics.StartModulePreviewQuizAsync(co, m2, "", null, 3)) && await Denied(() => topics.StartModulePreviewQuizAsync(co, m3, "", null, 3)), p + "P7 Mehrfachzuordnung (Thema 2 ohne Recht) und fremdes Modul bleiben gesperrt (ADR 0053)");
        var sD = await topics.StartModulePreviewQuizAsync(direct, m3, "", null, 3);
        check(sD.Questions.Count == 3 && await Denied(() => topics.StartModulePreviewQuizAsync(direct, m1, "", null, 3)), p + "P8 Direkter Modul-Co-Author testet sein Modul, aber kein fremdes");
        check(await Denied(() => topics.GetTopicPreviewAsync(direct, t1.Id)), p + "P9 Direkte Modulrechte gewähren keine Themenvorschau");
        check(await Denied(() => topics.StartModulePreviewQuizAsync(outsider, m1, "", null, 3)) && await Denied(() => topics.GetTopicPreviewAsync(outsider, t1.Id)) && await Denied(() => topics.GetModulePreviewAsync(outsider, m1, null)), p + "P10 Fremde: weder Modul noch Thema noch Karten");
        if (restricted is not null)
            check(await Denied(() => topics.StartModulePreviewQuizAsync(restricted, m1, "", null, 3)) && await Denied(() => topics.GetTopicPreviewAsync(restricted, t1.Id)), p + "P11 Eingeschränkte Lernprofile sind ausgeschlossen");

        // Lernvorschau (Karten): Entwurfsstand, aktuelle Berechtigung
        var view = await topics.GetModulePreviewAsync(co, m1, null);
        check(view.Cards.Count == 1 && view.Cards[0].Term == "Karte " + n1 && view.OwnerName == "Pia Preview", p + "P12 Lernvorschau liefert die Karten des Entwurfsstands (Co-Author, Themenkontext)");
        await repo.UpdateCardAsync(cards[m1].Id, new CardJson { Module = n1, Category = "K", Term = "Karte neu " + tag, Question = "Frage?", Definition = "Definition" }, owner, (await repo.GetCardAsync(cards[m1].Id, owner))!.UpdatedAt);
        check((await topics.GetModulePreviewAsync(co, m1, null)).Cards[0].Term == "Karte neu " + tag, p + "P13 Die Vorschau verwendet den aktuellen Entwurfsstand (Kartenänderung sofort sichtbar)");
        var newQ = await quiz.CreateQuestionAsync(m1, Q(cards[m1].Id, "Neue Entwurfsfrage " + tag), owner);
        var cfg1 = (await quiz.GetConfigViewAsync(m1, owner)).Config.Clone(); cfg1.QuestionCount = 4; await quiz.SaveConfigAsync(m1, cfg1, owner);
        var sNew = await topics.StartModulePreviewQuizAsync(co, m1, "", null, 4);
        check(sNew.Questions.Any(q => q.Question.Contains("Neue Entwurfsfrage")), p + "P14 Die Quizvorschau enthält neue, unveröffentlichte Fragen des Entwurfs");

        // ─── Themenvorschau und Prüfungsstruktur ───
        var tp = await topics.GetTopicPreviewAsync(co, t1.Id);
        check(tp.Previewable[m1] && !tp.Previewable[m2] && tp.Detail.IsDraft, p + "P15 Themenvorschau des Entwurfs: zulässige Module testbar, Mehrfachzuordnung ohne Recht nicht");
        var m4 = (await repo.CreateModuleAsync(new ModuleCreateRequest { Name = "PvD-" + tag }, owner)).Id;
        check(!(await topics.GetTopicPreviewAsync(co, t1.Id)).Previewable.ContainsKey(m4), p + "P16 Ein noch nicht zugeordnetes Modul erscheint nicht");
        await topics.AddModuleAsync(owner, t1.Id, m4);
        check((await topics.GetTopicPreviewAsync(co, t1.Id)).Previewable.GetValueOrDefault(m4), p + "P17 Nachträglich hinzugefügtes Modul ist im Themenkontext sofort testbar");
        await topics.RemoveModuleAsync(owner, t1.Id, m4);

        var examCfg = await topics.GetExamConfigAsync(owner, t1.Id);
        examCfg.ModuleExams[m1] = new TopicModuleExamConfig { Enabled = true, QuestionCount = 2, PassPercent = 50, FreeText = false, ShuffleQuestions = false };
        examCfg.FinalExam = new TopicFinalExamConfig { Enabled = true, QuestionCount = 4, PassPercent = 50, FreeText = false, RandomSelection = false, IncludeTopicQuestions = true };
        await topics.SaveExamConfigAsync(owner, t1.Id, examCfg);
        await topics.CreateQuestionAsync(owner, t1.Id, QT("Themenfrage " + tag));
        await topics.CreateQuestionAsync(owner, t1.Id, QT("Themenfrage 2 " + tag));
        baseline = await Snapshot(db);   // nach den Vorbereitungen (Fragen, Konfiguration): ab hier darf die Vorschau nichts mehr verändern
        var attemptsBefore = (await db.QueryAsync("SELECT COUNT(*) AS n FROM topic_exam_attempts"))[0]["n"];
        var modExam = await topics.StartPreviewModuleExamAsync(co, t1.Id, m1);
        var modRes = await topics.SubmitPreviewAsync(co, Answers(modExam));
        check(modExam.Questions.Count == 2 && modRes.Percentage == 100 && string.IsNullOrEmpty(modRes.ResultId), p + "P18 Modulprüfung des Themenentwurfs im Testmodus (Konfiguration der Themenprüfung, Testauswertung)");
        var (finalSession, excluded) = await topics.StartPreviewFinalExamAsync(co, t1.Id);
        var finalRes = await topics.SubmitPreviewAsync(co, Answers(finalSession));
        check(finalSession.Questions.Count == 4 && excluded == 1 && finalRes.Percentage == 100, p + "P19 Abschlussprüfung des Entwurfs im Testmodus; Modul mit Mehrfachzuordnung ohne Recht wird ausgeschlossen (ohne Inhalte offenzulegen)");
        check((await db.QueryAsync("SELECT COUNT(*) AS n FROM topic_exam_attempts"))[0]["n"] == attemptsBefore && await Snapshot(db) == baseline, p + "P20 Prüfungsvorschau erzeugt keinen Prüfungsversuch, keinen Abschluss, keine Ergebnisse und keinen Lernfortschritt");
        check(await Denied(() => topics.StartPreviewModuleExamAsync(co, t1.Id, m2)) && await Denied(() => topics.StartPreviewModuleExamAsync(outsider, t1.Id, m1)) && await Denied(() => topics.StartPreviewFinalExamAsync(outsider, t1.Id)), p + "P21 Prüfungsvorschau: Mehrfachzuordnung und Fremde gesperrt");

        // ─── reguläre Lernrechte unverändert ───
        check(await repo.ResolveModuleAccessAsync(co, m1) is null && await Denied(() => quiz.StartSessionAsync(m1, "", null, 3, co)), p + "P22 Das Vorschaurecht ist kein Lernrecht: reguläres Lernen und reguläres Quiz bleiben für Co-Autoren gesperrt");
        await repo.SaveUserCardStateAsync(new UserCardState { UserSub = owner, CardId = cards[m1].Id, IsChecked = true });   // reguläres Quiz nutzt die „gecheckten“ Karten des Lernenden
        var regular = await quiz.StartSessionAsync(m1, "", null, 3, owner);
        check(regular.SessionId.Length > 0 && (await db.QueryAsync("SELECT COUNT(*) AS n FROM quiz_sessions"))[0]["n"] != "0", p + "P23 Das reguläre Quiz des Eigentümers speichert weiterhin seine Sitzung (Vergleich: Vorschau speichert nichts)");
        baseline = await Snapshot(db);

        // ─── Entzug ───
        var open = await topics.StartModulePreviewQuizAsync(co, m1, "", null, 3);
        await topics.RemoveCoAuthorAsync(owner, t1.Id, co);
        var revoked = false; string msg = "";
        try { await topics.SubmitPreviewAsync(co, Answers(open)); } catch (KeyNotFoundException ex) { revoked = true; msg = ex.Message; }
        check(revoked && msg == TopicService.PreviewRevokedMessage && topics.GetPreviewScope(open.SessionId, co) is null, p + "P24 Themenentzug während geöffneter Vorschau: Abgabe verweigert (Meldung), Sitzung verworfen, keine Lösungen");
        check(await Denied(() => topics.GetModulePreviewAsync(co, m1, null)) && await Denied(() => topics.StartModulePreviewQuizAsync(co, m1, "", null, 3)) && await Denied(() => topics.GetTopicPreviewAsync(co, t1.Id)), p + "P25 Nach dem Entzug: weder Karten noch Quiz noch Themenvorschau");
        var openD = await topics.StartModulePreviewQuizAsync(direct, m3, "", null, 3);
        await topics.RemoveModuleCoAuthorAsync(owner, m3, direct);
        check(await Denied(() => topics.SubmitPreviewAsync(direct, Answers(openD))) && await Denied(() => topics.GetModulePreviewAsync(direct, m3, null)), p + "P26 Entzug der direkten Modulfreigabe wirkt sofort auch in der offenen Vorschau");
        // Themenentzug beendet nur die abgeleitete Quelle, direkte Freigabe bleibt bestehen (ADR 0053)
        await topics.AddCoAuthorAsync(owner, t1.Id, co); await topics.AddModuleCoAuthorAsync(owner, m1, co);
        await topics.RemoveCoAuthorAsync(owner, t1.Id, co);
        check((await topics.StartModulePreviewQuizAsync(co, m1, "", null, 3)).Questions.Count > 0 && await Denied(() => topics.GetTopicPreviewAsync(co, t1.Id)), p + "P27 Themenentzug lässt die direkte Modulfreigabe (und damit die Modulvorschau) bestehen, die Themenvorschau entfällt");
        var foreign = await topics.StartModulePreviewQuizAsync(owner, m1, "", null, 3);
        check(await Denied(() => topics.SubmitPreviewAsync(outsider, Answers(foreign))) && (await topics.SubmitPreviewAsync(owner, Answers(foreign))).Percentage == 100, p + "P28 Eine Vorschausitzung ist an den Handelnden gebunden (kein Übernehmen fremder Sitzungen)");
        check(await Snapshot(db) == baseline, p + "P29 Nach allen Vorschauen und Entzügen bleiben die produktiven Tabellen unverändert");
    }
}
