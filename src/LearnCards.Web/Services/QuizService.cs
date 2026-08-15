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

    public QuizService(CardRepository repo, OpenAiClient openAi)
    {
        _repo = repo;
        _openAi = openAi;
    }

    public async Task<List<QuizQuestion>> StartQuizAsync(string moduleId, string category, int numQuestions, string userSub, CancellationToken ct = default)
    {
        numQuestions = Math.Clamp(numQuestions, 1, 20);
        var cards = await _repo.ListCheckedCardsAsync(moduleId, string.IsNullOrEmpty(category) ? null : category, userSub);
        if (cards.Count == 0)
            throw new InvalidOperationException("Keine markierten Karten für dieses Modul / diese Kategorie gefunden. Markiere zuerst Karten mit Check.");

        if (!_openAi.IsConfigured)
        {
            // Fallback: Fragen direkt aus den Karten ziehen
            var rnd = new Random();
            return cards.OrderBy(_ => rnd.Next()).Take(numQuestions)
                .Select(c => new QuizQuestion { Question = c.Question, Topic = c.Category }).ToList();
        }

        var cardSummaries = string.Join("\n",
            cards.Take(40).Select(c => $"- {c.Term}: {c.Definition[..Math.Min(200, c.Definition.Length)]}"));

        var topicLabel = string.IsNullOrEmpty(category) ? "Alle Kategorien" : category;
        var prompt = $$"""
            Du bist ein Prüfer für das Thema '{{topicLabel}}'.
            Generiere {{numQuestions}} Prüfungsfragen auf Basis dieser Lernkarten:

            {{cardSummaries}}

            Regeln:
            - Jede Frage muss im Fließtext beantwortet werden (keine Multiple Choice)
            - Fragen sollen Verständnis prüfen, nicht nur Definitionen abfragen
            - Schwierigkeitsgrad: Level 400 (tiefes technisches Verständnis)
            - Antworte NUR als JSON-Objekt: {"questions": [{"question": "...", "topic": "..."}]}
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
                    questions.Add(new QuizQuestion
                    {
                        Question = el.TryGetProperty("question", out var qq) ? qq.GetString() ?? "" : "",
                        Topic = el.TryGetProperty("topic", out var tt) ? tt.GetString() ?? "" : category,
                    });
            questions = questions.Where(x => x.Question.Length > 0).Take(numQuestions).ToList();
            if (questions.Count > 0) return questions;
        }
        catch (InvalidOperationException) { /* Fallback unten */ }

        return new List<QuizQuestion> { new() { Question = "Definiere den Begriff in eigenen Worten.", Topic = category } };
    }

    public async Task<QuizResultResponse> SubmitQuizAsync(QuizSubmitRequest req, string userSub, CancellationToken ct = default)
    {
        QuizResultResponse result;
        if (_openAi.IsConfigured)
            result = await GradeWithOpenAiAsync(req, ct);
        else
            result = await GradeLocallyAsync(req);

        var record = new QuizResultRecord
        {
            ModuleId = req.ModuleId,
            UserSub = userSub,
            Category = req.Category,
            Score = result.Score,
            MaxScore = result.MaxScore,
            Grade = result.Grade,
            Feedback = result.Feedback,
            AnswersJson = JsonSerializer.Serialize(result.GradedAnswers, Api.AppJson.Options),
        };
        await _repo.SaveQuizResultAsync(record);
        return result;
    }

    private async Task<QuizResultResponse> GradeWithOpenAiAsync(QuizSubmitRequest req, CancellationToken ct)
    {
        var cards = await _repo.ListCardsAsync(req.ModuleId, string.IsNullOrEmpty(req.Category) ? null : req.Category, archived: false);
        var qaPairs = string.Join("\n", req.Answers.Select((a, i) => $"Frage {i + 1}: {a.Question}\nAntwort: {a.UserAnswer}"));
        var references = string.Join("\n\n", req.Answers.Select((a, i) =>
        {
            var card = cards.FirstOrDefault(c => c.Question == a.Question);
            var referenceAnswer = BuildReferenceAnswer(card);
            var sources = card is null || card.OfficialSources.Count == 0
                ? "Keine offiziellen Quellen hinterlegt."
                : string.Join("\n", card.OfficialSources.Select(s => $"- {s.Title}: {s.Url}"));
            return $"Referenz {i + 1}:\nFrage: {a.Question}\nMusterlösung: {referenceAnswer}\nOffizielle Quellen:\n{sources}";
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
            foreach (var el in arr.EnumerateArray())
            {
                var question = el.TryGetProperty("question", out var q) ? q.GetString() ?? "" : "";
                var card = cards.FirstOrDefault(c => c.Question == question);
                graded.Add(new GradedAnswer
                {
                    Question = question,
                    UserAnswer = el.TryGetProperty("user_answer", out var ua) ? ua.GetString() ?? "" : "",
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
        var cards = await _repo.ListCardsAsync(req.ModuleId, string.IsNullOrEmpty(req.Category) ? null : req.Category, archived: false);
        var graded = new List<GradedAnswer>();

        foreach (var a in req.Answers)
        {
            var card = cards.FirstOrDefault(c => c.Question == a.Question);
            var reference = BuildReferenceAnswer(card);
            var refWords = Tokenize(reference);
            var ansWords = Tokenize(a.UserAnswer);
            var overlap = refWords.Count == 0 ? 0 : (double)ansWords.Intersect(refWords).Count() / Math.Min(refWords.Count, 12);
            var score = Math.Round(Math.Clamp(overlap, 0, 1) * 10, 1);
            graded.Add(new GradedAnswer
            {
                Question = a.Question,
                UserAnswer = a.UserAnswer,
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
}
