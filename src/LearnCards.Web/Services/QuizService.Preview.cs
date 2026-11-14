using System.Collections.Concurrent;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>Bereich einer Vorschausitzung (ADR 0054): ein Modul oder ein Themenentwurf mit den beteiligten Modulen. Dient der erneuten Berechtigungsprüfung bei der Abgabe.</summary>
public sealed record PreviewScope(string Kind, string? TopicId, IReadOnlyList<string> ModuleIds)
{
    public const string Module = "module";
    public const string Topic = "topic";
}

/// <summary>Aufbau einer Prüfungsvorschau für einen Themenentwurf: Quellen je Modul (Entwurfsfragenpool), optionale eigene Themenfragen, Konfiguration. Nichts davon wird gespeichert.</summary>
public sealed class PreviewExamPlan
{
    public string TopicId { get; set; } = "";
    public string Label { get; set; } = "";
    public QuizConfig Config { get; set; } = new();
    public List<(string ModuleId, int Count)> Sources { get; set; } = new();
    public List<TopicQuestion> TopicQuestions { get; set; } = new();
    public int TopicQuestionCount { get; set; }
    public int ExpectedCount => Sources.Sum(s => s.Count) + TopicQuestionCount;
}

/// <summary>
/// Lern- und Prüfungsvorschau (ADR 0054). Dieselbe Quiz-Engine wie das reguläre Quiz (Start und Bewertung), aber ohne jede Persistenz: Sitzungen leben nur im Arbeitsspeicher dieses Prozesses
/// (an den Handelnden gebunden, verfallen nach zwei Stunden), Ergebnisse werden nicht gespeichert, es entstehen keine Prüfungsversuche, Zertifikate, Statistiken oder Fortschritte.
/// Die <b>Berechtigung</b> prüft ausschließlich <c>TopicService</c> (Bearbeitungsrecht an Modul bzw. Thema) — vor Start <b>und</b> vor der Abgabe; diese Methoden vertrauen ihrem Aufrufer.
/// </summary>
public partial class QuizService
{
    private sealed record PreviewEntry(QuizSessionRecord Session, PreviewScope Scope, DateTime ExpiresAt);
    private static readonly ConcurrentDictionary<string, PreviewEntry> PreviewSessions = new();
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromHours(2);
    private const int MaxPreviewSessionsPerActor = 20;

    private static void PutPreview(QuizSessionRecord session, PreviewScope scope)
    {
        var now = DateTime.UtcNow;
        foreach (var (id, e) in PreviewSessions) if (e.ExpiresAt < now) PreviewSessions.TryRemove(id, out _);
        var mine = PreviewSessions.Where(kv => kv.Value.Session.UserSub == session.UserSub).OrderBy(kv => kv.Value.ExpiresAt).ToList();
        for (var i = 0; i <= mine.Count - MaxPreviewSessionsPerActor; i++) PreviewSessions.TryRemove(mine[i].Key, out _);
        PreviewSessions[session.Id] = new PreviewEntry(session, scope, now + PreviewLifetime);
    }

    /// <summary>Bereich einer offenen Vorschausitzung des Handelnden (für die erneute Berechtigungsprüfung); null, wenn unbekannt, fremd oder abgelaufen.</summary>
    public PreviewScope? GetPreviewScope(string sessionId, string actorId)
        => PreviewSessions.TryGetValue(sessionId ?? "", out var e) && e.Session.UserSub == actorId && e.ExpiresAt > DateTime.UtcNow ? e.Scope : null;

    /// <summary>Karten für Vorschau und Bewertung: alle aktiven Karten der Module (nicht nur die „gecheckten“ eines Benutzers — es gibt keinen Lernfortschritt in der Vorschau).</summary>
    private async Task<List<Card>> ResolvePreviewCardsAsync(IReadOnlyList<string> moduleIds, string? category, string? cardId)
    {
        var all = new List<Card>();
        foreach (var id in moduleIds) all.AddRange(await _repo.ListActiveCardsRawAsync(id));
        return all.Where(c => (string.IsNullOrWhiteSpace(cardId) || string.Equals(c.Id, cardId, StringComparison.OrdinalIgnoreCase))
                              && (string.IsNullOrEmpty(category) || c.Category == category))
                  .OrderBy(c => c.SortOrder).ThenBy(c => c.Term, StringComparer.Ordinal).ToList();
    }

    /// <summary>Konfiguration des Modulquiz für die Vorschau (Anzahl/Typen/Bewertung wie regulär).</summary>
    public async Task<QuizConfig> GetPreviewConfigAsync(string moduleId, CancellationToken ct = default) => await _pool.GetConfigAsync(moduleId, ct);

    /// <summary>Startet ein Modulquiz im Testmodus. <paramref name="ownerId"/> ist der vom Aufrufer berechtigte Eigentümerkontext; es wird nichts gespeichert.</summary>
    public async Task<QuizStartResponse> StartModulePreviewAsync(string actorId, string ownerId, string moduleId, string category, string? cardId, int requestedCount, CancellationToken ct = default)
    {
        var module = await _repo.GetModuleAsync(moduleId, ownerId) ?? throw new InvalidOperationException("Das ausgewählte Modul wurde nicht gefunden.");
        var access = new ModuleAccess(module, CardRepository.ContextPrivate, CanEdit: false);
        var (response, session) = await StartSessionCoreAsync(access, moduleId, category, cardId, requestedCount, ownerId, ct);
        session.UserSub = actorId;
        PutPreview(session, new PreviewScope(PreviewScope.Module, null, new[] { module.Id }));
        return response;
    }

    /// <summary>Startet eine Modul- oder Abschlussprüfung eines Themenentwurfs im Testmodus (Fragen aus den Entwurfsfragenpools der Module und den Themenfragen). Kein Versuch, keine Speicherung.</summary>
    public Task<QuizStartResponse> StartPreviewExamAsync(string actorId, PreviewExamPlan plan, CancellationToken ct = default)
        => StartPreviewExamCoreAsync(actorId, plan, ct);

    private async Task<QuizStartResponse> StartPreviewExamCoreAsync(string actorId, PreviewExamPlan plan, CancellationToken ct)
    {
        var config = plan.Config;
        var allowed = config.AllowedTypes();
        var rnd = new Random();
        var picked = new List<QuizSessionQuestion>();
        foreach (var (moduleId, count) in plan.Sources.Where(s => s.Count > 0))
        {
            var cards = (await _repo.ListActiveCardsRawAsync(moduleId)).ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
            var pool = (await _pool.ListAsync(moduleId, null, null, true, ct)).Where(q => allowed.Contains(q.Type)).ToList();
            pool = config.ShuffleQuestions ? pool.OrderBy(_ => rnd.Next()).ToList() : pool.OrderBy(q => q.CreatedAt).ThenBy(q => q.Id, StringComparer.Ordinal).ToList();
            picked.AddRange(pool.Take(count).Select(q => FromPool(q, cards, ephemeral: true)));
        }
        if (plan.TopicQuestionCount > 0)
        {
            var tq = plan.TopicQuestions.Where(q => q.Active && allowed.Contains(q.Type)).ToList();
            tq = config.ShuffleQuestions ? tq.OrderBy(_ => rnd.Next()).ToList() : tq.OrderBy(q => q.CreatedAt).ThenBy(q => q.Id, StringComparer.Ordinal).ToList();
            picked.AddRange(tq.Take(plan.TopicQuestionCount).Select(q => new QuizSessionQuestion
            {
                QuestionId = q.Id, CardId = null, SourceTerm = "", Topic = "", Type = q.Type, Question = q.Question, Options = q.Options.ToList(), CorrectIndexes = q.CorrectIndexes.ToList(),
                Explanation = q.Explanation, Origin = QuizOrigins.Manual, Ephemeral = true,
            }));
        }
        if (picked.Count < plan.ExpectedCount) throw new InvalidOperationException($"Es sind nur {picked.Count} von {plan.ExpectedCount} geforderten Prüfungsfragen verfügbar.");

        if (config.ShuffleQuestions) picked = picked.OrderBy(_ => rnd.Next()).ToList();
        if (config.ShuffleAnswers) foreach (var q in picked.Where(q => QuizTypes.IsChoice(q.Type))) ShuffleOptions(q, rnd);

        var moduleIds = plan.Sources.Select(s => s.ModuleId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var session = new QuizSessionRecord { UserSub = actorId, ModuleId = moduleIds.FirstOrDefault() ?? "", CardId = null, Category = plan.Label, Context = CardRepository.ContextPrivate, Config = config, Questions = picked };
        PutPreview(session, new PreviewScope(PreviewScope.Topic, plan.TopicId, moduleIds));
        return new QuizStartResponse
        {
            SessionId = session.Id, ModuleId = session.ModuleId, Category = session.Category, CardId = "", PassPercent = config.PassPercent, ShowCorrectAnswers = config.ShowCorrectAnswers,
            Questions = picked.Select(q => new QuizQuestion
            {
                Question = q.Question, Topic = q.Topic, SourceCardId = q.CardId ?? "", SourceTerm = q.SourceTerm,
                QuestionId = SessionQuestionKey(q, picked), Type = q.Type, Options = q.Options.ToList(),
            }).ToList(),
        };
    }

    /// <summary>Wertet eine Vorschausitzung mit der regulären Bewertung aus. Die Sitzung wird verbraucht; es entsteht kein Ergebnis, kein Versuch, kein Zertifikat.</summary>
    public async Task<QuizResultResponse> SubmitPreviewAsync(QuizSubmitRequest req, string actorId, CancellationToken ct = default)
    {
        if (!PreviewSessions.TryGetValue(req.SessionId ?? "", out var entry) || entry.Session.UserSub != actorId || entry.ExpiresAt <= DateTime.UtcNow)
            throw new InvalidOperationException("Die Vorschausitzung wurde nicht gefunden oder ist abgelaufen — starte die Vorschau neu.");
        if (!PreviewSessions.TryRemove(entry.Session.Id, out _)) throw new QuizRuleException("Diese Vorschau wurde bereits abgegeben.", 409);
        var access = new ModuleAccess(new ModuleInfo { Id = entry.Session.ModuleId }, CardRepository.ContextPrivate, CanEdit: false);
        var result = await GradeSessionAsync(entry.Session, req, actorId, access, ct, entry.Scope.ModuleIds);
        result.ResultId = ""; result.CertificateId = null;
        return result;
    }

    /// <summary>Verwirft eine offene Vorschausitzung (Berechtigung entzogen, Vorschau verlassen).</summary>
    public void DiscardPreview(string sessionId, string actorId)
    {
        if (PreviewSessions.TryGetValue(sessionId ?? "", out var e) && e.Session.UserSub == actorId) PreviewSessions.TryRemove(sessionId!, out _);
    }
}
