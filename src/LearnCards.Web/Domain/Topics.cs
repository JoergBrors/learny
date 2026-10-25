namespace LearnCards.Web.Domain;

// Themen, Lernpfade, Versionierung und Abschlussprüfungen (ADR 0027).
// Hierarchie: Thema → Lernmodule → Lernkarten und Modulprüfungen → Abschlussprüfung.

public static class TopicStatuses
{
    public const string Active = "active";
    public const string Archived = "archived";
}

/// <summary>Zustand einer Themenversion. <c>building</c> ist nur während der Erzeugung sichtbar; <c>failed</c> bleibt zur Nachvollziehbarkeit erhalten.</summary>
public static class TopicVersionStatuses
{
    public const string Building = "building";
    public const string Published = "published";
    public const string Failed = "failed";
}

/// <summary>Globale Sichtbarkeit einer veröffentlichten Version (Administratorfreigabe, wie ADR 0015).</summary>
public static class TopicGlobalStatuses
{
    public const string None = "none";
    public const string PendingApproval = "pending_approval";
    public const string Active = "active";
    public const string Superseded = "superseded";
    public const string Rejected = "rejected";
    public const string Deactivated = "deactivated";
}

/// <summary>Bereiche der Themenübersicht.</summary>
public static class TopicSections
{
    public const string Own = "own";
    public const string Shared = "shared";
    public const string Assigned = "assigned";
    public const string Global = "global";
}

public static class TopicExamKinds
{
    public const string Module = "module";
    public const string Final = "final";
}

// ─── Prüfungskonfiguration ──────────────────────────────────────────────────

/// <summary>Modulprüfung innerhalb eines Themas. Wird in eine <see cref="QuizConfig"/> der bestehenden Quiz-Engine (ADR 0019) übersetzt.</summary>
public class TopicModuleExamConfig
{
    public bool Enabled { get; set; }
    public int QuestionCount { get; set; } = 5;
    public bool SingleChoice { get; set; } = true;
    public bool MultipleResponse { get; set; } = true;
    public bool FreeText { get; set; } = true;
    public int PassPercent { get; set; } = 60;
    public bool AllowRetry { get; set; } = true;
    public bool ShuffleQuestions { get; set; } = true;
    public bool ShuffleAnswers { get; set; }
    /// <summary>Erklärungen und richtige Antworten nach der Abgabe anzeigen.</summary>
    public bool ShowExplanations { get; set; } = true;
    /// <summary>Mindestleistung (Prozent) in der Modulprüfung, um den nächsten Lernschritt freizuschalten; 0 = keine Voraussetzung.</summary>
    public int MinPercentForNext { get; set; }

    public List<string> AllowedTypes()
    {
        var l = new List<string>();
        if (SingleChoice) l.Add(QuizTypes.SingleChoice);
        if (MultipleResponse) l.Add(QuizTypes.MultipleResponse);
        if (FreeText) l.Add(QuizTypes.FreeText);
        return l;
    }

    public List<string> Validate(string label)
    {
        var e = new List<string>();
        if (!Enabled) return e;
        if (QuestionCount is < 1 or > 20) e.Add($"{label}: Die Anzahl der Prüfungsfragen muss zwischen 1 und 20 liegen.");
        if (PassPercent is < 1 or > 100) e.Add($"{label}: Die Bestehensgrenze muss zwischen 1 und 100 Prozent liegen.");
        if (MinPercentForNext is < 0 or > 100) e.Add($"{label}: Die Mindestleistung muss zwischen 0 und 100 Prozent liegen.");
        if (AllowedTypes().Count == 0) e.Add($"{label}: Mindestens ein Fragetyp muss erlaubt sein.");
        return e;
    }

    /// <summary>Konfiguration für die Quiz-Engine: nur gespeicherte Fragen (keine KI-Generierung, keine abgeleiteten Fragen), modulweit, serverseitig bewertet.</summary>
    public QuizConfig ToQuizConfig() => new()
    {
        Enabled = true, AllowMultipleChoice = SingleChoice || MultipleResponse, Origin = QuizConfig.OriginFixed, Strategy = QuizConfig.StrategyNone,
        SingleChoice = SingleChoice, MultipleResponse = MultipleResponse, FreeText = FreeText, QuestionCount = QuestionCount,
        ShuffleQuestions = ShuffleQuestions, ShuffleAnswers = ShuffleAnswers, Scope = QuizConfig.ScopeModule, PassPercent = PassPercent,
        AllowRetry = AllowRetry, ShowCorrectAnswers = ShowExplanations,
    };
}

/// <summary>Freischaltvoraussetzungen der Abschlussprüfung.</summary>
public class TopicUnlockRules
{
    /// <summary>Alle Module sind abgeschlossen (alle aktiven Karten als gelernt markiert).</summary>
    public bool RequireAllModulesCompleted { get; set; } = true;
    /// <summary>Alle aktiven Modulprüfungen sind bestanden.</summary>
    public bool RequireModuleExamsPassed { get; set; } = true;
}

/// <summary>Abschlussprüfung eines Themas: übergreifender Fragenpool aus den Modulen plus eigene themenbezogene Fragen.</summary>
public class TopicFinalExamConfig
{
    public const string DistributionEven = "even";
    public const string DistributionProportional = "proportional";
    public const string DistributionManual = "manual";

    public bool Enabled { get; set; }
    public int QuestionCount { get; set; } = 15;
    public int PassPercent { get; set; } = 60;
    public bool AllowRetry { get; set; } = true;
    /// <summary>Zufällige Auswahl und Reihenfolge der Fragen; sonst feste Reihenfolge (Modulreihenfolge, Erstellzeit).</summary>
    public bool RandomSelection { get; set; } = true;
    public bool ShuffleAnswers { get; set; }
    public bool SingleChoice { get; set; } = true;
    public bool MultipleResponse { get; set; } = true;
    public bool FreeText { get; set; } = true;
    /// <summary>even | proportional | manual — Verteilung der Fragen über die Module.</summary>
    public string Distribution { get; set; } = DistributionEven;
    /// <summary>Nur bei <c>manual</c>: Anzahl Fragen je Modul (Schlüssel = Modul-ID des Themas), Rest aus den eigenen Themenfragen.</summary>
    public Dictionary<string, int> ManualCounts { get; set; } = new();
    /// <summary>Eigene themenbezogene Fragen (Tabelle <c>topic_questions</c>) in den Pool aufnehmen.</summary>
    public bool IncludeTopicQuestions { get; set; } = true;
    public TopicUnlockRules Unlock { get; set; } = new();
    public bool ShowExplanations { get; set; } = true;

    public List<string> AllowedTypes()
    {
        var l = new List<string>();
        if (SingleChoice) l.Add(QuizTypes.SingleChoice);
        if (MultipleResponse) l.Add(QuizTypes.MultipleResponse);
        if (FreeText) l.Add(QuizTypes.FreeText);
        return l;
    }

    public List<string> Validate()
    {
        var e = new List<string>();
        if (!Enabled) return e;
        if (QuestionCount is < 1 or > 50) e.Add("Abschlussprüfung: Die Anzahl der Fragen muss zwischen 1 und 50 liegen.");
        if (PassPercent is < 1 or > 100) e.Add("Abschlussprüfung: Die Bestehensgrenze muss zwischen 1 und 100 Prozent liegen.");
        if (AllowedTypes().Count == 0) e.Add("Abschlussprüfung: Mindestens ein Fragetyp muss erlaubt sein.");
        if (Distribution is not (DistributionEven or DistributionProportional or DistributionManual)) e.Add("Abschlussprüfung: Die Verteilung muss even, proportional oder manual sein.");
        if (Distribution == DistributionManual && ManualCounts.Values.Any(v => v < 0 || v > 50)) e.Add("Abschlussprüfung: Die manuelle Verteilung enthält ungültige Anzahlen.");
        return e;
    }

    public QuizConfig ToQuizConfig() => new()
    {
        Enabled = true, AllowMultipleChoice = SingleChoice || MultipleResponse, Origin = QuizConfig.OriginFixed, Strategy = QuizConfig.StrategyNone,
        SingleChoice = SingleChoice, MultipleResponse = MultipleResponse, FreeText = FreeText, QuestionCount = Math.Clamp(QuestionCount, 1, 20),
        ShuffleQuestions = RandomSelection, ShuffleAnswers = ShuffleAnswers, Scope = QuizConfig.ScopeModule, PassPercent = PassPercent,
        AllowRetry = AllowRetry, ShowCorrectAnswers = ShowExplanations,
    };
}

/// <summary>Prüfungskonfiguration eines Themas (Entwurf: Schlüssel = ursprüngliche Modul-ID; Version: Schlüssel = ID der eingefrorenen Modulkopie).</summary>
public class TopicExamConfig
{
    public Dictionary<string, TopicModuleExamConfig> ModuleExams { get; set; } = new();
    public TopicFinalExamConfig FinalExam { get; set; } = new();

    public List<string> Validate(IReadOnlyDictionary<string, string>? moduleNames = null)
    {
        var errors = new List<string>();
        foreach (var (moduleId, cfg) in ModuleExams)
            errors.AddRange(cfg.Validate("Modulprüfung " + (moduleNames is not null && moduleNames.TryGetValue(moduleId, out var n) ? $"„{n}“" : moduleId)));
        errors.AddRange(FinalExam.Validate());
        return errors;
    }
}

// ─── Entitäten und Ansichten ────────────────────────────────────────────────

/// <summary>Eigene themenbezogene Frage der Abschlussprüfung (Entwurf; wird mit der Version in einen Prüfungsbereich eingefroren).</summary>
public class TopicQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TopicId { get; set; } = "";
    public string Type { get; set; } = QuizTypes.SingleChoice;
    public string Question { get; set; } = "";
    public List<string> Options { get; set; } = new();
    public List<int> CorrectIndexes { get; set; } = new();
    public string Explanation { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class TopicInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string OwnerUserId { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = TopicStatuses.Active;
    public int DraftRevision { get; set; }
    public int PublishedRevision { get; set; }
    /// <summary>Höchste veröffentlichte Versionsnummer (0 = noch nie veröffentlicht).</summary>
    public int LatestVersion { get; set; }
    /// <summary>Aktive globale Version (0 = nicht global veröffentlicht).</summary>
    public int GlobalVersion { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Der Entwurf weicht von der zuletzt veröffentlichten Version ab.</summary>
    public bool HasUnpublishedChanges => DraftRevision > PublishedRevision;
}

public class TopicVersionInfo
{
    public string Id { get; set; } = "";
    public string TopicId { get; set; } = "";
    public int VersionNumber { get; set; }
    public string Status { get; set; } = TopicVersionStatuses.Building;
    public string GlobalStatus { get; set; } = TopicGlobalStatuses.None;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? PublishedBy { get; set; }
    public string PublishedByName { get; set; } = "";
    public DateTime? SubmittedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
    public string DecisionReason { get; set; } = "";
    public TopicStats Stats { get; set; } = new();
    public string OwnerName { get; set; } = "";
    /// <summary>Nur intern (Snapshot-Struktur und eingefrorene Prüfungskonfiguration); wird nie ausgeliefert.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string SnapshotJson { get; set; } = "{}";
}

/// <summary>Zahlen eines Themas bzw. einer Version, automatisch aus dem tatsächlichen Inhalt und der Prüfungskonfiguration ermittelt.</summary>
public class TopicStats
{
    public int Modules { get; set; }
    /// <summary>Aktive Lernkarten aller Module.</summary>
    public int Cards { get; set; }
    /// <summary>Aktive Multiple-Choice-Fragen (single_choice + multiple_response) im Fragenpool der Module.</summary>
    public int ChoiceQuestions { get; set; }
    /// <summary>Alle aktiven Fragen im Pool der Module (alle Typen).</summary>
    public int PoolQuestions { get; set; }
    /// <summary>Eigene aktive Themenfragen der Abschlussprüfung.</summary>
    public int TopicQuestions { get; set; }
    /// <summary>Anzahl Fragen, die die Abschlussprüfung stellt (Konfiguration; 0 = Abschlussprüfung inaktiv).</summary>
    public int FinalExamQuestions { get; set; }
    /// <summary>Zulässige Fragen im Pool der Abschlussprüfung (Module + Themenfragen, erlaubte Typen).</summary>
    public int FinalExamPool { get; set; }
}

public class TopicModuleView
{
    /// <summary>Entwurf: Modul-ID des Originals; Version: ID der eingefrorenen Modulkopie (diese ID wird für Lernen und Prüfungen verwendet).</summary>
    public string ModuleId { get; set; } = "";
    public string? SourceModuleId { get; set; }
    public int Position { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "📚";
    public string Color { get; set; } = "#2AA79B";
    public int Cards { get; set; }
    public int ChoiceQuestions { get; set; }
    public int PoolQuestions { get; set; }
    public TopicModuleExamConfig Exam { get; set; } = new();
    /// <summary>Quelle ist nicht mehr zugreifbar (gelöscht/nicht mehr lesbar) — nur im Entwurf.</summary>
    public bool Unavailable { get; set; }
    /// <summary>Nur im Entwurf: Das Modul ist kein eigenes privates Modul (Altbestand, z. B. ein globales Modul) und muss vor der Veröffentlichung durch eine eigene Kopie ersetzt werden (ADR 0028).</summary>
    public bool RequiresOwnCopy { get; set; }
}

/// <summary>Detailansicht: entweder der Entwurf des Eigentümers oder eine veröffentlichte Version.</summary>
public class TopicDetail
{
    public TopicInfo Topic { get; set; } = new();
    /// <summary>0 = Entwurf, sonst Versionsnummer.</summary>
    public int Version { get; set; }
    public bool IsDraft => Version == 0;
    public bool IsOwner { get; set; }
    public string Section { get; set; } = TopicSections.Own;
    public List<TopicModuleView> Modules { get; set; } = new();
    public TopicFinalExamConfig FinalExam { get; set; } = new();
    public TopicStats Stats { get; set; } = new();
    public string GlobalStatus { get; set; } = TopicGlobalStatuses.None;
    public List<string> Warnings { get; set; } = new();
    /// <summary>Nur Entwurf des Eigentümers: Der Entwurf (Themenstruktur, Prüfungen oder Modulinhalt) weicht von der letzten veröffentlichten Version ab.</summary>
    public bool UnpublishedChanges { get; set; }
    public List<string> ChangedModuleNames { get; set; } = new();
    /// <summary>ID der Version (leer beim Entwurf).</summary>
    public string VersionId { get; set; } = "";
    /// <summary>ID der eingefrorenen Abschlussprüfungs-Modulkopie dieser Version (leer, wenn die Abschlussprüfung inaktiv ist).</summary>
    public string ExamModuleId { get; set; } = "";
}

public class TopicSummary
{
    public TopicInfo Topic { get; set; } = new();
    public string Section { get; set; } = TopicSections.Own;
    /// <summary>Versionsnummer, die der Betrachter lernt (0 = nur Entwurf).</summary>
    public int Version { get; set; }
    public string GlobalStatus { get; set; } = TopicGlobalStatuses.None;
    public TopicStats Stats { get; set; } = new();
    public TopicProgress? Progress { get; set; }
    public bool NewerVersionAvailable { get; set; }
    public int SharedWithCount { get; set; }
    public int AssignedCount { get; set; }
    /// <summary><see cref="TopicRelations"/> aus Sicht des Betrachters.</summary>
    public string Relation { get; set; } = TopicRelations.GlobalVoluntary;
    /// <summary>Nur bei ausdrücklicher, aktiver Zuweisung gesetzt; globale Veröffentlichung und Freigabe erzeugen nie eine Pflicht.</summary>
    public bool Mandatory { get; set; }
    public DateTime? AssignedAt { get; set; }
    public DateTime? DueAt { get; set; }
    public bool Overdue { get; set; }
    /// <summary>Zugewiesene Version (NULL im Altbestand).</summary>
    public int? AssignedVersion { get; set; }
}

public class TopicShareInfo
{
    public string Id { get; set; } = "";
    public string TopicId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Permission { get; set; } = "learn";
    public DateTime CreatedAt { get; set; }
}

public class TopicAssignmentInfo
{
    public string Id { get; set; } = "";
    public string TopicId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string AssignedBy { get; set; } = "";
    public DateTime AssignedAt { get; set; }
    public TopicProgress? Progress { get; set; }
    public string TopicName { get; set; } = "";
    /// <summary>Zugewiesene Themenversion (0 = Altbestand ohne feste Version).</summary>
    public int TopicVersion { get; set; }
    public bool Mandatory { get; set; } = true;
    public DateTime? DueAt { get; set; }
    public bool Overdue { get; set; }
    public string AssignedByName { get; set; } = "";
}

/// <summary>Zuweisbares Thema für Admin/Themen-Admin: globale Themen, eigene veröffentlichte Themen und ausdrücklich mit dem Akteur geteilte Themen.</summary>
public class AssignableTopic
{
    public string TopicId { get; set; } = "";
    public string Name { get; set; } = "";
    public string OwnerName { get; set; } = "";
    /// <summary>global | own | shared — warum das Thema zuweisbar ist.</summary>
    public string Via { get; set; } = "global";
    public List<AssignableVersion> Versions { get; set; } = new();
}

public class AssignableVersion
{
    public int Version { get; set; }
    public string GlobalStatus { get; set; } = TopicGlobalStatuses.None;
    public DateTime? PublishedAt { get; set; }
}

/// <summary>Zeile der Statusübersicht (Datenminimierung: nur Anzeigename, keine E-Mail, keine Antworten, keine Inhalte).</summary>
public class TopicStatusRow
{
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string TopicId { get; set; } = "";
    public string TopicName { get; set; } = "";
    public int Version { get; set; }
    /// <summary><see cref="TopicRelations"/></summary>
    public string Relation { get; set; } = TopicRelations.GlobalVoluntary;
    public bool Assigned { get; set; }
    public bool Mandatory { get; set; }
    public string Status { get; set; } = TopicLearnStatuses.NotStarted;
    public int ProgressPercent { get; set; }
    public int ModulesCompleted { get; set; }
    public int ModulesTotal { get; set; }
    public DateTime? AssignedAt { get; set; }
    public DateTime? DueAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool Overdue { get; set; }
    public string AssignmentId { get; set; } = "";
}

/// <summary>Verwendung eines eigenen Moduls in eigenen Themen (Entwurf und veröffentlichte Versionen).</summary>
public class ModuleTopicUsage
{
    public string TopicId { get; set; } = "";
    public string TopicName { get; set; } = "";
    public bool InDraft { get; set; }
    /// <summary>Veröffentlichte Versionen des Themas, in denen eine eingefrorene Kopie dieses Moduls enthalten ist.</summary>
    public List<int> Versions { get; set; } = new();
    public bool GlobalActive { get; set; }
}

/// <summary>Herkunft einer eingefrorenen Themenmodulkopie (für die Modulseite im Themenkontext).</summary>
public class TopicModuleOrigin
{
    public string TopicId { get; set; } = "";
    public string TopicName { get; set; } = "";
    public int Version { get; set; }
    public string GlobalStatus { get; set; } = TopicGlobalStatuses.None;
}

// ─── Lernfortschritt ────────────────────────────────────────────────────────

public class TopicModuleProgress
{
    public string ModuleId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Cards { get; set; }
    public int CardsChecked { get; set; }
    public bool Started { get; set; }
    public bool Completed { get; set; }
    public bool ExamEnabled { get; set; }
    public bool ExamPassed { get; set; }
    public double? ExamBestPercent { get; set; }
    public int ExamAttempts { get; set; }
    /// <summary>Der Lernschritt ist freigeschaltet (Mindestleistung der vorherigen Modulprüfung erfüllt).</summary>
    public bool Unlocked { get; set; } = true;
    public string UnlockHint { get; set; } = "";
}

public class TopicProgress
{
    public int Version { get; set; }
    public int ModulesTotal { get; set; }
    public int ModulesStarted { get; set; }
    public int ModulesCompleted { get; set; }
    public int ModuleExamsPassed { get; set; }
    public int ModuleExamsTotal { get; set; }
    public bool FinalExamEnabled { get; set; }
    public bool FinalExamAvailable { get; set; }
    public string FinalExamBlockedReason { get; set; } = "";
    public bool FinalExamPassed { get; set; }
    public double? FinalExamBestPercent { get; set; }
    public int FinalExamAttempts { get; set; }
    public DateTime? LastActivityAt { get; set; }
    /// <summary>Zuletzt genutzte Version (0 = noch nie geöffnet) — Grundlage für den Hinweis „neue Version verfügbar“.</summary>
    public int SeenVersion { get; set; }
    public bool NewerVersionAvailable { get; set; }
    public List<TopicModuleProgress> Modules { get; set; } = new();
    /// <summary>Lernstatus (ADR 0029): <see cref="TopicLearnStatuses"/> — pro Benutzer, Thema und Themenversion.</summary>
    public string Status { get; set; } = TopicLearnStatuses.NotStarted;
    /// <summary>Fortschritt in Prozent: abgeschlossene Module (und bestandene Abschlussprüfung, falls vorgesehen) im Verhältnis zum Gesamtumfang.</summary>
    public int ProgressPercent { get; set; }
    /// <summary>Zeitpunkt der letzten lernrelevanten Aktivität (Karte gelernt, Prüfung abgegeben), sobald das Thema abgeschlossen ist.</summary>
    public DateTime? CompletedAt { get; set; }
}

/// <summary>Lernstatus eines Themas für einen Benutzer. „Überfällig“ ist kein eigener Zustand, sondern ein Merkmal offener Pflichtzuweisungen mit Fälligkeitsdatum.</summary>
public static class TopicLearnStatuses
{
    public const string NotStarted = "not_started";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
}

/// <summary>Beziehung des Benutzers zum Thema (klar getrennt: global sichtbar, persönlich freigegeben, zugewiesen, verpflichtend).</summary>
public static class TopicRelations
{
    public const string Own = "own";
    public const string Shared = "shared";
    public const string AssignedVoluntary = "assigned_voluntary";
    public const string AssignedMandatory = "assigned_mandatory";
    public const string GlobalVoluntary = "global_voluntary";
}

/// <summary>Persistierter Prüfungsversuch (Verbindung von Benutzer, Themenversion, Prüfung und verifiziertem Quizergebnis).</summary>
public class TopicExamAttempt
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string TopicId { get; set; } = "";
    public string TopicVersionId { get; set; } = "";
    public int TopicVersion { get; set; }
    public string Kind { get; set; } = TopicExamKinds.Module;
    public string ModuleId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string? ResultId { get; set; }
    public double? Score { get; set; }
    public double? MaxScore { get; set; }
    public double? Percent { get; set; }
    public bool? Passed { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
