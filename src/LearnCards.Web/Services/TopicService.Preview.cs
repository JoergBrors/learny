using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>
/// Lern- und Prüfungsvorschau für Content-Co-Autoren (ADR 0054). <b>Berechtigung</b> (immer aktuell gegen die Datenbank, vor Start und vor jeder Abgabe): Modulvorschau = Bearbeitungsrecht am Modul
/// (<see cref="GetModuleEditAccessAsync"/>: Eigentümer, direkter Modul-Co-Author oder abgeleitet aus einem Themen-Co-Author; Mehrfachzuordnungs-Schutz aus ADR 0053 inklusive); Themenvorschau =
/// Eigentümer oder Co-Author dieses Themas, jeweils nur mit den Modulen, an denen die Bearbeitung zulässig ist. Es gibt kein reguläres Lernrecht (ADR 0015/0029) und keinen Weg für MCP/REST.
/// Gelesen wird der Entwurfsstand; gespeichert wird nichts.
/// </summary>
public partial class TopicService
{
    public const string PreviewRevokedMessage = "Dein Zugriff auf diese Vorschau wurde entzogen.";

    /// <summary>Aktuelles Vorschaurecht an einem Modul; <c>null</c> = keines.</summary>
    public async Task<ModuleEditAccess?> GetModulePreviewAccessAsync(string actorId, string moduleId)
    {
        var access = await GetModuleEditAccessAsync(actorId, moduleId);
        return access.CanEdit ? access : null;
    }

    private async Task<ModuleEditAccess> RequireModulePreviewAsync(string actorId, string moduleId)
        => await GetModulePreviewAccessAsync(actorId, moduleId) ?? throw new KeyNotFoundException(PreviewRevokedMessage);

    /// <summary>Karten des Moduls für die Lernvorschau (Entwurfsstand, aktive Karten, in der Reihenfolge des Moduls). Erneute Berechtigungsprüfung bei jedem Aufruf.</summary>
    public async Task<(ModuleInfo Module, List<Card> Cards, List<string> Categories, string OwnerName)> GetModulePreviewAsync(string actorId, string moduleId, string? category)
    {
        var access = await RequireModulePreviewAsync(actorId, moduleId);
        var module = await _repo.GetModuleAsync(moduleId, access.OwnerUserId) ?? throw new KeyNotFoundException(PreviewRevokedMessage);
        var cards = await _repo.ListReadableCardsAsync(access.OwnerUserId, moduleId, string.IsNullOrEmpty(category) ? null : category);
        var categories = await _repo.ListReadableCategoriesAsync(access.OwnerUserId, moduleId);
        return (module, cards, categories, (await _users.GetAsync(access.OwnerUserId))?.DisplayName ?? "");
    }

    public async Task<QuizConfig> GetModulePreviewConfigAsync(string actorId, string moduleId)
    {
        await RequireModulePreviewAsync(actorId, moduleId);
        return await _quiz.GetPreviewConfigAsync(moduleId);
    }

    /// <summary>Startet das Modulquiz im Testmodus (Konfiguration, Fragenzahl und Bewertung wie regulär; Entwurfsstand; nichts wird gespeichert).</summary>
    public async Task<QuizStartResponse> StartModulePreviewQuizAsync(string actorId, string moduleId, string category, string? cardId, int requestedCount, CancellationToken ct = default)
    {
        var access = await RequireModulePreviewAsync(actorId, moduleId);
        return await _quiz.StartModulePreviewAsync(actorId, access.OwnerUserId, moduleId, category, cardId, requestedCount, ct);
    }

    /// <summary>Bereich einer offenen Vorschausitzung des Handelnden; null, wenn unbekannt, fremd, abgelaufen oder verworfen.</summary>
    public PreviewScope? GetPreviewScope(string sessionId, string actorId) => _quiz.GetPreviewScope(sessionId, actorId);

    /// <summary>Verwirft eine offene Vorschausitzung (Vorschau verlassen, Zugriff entzogen).</summary>
    public void DiscardPreview(string sessionId, string actorId) => _quiz.DiscardPreview(sessionId, actorId);

    // ─── Themenvorschau ─────────────────────────────────────────────────────

    /// <summary>Entwurf des Themas für die Vorschau. Berechtigt: Eigentümer und Co-Autoren dieses Themas; je Modul wird angegeben, ob die Vorschau zulässig ist (Mehrfachzuordnung ohne Recht → nein).</summary>
    public async Task<(TopicDetail Detail, Dictionary<string, bool> Previewable, int TopicQuestions)> GetTopicPreviewAsync(string actorId, string topicId)
    {
        var topic = await GetTopicRawAsync(topicId);
        if (topic is null || await RoleForAsync(actorId, topic) == TopicRole.None || topic.Status == TopicStatuses.Archived) throw new KeyNotFoundException(PreviewRevokedMessage);
        var detail = await GetDetailAsync(actorId, topicId);
        var previewable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in detail.Modules.Where(x => !x.Unavailable && !x.RequiresOwnCopy)) previewable[m.ModuleId] = await CanEditModuleAsync(actorId, m.ModuleId);
        var tq = (await ListQuestionsAsync(actorId, topicId)).Count(q => q.Active);
        return (detail, previewable, tq);
    }

    /// <summary>Modul im Entwurf dieses Themas und Vorschau zulässig? (Themenrecht + Modulrecht, beides aktuell.)</summary>
    private async Task<TopicInfo> RequireTopicPreviewModuleAsync(string actorId, string topicId, string moduleId)
    {
        var topic = await GetTopicRawAsync(topicId);
        if (topic is null || await RoleForAsync(actorId, topic) == TopicRole.None || topic.Status == TopicStatuses.Archived) throw new KeyNotFoundException(PreviewRevokedMessage);
        var inDraft = (await _db.QueryAsync("SELECT 1 FROM topic_modules WHERE topic_id = @t AND module_id = @m", new (string, object?)[] { ("t", topic.Id), ("m", moduleId) })).Count > 0;
        if (!inDraft || !await CanEditModuleAsync(actorId, moduleId)) throw new KeyNotFoundException(PreviewRevokedMessage);
        return topic;
    }

    /// <summary>Modulprüfung des Themenentwurfs im Testmodus (Konfiguration der Themenprüfung, Fragen aus dem Entwurfsfragenpool des Moduls).</summary>
    public async Task<QuizStartResponse> StartPreviewModuleExamAsync(string actorId, string topicId, string moduleId, CancellationToken ct = default)
    {
        var topic = await RequireTopicPreviewModuleAsync(actorId, topicId, moduleId);
        var cfg = (await LoadExamConfigAsync(topic.Id)).ModuleExams.GetValueOrDefault(moduleId);
        if (cfg is null || !cfg.Enabled) throw new QuizRuleException("Für dieses Modul ist im Entwurf keine Prüfung aktiv.", 403);
        var allowed = cfg.AllowedTypes();
        var counts = await QuestionCountsByTypeAsync(moduleId);
        var available = allowed.Sum(t => counts.GetValueOrDefault(t));
        if (available < cfg.QuestionCount)
            throw new ArgumentException($"Die Modulprüfung benötigt {cfg.QuestionCount} Fragen, es sind aber nur {available} passende Fragen verfügbar (erlaubte Fragetypen: {TypeList(allowed)}).");
        var name = (await _db.QueryAsync("SELECT name FROM modules WHERE id = @m", new[] { ("m", (object?)moduleId) })).FirstOrDefault()?["name"] ?? "";
        var plan = new PreviewExamPlan { TopicId = topic.Id, Label = $"{topic.Name} · {name} (Vorschau)", Config = cfg.ToQuizConfig(), Sources = new() { (moduleId, cfg.QuestionCount) } };
        return await _quiz.StartPreviewExamAsync(actorId, plan, ct);
    }

    /// <summary>Abschlussprüfung des Themenentwurfs im Testmodus: Fragen aus den Entwurfsfragenpools der Module, an denen die Vorschau zulässig ist, plus eigene Themenfragen.</summary>
    public async Task<(QuizStartResponse Session, int ExcludedModules)> StartPreviewFinalExamAsync(string actorId, string topicId, CancellationToken ct = default)
    {
        var topic = await GetTopicRawAsync(topicId);
        if (topic is null || await RoleForAsync(actorId, topic) == TopicRole.None || topic.Status == TopicStatuses.Archived) throw new KeyNotFoundException(PreviewRevokedMessage);
        var fin = (await LoadExamConfigAsync(topic.Id)).FinalExam;
        if (!fin.Enabled) throw new QuizRuleException("Für dieses Thema ist im Entwurf keine Abschlussprüfung aktiv.", 403);
        var allowed = fin.AllowedTypes();
        var modules = (await _db.QueryAsync("SELECT module_id FROM topic_modules WHERE topic_id = @t ORDER BY sort_order", new[] { ("t", (object?)topic.Id) })).Select(r => r["module_id"] ?? "").ToList();
        var sources = new List<(string Id, int Available)>(); var excluded = 0;
        foreach (var m in modules)
        {
            if (!await CanEditModuleAsync(actorId, m)) { excluded++; continue; }   // Mehrfachzuordnung ohne Recht: nicht einbeziehen, nichts offenlegen
            var c = await QuestionCountsByTypeAsync(m);
            sources.Add((m, allowed.Sum(t => c.GetValueOrDefault(t))));
        }
        var topicQuestions = (await ListQuestionsAsync(actorId, topicId)).Where(q => q.Active && allowed.Contains(q.Type)).ToList();
        const string TopicSource = "__topic__";
        if (fin.IncludeTopicQuestions && topicQuestions.Count > 0) sources.Add((TopicSource, topicQuestions.Count));
        var alloc = AllocateQuestionCounts(sources, fin.QuestionCount, fin.Distribution, fin.ManualCounts, TopicSource, out var error);
        if (alloc is null) throw new ArgumentException($"{error}. Ergänze Fragen oder reduziere die Anzahl (erlaubte Fragetypen: {TypeList(allowed)}).");
        var cfg = fin.ToQuizConfig();
        var plan = new PreviewExamPlan
        {
            TopicId = topic.Id, Label = $"{topic.Name} · Abschlussprüfung (Vorschau)", Config = cfg,
            Sources = alloc.Where(kv => kv.Key != TopicSource).Select(kv => (kv.Key, kv.Value)).ToList(), TopicQuestions = topicQuestions, TopicQuestionCount = alloc.GetValueOrDefault(TopicSource),
        };
        plan.Config.QuestionCount = Math.Clamp(plan.ExpectedCount, 1, 50);
        return (await _quiz.StartPreviewExamAsync(actorId, plan, ct), excluded);
    }

    // ─── Abgabe ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Wertet eine Vorschau aus. <b>Berechtigung wird vor der Abgabe erneut geprüft</b>: Modulvorschau → aktuelles Bearbeitungsrecht am Modul; Themenvorschau → aktuelles Themenrecht, Modul(e) weiterhin im
    /// Entwurf und bearbeitbar. Bei Entzug wird die Sitzung verworfen (keine Lösungen, kein Ergebnis).
    /// </summary>
    public async Task<QuizResultResponse> SubmitPreviewAsync(string actorId, QuizSubmitRequest req, CancellationToken ct = default)
    {
        var scope = _quiz.GetPreviewScope(req.SessionId, actorId) ?? throw new InvalidOperationException("Die Vorschausitzung wurde nicht gefunden oder ist abgelaufen — starte die Vorschau neu.");
        try
        {
            if (scope.Kind == PreviewScope.Module) await RequireModulePreviewAsync(actorId, scope.ModuleIds[0]);
            else foreach (var m in scope.ModuleIds) await RequireTopicPreviewModuleAsync(actorId, scope.TopicId ?? "", m);
            if (scope.Kind == PreviewScope.Topic && scope.ModuleIds.Count == 0)
            {
                var topic = await GetTopicRawAsync(scope.TopicId ?? "");
                if (topic is null || await RoleForAsync(actorId, topic) == TopicRole.None) throw new KeyNotFoundException(PreviewRevokedMessage);
            }
        }
        catch (KeyNotFoundException)
        {
            _quiz.DiscardPreview(req.SessionId, actorId);
            throw new KeyNotFoundException(PreviewRevokedMessage);
        }
        return await _quiz.SubmitPreviewAsync(req, actorId, ct);
    }
}
