namespace LearnCards.Web.Domain;

/// <summary>Aktive Freigabe eines eigenen Moduls an einen Empfänger (ADR 0015).</summary>
public class ShareInfo
{
    public string ModuleId { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string RecipientUserId { get; set; } = "";
    public string RecipientName { get; set; } = "";
    public string RecipientEmail { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>Einreichung eines Moduls zur globalen Veröffentlichung.</summary>
public class SubmissionInfo
{
    public string Id { get; set; } = "";
    public string PublicationId { get; set; } = "";
    /// <summary>"new" (erste Veröffentlichung) oder "update" (neue Version einer bestehenden Veröffentlichung)</summary>
    public string Kind { get; set; } = "new";
    public string? SourceModuleId { get; set; }
    public string? StagedModuleId { get; set; }
    public string? SubmitterUserId { get; set; }
    public string SubmitterName { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public int CardCount { get; set; }
    public int Version { get; set; }
    /// <summary>"pending", "approved" oder "rejected"</summary>
    public string Status { get; set; } = "pending";
    public string Reason { get; set; } = "";
    public DateTime SubmittedAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
}

/// <summary>Kurzer Treffer der Benutzersuche für die Empfängerauswahl.</summary>
public class UserSearchResult
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
}

/// <summary>Ergebnisübersicht einer globalen Modulversion (nur aggregierte Werte).</summary>
public class GlobalResultSummary
{
    public string ModuleId { get; set; } = "";
    public string PublicationId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public string Status { get; set; } = "";
    public int Attempts { get; set; }
    public int Learners { get; set; }
    public double AveragePercent { get; set; }
}

/// <summary>Einzelnes globales Ergebnis für die berechtigte Detailansicht — ohne Antworten und Feedbacktexte.</summary>
public class GlobalResultRow
{
    public string ResultId { get; set; } = "";
    public string LearnerName { get; set; } = "";
    public string Category { get; set; } = "";
    public double Score { get; set; }
    public double MaxScore { get; set; }
    public string Grade { get; set; } = "";
    public DateTime CompletedAt { get; set; }
}
