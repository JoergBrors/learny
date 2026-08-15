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
    public string UserSub { get; set; } = "";
    public string Category { get; set; } = "";
    public double Score { get; set; }
    public double MaxScore { get; set; }
    public string Grade { get; set; } = "F";
    public string Feedback { get; set; } = "";
    public string AnswersJson { get; set; } = "[]";
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
        return (true, "");
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
    public int NumQuestions { get; set; } = 5;
}

public class QuizQuestion
{
    public string Question { get; set; } = "";
    public string Topic { get; set; } = "";
}

public class QuizAnswerItem
{
    public string Question { get; set; } = "";
    public string UserAnswer { get; set; } = "";
}

public class QuizSubmitRequest
{
    public string ModuleId { get; set; } = "";
    public string Category { get; set; } = "";
    public List<QuizAnswerItem> Answers { get; set; } = new();
}

public class GradedAnswer
{
    public string Question { get; set; } = "";
    public string UserAnswer { get; set; } = "";
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
    public List<GradedAnswer> GradedAnswers { get; set; } = new();
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
