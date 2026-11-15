using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>Unterschied zwischen einer veröffentlichten Modulversion und dem aktuellen Entwurf (nur Anzeige, nichts wird gespeichert).</summary>
public sealed class ModuleVersionDiff
{
    public List<string> Added { get; set; } = new();
    public List<string> Removed { get; set; } = new();
    public List<string> Changed { get; set; } = new();
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
}

/// <summary>
/// Modulversionen für Co-Autoren (ADR 0059). Berechtigung ist ausschließlich <see cref="GetModuleEditAccessAsync"/> (Eigentümer, direkt, abgeleitet inkl. Mehrfachzuordnungs-Schutz), bei <b>jedem</b>
/// Aufruf neu gegen die Datenbank. Veröffentlichen erzeugt nur einen unveränderlichen Modulsnapshot — keine Lernfreigabe, keine globale Freigabe, keine Themenversion, keine Übernahme in Themen.
/// Es gibt keine REST-/MCP-Schnittstelle dafür; die REST-Pfade unter <c>/modules/{id}/versions</c> bleiben eigentümergebunden.
/// </summary>
public partial class TopicService
{
    public const string ModuleVersionRevokedMessage = "Dein Co-Author-Zugriff auf dieses Modul wurde entzogen. Es wurde keine Version veröffentlicht.";

    private async Task<ModuleEditAccess> RequireModuleVersionAccessAsync(string actorId, string moduleId)
    {
        var access = await GetModuleEditAccessAsync(actorId, moduleId);
        if (!access.CanEdit) throw new KeyNotFoundException("Modul nicht gefunden.");
        return access;
    }

    public async Task<ModuleVersionOverview> GetModuleVersionOverviewAsync(string actorId, string moduleId)
    {
        var access = await RequireModuleVersionAccessAsync(actorId, moduleId);
        return await _moduleVersions.GetOverviewAsync(access.OwnerUserId, moduleId);
    }

    /// <summary>Veröffentlicht den Entwurf als neue Version; <paramref name="expectedDraftHash"/> = Stand, den der Handelnde gesehen hat. Handelnder wird in <c>published_by</c> und Audit festgehalten.</summary>
    public async Task<ModuleVersionInfo> PublishModuleVersionAsync(string actorId, string moduleId, string? note, string? expectedDraftHash)
    {
        var access = await GetModuleEditAccessAsync(actorId, moduleId);   // unmittelbar vor der Veröffentlichung, kein Zwischenspeicher
        if (!access.CanEdit) throw new KeyNotFoundException(ModuleVersionRevokedMessage);
        return await _moduleVersions.PublishAsync(access.OwnerUserId, moduleId, note, actorId, expectedDraftHash);
    }

    /// <summary>
    /// Leichte Änderungsmarke eines Moduls für die automatische Aktualisierung des Editors (Anzahl/jüngste Änderung der Karten inkl. Archiv, neueste Modulversion, Kompetenzmodell).
    /// Berechtigung wie überall <see cref="GetModuleEditAccessAsync"/> bei jedem Aufruf; <c>null</c> = kein (oder entzogener) Zugriff. Enthält keine Inhalte. Änderungen am Fragenpool/an der Quizkonfiguration allein ändern sie nicht.
    /// </summary>
    public async Task<string?> GetModuleChangeStampAsync(string actorId, string moduleId)
    {
        if (!(await GetModuleEditAccessAsync(actorId, moduleId)).CanEdit) return null;
        var p = new[] { ("m", (object?)moduleId) };
        var c = (await _db.QueryAsync("SELECT COUNT(*) AS n, MAX(updated_at) AS u FROM cards WHERE module_id = @m", p))[0];
        var v = (await _db.QueryAsync("SELECT COALESCE(MAX(version_number), 0) AS n FROM module_versions WHERE module_id = @m", p))[0];
        var k = (await _db.QueryAsync("SELECT updated_at FROM module_competencies WHERE module_id = @m", p)).FirstOrDefault();
        return string.Join("|", c["n"], c["u"], v["n"], k?["updated_at"]);
    }

    /// <summary>Karten einer veröffentlichten Version (nur lesen).</summary>
    public async Task<(ModuleVersionInfo Version, List<Card> Cards)> GetModuleVersionCardsAsync(string actorId, string moduleId, int versionNumber)
    {
        await RequireModuleVersionAccessAsync(actorId, moduleId);
        var v = await _moduleVersions.GetVersionRawAsync(moduleId, versionNumber) ?? throw new KeyNotFoundException("Version nicht gefunden.");
        var cards = (await _repo.ListActiveCardsRawAsync(v.SnapshotModuleId)).OrderBy(c => c.SortOrder).ToList();
        return (v, cards);
    }

    /// <summary>Vergleich Entwurf ↔ Version nach Kartenbegriff und -inhalt (Anzeige; keine zweite Versionsspeicherung).</summary>
    public async Task<ModuleVersionDiff> CompareDraftWithVersionAsync(string actorId, string moduleId, int versionNumber)
    {
        var (_, old) = await GetModuleVersionCardsAsync(actorId, moduleId, versionNumber);
        var draft = await _repo.ListActiveCardsRawAsync(moduleId);
        static string Sig(Card c) => string.Join("\u001f", c.Category, c.Question, c.Definition, c.HowItWorks, c.Context, c.KeyFact, c.ReferenceAnswer, c.Images.Count);
        var oldBy = old.GroupBy(c => c.Term, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var newBy = draft.GroupBy(c => c.Term, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var d = new ModuleVersionDiff();
        foreach (var (t, c) in newBy) { if (!oldBy.TryGetValue(t, out var o)) d.Added.Add(t); else if (Sig(o) != Sig(c)) d.Changed.Add(t); }
        foreach (var t in oldBy.Keys) if (!newBy.ContainsKey(t)) d.Removed.Add(t);
        return d;
    }
}
