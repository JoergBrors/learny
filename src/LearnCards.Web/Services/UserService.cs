using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services;

/// <summary>
/// Benutzerverwaltung (ADR 0011): Zuordnung externer Identitäten zu internen Benutzer-IDs, Rollen, Status, Audit.
/// Alle administrativen Methoden prüfen die Rolle des Aufrufers selbst gegen die Datenbank
/// (nicht gegen Cookie-Claims) und werfen <see cref="UnauthorizedAccessException"/>.
/// </summary>
public class UserService
{
    /// <summary>Provider-Name der lokalen Entwicklungsanmeldung.</summary>
    public const string DevProvider = "dev";
    public const string DevDefaultSubject = "local-dev-user";

    /// <summary>Tabellen, deren Spalte user_sub vor ADR 0011 die externe Subject-ID enthielt.</summary>
    private static readonly string[] LegacyUserSubTables = { "user_preferences", "user_card_states", "chat_history", "quiz_results" };

    private readonly IDatabase _db;
    private readonly AppConfig _cfg;

    public UserService(IDatabase db, AppConfig cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    // ─── Anmeldung ──────────────────────────────────────────────────────────

    /// <summary>
    /// Ordnet eine externe Identität einem internen Benutzer zu (legt ihn beim ersten Login an), aktualisiert
    /// Login-Zeit/E-Mail, vergibt Bootstrap-Adminrechte und überführt Altdaten dieser Identität
    /// (user_sub = externes Subject) auf die interne Benutzer-ID.
    /// </summary>
    public async Task<AppUser> SignInAsync(string provider, string subject, string displayName, string email, IReadOnlyCollection<string>? idpRoles = null)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("Provider und Subject sind erforderlich.");

        var userId = await FindUserIdByIdentityAsync(provider, subject);
        var created = false;
        if (userId is null)
        {
            userId = Guid.NewGuid().ToString();
            var now = DateTime.UtcNow;
            await _db.ExecuteAsync("""
                INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at)
                VALUES (@id, @display_name, @email, 'active', 0, @now, @now)
                """, new (string, object?)[]
            {
                ("id", userId), ("display_name", CleanName(displayName, "Benutzer")), ("email", (email ?? "").Trim()), ("now", now),
            });
            try
            {
                await _db.ExecuteAsync("""
                    INSERT INTO user_identities (provider, subject, user_id, created_at)
                    VALUES (@provider, @subject, @user_id, @now)
                    """, new (string, object?)[] { ("provider", provider), ("subject", subject), ("user_id", userId), ("now", now) });
                await GrantRoleCoreAsync(userId, Roles.User, null);
                created = true;
            }
            catch
            {
                // Paralleler Erstlogin: Identität existiert inzwischen → angelegten Benutzer verwerfen und den vorhandenen nehmen.
                await _db.ExecuteAsync("DELETE FROM users WHERE id = @id", new[] { ("id", (object?)userId) });
                userId = await FindUserIdByIdentityAsync(provider, subject) ?? throw new InvalidOperationException("Benutzeranlage fehlgeschlagen.");
            }
        }

        await _db.ExecuteAsync("UPDATE users SET last_login_at=@now, email=@email WHERE id=@id",
            new (string, object?)[] { ("now", DateTime.UtcNow), ("email", (email ?? "").Trim()), ("id", userId) });

        if (created) await WriteAuditAsync(userId, "user.created", userId, $"provider={provider}");

        // Entra-ID-App-Rollen (Claim "roles") abgleichen; null = Provider liefert keine Rollen (z. B. Dev-Login ohne Rollen)
        if (idpRoles is not null) await SyncIdpRolesAsync(userId, idpRoles);

        if (IsBootstrapAdmin(provider, subject) && !(await GetRolesAsync(userId)).Contains(Roles.Admin))
        {
            await GrantRoleCoreAsync(userId, Roles.Admin, "bootstrap");
            await WriteAuditAsync(null, "role.grant.bootstrap", userId, Roles.Admin);
        }

        await MigrateLegacyDataAsync(userId, subject);

        return await GetAsync(userId) ?? throw new InvalidOperationException("Benutzer nicht gefunden.");
    }

    /// <summary>
    /// Gleicht die vom Identity Provider gelieferten App-Rollen mit den internen Rollen ab (ADR 0012).
    /// Nur Rollen aus <see cref="AppConfig.RoleMap"/> werden verwaltet. Vergeben wird mit granted_by = 'idp'; entzogen wird nur, was
    /// ebenfalls vom IdP vergeben wurde — manuell (Admin-UI) oder per Bootstrap vergebene Rollen bleiben unangetastet.
    /// </summary>
    private async Task SyncIdpRolesAsync(string userId, IReadOnlyCollection<string> idpRoles)
    {
        var desired = idpRoles
            .Select(r => _cfg.RoleMap.TryGetValue(r, out var mapped) ? mapped : null)
            .Where(m => m is not null).Select(m => m!).ToHashSet();

        var rows = await _db.QueryAsync("SELECT role, granted_by FROM user_roles WHERE user_id=@u", new[] { ("u", (object?)userId) });
        var current = rows.ToDictionary(r => r["role"] ?? "", r => r.GetValueOrDefault("granted_by"));

        foreach (var role in _cfg.RoleMap.Values.Distinct())
        {
            var has = current.ContainsKey(role);
            if (desired.Contains(role) && !has)
            {
                await GrantRoleCoreAsync(userId, role, "idp");
                await WriteAuditAsync(null, "role.grant.idp", userId, role);
            }
            else if (!desired.Contains(role) && has && current[role] == "idp")
            {
                await _db.ExecuteAsync("DELETE FROM user_roles WHERE user_id=@u AND role=@r",
                    new (string, object?)[] { ("u", userId), ("r", role) });
                await WriteAuditAsync(null, "role.revoke.idp", userId, role);
            }
        }
    }

    private bool IsBootstrapAdmin(string provider, string subject) =>
        _cfg.AdminSubjects.Contains(subject)
        || (provider == DevProvider && _cfg.AuthMode == "dev" && subject == DevDefaultSubject);

    /// <summary>
    /// Setzt user_sub von Altdaten (externes Subject) auf die interne ID. Wiederholbar; erst nach vollständigem
    /// Durchlauf wird legacy_migrated gesetzt. Es werden nur Daten genau dieses Subjects berührt.
    /// </summary>
    private async Task MigrateLegacyDataAsync(string userId, string subject)
    {
        var rows = await _db.QueryAsync("SELECT legacy_migrated FROM users WHERE id=@id", new[] { ("id", (object?)userId) });
        if (DbValue.ToBool(rows.FirstOrDefault()?.GetValueOrDefault("legacy_migrated"))) return;

        foreach (var table in LegacyUserSubTables)
            await _db.ExecuteAsync($"UPDATE {table} SET user_sub=@new WHERE user_sub=@old",
                new (string, object?)[] { ("new", userId), ("old", subject) });

        await _db.ExecuteAsync("UPDATE users SET legacy_migrated=1 WHERE id=@id", new[] { ("id", (object?)userId) });
    }

    private async Task<string?> FindUserIdByIdentityAsync(string provider, string subject)
    {
        var rows = await _db.QueryAsync("SELECT user_id FROM user_identities WHERE provider=@p AND subject=@s",
            new (string, object?)[] { ("p", provider), ("s", subject) });
        return rows.FirstOrDefault()?.GetValueOrDefault("user_id");
    }

    // ─── Lesen ──────────────────────────────────────────────────────────────

    public async Task<AppUser?> GetAsync(string? userId)
    {
        if (!IsGuid(userId)) return null;
        var rows = await _db.QueryAsync("""
            SELECT id, display_name, email, status, created_at, last_login_at FROM users WHERE id=@id
            """, new[] { ("id", (object?)userId) });
        if (rows.Count == 0) return null;
        var u = MapUser(rows[0]);
        u.Roles = await GetRolesAsync(u.Id);
        return u;
    }

    public async Task<List<AppUser>> ListAsync()
    {
        var users = (await _db.QueryAsync("""
            SELECT id, display_name, email, status, created_at, last_login_at FROM users ORDER BY display_name, created_at
            """)).Select(MapUser).ToList();
        var roles = await _db.QueryAsync("SELECT user_id, role FROM user_roles");
        foreach (var u in users)
            u.Roles = roles.Where(r => string.Equals(r["user_id"], u.Id, StringComparison.OrdinalIgnoreCase))
                .Select(r => r["role"] ?? "").OrderBy(r => r).ToList();
        return users;
    }

    public async Task<List<UserIdentityInfo>> GetIdentitiesAsync(string userId)
    {
        var rows = await _db.QueryAsync("SELECT provider, subject FROM user_identities WHERE user_id=@u ORDER BY created_at",
            new[] { ("u", (object?)userId) });
        return rows.Select(r => new UserIdentityInfo { Provider = r["provider"] ?? "", Subject = r["subject"] ?? "" }).ToList();
    }

    private async Task<List<string>> GetRolesAsync(string userId) =>
        (await _db.QueryAsync("SELECT role FROM user_roles WHERE user_id=@u ORDER BY role", new[] { ("u", (object?)userId) }))
            .Select(r => r["role"] ?? "").Where(r => r.Length > 0).ToList();

    // ─── Autorisierung ──────────────────────────────────────────────────────

    /// <summary>Prüft serverseitig gegen die Datenbank, dass der Aufrufer aktiv ist und die Rolle besitzt.</summary>
    public async Task<AppUser> RequireRoleAsync(string? actorUserId, string role)
    {
        var actor = await GetAsync(actorUserId);
        if (actor is null || !actor.IsActive || !actor.Roles.Contains(role))
            throw new UnauthorizedAccessException($"Rolle '{role}' erforderlich.");
        return actor;
    }

    // ─── Änderungen ─────────────────────────────────────────────────────────

    /// <summary>Eigener Anzeigename — die einzige Profiländerung, die ein Benutzer selbst vornehmen darf.</summary>
    public async Task SetDisplayNameAsync(string userId, string displayName)
    {
        var name = (displayName ?? "").Trim();
        if (name.Length is 0 or > 80) throw new ArgumentException("Der Anzeigename muss 1 bis 80 Zeichen lang sein.");
        await _db.ExecuteAsync("UPDATE users SET display_name=@n WHERE id=@id",
            new (string, object?)[] { ("n", name), ("id", userId) });
    }

    /// <summary>Ersetzt die Rollen eines Benutzers (nur Administrator). "user" bleibt immer erhalten.</summary>
    public async Task SetRolesAsync(string? actorUserId, string targetUserId, IEnumerable<string> roles)
    {
        var actor = await RequireRoleAsync(actorUserId, Roles.Admin);
        var target = await GetAsync(targetUserId) ?? throw new KeyNotFoundException("Benutzer nicht gefunden.");

        var wanted = roles.Select(r => r.Trim().ToLowerInvariant()).Where(r => r.Length > 0).ToHashSet();
        if (wanted.Any(r => !Roles.All.Contains(r))) throw new ArgumentException("Unbekannte Rolle.");
        wanted.Add(Roles.User);

        if (target.IsAdmin && !wanted.Contains(Roles.Admin) && await CountActiveAdminsAsync() <= 1)
            throw new InvalidOperationException("Der letzte aktive Administrator kann seine Rolle nicht verlieren.");

        foreach (var r in wanted.Except(target.Roles))
        {
            await GrantRoleCoreAsync(target.Id, r, actor.Id);
            await WriteAuditAsync(actor.Id, "role.grant", target.Id, r);
        }
        foreach (var r in target.Roles.Except(wanted))
        {
            await _db.ExecuteAsync("DELETE FROM user_roles WHERE user_id=@u AND role=@r",
                new (string, object?)[] { ("u", target.Id), ("r", r) });
            await WriteAuditAsync(actor.Id, "role.revoke", target.Id, r);
        }
    }

    /// <summary>Sperrt/entsperrt einen Benutzer (nur Administrator). Sich selbst oder den letzten Admin zu sperren ist nicht erlaubt.</summary>
    public async Task SetStatusAsync(string? actorUserId, string targetUserId, bool active)
    {
        var actor = await RequireRoleAsync(actorUserId, Roles.Admin);
        var target = await GetAsync(targetUserId) ?? throw new KeyNotFoundException("Benutzer nicht gefunden.");
        if (!active && string.Equals(actor.Id, target.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Der eigene Zugang kann nicht gesperrt werden.");
        if (!active && target.IsAdmin && await CountActiveAdminsAsync() <= 1)
            throw new InvalidOperationException("Der letzte aktive Administrator kann nicht gesperrt werden.");

        await _db.ExecuteAsync("UPDATE users SET status=@s WHERE id=@id",
            new (string, object?)[] { ("s", active ? "active" : "disabled"), ("id", target.Id) });
        await WriteAuditAsync(actor.Id, active ? "user.enable" : "user.disable", target.Id, "");
    }

    public async Task<List<AuditEntry>> ListAuditAsync(string? actorUserId, int limit = 100)
    {
        await RequireRoleAsync(actorUserId, Roles.Admin);
        var rows = await _db.QueryAsync($"""
            SELECT id, actor_user_id, action, target, detail, created_at FROM audit_log
            ORDER BY created_at DESC LIMIT {Math.Clamp(limit, 1, 500)}
            """);
        return rows.Select(r => new AuditEntry
        {
            Id = r["id"] ?? "",
            ActorUserId = r.GetValueOrDefault("actor_user_id"),
            Action = r["action"] ?? "",
            Target = r["target"] ?? "",
            Detail = r["detail"] ?? "",
            CreatedAt = DbValue.ToDateTime(r["created_at"]),
        }).ToList();
    }

    public Task WriteAuditAsync(string? actorUserId, string action, string target, string detail) =>
        _db.ExecuteAsync("""
            INSERT INTO audit_log (id, actor_user_id, action, target, detail, created_at)
            VALUES (@id, @actor, @action, @target, @detail, @now)
            """, new (string, object?)[]
        {
            ("id", Guid.NewGuid().ToString()), ("actor", actorUserId), ("action", action),
            ("target", target), ("detail", detail), ("now", DateTime.UtcNow),
        });

    // ─── Hilfen ─────────────────────────────────────────────────────────────

    private Task GrantRoleCoreAsync(string userId, string role, string? grantedBy) =>
        _db.ExecuteAsync("""
            INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@u, @r, @by, @now)
            """, new (string, object?)[] { ("u", userId), ("r", role), ("by", grantedBy), ("now", DateTime.UtcNow) });

    private async Task<int> CountActiveAdminsAsync()
    {
        var rows = await _db.QueryAsync("""
            SELECT COUNT(*) AS n FROM user_roles r JOIN users u ON u.id = r.user_id
            WHERE r.role = 'admin' AND u.status = 'active'
            """);
        return DbValue.ToInt(rows.FirstOrDefault()?.GetValueOrDefault("n"));
    }

    private static AppUser MapUser(Dictionary<string, string?> r) => new()
    {
        Id = r["id"] ?? "",
        DisplayName = r.GetValueOrDefault("display_name") ?? "",
        Email = r.GetValueOrDefault("email") ?? "",
        Status = r.GetValueOrDefault("status") ?? "active",
        CreatedAt = DbValue.ToDateTime(r.GetValueOrDefault("created_at")),
        LastLoginAt = DbValue.ToDateTime(r.GetValueOrDefault("last_login_at")),
    };

    private static string CleanName(string? name, string fallback)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) n = fallback;
        return n.Length > 80 ? n[..80] : n;
    }

    /// <summary>IDs sind UUIDs; ungültige Werte werden früh abgewiesen (PostgreSQL würde sonst einen Typfehler werfen).</summary>
    public static bool IsGuid(string? value) => Guid.TryParse(value, out _);
}
