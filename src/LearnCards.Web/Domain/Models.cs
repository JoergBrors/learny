using System.Text.Json.Serialization;

namespace LearnCards.Web.Domain;

/// <summary>Lernmodul (z. B. "Azure Networking L400").</summary>
public class ModuleInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "📚";
    public string Color { get; set; } = "#2AA79B";
    /// <summary>Eigentümer (interne Benutzer-ID); null = unzugeordneter Altbestand (nur für Administratoren sichtbar) oder globales Modul.</summary>
    public string? OwnerUserId { get; set; }
    /// <summary>"private" (Eigentum eines Benutzers) oder "global" (vom Administrator veröffentlichte Version, ADR 0015).</summary>
    public string Scope { get; set; } = "private";
    /// <summary>Stabile Identität einer globalen Veröffentlichung über alle ihre Versionen hinweg.</summary>
    public string? PublicationId { get; set; }
    public int? GlobalVersion { get; set; }
    /// <summary>"staged" (eingereicht, nicht lernbar), "active", "superseded" (durch neuere Version ersetzt) oder "deactivated".</summary>
    public string? GlobalStatus { get; set; }
    public string? SourceModuleId { get; set; }
    public string? SourceOwnerUserId { get; set; }
    public string? SourceOwnerName { get; set; }
    public DateTime? PublishedAt { get; set; }
    /// <summary>Kontext aus Sicht des anfragenden Benutzers: "private", "shared" oder "global" (nicht gespeichert).</summary>
    public string Context { get; set; } = "private";
    /// <summary>Anzeigename des Eigentümers bzw. Herausgebers (bei "shared"/"global"; nicht gespeichert).</summary>
    public string OwnerName { get; set; } = "";
    public bool IsGlobal => Scope == "global";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int CardCount { get; set; }
    public int ActiveCount { get; set; }
}

/// <summary>Eine Lernkarte.</summary>
public class Card
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ModuleId { get; set; } = "";
    /// <summary>Eigentümer (interne Benutzer-ID); entspricht immer dem Eigentümer des Moduls.</summary>
    public string? OwnerUserId { get; set; }
    public string Category { get; set; } = "";
    public string Term { get; set; } = "";
    public string Question { get; set; } = "";
    public string Definition { get; set; } = "";
    public string HowItWorks { get; set; } = "";
    public string Context { get; set; } = "";
    public string KeyFact { get; set; } = "";
    public string ReferenceAnswer { get; set; } = "";
    public string ChatPrompt { get; set; } = "";
    public List<OfficialSource> OfficialSources { get; set; } = new();
    public int? SlideNumber { get; set; }
    public int? TargetTimeSec { get; set; }
    public List<CardQuizQuestion> Quiz { get; set; } = new();
    /// <summary>Optionale Bilder (Base64, ADR 0016); leer = keine Bilder.</summary>
    public List<CardImage> Images { get; set; } = new();
    public bool Archived { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class OfficialSource
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Publisher { get; set; } = "";
}

/// <summary>Gespeichertes Quiz-Ergebnis eines Benutzers.</summary>
public class QuizResultRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ModuleId { get; set; } = "";
    public string CardId { get; set; } = "";
    public string UserSub { get; set; } = "";
    public string Category { get; set; } = "";
    public double Score { get; set; }
    public double MaxScore { get; set; }
    public string Grade { get; set; } = "F";
    public string Feedback { get; set; } = "";
    public string AnswersJson { get; set; } = "[]";
    public string StatsJson { get; set; } = "{}";
    /// <summary>Quizsitzung (ADR 0019), aus der das Ergebnis serverseitig berechnet wurde; leer bei Altdaten.</summary>
    public string? SessionId { get; set; }
    /// <summary>Bestanden laut Bestehensgrenze zum Zeitpunkt des Versuchs; null bei Altdaten.</summary>
    public bool? Passed { get; set; }
    public int? PassPercent { get; set; }
    /// <summary>Globale Modulversion des Versuchs; null bei privaten Modulen.</summary>
    public int? ModuleVersion { get; set; }
    /// <summary>Lernkontext des Ergebnisses: "private", "shared" oder "global" (ADR 0015).</summary>
    public string Context { get; set; } = "private";
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}

public class UserPreference
{
    public string UserSub { get; set; } = "";
    public string Theme { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class UserCardState
{
    public string UserSub { get; set; } = "";
    public string CardId { get; set; } = "";
    public bool IsChecked { get; set; }
    public bool MarkedReview { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ChatHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserSub { get; set; } = "";
    public string CardId { get; set; } = "";
    public string OriginalQuestion { get; set; } = "";
    public string NormalizedQuestion { get; set; } = "";
    public string AssistantAnswer { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Kanonisches Karten-JSON-Format (identisch zum Python-Original) —
/// Austauschformat für API, MCP-Server und Import/Export. snake_case über globale JSON-Optionen.
/// </summary>
public class CardJson
{
    public string? Id { get; set; }
    public string Module { get; set; } = "";
    public string Category { get; set; } = "";
    public string Term { get; set; } = "";
    public string Question { get; set; } = "";
    public string Definition { get; set; } = "";
    public string HowItWorks { get; set; } = "";
    public string Context { get; set; } = "";
    public string KeyFact { get; set; } = "";
    public string ReferenceAnswer { get; set; } = "";
    public string ChatPrompt { get; set; } = "";
    public List<OfficialSource> OfficialSources { get; set; } = new();
    public int? SlideNumber { get; set; }
    public int? TargetTimeSec { get; set; }
    public List<CardQuizQuestion> Quiz { get; set; } = new();
    /// <summary>
    /// Optionale Bilder (ADR 0016). <c>null</c> = Feld fehlt: neue Karte ohne Bilder, bestehende Karte behält ihre Bilder;
    /// <c>[]</c> = Bilder bewusst entfernen.
    /// </summary>
    public List<CardImage>? Images { get; set; }
    public bool Archived { get; set; }
    public int SortOrder { get; set; }

    public (bool Ok, string Error) Validate()
    {
        if (string.IsNullOrWhiteSpace(Module)) return (false, "'module' ist erforderlich");
        if (string.IsNullOrWhiteSpace(Category)) return (false, "'category' ist erforderlich");
        if (string.IsNullOrWhiteSpace(Term)) return (false, "'term' ist erforderlich");
        if (string.IsNullOrWhiteSpace(Question)) return (false, "'question' ist erforderlich");
        if (string.IsNullOrWhiteSpace(Definition)) return (false, "'definition' ist erforderlich");
        foreach (var source in OfficialSources)
        {
            if (string.IsNullOrWhiteSpace(source.Title)) return (false, "Jede offizielle Quelle benötigt einen 'title'");
            if (string.IsNullOrWhiteSpace(source.Url)) return (false, "Jede offizielle Quelle benötigt eine 'url'");
            if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || uri.Scheme is not "https")
                return (false, "Offizielle Quellen müssen absolute HTTPS-URLs sein");
        }
        if (SlideNumber is <= 0) return (false, "'slide_number' muss größer als 0 sein");
        if (TargetTimeSec is <= 0) return (false, "'target_time_sec' muss größer als 0 sein");
        foreach (var quiz in Quiz)
        {
            var (quizOk, quizError) = quiz.Validate();
            if (!quizOk) return (false, quizError);
        }
        var (imagesOk, imagesError) = CardImageValidator.ValidateAndNormalize(Images);
        if (!imagesOk) return (false, imagesError);
        return (true, "");
    }
}

/// <summary>
/// Frage im kanonischen <c>quiz[]</c> der Karte (ADR 0006/0010/0019). <c>single_choice</c>: genau 4 Optionen + <c>correct_index</c> (unverändert).
/// Optional und abwärtskompatibel: <c>multiple_response</c> (3–6 Optionen, <c>correct_indexes</c>) und <c>free_text</c> (ohne Optionen, <c>explanation</c> = Musterlösung).
/// </summary>
public class CardQuizQuestion
{
    public string Type { get; set; } = "single_choice";
    public string Question { get; set; } = "";
    public List<string> Options { get; set; } = new();
    public int CorrectIndex { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? CorrectIndexes { get; set; }
    public string Explanation { get; set; } = "";

    public (bool Ok, string Error) Validate()
    {
        if (string.IsNullOrWhiteSpace(Question)) return (false, "Jede Quizfrage benötigt ein 'question'");
        if (string.IsNullOrWhiteSpace(Explanation)) return (false, "Jede Quizfrage benötigt eine 'explanation'");
        switch (Type)
        {
            case QuizTypes.SingleChoice:
                if (Options is null || Options.Count != 4 || Options.Any(string.IsNullOrWhiteSpace))
                    return (false, "Jede Quizfrage benötigt genau 4 nicht-leere 'options'");
                if (CorrectIndex < 0 || CorrectIndex >= Options.Count)
                    return (false, "'correct_index' muss auf eine vorhandene Option zeigen");
                return (true, "");
            case QuizTypes.MultipleResponse:
                if (Options is null || Options.Count is < 3 or > 6 || Options.Any(string.IsNullOrWhiteSpace))
                    return (false, "multiple_response benötigt 3 bis 6 nicht-leere 'options'");
                if (CorrectIndexes is null || CorrectIndexes.Count == 0 || CorrectIndexes.Distinct().Count() != CorrectIndexes.Count
                    || CorrectIndexes.Any(i => i < 0 || i >= Options.Count))
                    return (false, "'correct_indexes' muss mindestens eine, eindeutige, vorhandene Option enthalten");
                return (true, "");
            case QuizTypes.FreeText:
                return (true, "");
            default:
                return (false, "quiz.type muss 'single_choice', 'multiple_response' oder 'free_text' sein");
        }
    }
}

public class CardImportBatch
{
    public List<CardJson> Cards { get; set; } = new();
    public bool OverwriteExisting { get; set; }
}

public class ModuleCreateRequest
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "📚";
    public string Color { get; set; } = "#2AA79B";
}

// ─── Quiz DTOs ──────────────────────────────────────────────────────────────

public class QuizStartRequest
{
    public string ModuleId { get; set; } = "";
    public string Category { get; set; } = "";
    public string CardId { get; set; } = "";
    public int NumQuestions { get; set; } = 5;
}

/// <summary>Frage für den Client (ohne Lösung). Alte Clients lesen weiter nur <c>question</c>/<c>topic</c>/<c>source_card_id</c>/<c>source_term</c>.</summary>
public class QuizQuestion
{
    public string Question { get; set; } = "";
    public string Topic { get; set; } = "";
    public string SourceCardId { get; set; } = "";
    public string SourceTerm { get; set; } = "";
    public string QuestionId { get; set; } = "";
    /// <summary>single_choice | multiple_response | free_text</summary>
    public string Type { get; set; } = QuizTypes.FreeText;
    public List<string> Options { get; set; } = new();
}

public class QuizAnswerItem
{
    public string Question { get; set; } = "";
    public string UserAnswer { get; set; } = "";
    public string SourceCardId { get; set; } = "";
    /// <summary>Frage-ID aus <c>/quiz/start</c> (maßgeblich bei Sitzungen).</summary>
    public string QuestionId { get; set; } = "";
    /// <summary>Gewählte Optionen (0-basiert) bei single_choice/multiple_response.</summary>
    public List<int> SelectedIndexes { get; set; } = new();
}

public class QuizSubmitRequest
{
    public string ModuleId { get; set; } = "";
    public string Category { get; set; } = "";
    public string CardId { get; set; } = "";
    /// <summary>Sitzung aus <c>/quiz/start</c>. Ohne Sitzung gilt der ältere Freitext-Ablauf (Rückwärtskompatibilität).</summary>
    public string SessionId { get; set; } = "";
    public List<QuizAnswerItem> Answers { get; set; } = new();
}

public class GradedAnswer
{
    public string Question { get; set; } = "";
    public string UserAnswer { get; set; } = "";
    public string SourceCardId { get; set; } = "";
    public string SourceTerm { get; set; } = "";
    public string QuestionId { get; set; } = "";
    public string Type { get; set; } = QuizTypes.FreeText;
    public List<string> Options { get; set; } = new();
    public List<int> SelectedIndexes { get; set; } = new();
    /// <summary>Richtige Optionen — nur gefüllt, wenn die Konfiguration „richtige Antworten anzeigen“ erlaubt.</summary>
    public List<int> CorrectIndexes { get; set; } = new();
    public bool? Correct { get; set; }
    public string Explanation { get; set; } = "";
    public double Score { get; set; }
    public double Max { get; set; } = 10;
    public string Feedback { get; set; } = "";
    public string ReferenceAnswer { get; set; } = "";
    public string CompletedSolution { get; set; } = "";
    public List<OfficialSource> OfficialSources { get; set; } = new();
}

public class QuizResultResponse
{
    public double Score { get; set; }
    public double MaxScore { get; set; }
    public double Percentage { get; set; }
    public string Grade { get; set; } = "F";
    public string Feedback { get; set; } = "";
    public QuizSessionStats Stats { get; set; } = new();
    public List<GradedAnswer> GradedAnswers { get; set; } = new();
    public string ResultId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public bool? Passed { get; set; }
    public int? PassPercent { get; set; }
    public int? ModuleVersion { get; set; }
    public bool ShowCorrectAnswers { get; set; } = true;
}

public class QuizSessionStats
{
    public int CheckedCardCount { get; set; }
    public int UsedCheckedCardCount { get; set; }
    public int QuestionCount { get; set; }
    public double CheckedCoverageRatio { get; set; }
    public double DistributionWeight { get; set; }
    public double QuestionsPerUsedCard { get; set; }
    public List<QuizCardUsage> CardUsage { get; set; } = new();
}

public class QuizCardUsage
{
    public string CardId { get; set; } = "";
    public string Term { get; set; } = "";
    public string Category { get; set; } = "";
    public int QuestionCount { get; set; }
    public double RelativeWeight { get; set; }
}

public class QuizHistoryEntry
{
    public QuizResultRecord Result { get; set; } = new();
    public QuizSessionStats Stats { get; set; } = new();
    public List<GradedAnswer> Answers { get; set; } = new();
}

public class CardStateUpdateRequest
{
    public bool IsChecked { get; set; }
    public bool MarkedReview { get; set; }
}

public class ThemeUpdateRequest
{
    public string Theme { get; set; } = "";
}

// ─── Chat DTOs ──────────────────────────────────────────────────────────────

public class ChatMessage
{
    public string Role { get; set; } = "user";     // user | assistant
    public string Content { get; set; } = "";
}

public class ChatRequest
{
    public string CardId { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = new();
}
