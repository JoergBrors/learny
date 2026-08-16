using System.Text.Json;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>
/// KI-gestützter Quiz-Modus: Fragen aus dem Kartenpool generieren und Freitext-Antworten bewerten.
/// Ohne konfigurierten OpenAI-Schlüssel greift ein lokaler Fallback (Fragen direkt von den Karten,
/// Bewertung per Stichwort-Heuristik), damit die Entwicklungsumgebung ohne Schlüssel nutzbar bleibt.
/// </summary>
public class QuizService
{
    private readonly CardRepository _repo;
    private readonly OpenAiClient _openAi;
    private const int PageQuizCount = 3;

    public QuizService(CardRepository repo, OpenAiClient openAi)
    {
        _repo = repo;
        _openAi = openAi;
    }

    public Task<List<QuizHistoryEntry>> GetHistoryAsync(string userSub, string moduleId, string? cardId = null, int limit = 10) =>
        _repo.QuizHistoryDetailedAsync(userSub, moduleId, cardId, limit);

    public Task<int> DeleteHistoryEntryAsync(string userSub, string quizResultId) =>
        _repo.DeleteQuizResultAsync(userSub, quizResultId);

    public async Task<List<CardQuizQuestion>> GetCardQuizAsync(string cardId, CancellationToken ct = default)
    {
        var card = await _repo.GetCardAsync(cardId)
            ?? throw new InvalidOperationException("Die ausgewählte Karte wurde nicht gefunden.");

        if (card.Archived)
            throw new InvalidOperationException("Für archivierte Karten ist kein Seiten-Quiz verfügbar.");

        if (card.Quiz.Count > 0)
            return card.Quiz;

        if (!_openAi.IsConfigured)
            throw new InvalidOperationException("Für Karten ohne vorbereitete Quizfragen ist aktuell kein LLM konfiguriert.");

        var prompt = $$"""
            Du erzeugst ausschließlich quellengebundene Multiple-Choice-Fragen für eine Lernkarte.
            Verwende nur Fakten aus der Karte und ihren offiziellen Quellen. Erfinde keine Details,
            keine Best Practices und keine Randbedingungen. Wenn die Kartendaten keine tragfähigen
            Fragen hergeben, liefere {"questions":[]} statt zu halluzinieren.

            Erzeuge genau 2 bis 3 Fragen auf Level 400 mit genau 4 Antwortoptionen, genau einer
            korrekten Antwort und einer kurzen Erklärung.

            Antworte ausschließlich als valides JSON-Objekt in diesem Format:
            {
              "questions": [
                {
                  "type": "single_choice",
                  "question": "...",
                  "options": ["...", "...", "...", "..."],
                  "correct_index": 0,
                  "explanation": "..."
                }
              ]
            }

            Begriff: {{card.Term}}
            Kategorie: {{card.Category}}
            Frage: {{card.Question}}
            Definition: {{card.Definition}}
            Funktionsweise: {{card.HowItWorks}}
            Kontext: {{card.Context}}
            Kernfakt: {{card.KeyFact}}
            Referenzantwort: {{BuildReferenceAnswer(card)}}
            Offizielle Quellen:
            {{FormatOfficialSources(card)}}
            """;

        try
        {
            using var doc = await _openAi.CompleteJsonAsync(prompt, 1200, ct);
            var questions = ParseCardQuizResponse(doc.RootElement)
                .Where(IsValidCardQuizQuestion)
                .Take(PageQuizCount)
                .ToList();

            if (questions.Count > 0)
                return questions;

            throw new InvalidOperationException("Das LLM hat kein verwertbares Quiz für diese Karte geliefert.");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Das Quiz konnte nicht generiert werden: " + ex.Message, ex);
        }
    }

    public async Task<List<QuizQuestion>> StartQuizAsync(string moduleId, string category, int numQuestions, string userSub, string? cardId = null, CancellationToken ct = default)
    {
        numQuestions = Math.Clamp(numQuestions, 1, 20);
        var cards = await ResolveQuizCardsAsync(moduleId, category, userSub, cardId);
        if (cards.Count == 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(cardId)
                ? "Keine markierten Karten für dieses Modul / diese Kategorie gefunden. Markiere zuerst Karten mit Check."
                : "Die ausgewählte Karte wurde nicht gefunden oder ist nicht aktiv.");

        if (!_openAi.IsConfigured)
        {
            // Fallback: Fragen direkt aus den Karten ziehen
            var rnd = new Random();
            return cards.OrderBy(_ => rnd.Next()).Take(numQuestions)
                .Select(c => new QuizQuestion { Question = c.Question, Topic = c.Category, SourceCardId = c.Id, SourceTerm = c.Term }).ToList();
        }

        var quizCards = cards.Take(40).ToList();
        var cardSummaries = string.Join("\n\n", quizCards.Select(BuildQuizCardBrief));

        var topicLabel = string.IsNullOrEmpty(category) ? "Alle Kategorien" : category;
        var prompt = $$"""
            Du bist ein Prüfer für das Thema '{{topicLabel}}'.
            Generiere {{numQuestions}} Prüfungsfragen ausschließlich auf Basis dieser gecheckten Lernkarten:

            {{cardSummaries}}

            Regeln:
            - Verwende ausschließlich Fakten, Begriffe und Zusammenhänge, die in den gecheckten Karten oder deren offiziellen Quellen enthalten sind.
            - Erfinde keine zusätzlichen Produktdetails, Randbedingungen, Best Practices oder Architekturannahmen.
            - Wenn Informationen fehlen, vereinfache nicht kreativ, sondern bleibe strikt innerhalb der Kartendaten.
            - Wenn eine Karte offizielle Quellen hat, muss die Frage zu diesen Quellen konsistent sein.
            - Jede Frage muss im Fließtext beantwortet werden (keine Multiple Choice)
            - Fragen sollen Verständnis prüfen, nicht nur Definitionen abfragen
            - Schwierigkeitsgrad: Level 400 (tiefes technisches Verständnis)
            - Frage nie nach Fakten, die in den Karten nicht eindeutig vorbereitet sind.
            - Jede erzeugte Frage MUSS genau eine Quellenkarte referenzieren.
            - Verwende bei "source_card_id" exakt die Karten-ID aus den bereitgestellten Kartendaten.
            - Antworte NUR als JSON-Objekt: {"questions": [{"question": "...", "topic": "...", "source_card_id": "..."}]}
            """;

        try
        {
            using var doc = await _openAi.CompleteJsonAsync(prompt, 1500, ct);
            var root = doc.RootElement;
            var arr = root.ValueKind == JsonValueKind.Array ? root
                : root.TryGetProperty("questions", out var q) ? q : default;

            var questions = new List<QuizQuestion>();
            if (arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                {
                    var sourceCardId = el.TryGetProperty("source_card_id", out var sid) ? sid.GetString() ?? "" : "";
                    var sourceCard = quizCards.FirstOrDefault(c => c.Id == sourceCardId);
                    if (sourceCard is null) continue;
                    questions.Add(new QuizQuestion
                    {
                        Question = el.TryGetProperty("question", out var qq) ? qq.GetString() ?? "" : "",
                        Topic = el.TryGetProperty("topic", out var tt) ? tt.GetString() ?? "" : category,
                        SourceCardId = sourceCardId,
                        SourceTerm = sourceCard.Term,
                    });
                }
            questions = questions.Where(x => x.Question.Length > 0).Take(numQuestions).ToList();
            if (questions.Count > 0) return questions;
        }
        catch (InvalidOperationException) { /* Fallback unten */ }

        var fallbackRandom = new Random();
        return cards.OrderBy(_ => fallbackRandom.Next()).Take(numQuestions)
            .Select(c => new QuizQuestion { Question = c.Question, Topic = c.Category, SourceCardId = c.Id, SourceTerm = c.Term })
            .ToList();
    }

    public async Task<QuizResultResponse> SubmitQuizAsync(QuizSubmitRequest req, string userSub, CancellationToken ct = default)
    {
        var checkedCards = await ResolveQuizCardsAsync(req.ModuleId, req.Category, userSub, req.CardId);
        QuizResultResponse result;
        if (_openAi.IsConfigured)
            result = await GradeWithOpenAiAsync(req, ct);
        else
            result = await GradeLocallyAsync(req);

        result.Stats = BuildQuizStats(req.Answers, checkedCards);

        var record = new QuizResultRecord
        {
            ModuleId = req.ModuleId,
            CardId = req.CardId,
            UserSub = userSub,
            Category = req.Category,
            Score = result.Score,
            MaxScore = result.MaxScore,
            Grade = result.Grade,
            Feedback = result.Feedback,
            AnswersJson = JsonSerializer.Serialize(result.GradedAnswers, Api.AppJson.Options),
            StatsJson = JsonSerializer.Serialize(result.Stats, Api.AppJson.Options),
        };
        await _repo.SaveQuizResultAsync(record);
        return result;
    }

    private async Task<QuizResultResponse> GradeWithOpenAiAsync(QuizSubmitRequest req, CancellationToken ct)
    {
        var cards = await ResolveCardsForEvaluationAsync(req.ModuleId, req.Category, req.CardId);
        var qaPairs = string.Join("\n", req.Answers.Select((a, i) => $"Frage {i + 1}: {a.Question}\nAntwort: {a.UserAnswer}"));
        var references = string.Join("\n\n", req.Answers.Select((a, i) =>
        {
            var card = FindCard(cards, a);
            var referenceAnswer = BuildReferenceAnswer(card);
            var sources = card is null || card.OfficialSources.Count == 0
                ? "Keine offiziellen Quellen hinterlegt."
                : string.Join("\n", card.OfficialSources.Select(s => $"- {s.Title}: {s.Url}"));
            return $"Referenz {i + 1}:\nQuellenkarte: {(card?.Term ?? "unbekannt")} ({a.SourceCardId})\nFrage: {a.Question}\nBegrenzung: Bewerte nur gegen diese Referenz und diese Quellen.\nMusterlösung: {referenceAnswer}\nOffizielle Quellen:\n{sources}";
        }));
        var maxScore = req.Answers.Count * 10;

        var gradingPrompt = $$"""
            Du bist ein strenger aber fairer Azure-Experte und bewertest eine Prüfung.
            Thema: {{req.Category}}
            Bewerte jede Antwort einzeln mit 0-10 Punkten und erkläre kurz warum.
            Ergänze anschließend eine ausgefüllte Referenzlösung, die die Antwort des Nutzers aufgreift,
            Lücken mit der Musterlösung schließt und nur auf den offiziellen Quellen basiert.
            Erfinde keine technischen Details oder Quellen. Wenn die hinterlegten offiziellen Quellen etwas
            nicht abdecken, sage das explizit statt zu halluzinieren.
            Nutze für Bewertung und Lösung ausschließlich die bereitgestellten Kartenfakten, Musterlösungen
            und offiziellen Quellen. Triff keine Annahmen und fülle keine Lücken mit Weltwissen.
            Wenn eine Nutzerantwort einen fachlich möglichen Punkt nennt, der aber nicht in Referenz oder Quellen
            enthalten ist, werte ihn nicht als korrekt belegten Punkt.
            Gib Gesamtnote (A/B/C/D/F) und zusammenfassendes Feedback.
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
              "total_score": 40,
              "max_score": {{maxScore}},
              "grade": "B",
              "overall_feedback": "..."
            }

            Fragen und Antworten:
            {{qaPairs}}

            Musterlösungen und offizielle Quellen:
            {{references}}
            """;

        using var doc = await _openAi.CompleteJsonAsync(gradingPrompt, 2000, ct);
        var root = doc.RootElement;

        double total = root.TryGetProperty("total_score", out var ts) ? ts.GetDouble() : 0;
        double max = root.TryGetProperty("max_score", out var ms) ? ms.GetDouble() : maxScore;
        var grade = root.TryGetProperty("grade", out var g) ? g.GetString() ?? "F" : "F";
        var feedback = root.TryGetProperty("overall_feedback", out var fb) ? fb.GetString() ?? "" : "";

        var graded = new List<GradedAnswer>();
        if (root.TryGetProperty("graded", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var idx = 0;
            foreach (var el in arr.EnumerateArray())
            {
                var submitted = idx < req.Answers.Count ? req.Answers[idx] : new QuizAnswerItem();
                var question = el.TryGetProperty("question", out var q) ? q.GetString() ?? "" : "";
                var card = FindCard(cards, submitted) ?? cards.FirstOrDefault(c => c.Question == question);
                graded.Add(new GradedAnswer
                {
                    Question = question,
                    UserAnswer = el.TryGetProperty("user_answer", out var ua) ? ua.GetString() ?? "" : "",
                    SourceCardId = submitted.SourceCardId,
                    SourceTerm = card?.Term ?? "",
                    Score = el.TryGetProperty("score", out var sc) ? sc.GetDouble() : 0,
                    Max = el.TryGetProperty("max", out var mx) ? mx.GetDouble() : 10,
                    Feedback = el.TryGetProperty("feedback", out var f) ? f.GetString() ?? "" : "",
                    ReferenceAnswer = el.TryGetProperty("reference_answer", out var ra) ? ra.GetString() ?? "" : BuildReferenceAnswer(card),
                    CompletedSolution = el.TryGetProperty("completed_solution", out var cs) ? cs.GetString() ?? "" : BuildCompletedSolution(
                        el.TryGetProperty("user_answer", out var uav) ? uav.GetString() ?? "" : "",
                        card),
                    OfficialSources = el.TryGetProperty("official_sources", out var os) && os.ValueKind == JsonValueKind.Array
                        ? os.Deserialize<List<OfficialSource>>() ?? card?.OfficialSources ?? new()
                        : card?.OfficialSources ?? new(),
                });
                idx++;
            }
        }

        return new QuizResultResponse
        {
            Score = total,
            MaxScore = max,
            Percentage = max > 0 ? Math.Round(total / max * 100, 1) : 0,
            Grade = grade,
            Feedback = feedback,
            GradedAnswers = graded,
        };
    }

    /// <summary>Heuristische Offline-Bewertung: Wortüberlappung der Antwort mit der Kartendefinition.</summary>
    private async Task<QuizResultResponse> GradeLocallyAsync(QuizSubmitRequest req)
    {
        var cards = await ResolveCardsForEvaluationAsync(req.ModuleId, req.Category, req.CardId);
        var graded = new List<GradedAnswer>();

        foreach (var a in req.Answers)
        {
            var card = FindCard(cards, a);
            var reference = BuildReferenceAnswer(card);
            var refWords = Tokenize(reference);
            var ansWords = Tokenize(a.UserAnswer);
            var overlap = refWords.Count == 0 ? 0 : (double)ansWords.Intersect(refWords).Count() / Math.Min(refWords.Count, 12);
            var score = Math.Round(Math.Clamp(overlap, 0, 1) * 10, 1);
            graded.Add(new GradedAnswer
            {
                Question = a.Question,
                UserAnswer = a.UserAnswer,
                SourceCardId = a.SourceCardId,
                SourceTerm = card?.Term ?? "",
                Score = score,
                Max = 10,
                Feedback = "Offline-Bewertung (Stichwort-Heuristik) — für eine fundierte KI-Bewertung OPENAI_API_KEY setzen.",
                ReferenceAnswer = reference,
                CompletedSolution = BuildCompletedSolution(a.UserAnswer, card),
                OfficialSources = card?.OfficialSources ?? new(),
            });
        }

        var total = graded.Sum(x => x.Score);
        var maxScore = graded.Count * 10.0;
        var pct = maxScore > 0 ? total / maxScore * 100 : 0;
        return new QuizResultResponse
        {
            Score = total,
            MaxScore = maxScore,
            Percentage = Math.Round(pct, 1),
            Grade = pct >= 90 ? "A" : pct >= 75 ? "B" : pct >= 60 ? "C" : pct >= 45 ? "D" : "F",
            Feedback = "Hinweis: Es war kein OpenAI-Schlüssel konfiguriert — die Bewertung erfolgte lokal per Stichwort-Heuristik und ist nur ein grober Anhaltspunkt.",
            GradedAnswers = graded,
        };
    }

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

    private static Card? FindCard(List<Card> cards, QuizAnswerItem answer) =>
        cards.FirstOrDefault(c => c.Id == answer.SourceCardId)
        ?? cards.FirstOrDefault(c => c.Question == answer.Question);

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

    private static string FormatOfficialSources(Card card) =>
        card.OfficialSources.Count == 0
            ? "Keine offiziellen Quellen hinterlegt."
            : string.Join("\n", card.OfficialSources.Select(s => $"- {s.Title} ({s.Publisher}): {s.Url}"));

    private static IEnumerable<CardQuizQuestion> ParseCardQuizResponse(JsonElement root)
    {
        JsonElement questions = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("questions", out var arr) => arr,
            _ => default,
        };

        if (questions.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in questions.EnumerateArray())
        {
            List<string> options = new();
            if (item.TryGetProperty("options", out var rawOptions) && rawOptions.ValueKind == JsonValueKind.Array)
            {
                options = rawOptions.EnumerateArray()
                    .Select(x => x.GetString() ?? "")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            }

            yield return new CardQuizQuestion
            {
                Type = item.TryGetProperty("type", out var type) ? type.GetString() ?? "single_choice" : "single_choice",
                Question = item.TryGetProperty("question", out var question) ? question.GetString() ?? "" : "",
                Options = options,
                CorrectIndex = item.TryGetProperty("correct_index", out var correctIndex) ? correctIndex.GetInt32() : -1,
                Explanation = item.TryGetProperty("explanation", out var explanation) ? explanation.GetString() ?? "" : "",
            };
        }
    }

    private static bool IsValidCardQuizQuestion(CardQuizQuestion question) =>
        question.Type == "single_choice"
        && !string.IsNullOrWhiteSpace(question.Question)
        && question.Options.Count == 4
        && question.Options.All(x => !string.IsNullOrWhiteSpace(x))
        && question.CorrectIndex >= 0
        && question.CorrectIndex < question.Options.Count
        && !string.IsNullOrWhiteSpace(question.Explanation);

    private async Task<List<Card>> ResolveQuizCardsAsync(string moduleId, string category, string userSub, string? cardId)
    {
        if (!string.IsNullOrWhiteSpace(cardId))
        {
            var card = await _repo.GetCardAsync(cardId);
            if (card is null || card.Archived || !string.Equals(card.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase))
                return new();
            return new List<Card> { card };
        }

        return await _repo.ListCheckedCardsAsync(moduleId, string.IsNullOrEmpty(category) ? null : category, userSub);
    }

    private async Task<List<Card>> ResolveCardsForEvaluationAsync(string moduleId, string category, string? cardId)
    {
        if (!string.IsNullOrWhiteSpace(cardId))
        {
            var card = await _repo.GetCardAsync(cardId);
            if (card is null || card.Archived || !string.Equals(card.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase))
                return new();
            return new List<Card> { card };
        }

        return await _repo.ListCardsAsync(moduleId, string.IsNullOrEmpty(category) ? null : category, archived: false);
    }
}
