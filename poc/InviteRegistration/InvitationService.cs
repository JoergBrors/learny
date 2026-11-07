using System.Security.Cryptography;
using System.Text;
using LearnCards.Web.Data;

namespace InviteRegistration;

/// <summary>
/// Einladungspflichtige Registrierung (ADR 0042). Verwaltet Einladungen und legt Learny-Benutzer erst nach
/// validiertem Login und erfolgreicher Einladungsprüfung an. Kein Zugriff auf Anfrage-Daten: Profil und Inhalte
/// stammen ausschließlich aus dem gespeicherten Einladungseintrag.
/// </summary>
public sealed class InvitationService
{
    private readonly IDatabase _db;
    private readonly InvitationOptions _opt;

    /// <summary>Optionales Ereignisprotokoll (ADR 0046). Ohne Wert werden keine Ereignisse geschrieben.</summary>
    public InviteEvents? Events { get; set; }

    private Task Ev(string type, string? invitationId = null, string? userId = null, string? category = null, string? actor = null) =>
        Events is null ? Task.CompletedTask : Events.LogAsync(type, invitationId, userId, category, actor);

    /// <summary>Testnaht: wird vor den Schritten "create_user", "assign_profile", "grant_content" aufgerufen und darf Fehler auslösen.</summary>
    public Func<string, Task>? FaultInjector { get; set; }

    public InvitationService(IDatabase db, InvitationOptions options)
    {
        _db = db;
        _opt = options;
        if (options.TokenKey.Length < 32) throw new InvalidOperationException("TokenKey muss mindestens 32 Byte lang sein (fail-closed).");
        if (options.ValidityHours is < 1 or > 720) throw new InvalidOperationException("ValidityHours muss zwischen 1 und 720 liegen.");
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var u) || u.Query.Length > 0)
            throw new InvalidOperationException("BaseUrl ungültig.");
        if (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && u.IsLoopback))
            throw new InvalidOperationException("BaseUrl muss HTTPS verwenden (HTTP nur für localhost).");
    }

    public async Task InitAsync(CancellationToken ct = default)
    {
        foreach (var ddl in InviteSchema.Statements(_db.ProviderName)) await _db.ExecuteAsync(ddl, null, ct);
    }

    // ─── Verwaltung (nur Administratoren) ──────────────────────────────

    public Task<CreatedInvitation> CreateAsync(string actorUserId, string recipientEmail, string targetProfile,
        IReadOnlyCollection<string>? moduleIds = null, CancellationToken ct = default) =>
        CreateCoreAsync(actorUserId, recipientEmail, targetProfile, moduleIds, null, ct);

    /// <summary>Erstellt eine Einladung mit Verwaltungsangaben (Anzeigename, Beschreibung, interne Notiz) und eigener Gültigkeit (1–720 h).</summary>
    public Task<CreatedInvitation> CreateAsync(string actorUserId, string recipientEmail, string targetProfile,
        IReadOnlyCollection<string>? moduleIds, InvitationMeta meta, CancellationToken ct = default) =>
        CreateCoreAsync(actorUserId, recipientEmail, targetProfile, moduleIds, meta, ct);

    private async Task<CreatedInvitation> CreateCoreAsync(string actorUserId, string recipientEmail, string targetProfile,
        IReadOnlyCollection<string>? moduleIds, InvitationMeta? meta, CancellationToken ct)
    {
        await RequireAdminAsync(actorUserId, ct);
        meta?.Validate();
        if (!TargetProfiles.All.Contains(targetProfile, StringComparer.Ordinal))
            throw new ArgumentException("Zielprofil nicht erlaubt.");
        var email = NormalizeEmail(recipientEmail) ?? throw new ArgumentException("E-Mail-Adresse ungültig.");

        // Existiert schon ein Konto mit dieser Adresse, ist eine Einladung sinnlos (Einlösung würde mit IdentityAlreadyRegistered scheitern).
        if ((await _db.QueryAsync("SELECT 1 AS x FROM users WHERE LOWER(email)=@e", new (string, object?)[] { ("e", email) }, ct)).Count > 0)
            throw new AccountExistsException();

        var ids = (moduleIds ?? Array.Empty<string>()).Distinct().ToList();
        foreach (var m in ids)
        {
            var rows = await _db.QueryAsync("SELECT scope FROM poc_modules WHERE id=@id", new (string, object?)[] { ("id", m) }, ct);
            if (rows.Count == 0 || rows[0]["scope"] is not ("global" or "shared"))
                throw new ArgumentException("Nur vorhandene globale oder freigegebene Module sind zuweisbar.");
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));   // 256 Bit
        var invitationId = Guid.NewGuid().ToString();
        var now = _opt.Clock();
        var expires = now.AddHours(meta?.ValidityHours ?? _opt.ValidityHours);
        await _db.ExecuteAsync("""
            INSERT INTO invitations (invitation_id, recipient_email, token_hash, target_profile, status, created_by, created_at, expires_at)
            VALUES (@id, @email, @hash, @profile, 'pending', @by, @now, @exp)
            """, new (string, object?)[]
        {
            ("id", invitationId), ("email", email), ("hash", Hash(token)), ("profile", targetProfile),
            ("by", actorUserId), ("now", now), ("exp", expires),
        }, ct);
        foreach (var m in ids)
            await _db.ExecuteAsync("INSERT INTO invitation_grants (invitation_id, module_id) VALUES (@i, @m)",
                new (string, object?)[] { ("i", invitationId), ("m", m) }, ct);
        await _db.ExecuteAsync("INSERT INTO invitation_deliveries (invitation_id, status, updated_at) VALUES (@i, 'pending', @now)",
            new (string, object?)[] { ("i", invitationId), ("now", now) }, ct);
        if (meta is { HasText: true })
            await _db.ExecuteAsync("INSERT INTO invitation_meta (invitation_id, display_name, description, note) VALUES (@i,@d,@x,@n)",
                new (string, object?)[] { ("i", invitationId), ("d", meta.DisplayName), ("x", meta.Description), ("n", meta.Note) }, ct);
        await AuditAsync(invitationId, "created", "ok", ct);
        await Ev(EventTypes.Created, invitationId, null, null, actorUserId);
        return new CreatedInvitation(invitationId, _opt.BaseUrl.TrimEnd('/') + "/invite/" + token, expires, token);
    }

    public async Task<bool> RevokeAsync(string actorUserId, string invitationId, CancellationToken ct = default)
    {
        await RequireAdminAsync(actorUserId, ct);
        var n = await _db.ExecuteAsync("""
            UPDATE invitations SET status='revoked', revoked_at=@now
            WHERE invitation_id=@id AND status IN ('pending','expired')
            """, new (string, object?)[] { ("id", invitationId), ("now", _opt.Clock()) }, ct);
        await AuditAsync(invitationId, "revoked", n == 1 ? "ok" : "not_revocable", ct);
        if (n == 1) await Ev(EventTypes.Revoked, invitationId, null, null, actorUserId);
        return n == 1;
    }

    /// <summary>Kontrollierte Neuausstellung des Links derselben Einladung (Rotation): der alte Link wird sofort ungültig, es entsteht keine zweite Einladung.
    /// Die Bedingung im UPDATE macht die Rotation gegenüber paralleler Einlösung/Widerruf atomar (nur im Status pending, unverändert ablaufend).</summary>
    public async Task<CreatedInvitation?> RotateAsync(string actorUserId, string invitationId, CancellationToken ct = default)
    {
        await RequireAdminAsync(actorUserId, ct);
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = _opt.Clock();
        var n = await _db.ExecuteAsync("""
            UPDATE invitations SET token_hash=@hash, token_version=token_version+1
            WHERE invitation_id=@id AND status='pending' AND revoked_at IS NULL AND expires_at>@now
            """, new (string, object?)[] { ("id", invitationId), ("hash", Hash(token)), ("now", now) }, ct);
        if (n != 1) { await AuditAsync(invitationId, "rotate_failed", "not_rotatable", ct); return null; }
        await _db.ExecuteAsync("UPDATE invitation_deliveries SET status='pending', lease_until=NULL, last_error_code=NULL, attempts=0, updated_at=@now WHERE invitation_id=@i",
            new (string, object?)[] { ("i", invitationId), ("now", now) }, ct);
        await AuditAsync(invitationId, "rotated", "ok", ct);
        await Ev(EventTypes.Rotated, invitationId, null, null, actorUserId);
        var rows = await _db.QueryAsync("SELECT expires_at FROM invitations WHERE invitation_id=@i", new (string, object?)[] { ("i", invitationId) }, ct);
        return new CreatedInvitation(invitationId, _opt.BaseUrl.TrimEnd('/') + "/invite/" + token, DbValue.ToDateTime(rows[0]["expires_at"]), token);
    }

    /// <summary>Nichtgeheime Angaben für Versand und Verwaltung (nie Hash oder Token).</summary>
    public async Task<InvitationInfo?> GetInfoAsync(string invitationId, CancellationToken ct = default)
    {
        var rows = await _db.QueryAsync("SELECT * FROM invitations WHERE invitation_id=@i", new (string, object?)[] { ("i", invitationId) }, ct);
        if (rows.Count == 0) return null;
        var r = rows[0];
        var meta = (await _db.QueryAsync("SELECT display_name FROM invitation_meta WHERE invitation_id=@i", new (string, object?)[] { ("i", invitationId) }, ct)).FirstOrDefault();
        return new InvitationInfo(invitationId, r["recipient_email"]!, r["target_profile"]!, EffectiveStatus(r), DbValue.ToDateTime(r["expires_at"]), DbValue.ToInt(r["token_version"]), meta?["display_name"]);
    }

    /// <summary>Einladungs-ID zu einem Token (nur wenn der Token bekannt ist). Für Ereignisse; gibt nie Hash oder Token heraus.</summary>
    public async Task<string?> GetInvitationIdAsync(string? token, CancellationToken ct = default) => (await FindAsync(token, ct))?["invitation_id"];

    /// <summary>Interne Benutzer-ID zu einer bereits registrierten Identität (Issuer + Subject), sofern aktiv und mit Profil. Sonst null (ohne Einladung keine Freischaltung).</summary>
    public async Task<string?> FindActiveUserByIdentityAsync(string issuer, string subject, CancellationToken ct = default)
    {
        var rows = await _db.QueryAsync("""
            SELECT i.user_id AS user_id FROM user_identities i
            JOIN users u ON u.id = i.user_id AND u.status='active'
            JOIN user_profiles p ON p.user_id = i.user_id
            WHERE i.provider=@p AND i.subject=@s
            """, new (string, object?)[] { ("p", issuer), ("s", subject) }, ct);
        return rows.Count == 1 ? rows[0]["user_id"] : null;
    }

    /// <summary>Gehört der Token zum aktuellen Stand der Einladung (nach Rotation sind alte Token nicht mehr aktuell)?</summary>
    public async Task<bool> IsCurrentTokenAsync(string invitationId, string token, CancellationToken ct = default)
    {
        var inv = await FindAsync(token, ct);
        return inv is not null && inv["invitation_id"] == invitationId;
    }

    /// <summary>Bekannter Empfänger für Anmeldecodes: gültige Einladung (pending, nicht abgelaufen, nicht widerrufen) oder aktiver Learny-Benutzer mit dieser Adresse. Nur lesend.</summary>
    public async Task<bool> IsKnownRecipientAsync(string email, CancellationToken ct = default)
    {
        var e = NormalizeEmail(email);
        if (e is null) return false;
        var inv = await _db.QueryAsync("SELECT 1 AS x FROM invitations WHERE recipient_email=@e AND status='pending' AND revoked_at IS NULL AND expires_at>@now",
            new (string, object?)[] { ("e", e), ("now", _opt.Clock()) }, ct);
        if (inv.Count > 0) return true;
        var usr = await _db.QueryAsync("SELECT 1 AS x FROM users WHERE lower(email)=@e AND status='active'", new (string, object?)[] { ("e", e) }, ct);
        return usr.Count > 0;
    }

    // ─── Vorprüfung (vor dem Redirect zu Entra; verbraucht nichts) ─────

    public async Task<bool> IsRedeemableAsync(string? token, CancellationToken ct = default)
    {
        var inv = await FindAsync(token, ct);
        return inv is not null && EffectiveStatus(inv) == InvitationStatus.Pending;
    }

    // ─── Einlösung (nach validiertem OIDC-Token) ───────────────────────

    public async Task<RedeemResult> RedeemAsync(string? token, VerifiedIdentity identity, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return await FailAsync(null, RedeemOutcome.MissingToken, ct);
        if (string.IsNullOrWhiteSpace(identity.Issuer) || string.IsNullOrWhiteSpace(identity.Subject))
            return await FailAsync(null, RedeemOutcome.InvalidIdentity, ct);

        var inv = await FindAsync(token, ct);
        if (inv is null) return await FailAsync(null, RedeemOutcome.UnknownToken, ct);
        var id = inv["invitation_id"]!;

        switch (EffectiveStatus(inv))
        {
            case InvitationStatus.Revoked: return await FailAsync(id, RedeemOutcome.Revoked, ct);
            case InvitationStatus.Accepted: return await FailAsync(id, RedeemOutcome.AlreadyUsed, ct);
            case InvitationStatus.Expired: return await FailAsync(id, RedeemOutcome.Expired, ct);
        }

        // Bestehende Identität → Einladung nur für Neuregistrierungen.
        var existing = await _db.QueryAsync("SELECT user_id FROM user_identities WHERE provider=@p AND subject=@s",
            new (string, object?)[] { ("p", identity.Issuer), ("s", identity.Subject) }, ct);
        if (existing.Count > 0) return await FailAsync(id, RedeemOutcome.IdentityAlreadyRegistered, ct);

        // E-Mail-Bindung: nur bei bestätigter Adresse; gleiche E-Mail allein verknüpft nie mit einem Konto.
        if (!identity.EmailVerified) return await FailAsync(id, RedeemOutcome.EmailNotVerified, ct, identity.ClaimHint);
        var claimed = NormalizeEmail(identity.Email);
        if (claimed is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(claimed), Encoding.UTF8.GetBytes(inv["recipient_email"]!)))
            return await FailAsync(id, RedeemOutcome.EmailMismatch, ct);

        // Atomarer Verbrauch: genau eine Anweisung entscheidet, wer gewinnt (auch bei parallelen Prozessen).
        var userId = Guid.NewGuid().ToString();
        var now = _opt.Clock();
        var claim = await _db.ExecuteAsync("""
            UPDATE invitations SET status='accepted', accepted_at=@now, accepted_user_id=@uid
            WHERE invitation_id=@id AND token_hash=@hash AND status='pending' AND revoked_at IS NULL AND expires_at>@now
            """, new (string, object?)[] { ("id", id), ("uid", userId), ("now", now), ("hash", inv["token_hash"]) }, ct);
        if (claim != 1) return await FailAsync(id, RedeemOutcome.AlreadyUsed, ct);

        var profile = inv["target_profile"]!;
        try
        {
            await Fault("create_user");
            await _db.ExecuteAsync("""
                INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at)
                VALUES (@id, @name, @email, 'active', 0, @now, @now)
                """, new (string, object?)[] { ("id", userId), ("name", await InvitedNameAsync(id, claimed, ct)), ("email", claimed), ("now", now) }, ct);
            await _db.ExecuteAsync("INSERT INTO user_identities (provider, subject, user_id, created_at) VALUES (@p, @s, @u, @now)",
                new (string, object?)[] { ("p", identity.Issuer), ("s", identity.Subject), ("u", userId), ("now", now) }, ct);

            if (identity.ObjectId is not null) await RecordObjectIdAsync(userId, identity.ObjectId, ct);

            await Fault("assign_profile");
            await _db.ExecuteAsync("INSERT INTO user_profiles (user_id, profile, source_invitation_id, created_at) VALUES (@u, @p, @i, @now)",
                new (string, object?)[] { ("u", userId), ("p", profile), ("i", id), ("now", now) }, ct);

            await Fault("grant_content");
            await _db.ExecuteAsync("""
                INSERT INTO module_grants (user_id, module_id, source_invitation_id, granted_at)
                SELECT @u, module_id, invitation_id, @now FROM invitation_grants WHERE invitation_id=@i
                """, new (string, object?)[] { ("u", userId), ("i", id), ("now", now) }, ct);
        }
        catch (Exception)
        {
            await CompensateAsync(id, userId, ct);
            return await FailAsync(id, RedeemOutcome.ProvisioningFailed, ct);
        }

        await AuditAsync(id, "redeemed", "ok", ct);
        await Ev(EventTypes.Accepted, id, userId);
        await Ev(EventTypes.UserCreated, id, userId);
        await Ev(EventTypes.ProfileAssigned, id, userId, profile);
        var granted = await _db.QueryAsync("SELECT COUNT(*) AS n FROM module_grants WHERE user_id=@u AND source_invitation_id=@i", new (string, object?)[] { ("u", userId), ("i", id) }, ct);
        if (DbValue.ToInt(granted[0]["n"]) > 0) await Ev(EventTypes.ContentAssigned, id, userId, "invitation");
        return new RedeemResult(RedeemOutcome.Success, userId, profile);
    }

    // ─── Hilfen ────────────────────────────────────────────────────────

    /// <summary>Teilfehler: angelegte Zeilen entfernen und den Verbrauch zurücknehmen. Schlägt auch das fehl, bleibt die Einladung
    /// "accepted" (fail-closed): ein halb angelegter Benutzer ohne Profil hat keinen Zugriff (siehe <see cref="ContentAccess"/>).</summary>
    private async Task CompensateAsync(string invitationId, string userId, CancellationToken ct)
    {
        try
        {
            await _db.ExecuteAsync("DELETE FROM users WHERE id=@u", new (string, object?)[] { ("u", userId) }, ct);   // Kaskade: Identität, Profil, Rechte
            await _db.ExecuteAsync("""
                UPDATE invitations SET status='pending', accepted_at=NULL, accepted_user_id=NULL
                WHERE invitation_id=@i AND accepted_user_id=@u
                """, new (string, object?)[] { ("i", invitationId), ("u", userId) }, ct);
        }
        catch (Exception) { /* fail-closed: Einladung bleibt verbraucht, Administrator stellt neu aus */ }
    }

    private Task Fault(string step) => FaultInjector?.Invoke(step) ?? Task.CompletedTask;

    private async Task<Dictionary<string, string?>?> FindAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return null;
        var rows = await _db.QueryAsync("SELECT * FROM invitations WHERE token_hash=@h", new (string, object?)[] { ("h", Hash(token)) }, ct);
        return rows.Count == 1 ? rows[0] : null;
    }

    private string EffectiveStatus(Dictionary<string, string?> inv)
    {
        var status = inv["status"]!;
        if (status == InvitationStatus.Pending && DbValue.ToDateTime(inv["expires_at"]) <= _opt.Clock()) return InvitationStatus.Expired;
        return status;
    }

    private string Hash(string token)
    {
        using var h = new HMACSHA256(_opt.TokenKey);
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }

    public async Task RequireAdminAsync(string userId, CancellationToken ct)
    {
        var rows = await _db.QueryAsync("SELECT 1 AS x FROM user_roles WHERE user_id=@u AND role='admin'", new (string, object?)[] { ("u", userId) }, ct);
        if (rows.Count == 0) throw new UnauthorizedAccessException("Nur Administratoren dürfen Einladungen verwalten.");
    }

    /// <summary>Hält die Entra-Objekt-ID des Benutzers fest (einmalig; eine bereits einem anderen Benutzer zugeordnete ID wird nie umgehängt).</summary>
    public async Task RecordObjectIdAsync(string userId, string objectId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(objectId, out var g)) return;
        var oid = g.ToString();
        if ((await _db.QueryAsync("SELECT 1 AS x FROM user_entra_oids WHERE oid=@o", new (string, object?)[] { ("o", oid) }, ct)).Count > 0) return;
        await _db.ExecuteAsync("INSERT INTO user_entra_oids (oid, user_id, created_at) VALUES (@o,@u,@now)", new (string, object?)[] { ("o", oid), ("u", userId), ("now", _opt.Clock()) }, ct);
    }

    private async Task<RedeemResult> FailAsync(string? invitationId, RedeemOutcome outcome, CancellationToken ct, string? hint = null)
    {
        await AuditAsync(invitationId, "redeem_failed", outcome.ToString(), ct);
        await Ev(EventTypes.RegistrationRejected, invitationId, null, hint is null ? outcome.ToString() : outcome + ":" + hint);
        return new RedeemResult(outcome);
    }

    private Task AuditAsync(string? invitationId, string ev, string reason, CancellationToken ct) =>
        _db.ExecuteAsync("INSERT INTO invitation_audit (id, invitation_id, event, reason, at) VALUES (@id, @i, @e, @r, @now)",
            new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("i", invitationId), ("e", ev), ("r", reason), ("now", _opt.Clock()) }, ct);

    public static string? NormalizeEmail(string? email)
    {
        var e = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(e) || e.Length > 254) return null;
        var at = e.IndexOf('@');
        return at > 0 && at == e.LastIndexOf('@') && at < e.Length - 3 && !e.Any(char.IsWhiteSpace) ? e : null;
    }

    /// <summary>Anzeigename kommt nur aus der Einladung (Administratoreingabe), nie aus dem Token; ohne Angabe der Teil der Adresse vor dem @.</summary>
    private async Task<string> InvitedNameAsync(string invitationId, string email, CancellationToken ct)
    {
        var row = (await _db.QueryAsync("SELECT display_name FROM invitation_meta WHERE invitation_id=@i", new (string, object?)[] { ("i", invitationId) }, ct)).FirstOrDefault();
        var name = row?["display_name"]?.Trim();
        return string.IsNullOrEmpty(name) ? DisplayName(email) : name;
    }

    private static string DisplayName(string email) => email[..email.IndexOf('@')];

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Serverseitige Inhaltsprüfung: ohne Profil und ohne ausdrückliche Zuweisung kein Zugriff (fail-closed).</summary>
public sealed class ContentAccess
{
    private readonly IDatabase _db;
    public ContentAccess(IDatabase db) => _db = db;

    public async Task<bool> CanReadModuleAsync(string userId, string moduleId, CancellationToken ct = default)
    {
        var rows = await _db.QueryAsync("""
            SELECT 1 AS x FROM user_profiles p
            JOIN users u ON u.id = p.user_id AND u.status='active'
            JOIN module_grants g ON g.user_id = p.user_id
            JOIN poc_modules m ON m.id = g.module_id
            WHERE p.user_id=@u AND g.module_id=@m AND m.scope IN ('global','shared')
            """, new (string, object?)[] { ("u", userId), ("m", moduleId) }, ct);
        return rows.Count > 0;
    }

    public async Task<List<string>> ListReadableModulesAsync(string userId, CancellationToken ct = default)
    {
        var rows = await _db.QueryAsync("""
            SELECT m.id AS id FROM user_profiles p
            JOIN users u ON u.id = p.user_id AND u.status='active'
            JOIN module_grants g ON g.user_id = p.user_id
            JOIN poc_modules m ON m.id = g.module_id
            WHERE p.user_id=@u AND m.scope IN ('global','shared') ORDER BY m.name
            """, new (string, object?)[] { ("u", userId) }, ct);
        return rows.Select(r => r["id"]!).ToList();
    }
}
