using System.Security.Cryptography;
using System.Text;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services;

/// <summary>
/// Einladungspflichtige Registrierung externer Lernender (ADR 0042/0043/0049). Verwaltet Einladungen und legt Learny-Benutzer <b>erst</b> nach validiertem Login und
/// erfolgreicher Einladungsprüfung an. Profil und Lernrechte stammen ausschließlich aus dem gespeicherten Einladungsdatensatz, nie aus Anfragedaten oder Token-Claims.
/// Die Einlösung ist eine einzige atomare Datenbanktransaktion (<see cref="IDatabase.ExecuteBatchAsync"/>): Anspruch auf die Einladung, Benutzer, Identität, Rolle „user“,
/// Profil, Modulzuweisungen, Themenzuweisungen und Ereignisse entstehen vollständig oder gar nicht — es gibt keine Kompensation und keinen halb angelegten Benutzer.
/// Verwaltungsmethoden prüfen die Rolle des Aufrufers selbst gegen die Datenbank.
/// </summary>
public sealed class InvitationService
{
    private readonly IDatabase _db;
    private readonly UserService _users;
    private readonly InviteSettings _s;
    private readonly TopicService? _topics;
    public InviteEvents Events { get; }
    public Func<DateTime> Clock { get; private set; } = () => DateTime.UtcNow;

    /// <summary>true, wenn Schlüssel und öffentliche Adresse für Einladungslinks konfiguriert sind.</summary>
    public bool Enabled => _s.Enabled;

    /// <summary>Konstruktor für die Abhängigkeitsinjektion.</summary>
    public InvitationService(IDatabase db, UserService users, AppConfig cfg, InviteEvents events, TopicService? topics = null)
        : this(db, users, cfg.Invitations, events, topics, 0) { }

    private InvitationService(IDatabase db, UserService users, InviteSettings settings, InviteEvents events, TopicService? topics, int _)
    {
        _db = db; _users = users; _s = settings; Events = events; _topics = topics;
    }

    /// <summary>Erzeugt den Dienst mit expliziten Einstellungen (Tests).</summary>
    public static InvitationService Create(IDatabase db, UserService users, InviteSettings settings, InviteEvents events, TopicService? topics = null) =>
        new(db, users, settings, events, topics, 0);

    /// <summary>Testnaht: gemeinsame Uhr für Dienst und Ereignisse.</summary>
    public void UseClock(Func<DateTime> clock) { Clock = clock; Events.Clock = clock; }

    // ─── Verwaltung (nur Administratoren) ──────────────────────────────

    /// <summary>
    /// Erstellt eine Einladung für ein externes Lernprofil. Module müssen aktive globale Module sein, Themen müssen für den Akteur zuweisbar sein; Themenzuweisungen
    /// werden an eine <b>konkrete Version</b> gebunden. Es entsteht nie eine Einladung für eine Adresse mit vorhandenem Konto und nie eine Einladung für Administrator-,
    /// Auditor- oder Themen-Admin-Rollen. Der Link enthält den Token, in der Datenbank steht nur dessen HMAC.
    /// </summary>
    public async Task<CreatedInvitation> CreateAsync(string? actorUserId, string recipientEmail, string profile, IReadOnlyCollection<string>? moduleIds = null,
        IReadOnlyCollection<InvitationTopicGrant>? topicGrants = null, InvitationMeta? meta = null, CancellationToken ct = default)
    {
        var actor = await _users.RequireRoleAsync(actorUserId, Roles.Admin);
        if (!Enabled) throw new InvalidOperationException("Einladungen sind nicht konfiguriert (INVITE_TOKEN_KEY und PUBLIC_BASE_URL).");
        meta?.Validate();
        if (!LearnerProfiles.IsExternal(profile)) throw new ArgumentException("Zielprofil nicht erlaubt.");
        var email = NormalizeEmail(recipientEmail) ?? throw new ArgumentException("E-Mail-Adresse ungültig.");

        // Existiert schon ein Konto mit dieser Adresse, ist eine Einladung sinnlos und eine Verknüpfung über die Adresse ausdrücklich ausgeschlossen.
        if ((await _db.QueryAsync("SELECT 1 AS x FROM users WHERE LOWER(email)=@e", new (string, object?)[] { ("e", email) }, ct)).Count > 0)
            throw new AccountExistsException();

        var modules = (moduleIds ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var m in modules)
        {
            var rows = UserService.IsGuid(m) ? await _db.QueryAsync("SELECT scope, global_status FROM modules WHERE id=@id", new (string, object?)[] { ("id", m) }, ct) : new();
            if (rows.Count == 0 || rows[0]["scope"] != "global" || rows[0]["global_status"] != "active")
                throw new ArgumentException("Nur aktive globale Module sind zuweisbar.");
        }

        var topics = new List<(string TopicId, int Version, bool Mandatory, DateTime? Due)>();
        if (topicGrants is { Count: > 0 })
        {
            if (_topics is null) throw new InvalidOperationException("Themenzuweisungen sind nicht verfügbar.");
            var assignable = await _topics.ListAssignableTopicsAsync(actor.Id);
            foreach (var g in topicGrants.GroupBy(x => x.TopicId, StringComparer.OrdinalIgnoreCase).Select(x => x.Last()))
            {
                var t = assignable.FirstOrDefault(a => string.Equals(a.TopicId, g.TopicId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException("Thema nicht gefunden oder nicht zuweisbar.");
                var v = g.Version is int ver && ver > 0 ? t.Versions.FirstOrDefault(x => x.Version == ver) : t.Versions.FirstOrDefault();
                if (v is null) throw new ArgumentException("Diese Themenversion ist nicht zuweisbar.");
                if (g.DueAt is DateTime due && (due <= Clock() || due > Clock().AddYears(5))) throw new ArgumentException("Das Fälligkeitsdatum muss in der Zukunft liegen (höchstens 5 Jahre).");
                topics.Add((t.TopicId, v.Version, g.Mandatory, g.DueAt));
            }
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));   // 256 Bit
        var invitationId = Guid.NewGuid().ToString();
        var now = Clock();
        var expires = now.AddHours(meta?.ValidityHours ?? _s.ValidityHours);
        var stmts = new List<DbStatement>
        {
            new("INSERT INTO invitations (invitation_id, recipient_email, token_hash, target_profile, status, created_by, created_at, expires_at) VALUES (@id, @email, @hash, @profile, 'pending', @by, @now, @exp)",
                new (string, object?)[] { ("id", invitationId), ("email", email), ("hash", Hash(token)), ("profile", profile), ("by", actor.Id), ("now", now), ("exp", expires) }, 1),
            new("INSERT INTO invitation_deliveries (invitation_id, status, updated_at) VALUES (@i, 'pending', @now)", new (string, object?)[] { ("i", invitationId), ("now", now) }, 1),
        };
        foreach (var m in modules)
            stmts.Add(new("INSERT INTO invitation_module_grants (invitation_id, module_id) VALUES (@i, @m)", new (string, object?)[] { ("i", invitationId), ("m", m) }, 1));
        foreach (var t in topics)
            stmts.Add(new("INSERT INTO invitation_topic_grants (invitation_id, topic_id, topic_version, mandatory, due_at) VALUES (@i, @t, @v, @m, @d)",
                new (string, object?)[] { ("i", invitationId), ("t", t.TopicId), ("v", t.Version), ("m", t.Mandatory ? 1 : 0), ("d", t.Due) }, 1));
        if (meta is { HasText: true })
            stmts.Add(new("INSERT INTO invitation_meta (invitation_id, display_name, description, note) VALUES (@i,@d,@x,@n)",
                new (string, object?)[] { ("i", invitationId), ("d", meta.DisplayName), ("x", meta.Description), ("n", meta.Note) }, 1));
        stmts.Add(Events.Statement(InviteEventTypes.Created, invitationId, null, profile, actor.Id));
        stmts.Add(AuditStatement(actor.Id, "invitation.create", invitationId, $"profile={profile} modules={modules.Count} topics={topics.Count}"));
        await _db.ExecuteBatchAsync(stmts, ct);
        return new CreatedInvitation(invitationId, _s.LinkBaseUrl + "/invite/" + token, expires, token);
    }

    public async Task<bool> RevokeAsync(string? actorUserId, string invitationId, CancellationToken ct = default)
    {
        var actor = await _users.RequireRoleAsync(actorUserId, Roles.Admin);
        if (!UserService.IsGuid(invitationId)) return false;
        var n = await _db.ExecuteAsync("UPDATE invitations SET status='revoked', revoked_at=@now WHERE invitation_id=@id AND status IN ('pending','expired')",
            new (string, object?)[] { ("id", invitationId), ("now", Clock()) }, ct);
        if (n == 1)
        {
            await Events.LogAsync(InviteEventTypes.Revoked, invitationId, null, null, actor.Id, ct);
            await _users.WriteAuditAsync(actor.Id, "invitation.revoke", invitationId, "");
        }
        return n == 1;
    }

    /// <summary>Kontrollierte Neuausstellung des Links derselben Einladung (Rotation): der alte Link wird sofort ungültig, es entsteht keine zweite Einladung.
    /// Die Bedingung im UPDATE macht die Rotation gegenüber paralleler Einlösung und Widerruf atomar (nur im Status pending, unverändert ablaufend).</summary>
    public async Task<CreatedInvitation?> RotateAsync(string? actorUserId, string invitationId, CancellationToken ct = default)
    {
        var actor = await _users.RequireRoleAsync(actorUserId, Roles.Admin);
        if (!Enabled || !UserService.IsGuid(invitationId)) return null;
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = Clock();
        var n = await _db.ExecuteAsync("UPDATE invitations SET token_hash=@hash, token_version=token_version+1 WHERE invitation_id=@id AND status='pending' AND revoked_at IS NULL AND expires_at>@now",
            new (string, object?)[] { ("id", invitationId), ("hash", Hash(token)), ("now", now) }, ct);
        if (n != 1) return null;
        await _db.ExecuteAsync("UPDATE invitation_deliveries SET status='pending', lease_until=NULL, last_error_code=NULL, attempts=0, updated_at=@now WHERE invitation_id=@i",
            new (string, object?)[] { ("i", invitationId), ("now", now) }, ct);
        await Events.LogAsync(InviteEventTypes.Rotated, invitationId, null, null, actor.Id, ct);
        await _users.WriteAuditAsync(actor.Id, "invitation.rotate", invitationId, "");
        var rows = await _db.QueryAsync("SELECT expires_at FROM invitations WHERE invitation_id=@i", new (string, object?)[] { ("i", invitationId) }, ct);
        return new CreatedInvitation(invitationId, _s.LinkBaseUrl + "/invite/" + token, DbValue.ToDateTime(rows[0]["expires_at"]), token);
    }

    /// <summary>Einladungsübersicht für die Verwaltung (nur Administratoren). Nie Hash oder Token.</summary>
    public async Task<List<InvitationRow>> ListAsync(string? actorUserId, InvitationFilter? filter = null, int take = 200, CancellationToken ct = default)
    {
        await _users.RequireRoleAsync(actorUserId, Roles.Admin);
        var sql = """
            SELECT i.invitation_id, i.recipient_email, i.target_profile, i.status, i.created_at, i.expires_at, i.accepted_at, i.accepted_user_id,
                   COALESCE(d.status, 'pending') AS delivery_status, m.display_name AS display_name,
                   (SELECT COUNT(*) FROM invitation_module_grants g WHERE g.invitation_id = i.invitation_id) AS module_count,
                   (SELECT COUNT(*) FROM invitation_topic_grants t WHERE t.invitation_id = i.invitation_id) AS topic_count
            FROM invitations i
            LEFT JOIN invitation_deliveries d ON d.invitation_id = i.invitation_id
            LEFT JOIN invitation_meta m ON m.invitation_id = i.invitation_id
            """;
        var args = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(filter?.Query))
        {
            var q = "%" + filter!.Query!.Trim().ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            sql += " WHERE LOWER(i.recipient_email) LIKE @q ESCAPE '\\'";
            args.Add(("q", q));
        }
        sql += " ORDER BY i.created_at DESC LIMIT " + Math.Clamp(take, 1, 1000);
        var rows = (await _db.QueryAsync(sql, args, ct)).Select(r => new InvitationRow(r["invitation_id"]!, r["recipient_email"]!, r["target_profile"]!, EffectiveStatus(r), r["delivery_status"]!,
            DbValue.ToDateTime(r["created_at"]), DbValue.ToDateTime(r["expires_at"]), r["accepted_at"] is null ? null : DbValue.ToDateTime(r["accepted_at"]), r.GetValueOrDefault("accepted_user_id"),
            r.GetValueOrDefault("display_name"), DbValue.ToInt(r["module_count"]), DbValue.ToInt(r["topic_count"]))).ToList();
        return string.IsNullOrWhiteSpace(filter?.Status) ? rows : rows.Where(r => r.Status == filter!.Status).ToList();
    }

    /// <summary>Nichtgeheime Angaben für Versand und Verwaltung (nie Hash oder Token).</summary>
    public async Task<InvitationInfo?> GetInfoAsync(string invitationId, CancellationToken ct = default)
    {
        if (!UserService.IsGuid(invitationId)) return null;
        var rows = await _db.QueryAsync("SELECT * FROM invitations WHERE invitation_id=@i", new (string, object?)[] { ("i", invitationId) }, ct);
        if (rows.Count == 0) return null;
        var r = rows[0];
        var meta = (await _db.QueryAsync("SELECT display_name FROM invitation_meta WHERE invitation_id=@i", new (string, object?)[] { ("i", invitationId) }, ct)).FirstOrDefault();
        return new InvitationInfo(invitationId, r["recipient_email"]!, r["target_profile"]!, EffectiveStatus(r), DbValue.ToDateTime(r["expires_at"]), DbValue.ToInt(r["token_version"]), meta?["display_name"]);
    }

    // ─── Vorprüfung (vor dem Redirect zu Entra; verbraucht nichts) ─────

    /// <summary>Einladungs-ID zu einem Token (nur wenn der Token bekannt ist). Gibt nie Hash oder Token heraus.</summary>
    public async Task<string?> GetInvitationIdAsync(string? token, CancellationToken ct = default) => (await FindAsync(token, ct))?["invitation_id"];

    public async Task<bool> IsRedeemableAsync(string? token, CancellationToken ct = default)
    {
        if (!Enabled) return false;
        var inv = await FindAsync(token, ct);
        return inv is not null && EffectiveStatus(inv) == InvitationStatus.Pending;
    }

    /// <summary>Grund einer abgewiesenen Vorprüfung als Ereigniskategorie (nur Enum-Name, nie Token).</summary>
    public async Task<string> PreflightFailureAsync(string? token, CancellationToken ct = default)
    {
        var inv = await FindAsync(token, ct);
        if (inv is null) return RedeemOutcome.UnknownToken.ToString();
        return EffectiveStatus(inv) switch
        {
            InvitationStatus.Accepted => RedeemOutcome.AlreadyUsed.ToString(),
            InvitationStatus.Revoked => RedeemOutcome.Revoked.ToString(),
            InvitationStatus.Expired => RedeemOutcome.Expired.ToString(),
            _ => RedeemOutcome.UnknownToken.ToString(),
        };
    }

    // ─── Einlösung (nach validiertem OIDC-Token) ───────────────────────

    /// <summary>
    /// Löst die Einladung nach einem <b>bereits geprüften</b> ID-Token atomar ein. Reihenfolge: Einladung prüfen → Identität bereits vorhanden? (ablehnen) →
    /// E-Mail bestätigt und gleich der Einladung (Konstantzeitvergleich) → eine Transaktion (Anspruch, Benutzer, Identität, Rolle, Profil, Zuweisungen, Ereignisse).
    /// Alle Ablehnungsgründe liefern nach außen dieselbe Meldung; der Grund steht nur im Ereignisprotokoll.
    /// </summary>
    public async Task<RedeemResult> RedeemAsync(string? token, VerifiedExternalIdentity identity, CancellationToken ct = default)
    {
        if (!Enabled) return await FailAsync(null, RedeemOutcome.UnknownToken, ct);
        if (string.IsNullOrWhiteSpace(token)) return await FailAsync(null, RedeemOutcome.MissingToken, ct);
        if (string.IsNullOrWhiteSpace(identity.Issuer) || string.IsNullOrWhiteSpace(identity.Subject)) return await FailAsync(null, RedeemOutcome.InvalidIdentity, ct);

        var inv = await FindAsync(token, ct);
        if (inv is null) return await FailAsync(null, RedeemOutcome.UnknownToken, ct);
        var id = inv["invitation_id"]!;
        switch (EffectiveStatus(inv))
        {
            case InvitationStatus.Accepted: return await FailAsync(id, RedeemOutcome.AlreadyUsed, ct);
            case InvitationStatus.Revoked: return await FailAsync(id, RedeemOutcome.Revoked, ct);
            case InvitationStatus.Expired: return await FailAsync(id, RedeemOutcome.Expired, ct);
        }

        // Bestehende Identität → Einladung nur für Neuregistrierungen.
        if ((await _db.QueryAsync("SELECT 1 AS x FROM user_identities WHERE provider=@p AND subject=@s", new (string, object?)[] { ("p", identity.Issuer), ("s", identity.Subject) }, ct)).Count > 0)
            return await FailAsync(id, RedeemOutcome.IdentityAlreadyRegistered, ct);

        // E-Mail-Bindung: nur bei bestätigter Adresse; gleiche E-Mail allein verknüpft nie mit einem Konto.
        if (!identity.EmailVerified) return await FailAsync(id, RedeemOutcome.EmailNotVerified, ct);
        var claimed = NormalizeEmail(identity.Email);
        if (claimed is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(claimed), Encoding.UTF8.GetBytes(inv["recipient_email"]!)))
            return await FailAsync(id, RedeemOutcome.EmailMismatch, ct);
        // Inzwischen angelegtes Konto mit derselben Adresse (z. B. Workforce): keine zweite Identität unter derselben Adresse.
        if ((await _db.QueryAsync("SELECT 1 AS x FROM users WHERE LOWER(email)=@e", new (string, object?)[] { ("e", claimed) }, ct)).Count > 0)
            return await FailAsync(id, RedeemOutcome.IdentityAlreadyRegistered, ct);

        // Das Profil kommt nur aus dem Einladungsdatensatz und muss ein externes Lernprofil sein (nie eine Rolle).
        var profile = inv["target_profile"]!;
        if (!LearnerProfiles.IsExternal(profile)) return await FailAsync(id, RedeemOutcome.ProvisioningFailed, ct);

        var moduleIds = (await _db.QueryAsync("SELECT module_id FROM invitation_module_grants WHERE invitation_id=@i", new (string, object?)[] { ("i", id) }, ct)).Select(r => r["module_id"]!).ToList();
        var topicRows = await _db.QueryAsync("SELECT topic_id, topic_version, mandatory, due_at FROM invitation_topic_grants WHERE invitation_id=@i", new (string, object?)[] { ("i", id) }, ct);
        var displayName = await InvitedNameAsync(id, claimed, ct);

        var userId = Guid.NewGuid().ToString();
        var now = Clock();
        var createdBy = inv["created_by"];
        var stmts = new List<DbStatement>
        {
            // 0: atomarer Anspruch — genau eine Zeile gewinnt; bindet den aktuellen Token-Hash (Rotation macht alte Links wirkungslos)
            new("UPDATE invitations SET status='accepted', accepted_at=@now, accepted_user_id=@uid WHERE invitation_id=@id AND token_hash=@hash AND status='pending' AND revoked_at IS NULL AND expires_at>@now",
                new (string, object?)[] { ("id", id), ("uid", userId), ("now", now), ("hash", inv["token_hash"]) }, 1),
            new("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id, @name, @email, 'active', 1, @now, @now)",
                new (string, object?)[] { ("id", userId), ("name", displayName), ("email", claimed), ("now", now) }, 1),
            new("INSERT INTO user_identities (provider, subject, user_id, created_at, kind) VALUES (@p, @s, @u, @now, 'external')",
                new (string, object?)[] { ("p", identity.Issuer), ("s", identity.Subject), ("u", userId), ("now", now) }, 1),
            new("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@u, 'user', NULL, @now)", new (string, object?)[] { ("u", userId), ("now", now) }, 1),
            new("INSERT INTO user_profiles (user_id, profile, source_invitation_id, created_at) VALUES (@u, @p, @i, @now)", new (string, object?)[] { ("u", userId), ("p", profile), ("i", id), ("now", now) }, 1),
        };
        foreach (var m in moduleIds)   // nur noch aktive globale Module; ein inzwischen deaktiviertes Modul wird nicht zugewiesen (kein Zugriff, kein Fehler)
            stmts.Add(new("INSERT INTO module_grants (user_id, module_id, publication_id, source_invitation_id, granted_by, granted_at) SELECT @u, m.id, m.publication_id, @i, @by, @now FROM modules m WHERE m.id=@m AND m.scope='global' AND m.global_status='active'",
                new (string, object?)[] { ("u", userId), ("m", m), ("i", id), ("by", createdBy), ("now", now) }));
        foreach (var t in topicRows)   // nur aktive Themen; Version und Pflichtstatus stammen aus der Einladung
        {
            DateTime? due = t["due_at"] is null ? null : DbValue.ToDateTime(t["due_at"]);
            if (due is DateTime d && d <= now) due = null;   // Fälligkeit bereits verstrichen: Zuweisung ohne Frist
            stmts.Add(new("INSERT INTO topic_assignments (id, topic_id, user_id, assigned_by, assigned_at, status, topic_version, mandatory, due_at) SELECT @aid, t.id, @u, @by, @now, 'active', @v, @mand, @due FROM topics t WHERE t.id=@t AND t.status='active'",
                new (string, object?)[] { ("aid", Guid.NewGuid().ToString()), ("t", t["topic_id"]), ("u", userId), ("by", createdBy ?? userId), ("now", now), ("v", t["topic_version"] is null ? null : DbValue.ToInt(t["topic_version"])), ("mand", DbValue.ToInt(t["mandatory"])), ("due", due) }));
        }
        stmts.Add(Events.Statement(InviteEventTypes.Accepted, id, userId));
        stmts.Add(Events.Statement(InviteEventTypes.UserCreated, id, userId));
        stmts.Add(Events.Statement(InviteEventTypes.ProfileAssigned, id, userId, profile));
        if (moduleIds.Count + topicRows.Count > 0) stmts.Add(Events.Statement(InviteEventTypes.ContentAssigned, id, userId, "invitation"));
        stmts.Add(AuditStatement(null, "invitation.redeem", id, $"user={userId} profile={profile}"));

        try
        {
            await _db.ExecuteBatchAsync(stmts, ct);
        }
        catch (DbBatchConflictException ex) when (ex.Index == 0)
        {
            return await FailAsync(id, RedeemOutcome.AlreadyUsed, ct);   // ein anderer Aufruf (oder Rotation/Widerruf) war schneller; nichts wurde angelegt
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ausgang prüfen: Ist unser Benutzer trotz Fehler (z. B. Verbindungsabbruch beim Commit) angelegt, war die Transaktion erfolgreich.
            List<Dictionary<string, string?>> ours;
            try { ours = await _db.QueryAsync("SELECT user_id FROM user_identities WHERE provider=@p AND subject=@s", new (string, object?)[] { ("p", identity.Issuer), ("s", identity.Subject) }, ct); }
            catch (Exception ex2) when (ex2 is not OperationCanceledException) { ours = new(); }   // Datenbank nicht erreichbar: als Fehlschlag behandeln (fail-closed)
            if (ours.Count == 1 && string.Equals(ours[0]["user_id"], userId, StringComparison.OrdinalIgnoreCase)) return new RedeemResult(RedeemOutcome.Success, userId, profile);
            if (ours.Count > 0) return await FailAsync(id, RedeemOutcome.IdentityAlreadyRegistered, ct);   // paralleler Aufruf mit derselben Identität hat gewonnen
            return await FailAsync(id, RedeemOutcome.ProvisioningFailed, ct);   // nichts angelegt, Einladung bleibt gültig
        }
        return new RedeemResult(RedeemOutcome.Success, userId, profile);
    }

    // ─── Hilfen ────────────────────────────────────────────────────────

    private async Task<RedeemResult> FailAsync(string? invitationId, RedeemOutcome outcome, CancellationToken ct, string? hint = null)
    {
        try { await Events.LogAsync(InviteEventTypes.RegistrationRejected, invitationId, null, hint is null ? outcome.ToString() : outcome + ":" + hint, null, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* Protokollfehler darf die Ablehnung nicht verändern */ }
        return new RedeemResult(outcome);
    }

    private async Task<Dictionary<string, string?>?> FindAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128 || !_s.Enabled) return null;
        var rows = await _db.QueryAsync("SELECT * FROM invitations WHERE token_hash=@h", new (string, object?)[] { ("h", Hash(token)) }, ct);
        return rows.Count == 1 ? rows[0] : null;
    }

    private string EffectiveStatus(Dictionary<string, string?> inv)
    {
        var status = inv["status"]!;
        if (status == InvitationStatus.Pending && DbValue.ToDateTime(inv["expires_at"]) <= Clock()) return InvitationStatus.Expired;
        return status;
    }

    private string Hash(string token)
    {
        using var h = new HMACSHA256(_s.TokenKey);
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }

    private DbStatement AuditStatement(string? actor, string action, string target, string detail) =>
        new("INSERT INTO audit_log (id, actor_user_id, action, target, detail, created_at) VALUES (@id, @actor, @action, @target, @detail, @now)",
            new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("actor", actor), ("action", action), ("target", target), ("detail", detail), ("now", Clock()) });

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
        var fallback = email[..email.IndexOf('@')];
        var chosen = string.IsNullOrEmpty(name) ? fallback : name;
        return chosen.Length > 80 ? chosen[..80] : chosen;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
