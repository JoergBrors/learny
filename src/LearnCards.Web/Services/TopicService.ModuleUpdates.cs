using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>Ein eigenes Modul, dessen neueste Version neuer ist als die im Themenentwurf festgelegte.</summary>
public sealed class ModuleUpdate
{
    public string ModuleId { get; set; } = "";
    public string TopicId { get; set; } = "";
    public string TopicName { get; set; } = "";
    public int PinnedVersion { get; set; }
    public int LatestVersion { get; set; }
}

public sealed class ModuleFieldChange
{
    public string Label { get; set; } = "";
    public string Old { get; set; } = "";
    public string New { get; set; } = "";
}

/// <summary>Änderung einer Karte zwischen zwei Modulversionen (<c>Kind</c>: added | removed | changed).</summary>
public sealed class ModuleCardChange
{
    public string Term { get; set; } = "";
    public string Kind { get; set; } = "";
    public List<ModuleFieldChange> Fields { get; set; } = new();
}

public partial class TopicService
{
    /// <summary>
    /// Neuere Modulversionen für die Modulliste: je Thema-Entwurf des Benutzers (aktive Themen) ein Eintrag, wenn die festgelegte Modulversion älter ist als die neueste. Reine Anzeige (ADR 0030);
    /// übernommen wird nur ausdrücklich über <see cref="AdoptModuleVersionAsync"/>.
    /// </summary>
    public async Task<List<ModuleUpdate>> ListModuleUpdatesAsync(string userId)
    {
        if (!UserService.IsGuid(userId)) return new();
        var rows = await _db.QueryAsync("""
            SELECT tm.module_id, tm.topic_id, t.name AS topic_name, tm.module_version AS pinned, MAX(mv.version_number) AS latest
            FROM topic_modules tm
            JOIN topics t ON t.id = tm.topic_id
            JOIN module_versions mv ON mv.module_id = tm.module_id
            WHERE t.owner_user_id = @u AND t.status = @s AND tm.module_version IS NOT NULL
            GROUP BY tm.module_id, tm.topic_id, t.name, tm.module_version
            HAVING MAX(mv.version_number) > tm.module_version
            ORDER BY t.name
            """, new (string, object?)[] { ("u", userId), ("s", TopicStatuses.Active) });
        return rows.Select(r => new ModuleUpdate
        {
            ModuleId = r["module_id"] ?? "", TopicId = r["topic_id"] ?? "", TopicName = r["topic_name"] ?? "",
            PinnedVersion = DbValue.ToInt(r["pinned"]), LatestVersion = DbValue.ToInt(r["latest"]),
        }).ToList();
    }

    /// <summary>
    /// Eigene Module mit <b>unveröffentlichten Änderungen</b> gegenüber ihrer neuesten Version (Entwurf ≠ Version). Eine günstige SQL-Vorauswahl (Karten/Modul zeitgleich mit oder nach der letzten Veröffentlichung geändert oder Kartenanzahl
    /// abweichend) wird mit dem Inhaltsfingerabdruck bestätigt (<see cref="ModuleVersionService.GetStateAsync"/>). Änderungen allein an Fragenpool/Quizkonfiguration/Kompetenzen können in der Vorauswahl fehlen
    /// (sie zeigt dann erst der Editor an). Nie veröffentlichte Module (Entwurf ohne Version) zählen nicht.
    /// </summary>
    public async Task<List<string>> ListModulesWithUnpublishedChangesAsync(string userId)
    {
        if (!UserService.IsGuid(userId)) return new();
        var rows = await _db.QueryAsync("""
            SELECT m.id FROM modules m
            WHERE m.owner_user_id = @u AND m.scope = 'private'
              AND EXISTS (SELECT 1 FROM module_versions v WHERE v.module_id = m.id)
              AND ( m.updated_at >= (SELECT MAX(v2.published_at) FROM module_versions v2 WHERE v2.module_id = m.id)
                 OR EXISTS (SELECT 1 FROM cards c WHERE c.module_id = m.id AND c.updated_at >= (SELECT MAX(v3.published_at) FROM module_versions v3 WHERE v3.module_id = m.id))
                 OR (SELECT COUNT(*) FROM cards c2 WHERE c2.module_id = m.id AND c2.archived = 0)
                    <> (SELECT COUNT(*) FROM cards c3 WHERE c3.archived = 0 AND c3.module_id =
                          (SELECT v4.snapshot_module_id FROM module_versions v4 WHERE v4.module_id = m.id ORDER BY v4.version_number DESC LIMIT 1)) )
            """, new[] { ("u", (object?)userId) });
        var result = new List<string>();
        foreach (var r in rows)
        {
            var module = await _repo.GetModuleAsync(r["id"] ?? "", userId);
            if (module is null) continue;
            var (_, changed) = await _moduleVersions.GetStateAsync(module);
            if (changed) result.Add(module.Id);
        }
        return result;
    }

    /// <summary>Kartenvergleich neueste Version ↔ aktueller Entwurf (nur lesen; Berechtigung wie bei der Versionsansicht).</summary>
    public async Task<List<ModuleCardChange>> CompareDraftChangesAsync(string actorId, string moduleId, int versionNumber)
    {
        var (_, oldCards) = await GetModuleVersionCardsAsync(actorId, moduleId, versionNumber);
        return DiffCards(oldCards, await _repo.ListActiveCardsRawAsync(moduleId));
    }

    /// <summary>Kartenvergleich zweier Modulversionen (nur lesen; Berechtigung wie bei der Versionsansicht, bei jedem Aufruf neu).</summary>
    public async Task<List<ModuleCardChange>> CompareModuleVersionsAsync(string actorId, string moduleId, int fromVersion, int toVersion)
    {
        var (_, oldCards) = await GetModuleVersionCardsAsync(actorId, moduleId, fromVersion);
        var (_, newCards) = await GetModuleVersionCardsAsync(actorId, moduleId, toVersion);
        return DiffCards(oldCards, newCards);
    }

    private static string Clip(string? s) { s = (s ?? "").Trim(); return s.Length > 400 ? s[..400] + "…" : s; }

    internal static List<ModuleCardChange> DiffCards(List<Card> oldCards, List<Card> newCards)
    {
        var oldBy = oldCards.GroupBy(c => c.Term, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var newBy = newCards.GroupBy(c => c.Term, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var result = new List<ModuleCardChange>();
        foreach (var (term, n) in newBy)
        {
            if (!oldBy.TryGetValue(term, out var o)) { result.Add(new ModuleCardChange { Term = term, Kind = "added" }); continue; }
            var fields = new List<ModuleFieldChange>();
            void F(string label, string? a, string? b) { if ((a ?? "").Trim() != (b ?? "").Trim()) fields.Add(new ModuleFieldChange { Label = label, Old = Clip(a), New = Clip(b) }); }
            F("Kategorie", o.Category, n.Category); F("Frage", o.Question, n.Question); F("Antwort / Definition", o.Definition, n.Definition); F("So funktioniert es", o.HowItWorks, n.HowItWorks);
            F("Kontext", o.Context, n.Context); F("Kernaussage", o.KeyFact, n.KeyFact); F("Musterantwort", o.ReferenceAnswer, n.ReferenceAnswer);
            F("Bilder", o.Images.Count.ToString(), n.Images.Count.ToString()); F("Quizfragen", o.Quiz.Count.ToString(), n.Quiz.Count.ToString());
            if (fields.Count > 0) result.Add(new ModuleCardChange { Term = term, Kind = "changed", Fields = fields });
        }
        foreach (var term in oldBy.Keys.Where(t => !newBy.ContainsKey(t))) result.Add(new ModuleCardChange { Term = term, Kind = "removed" });
        return result.OrderBy(c => c.Kind == "added" ? 0 : c.Kind == "changed" ? 1 : 2).ThenBy(c => c.Term, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
