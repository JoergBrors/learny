namespace LearnCards.Web.Domain;

/// <summary>Fragetypen der einheitlichen Quiz-Engine (ADR 0019).</summary>
public static class QuizTypes
{
    public const string SingleChoice = "single_choice";
    public const string MultipleResponse = "multiple_response";
    public const string FreeText = "free_text";

    public static readonly string[] All = { SingleChoice, MultipleResponse, FreeText };
    public static bool IsChoice(string type) => type is SingleChoice or MultipleResponse;
}

/// <summary>Herkunft einer Frage im Pool.</summary>
public static class QuizOrigins
{
    public const string Manual = "manual";
    public const string Ai = "ai";
}

/// <summary>
/// Wo die Frage fachlich gepflegt wird (genau ein Ort je Frage):
/// <c>card_json</c> = <c>quiz[]</c> der Karte (Import/Export-Vertrag, Projektion in den Pool), <c>ai</c> = KI-generiert und im Pool gespeichert,
/// <c>editor</c> = direkt im Pool gepflegt (Auftrag 2).
/// </summary>
public static class QuizSources
{
    public const string CardJson = "card_json";
    public const string Ai = "ai";
    public const string Editor = "editor";
}

/// <summary>
/// Quizkonfiguration eines Lernmoduls (genau eine je Modul bzw. Modulversion). Fehlt sie, gelten kompatible Standardwerte.
/// Serialisierung: snake_case (<c>AppJson.Options</c>).
/// </summary>
public class QuizConfig
{
    public const string OriginFixed = "fixed";
    public const string OriginAi = "ai";
    public const string OriginMixed = "mixed";
    public const string StrategyNone = "none";
    public const string StrategyOnDemand = "on_demand";
    public const string StrategyPreferStored = "prefer_stored";
    public const string StrategyMix = "mix";
    public const string ScopeCard = "card";
    public const string ScopeModule = "module";
    public const string ScopeBoth = "both";

    public bool Enabled { get; set; } = true;
    /// <summary>Nein ⇒ nur Freitext erlaubt (Single Choice und Multiple Response müssen deaktiviert sein).</summary>
    public bool AllowMultipleChoice { get; set; } = true;
    /// <summary>fixed | ai | mixed — Herkunft der Fragen.</summary>
    public string Origin { get; set; } = OriginMixed;
    /// <summary>none | on_demand | prefer_stored | mix — Umgang mit gespeicherten und neu zu generierenden Fragen.</summary>
    public string Strategy { get; set; } = StrategyOnDemand;
    public bool SingleChoice { get; set; } = true;
    public bool MultipleResponse { get; set; } = true;
    public bool FreeText { get; set; } = true;
    public int QuestionCount { get; set; } = 5;
    public bool ShuffleQuestions { get; set; } = true;
    public bool ShuffleAnswers { get; set; }
    /// <summary>card | module | both — von wo aus ein Quiz gestartet werden darf (kartenbezogen, modulweit oder beides).</summary>
    public string Scope { get; set; } = ScopeBoth;
    public int PassPercent { get; set; } = 60;
    public bool AllowRetry { get; set; } = true;
    public bool ShowCorrectAnswers { get; set; } = true;

    public bool IsFormatAllowed(string type) => type switch
    {
        QuizTypes.SingleChoice => AllowMultipleChoice && SingleChoice,
        QuizTypes.MultipleResponse => AllowMultipleChoice && MultipleResponse,
        QuizTypes.FreeText => FreeText,
        _ => false,
    };

    public List<string> AllowedTypes() => QuizTypes.All.Where(IsFormatAllowed).ToList();

    /// <summary>Alle Verstöße gegen die zulässigen Kombinationen (leer = gültig).</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (!AllowMultipleChoice && (SingleChoice || MultipleResponse))
            errors.Add("Multiple Choice ist deaktiviert: 'single_choice' und 'multiple_response' müssen ebenfalls deaktiviert sein.");
        if (Origin is not (OriginFixed or OriginAi or OriginMixed)) errors.Add("'origin' muss fixed, ai oder mixed sein.");
        if (Strategy is not (StrategyNone or StrategyOnDemand or StrategyPreferStored or StrategyMix)) errors.Add("'strategy' muss none, on_demand, prefer_stored oder mix sein.");
        if (Scope is not (ScopeCard or ScopeModule or ScopeBoth)) errors.Add("'scope' muss card, module oder both sein.");
        if (QuestionCount is < 1 or > 20) errors.Add("'question_count' muss zwischen 1 und 20 liegen.");
        if (PassPercent is < 1 or > 100) errors.Add("'pass_percent' muss zwischen 1 und 100 liegen.");
        if (Enabled && AllowedTypes().Count == 0) errors.Add("Mindestens ein Frageformat muss erlaubt sein (Single Choice, Multiple Response oder Freitext).");
        if (Origin == OriginFixed && Strategy != StrategyNone)
            errors.Add("Bei 'nur feste Fragen' wird nichts generiert: 'strategy' muss 'none' sein.");
        return errors;
    }

    public QuizConfig Clone() => (QuizConfig)MemberwiseClone();
}

/// <summary>Frage im persistenten Fragenpool (<c>quiz_questions</c>).</summary>
public class QuizPoolQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ModuleId { get; set; } = "";
    public string? CardId { get; set; }
    public string Type { get; set; } = QuizTypes.SingleChoice;
    public string Question { get; set; } = "";
    public List<string> Options { get; set; } = new();
    /// <summary>0-basierte Indizes der richtigen Optionen (single_choice: genau einer; multiple_response: mindestens einer; free_text: leer).</summary>
    public List<int> CorrectIndexes { get; set; } = new();
    /// <summary>Erklärung bzw. bei Freitext die Musterlösung.</summary>
    public string Explanation { get; set; } = "";
    public string Origin { get; set; } = QuizOrigins.Manual;
    public string Source { get; set; } = QuizSources.Editor;
    public bool Active { get; set; } = true;
    /// <summary>Globale Modulversion, zu der die Frage gehört (null bei privaten Modulen).</summary>
    public int? ModuleVersion { get; set; }
    public string Fingerprint { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Eingabe zum Anlegen/Ändern einer Pool-Frage (Service-Schnittstelle für den Editor, Auftrag 2).</summary>
public class QuizQuestionInput
{
    public string? CardId { get; set; }
    public string Type { get; set; } = QuizTypes.SingleChoice;
    public string Question { get; set; } = "";
    public List<string> Options { get; set; } = new();
    public List<int> CorrectIndexes { get; set; } = new();
    public string Explanation { get; set; } = "";
}

// ─── Quizsitzung (serverseitig, enthält die Lösungen) ───────────────────────

/// <summary>Frage innerhalb einer Quizsitzung — mit Lösung, nur serverseitig gespeichert.</summary>
public class QuizSessionQuestion
{
    public string QuestionId { get; set; } = "";
    public string? CardId { get; set; }
    public string SourceTerm { get; set; } = "";
    public string Topic { get; set; } = "";
    public string Type { get; set; } = QuizTypes.SingleChoice;
    public string Question { get; set; } = "";
    /// <summary>Optionen in der Reihenfolge, wie sie dem Benutzer gezeigt wurden (ggf. gemischt).</summary>
    public List<string> Options { get; set; } = new();
    public List<int> CorrectIndexes { get; set; } = new();
    public string Explanation { get; set; } = "";
    public string Origin { get; set; } = QuizOrigins.Manual;
    /// <summary>Frage wurde nicht im Pool gespeichert (z. B. Lernender in freigegebenem/globalem Modul, Offline-Ableitung aus der Karte).</summary>
    public bool Ephemeral { get; set; }
}

public class QuizSessionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserSub { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public string? CardId { get; set; }
    public string Category { get; set; } = "";
    public string Context { get; set; } = "private";
    public int? ModuleVersion { get; set; }
    public QuizConfig Config { get; set; } = new();
    public List<QuizSessionQuestion> Questions { get; set; } = new();
    /// <summary>open | submitted</summary>
    public string Status { get; set; } = "open";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Antwort von <c>/api/quiz/start</c> — Fragen ohne Lösungen.</summary>
public class QuizStartResponse
{
    public string SessionId { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public string Category { get; set; } = "";
    public string CardId { get; set; } = "";
    public int PassPercent { get; set; }
    public bool ShowCorrectAnswers { get; set; }
    public List<QuizQuestion> Questions { get; set; } = new();
    /// <summary>Hinweise (z. B. „KI nicht verfügbar — gespeicherte Fragen verwendet“).</summary>
    public List<string> Notices { get; set; } = new();
}

/// <summary>Zusammenfassung der Quizkonfiguration für Clients (inkl. KI-Verfügbarkeit und Berechtigung).</summary>
public class QuizConfigView
{
    public string ModuleId { get; set; } = "";
    public QuizConfig Config { get; set; } = new();
    public bool CanEdit { get; set; }
    public bool AiAvailable { get; set; }
    public int? ModuleVersion { get; set; }
    public string Context { get; set; } = "private";
}

/// <summary>Serverseitig verifiziertes Ergebnis eines Quizversuchs (Schnittstelle für Auftrag 3 — noch keine Zertifikate).</summary>
public class VerifiedQuizResult
{
    public string ResultId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public int? ModuleVersion { get; set; }
    public double Score { get; set; }
    public double MaxScore { get; set; }
    public double Percentage { get; set; }
    public int PassPercent { get; set; }
    public bool Passed { get; set; }
    public DateTime CompletedAt { get; set; }
}
