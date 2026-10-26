using System.Text.RegularExpressions;

namespace LearnCards.Web.Domain;

/// <summary>Lernziel eines Moduls (ADR 0031). Kompetenzen verweisen darauf.</summary>
public class LearningObjective
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>
/// Kompetenz eines Moduls (ADR 0031): Modul → Lernziel → Kompetenz → Lernkarte (<c>competencies[]</c> der Karte) → Quizfrage (über die Karte) → Bewertungskriterium → Prüfungsergebnis.
/// Bewertungsmethode ist das serverseitig bewertete Modulquiz. Eine Kompetenz gilt nur als nachgewiesen, wenn mindestens <see cref="MinQuestions"/> ihrer Fragen geprüft wurden
/// und der erreichte Anteil mindestens <see cref="PassPercent"/> beträgt.
/// </summary>
public class Competency
{
    public string Id { get; set; } = "";
    public string? ObjectiveId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Bestehensgrenze der Kompetenz in Prozent der auf sie entfallenden Punkte.</summary>
    public int PassPercent { get; set; } = 60;
    /// <summary>Erforderliche Prüfungsabdeckung: Mindestanzahl geprüfter Fragen dieser Kompetenz in einem Prüfungsversuch.</summary>
    public int MinQuestions { get; set; } = 1;
}

/// <summary>Lernziele und Kompetenzen eines Moduls (Teil der Modulversion).</summary>
public class ModuleCompetencyModel
{
    public List<LearningObjective> Objectives { get; set; } = new();
    public List<Competency> Competencies { get; set; } = new();

    public bool IsEmpty => Objectives.Count == 0 && Competencies.Count == 0;

    /// <summary>Prüft Struktur und Limits; liefert Fehlermeldungen (leer = gültig).</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        void Err(string m) { if (errors.Count < 10) errors.Add(m); }
        if (Objectives.Count > CompetencyRules.MaxObjectives) Err($"Höchstens {CompetencyRules.MaxObjectives} Lernziele je Modul.");
        if (Competencies.Count > CompetencyRules.MaxCompetencies) Err($"Höchstens {CompetencyRules.MaxCompetencies} Kompetenzen je Modul.");
        var oIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in Objectives)
        {
            if (!CompetencyRules.IsValidId(o.Id)) Err($"Lernziel-ID '{o.Id}' ist ungültig (Kleinbuchstaben, Ziffern, . _ -; höchstens 64 Zeichen).");
            else if (!oIds.Add(o.Id)) Err($"Lernziel-ID '{o.Id}' ist doppelt.");
            if (string.IsNullOrWhiteSpace(o.Text) || o.Text.Length > CompetencyRules.MaxTextLength) Err($"Lernziel '{o.Id}': Text erforderlich (höchstens {CompetencyRules.MaxTextLength} Zeichen).");
        }
        var cIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Competencies)
        {
            if (!CompetencyRules.IsValidId(c.Id)) Err($"Kompetenz-ID '{c.Id}' ist ungültig (Kleinbuchstaben, Ziffern, . _ -; höchstens 64 Zeichen).");
            else if (!cIds.Add(c.Id)) Err($"Kompetenz-ID '{c.Id}' ist doppelt.");
            if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > CompetencyRules.MaxNameLength) Err($"Kompetenz '{c.Id}': Bezeichnung erforderlich (höchstens {CompetencyRules.MaxNameLength} Zeichen).");
            if ((c.Description ?? "").Length > CompetencyRules.MaxTextLength) Err($"Kompetenz '{c.Id}': Beschreibung zu lang (höchstens {CompetencyRules.MaxTextLength} Zeichen).");
            if (c.PassPercent is < 1 or > 100) Err($"Kompetenz '{c.Id}': Bestehensgrenze muss 1–100 sein.");
            if (c.MinQuestions is < 1 or > 50) Err($"Kompetenz '{c.Id}': Mindestanzahl Fragen muss 1–50 sein.");
            if (c.ObjectiveId is { Length: > 0 } oid && !oIds.Contains(oid)) Err($"Kompetenz '{c.Id}': Lernziel '{oid}' ist nicht definiert.");
        }
        return errors;
    }
}

public static class CompetencyRules
{
    public const int MaxObjectives = 50, MaxCompetencies = 100, MaxNameLength = 120, MaxTextLength = 500, MaxCardCompetencies = 10;
    private static readonly Regex IdRx = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static bool IsValidId(string? id) => id is not null && IdRx.IsMatch(id);
}

public static class CompetencyStatus
{
    public const string Demonstrated = "demonstrated";
    public const string NotDemonstrated = "not_demonstrated";
    /// <summary>Zu wenige Fragen dieser Kompetenz im Prüfungsversuch — weder bestanden noch nicht bestanden.</summary>
    public const string InsufficientlyTested = "insufficiently_tested";
}

/// <summary>Nachvollziehbarer Nachweis einer Kompetenz aus einem verifizierten Prüfungsergebnis.</summary>
public class CompetencyEvidence
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? ObjectiveId { get; set; }
    public string Objective { get; set; } = "";
    public int PassPercent { get; set; }
    public int MinQuestions { get; set; }
    /// <summary>Tatsächlich geprüfte Fragen dieser Kompetenz.</summary>
    public int QuestionsAsked { get; set; }
    public double Earned { get; set; }
    public double Max { get; set; }
    public double Percent { get; set; }
    public string Status { get; set; } = CompetencyStatus.InsufficientlyTested;
    public string Method { get; set; } = "module_quiz";
}

/// <summary>
/// Unveränderlicher Nachweissnapshot eines verifizierten Modulabschlusses (ADR 0031): Modul-/Prüfungsversion, Lernziele, geprüfte Kompetenzen, Bewertungskriterien,
/// Ergebnisse, Abschlusszeitpunkt, Identität und Hash. Enthält weder Lernendenangaben noch Antworten oder Fragetexte.
/// </summary>
public class ExamSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ResultId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public int ModuleVersion { get; set; }
    /// <summary><c>module</c> oder <c>topic</c> (Themenabschluss; <c>ResultId</c> = Zertifikats-ID, <c>ModuleId</c> = Themen-ID).</summary>
    public string Kind { get; set; } = "module";
    public string Json { get; set; } = "{}";
    /// <summary>SHA-256 (Hex) des kanonischen JSON.</summary>
    public string Hash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
