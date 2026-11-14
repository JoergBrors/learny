using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>
/// Modulbezogene Co-Author-Berechtigungen (ADR 0053). <b>Einzige</b> Entscheidungsstelle für Schreibrechte an fremden Modulen ist <see cref="GetModuleEditAccessAsync"/>.
/// Zwei unabhängige Quellen: (a) direkte Modulfreigabe (<c>module_coauthors</c>), (b) abgeleitet aus einem Themen-Co-Author (ADR 0052; nichts gespeichert, bei jedem Aufruf aus
/// aktuellen Themenberechtigungen und Modulzuordnungen berechnet). Berechtigt wird nie das Lernen (ADR 0015), Veröffentlichen, Freigeben oder Zuweisen.
/// </summary>
public partial class TopicService
{
    public const int MaxCoAuthorsPerModule = 50;

    // ─── Zentrale Berechtigungsentscheidung ─────────────────────────────────

    public async Task<ModuleEditAccess> GetModuleEditAccessAsync(string actorId, string moduleId)
    {
        var none = new ModuleEditAccess();
        if (!UserService.IsGuid(actorId) || !UserService.IsGuid(moduleId)) return none;
        var mod = (await _db.QueryAsync("SELECT owner_user_id, scope FROM modules WHERE id = @m", new[] { ("m", (object?)moduleId) })).FirstOrDefault();
        var owner = mod?.GetValueOrDefault("owner_user_id");
        if (mod is null || string.IsNullOrEmpty(owner) || mod.GetValueOrDefault("scope") != "private") return none;   // globale/eingefrorene Kopien sind nie bearbeitbar
        if (string.Equals(owner, actorId, StringComparison.OrdinalIgnoreCase)) return new ModuleEditAccess { Kind = ModuleAccessKind.Owner, OwnerUserId = owner };
        if (await _repo.IsRestrictedLearnerAsync(actorId)) return none;   // ADR 0049: nie

        var direct = (await _db.QueryAsync("""
            SELECT 1 FROM module_coauthors c JOIN users u ON u.id = c.user_id WHERE c.module_id = @m AND c.user_id = @u AND u.status = 'active'
            """, new (string, object?)[] { ("m", moduleId), ("u", actorId) })).Count > 0;

        // abgeleitet: bestehende Schutzregel aus ADR 0052 — liegt das Modul auch in einem Thema ohne Bearbeitungsrecht des Handelnden, gibt es kein abgeleitetes Recht
        var via = new List<ModuleTopicRef>();
        var allEditable = true;
        var topics = await _db.QueryAsync("SELECT t.id FROM topic_modules tm JOIN topics t ON t.id = tm.topic_id WHERE tm.module_id = @m", new[] { ("m", (object?)moduleId) });
        foreach (var row in topics)
        {
            var topic = await GetTopicRawAsync(row["id"] ?? "");
            if (topic is null) continue;
            var role = await RoleForAsync(actorId, topic);
            if (role == TopicRole.None) { allEditable = false; break; }
            if (role == TopicRole.CoAuthor && string.Equals(topic.OwnerUserId, owner, StringComparison.OrdinalIgnoreCase) && topic.Status == TopicStatuses.Active)
                via.Add(new ModuleTopicRef { TopicId = topic.Id, TopicName = topic.Name });
        }
        if (!allEditable) via.Clear();
        if (!direct && via.Count == 0) return none;
        return new ModuleEditAccess { Kind = direct ? ModuleAccessKind.Direct : ModuleAccessKind.ViaTopic, OwnerUserId = owner, IsDirect = direct, ViaTopics = via };
    }

    public async Task<bool> CanEditModuleAsync(string actorId, string moduleId) => (await GetModuleEditAccessAsync(actorId, moduleId)).CanEdit;

    private async Task<string> RequireModuleManagerAsync(string actorId, string moduleId)
    {
        var access = await GetModuleEditAccessAsync(actorId, moduleId);
        if (access.Kind is not (ModuleAccessKind.Owner or ModuleAccessKind.Direct)) throw new KeyNotFoundException("Modul nicht gefunden.");   // abgeleitetes Recht verwaltet keine direkten Co-Autoren
        return access.OwnerUserId;
    }

    // ─── Anzeige und Verwaltung der direkten Modul-Co-Autoren ────────────────

    public async Task<ModuleCollaboration> GetModuleCollaborationAsync(string actorId, string moduleId)
    {
        var access = await GetModuleEditAccessAsync(actorId, moduleId);
        if (!access.CanEdit) throw new KeyNotFoundException("Modul nicht gefunden.");
        var name = (await _db.QueryAsync("SELECT name FROM modules WHERE id = @m", new[] { ("m", (object?)moduleId) }))[0]["name"] ?? "";
        var owner = await _users.GetAsync(access.OwnerUserId);
        var result = new ModuleCollaboration
        {
            ModuleId = moduleId, ModuleName = name, OwnerUserId = access.OwnerUserId, OwnerName = owner?.DisplayName ?? "", OwnerEmail = owner?.Email ?? "",
            ViewerKind = access.Kind, CanManage = access.Kind is ModuleAccessKind.Owner or ModuleAccessKind.Direct, ViewerViaTopics = access.ViaTopics,
        };
        if (!result.CanManage) return result;   // nur abgeleitetes Recht: sieht die eigene Herkunft, keine fremden Berechtigungen
        var rows = await _db.QueryAsync("""
            SELECT c.user_id, c.created_at, u.display_name, u.email, g.display_name AS granted_name
            FROM module_coauthors c JOIN users u ON u.id = c.user_id LEFT JOIN users g ON g.id = c.granted_by
            WHERE c.module_id = @m ORDER BY u.display_name
            """, new[] { ("m", (object?)moduleId) });
        result.DirectCoAuthors = rows.Select(r => new TopicCoAuthorInfo
        {
            UserId = r["user_id"] ?? "", DisplayName = r.GetValueOrDefault("display_name") ?? "", Email = r.GetValueOrDefault("email") ?? "",
            GrantedByName = r.GetValueOrDefault("granted_name") ?? "", CreatedAt = DbValue.ToDateTime(r.GetValueOrDefault("created_at")),
        }).ToList();
        // abgeleitete Zugriffe: der Eigentümer sieht alle Themen mit dem Modul, ein direkter Co-Author nur Themen, an denen er selbst beteiligt ist
        var derived = await _db.QueryAsync("""
            SELECT t.id AS topic_id, c.user_id, u.display_name
            FROM topic_modules tm JOIN topics t ON t.id = tm.topic_id
            JOIN topic_coauthors c ON c.topic_id = t.id JOIN users u ON u.id = c.user_id
            WHERE tm.module_id = @m AND t.status = 'active' AND t.owner_user_id = @o AND u.status = 'active'
            ORDER BY t.name, u.display_name
            """, new (string, object?)[] { ("m", moduleId), ("o", access.OwnerUserId) });
        foreach (var d in derived)
        {
            var topic = await GetTopicRawAsync(d["topic_id"] ?? "");
            if (topic is null) continue;
            if (access.Kind != ModuleAccessKind.Owner && await RoleForAsync(actorId, topic) == TopicRole.None) continue;
            result.DerivedAccess.Add(new ModuleDerivedAccess { TopicId = topic.Id, TopicName = topic.Name, UserId = d["user_id"] ?? "", DisplayName = d.GetValueOrDefault("display_name") ?? "" });
        }
        return result;
    }

    public async Task<List<UserSearchResult>> SearchModuleCoAuthorCandidatesAsync(string actorId, string moduleId, string? query)
    {
        var ownerId = await RequireModuleManagerAsync(actorId, moduleId);
        var existing = (await _db.QueryAsync("SELECT user_id FROM module_coauthors WHERE module_id = @m", new[] { ("m", (object?)moduleId) })).Select(r => r["user_id"] ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<UserSearchResult>();
        foreach (var u in await _users.SearchUsersAsync(actorId, query))
        {
            if (existing.Contains(u.Id) || string.Equals(u.Id, ownerId, StringComparison.OrdinalIgnoreCase)) continue;
            if (await _repo.IsRestrictedLearnerAsync(u.Id)) continue;
            found.Add(u);
        }
        return found;
    }

    /// <summary>Fügt einen direkten Modul-Co-Author hinzu (Eigentümer oder direkter Co-Author). Idempotent; kein Lernrecht, keine Freigabe, keine Rolle.</summary>
    public async Task<TopicCoAuthorInfo> AddModuleCoAuthorAsync(string actorId, string moduleId, string targetUserId)
    {
        var ownerId = await RequireModuleManagerAsync(actorId, moduleId);
        if (!UserService.IsGuid(targetUserId)) throw new KeyNotFoundException("Benutzer nicht gefunden.");
        if (string.Equals(targetUserId, actorId, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Du kannst dir selbst keine Berechtigung erteilen.");
        if (string.Equals(targetUserId, ownerId, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Der Eigentümer des Moduls ist bereits berechtigt.");
        var target = await _users.GetAsync(targetUserId);
        if (target is null || !target.IsActive) throw new KeyNotFoundException("Benutzer nicht gefunden.");
        if (await _repo.IsRestrictedLearnerAsync(target.Id)) throw new ArgumentException("Eingeschränkte Lernprofile (externe Lernende) können keine Co-Autoren sein.");

        var key = new (string, object?)[] { ("m", moduleId), ("u", target.Id) };
        if ((await _db.QueryAsync("SELECT 1 FROM module_coauthors WHERE module_id = @m AND user_id = @u", key)).Count == 0)
        {
            var count = DbValue.ToInt((await _db.QueryAsync("SELECT COUNT(*) AS n FROM module_coauthors WHERE module_id = @m", new[] { ("m", (object?)moduleId) }))[0]["n"]);
            if (count >= MaxCoAuthorsPerModule) throw new InvalidOperationException($"Ein Modul kann höchstens {MaxCoAuthorsPerModule} direkte Co-Autoren haben.");
            try
            {
                await _db.ExecuteAsync("INSERT INTO module_coauthors (module_id, user_id, granted_by, created_at) VALUES (@m, @u, @g, @n)",
                    new (string, object?)[] { ("m", moduleId), ("u", target.Id), ("g", actorId), ("n", DateTime.UtcNow) });
            }
            catch (Exception)
            {
                if ((await _db.QueryAsync("SELECT 1 FROM module_coauthors WHERE module_id = @m AND user_id = @u", key)).Count == 0) throw;   // paralleles Hinzufügen: Eintrag existiert bereits
            }
            await _users.WriteAuditAsync(actorId, "module.coauthor.add", moduleId, $"user={target.Id}");
        }
        return (await GetModuleCollaborationAsync(actorId, moduleId)).DirectCoAuthors.First(c => string.Equals(c.UserId, target.Id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Entfernt einen direkten Modul-Co-Author sofort. Aus Themen abgeleitete Rechte bleiben davon unberührt. Der Eigentümer ist nicht entfernbar.</summary>
    public async Task<bool> RemoveModuleCoAuthorAsync(string actorId, string moduleId, string targetUserId)
    {
        var ownerId = await RequireModuleManagerAsync(actorId, moduleId);
        if (!UserService.IsGuid(targetUserId)) return false;
        if (string.Equals(targetUserId, ownerId, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Der Eigentümer des Moduls kann nicht entfernt werden.");
        var n = await _db.ExecuteAsync("DELETE FROM module_coauthors WHERE module_id = @m AND user_id = @u", new (string, object?)[] { ("m", moduleId), ("u", targetUserId) });
        if (n > 0) await _users.WriteAuditAsync(actorId, "module.coauthor.remove", moduleId, $"user={targetUserId}");
        return n > 0;
    }

    /// <summary>Beendet die eigene direkte Mitarbeit (nur direkte Co-Autoren; ein abgeleitetes Recht endet mit der Themenberechtigung).</summary>
    public async Task<bool> LeaveModuleCoAuthorAsync(string actorId, string moduleId)
    {
        var access = await GetModuleEditAccessAsync(actorId, moduleId);
        if (!access.IsDirect) throw new KeyNotFoundException("Modul nicht gefunden.");
        var n = await _db.ExecuteAsync("DELETE FROM module_coauthors WHERE module_id = @m AND user_id = @u", new (string, object?)[] { ("m", moduleId), ("u", actorId) });
        if (n > 0) await _users.WriteAuditAsync(actorId, "module.coauthor.leave", moduleId, "");
        return n > 0;
    }

    // ─── Übersicht „Gemeinsam bearbeitet“ (Module) ─────────────────────────────

    /// <summary>Module mit aktuell gültigem Schreibzugriff des Benutzers (direkt oder über ein gemeinsam bearbeitetes Thema) — ohne die übrige Bibliothek der Eigentümer.</summary>
    public async Task<List<CoEditedModule>> ListCoEditedModulesAsync(string actorId)
    {
        var result = new List<CoEditedModule>();
        if (!UserService.IsGuid(actorId) || await _repo.IsRestrictedLearnerAsync(actorId)) return result;
        var direct = await _db.QueryAsync("SELECT c.module_id AS id FROM module_coauthors c JOIN modules m ON m.id = c.module_id WHERE c.user_id = @u AND m.scope = 'private'", new[] { ("u", (object?)actorId) });
        var viaTopics = await _db.QueryAsync("SELECT tm.module_id AS id FROM topic_coauthors tc JOIN topic_modules tm ON tm.topic_id = tc.topic_id WHERE tc.user_id = @u", new[] { ("u", (object?)actorId) });
        var ids = direct.Concat(viaTopics).Select(r => r["id"] ?? "").Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var access = await GetModuleEditAccessAsync(actorId, id);
            if (access.Kind is not (ModuleAccessKind.Direct or ModuleAccessKind.ViaTopic)) continue;
            var module = await _repo.GetModuleAsync(id, access.OwnerUserId);
            if (module is null) continue;
            result.Add(new CoEditedModule { Module = module, OwnerName = (await _users.GetAsync(access.OwnerUserId))?.DisplayName ?? "", Direct = access.IsDirect, ViaTopics = access.ViaTopics });
        }
        return result.OrderBy(c => c.Module.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
