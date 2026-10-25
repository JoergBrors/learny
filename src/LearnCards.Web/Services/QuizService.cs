using System.Text.Json;
using LearnCards.Web.Api;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Services.Certificates;

namespace LearnCards.Web.Services;

/// <summary>Verletzung einer Quiz-Regel (Konfiguration, Berechtigung, Wiederholung); <see cref="Status"/> ist der passende HTTP-Status.</summary>
public sealed class QuizRuleException : InvalidOperationException
{
    public int Status { get; }
    public QuizRuleException(string message, int status = 400) : base(message) => Status = status;
}

/// <summary>
/// Einheitliche Quiz-Engine (ADR 0019): Frageauswahl (gespeicherter Pool, KI-Generierung, Offline-Fallback), Quizsitzung, Antwortvalidierung,
/// Bewertung und Ergebnisspeicherung für <b>Karten- und Modulquiz</b>. Alle Bewertungen laufen serverseitig anhand der in der Sitzung hinterlegten Lösungen —
/// der Client erhält vor der Abgabe nie die richtigen Antworten.
/// <para>
/// Fragetypen: <c>single_choice</c> und <c>multiple_response</c> werden lokal exakt bewertet (alles-oder-nichts, 10 Punkte), <c>free_text</c> per KI
/// (ohne Schlüssel bzw. bei KI-Fehler: lokale Stichwort-Heuristik als Offline-Fallback).
/// </para>
/// </summary>
public class QuizService
{
    private const int PageQuizCount = 3;
    private const double PointsPerQuestion = 10;

    private readonly CardRepository _repo;
    private readonly OpenAiClient _openAi;
    private readonly QuizPoolService _pool;
    private readonly CertificateService _certificates;
    private readonly Microsoft.Extensions.Logging.ILogger<QuizService> _log;

    public QuizService(CardRepository repo, OpenAiClient openAi, QuizPoolService pool, CertificateService certificates, Microsoft.Extensions.Logging.ILogger<QuizService> log)
    {
        _certificates = certificates;
        _repo = repo;
        _openAi = openAi;
        _pool = pool;
        _log = log;
    }

    public bool AiAvailable => _openAi.IsConfigured;

    private static long _lastCleanupTicks;

    /// <summary>Begrenzte Bereinigung abgelaufener Sitzungen — höchstens alle 10 Minuten (und beim Start).</summary>
    public async Task CleanupSessionsIfDueAsync(bool force = false, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastCleanupTicks);
        if (!force && now - last < TimeSpan.FromMinutes(10).Ticks) return;
        if (Interlocked.CompareExchange(ref _lastCleanupTicks, now, last) != last) return;
        try { await _pool.CleanupSessionsAsync(ct: ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning("Quiz: Sitzungsbereinigung fehlgeschlagen: {Message}", ex.Message); }
    }

    public Task<List<QuizHistoryEntry>> GetHistoryAsync(string userSub, string moduleId, string? cardId = null, int limit = 10) =>
        _repo.QuizHistoryDetailedAsync(userSub, moduleId, cardId, limit);

    public Task<int> DeleteHistoryEntryAsync(string userSub, string quizResultId) =>
        _repo.DeleteQuizResultAsync(userSub, quizResultId);

    // ─── Konfiguration und Fragenpool (Service-Schnittstelle für Auftrag 2) ─────────────

    /// <summary>Konfiguration eines Moduls; lesbar für jeden, der das Modul lernen darf. <see cref="QuizConfigView.CanEdit"/> nur für den Eigentümer.</summary>
    public async Task<QuizConfigView> GetConfigViewAsync(string moduleId, string userSub, CancellationToken ct = default)
    {
        var access = await _repo.ResolveModuleAccessAsync(userSub, moduleId) ?? throw new KeyNotFoundException("Modul nicht gefunden.");
        return new QuizConfigView
        {
            ModuleId = access.Module.Id, Config = await _pool.GetConfigAsync(access.Module.Id, ct), CanEdit = access.CanEdit,
            AiAvailable = AiAvailable, ModuleVersion = access.Module.GlobalVersion, Context = access.Context,
        };
    }

    /// <summary>Speichert die Konfiguration. Nur der Eigentümer eines privaten Moduls; freigegebene und globale Module sind schreibgeschützt.</summary>
    public async Task<QuizConfigView> SaveConfigAsync(string moduleId, QuizConfig config, string userSub, CancellationToken ct = default)
    {
        var access = await RequireOwnedModuleAsync(moduleId, userSub);
        await _pool.SaveConfigAsync(access.Module.Id, config, ct);   // wirft ArgumentException bei ungültigen Kombinationen
        return await GetConfigViewAsync(moduleId, userSub, ct);
    }

    public async Task<List<QuizPoolQuestion>> ListQuestionsAsync(string moduleId, string userSub, string? cardId = null, string? origin = null, bool? active = null, CancellationToken ct = default)
    {
        var access = await RequireOwnedModuleAsync(moduleId, userSub);   // enthält Lösungen → nur Eigentümer
        return await _pool.ListAsync(access.Module.Id, cardId, origin, active, ct);
    }

    public async Task<QuizPoolQuestion> CreateQuestionAsync(string moduleId, QuizQuestionInput input, string userSub, CancellationToken ct = default)
    {
        var access = await RequireOwnedModuleAsync(moduleId, userSub);
        await EnsureCardInModuleAsync(input.CardId, access.Module.Id, userSub);
        var (question, created) = await _pool.CreateAsync(access.Module.Id, input, QuizOrigins.Manual, QuizSources.Editor, null, ct);
        if (!created) throw new InvalidOperationException("Eine gleiche Frage existiert bereits in diesem Modul.");
        return question;
    }

    public async Task<QuizPoolQuestion> UpdateQuestionAsync(string moduleId, string questionId, QuizQuestionInput input, string userSub, CancellationToken ct = default)
    {
        var access = await RequireOwnedModuleAsync(moduleId, userSub);
        await EnsureCardInModuleAsync(input.CardId, access.Module.Id, userSub);
        return await _pool.UpdateAsync(access.Module.Id, questionId, input, ct);
    }

    public async Task SetQuestionActiveAsync(string moduleId, string questionId, bool active, string userSub, CancellationToken ct = default)
    {
        var access = await RequireOwnedModuleAsync(moduleId, userSub);
        if (!await _pool.SetActiveAsync(access.Module.Id, questionId, active, ct)) throw new KeyNotFoundException("Frage nicht gefunden.");
    }

    private async Task<ModuleAccess> RequireOwnedModuleAsync(string moduleId, string userSub)
    {
        var access = await _repo.ResolveModuleAccessAsync(userSub, moduleId) ?? throw new KeyNotFoundException("Modul nicht gefunden.");
        if (!access.CanEdit) throw new UnauthorizedAccessException("Freigegebene und globale Module sind schreibgeschützt.");
        return access;
    }

    private async Task EnsureCardInModuleAsync(string? cardId, string moduleId, string userSub)
    {
        if (string.IsNullOrEmpty(cardId)) return;
        var card = await _repo.GetReadableCardAsync(cardId, userSub);
        if (card is null || !string.Equals(card.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Die Karte gehört nicht zu diesem Modul.");
    }

    // ─── Quiz starten ───────────────────────────────────────────────────────

    /// <summary>
    /// Startet ein Quiz (Karten- oder Modulquiz) und legt die serverseitige Sitzung an. <paramref name="cardId"/> gesetzt = Kartenquiz.
    /// <paramref name="requestedCount"/> kann den konfigurierten Umfang nur verkleinern.
    /// </summary>
    public async Task<QuizStartResponse> StartSessionAsync(string moduleId, string category, string? cardId, int requestedCount, string userSub, CancellationToken ct = default)
    {
        await CleanupSessionsIfDueAsync(false, ct);
        var access = await _repo.ResolveModuleAccessAsync(userSub, moduleId)
            ?? throw new InvalidOperationException("Das ausgewählte Modul wurde nicht gefunden.");
        var module = access.Module;
        var config = await _pool.GetConfigAsync(module.Id, ct);
        var cardQuiz = !string.IsNullOrWhiteSpace(cardId);

        if (!config.Enabled) throw new QuizRuleException("Das Quiz ist für dieses Modul deaktiviert.", 403);
        if (cardQuiz && config.Scope == QuizConfig.ScopeModule) throw new QuizRuleException("Für dieses Modul sind nur modulweite Quizze erlaubt.", 403);
        if (!cardQuiz && config.Scope == QuizConfig.ScopeCard) throw new QuizRuleException("Für dieses Modul sind nur Karten-Quizze erlaubt.", 403);
        if (!config.AllowRetry && (await _repo.QuizHistoryAsync(userSub, module.Id, cardQuiz ? cardId : null, 200))
                .Any(r => !string.IsNullOrEmpty(r.SessionId) && (cardQuiz || string.IsNullOrEmpty(r.CardId))))
            throw new QuizRuleException("Wiederholungen sind für dieses Quiz nicht erlaubt.", 409);

        var cards = await ResolveQuizCardsAsync(module.Id, category, userSub, cardId);
        if (cards.Count == 0)
            throw new InvalidOperationException(cardQuiz
                ? "Die ausgewählte Karte wurde nicht gefunden oder ist nicht aktiv."
                : "Keine markierten Karten für dieses Modul / diese Kategorie gefunden. Markiere zuerst Karten mit Check.");
        var cardsById = cards.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

        // 1) gespeicherte Fragen des Moduls (nur aktive, erlaubte Typen und Herkunft, passend zum Umfang)
        var allowed = config.AllowedTypes();
        var stored = (await _pool.ListAsync(module.Id, null, null, true, ct))
            .Where(q => allowed.Contains(q.Type) && OriginAllowed(config, q.Origin)
                        && (cardQuiz ? string.Equals(q.CardId, cardId, StringComparison.OrdinalIgnoreCase)
                                     : q.CardId is null || cardsById.ContainsKey(q.CardId)))
            .ToList();
        var rnd = new Random();
        if (config.ShuffleQuestions) stored = stored.OrderBy(_ => rnd.Next()).ToList();

        var count = cardQuiz
            ? Math.Min(config.QuestionCount, Math.Max(stored.Count, PageQuizCount))
            : Math.Min(config.QuestionCount, requestedCount > 0 ? requestedCount : config.QuestionCount);
        count = Math.Clamp(count, 1, 20);

        var notices = new List<string>();
        var picked = new List<QuizSessionQuestion>();
        var aiWanted = config.Origin != QuizConfig.OriginFixed && config.Strategy != QuizConfig.StrategyNone;
        var canGenerate = aiWanted && AiAvailable;
        if (aiWanted && !AiAvailable) notices.Add("KI ist nicht konfiguriert — es werden gespeicherte bzw. aus den Karten abgeleitete Fragen verwendet.");

        int storedTake = config.Strategy == QuizConfig.StrategyMix && canGenerate ? Math.Min(stored.Count, count - (int)Math.Ceiling(count / 2.0)) : count;
        picked.AddRange(stored.Take(storedTake).Select(q => FromPool(q, cardsById, ephemeral: false)));

        var missing = count - picked.Count;
        var generateNow = canGenerate && missing > 0 && config.Strategy switch
        {
            QuizConfig.StrategyPreferStored => stored.Count == 0,   // nur wenn nichts Passendes gespeichert ist
            _ => true,                                              // on_demand und mix: Lücke füllen
        };
        if (generateNow)
        {
            try
            {
                var persist = access.CanEdit;   // Lernende in freigegebenen/globalen Modulen verändern den Pool nie
                var existingFp = stored.Select(q => q.Fingerprint).ToHashSet();
                var generated = await GenerateQuestionsAsync(module.Id, cards, allowed, missing, persist, existingFp, module.GlobalVersion, ct);
                picked.AddRange(generated);
                if (generated.Count < missing) notices.Add("Die KI lieferte weniger gültige Fragen als gewünscht.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Quiz: KI-Generierung für Modul {Module} fehlgeschlagen: {Message}", module.Id, ex.Message);
                notices.Add("Die KI-Generierung ist fehlgeschlagen — es werden vorhandene bzw. aus den Karten abgeleitete Fragen verwendet.");
            }
        }

        // 2) Offline-Fallback: Fragen aus den Karten ableiten (nur Freitext — nie ein nicht erlaubter Typ)
        var shortfall = count - picked.Count;
        if (shortfall > 0 && allowed.Contains(QuizTypes.FreeText) && (picked.Count == 0 || config.Strategy != QuizConfig.StrategyNone))
        {
            var usedCards = picked.Where(p => p.CardId is not null).Select(p => p.CardId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var derived = cards.Where(c => !usedCards.Contains(c.Id)).OrderBy(_ => rnd.Next()).Take(shortfall)
                .Select(c => new QuizSessionQuestion
                {
                    QuestionId = "", CardId = c.Id, SourceTerm = c.Term, Topic = c.Category, Type = QuizTypes.FreeText, Question = c.Question,
                    Explanation = BuildReferenceAnswer(c), Origin = QuizOrigins.Manual, Ephemeral = true,
                }).ToList();
            if (derived.Count > 0 && picked.Count > 0) notices.Add("Fehlende Fragen wurden aus den Karten abgeleitet (Freitext).");
            picked.AddRange(derived);
        }

        if (picked.Count == 0)
            throw new InvalidOperationException(allowed.Count == 0 || !allowed.Contains(QuizTypes.FreeText)
                ? "Für die gewählten Frageformate sind keine Fragen verfügbar. Lege Fragen an (quiz[] der Karte), erlaube KI-Generierung oder aktiviere Freitext."
                : "Es sind keine Quizfragen verfügbar.");

        if (config.ShuffleQuestions) picked = picked.OrderBy(_ => rnd.Next()).ToList();
        if (config.ShuffleAnswers) foreach (var q in picked.Where(q => QuizTypes.IsChoice(q.Type))) ShuffleOptions(q, rnd);

        var session = new QuizSessionRecord
        {
            UserSub = userSub, ModuleId = module.Id, CardId = cardQuiz ? cardId : null, Category = category ?? "", Context = access.Context,
            ModuleVersion = module.GlobalVersion, Config = config, Questions = picked,
        };
        await _pool.SaveSessionAsync(session);

        return new QuizStartResponse
        {
            SessionId = session.Id, ModuleId = module.Id, Category = session.Category, CardId = cardQuiz ? cardId! : "",
            PassPercent = config.PassPercent, ShowCorrectAnswers = config.ShowCorrectAnswers, Notices = notices,
            Questions = picked.Select(q => new QuizQuestion
            {
                Question = q.Question, Topic = q.Topic, SourceCardId = q.CardId ?? "", SourceTerm = q.SourceTerm,
                QuestionId = SessionQuestionKey(q, picked), Type = q.Type, Options = q.Options.ToList(),
            }).ToList(),
        };
    }

    private static bool OriginAllowed(QuizConfig c, string origin) => c.Origin switch
    {
        QuizConfig.OriginFixed => origin == QuizOrigins.Manual,
        QuizConfig.OriginAi => origin == QuizOrigins.Ai,
        _ => true,
    };

    /// <summary>Stabile Frage-Kennung innerhalb der Sitzung: Pool-ID oder Position (für abgeleitete Fragen).</summary>
    private static string SessionQuestionKey(QuizSessionQuestion q, List<QuizSessionQuestion> all) =>
        !string.IsNullOrEmpty(q.QuestionId) ? q.QuestionId : "d" + all.IndexOf(q);

    private static QuizSessionQuestion FromPool(QuizPoolQuestion q, Dictionary<string, Card> cards, bool ephemeral)
    {
        Card? card = null;
        if (q.CardId is not null) cards.TryGetValue(q.CardId, out card);
        return new QuizSessionQuestion
        {
            QuestionId = q.Id, CardId = q.CardId, SourceTerm = card?.Term ?? "", Topic = card?.Category ?? "", Type = q.Type, Question = q.Question,
            Options = q.Options.ToList(), CorrectIndexes = q.CorrectIndexes.ToList(), Explanation = q.Explanation, Origin = q.Origin, Ephemeral = ephemeral,
        };
    }

    private static void ShuffleOptions(QuizSessionQuestion q, Random rnd)
    {
        var order = Enumerable.Range(0, q.Options.Count).OrderBy(_ => rnd.Next()).ToList();   // neue Position i zeigt alte Option order[i]
        var newOptions = order.Select(o => q.Options[o]).ToList();
        q.CorrectIndexes = q.CorrectIndexes.Select(old => order.IndexOf(old)).OrderBy(i => i).ToList();
        q.Options = newOptions;
    }

    // ─── KI-Generierung ─────────────────────────────────────────────────────

    private async Task<List<QuizSessionQuestion>> GenerateQuestionsAsync(string moduleId, List<Card> cards, List<string> allowedTypes, int count, bool persist,
        HashSet<string> knownFingerprints, int? moduleVersion, CancellationToken ct)
    {
        var quizCards = cards.Take(40).ToList();
        var cardSummaries = string.Join("\n\n", quizCards.Select(BuildQuizCardBrief));
        var typeHelp = string.Join("\n", allowedTypes.Select(t => t switch
        {
            QuizTypes.SingleChoice => "- single_choice: genau 4 Optionen, genau ein Index in correct_indexes",
            QuizTypes.MultipleResponse => "- multiple_response: 4 bis 5 Optionen, mindestens zwei und höchstens (Optionen-1) Indizes in correct_indexes",
            _ => "- free_text: keine Optionen (options: []), correct_indexes: [], explanation = knappe Musterlösung",
        }));

        var prompt = $$"""
            Du erzeugst Prüfungsfragen ausschließlich auf Basis der folgenden Lernkarten:

            {{cardSummaries}}

            Regeln:
            - Verwende nur Fakten aus den Karten und ihren offiziellen Quellen. Erfinde keine Details, keine Best Practices und keine Randbedingungen.
            - Wenn die Kartendaten keine tragfähigen Fragen hergeben, liefere {"questions":[]}.
            - Erzeuge genau {{count}} Fragen (oder weniger, wenn nicht genug belegt ist), Schwierigkeit Level 400.
            - Erlaubte Fragetypen (nutze nur diese, mische sie sinnvoll):
            {{typeHelp}}
            - Jede Frage braucht eine kurze Erklärung ("explanation") und genau eine Quellenkarte: "source_card_id" mit exakt der Karten-ID oben.
            - Antworte NUR als JSON-Objekt:
              {"questions":[{"type":"single_choice","question":"...","options":["...","...","...","..."],"correct_indexes":[0],"explanation":"...","source_card_id":"..."}]}
            """;

        var budget = Math.Min(Math.Max(1500, 700 + count * 600), 7000);
        using var doc = await _openAi.CompleteJsonAsync(prompt, budget, ct);
        var root = doc.RootElement;
        var arr = root.ValueKind == JsonValueKind.Array ? root : root.TryGetProperty("questions", out var q) ? q : default;
        var result = new List<QuizSessionQuestion>();
        if (arr.ValueKind != JsonValueKind.Array) return result;

        var cardsById = quizCards.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(knownFingerprints);
        foreach (var el in arr.EnumerateArray())
        {
            if (result.Count >= count) break;
            var input = ParseGeneratedQuestion(el, allowedTypes, cardsById, out var sourceCardId);
            if (input is null) continue;   // ungültige KI-Fragen werden verworfen
            var fp = QuizPoolService.Fingerprint(sourceCardId, input);
            if (!seen.Add(fp)) continue;   // Duplikat (gespeichert oder im selben Lauf)

            var card = cardsById[sourceCardId!];
            var sq = new QuizSessionQuestion
            {
                CardId = sourceCardId, SourceTerm = card.Term, Topic = card.Category, Type = input.Type, Question = input.Question.Trim(),
                Options = input.Options.Select(o => o.Trim()).ToList(), CorrectIndexes = input.CorrectIndexes.OrderBy(i => i).ToList(),
                Explanation = input.Explanation.Trim(), Origin = QuizOrigins.Ai, Ephemeral = !persist,
            };
            if (persist)
            {
                var (saved, created) = await _pool.CreateAsync(moduleId, input, QuizOrigins.Ai, QuizSources.Ai, moduleVersion, ct);
                if (!created) continue;   // zwischenzeitlich bereits vorhanden
                sq.QuestionId = saved.Id;
            }
            result.Add(sq);
        }
        return result;
    }

    private static QuizQuestionInput? ParseGeneratedQuestion(JsonElement el, List<string> allowedTypes, Dictionary<string, Card> cardsById, out string? sourceCardId)
    {
        sourceCardId = null;
        if (el.ValueKind != JsonValueKind.Object) return null;
        string Str(string name) => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var type = Str("type");
        if (!allowedTypes.Contains(type)) return null;   // nur erlaubte Fragetypen
        sourceCardId = Str("source_card_id");
        if (!cardsById.ContainsKey(sourceCardId)) return null;

        var options = new List<string>();
        if (el.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array)
            options = o.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").ToList();
        var correct = new List<int>();
        if (el.TryGetProperty("correct_indexes", out var c) && c.ValueKind == JsonValueKind.Array)
            foreach (var x in c.EnumerateArray()) { if (x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var i)) correct.Add(i); else return null; }
        else if (el.TryGetProperty("correct_index", out var ci) && ci.ValueKind == JsonValueKind.Number && ci.TryGetInt32(out var one)) correct.Add(one);

        var input = new QuizQuestionInput { CardId = sourceCardId, Type = type, Question = Str("question"), Options = options, CorrectIndexes = correct, Explanation = Str("explanation") };
        if (type == QuizTypes.SingleChoice && options.Count != 4) return null;
        if (type == QuizTypes.MultipleResponse && (correct.Count < 1 || correct.Count >= options.Count)) return null;
        return QuizPoolService.Validate(input) is null ? input : null;
    }

    // ─── Sitzungen ──────────────────────────────────────────────────────────

    // ─── Abgabe und Bewertung ───────────────────────────────────────────────

    /// <summary>
    /// Bewertet eine Quizabgabe serverseitig und speichert das Ergebnis. Mit <see cref="QuizSubmitRequest.SessionId"/> gilt die Sitzung (Lösungen vom Server);
    /// ohne Sitzung (ältere Clients) wird die Abgabe als reine Freitext-Sitzung behandelt — dieselbe Bewertungslogik.
    /// </summary>
    public async Task<QuizResultResponse> SubmitQuizAsync(QuizSubmitRequest req, string userSub, CancellationToken ct = default)
    {
        // Ergebnisse dürfen nur auf Module/Karten verweisen, die der Benutzer lernen darf: eigene, freigegebene oder globale (ADR 0011/0015).
        var access = await _repo.ResolveModuleAccessAsync(userSub, req.ModuleId)
            ?? throw new InvalidOperationException("Das ausgewählte Modul wurde nicht gefunden.");
        if (!string.IsNullOrWhiteSpace(req.CardId)
            && (await _repo.GetReadableCardAsync(req.CardId, userSub) is not { } selected
                || !string.Equals(selected.ModuleId, access.Module.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Die ausgewählte Karte wurde nicht gefunden.");

        QuizSessionRecord session;
        var claimed = false;
        if (!string.IsNullOrWhiteSpace(req.SessionId))
        {
            session = await _pool.LoadSessionAsync(req.SessionId, userSub) ?? throw new InvalidOperationException("Die Quizsitzung wurde nicht gefunden.");
            if (!string.Equals(session.ModuleId, access.Module.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Die Quizsitzung gehört zu einem anderen Modul.");
            // Atomar beanspruchen: jede Sitzung kann nur einmal abgegeben werden
            if (QuizPoolService.IsExpired(session)) throw new QuizRuleException("Die Quizsitzung ist abgelaufen — starte das Quiz neu.", 410);
            if (!await _pool.ClaimSessionAsync(session.Id, userSub)) throw new QuizRuleException("Dieses Quiz wurde bereits abgegeben.", 409);
            claimed = true;
        }
        else
        {
            var config = await _pool.GetConfigAsync(access.Module.Id, ct);
            session = new QuizSessionRecord
            {
                UserSub = userSub, ModuleId = access.Module.Id, CardId = string.IsNullOrWhiteSpace(req.CardId) ? null : req.CardId, Category = req.Category ?? "",
                Context = access.Context, ModuleVersion = access.Module.GlobalVersion, Config = config,
                Questions = req.Answers.Select(a => new QuizSessionQuestion { QuestionId = "", CardId = string.IsNullOrWhiteSpace(a.SourceCardId) ? null : a.SourceCardId, Type = QuizTypes.FreeText, Question = a.Question }).ToList(),
            };
        }

        try
        {
            var result = await GradeSessionAsync(session, req, userSub, access, ct);
            return result;
        }
        catch
        {
            if (claimed)   // Bewertung fehlgeschlagen → Sitzung wieder freigeben
                await _pool.ReleaseSessionAsync(session.Id, userSub);
            throw;
        }
    }

    private sealed record FreeItem(int Index, string Question, string UserAnswer, string SourceCardId, string Explanation);

    private async Task<QuizResultResponse> GradeSessionAsync(QuizSessionRecord session, QuizSubmitRequest req, string userSub, ModuleAccess access, CancellationToken ct)
    {
        var cfg = session.Config;
        var graded = new GradedAnswer?[session.Questions.Count];
        var freeItems = new List<FreeItem>();

        for (var i = 0; i < session.Questions.Count; i++)
        {
            var q = session.Questions[i];
            var key = SessionQuestionKey(q, session.Questions);
            // Zuordnung: Frage-ID bevorzugt, sonst Position (ältere Clients)
            var answer = req.Answers.FirstOrDefault(a => !string.IsNullOrEmpty(a.QuestionId) && a.QuestionId == key)
                         ?? (req.Answers.All(a => string.IsNullOrEmpty(a.QuestionId)) && i < req.Answers.Count ? req.Answers[i] : null);

            if (!QuizTypes.IsChoice(q.Type))
            {
                freeItems.Add(new FreeItem(i, q.Question, answer?.UserAnswer ?? "", q.CardId ?? "", q.Explanation));
                continue;
            }

            var selected = (answer?.SelectedIndexes ?? new()).Where(x => x >= 0 && x < q.Options.Count).Distinct().OrderBy(x => x).ToList();
            if (q.Type == QuizTypes.SingleChoice && selected.Count > 1) selected = selected.Take(1).ToList();
            var correct = q.CorrectIndexes.OrderBy(x => x).ToList();
            var isCorrect = selected.Count > 0 && selected.SequenceEqual(correct);
            graded[i] = new GradedAnswer
            {
                Question = q.Question, QuestionId = key, Type = q.Type, Options = q.Options.ToList(), SelectedIndexes = selected, SourceCardId = q.CardId ?? "", SourceTerm = q.SourceTerm,
                UserAnswer = string.Join("; ", selected.Select(x => q.Options[x])), Correct = isCorrect, Score = isCorrect ? PointsPerQuestion : 0, Max = PointsPerQuestion,
                Feedback = isCorrect ? "Richtig." : selected.Count == 0 ? "Keine Antwort gegeben." : "Nicht korrekt.",
                CorrectIndexes = cfg.ShowCorrectAnswers ? correct : new(), Explanation = cfg.ShowCorrectAnswers ? q.Explanation : "",
            };
        }

        string overallFeedback = "";
        if (freeItems.Count > 0)
        {
            var cards = await ResolveCardsForEvaluationAsync(session.ModuleId, session.Category, session.CardId, userSub);
            var (freeGraded, feedback) = await GradeFreeTextAsync(freeItems, cards, session.Category, ct);
            overallFeedback = feedback;
            foreach (var (item, g) in freeItems.Zip(freeGraded))
            {
                var q = session.Questions[item.Index];
                g.QuestionId = SessionQuestionKey(q, session.Questions); g.Type = QuizTypes.FreeText;
                if (string.IsNullOrEmpty(g.SourceTerm)) g.SourceTerm = q.SourceTerm;
                graded[item.Index] = g;
            }
        }

        var all = graded.Select(g => g!).ToList();
        var total = all.Sum(g => g.Score);
        var max = all.Sum(g => g.Max);
        var pct = max > 0 ? Math.Round(total / max * 100, 1) : 0;
        var passed = pct >= cfg.PassPercent;
        var choiceCount = all.Count(g => QuizTypes.IsChoice(g.Type));
        var feedbackText = string.IsNullOrWhiteSpace(overallFeedback)
            ? $"{all.Count(g => g.Correct == true)} von {choiceCount} Auswahlfragen richtig."
            : overallFeedback;

        var checkedCards = await ResolveQuizCardsAsync(session.ModuleId, session.Category, userSub, session.CardId);
        var result = new QuizResultResponse
        {
            Score = total, MaxScore = max, Percentage = pct, Grade = GradeFor(pct), Feedback = feedbackText, GradedAnswers = all,
            Stats = BuildQuizStats(session.Questions.Select(q => new QuizAnswerItem { Question = q.Question, SourceCardId = q.CardId ?? "" }).ToList(), checkedCards),
            SessionId = session.Id, Passed = passed, PassPercent = cfg.PassPercent, ModuleVersion = session.ModuleVersion, ShowCorrectAnswers = cfg.ShowCorrectAnswers,
        };

        var record = new QuizResultRecord
        {
            ModuleId = session.ModuleId, CardId = session.CardId ?? "", UserSub = userSub, Context = access.Context, Category = session.Category,
            Score = total, MaxScore = max, Grade = result.Grade, Feedback = feedbackText,
            // Gespeichert wird das serverseitig verifizierte Ergebnis; Lösungen nur, wenn die Konfiguration sie anzeigen darf (GradedAnswer ist bereits bereinigt)
            AnswersJson = JsonSerializer.Serialize(all, AppJson.Options),
            StatsJson = JsonSerializer.Serialize(result.Stats, AppJson.Options),
            SessionId = string.IsNullOrWhiteSpace(req.SessionId) ? null : session.Id, Passed = passed, PassPercent = cfg.PassPercent, ModuleVersion = session.ModuleVersion,
        };
        await _repo.SaveQuizResultAsync(record);
        result.ResultId = record.Id;
        // Abschlussnachweis (ADR 0021): nur nach bestandenem, sitzungsverifiziertem Modulquiz; die Bewertung selbst bleibt hier — ein Fehler darf das Ergebnis nie gefährden
        if (passed && record.SessionId is not null && string.IsNullOrEmpty(record.CardId))
        {
            try
            {
                var cert = await _certificates.TryIssueAsync(record, access.Module.Name, access.Context, access.Module.GlobalVersion);
                if (cert is not null) result.CertificateId = cert.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning("Zertifikat konnte nicht ausgestellt werden: {Message}", ex.Message); }
        }
        return result;
    }

    private static string GradeFor(double pct) => pct >= 90 ? "A" : pct >= 75 ? "B" : pct >= 60 ? "C" : pct >= 45 ? "D" : "F";

    /// <summary>
    /// Serverseitig verifiziertes Ergebnis eines Versuchs (Schnittstelle für Auftrag 3 — Zertifikate/Abschlussnachweise). Nur für den Benutzer selbst
    /// und nur für Ergebnisse, die aus einer Quizsitzung stammen (nicht aus Altdaten).
    /// </summary>
    public async Task<VerifiedQuizResult?> GetVerifiedResultAsync(string resultId, string userSub)
    {
        if (!UserService.IsGuid(resultId)) return null;
        var r = (await _repo.QuizHistoryAsync(userSub, null, null, 200)).FirstOrDefault(x => x.Id == resultId);
        if (r is null || string.IsNullOrEmpty(r.SessionId) || r.Passed is null || r.PassPercent is null) return null;
        return new VerifiedQuizResult
        {
            ResultId = r.Id, SessionId = r.SessionId, UserId = r.UserSub, ModuleId = r.ModuleId, ModuleVersion = r.ModuleVersion, Score = r.Score, MaxScore = r.MaxScore,
            Percentage = r.MaxScore > 0 ? Math.Round(r.Score / r.MaxScore * 100, 1) : 0, PassPercent = r.PassPercent.Value, Passed = r.Passed.Value, CompletedAt = r.CompletedAt,
        };
    }

    // ─── Freitext-Bewertung (KI mit Offline-Fallback) ───────────────────────

    private async Task<(List<GradedAnswer> Graded, string Feedback)> GradeFreeTextAsync(List<FreeItem> items, List<Card> cards, string category, CancellationToken ct)
    {
        if (_openAi.IsConfigured)
        {
            try { return await GradeFreeTextWithOpenAiAsync(items, cards, category, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Quiz: KI-Bewertung fehlgeschlagen, lokaler Fallback: {Message}", ex.Message);
            }
        }
        return GradeFreeTextLocally(items, cards);
    }

    private static string ReferenceFor(FreeItem item, Card? card)
    {
        var reference = BuildReferenceAnswer(card);
        if (string.IsNullOrWhiteSpace(item.Explanation)) return reference;
        return string.IsNullOrWhiteSpace(reference) ? item.Explanation.Trim()
            : string.Equals(reference.Trim(), item.Explanation.Trim(), StringComparison.Ordinal) ? reference : item.Explanation.Trim() + "\n\n" + reference;
    }

    private async Task<(List<GradedAnswer>, string)> GradeFreeTextWithOpenAiAsync(List<FreeItem> items, List<Card> cards, string category, CancellationToken ct)
    {
        var qaPairs = string.Join("\n", items.Select((a, i) => $"Frage {i + 1}: {a.Question}\nAntwort: {a.UserAnswer}"));
        var references = string.Join("\n\n", items.Select((a, i) =>
        {
            var card = FindCard(cards, a.SourceCardId, a.Question);
            var sources = card is null || card.OfficialSources.Count == 0
                ? "Keine offiziellen Quellen hinterlegt."
                : string.Join("\n", card.OfficialSources.Select(s => $"- {s.Title}: {s.Url}"));
            return $"Referenz {i + 1}:\nQuellenkarte: {(card?.Term ?? "unbekannt")} ({a.SourceCardId})\nFrage: {a.Question}\nBegrenzung: Bewerte nur gegen diese Referenz und diese Quellen.\nMusterlösung: {ReferenceFor(a, card)}\nOffizielle Quellen:\n{sources}";
        }));
        var maxScore = items.Count * 10;

        var gradingPrompt = $$"""
            Du bist ein strenger aber fairer Azure-Experte und bewertest eine Prüfung.
            Thema: {{category}}
            Bewerte jede Antwort einzeln mit 0-10 Punkten und erkläre kurz warum.
            Ergänze anschließend eine ausgefüllte Referenzlösung, die die Antwort des Nutzers aufgreift,
            Lücken mit der Musterlösung schließt und nur auf den offiziellen Quellen basiert.
            Erfinde keine technischen Details oder Quellen. Wenn die hinterlegten offiziellen Quellen etwas
            nicht abdecken, sage das explizit statt zu halluzinieren.
            Nutze für Bewertung und Lösung ausschließlich die bereitgestellten Kartenfakten, Musterlösungen
            und offiziellen Quellen. Triff keine Annahmen und fülle keine Lücken mit Weltwissen.
            Wenn eine Nutzerantwort einen fachlich möglichen Punkt nennt, der aber nicht in Referenz oder Quellen
            enthalten ist, werte ihn nicht als korrekt belegten Punkt.
            Gib zusammenfassendes Feedback.
            Antworte NUR als JSON:
            {
              "graded": [{
                "question": "...",
                "user_answer": "...",
                "score": 8,
                "max": 10,
                "feedback": "...",
                "reference_answer": "...",
                "completed_solution": "...",
                "official_sources": [{"title": "...", "url": "...", "publisher": "..."}]
              }],
              "overall_feedback": "..."
            }

            Fragen und Antworten:
            {{qaPairs}}

            Musterlösungen und offizielle Quellen:
            {{references}}
            """;

        var gradingTokenBudget = Math.Min(Math.Max(2000, 900 + items.Count * 900), 8000);
        using var doc = await _openAi.CompleteJsonAsync(gradingPrompt, gradingTokenBudget, ct);
        var root = doc.RootElement;
        var feedback = root.TryGetProperty("overall_feedback", out var fb) ? fb.GetString() ?? "" : "";

        var graded = new List<GradedAnswer>();
        var returned = root.TryGetProperty("graded", out var arr) && arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().ToList() : new List<JsonElement>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var card = FindCard(cards, item.SourceCardId, item.Question);
            var el = i < returned.Count ? returned[i] : default;
            var has = el.ValueKind == JsonValueKind.Object;
            double score = has && el.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? Math.Clamp(sc.GetDouble(), 0, 10) : 0;
            graded.Add(new GradedAnswer
            {
                Question = item.Question, UserAnswer = item.UserAnswer, SourceCardId = item.SourceCardId, SourceTerm = card?.Term ?? "",
                Score = score, Max = 10,
                Feedback = has && el.TryGetProperty("feedback", out var f) ? f.GetString() ?? "" : "Keine Bewertung geliefert.",
                ReferenceAnswer = has && el.TryGetProperty("reference_answer", out var ra) ? ra.GetString() ?? "" : ReferenceFor(item, card),
                CompletedSolution = has && el.TryGetProperty("completed_solution", out var cs) ? cs.GetString() ?? "" : BuildCompletedSolution(item.UserAnswer, card),
                OfficialSources = has && el.TryGetProperty("official_sources", out var os) && os.ValueKind == JsonValueKind.Array
                    ? os.Deserialize<List<OfficialSource>>(AppJson.Options) ?? card?.OfficialSources ?? new()
                    : card?.OfficialSources ?? new(),
            });
        }
        return (graded, feedback);
    }

    /// <summary>Heuristische Offline-Bewertung: Wortüberlappung der Antwort mit der Referenz (Kartentext bzw. Musterlösung).</summary>
    private static (List<GradedAnswer>, string) GradeFreeTextLocally(List<FreeItem> items, List<Card> cards)
    {
        var graded = new List<GradedAnswer>();
        foreach (var a in items)
        {
            var card = FindCard(cards, a.SourceCardId, a.Question);
            var reference = ReferenceFor(a, card);
            var refWords = Tokenize(reference);
            var ansWords = Tokenize(a.UserAnswer);
            var overlap = refWords.Count == 0 ? 0 : (double)ansWords.Intersect(refWords).Count() / Math.Min(refWords.Count, 12);
            graded.Add(new GradedAnswer
            {
                Question = a.Question, UserAnswer = a.UserAnswer, SourceCardId = a.SourceCardId, SourceTerm = card?.Term ?? "",
                Score = Math.Round(Math.Clamp(overlap, 0, 1) * 10, 1), Max = 10,
                Feedback = "Offline-Bewertung (Stichwort-Heuristik) — für eine fundierte KI-Bewertung OPENAI_API_KEY setzen.",
                ReferenceAnswer = reference, CompletedSolution = BuildCompletedSolution(a.UserAnswer, card), OfficialSources = card?.OfficialSources ?? new(),
            });
        }
        return (graded, "Hinweis: Freitextantworten wurden lokal per Stichwort-Heuristik bewertet (kein OpenAI-Schlüssel bzw. KI nicht erreichbar) — nur ein grober Anhaltspunkt.");
    }

    // ─── Hilfen ─────────────────────────────────────────────────────────────

    private static HashSet<string> Tokenize(string text) =>
        text.ToLowerInvariant()
            .Split(new[] { ' ', '\n', '\t', ',', '.', ';', ':', '(', ')', '-', '/', '"' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .ToHashSet();

    private static string BuildReferenceAnswer(Card? card)
    {
        if (card is null) return "";
        if (!string.IsNullOrWhiteSpace(card.ReferenceAnswer)) return card.ReferenceAnswer.Trim();

        var parts = new[] { card.Definition, card.HowItWorks, card.Context, card.KeyFact }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim());
        return string.Join("\n\n", parts);
    }

    private static string BuildCompletedSolution(string userAnswer, Card? card)
    {
        var reference = BuildReferenceAnswer(card);
        if (string.IsNullOrWhiteSpace(userAnswer)) return reference;
        if (string.IsNullOrWhiteSpace(reference)) return userAnswer.Trim();
        return $"Markierte Antwort:\n{userAnswer.Trim()}\n\nErgänzte Referenzlösung:\n{reference}";
    }

    private static Card? FindCard(List<Card> cards, string sourceCardId, string question) =>
        cards.FirstOrDefault(c => !string.IsNullOrEmpty(sourceCardId) && c.Id == sourceCardId)
        ?? cards.FirstOrDefault(c => c.Question == question);

    private static QuizSessionStats BuildQuizStats(List<QuizAnswerItem> answers, List<Card> checkedCards)
    {
        var checkedById = checkedCards.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        var usage = answers
            .Where(a => !string.IsNullOrWhiteSpace(a.SourceCardId))
            .GroupBy(a => a.SourceCardId, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                checkedById.TryGetValue(g.Key, out var card);
                return new QuizCardUsage
                {
                    CardId = g.Key,
                    Term = card?.Term ?? "",
                    Category = card?.Category ?? "",
                    QuestionCount = g.Count(),
                    RelativeWeight = answers.Count == 0 ? 0 : Math.Round((double)g.Count() / answers.Count, 3),
                };
            })
            .OrderByDescending(x => x.QuestionCount)
            .ThenBy(x => x.Term)
            .ToList();

        var usedCheckedCardCount = usage.Count;
        var questionCount = answers.Count;
        var checkedCardCount = checkedCards.Count;
        return new QuizSessionStats
        {
            CheckedCardCount = checkedCardCount,
            UsedCheckedCardCount = usedCheckedCardCount,
            QuestionCount = questionCount,
            CheckedCoverageRatio = checkedCardCount == 0 ? 0 : Math.Round((double)usedCheckedCardCount / checkedCardCount, 3),
            DistributionWeight = questionCount == 0 ? 0 : Math.Round((double)usedCheckedCardCount / questionCount, 3),
            QuestionsPerUsedCard = usedCheckedCardCount == 0 ? 0 : Math.Round((double)questionCount / usedCheckedCardCount, 3),
            CardUsage = usage,
        };
    }

    private static string BuildQuizCardBrief(Card card)
    {
        var sources = card.OfficialSources.Count == 0
            ? "Keine offiziellen Quellen hinterlegt."
            : string.Join("\n", card.OfficialSources.Select(s => $"- {s.Title}: {s.Url}"));

        return $$"""
            Karte:
            - Karten-ID: {{card.Id}}
            - Kategorie: {{card.Category}}
            - Begriff: {{card.Term}}
            - Prüfungsfrage der Karte: {{card.Question}}
            - Referenzwissen:
            {{BuildReferenceAnswer(card)}}
            - Offizielle Quellen:
            {{sources}}
            """;
    }

    private async Task<List<Card>> ResolveQuizCardsAsync(string moduleId, string? category, string userSub, string? cardId)
    {
        if (!string.IsNullOrWhiteSpace(cardId))
        {
            var card = await _repo.GetReadableCardAsync(cardId, userSub);
            if (card is null || card.Archived || !string.Equals(card.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase))
                return new();
            return new List<Card> { card };
        }

        return await _repo.ListCheckedCardsAsync(moduleId, string.IsNullOrEmpty(category) ? null : category, userSub);
    }

    private async Task<List<Card>> ResolveCardsForEvaluationAsync(string moduleId, string? category, string? cardId, string userSub)
    {
        if (!string.IsNullOrWhiteSpace(cardId))
        {
            var card = await _repo.GetReadableCardAsync(cardId, userSub);
            if (card is null || card.Archived || !string.Equals(card.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase))
                return new();
            return new List<Card> { card };
        }

        return await _repo.ListReadableCardsAsync(userSub, moduleId, string.IsNullOrEmpty(category) ? null : category);
    }
}
