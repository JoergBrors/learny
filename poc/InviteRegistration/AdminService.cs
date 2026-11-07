using System.Security.Cryptography;
using System.Text;
using LearnCards.Web.Data;

namespace InviteRegistration;

public sealed record AdminStats(int TotalInvitations, int Pending, int Submitted, int Accepted, int Expired, int Revoked, int SendFailed, int ActiveUsers, int DisabledUsers);

public sealed record InvitationRow(string InvitationId, string RecipientEmail, string TargetProfile, string Status, string DeliveryStatus, string? DisplayName,
    DateTime CreatedAt, DateTime ExpiresAt, DateTime? AcceptedAt, IReadOnlyList<string> Modules, string? LastEvent);

public sealed record InvitationDetail(InvitationRow Row, string? Description, string? Note, string? DeliveryErrorCode, int Attempts, string? AcceptedUserId, IReadOnlyList<InviteEvent> Events);

public sealed record UserRow(string UserId, string Email, string DisplayName, string Profile, string Status, DateTime CreatedAt, DateTime LastLoginAt, IReadOnlyList<string> ModuleNames);

public sealed record ModuleRow(string Id, string Name, string Scope);

public sealed record InvitationFilter(string? Status = null, string? Delivery = null, string? Search = null);

/// <summary>
/// Verwaltungsfunktionen des Administrationsportals (ADR 0046). Jede Methode prüft die Administratorrolle serverseitig gegen die Datenbank (ADR 0011/0012)
/// und protokolliert Änderungen. Profiltyp und Identitätsbindung werden hier nie geändert; Lernrechte nur über <c>module_grants</c> (zentrale Autorisierung: <see cref="ContentAccess"/>).
/// </summary>
public sealed class AdminService
{
    private readonly IDatabase _db; private readonly InvitationService _inv; private readonly InviteEvents _events; private readonly Func<DateTime> _clock;
    public AdminService(IDatabase db, InvitationService inv, InviteEvents events, Func<DateTime>? clock = null)
    { _db = db; _inv = inv; _events = events; _clock = clock ?? (() => DateTime.UtcNow); }

    public async Task<AdminStats> GetStatsAsync(string actor, CancellationToken ct = default)
    {
        await _inv.RequireAdminAsync(actor, ct);
        var now = _clock();
        async Task<int> N(string sql, params (string, object?)[] a) => DbValue.ToInt((await _db.QueryAsync(sql, a, ct))[0]["n"]);
        return new AdminStats(
            await N("SELECT COUNT(*) AS n FROM invitations"),
            await N("SELECT COUNT(*) AS n FROM invitations WHERE status='pending' AND revoked_at IS NULL AND expires_at>@now", ("now", now)),
            await N("SELECT COUNT(*) AS n FROM invitation_deliveries WHERE status='submitted'"),
            await N("SELECT COUNT(*) AS n FROM invitations WHERE status='accepted'"),
            await N("SELECT COUNT(*) AS n FROM invitations WHERE status='pending' AND revoked_at IS NULL AND expires_at<=@now", ("now", now)),
            await N("SELECT COUNT(*) AS n FROM invitations WHERE status='revoked'"),
            await N("SELECT COUNT(*) AS n FROM invitation_deliveries WHERE status IN ('failed','unknown')"),
            await N("SELECT COUNT(*) AS n FROM users u JOIN user_profiles p ON p.user_id=u.id WHERE u.status='active'"),
            await N("SELECT COUNT(*) AS n FROM users u JOIN user_profiles p ON p.user_id=u.id WHERE u.status<>'active'"));
    }

    private string Effective(string status, string expiresAt, string? revokedAt) =>
        status == InvitationStatus.Pending && revokedAt is null && DbValue.ToDateTime(expiresAt) <= _clock() ? InvitationStatus.Expired : status;

    public async Task<List<InvitationRow>> ListInvitationsAsync(string actor, InvitationFilter? f = null, int take = 200, CancellationToken ct = default)
    {
        await _inv.RequireAdminAsync(actor, ct);
        var rows = await _db.QueryAsync("""
            SELECT i.invitation_id, i.recipient_email, i.target_profile, i.status, i.created_at, i.expires_at, i.accepted_at, i.revoked_at,
                   COALESCE(d.status,'pending') AS delivery_status, m.display_name AS display_name
            FROM invitations i
            LEFT JOIN invitation_deliveries d ON d.invitation_id = i.invitation_id
            LEFT JOIN invitation_meta m ON m.invitation_id = i.invitation_id
            ORDER BY i.created_at DESC
            """, null, ct);
        var result = new List<InvitationRow>();
        foreach (var r in rows)
        {
            var eff = Effective(r["status"]!, r["expires_at"]!, r["revoked_at"]);
            if (f?.Status is { Length: > 0 } st && st != eff) continue;
            if (f?.Delivery is { Length: > 0 } dl && dl != r["delivery_status"]) continue;
            if (f?.Search is { Length: > 0 } q && !(r["recipient_email"]!.Contains(q, StringComparison.OrdinalIgnoreCase) || (r["display_name"]?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))) continue;
            if (result.Count >= take) break;
            var id = r["invitation_id"]!;
            var mods = (await _db.QueryAsync("SELECT m.name AS name FROM invitation_grants g JOIN poc_modules m ON m.id=g.module_id WHERE g.invitation_id=@i ORDER BY m.name", new (string, object?)[] { ("i", id) }, ct)).Select(x => x["name"]!).ToList();
            var last = (await _db.QueryAsync("SELECT event_type FROM invitation_events WHERE invitation_id=@i ORDER BY at DESC LIMIT 1", new (string, object?)[] { ("i", id) }, ct)).FirstOrDefault()?["event_type"];
            result.Add(new InvitationRow(id, r["recipient_email"]!, r["target_profile"]!, eff, r["delivery_status"]!, r["display_name"], DbValue.ToDateTime(r["created_at"]), DbValue.ToDateTime(r["expires_at"]),
                r["accepted_at"] is null ? null : DbValue.ToDateTime(r["accepted_at"]), mods, last));
        }
        return result;
    }

    public async Task<InvitationDetail?> GetInvitationAsync(string actor, string id, CancellationToken ct = default)
    {
        var row = (await ListInvitationsAsync(actor, null, 1000, ct)).FirstOrDefault(x => x.InvitationId == id);
        if (row is null) return null;
        var meta = (await _db.QueryAsync("SELECT description, note FROM invitation_meta WHERE invitation_id=@i", new (string, object?)[] { ("i", id) }, ct)).FirstOrDefault();
        var del = (await _db.QueryAsync("SELECT last_error_code, attempts FROM invitation_deliveries WHERE invitation_id=@i", new (string, object?)[] { ("i", id) }, ct)).FirstOrDefault();
        var acc = (await _db.QueryAsync("SELECT accepted_user_id FROM invitations WHERE invitation_id=@i", new (string, object?)[] { ("i", id) }, ct)).FirstOrDefault();
        var events = (await _events.ListAsync(id, null, 500, ct)).OrderBy(e => e.At).ToList();
        return new InvitationDetail(row, meta?["description"], meta?["note"], del?["last_error_code"], DbValue.ToInt(del?["attempts"]), acc?["accepted_user_id"], events);
    }

    public async Task<List<ModuleRow>> ListModulesAsync(string actor, CancellationToken ct = default)
    {
        await _inv.RequireAdminAsync(actor, ct);
        return (await _db.QueryAsync("SELECT id, name, scope FROM poc_modules ORDER BY name", null, ct)).Select(r => new ModuleRow(r["id"]!, r["name"]!, r["scope"]!)).ToList();
    }

    public async Task<List<UserRow>> ListUsersAsync(string actor, CancellationToken ct = default)
    {
        await _inv.RequireAdminAsync(actor, ct);
        var rows = await _db.QueryAsync("""
            SELECT u.id, u.email, u.display_name, u.status, u.created_at, u.last_login_at, p.profile
            FROM users u JOIN user_profiles p ON p.user_id = u.id
            ORDER BY u.created_at DESC
            """, null, ct);
        var list = new List<UserRow>();
        foreach (var r in rows) list.Add(await ToUserAsync(r, ct));
        return list;
    }

    public async Task<UserRow?> GetUserAsync(string actor, string userId, CancellationToken ct = default)
    {
        await _inv.RequireAdminAsync(actor, ct);
        var r = (await _db.QueryAsync("""
            SELECT u.id, u.email, u.display_name, u.status, u.created_at, u.last_login_at, p.profile
            FROM users u JOIN user_profiles p ON p.user_id = u.id WHERE u.id=@u
            """, new (string, object?)[] { ("u", userId) }, ct)).FirstOrDefault();
        return r is null ? null : await ToUserAsync(r, ct);
    }

    private async Task<UserRow> ToUserAsync(Dictionary<string, string?> r, CancellationToken ct)
    {
        var mods = (await _db.QueryAsync("SELECT m.name AS name FROM module_grants g JOIN poc_modules m ON m.id=g.module_id WHERE g.user_id=@u ORDER BY m.name", new (string, object?)[] { ("u", r["id"]) }, ct)).Select(x => x["name"]!).ToList();
        return new UserRow(r["id"]!, r["email"] ?? "", r["display_name"] ?? "", r["profile"]!, r["status"]!, DbValue.ToDateTime(r["created_at"]), DbValue.ToDateTime(r["last_login_at"]), mods);
    }

    public async Task<List<(string ModuleId, string ModuleName, string Scope)>> ListGrantsAsync(string actor, string userId, CancellationToken ct = default)
    {
        await _inv.RequireAdminAsync(actor, ct);
        return (await _db.QueryAsync("SELECT m.id AS id, m.name AS name, m.scope AS scope FROM module_grants g JOIN poc_modules m ON m.id=g.module_id WHERE g.user_id=@u ORDER BY m.name", new (string, object?)[] { ("u", userId) }, ct))
            .Select(r => (r["id"]!, r["name"]!, r["scope"]!)).ToList();
    }

    /// <summary>Weist einem registrierten externen Benutzer ein globales oder freigegebenes Modul zu. Idempotent; Wirkung sofort (ContentAccess prüft bei jedem Zugriff).</summary>
    public async Task<bool> AssignModuleAsync(string actor, string userId, string moduleId, CancellationToken ct = default)
    {
        await _inv.RequireAdminAsync(actor, ct);
        await RequireExternalUserAsync(userId, ct);
        var mod = await _db.QueryAsync("SELECT scope FROM poc_modules WHERE id=@m", new (string, object?)[] { ("m", moduleId) }, ct);
        if (mod.Count == 0 || mod[0]["scope"] is not ("global" or "shared")) throw new ArgumentException("Nur vorhandene globale oder freigegebene Module sind zuweisbar.");
        var have = await _db.QueryAsync("SELECT 1 AS x FROM module_grants WHERE user_id=@u AND module_id=@m", new (string, object?)[] { ("u", userId), ("m", moduleId) }, ct);
        if (have.Count > 0) return false;
        await _db.ExecuteAsync("INSERT INTO module_grants (user_id, module_id, source_invitation_id, granted_by, granted_at) VALUES (@u,@m,NULL,@a,@now)",
            new (string, object?)[] { ("u", userId), ("m", moduleId), ("a", actor), ("now", _clock()) }, ct);
        await _events.LogAsync(EventTypes.ContentAssigned, null, userId, "admin", actor, ct);
        return true;
    }

    /// <summary>Entzieht ein Modul sofort; auch der direkte Zugriff ist danach gesperrt.</summary>
    public async Task<bool> RevokeModuleAsync(string actor, string userId, string moduleId, CancellationToken ct = default)
    {
        await _inv.RequireAdminAsync(actor, ct);
        await RequireExternalUserAsync(userId, ct);
        var n = await _db.ExecuteAsync("DELETE FROM module_grants WHERE user_id=@u AND module_id=@m", new (string, object?)[] { ("u", userId), ("m", moduleId) }, ct);
        if (n > 0) await _events.LogAsync(EventTypes.ContentRevoked, null, userId, "admin", actor, ct);
        return n > 0;
    }

    public async Task<bool> SetUserActiveAsync(string actor, string userId, bool active, CancellationToken ct = default)
    {
        await _inv.RequireAdminAsync(actor, ct);
        await RequireExternalUserAsync(userId, ct);
        var n = await _db.ExecuteAsync("UPDATE users SET status=@new WHERE id=@u AND status=@old", new (string, object?)[] { ("u", userId), ("new", active ? "active" : "disabled"), ("old", active ? "disabled" : "active") }, ct);
        if (n > 0) await _events.LogAsync(active ? EventTypes.UserReactivated : EventTypes.UserDeactivated, null, userId, null, actor, ct);
        return n > 0;
    }

    /// <summary>Ein Wechsel zwischen B2B_LEARNER und B2C_LEARNER ist bewusst nicht vorgesehen und braucht eine eigene dokumentierte Entscheidung mit erneuter Rechteprüfung (ADR 0046).</summary>
    public Task ChangeProfileAsync(string actor, string userId, string newProfile) =>
        throw new NotSupportedException("Profilwechsel ist nicht vorgesehen (eigene Entscheidung nötig).");

    private async Task RequireExternalUserAsync(string userId, CancellationToken ct)
    {
        var rows = await _db.QueryAsync("""
            SELECT u.id AS id FROM users u JOIN user_profiles p ON p.user_id = u.id
            WHERE u.id=@u AND NOT EXISTS (SELECT 1 FROM user_roles r WHERE r.user_id = u.id)
            """, new (string, object?)[] { ("u", userId) }, ct);
        if (rows.Count == 0) throw new ArgumentException("Nur registrierte externe Benutzer ohne Rolle sind verwaltbar.");
    }
}

/// <summary>
/// Sichere Bootstrap-Konfiguration für Administratoren (ADR 0046): Ein Administrator ist nur, wer von einem ausdrücklich vertrauten Workforce-Issuer
/// stammt UND dessen Subject (nicht die E-Mail) in der Konfiguration steht. Einzige Stelle, an der die Rolle <c>admin</c> vergeben wird; nie über Einladungen.
/// </summary>
public sealed class AdminBootstrap
{
    private readonly IDatabase _db; private readonly string _issuer; private readonly HashSet<string> _subjects; private readonly HashSet<string> _roles; private readonly Func<DateTime> _clock;

    /// <param name="allowedRoles">App-Rollen, die im ID-Token des vertrauten Workforce-Issuers zur Administration berechtigen (z. B. "Admin.All" wie in Learny).</param>
    public AdminBootstrap(IDatabase db, string trustedIssuer, IEnumerable<string> subjects, IEnumerable<string> forbiddenIssuers, Func<DateTime>? clock = null, IEnumerable<string>? allowedRoles = null)
    {
        _db = db; _issuer = trustedIssuer; _clock = clock ?? (() => DateTime.UtcNow);
        _roles = (allowedRoles ?? Array.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToHashSet(StringComparer.Ordinal);
        _subjects = subjects.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToHashSet(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(trustedIssuer)) throw new InvalidOperationException("Der vertraute Administrator-Issuer ist Pflicht.");
        if (forbiddenIssuers.Any(f => string.Equals(f, trustedIssuer, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Der Administrator-Issuer darf nicht der External-ID-Issuer sein (getrennte vertrauenswürdige Issuer).");
    }

    public int AllowedCount => _subjects.Count + _roles.Count;

    /// <summary>Interne Benutzer-ID des Administrators oder null, wenn die Identität nicht ausdrücklich zugelassen ist (fail-closed).</summary>
    public async Task<string?> TryEnsureAdminAsync(VerifiedIdentity id, CancellationToken ct = default)
    {
        if (!string.Equals(id.Issuer, _issuer, StringComparison.Ordinal)) return null;   // nur der ausdrücklich vertraute Workforce-Issuer
        var allowed = _subjects.Contains(id.Subject) || (id.Roles?.Any(_roles.Contains) ?? false);
        if (!allowed)
        {
            // Quelle der Wahrheit ist Entra: Wer die Rolle bei der Anmeldung nicht mehr hat, verliert die Portal-Administration sofort (laufende Sitzungen werden bei jedem Aufruf gegen die Datenbank geprüft).
            await _db.ExecuteAsync("DELETE FROM user_roles WHERE role='admin' AND user_id IN (SELECT user_id FROM user_identities WHERE provider=@p AND subject=@s)",
                new (string, object?)[] { ("p", id.Issuer), ("s", id.Subject) }, ct);
            return null;
        }
        var existing = await _db.QueryAsync("SELECT user_id FROM user_identities WHERE provider=@p AND subject=@s", new (string, object?)[] { ("p", id.Issuer), ("s", id.Subject) }, ct);
        var now = _clock();
        string userId;
        if (existing.Count > 0) userId = existing[0]["user_id"]!;
        else
        {
            userId = Guid.NewGuid().ToString();
            var name = InvitationService.NormalizeEmail(id.Email) is { } e ? e[..e.IndexOf('@')] : "Administrator";
            await _db.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id,@n,@e,'active',0,@now,@now)",
                new (string, object?)[] { ("id", userId), ("n", name), ("e", InvitationService.NormalizeEmail(id.Email) ?? ""), ("now", now) }, ct);
            await _db.ExecuteAsync("INSERT INTO user_identities (provider, subject, user_id, created_at) VALUES (@p,@s,@u,@now)", new (string, object?)[] { ("p", id.Issuer), ("s", id.Subject), ("u", userId), ("now", now) }, ct);
        }
        var role = await _db.QueryAsync("SELECT 1 AS x FROM user_roles WHERE user_id=@u AND role='admin'", new (string, object?)[] { ("u", userId) }, ct);
        if (role.Count == 0) await _db.ExecuteAsync("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@u,'admin',NULL,@now)", new (string, object?)[] { ("u", userId), ("now", now) }, ct);
        await _db.ExecuteAsync("UPDATE users SET status='active', last_login_at=@now WHERE id=@u", new (string, object?)[] { ("u", userId), ("now", now) }, ct);
        return userId;
    }

    /// <summary>Ist der Benutzer weiterhin ein aktiver Administrator? Wird bei jedem Aufruf geprüft (Rolle gegen die Datenbank).</summary>
    public async Task<bool> IsActiveAdminAsync(string userId, CancellationToken ct = default) =>
        (await _db.QueryAsync("SELECT 1 AS x FROM user_roles r JOIN users u ON u.id=r.user_id WHERE r.user_id=@u AND r.role='admin' AND u.status='active'", new (string, object?)[] { ("u", userId) }, ct)).Count > 0;
}
