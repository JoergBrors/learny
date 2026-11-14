using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Fragenpool und Quizkonfiguration bei Modul-Co-Autoren (ADR 0052/0053). Die Schreibpfade <c>QuizService.CreateQuestionAsync</c>, <c>UpdateQuestionAsync</c>, <c>SetQuestionActiveAsync</c> und
/// <c>SaveConfigAsync</c> prüfen über <c>RequireOwnedModuleAsync</c> (→ <c>ResolveModuleAccessAsync</c>) ausschließlich das <b>Eigentum</b> des übergebenen Benutzers: Sie arbeiten unter dem Eigentümer, den der
/// Aufrufer über <c>TopicService.ResolveModuleEditOwnerAsync</c> ermittelt. Mit der Identität eines Co-Autors (oder eines entzogenen Benutzers) werden sie abgewiesen. Läuft identisch auf SQLite und PostgreSQL.
/// </summary>
public static class QuizPoolCoAuthorTests
{
    private static async Task<bool> NotWritten(Func<Task<bool>> tryWrite)
    {
        try { return !await tryWrite(); }
        catch (Exception e) when (e is KeyNotFoundException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or CardRepository.CardConflictException) { return true; }
    }

    public static async Task RunAsync(Action<bool, string> check, string tmp)
    {
        Console.WriteLine("\nFragenpool und Quizkonfiguration bei Modul-Co-Autoren (ADR 0053), im Prozess auf SQLite");
        var e = await InvitationUnitTests.Env.CreateAsync(Path.Combine(tmp, "poolcoauthor"));
        var pool = new QuizPoolService(e.Db);
        var comp = new CompetencyService(e.Db, e.Repo, e.Users);
        var quiz = Build(e.Repo, pool, comp, e.Db, e.Users);
        var n = 0;
        await CoreAsync("SQLite", e.Db, e.Topics, e.Repo, pool, quiz, async name => await e.AddWorkforceUserAsync(name, $"{name.ToLowerInvariant().Replace(' ', '.')}-q{++n}@example.org"), check);
    }

    /// <summary>QuizService für Dienstetests: Berechtigungs- und Poolpfade brauchen weder KI noch Zertifikate; nicht benötigte Abhängigkeiten bleiben leer.</summary>
    public static QuizService Build(CardRepository repo, QuizPoolService pool, CompetencyService comp, IDatabase db, UserService users)
        => new(repo, new OpenAiClient(new AppConfig(), new OfflineHttpFactory()), pool, null!, comp, new ModuleVersionService(db, repo, users), null!, NullLogger<QuizService>.Instance);

    /// <summary>Ohne KI-Schlüssel (Offline-Fallback, ADR 0008): es wird nie ein Netzwerkaufruf ausgeführt.</summary>
    private sealed class OfflineHttpFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private static QuizQuestionInput Question(string cardId, string text) => new()
    {
        CardId = cardId, Type = QuizTypes.SingleChoice, Question = text, Options = new() { "A", "B", "C", "D" }, CorrectIndexes = new() { 1 }, Explanation = "Weil B.",
    };

    public static async Task CoreAsync(string label, IDatabase db, TopicService topics, CardRepository repo, QuizPoolService pool, QuizService quiz, Func<string, Task<string>> newUser, Action<bool, string> check)
    {
        var p = label + " ";
        var owner = await newUser("Pia Pool"); var co = await newUser("Cleo Co");
        var name = "Pool-" + Guid.NewGuid().ToString("N")[..6];
        var topicOnlyName = "PoolT-" + Guid.NewGuid().ToString("N")[..6];
        var mDirect = (await repo.CreateModuleAsync(new ModuleCreateRequest { Name = name }, owner)).Id;
        var mTopic = (await repo.CreateModuleAsync(new ModuleCreateRequest { Name = topicOnlyName }, owner)).Id;
        var mMulti = (await repo.CreateModuleAsync(new ModuleCreateRequest { Name = name + "M" }, owner)).Id;
        var cDirect = await repo.CreateCardAsync(new CardJson { Module = name, Category = "K", Term = "T", Question = "Q?", Definition = "D" }, owner);
        var cTopic = await repo.CreateCardAsync(new CardJson { Module = topicOnlyName, Category = "K", Term = "T", Question = "Q?", Definition = "D" }, owner);
        var cMulti = await repo.CreateCardAsync(new CardJson { Module = name + "M", Category = "K", Term = "T", Question = "Q?", Definition = "D" }, owner);
        var t1 = await topics.CreateAsync(owner, "PT1 " + name, ""); var t2 = await topics.CreateAsync(owner, "PT2 " + name, "");
        await topics.AddModuleAsync(owner, t1.Id, mTopic); await topics.AddModuleAsync(owner, t1.Id, mMulti); await topics.AddModuleAsync(owner, t2.Id, mMulti);
        async Task<List<QuizPoolQuestion>> Pool(string m) => await pool.ListAsync(m);

        // Schreibpfade des Pools, direkt am Dienst (unabhängig vom Razor-Editor):
        //   CreateQuestionAsync / UpdateQuestionAsync / SetQuestionActiveAsync / SaveConfigAsync → RequireOwnedModuleAsync → ResolveModuleAccessAsync(userSub).CanEdit (nur Eigentümer)
        foreach (var kind in new[] { "direkt", "abgeleitet" })
        {
            var module = kind == "direkt" ? mDirect : mTopic; var card = kind == "direkt" ? cDirect : cTopic;
            if (kind == "direkt") await topics.AddModuleCoAuthorAsync(owner, mDirect, co); else await topics.AddCoAuthorAsync(owner, t1.Id, co);

            // vor dem Entzug: der vom Dienst gelieferte Kontext erlaubt Anlegen, Ändern, Deaktivieren und Konfiguration
            var ctx = await topics.ResolveModuleEditOwnerAsync(co, module);
            var created = await quiz.CreateQuestionAsync(module, Question(card.Id, "Frage vor Entzug " + kind), ctx);
            var updated = await quiz.UpdateQuestionAsync(module, created.Id, Question(card.Id, "Frage geändert " + kind), ctx);
            await quiz.SetQuestionActiveAsync(module, created.Id, false, ctx);
            var cfg = (await quiz.GetConfigViewAsync(module, ctx)).Config.Clone(); cfg.QuestionCount = 7;
            await quiz.SaveConfigAsync(module, cfg, ctx);
            var q1 = (await Pool(module)).Single(q => q.Id == created.Id);
            check(ctx == owner && updated.Question.EndsWith("geändert " + kind) && !q1.Active && (await pool.GetConfigAsync(module)).QuestionCount == 7, p + $"Q1 ({kind}) vor dem Entzug: Fragenpool anlegen/ändern/deaktivieren und Quizkonfiguration über den Bearbeitungskontext");

            // mit der eigenen Identität des Co-Autors (so arbeitet z. B. das Quiz-Einstellungen-Panel ohne Kontext) ist jeder Pfad gesperrt: Berechtigungsentscheidung liegt beim Aufrufer
            check(await NotWritten(async () => { await quiz.CreateQuestionAsync(module, Question(card.Id, "Fremd"), co); return true; })
                  && await NotWritten(async () => { await quiz.UpdateQuestionAsync(module, created.Id, Question(card.Id, "Fremd"), co); return true; })
                  && await NotWritten(async () => { await quiz.SetQuestionActiveAsync(module, created.Id, true, co); return true; })
                  && await NotWritten(async () => { await quiz.SaveConfigAsync(module, cfg, co); return true; }), p + $"Q2 ({kind}) Die Dienste arbeiten nur unter dem Eigentümerkontext: die Identität des Co-Autors selbst wird abgewiesen");

            // Entzug
            var before = (await Pool(module)).OrderBy(q => q.Id).Select(q => $"{q.Id}|{q.Question}|{q.Active}").ToList(); var cfgBefore = (await pool.GetConfigAsync(module)).QuestionCount;
            if (kind == "direkt") await topics.RemoveModuleCoAuthorAsync(owner, mDirect, co); else await topics.RemoveCoAuthorAsync(owner, t1.Id, co);
            var staleCtx = ctx;   // im geöffneten Editor beim Laden ermittelt — der Wächter (StillAllowedAsync) löst vor jedem Schreiben neu auf
            var fresh = await topics.ResolveModuleEditOwnerAsync(co, module);
            check(fresh == co && staleCtx == owner, p + $"Q3 ({kind}) nach dem Entzug liefert die Auflösung den Handelnden selbst; der alte Kontext ist nur noch im Aufrufer vorhanden");
            // Ablauf des Editors nach dem Entzug: Auflösung → Kontext = Handelnder → alle Pfade abgewiesen
            check(await NotWritten(async () => { await quiz.CreateQuestionAsync(module, Question(card.Id, "Nach Entzug"), fresh); return true; })
                  && await NotWritten(async () => { await quiz.UpdateQuestionAsync(module, created.Id, Question(card.Id, "Nach Entzug"), fresh); return true; })
                  && await NotWritten(async () => { await quiz.SetQuestionActiveAsync(module, created.Id, true, fresh); return true; })
                  && await NotWritten(async () => { var c2 = cfg.Clone(); c2.QuestionCount = 3; await quiz.SaveConfigAsync(module, c2, fresh); return true; })
                  && await NotWritten(async () => { await quiz.ListQuestionsAsync(module, fresh); return true; }), p + $"Q4 ({kind}) nach dem Entzug werden Fragenpool anlegen/ändern/aktivieren, Konfiguration und Lesen der Lösungen abgewiesen");
            var after = (await Pool(module)).OrderBy(q => q.Id).Select(q => $"{q.Id}|{q.Question}|{q.Active}").ToList();
            check(before.SequenceEqual(after) && (await pool.GetConfigAsync(module)).QuestionCount == cfgBefore, p + $"Q5 ({kind}) Fragenpool und Konfiguration bleiben nach den abgewiesenen Änderungen unverändert");
            // Eigentümer
            var ownerQ = await quiz.CreateQuestionAsync(module, Question(card.Id, "Eigentümer " + kind), owner);
            await quiz.SetQuestionActiveAsync(module, created.Id, true, owner);
            check(ownerQ.Id.Length > 0 && (await Pool(module)).Single(q => q.Id == created.Id).Active, p + $"Q6 ({kind}) der Eigentümer bearbeitet den Fragenpool weiterhin");
            // erneute Gewährung
            if (kind == "direkt") await topics.AddModuleCoAuthorAsync(owner, mDirect, co); else await topics.AddCoAuthorAsync(owner, t1.Id, co);
            var again = await topics.ResolveModuleEditOwnerAsync(co, module);
            var q7 = await quiz.CreateQuestionAsync(module, Question(card.Id, "Wieder erlaubt " + kind), again);
            check(again == owner && q7.Id.Length > 0, p + $"Q7 ({kind}) nach erneuter Gewährung ist die Bearbeitung des Fragenpools wieder möglich");
            if (kind == "direkt") await topics.RemoveModuleCoAuthorAsync(owner, mDirect, co); else await topics.RemoveCoAuthorAsync(owner, t1.Id, co);
        }

        // Mehrfachzuordnung: Modul auch in Thema 2 ohne Recht → abgeleitet kein Zugriff; Fragenpool unverändert; mit Recht an beiden Themen erlaubt; direkte Freigabe unabhängig
        await topics.AddCoAuthorAsync(owner, t1.Id, co);
        var ctxMulti = await topics.ResolveModuleEditOwnerAsync(co, mMulti);
        var multiBefore = (await Pool(mMulti)).Count;
        check(ctxMulti == co && await NotWritten(async () => { await quiz.CreateQuestionAsync(mMulti, Question(cMulti.Id, "Mehrfach"), ctxMulti); return true; }) && (await Pool(mMulti)).Count == multiBefore, p + "Q8 Mehrfachzuordnung (Thema 2 ohne Recht): keine Fragenpool-Änderung, Pool unverändert");
        await topics.AddCoAuthorAsync(owner, t2.Id, co);
        var both = await topics.ResolveModuleEditOwnerAsync(co, mMulti);
        check(both == owner && (await quiz.CreateQuestionAsync(mMulti, Question(cMulti.Id, "Beide Themen"), both)).Id.Length > 0, p + "Q9 Mehrfachzuordnung mit Recht an beiden Themen: Fragenpool bearbeitbar");
        await topics.RemoveCoAuthorAsync(owner, t2.Id, co);
        await topics.AddModuleCoAuthorAsync(owner, mMulti, co);
        var direct = await topics.ResolveModuleEditOwnerAsync(co, mMulti);
        check(direct == owner && (await quiz.CreateQuestionAsync(mMulti, Question(cMulti.Id, "Direkt trotz Mehrfach"), direct)).Id.Length > 0, p + "Q10 Direkte Freigabe gilt unabhängig von der Mehrfachzuordnung");
        await topics.RemoveModuleCoAuthorAsync(owner, mMulti, co);
        check(await topics.ResolveModuleEditOwnerAsync(co, mMulti) == co, p + "Q11 Nach Entzug der direkten Freigabe und ohne Recht an Thema 2 kein Zugriff mehr");

        // Lernende (freigegeben/lernen) und Fremde bearbeiten den Fragenpool nie
        var outsider = await newUser("Olli Out");
        check(await NotWritten(async () => { await quiz.CreateQuestionAsync(mDirect, Question(cDirect.Id, "Fremd"), outsider); return true; }), p + "Q12 Fremde ohne Berechtigung: Fragenpool abgewiesen");
    }
}
