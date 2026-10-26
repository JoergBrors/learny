using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>Veröffentlichter, unveränderlicher Stand eines eigenen Moduls (ADR 0030).</summary>
public sealed class ModuleVersionInfo
{
    public string Id { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public int VersionNumber { get; set; }
    /// <summary>ID der eingefrorenen Modulkopie (<c>scope = 'topic'</c>) mit dem Inhalt dieser Version.</summary>
    public string SnapshotModuleId { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime PublishedAt { get; set; }
    public int Cards { get; set; }
}

public sealed class ModuleVersionOverview
{
    public string ModuleId { get; set; } = "";
    public string ReferenceId { get; set; } = "";
    /// <summary>Neueste Version (0 = noch nie veröffentlicht).</summary>
    public int LatestVersion { get; set; }
    /// <summary>Der Entwurf (aktueller Bearbeitungsstand) weicht inhaltlich von der neuesten Version ab oder das Modul wurde nie veröffentlicht.</summary>
    public bool HasUnpublishedChanges { get; set; }
    public List<ModuleVersionInfo> Versions { get; set; } = new();
}

/// <summary>
/// Modulversionen (ADR 0030): Ein eigenes privates Modul besitzt einen bearbeitbaren Entwurf (das Modul selbst) und fortlaufend nummerierte, unveränderliche Versionen.
/// Eine Version friert Metadaten, Karten, Bilder, Reihenfolge, feste Quizfragen, Quizkonfiguration und Fragenpool in einer Modulkopie ein — über denselben Mechanismus
/// wie Themenversionen (<see cref="CardRepository.CreateTopicSnapshotModuleAsync"/>). Themen binden konkrete Modulversionen; neuere Stände wirken nie stillschweigend.
/// </summary>
public class ModuleVersionService
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    private readonly IDatabase _db;
    private readonly CardRepository _repo;
    private readonly UserService _users;

    public ModuleVersionService(IDatabase db, CardRepository repo, UserService users)
    {
        _db = db;
        _repo = repo;
        _users = users;
    }

    private static ModuleVersionInfo Map(Dictionary<string, string?> r) => new()
    {
        Id = r["id"] ?? "", ModuleId = r["module_id"] ?? "", VersionNumber = DbValue.ToInt(r.GetValueOrDefault("version_number")), SnapshotModuleId = r["snapshot_module_id"] ?? "",
        ContentHash = r.GetValueOrDefault("content_hash") ?? "", Note = r.GetValueOrDefault("note") ?? "", PublishedAt = DbValue.ToDateTime(r.GetValueOrDefault("published_at")),
    };

    private async Task<ModuleInfo> RequireOwnAsync(string ownerId, string moduleId)
    {
        var m = UserService.IsGuid(moduleId) ? await _repo.GetModuleAsync(moduleId, ownerId) : null;
        if (m is null || m.Scope != "private") throw new KeyNotFoundException("Modul nicht gefunden.");
        return m;
    }

    private async Task<List<ModuleVersionInfo>> ListRawAsync(string moduleId) =>
        (await _db.QueryAsync("SELECT * FROM module_versions WHERE module_id = @m ORDER BY version_number DESC", new[] { ("m", (object?)moduleId) })).Select(Map).ToList();

    /// <summary>Versionen eines eigenen Moduls (neueste zuerst) und ob der Entwurf davon abweicht.</summary>
    public async Task<ModuleVersionOverview> GetOverviewAsync(string ownerId, string moduleId)
    {
        var module = await RequireOwnAsync(ownerId, moduleId);
        var versions = await ListRawAsync(module.Id);
        foreach (var v in versions) v.Cards = DbValue.ToInt((await _db.QueryAsync("SELECT COUNT(*) AS n FROM cards WHERE module_id = @m AND archived = 0", new[] { ("m", (object?)v.SnapshotModuleId) }))[0]["n"]);
        var hash = await _repo.ComputeModuleContentHashAsync(module);
        return new ModuleVersionOverview
        {
            ModuleId = module.Id, ReferenceId = await _repo.EnsureReferenceIdAsync(module.Id, ownerId), Versions = versions,
            LatestVersion = versions.FirstOrDefault()?.VersionNumber ?? 0,
            HasUnpublishedChanges = versions.Count == 0 || !string.Equals(versions[0].ContentHash, hash, StringComparison.Ordinal),
        };
    }

    /// <summary>Kurzinformation für Listen: neueste Versionsnummer und Änderungsindikator für ein eigenes Modul.</summary>
    public async Task<(int Latest, bool Changed)> GetStateAsync(ModuleInfo ownModule)
    {
        var latest = (await ListRawAsync(ownModule.Id)).FirstOrDefault();
        if (latest is null) return (0, true);
        return (latest.VersionNumber, !string.Equals(latest.ContentHash, await _repo.ComputeModuleContentHashAsync(ownModule), StringComparison.Ordinal));
    }

    public async Task<ModuleVersionInfo?> GetVersionAsync(string ownerId, string moduleId, int versionNumber)
    {
        var module = await RequireOwnAsync(ownerId, moduleId);
        return (await ListRawAsync(module.Id)).FirstOrDefault(v => v.VersionNumber == versionNumber);
    }

    /// <summary>
    /// Veröffentlicht den aktuellen Stand als neue, unveränderliche Version. Ohne inhaltliche Änderung gegenüber der neuesten Version: <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task<ModuleVersionInfo> PublishAsync(string ownerId, string moduleId, string? note = null)
    {
        var module = await RequireOwnAsync(ownerId, moduleId);
        var gate = Gates.GetOrAdd(module.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var created = await CreateIfChangedAsync(ownerId, module, note);
            if (created is null) throw new InvalidOperationException($"Keine Änderungen seit Version {(await ListRawAsync(module.Id))[0].VersionNumber}. Ändere das Modul, bevor du eine neue Version veröffentlichst.");
            return created;
        }
        finally { gate.Release(); }
    }

    /// <summary>Stellt sicher, dass der aktuelle Stand als Version vorliegt (legt bei Bedarf eine an) und liefert die neueste Version. Für das Festlegen im Themenentwurf.</summary>
    public async Task<ModuleVersionInfo> EnsureCurrentVersionAsync(string ownerId, string moduleId, string? note = null)
    {
        var module = await RequireOwnAsync(ownerId, moduleId);
        var gate = Gates.GetOrAdd(module.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { return await CreateIfChangedAsync(ownerId, module, note) ?? (await ListRawAsync(module.Id))[0]; }
        finally { gate.Release(); }
    }

    private async Task<ModuleVersionInfo?> CreateIfChangedAsync(string ownerId, ModuleInfo module, string? note)
    {
        var versions = await ListRawAsync(module.Id);
        var hash = await _repo.ComputeModuleContentHashAsync(module);
        if (versions.Count > 0 && string.Equals(versions[0].ContentHash, hash, StringComparison.Ordinal)) return null;
        var next = (versions.FirstOrDefault()?.VersionNumber ?? 0) + 1;
        var snapshot = await _repo.CreateTopicSnapshotModuleAsync(module, next);
        var trimmed = (note ?? "").Trim();
        var v = new ModuleVersionInfo
        {
            Id = Guid.NewGuid().ToString(), ModuleId = module.Id, VersionNumber = next, SnapshotModuleId = snapshot.Id, ContentHash = hash,
            Note = trimmed.Length > 300 ? trimmed[..300] : trimmed, PublishedAt = DateTime.UtcNow,
        };
        await _db.ExecuteAsync("""
            INSERT INTO module_versions (id, module_id, version_number, snapshot_module_id, content_hash, note, published_at, published_by)
            VALUES (@id, @m, @n, @s, @h, @note, @at, @u)
            """, new (string, object?)[] { ("id", v.Id), ("m", v.ModuleId), ("n", v.VersionNumber), ("s", v.SnapshotModuleId), ("h", v.ContentHash), ("note", v.Note), ("at", v.PublishedAt), ("u", ownerId) });
        await _users.WriteAuditAsync(ownerId, "module.version.publish", module.Id, $"version={next}");
        return v;
    }

    /// <summary>Neueste Versionsnummer je eigenem Modul (eine Abfrage; Module ohne Version fehlen).</summary>
    public async Task<Dictionary<string, int>> ListLatestVersionNumbersAsync(string ownerId)
    {
        var rows = await _db.QueryAsync("""
            SELECT mv.module_id, MAX(mv.version_number) AS n FROM module_versions mv
            JOIN modules m ON m.id = mv.module_id WHERE m.owner_user_id = @o GROUP BY mv.module_id
            """, new[] { ("o", (object?)ownerId) });
        return rows.ToDictionary(r => r["module_id"] ?? "", r => DbValue.ToInt(r["n"]), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Version über die ID der eingefrorenen Kopie.</summary>
    public async Task<ModuleVersionInfo?> FindBySnapshotAsync(string snapshotModuleId)
    {
        var rows = await _db.QueryAsync("SELECT * FROM module_versions WHERE snapshot_module_id = @s", new[] { ("s", (object?)snapshotModuleId) });
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    /// <summary>Interne Auflösung ohne Besitzprüfung (nur nach Berechtigungsprüfung des Aufrufers).</summary>
    internal async Task<ModuleVersionInfo?> GetVersionRawAsync(string moduleId, int versionNumber) =>
        (await ListRawAsync(moduleId)).FirstOrDefault(v => v.VersionNumber == versionNumber);

    internal async Task<ModuleVersionInfo?> GetLatestRawAsync(string moduleId) => (await ListRawAsync(moduleId)).FirstOrDefault();
}
