using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>
/// Themenprüfungen (ADR 0027) laufen auf der bestehenden Quiz-Engine (ADR 0019): gleiche Sitzungen mit serverseitigen Lösungen, gleiche Bewertung
/// (<c>SubmitQuizAsync</c>/<c>GradeSessionAsync</c>), gleiche Ergebnistabelle. Hier wird nur die <b>Zusammenstellung</b> der Fragen aus den eingefrorenen Pools ergänzt.
/// </summary>
public partial class QuizService
{
    /// <summary>
    /// Baut aus einem vom <c>TopicService</c> erstellten Plan eine Quizsitzung. Verwendet ausschließlich gespeicherte Fragen der Quellen (keine KI-Generierung, keine aus Karten
    /// abgeleiteten Fragen); fehlen Fragen, wird nicht gestartet. Die Sitzung ist an das Prüfungsmodul der Themenversion gebunden.
    /// </summary>
    public async Task<QuizStartResponse> StartTopicExamSessionAsync(TopicExamPlan plan, string userSub, CancellationToken ct = default)
    {
        await CleanupSessionsIfDueAsync(false, ct);
        var access = await _repo.ResolveModuleAccessAsync(userSub, plan.SessionModuleId);
        if (access is null || access.Context != CardRepository.ContextTopic) throw new InvalidOperationException("Das Prüfungsmodul wurde nicht gefunden.");

        var config = plan.Config;
        var allowed = config.AllowedTypes();
        var rnd = new Random();
        var picked = new List<QuizSessionQuestion>();
        foreach (var (moduleId, count) in plan.Sources.Where(s => s.Count > 0))
        {
            var cards = (await _repo.ListActiveCardsRawAsync(moduleId)).ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
            var pool = (await _pool.ListAsync(moduleId, null, null, true, ct)).Where(q => allowed.Contains(q.Type)).ToList();
            pool = config.ShuffleQuestions ? pool.OrderBy(_ => rnd.Next()).ToList() : pool.OrderBy(q => q.CreatedAt).ThenBy(q => q.Id, StringComparer.Ordinal).ToList();
            picked.AddRange(pool.Take(count).Select(q => FromPool(q, cards, ephemeral: false)));
        }
        if (picked.Count < plan.ExpectedCount)
            throw new InvalidOperationException($"Es sind nur {picked.Count} von {plan.ExpectedCount} geforderten Prüfungsfragen verfügbar.");

        if (config.ShuffleQuestions) picked = picked.OrderBy(_ => rnd.Next()).ToList();
        if (config.ShuffleAnswers) foreach (var q in picked.Where(q => QuizTypes.IsChoice(q.Type))) ShuffleOptions(q, rnd);

        var session = new QuizSessionRecord
        {
            UserSub = userSub, ModuleId = plan.SessionModuleId, CardId = null, Category = plan.Label, Context = CardRepository.ContextTopic,
            ModuleVersion = plan.VersionNumber, Config = config, Questions = picked,
        };
        await _pool.SaveSessionAsync(session);

        return new QuizStartResponse
        {
            SessionId = session.Id, ModuleId = plan.SessionModuleId, Category = session.Category, CardId = "", PassPercent = config.PassPercent, ShowCorrectAnswers = config.ShowCorrectAnswers,
            Questions = picked.Select(q => new QuizQuestion
            {
                Question = q.Question, Topic = q.Topic, SourceCardId = q.CardId ?? "", SourceTerm = q.SourceTerm,
                QuestionId = SessionQuestionKey(q, picked), Type = q.Type, Options = q.Options.ToList(),
            }).ToList(),
        };
    }
}
