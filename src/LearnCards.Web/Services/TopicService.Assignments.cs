using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>
/// Administrative Themenzuweisungen, Lernstatus und Modulverwendung (ADR 0029).
/// Zuweisen und Widerrufen dürfen nur Admin und Themen-Admin; den Lernstatus lesen zusätzlich Auditoren. Alle Prüfungen laufen serverseitig über die Rollen aus der Datenbank
/// (<see cref="UserService.RequireAnyRoleAsync"/>) — die Oberfläche blendet nur aus. Zugewiesen werden können nur Themen, die durch ihre Veröffentlichung oder ausdrückliche Freigabe
/// zuweisbar sind; ein Themen-Admin erhält dadurch keinen allgemeinen Zugriff auf fremde private Themen oder Module.
/// </summary>
public partial class TopicService
{
    private async Task<AppUser> RequireAssignerAsync(string? actorId) => await _users.RequireAnyRoleAsync(actorId, Roles.Admin, Roles.TopicAdmin);

    /// <summary>Warum ein Thema für den Akteur zuweisbar ist: global (aktive globale Version), eigenes veröffentlichtes Thema oder ausdrücklich mit dem Akteur geteilt. Sonst null.</summary>
    private async Task<(string Via, List<TopicVersionInfo> Versions)?> AssignableAsync(string actorId, TopicInfo topic)
    {
        if (topic.Status == TopicStatuses.Archived) return null;
        var published = (await _db.QueryAsync(VersionSelect + " WHERE v.topic_id = @t AND v.status = 'published' ORDER BY v.version_number DESC", new[] { ("t", (object?)topic.Id) })).Select(MapVersion).ToList();
        if (published.Count == 0) return null;
        var global = published.Where(v => v.GlobalStatus == TopicGlobalStatuses.Active).ToList();
        var own = string.Equals(topic.OwnerUserId, actorId, StringComparison.OrdinalIgnoreCase);
        var shared = !own && (await _db.QueryAsync("SELECT 1 FROM topic_shares WHERE topic_id = @t AND target_user_id = @u AND revoked_at IS NULL", new (string, object?)[] { ("t", topic.Id), ("u", actorId) })).Count > 0;
        if (own) return ("own", published);
        if (shared) return ("shared", published);
        if (global.Count > 0) return ("global", global);   // nur die aktive globale Version, nie zurückgezogene oder private Versionen
        return null;
    }

    /// <summary>Benutzersuche für die Zuweisung (nur Admin/Themen-Admin).</summary>
    public async Task<List<UserSearchResult>> SearchUsersForAssignmentAsync(string? actorId, string? query)
    {
        var actor = await RequireAssignerAsync(actorId);
        return await _users.SearchUsersAsync(actor.Id, query);
    }

    /// <summary>Zuweisbare Themen mit ihren zuweisbaren Versionen (nur Admin/Themen-Admin).</summary>
    public async Task<List<AssignableTopic>> ListAssignableTopicsAsync(string? actorId)
    {
        var actor = await RequireAssignerAsync(actorId);
        var result = new List<AssignableTopic>();
        var rows = await _db.QueryAsync(TopicSelect + " " + """
             WHERE t.status = 'active' AND (t.owner_user_id = @u
                OR EXISTS (SELECT 1 FROM topic_shares s WHERE s.topic_id = t.id AND s.target_user_id = @u AND s.revoked_at IS NULL)
                OR EXISTS (SELECT 1 FROM topic_versions gv WHERE gv.topic_id = t.id AND gv.status = 'published' AND gv.global_status = 'active'))
             ORDER BY t.name
             """, new[] { ("u", (object?)actor.Id) });
        foreach (var r in rows)
        {
            var topic = MapTopic(r);
            if (await AssignableAsync(actor.Id, topic) is not { } a) continue;
            result.Add(new AssignableTopic
            {
                TopicId = topic.Id, Name = topic.Name, OwnerName = topic.OwnerName, Via = a.Via,
                Versions = a.Versions.Select(v => new AssignableVersion { Version = v.VersionNumber, GlobalStatus = v.GlobalStatus, PublishedAt = v.PublishedAt }).ToList(),
            });
        }
        return result;
    }

    /// <summary>
    /// Weist ein zuweisbares Thema einem aktiven Benutzer in einer <b>konkreten Version</b> zu (verpflichtend oder freiwillig, optional mit Fälligkeitsdatum).
    /// Wiederholtes Zuweisen aktualisiert die vorhandene Zuweisung; bisherige Ergebnisse bleiben erhalten. Eine Zuweisung ändert weder Themenentwurf noch Eigentum.
    /// </summary>
    public async Task<TopicAssignmentInfo> AssignAsync(string? actorId, string topicId, string targetUserId, int? version = null, bool mandatory = true, DateTime? dueAt = null)
    {
        var actor = await RequireAssignerAsync(actorId);
        var topic = await GetTopicRawAsync(topicId);
        var assignable = topic is null ? null : await AssignableAsync(actor.Id, topic);
        if (topic is null || assignable is null) throw new KeyNotFoundException("Thema nicht gefunden oder nicht zuweisbar.");   // nie Hinweise auf fremde private Themen
        if (!UserService.IsGuid(targetUserId)) throw new KeyNotFoundException("Benutzer nicht gefunden.");
        var target = await _users.GetAsync(targetUserId);
        if (target is null || !target.IsActive) throw new KeyNotFoundException("Benutzer nicht gefunden.");
        var chosen = version is int v && v > 0 ? assignable.Value.Versions.FirstOrDefault(x => x.VersionNumber == v) : assignable.Value.Versions.First();
        if (chosen is null) throw new ArgumentException("Diese Themenversion ist nicht zuweisbar (nur veröffentlichte Versionen; bei globalen Themen nur die aktive globale Version).");
        if (dueAt is DateTime due && (due <= DateTime.UtcNow || due > DateTime.UtcNow.AddYears(5))) throw new ArgumentException("Das Fälligkeitsdatum muss in der Zukunft liegen (höchstens 5 Jahre).");

        var now = DateTime.UtcNow;
        var args = new (string, object?)[] { ("v", chosen.VersionNumber), ("m", mandatory ? 1 : 0), ("d", dueAt), ("a", actor.Id), ("n", now), ("t", topic.Id), ("u", target.Id) };
        var n = await _db.ExecuteAsync("UPDATE topic_assignments SET status = 'active', revoked_at = NULL, topic_version = @v, mandatory = @m, due_at = @d, assigned_by = @a, assigned_at = @n WHERE topic_id = @t AND user_id = @u", args);
        if (n == 0)
        {
            try
            {
                await _db.ExecuteAsync("INSERT INTO topic_assignments (id, topic_id, user_id, assigned_by, assigned_at, status, topic_version, mandatory, due_at) VALUES (@id, @t, @u, @a, @n, 'active', @v, @m, @d)",
                    new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("t", topic.Id), ("u", target.Id), ("a", actor.Id), ("n", now), ("v", chosen.VersionNumber), ("m", mandatory ? 1 : 0), ("d", dueAt) });
            }
            catch (Exception) { await _db.ExecuteAsync("UPDATE topic_assignments SET status = 'active', revoked_at = NULL, topic_version = @v, mandatory = @m, due_at = @d, assigned_by = @a, assigned_at = @n WHERE topic_id = @t AND user_id = @u", args); }
        }
        await _users.WriteAuditAsync(actor.Id, "topic.assign", topic.Id, $"user={target.Id} version={chosen.VersionNumber} mandatory={(mandatory ? 1 : 0)}{(dueAt is null ? "" : " due=" + dueAt.Value.ToString("yyyy-MM-dd"))}");
        return (await ListAssignmentsAsync(actor.Id, topic.Id)).First(a => string.Equals(a.UserId, target.Id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Widerruft eine Zuweisung: Das daraus abgeleitete Lernrecht endet (außer bei Eigentum, persönlicher Freigabe oder aktiver globaler Version); Ergebnisse und Fortschritt bleiben erhalten.</summary>
    public async Task<bool> RevokeAssignmentAsync(string? actorId, string assignmentId)
    {
        var actor = await RequireAssignerAsync(actorId);
        if (!UserService.IsGuid(assignmentId)) return false;
        var row = (await _db.QueryAsync("SELECT topic_id, user_id FROM topic_assignments WHERE id = @id AND status = 'active'", new[] { ("id", (object?)assignmentId) })).FirstOrDefault();
        if (row is null) return false;
        var n = await _db.ExecuteAsync("UPDATE topic_assignments SET status = 'revoked', revoked_at = @n WHERE id = @id AND status = 'active'", new (string, object?)[] { ("n", DateTime.UtcNow), ("id", assignmentId) });
        if (n > 0) await _users.WriteAuditAsync(actor.Id, "topic.unassign", row["topic_id"], $"user={row["user_id"]}");
        return n > 0;
    }

    /// <summary>Aktive Zuweisungen mit Lernstatus. Sichtbar sind nur Zuweisungen zuweisbarer Themen bzw. eigene Zuweisungen des Akteurs (keine fremden privaten Themen).</summary>
    public async Task<List<TopicAssignmentInfo>> ListAssignmentsAsync(string? actorId, string? topicId = null)
    {
        var actor = await RequireAssignerAsync(actorId);
        var sql = """
            SELECT a.id, a.topic_id, a.user_id, a.assigned_by, a.assigned_at, a.topic_version, a.mandatory, a.due_at, u.display_name, b.display_name AS by_name
            FROM topic_assignments a JOIN users u ON u.id = a.user_id LEFT JOIN users b ON CAST(b.id AS TEXT) = a.assigned_by
            WHERE a.status = 'active'
            """;
        var args = new List<(string, object?)>();
        if (topicId is not null) { sql += " AND a.topic_id = @t"; args.Add(("t", topicId)); }
        var list = new List<TopicAssignmentInfo>();
        foreach (var r in await _db.QueryAsync(sql + " ORDER BY a.assigned_at DESC", args))
        {
            var topic = await GetTopicRawAsync(r["topic_id"] ?? "");
            if (topic is null) continue;
            var mine = string.Equals(r.GetValueOrDefault("assigned_by"), actor.Id, StringComparison.OrdinalIgnoreCase);
            if (!mine && await AssignableAsync(actor.Id, topic) is null && !(await HasActiveGlobalAsync(topic.Id))) continue;
            list.Add(await MapAssignmentAsync(r, topic));
        }
        return list;
    }

    private async Task<bool> HasActiveGlobalAsync(string topicId) =>
        (await _db.QueryAsync("SELECT 1 FROM topic_versions WHERE topic_id = @t AND status = 'published' AND global_status = 'active'", new[] { ("t", (object?)topicId) })).Count > 0;

    private async Task<TopicAssignmentInfo> MapAssignmentAsync(Dictionary<string, string?> r, TopicInfo topic)
    {
        var uid = r["user_id"] ?? "";
        var pinned = r.GetValueOrDefault("topic_version") is { Length: > 0 } pv ? DbValue.ToInt(pv) : 0;
        var ver = pinned > 0 ? await LoadVersionAsync(topic.Id, pinned) : await LoadLatestPublishedAsync(topic.Id);
        var progress = ver is null ? null : await ComputeProgressForUserAsync(uid, topic, requireAccess: false, ver);
        var mandatory = DbValue.ToBool(r.GetValueOrDefault("mandatory"));
        DateTime? due = r.GetValueOrDefault("due_at") is { Length: > 0 } d ? DbValue.ToDateTime(d) : null;
        return new TopicAssignmentInfo
        {
            Id = r["id"] ?? "", TopicId = topic.Id, TopicName = topic.Name, UserId = uid, UserName = r.GetValueOrDefault("display_name") ?? "", AssignedBy = r.GetValueOrDefault("assigned_by") ?? "",
            AssignedByName = r.GetValueOrDefault("by_name") ?? "", AssignedAt = DbValue.ToDateTime(r.GetValueOrDefault("assigned_at")), TopicVersion = ver?.VersionNumber ?? pinned, Mandatory = mandatory, DueAt = due,
            Overdue = mandatory && due is DateTime dd && dd < DateTime.UtcNow && progress?.Status != TopicLearnStatuses.Completed, Progress = progress,
        };
    }

    // ─── Lernstatus ─────────────────────────────────────────────────────────

    public sealed class StatusFilter
    {
        public string? TopicId { get; set; }
        public string? UserId { get; set; }
        /// <summary>not_started | in_progress | completed | overdue</summary>
        public string? Status { get; set; }
        public bool MandatoryOnly { get; set; }
    }

    /// <summary>
    /// Lernstatus je Benutzer, Thema und Version für Auditor, Themen-Admin und Admin (nur lesend). Enthalten sind Zuweisungen und freiwillige Bearbeitungen globaler Themen — keine privaten
    /// Themen ohne Zuweisung, keine Karteninhalte, Chats oder Prüfungsantworten; je Benutzer nur der Anzeigename (Datenminimierung).
    /// </summary>
    public async Task<List<TopicStatusRow>> ListStatusAsync(string? actorId, StatusFilter? filter = null, int limit = 500)
    {
        await _users.RequireAnyRoleAsync(actorId, Roles.Auditor, Roles.TopicAdmin, Roles.Admin);
        filter ??= new();
        var rows = new List<TopicStatusRow>();
        var assignedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sql = """
            SELECT a.id, a.topic_id, a.user_id, a.assigned_at, a.topic_version, a.mandatory, a.due_at, u.display_name
            FROM topic_assignments a JOIN users u ON u.id = a.user_id WHERE a.status = 'active'
            """;
        var args = new List<(string, object?)>();
        if (filter.TopicId is not null) { sql += " AND a.topic_id = @t"; args.Add(("t", filter.TopicId)); }
        if (filter.UserId is not null) { sql += " AND a.user_id = @u"; args.Add(("u", filter.UserId)); }
        if (filter.MandatoryOnly) sql += " AND a.mandatory = 1";
        foreach (var r in await _db.QueryAsync(sql + " ORDER BY a.assigned_at DESC", args))
        {
            if (rows.Count >= limit) break;
            var topic = await GetTopicRawAsync(r["topic_id"] ?? "");
            if (topic is null) continue;
            assignedKeys.Add(topic.Id + ":" + r["user_id"]);
            var info = await MapAssignmentAsync(r, topic);
            if (info.Progress is null) continue;
            rows.Add(new TopicStatusRow
            {
                UserId = info.UserId, UserName = info.UserName, TopicId = topic.Id, TopicName = topic.Name, Version = info.TopicVersion, Assigned = true, Mandatory = info.Mandatory,
                Relation = info.Mandatory ? TopicRelations.AssignedMandatory : TopicRelations.AssignedVoluntary, Status = info.Progress.Status, ProgressPercent = info.Progress.ProgressPercent,
                ModulesCompleted = info.Progress.ModulesCompleted, ModulesTotal = info.Progress.ModulesTotal, AssignedAt = info.AssignedAt, DueAt = info.DueAt, CompletedAt = info.Progress.CompletedAt,
                Overdue = info.Overdue, AssignmentId = info.Id,
            });
        }

        if (!filter.MandatoryOnly)
        {
            var vsql = """
                SELECT s.topic_id, s.user_id, u.display_name FROM topic_user_state s JOIN users u ON u.id = s.user_id
                WHERE EXISTS (SELECT 1 FROM topic_versions gv WHERE gv.topic_id = s.topic_id AND gv.status = 'published' AND gv.global_status = 'active')
                """;
            var vargs = new List<(string, object?)>();
            if (filter.TopicId is not null) { vsql += " AND s.topic_id = @t"; vargs.Add(("t", filter.TopicId)); }
            if (filter.UserId is not null) { vsql += " AND s.user_id = @u"; vargs.Add(("u", filter.UserId)); }
            foreach (var r in await _db.QueryAsync(vsql, vargs))
            {
                if (rows.Count >= limit) break;
                var topic = await GetTopicRawAsync(r["topic_id"] ?? "");
                if (topic is null || assignedKeys.Contains(topic.Id + ":" + r["user_id"])) continue;
                var ver = await ResolveVersionForLearnerAsync(r["user_id"] ?? "", topic, TopicSections.Global, null);
                if (ver is null) continue;
                var p = await ComputeProgressForUserAsync(r["user_id"] ?? "", topic, requireAccess: false, ver);
                if (p is null || p.Status == TopicLearnStatuses.NotStarted && p.LastActivityAt is null) continue;   // nur Benutzer mit tatsächlicher Aktivität
                rows.Add(new TopicStatusRow
                {
                    UserId = r["user_id"] ?? "", UserName = r.GetValueOrDefault("display_name") ?? "", TopicId = topic.Id, TopicName = topic.Name, Version = ver.VersionNumber, Assigned = false, Mandatory = false,
                    Relation = TopicRelations.GlobalVoluntary, Status = p.Status, ProgressPercent = p.ProgressPercent, ModulesCompleted = p.ModulesCompleted, ModulesTotal = p.ModulesTotal, CompletedAt = p.CompletedAt,
                });
            }
        }

        IEnumerable<TopicStatusRow> q = rows;
        if (!string.IsNullOrEmpty(filter.Status))
            q = filter.Status == "overdue" ? q.Where(r => r.Overdue) : q.Where(r => r.Status == filter.Status);
        return q.OrderByDescending(r => r.Overdue).ThenBy(r => r.TopicName).ThenBy(r => r.UserName).ToList();
    }

    // ─── Modulverwendung und Herkunft ───────────────────────────────────────

    /// <summary>
    /// In welchen <b>eigenen</b> Themen werden die eigenen Module verwendet (Entwurf und veröffentlichte Versionen)? Läuft über die stabilen Modul-IDs, bleibt also bei Umbenennung korrekt.
    /// Fremde Themen erscheinen nie; Zuordnung nur über Themen des Benutzers.
    /// </summary>
    public async Task<Dictionary<string, List<ModuleTopicUsage>>> ListModuleUsageAsync(string ownerId)
    {
        var map = new Dictionary<string, Dictionary<string, ModuleTopicUsage>>(StringComparer.OrdinalIgnoreCase);
        ModuleTopicUsage Get(string moduleId, string topicId, string topicName)
        {
            if (!map.TryGetValue(moduleId, out var perTopic)) map[moduleId] = perTopic = new(StringComparer.OrdinalIgnoreCase);
            if (!perTopic.TryGetValue(topicId, out var u)) perTopic[topicId] = u = new ModuleTopicUsage { TopicId = topicId, TopicName = topicName };
            return u;
        }
        foreach (var r in await _db.QueryAsync("""
            SELECT tm.module_id, t.id, t.name FROM topic_modules tm JOIN topics t ON t.id = tm.topic_id WHERE t.owner_user_id = @u AND t.status = 'active'
            """, new[] { ("u", (object?)ownerId) }))
            Get(r["module_id"] ?? "", r["id"] ?? "", r["name"] ?? "").InDraft = true;
        foreach (var r in await _db.QueryAsync("""
            SELECT vm.source_module_id, t.id, t.name, v.version_number, v.global_status
            FROM topic_version_modules vm JOIN topic_versions v ON v.id = vm.version_id AND v.status = 'published' JOIN topics t ON t.id = v.topic_id
            WHERE t.owner_user_id = @u AND t.status = 'active' AND vm.role = 'module' AND vm.source_module_id IS NOT NULL ORDER BY v.version_number
            """, new[] { ("u", (object?)ownerId) }))
        {
            var u = Get(r["source_module_id"] ?? "", r["id"] ?? "", r["name"] ?? "");
            u.Versions.Add(DbValue.ToInt(r["version_number"]));
            if (r.GetValueOrDefault("global_status") == TopicGlobalStatuses.Active) u.GlobalActive = true;
        }
        return map.ToDictionary(kv => kv.Key, kv => kv.Value.Values.OrderBy(u => u.TopicName, StringComparer.CurrentCultureIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Herkunft einer eingefrorenen Themenmodulkopie (Thema und neueste enthaltende Version). Nur nach erfolgreichem Zugriffsentscheid des Aufrufers verwenden.</summary>
    public async Task<TopicModuleOrigin?> GetModuleOriginAsync(string moduleId)
    {
        if (!UserService.IsGuid(moduleId)) return null;
        var r = (await _db.QueryAsync("""
            SELECT t.id, t.name, v.version_number, v.global_status
            FROM topic_version_modules vm JOIN topic_versions v ON v.id = vm.version_id AND v.status = 'published' JOIN topics t ON t.id = v.topic_id
            WHERE vm.module_id = @m ORDER BY v.version_number DESC LIMIT 1
            """, new[] { ("m", (object?)moduleId) })).FirstOrDefault();
        return r is null ? null : new TopicModuleOrigin { TopicId = r["id"] ?? "", TopicName = r["name"] ?? "", Version = DbValue.ToInt(r["version_number"]), GlobalStatus = r.GetValueOrDefault("global_status") ?? "none" };
    }
}
