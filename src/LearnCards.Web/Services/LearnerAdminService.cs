using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>Zeile der Lernendenübersicht (Verwaltung). Keine Rollen, keine Inhalte, keine Antworten.</summary>
public sealed record LearnerRow(string UserId, string DisplayName, string Email, string Status, string? Profile, DateTime CreatedAt);

/// <summary>
/// Fachliche Verwaltungsoperationen für Lernprofile und Zuweisungen (ADR 0049) — die Schnittstelle der Administrationsoberfläche (Phase C). Keine zweite Benutzer-, Modul- oder
/// Themenverwaltung: Benutzer kommen aus <see cref="UserService"/>, Themenzuweisungen aus <see cref="TopicService"/> (ADR 0027/0029), Modulzuweisungen liegen in <c>module_grants</c>.
/// Alle Methoden prüfen die Rolle des Aufrufers gegen die Datenbank. Ein Lernprofil wird nur durch die Einladungseinlösung vergeben und hier nie geändert; Zuweisungen verleihen
/// nie Rollen. Für Benutzer des Bestandsprofils sind Modulzuweisungen informativ (ihr Zugriff bleibt unverändert), für externe Profile die einzige Quelle des Zugriffs auf globale Module.
/// </summary>
public sealed class LearnerAdminService
{
    private readonly IDatabase _db;
    private readonly UserService _users;
    private readonly TopicService _topics;
    private readonly InviteEvents _events;

    public LearnerAdminService(IDatabase db, UserService users, TopicService topics, InviteEvents events)
    {
        _db = db; _users = users; _topics = topics; _events = events;
    }

    // ─── Module ────────────────────────────────────────────────────────

    /// <summary>Weist einem aktiven Benutzer ein aktives globales Modul ausdrücklich zu (nur Administratoren). Wiederholtes Zuweisen reaktiviert einen widerrufenen Eintrag.</summary>
    public async Task AssignModuleAsync(string? actorId, string targetUserId, string moduleId, CancellationToken ct = default)
    {
        var actor = await _users.RequireRoleAsync(actorId, Roles.Admin);
        var target = UserService.IsGuid(targetUserId) ? await _users.GetAsync(targetUserId) : null;
        if (target is null || !target.IsActive) throw new KeyNotFoundException("Benutzer nicht gefunden.");
        var rows = UserService.IsGuid(moduleId)
            ? await _db.QueryAsync("SELECT id, publication_id FROM modules WHERE id=@m AND scope='global' AND global_status='active'", new (string, object?)[] { ("m", moduleId) }, ct) : new();
        if (rows.Count == 0) throw new ArgumentException("Nur aktive globale Module sind zuweisbar.");
        var now = DateTime.UtcNow;
        await _db.ExecuteAsync("""
            INSERT INTO module_grants (user_id, module_id, publication_id, source_invitation_id, granted_by, granted_at)
            VALUES (@u, @m, @pub, NULL, @by, @now)
            ON CONFLICT (user_id, module_id) DO UPDATE SET revoked_at = NULL, revoked_by = NULL, granted_by = @by, granted_at = @now, publication_id = @pub
            """, new (string, object?)[] { ("u", target.Id), ("m", rows[0]["id"]), ("pub", rows[0]["publication_id"]), ("by", actor.Id), ("now", now) }, ct);
        await _events.LogAsync(InviteEventTypes.ContentAssigned, null, target.Id, "module", actor.Id, ct);
        await _users.WriteAuditAsync(actor.Id, "module.grant", moduleId, $"user={target.Id}");
    }

    /// <summary>Widerruft eine Modulzuweisung (nur Administratoren). Für externe Profile endet damit der Zugriff sofort; Ergebnisse und Fortschritt bleiben erhalten.</summary>
    public async Task<bool> RevokeModuleAsync(string? actorId, string targetUserId, string moduleId, CancellationToken ct = default)
    {
        var actor = await _users.RequireRoleAsync(actorId, Roles.Admin);
        if (!UserService.IsGuid(targetUserId) || !UserService.IsGuid(moduleId)) return false;
        var n = await _db.ExecuteAsync("UPDATE module_grants SET revoked_at=@now, revoked_by=@by WHERE user_id=@u AND module_id=@m AND revoked_at IS NULL",
            new (string, object?)[] { ("u", targetUserId), ("m", moduleId), ("by", actor.Id), ("now", DateTime.UtcNow) }, ct);
        if (n > 0)
        {
            await _events.LogAsync(InviteEventTypes.ContentRevoked, null, targetUserId, "module", actor.Id, ct);
            await _users.WriteAuditAsync(actor.Id, "module.ungrant", moduleId, $"user={targetUserId}");
        }
        return n > 0;
    }

    public async Task<List<ModuleGrantInfo>> ListModuleGrantsAsync(string? actorId, string targetUserId, CancellationToken ct = default)
    {
        await _users.RequireAnyRoleAsync(actorId, Roles.Admin, Roles.TopicAdmin);
        return await GrantsAsync(targetUserId, ct);
    }

    private async Task<List<ModuleGrantInfo>> GrantsAsync(string userId, CancellationToken ct)
    {
        if (!UserService.IsGuid(userId)) return new();
        var rows = await _db.QueryAsync("""
            SELECT g.module_id, m.name, m.scope, g.granted_at, g.revoked_at, g.source_invitation_id
            FROM module_grants g JOIN modules m ON m.id = g.module_id
            WHERE g.user_id = @u ORDER BY m.name
            """, new[] { ("u", (object?)userId) }, ct);
        return rows.Select(r => new ModuleGrantInfo(r["module_id"]!, r["name"] ?? "", r["scope"] ?? "", DbValue.ToDateTime(r["granted_at"]), r["revoked_at"] is null,
            r["revoked_at"] is null ? null : DbValue.ToDateTime(r["revoked_at"]), r.GetValueOrDefault("source_invitation_id"))).ToList();
    }

    // ─── Themen (bestehende Zuweisungslogik, ADR 0029) ─────────────────

    /// <summary>Weist ein Thema in einer konkreten Version zu (verpflichtend nur bei ausdrücklichem <paramref name="mandatory"/>). Delegiert an <see cref="TopicService.AssignAsync"/>; Administrator und Themen-Admin.</summary>
    public async Task<TopicAssignmentInfo> AssignTopicAsync(string? actorId, string targetUserId, string topicId, int? version = null, bool mandatory = false, DateTime? dueAt = null, CancellationToken ct = default)
    {
        var result = await _topics.AssignAsync(actorId, topicId, targetUserId, version, mandatory, dueAt);
        await _events.LogAsync(InviteEventTypes.ContentAssigned, null, targetUserId, mandatory ? "topic_mandatory" : "topic", actorId, ct);
        return result;
    }

    public async Task<bool> RevokeTopicAssignmentAsync(string? actorId, string assignmentId, CancellationToken ct = default)
    {
        string? userId = null;
        if (UserService.IsGuid(assignmentId))
            userId = (await _db.QueryAsync("SELECT user_id FROM topic_assignments WHERE id=@id AND status='active'", new[] { ("id", (object?)assignmentId) }, ct)).FirstOrDefault()?["user_id"];
        var ok = await _topics.RevokeAssignmentAsync(actorId, assignmentId);
        if (ok && userId is not null) await _events.LogAsync(InviteEventTypes.ContentRevoked, null, userId, "topic", actorId, ct);
        return ok;
    }

    // ─── Status ────────────────────────────────────────────────────────

    /// <summary>Externe Lernende (mit Lernprofil), neueste zuerst (Administrator und Themen-Admin).</summary>
    public async Task<List<LearnerRow>> ListExternalLearnersAsync(string? actorId, int take = 500, CancellationToken ct = default)
    {
        await _users.RequireAnyRoleAsync(actorId, Roles.Admin, Roles.TopicAdmin);
        var rows = await _db.QueryAsync($"""
            SELECT u.id, u.display_name, u.email, u.status, u.created_at, p.profile
            FROM users u JOIN user_profiles p ON p.user_id = u.id
            ORDER BY u.created_at DESC LIMIT {Math.Clamp(take, 1, 2000)}
            """, null, ct);
        return rows.Select(r => new LearnerRow(r["id"]!, r["display_name"] ?? "", r["email"] ?? "", r["status"] ?? "", r["profile"], DbValue.ToDateTime(r["created_at"]))).ToList();
    }

    /// <summary>Profil, Identitätsarten und Zuweisungen eines Benutzers (Administrator und Themen-Admin). Auch für Benutzer des Bestandsprofils (Profil = null).</summary>
    public async Task<LearnerOverviewInfo?> GetOverviewAsync(string? actorId, string targetUserId, CancellationToken ct = default)
    {
        await _users.RequireAnyRoleAsync(actorId, Roles.Admin, Roles.TopicAdmin);
        var user = UserService.IsGuid(targetUserId) ? await _users.GetAsync(targetUserId) : null;
        if (user is null) return null;
        var profile = (await _db.QueryAsync("SELECT profile FROM user_profiles WHERE user_id=@u", new[] { ("u", (object?)user.Id) }, ct)).FirstOrDefault()?["profile"];
        var kinds = (await _db.QueryAsync("SELECT DISTINCT kind FROM user_identities WHERE user_id=@u", new[] { ("u", (object?)user.Id) }, ct)).Select(r => r["kind"]).ToHashSet();
        var topics = (await _db.QueryAsync("""
            SELECT a.id, a.topic_id, t.name, a.topic_version, a.mandatory, a.due_at, a.status
            FROM topic_assignments a JOIN topics t ON t.id = a.topic_id
            WHERE a.user_id = @u ORDER BY t.name
            """, new[] { ("u", (object?)user.Id) }, ct)).Select(r => new LearnerTopicAssignment(r["id"]!, r["topic_id"]!, r["name"] ?? "",
            r["topic_version"] is { Length: > 0 } v ? DbValue.ToInt(v) : null, DbValue.ToBool(r["mandatory"]), r["due_at"] is { Length: > 0 } d ? DbValue.ToDateTime(d) : null, r["status"] ?? "")).ToList();
        return new LearnerOverviewInfo(user.Id, user.DisplayName, user.Email, user.Status, profile, kinds.Contains(IdentityKinds.External), kinds.Contains(IdentityKinds.Workforce),
            await GrantsAsync(user.Id, ct), topics);
    }
}
