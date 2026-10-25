using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>Ergebnis der Zugriffsprüfung auf ein Modul aus Sicht eines Benutzers (ADR 0015).</summary>
public sealed record ModuleAccess(ModuleInfo Module, string Context, bool CanEdit);

/// <summary>
/// Lesezugriffe auf freigegebene und globale Module (ADR 0015). Die Eigentümerfilter der Änderungsmethoden in
/// <c>CardRepository</c> bleiben unverändert (ADR 0011); hier wird ausschließlich <b>gelesen</b>, und jeder Pfad läuft über
/// <see cref="ResolveModuleAccessAsync"/> — den einzigen Ort, an dem entschieden wird, was ein Benutzer lernen darf.
/// </summary>
public partial class CardRepository
{
    public const string ContextPrivate = "private";
    public const string ContextShared = "shared";
    public const string ContextGlobal = "global";

    // ─── Zugriffsentscheid ──────────────────────────────────────────────────

    /// <summary>
    /// Entscheidet, ob und wie der Benutzer das Modul nutzen darf:
    /// <list type="bullet">
    /// <item><b>private</b>: Eigentümer (Lesen und Ändern)</item>
    /// <item><b>shared</b>: aktive, nicht widerrufene Freigabe an diesen Benutzer (nur Lernen)</item>
    /// <item><b>global</b>: aktive globale Veröffentlichung (nur Lernen; Verwaltung nur über die Administration)</item>
    /// </list>
    /// Alles andere (fremd, unveröffentlicht, eingereicht, ersetzt, deaktiviert) verhält sich wie „nicht vorhanden“ (null).
    /// </summary>
    public async Task<ModuleAccess?> ResolveModuleAccessAsync(string userId, string moduleId)
    {
        var module = await GetModuleRawAsync(moduleId);
        if (module is null) return null;

        if (module.Scope == "private")
        {
            if (string.Equals(module.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase))
            {
                module.Context = ContextPrivate;
                return new ModuleAccess(module, ContextPrivate, CanEdit: true);
            }
            var shared = await _db.QueryAsync("""
                SELECT 1 FROM module_shares WHERE module_id = @m AND recipient_user_id = @u AND revoked_at IS NULL
                """, new (string, object?)[] { ("m", module.Id), ("u", userId) });
            if (shared.Count == 0) return null;
            module.Context = ContextShared;
            module.OwnerName = await DisplayNameAsync(module.OwnerUserId);
            return new ModuleAccess(module, ContextShared, CanEdit: false);
        }

        if (module.Scope == "global" && module.GlobalStatus == "active")
        {
            module.Context = ContextGlobal;
            module.OwnerName = module.SourceOwnerName ?? "";
            return new ModuleAccess(module, ContextGlobal, CanEdit: false);
        }
        return null;
    }

    /// <summary>Modul ohne Berechtigungsprüfung — nur für interne Zwecke (Zugriffsentscheid, Administration/Audit). Nie direkt an Benutzer ausliefern.</summary>
    public async Task<ModuleInfo?> GetModuleRawAsync(string id)
    {
        if (!UserService.IsGuid(id)) return null;
        var rows = await _db.QueryAsync($"{ModuleSelect} WHERE m.id = @id {ModuleGroupBy}", new[] { ("id", (object?)id) });
        return rows.Count == 0 ? null : MapModule(rows[0]);
    }

    // ─── Übersichten ────────────────────────────────────────────────────────

    /// <summary>Module, die anderen Benutzern gehören und mir aktiv freigegeben wurden.</summary>
    public async Task<List<ModuleInfo>> ListSharedWithMeAsync(string userId)
    {
        var rows = await _db.QueryAsync($"""
            {ModuleSelect}
            JOIN module_shares s ON s.module_id = m.id AND s.recipient_user_id = @u AND s.revoked_at IS NULL
            WHERE m.scope = 'private'
            {ModuleGroupBy}
            ORDER BY m.name
            """, new[] { ("u", (object?)userId) });
        var modules = rows.Select(MapModule).ToList();
        foreach (var m in modules)
        {
            m.Context = ContextShared;
            m.OwnerName = await DisplayNameAsync(m.OwnerUserId);
        }
        return modules;
    }

    /// <summary>Aktive globale Module (jeweils die aktuelle Version einer Veröffentlichung).</summary>
    public async Task<List<ModuleInfo>> ListGlobalModulesAsync()
    {
        var rows = await _db.QueryAsync($"{ModuleSelect} WHERE m.scope = 'global' AND m.global_status = 'active' {ModuleGroupBy} ORDER BY m.name");
        var modules = rows.Select(MapModule).ToList();
        foreach (var m in modules)
        {
            m.Context = ContextGlobal;
            m.OwnerName = m.SourceOwnerName ?? "";
        }
        return modules;
    }

    /// <summary>Alle globalen Module aller Versionen und Status — nur für Administration/Audit.</summary>
    public async Task<List<ModuleInfo>> ListAllGlobalModulesAsync()
    {
        var rows = await _db.QueryAsync($"{ModuleSelect} WHERE m.scope = 'global' {ModuleGroupBy} ORDER BY m.name, m.global_version DESC");
        return rows.Select(MapModule).ToList();
    }

    // ─── Lesen von Karten (nur nach Zugriffsentscheid) ──────────────────────

    /// <summary>Aktive (nicht archivierte) Karten eines lesbaren Moduls; leer, wenn kein Zugriff besteht.</summary>
    public async Task<List<Card>> ListReadableCardsAsync(string userId, string moduleId, string? category = null, bool slideOnly = false)
    {
        if (await ResolveModuleAccessAsync(userId, moduleId) is null) return new();
        var sql = "SELECT * FROM cards WHERE module_id = @m AND archived = 0";
        var args = new List<(string, object?)> { ("m", moduleId) };
        if (!string.IsNullOrWhiteSpace(category)) { sql += " AND category = @c"; args.Add(("c", category)); }
        if (slideOnly) sql += " AND slide_number IS NOT NULL";
        sql += slideOnly ? " ORDER BY slide_number, sort_order, term" : " ORDER BY category, sort_order, term";
        var cards = (await _db.QueryAsync(sql, args)).Select(MapCard).ToList();
        await _images.AttachAsync(cards);
        return cards;
    }

    public async Task<List<string>> ListReadableCategoriesAsync(string userId, string moduleId)
    {
        if (await ResolveModuleAccessAsync(userId, moduleId) is null) return new();
        var rows = await _db.QueryAsync("SELECT DISTINCT category FROM cards WHERE module_id = @m AND archived = 0 ORDER BY category",
            new[] { ("m", (object?)moduleId) });
        return rows.Select(r => r["category"] ?? "").Where(c => c.Length > 0).ToList();
    }

    /// <summary>Karte, sofern ihr Modul für den Benutzer lesbar ist. Archivierte Karten sieht nur der Eigentümer.</summary>
    public async Task<Card?> GetReadableCardAsync(string cardId, string userId)
    {
        if (!UserService.IsGuid(cardId)) return null;
        var rows = await _db.QueryAsync("SELECT * FROM cards WHERE id = @id", new[] { ("id", (object?)cardId) });
        if (rows.Count == 0) return null;
        var card = MapCard(rows[0]);
        var access = await ResolveModuleAccessAsync(userId, card.ModuleId);
        if (access is null) return null;
        if (card.Archived && !access.CanEdit) return null;
        await _images.AttachAsync(new[] { card });
        return card;
    }

    /// <summary>
    /// Bildmetadaten, sofern die Karte des Bildes für den Benutzer lesbar ist (eigenes, freigegebenes oder globales Modul; archivierte Karten nur für den Eigentümer).
    /// Einziger Berechtigungspunkt für <c>GET /api/images/{id}</c>: unabhängig vom Bildspeicher und vom Cache.
    /// </summary>
    public async Task<CardImage?> GetReadableImageAsync(string imageId, string userId)
    {
        var meta = await _images.GetMetaAsync(imageId);
        if (meta is null) return null;
        var rows = await _db.QueryAsync("SELECT module_id, archived FROM cards WHERE id = @id", new[] { ("id", (object?)meta.CardId) });
        if (rows.Count == 0) return null;
        var access = await ResolveModuleAccessAsync(userId, rows[0]["module_id"] ?? "");
        if (access is null) return null;
        if (DbValue.ToBool(rows[0].GetValueOrDefault("archived")) && !access.CanEdit) return null;
        return meta;
    }

    /// <summary>Alle Karten eines Moduls ohne Berechtigungsprüfung (nur Administration: Vorschau eingefrorener Einreichungen), inkl. Bildmetadaten.</summary>
    /// <summary>Karte ohne Berechtigungsprüfung — nur für interne Wartungsläufe (Start-Synchronisation), nie an Benutzer ausliefern.</summary>
    public async Task<Card?> GetCardRawAsync(string id)
    {
        if (!UserService.IsGuid(id)) return null;
        var rows = await _db.QueryAsync("SELECT * FROM cards WHERE id = @id", new[] { ("id", (object?)id) });
        return rows.Count == 0 ? null : MapCard(rows[0]);
    }

    /// <summary>Überführt <c>quiz[]</c> bestehender Karten einmalig in den Fragenpool (ADR 0019); idempotent, <c>quiz_json</c> bleibt erhalten.</summary>
    public Task<int> SyncLegacyQuizAsync() => _quizPool.SyncLegacyAsync(GetCardRawAsync);

    public async Task<List<Card>> ListAllCardsOfModuleRawAsync(string moduleId)
    {
        if (!UserService.IsGuid(moduleId)) return new();
        var cards = (await _db.QueryAsync("SELECT * FROM cards WHERE module_id = @m ORDER BY category, sort_order, term", new[] { ("m", (object?)moduleId) }))
            .Select(MapCard).ToList();
        await _images.AttachAsync(cards);
        return cards;
    }

    // ─── Löschschutz (Schutz vorhandener Lernergebnisse) ────────────────────

    /// <summary>Anzahl Lernergebnisse anderer Benutzer auf diesem Modul (würden beim Löschen per Fremdschlüssel mitgelöscht).</summary>
    private async Task<int> ForeignResultCountAsync(string column, string id, string ownerId)
    {
        var rows = await _db.QueryAsync($"SELECT COUNT(*) AS n FROM quiz_results WHERE {column} = @id AND user_sub <> @u",
            new (string, object?)[] { ("id", id), ("u", ownerId) });
        return DbValue.ToInt(rows.FirstOrDefault()?.GetValueOrDefault("n"));
    }

    // ─── Globale Kopien (Einreichung) ───────────────────────────────────────

    /// <summary>
    /// Legt eine eingefrorene Kopie des Moduls samt aktiver Karten (inkl. statischer Quizze) als „staged“ globale Version an.
    /// Das private Original bleibt unverändert; die Kopie hat eigene IDs und keinen Eigentümer.
    /// </summary>
    public async Task<(ModuleInfo Module, int CardCount)> CreateStagedGlobalCopyAsync(
        ModuleInfo source, string publicationId, int version, string sourceOwnerName)
    {
        var sourceCards = await ListCardsAsync(source.OwnerUserId!, source.Id, null, archived: false);
        var copy = new ModuleInfo
        {
            Name = source.Name, Description = source.Description, Icon = source.Icon, Color = source.Color,
            OwnerUserId = null, Scope = "global", PublicationId = publicationId, GlobalVersion = version, GlobalStatus = "staged",
            SourceModuleId = source.Id, SourceOwnerUserId = source.OwnerUserId, SourceOwnerName = sourceOwnerName,
        };
        await _db.ExecuteAsync("""
            INSERT INTO modules (id, name, description, icon, color, owner_user_id, scope, publication_id, global_version, global_status,
                                 source_module_id, source_owner_user_id, source_owner_name, created_at, updated_at)
            VALUES (@id, @name, @description, @icon, @color, NULL, 'global', @pub, @ver, 'staged', @src, @src_owner, @src_owner_name, @created_at, @updated_at)
            """, new (string, object?)[]
        {
            ("id", copy.Id), ("name", copy.Name), ("description", copy.Description), ("icon", copy.Icon), ("color", copy.Color),
            ("pub", publicationId), ("ver", version), ("src", source.Id), ("src_owner", source.OwnerUserId), ("src_owner_name", sourceOwnerName),
            ("created_at", copy.CreatedAt), ("updated_at", copy.UpdatedAt),
        });

        var cardIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in sourceCards)
        {
            var copyCard = new Card
            {
                ModuleId = copy.Id, OwnerUserId = null, Category = c.Category, Term = c.Term, Question = c.Question, Definition = c.Definition,
                HowItWorks = c.HowItWorks, Context = c.Context, KeyFact = c.KeyFact, ReferenceAnswer = c.ReferenceAnswer, ChatPrompt = c.ChatPrompt,
                OfficialSources = c.OfficialSources, SlideNumber = c.SlideNumber, TargetTimeSec = c.TargetTimeSec, Quiz = c.Quiz,
                Archived = false, SortOrder = c.SortOrder,
            };
            await InsertCardAsync(copyCard);
            await _images.CopyForCardAsync(c.Id, copyCard.Id);   // gleiche Binärdaten, neue Bild-IDs (Referenzzählung schützt vor vorzeitigem Löschen)
            cardIdMap[c.Id] = copyCard.Id;
        }
        // Quizkonfiguration und aktive Fragen (feste und gespeicherte KI-Fragen) werden mit der Version eingefroren (ADR 0019)
        await _quizPool.CopyForStagedModuleAsync(source.Id, copy.Id, cardIdMap, version);
        await _db.ExecuteAsync("UPDATE cards SET quiz_synced = 1 WHERE module_id = @m", new[] { ("m", (object?)copy.Id) });
        return (copy, sourceCards.Count);
    }

    /// <summary>Löscht eine noch nicht veröffentlichte („staged“) Kopie (bei Ablehnung). Veröffentlichte Versionen werden nie gelöscht.</summary>
    /// <summary>Löscht ein (nicht eingefrorenes) globales Modul samt Karten, Ergebnissen und nicht mehr referenzierten Bildern.</summary>
    public async Task<int> DeleteGlobalModuleAsync(string moduleId)
    {
        var imageRefs = await _images.CollectForModuleAsync(moduleId);
        var deleted = await _db.ExecuteAsync("DELETE FROM modules WHERE id = @id AND scope = 'global' AND global_status <> 'staged'",
            new[] { ("id", (object?)moduleId) });
        if (deleted > 0) await _images.CleanupOrphansAsync(imageRefs);
        return deleted;
    }

    public Task<int> DeleteStagedModuleAsync(string moduleId) =>
        _db.ExecuteAsync("DELETE FROM modules WHERE id = @id AND scope = 'global' AND global_status = 'staged'",
            new[] { ("id", (object?)moduleId) });

    // ─── Hilfen ─────────────────────────────────────────────────────────────

    public static Card MapCardPublic(Dictionary<string, string?> r) => MapCard(r);

    public async Task<string> DisplayNameAsync(string? userId)
    {
        if (!UserService.IsGuid(userId)) return "";
        var rows = await _db.QueryAsync("SELECT display_name FROM users WHERE id = @id", new[] { ("id", (object?)userId) });
        return rows.FirstOrDefault()?.GetValueOrDefault("display_name") ?? "";
    }
}
