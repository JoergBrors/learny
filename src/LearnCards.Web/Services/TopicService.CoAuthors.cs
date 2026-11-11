using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>
/// Co-Author-Modus (ADR 0052): themenbezogene Bearbeitungsberechtigung. <b>Einzige</b> Entscheidungsstelle für Schreibrechte an Themen ist <see cref="GetTopicRoleAsync"/>;
/// alle Prüfungen laufen hier im Service gegen die Datenbank (nie aus Request-Daten, nie aus Token-Claims). Co-Author ist keine globale Rolle (ADR 0012) und keine Lernfreigabe (ADR 0015).
/// </summary>
public partial class TopicService
{
    public const int MaxCoAuthorsPerTopic = 50;

    // ─── Zentrale Berechtigungsentscheidung ─────────────────────────────────

    /// <summary>
    /// Rolle des Benutzers an genau diesem Thema. Eigentümer (unverändert) → <see cref="TopicRole.Owner"/>; Eintrag in <c>topic_coauthors</c> für dieses Thema, aktiver und nicht
    /// eingeschränkter Benutzer → <see cref="TopicRole.CoAuthor"/>; sonst <see cref="TopicRole.None"/>. Berechtigungen anderer Themen fließen nie ein.
    /// </summary>
    public async Task<TopicRole> GetTopicRoleAsync(string userId, string topicId)
    {
        var topic = await GetTopicRawAsync(topicId);
        return topic is null ? TopicRole.None : await RoleForAsync(userId, topic);
    }

    private async Task<TopicRole> RoleForAsync(string userId, TopicInfo topic)
    {
        if (!UserService.IsGuid(userId)) return TopicRole.None;
        if (string.Equals(topic.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase)) return TopicRole.Owner;
        if (await _repo.IsRestrictedLearnerAsync(userId)) return TopicRole.None;   // ADR 0049: eingeschränkte Lernprofile erhalten nie Autorenrechte
        var rows = await _db.QueryAsync("""
            SELECT 1 FROM topic_coauthors c JOIN users u ON u.id = c.user_id
            WHERE c.topic_id = @t AND c.user_id = @u AND u.status = 'active'
            """, new (string, object?)[] { ("t", topic.Id), ("u", userId) });
        return rows.Count > 0 ? TopicRole.CoAuthor : TopicRole.None;
    }

    /// <summary>Thema für Bearbeitung des Entwurfs: Eigentümer oder Co-Author dieses Themas; alle anderen verhalten sich wie „nicht gefunden“.</summary>
    private async Task<TopicInfo> RequireEditableTopicAsync(string actorId, string topicId, bool allowArchived = false)
    {
        var topic = await GetTopicRawAsync(topicId);
        if (topic is null || await RoleForAsync(actorId, topic) == TopicRole.None) throw new KeyNotFoundException("Thema nicht gefunden.");
        if (!allowArchived && topic.Status == TopicStatuses.Archived) throw new InvalidOperationException("Das Thema ist archiviert. Hebe die Archivierung auf, um es zu bearbeiten.");
        return topic;
    }

    /// <summary>
    /// Konflikterkennung für den Themenentwurf (ADR 0052): hat jemand anderes den Entwurf seit dem Laden verändert (<c>draft_revision</c>), wird nicht gespeichert.
    /// <paramref name="expectedRevision"/> = null prüft nicht (bestehende Aufrufer).
    /// </summary>
    private static void CheckDraftRevision(TopicInfo topic, int? expectedRevision)
    {
        if (expectedRevision is int e && e != topic.DraftRevision)
            throw new InvalidOperationException("Konflikt: Das Thema wurde inzwischen von jemand anderem geändert. Lade es neu, um die aktuellen Änderungen zu sehen — deine Änderung wurde nicht gespeichert.");
    }

    // ─── Bearbeitungskontext für Module (themenbezogen, ohne Freigabe fremder Module) ─────────

    /// <summary>
    /// Benutzer, unter dessen Eigentum Modul- und Karten-Dienste für <paramref name="actorId"/> arbeiten. Eigene Module → der Benutzer selbst (unverändert). Fremdes privates Modul →
    /// der Eigentümer <b>nur</b>, wenn das Modul im Entwurf eines Themas dieses Eigentümers liegt, an dem der Benutzer Co-Author ist, und das Modul in <b>keinem</b> anderen Thema
    /// liegt (keine Auswirkungen auf Themen ohne Bearbeitungsrecht). Sonst der Benutzer selbst (Dienste verhalten sich dann wie bisher: „nicht gefunden“).
    /// Moduleigentum, Veröffentlichung von Modulversionen und Modulverwaltung bleiben beim Eigentümer: dafür wird immer die eigene Benutzer-ID verwendet.
    /// </summary>
    public async Task<string> ResolveModuleEditOwnerAsync(string actorId, string moduleId)
    {
        if (!UserService.IsGuid(actorId) || !UserService.IsGuid(moduleId)) return actorId;
        var mod = (await _db.QueryAsync("SELECT owner_user_id, scope FROM modules WHERE id = @m", new[] { ("m", (object?)moduleId) })).FirstOrDefault();
        var owner = mod?.GetValueOrDefault("owner_user_id");
        if (mod is null || string.IsNullOrEmpty(owner) || mod.GetValueOrDefault("scope") != "private") return actorId;
        if (string.Equals(owner, actorId, StringComparison.OrdinalIgnoreCase)) return actorId;

        var topics = await _db.QueryAsync("""
            SELECT t.id, t.owner_user_id, t.status FROM topic_modules tm JOIN topics t ON t.id = tm.topic_id WHERE tm.module_id = @m
            """, new[] { ("m", (object?)moduleId) });
        if (topics.Count == 0) return actorId;
        var viaCoAuthorTopic = false;
        foreach (var t in topics)
        {
            var topic = await GetTopicRawAsync(t["id"] ?? "");
            if (topic is null) continue;
            var role = await RoleForAsync(actorId, topic);
            if (role == TopicRole.None) return actorId;   // das Modul wird auch in einem Thema ohne Bearbeitungsrecht verwendet: keine gemeinsame Bearbeitung
            if (role == TopicRole.CoAuthor && string.Equals(topic.OwnerUserId, owner, StringComparison.OrdinalIgnoreCase) && topic.Status == TopicStatuses.Active) viaCoAuthorTopic = true;
        }
        return viaCoAuthorTopic ? owner : actorId;
    }

    // ─── Verwaltung der Co-Autoren ──────────────────────────────────────────

    /// <summary>Eigentümer und Co-Autoren des Themas — sichtbar für Eigentümer und Co-Autoren.</summary>
    public async Task<TopicCollaboration> GetCollaborationAsync(string actorId, string topicId)
    {
        var topic = await RequireEditableTopicAsync(actorId, topicId, allowArchived: true);
        var owner = await _users.GetAsync(topic.OwnerUserId);
        var rows = await _db.QueryAsync("""
            SELECT c.user_id, c.created_at, u.display_name, u.email, g.display_name AS granted_name
            FROM topic_coauthors c JOIN users u ON u.id = c.user_id LEFT JOIN users g ON g.id = c.granted_by
            WHERE c.topic_id = @t ORDER BY u.display_name
            """, new[] { ("t", (object?)topic.Id) });
        return new TopicCollaboration
        {
            TopicId = topic.Id, OwnerUserId = topic.OwnerUserId, OwnerName = topic.OwnerName, OwnerEmail = owner?.Email ?? "",
            ViewerRole = await RoleForAsync(actorId, topic),
            CoAuthors = rows.Select(r => new TopicCoAuthorInfo
            {
                UserId = r["user_id"] ?? "", DisplayName = r.GetValueOrDefault("display_name") ?? "", Email = r.GetValueOrDefault("email") ?? "",
                GrantedByName = r.GetValueOrDefault("granted_name") ?? "", CreatedAt = DbValue.ToDateTime(r.GetValueOrDefault("created_at")),
            }).ToList(),
        };
    }

    /// <summary>Kandidaten für „Co-Author hinzufügen“: aktive, nicht eingeschränkte Benutzer, die weder Eigentümer noch bereits Co-Author sind.</summary>
    public async Task<List<UserSearchResult>> SearchCoAuthorCandidatesAsync(string actorId, string topicId, string? query)
    {
        var topic = await RequireEditableTopicAsync(actorId, topicId, allowArchived: true);
        var existing = (await _db.QueryAsync("SELECT user_id FROM topic_coauthors WHERE topic_id = @t", new[] { ("t", (object?)topic.Id) })).Select(r => r["user_id"] ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<UserSearchResult>();
        foreach (var u in await _users.SearchUsersAsync(actorId, query))
        {
            if (existing.Contains(u.Id) || string.Equals(u.Id, topic.OwnerUserId, StringComparison.OrdinalIgnoreCase)) continue;
            if (await _repo.IsRestrictedLearnerAsync(u.Id)) continue;
            found.Add(u);
        }
        return found;
    }

    /// <summary>
    /// Fügt einen Co-Author hinzu. Berechtigt: Eigentümer und Co-Autoren dieses Themas. Nicht möglich: sich selbst, den Eigentümer, nicht existierende/inaktive oder eingeschränkte Benutzer.
    /// Idempotent. Erzeugt weder Lernfreigabe noch Zuweisung noch Rolle.
    /// </summary>
    public async Task<TopicCoAuthorInfo> AddCoAuthorAsync(string actorId, string topicId, string targetUserId)
    {
        var topic = await RequireEditableTopicAsync(actorId, topicId, allowArchived: true);
        if (!UserService.IsGuid(targetUserId)) throw new KeyNotFoundException("Benutzer nicht gefunden.");
        if (string.Equals(targetUserId, actorId, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Du kannst dir selbst keine Berechtigung erteilen.");
        if (string.Equals(targetUserId, topic.OwnerUserId, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Der Eigentümer des Themas ist bereits berechtigt.");
        var target = await _users.GetAsync(targetUserId);
        if (target is null || !target.IsActive) throw new KeyNotFoundException("Benutzer nicht gefunden.");
        if (await _repo.IsRestrictedLearnerAsync(target.Id)) throw new ArgumentException("Eingeschränkte Lernprofile (externe Lernende) können keine Co-Autoren sein.");

        var existing = await _db.QueryAsync("SELECT 1 FROM topic_coauthors WHERE topic_id = @t AND user_id = @u", new (string, object?)[] { ("t", topic.Id), ("u", target.Id) });
        if (existing.Count == 0)
        {
            var count = DbValue.ToInt((await _db.QueryAsync("SELECT COUNT(*) AS n FROM topic_coauthors WHERE topic_id = @t", new[] { ("t", (object?)topic.Id) }))[0]["n"]);
            if (count >= MaxCoAuthorsPerTopic) throw new InvalidOperationException($"Ein Thema kann höchstens {MaxCoAuthorsPerTopic} Co-Autoren haben.");
            try
            {
                await _db.ExecuteAsync("INSERT INTO topic_coauthors (topic_id, user_id, granted_by, created_at) VALUES (@t, @u, @g, @n)",
                    new (string, object?)[] { ("t", topic.Id), ("u", target.Id), ("g", actorId), ("n", DateTime.UtcNow) });
            }
            catch (Exception)
            {
                // paralleles Hinzufügen desselben Benutzers: der Eintrag existiert dann bereits (Primärschlüssel), alles andere ist ein echter Fehler
                var now = await _db.QueryAsync("SELECT 1 FROM topic_coauthors WHERE topic_id = @t AND user_id = @u", new (string, object?)[] { ("t", topic.Id), ("u", target.Id) });
                if (now.Count == 0) throw;
            }
            await _users.WriteAuditAsync(actorId, "topic.coauthor.add", topic.Id, $"user={target.Id}");
        }
        return (await GetCollaborationAsync(actorId, topic.Id)).CoAuthors.First(c => string.Equals(c.UserId, target.Id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Entfernt einen Co-Author sofort (Zugriff erlischt mit dem nächsten Aufruf). Berechtigt: Eigentümer und Co-Autoren dieses Themas. Der Eigentümer kann nicht entfernt werden.
    /// Löscht ausschließlich die Berechtigung, nie Themen-, Modul-, Karten- oder Lerninhalte.
    /// </summary>
    public async Task<bool> RemoveCoAuthorAsync(string actorId, string topicId, string targetUserId)
    {
        var topic = await RequireEditableTopicAsync(actorId, topicId, allowArchived: true);
        if (!UserService.IsGuid(targetUserId)) return false;
        if (string.Equals(targetUserId, topic.OwnerUserId, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Der Eigentümer des Themas kann nicht entfernt werden.");
        var n = await _db.ExecuteAsync("DELETE FROM topic_coauthors WHERE topic_id = @t AND user_id = @u", new (string, object?)[] { ("t", topic.Id), ("u", targetUserId) });
        if (n > 0) await _users.WriteAuditAsync(actorId, "topic.coauthor.remove", topic.Id, $"user={targetUserId}");
        return n > 0;
    }

    /// <summary>Beendet die eigene Mitarbeit (nur Co-Autoren; der Eigentümer kann nicht austreten).</summary>
    public async Task<bool> LeaveCoAuthorAsync(string actorId, string topicId)
    {
        var topic = await GetTopicRawAsync(topicId);
        if (topic is null || await RoleForAsync(actorId, topic) != TopicRole.CoAuthor) throw new KeyNotFoundException("Thema nicht gefunden.");
        var n = await _db.ExecuteAsync("DELETE FROM topic_coauthors WHERE topic_id = @t AND user_id = @u", new (string, object?)[] { ("t", topic.Id), ("u", actorId) });
        if (n > 0) await _users.WriteAuditAsync(actorId, "topic.coauthor.leave", topic.Id, "");
        return n > 0;
    }
}
