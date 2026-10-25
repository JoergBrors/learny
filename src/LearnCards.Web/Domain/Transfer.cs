namespace LearnCards.Web.Domain;

// Portable JSON-Definitionen für Module und Themen (ADR 0028). Sie sind eigenständig und versioniert; das kanonische Karten-JSON (ADR 0006)
// bleibt unverändert und wird innerhalb von "cards" wiederverwendet.

public static class TransferFormats
{
    public const string Module = "learny-module";
    public const string Topic = "learny-topic";
    public const int SchemaVersion = 1;
}

/// <summary>Zusätzliche Pool-Frage eines Moduls (Editor-/KI-Fragen). Fragen aus <c>quiz[]</c> der Karten gehören nicht hierher — ihre Quelle ist die Karte.</summary>
public class ModulePoolQuestionDefinition
{
    /// <summary>Position der Karte in <c>cards</c> (0-basiert); fehlt = modulweite Frage.</summary>
    public int? CardIndex { get; set; }
    public string Type { get; set; } = QuizTypes.SingleChoice;
    public string Question { get; set; } = "";
    public List<string> Options { get; set; } = new();
    public List<int> CorrectIndexes { get; set; } = new();
    public string Explanation { get; set; } = "";
    /// <summary>manual | ai</summary>
    public string Origin { get; set; } = QuizOrigins.Manual;
    public bool Active { get; set; } = true;
}

/// <summary>Vollständige, portable Moduldefinition (<c>learny-module</c>, Schema 1).</summary>
public class ModuleDefinition
{
    public string Format { get; set; } = TransferFormats.Module;
    public int SchemaVersion { get; set; } = TransferFormats.SchemaVersion;
    /// <summary>Stabile Referenz (GUID) des Moduls — überlebt Umbenennen und wird beim Export vergeben. Optional im Import; <c>name</c> ist nur der Anzeigename.</summary>
    public string? ReferenceId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "📚";
    public string Color { get; set; } = "#2AA79B";
    public List<CardJson> Cards { get; set; } = new();
    public QuizConfig? QuizConfig { get; set; }
    public List<ModulePoolQuestionDefinition> PoolQuestions { get; set; } = new();
}

/// <summary>Modulzuordnung in einer Themendefinition: Auflösung über den Namen eines eigenen Moduls, optional mit eingebetteter Moduldefinition.</summary>
public class TopicDefinitionModule
{
    /// <summary>Stabile Referenz (GUID) des eigenen Moduls (<c>reference_id</c>); hat Vorrang vor dem Namen. Fehlt sie, wird über <c>module_name</c> aufgelöst.</summary>
    public string? ModuleRef { get; set; }
    /// <summary>Anzeigename des Moduls (bei fehlender Referenz zugleich Auflösungsschlüssel).</summary>
    public string ModuleName { get; set; } = "";
    public int? SortOrder { get; set; }
    public TopicModuleExamConfig? Exam { get; set; }
    /// <summary>Manuelle Verteilung der Abschlussprüfung: Fragenzahl aus diesem Modul (nur bei <c>distribution = manual</c>).</summary>
    public int? FinalExamQuestions { get; set; }
    /// <summary>Optional: vollständige Moduldefinition; wird beim Import als neues eigenes Modul angelegt (bei Namenskonflikt unter einem freien Namen).</summary>
    public ModuleDefinition? Module { get; set; }
}

public class TopicDefinitionExamConfig
{
    /// <summary>Abschlussprüfung; <c>manual_counts</c> wird nicht verwendet (stattdessen <c>final_exam_questions</c> je Modul).</summary>
    public TopicFinalExamConfig FinalExam { get; set; } = new();
}

public class TopicDefinitionQuestion
{
    public string Type { get; set; } = QuizTypes.SingleChoice;
    public string Question { get; set; } = "";
    public List<string> Options { get; set; } = new();
    public List<int> CorrectIndexes { get; set; } = new();
    public string Explanation { get; set; } = "";
    public bool Active { get; set; } = true;
}

/// <summary>Portable Themendefinition (<c>learny-topic</c>, Schema 1). Enthält nie IDs, Besitzer, Versionen, Freigaben, Zuweisungen oder Ergebnisse.</summary>
public class TopicDefinition
{
    public string Format { get; set; } = TransferFormats.Topic;
    public int SchemaVersion { get; set; } = TransferFormats.SchemaVersion;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<TopicDefinitionModule> Modules { get; set; } = new();
    public TopicDefinitionExamConfig ExamConfig { get; set; } = new();
    public List<TopicDefinitionQuestion> TopicQuestions { get; set; } = new();
}
