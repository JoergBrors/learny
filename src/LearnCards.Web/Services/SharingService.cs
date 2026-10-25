using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>
/// Freigaben, Einreichung und globale Veröffentlichung von Lernmodulen (ADR 0015).
/// Drei Bereiche: PRIVAT (Eigentümer), FREIGEGEBEN (Eigentümer erlaubt einzelnen Benutzern das Lernen),
/// GLOBAL (Administrator veröffentlicht eine eingefrorene Version). Fehler:
/// <see cref="UnauthorizedAccessException"/> (403), <see cref="KeyNotFoundException"/> (404),
/// <see cref="InvalidOperationException"/>/<see cref="ArgumentException"/> (400/409).
/// </summary>
public class SharingService
{
    private readonly IDatabase _db;
    private readonly CardRepository _repo;
    private readonly UserService _users;

    public SharingService(IDatabase db, CardRepository repo, UserService users)
    {
        _db = db;
        _repo = repo;
        _users = users;
    }

    // ─── Persönliche Freigaben (Eigentümer) ────────────────────────────────

    /// <summary>Aktive Empfänger eines eigenen Moduls.</summary>
    public async Task<List<ShareInfo>> ListSharesAsync(string ownerId, string moduleId)
    {
        await RequireOwnModuleAsync(ownerId, moduleId);
        var rows = await _db.QueryAsync("""
            SELECT s.module_id, m.name AS module_name, s.recipient_user_id, u.display_name, u.email, s.created_at
            FROM module_shares s
            JOIN modules m ON m.id = s.module_id
            JOIN users u ON u.id = s.recipient_user_id
            WHERE s.module_id = @m AND s.revoked_at IS NULL
            ORDER BY u.display_name
            """, new[] { ("m", (object?)moduleId) });
        return rows.Select(MapShare).ToList();
    }

    /// <summary>Alle Freigaben, die der Benutzer ausgesprochen hat (für das Profil).</summary>
    public async Task<List<ShareInfo>> ListOutgoingSharesAsync(string ownerId)
    {
        var rows = await _db.QueryAsync("""
            SELECT s.module_id, m.name AS module_name, s.recipient_user_id, u.display_name, u.email, s.created_at
            FROM module_shares s
            JOIN modules m ON m.id = s.module_id
            JOIN users u ON u.id = s.recipient_user_id
            WHERE m.owner_user_id = @o AND s.revoked_at IS NULL
            ORDER BY m.name, u.display_name
            """, new[] { ("o", (object?)ownerId) });
        return rows.Select(MapShare).ToList();
    }

    /// <summary>
    /// Gibt ein eigenes Modul für einen aktiven Benutzer frei (Lesen/Lernen). Der Server prüft das Eigentum selbst;
    /// Lernstatus, Chat und Ergebnisse werden nicht mitübertragen.
    /// </summary>
    public async Task GrantShareAsync(string ownerId, string moduleId, string recipientId)
    {
        var module = await RequireOwnModuleAsync(ownerId, moduleId);
        if (!UserService.IsGuid(recipientId)) throw new KeyNotFoundException("Empfänger nicht gefunden.");
        if (string.Equals(ownerId, recipientId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Ein Modul kann nicht für sich selbst freigegeben werden.");
        var recipient = await _users.GetAsync(recipientId);
        if (recipient is null || !recipient.IsActive) throw new KeyNotFoundException("Empfänger nicht gefunden.");

        var existing = await _db.QueryAsync("SELECT id FROM module_shares WHERE module_id = @m AND recipient_user_id = @r",
            new (string, object?)[] { ("m", module.Id), ("r", recipient.Id) });
        if (existing.Count > 0)
            await _db.ExecuteAsync("UPDATE module_shares SET revoked_at = NULL, shared_by_user_id = @o WHERE module_id = @m AND recipient_user_id = @r",
                new (string, object?)[] { ("o", ownerId), ("m", module.Id), ("r", recipient.Id) });
        else
            await _db.ExecuteAsync("""
                INSERT INTO module_shares (id, module_id, recipient_user_id, shared_by_user_id, created_at)
                VALUES (@id, @m, @r, @o, @now)
                """, new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("m", module.Id), ("r", recipient.Id), ("o", ownerId), ("now", DateTime.UtcNow) });
        await _users.WriteAuditAsync(ownerId, "share.grant", module.Id, $"recipient={recipient.Id}");
    }

    /// <summary>Widerruft eine Freigabe. Bereits erzielte Lernergebnisse des Empfängers bleiben erhalten (nur der Zugriff endet).</summary>
    public async Task<bool> RevokeShareAsync(string ownerId, string moduleId, string recipientId)
    {
        var module = await RequireOwnModuleAsync(ownerId, moduleId);
        if (!UserService.IsGuid(recipientId)) return false;
        var n = await _db.ExecuteAsync("UPDATE module_shares SET revoked_at = @now WHERE module_id = @m AND recipient_user_id = @r AND revoked_at IS NULL",
            new (string, object?)[] { ("now", DateTime.UtcNow), ("m", module.Id), ("r", recipientId) });
        if (n > 0) await _users.WriteAuditAsync(ownerId, "share.revoke", module.Id, $"recipient={recipientId}");
        return n > 0;
    }

    /// <summary>Sucht aktive Benutzer als Freigabeempfänger (mindestens 3 Zeichen; Name oder E-Mail).</summary>
    public Task<List<UserSearchResult>> SearchRecipientsAsync(string userId, string? query) => _users.SearchUsersAsync(userId, query);

    // ─── Einreichung zur globalen Veröffentlichung (Eigentümer) ────────────

    public async Task<List<SubmissionInfo>> ListOwnSubmissionsAsync(string ownerId, string moduleId)
    {
        await RequireOwnModuleAsync(ownerId, moduleId);
        var rows = await _db.QueryAsync($"{SubmissionSelect} WHERE source_module_id = @m ORDER BY submitted_at DESC",
            new[] { ("m", (object?)moduleId) });
        return rows.Select(MapSubmission).ToList();
    }

    /// <summary>
    /// Reicht ein eigenes Modul ein: Es wird sofort eine eingefrorene Kopie (staged) erzeugt, die der Administrator prüft.
    /// Nachträgliche Änderungen am Original ändern die Einreichung nicht. Eine erneute Einreichung eines bereits veröffentlichten
    /// Moduls erzeugt eine neue Version derselben Veröffentlichung.
    /// </summary>
    public async Task<SubmissionInfo> SubmitAsync(string ownerId, string moduleId)
    {
        var module = await RequireOwnModuleAsync(ownerId, moduleId);
        var pending = await _db.QueryAsync("SELECT 1 FROM module_submissions WHERE source_module_id = @m AND status = 'pending'",
            new[] { ("m", (object?)module.Id) });
        if (pending.Count > 0) throw new InvalidOperationException("Dieses Modul wurde bereits eingereicht und wartet auf die Prüfung.");
        if (module.ActiveCount == 0) throw new InvalidOperationException("Das Modul enthält keine aktiven Karten.");

        var known = await _db.QueryAsync("""
            SELECT publication_id, MAX(global_version) AS v FROM modules
            WHERE scope = 'global' AND source_module_id = @m AND publication_id IS NOT NULL
            GROUP BY publication_id
            """, new[] { ("m", (object?)module.Id) });
        var kind = known.Count > 0 ? "update" : "new";
        var publicationId = known.Count > 0 ? known[0]["publication_id"]! : Guid.NewGuid().ToString();
        var version = known.Count > 0 ? DbValue.ToInt(known[0]["v"]) + 1 : 1;

        var submitterName = await _repo.DisplayNameAsync(ownerId);
        var (staged, cardCount) = await _repo.CreateStagedGlobalCopyAsync(module, publicationId, version, submitterName);

        var id = Guid.NewGuid().ToString();
        await _db.ExecuteAsync("""
            INSERT INTO module_submissions (id, publication_id, kind, source_module_id, staged_module_id, submitter_user_id, submitter_name,
                                            module_name, card_count, version, status, reason, submitted_at)
            VALUES (@id, @pub, @kind, @src, @staged, @sub, @subname, @name, @cnt, @ver, 'pending', '', @now)
            """, new (string, object?)[]
        {
            ("id", id), ("pub", publicationId), ("kind", kind), ("src", module.Id), ("staged", staged.Id), ("sub", ownerId),
            ("subname", submitterName), ("name", module.Name), ("cnt", cardCount), ("ver", version), ("now", DateTime.UtcNow),
        });
        await _users.WriteAuditAsync(ownerId, "submission.create", id, $"module={module.Id};version={version}");
        return (await GetSubmissionAsync(id))!;
    }

    // ─── Prüfung und Veröffentlichung (Administrator) ──────────────────────

    public async Task<List<SubmissionInfo>> ListSubmissionsAsync(string actorId, string? status = null)
    {
        await _users.RequireRoleAsync(actorId, Roles.Admin);
        var sql = SubmissionSelect;
        var args = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(status)) { sql += " WHERE status = @s"; args.Add(("s", status)); }
        var rows = await _db.QueryAsync(sql + " ORDER BY submitted_at DESC LIMIT 200", args);
        return rows.Select(MapSubmission).ToList();
    }

    /// <summary>Vorschau der eingefrorenen Kopie einer offenen Einreichung. Das private Original wird nicht gelesen.</summary>
    public async Task<(SubmissionInfo Submission, ModuleInfo Module, List<Card> Cards)> PreviewSubmissionAsync(string actorId, string submissionId)
    {
        await _users.RequireRoleAsync(actorId, Roles.Admin);
        var sub = await GetSubmissionAsync(submissionId) ?? throw new KeyNotFoundException("Einreichung nicht gefunden.");
        var module = sub.StagedModuleId is null ? null : await _repo.GetModuleRawAsync(sub.StagedModuleId);
        if (module is null || module.Scope != "global") throw new KeyNotFoundException("Die eingefrorene Kopie ist nicht mehr vorhanden.");
        return (sub, module, await _repo.ListAllCardsOfModuleRawAsync(module.Id));
    }

    /// <summary>Kennzeichen für „als eigenständige neue Veröffentlichung“ bei <see cref="ApproveAsync"/>.</summary>
    public const string PublishAsNew = "new";

    /// <summary>
    /// Veröffentlicht eine offene Einreichung. <paramref name="target"/> bestimmt, was passiert:
    /// <c>null</c> = Standard (Aktualisierung ersetzt die eigenen früheren Versionen, eine Erstveröffentlichung wird eigenständig);
    /// <see cref="PublishAsNew"/> = eigenständige neue Veröffentlichung neben den vorhandenen;
    /// eine Veröffentlichungs-ID = wird als neue Version <b>dieser</b> Veröffentlichung geführt und ersetzt deren frühere Versionen (nicht gelöscht, nur „ersetzt“).
    /// </summary>
    public async Task ApproveAsync(string actorId, string submissionId, string? target = null)
    {
        var actor = await _users.RequireRoleAsync(actorId, Roles.Admin);
        var sub = await GetSubmissionAsync(submissionId) ?? throw new KeyNotFoundException("Einreichung nicht gefunden.");
        if (target is not null && target != PublishAsNew)
        {
            // Vor dem Beanspruchen prüfen, damit ein falsches Ziel die Einreichung nicht „verbraucht“
            if (!UserService.IsGuid(target)
                || (await _db.QueryAsync("SELECT 1 FROM modules WHERE scope = 'global' AND publication_id = @p AND global_status <> 'staged'", new[] { ("p", (object?)target) })).Count == 0)
                throw new KeyNotFoundException("Die zu ersetzende Veröffentlichung wurde nicht gefunden.");
        }
        // Atomar beanspruchen: nur ein Administrator kann eine offene Einreichung entscheiden
        var claimed = await _db.ExecuteAsync("UPDATE module_submissions SET status = 'approved', decided_by = @a, decided_at = @n WHERE id = @id AND status = 'pending'",
            new (string, object?)[] { ("a", actor.Id), ("n", DateTime.UtcNow), ("id", submissionId) });
        if (claimed == 0) throw new InvalidOperationException("Die Einreichung wurde bereits entschieden.");
        if (sub.StagedModuleId is null) throw new InvalidOperationException("Die eingefrorene Kopie fehlt.");

        if (target is not null)
        {
            string pub; int version;
            if (target == PublishAsNew) { pub = Guid.NewGuid().ToString(); version = 1; }
            else
            {
                if (!UserService.IsGuid(target)) throw new KeyNotFoundException("Die zu ersetzende Veröffentlichung wurde nicht gefunden.");
                var existing = await _db.QueryAsync("SELECT MAX(global_version) AS v FROM modules WHERE scope = 'global' AND publication_id = @p AND global_status <> 'staged'",
                    new[] { ("p", (object?)target) });
                if (existing.Count == 0 || existing[0]["v"] is null) throw new KeyNotFoundException("Die zu ersetzende Veröffentlichung wurde nicht gefunden.");
                pub = target; version = DbValue.ToInt(existing[0]["v"]) + 1;
            }
            await _db.ExecuteAsync("UPDATE modules SET publication_id = @p, global_version = @v WHERE id = @staged AND scope = 'global' AND global_status = 'staged'",
                new (string, object?)[] { ("p", pub), ("v", version), ("staged", sub.StagedModuleId) });
            await _db.ExecuteAsync("UPDATE module_submissions SET publication_id = @p, version = @v, kind = @k WHERE id = @id",
                new (string, object?)[] { ("p", pub), ("v", version), ("k", version > 1 ? "update" : "new"), ("id", sub.Id) });
            sub.PublicationId = pub; sub.Version = version; sub.Kind = version > 1 ? "update" : "new";
        }

        var now = DateTime.UtcNow;
        await _db.ExecuteAsync("UPDATE modules SET global_status = 'superseded' WHERE scope = 'global' AND publication_id = @p AND id <> @staged AND global_status IN ('active', 'deactivated')",
            new (string, object?)[] { ("p", sub.PublicationId), ("staged", sub.StagedModuleId) });
        var n = await _db.ExecuteAsync("UPDATE modules SET global_status = 'active', published_at = @n, published_by = @a, updated_at = @n WHERE id = @staged AND scope = 'global' AND global_status = 'staged'",
            new (string, object?)[] { ("n", now), ("a", actor.Id), ("staged", sub.StagedModuleId) });
        if (n == 0) throw new InvalidOperationException("Die eingefrorene Kopie ist nicht mehr veröffentlichbar.");
        await _users.WriteAuditAsync(actor.Id, sub.Kind == "update" ? "publication.update" : "publication.approve", sub.PublicationId, $"submission={sub.Id};version={sub.Version}");
    }

    /// <summary>
    /// Löscht eine globale Modulversion endgültig (Karten, Bilder, zugehörige Lernergebnisse). Nur Administratoren; offene Einreichungen werden abgelehnt/verworfen, nicht hier gelöscht.
    /// Ist es die letzte Version, verschwindet die Veröffentlichung. Das private Original des Autors bleibt unverändert.
    /// </summary>
    public async Task DeleteGlobalVersionAsync(string actorId, string moduleId)
    {
        var actor = await _users.RequireRoleAsync(actorId, Roles.Admin);
        if (!UserService.IsGuid(moduleId)) throw new KeyNotFoundException("Globales Modul nicht gefunden.");
        var module = await _repo.GetModuleRawAsync(moduleId);
        if (module is null || module.Scope != "global" || module.GlobalStatus == "staged") throw new KeyNotFoundException("Globales Modul nicht gefunden.");
        await _repo.DeleteGlobalModuleAsync(moduleId);
        await _users.WriteAuditAsync(actor.Id, "publication.delete", module.PublicationId ?? moduleId, $"module={moduleId};version={module.GlobalVersion};status={module.GlobalStatus}");
    }

    /// <summary>Lehnt eine Einreichung ab; die eingefrorene Kopie wird verworfen, das private Original bleibt unverändert.</summary>
    public async Task RejectAsync(string actorId, string submissionId, string? reason)
    {
        var actor = await _users.RequireRoleAsync(actorId, Roles.Admin);
        var sub = await GetSubmissionAsync(submissionId) ?? throw new KeyNotFoundException("Einreichung nicht gefunden.");
        var text = (reason ?? "").Trim();
        if (text.Length > 500) text = text[..500];
        var claimed = await _db.ExecuteAsync("UPDATE module_submissions SET status = 'rejected', reason = @r, decided_by = @a, decided_at = @n WHERE id = @id AND status = 'pending'",
            new (string, object?)[] { ("r", text), ("a", actor.Id), ("n", DateTime.UtcNow), ("id", submissionId) });
        if (claimed == 0) throw new InvalidOperationException("Die Einreichung wurde bereits entschieden.");
        if (sub.StagedModuleId is not null) await _repo.DeleteStagedModuleAsync(sub.StagedModuleId);
        await _users.WriteAuditAsync(actor.Id, "publication.reject", sub.PublicationId, $"submission={sub.Id}");
    }

    /// <summary>Deaktiviert die aktive Version einer Veröffentlichung bzw. aktiviert die neueste deaktivierte Version wieder. Ergebnisse bleiben erhalten.</summary>
    public async Task SetPublicationActiveAsync(string actorId, string publicationId, bool active)
    {
        var actor = await _users.RequireRoleAsync(actorId, Roles.Admin);
        if (!UserService.IsGuid(publicationId)) throw new KeyNotFoundException("Veröffentlichung nicht gefunden.");
        int n;
        if (!active)
        {
            n = await _db.ExecuteAsync("UPDATE modules SET global_status = 'deactivated' WHERE scope = 'global' AND publication_id = @p AND global_status = 'active'",
                new[] { ("p", (object?)publicationId) });
        }
        else
        {
            var latest = await _db.QueryAsync("SELECT id FROM modules WHERE scope = 'global' AND publication_id = @p AND global_status = 'deactivated' ORDER BY global_version DESC LIMIT 1",
                new[] { ("p", (object?)publicationId) });
            var activeNow = await _db.QueryAsync("SELECT 1 FROM modules WHERE scope = 'global' AND publication_id = @p AND global_status = 'active'",
                new[] { ("p", (object?)publicationId) });
            n = latest.Count == 0 || activeNow.Count > 0 ? 0
                : await _db.ExecuteAsync("UPDATE modules SET global_status = 'active' WHERE id = @id", new[] { ("id", (object?)latest[0]["id"]) });
        }
        if (n == 0) throw new KeyNotFoundException(active ? "Keine deaktivierte Version vorhanden." : "Keine aktive Version vorhanden.");
        await _users.WriteAuditAsync(actor.Id, active ? "publication.activate" : "publication.deactivate", publicationId, "");
    }

    // ─── Einsicht in globale Inhalte und Ergebnisse (Administrator und Auditor, nur lesend) ─────────

    public async Task<List<ModuleInfo>> ListGlobalModulesForReviewAsync(string actorId)
    {
        await _users.RequireAnyRoleAsync(actorId, Roles.Admin, Roles.Auditor);
        return (await _repo.ListAllGlobalModulesAsync()).Where(m => m.GlobalStatus != "staged").ToList();
    }

    /// <summary>Aggregierte Ergebnisse je globaler Modulversion (Versuche, Lernende, Durchschnitt) — ohne personenbezogene Details.</summary>
    public async Task<List<GlobalResultSummary>> GetGlobalResultSummariesAsync(string actorId, string? publicationId = null)
    {
        await _users.RequireAnyRoleAsync(actorId, Roles.Admin, Roles.Auditor);
        var sql = """
            SELECT m.id AS module_id, m.publication_id, m.name, m.global_version, m.global_status,
                   COUNT(r.id) AS attempts, COUNT(DISTINCT r.user_sub) AS learners,
                   AVG(CASE WHEN r.max_score > 0 THEN r.score * 100.0 / r.max_score END) AS avg_percent
            FROM modules m
            LEFT JOIN quiz_results r ON r.module_id = m.id
            WHERE m.scope = 'global' AND m.global_status <> 'staged'
            """;
        var args = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(publicationId))
        {
            if (!UserService.IsGuid(publicationId)) return new();
            sql += " AND m.publication_id = @p";
            args.Add(("p", publicationId));
        }
        sql += " GROUP BY m.id, m.publication_id, m.name, m.global_version, m.global_status ORDER BY m.name, m.global_version DESC";
        var rows = await _db.QueryAsync(sql, args);
        return rows.Select(r => new GlobalResultSummary
        {
            ModuleId = r["module_id"] ?? "", PublicationId = r.GetValueOrDefault("publication_id") ?? "", Name = r.GetValueOrDefault("name") ?? "",
            Version = DbValue.ToInt(r.GetValueOrDefault("global_version")), Status = r.GetValueOrDefault("global_status") ?? "",
            Attempts = DbValue.ToInt(r.GetValueOrDefault("attempts")), Learners = DbValue.ToInt(r.GetValueOrDefault("learners")),
            AveragePercent = Math.Round(DbValue.ToDouble(r.GetValueOrDefault("avg_percent")), 1),
        }).ToList();
    }

    /// <summary>
    /// Einzelergebnisse einer globalen Modulversion mit Lernendenname (Datenminimierung: keine Antworten, kein Feedback).
    /// Der Zugriff wird protokolliert.
    /// </summary>
    public async Task<List<GlobalResultRow>> GetGlobalResultRowsAsync(string actorId, string moduleId, int limit = 200)
    {
        var actor = await _users.RequireAnyRoleAsync(actorId, Roles.Admin, Roles.Auditor);
        var module = await _repo.GetModuleRawAsync(moduleId);
        if (module is null || module.Scope != "global" || module.GlobalStatus == "staged") throw new KeyNotFoundException("Globales Modul nicht gefunden.");
        var rows = await _db.QueryAsync($"""
            SELECT r.id, r.category, r.score, r.max_score, r.grade, r.completed_at, u.display_name
            FROM quiz_results r
            LEFT JOIN users u ON CAST(u.id AS TEXT) = r.user_sub
            WHERE r.module_id = @m
            ORDER BY r.completed_at DESC
            LIMIT {Math.Clamp(limit, 1, 500)}
            """, new[] { ("m", (object?)module.Id) });
        await _users.WriteAuditAsync(actor.Id, "publication.results.read", module.Id, $"rows={rows.Count}");
        return rows.Select(r => new GlobalResultRow
        {
            ResultId = r["id"] ?? "", LearnerName = r.GetValueOrDefault("display_name") ?? "(unbekannt)", Category = r.GetValueOrDefault("category") ?? "",
            Score = DbValue.ToDouble(r.GetValueOrDefault("score")), MaxScore = DbValue.ToDouble(r.GetValueOrDefault("max_score")),
            Grade = r.GetValueOrDefault("grade") ?? "", CompletedAt = DbValue.ToDateTime(r.GetValueOrDefault("completed_at")),
        }).ToList();
    }

    // ─── Hilfen ─────────────────────────────────────────────────────────────

    private async Task<ModuleInfo> RequireOwnModuleAsync(string ownerId, string moduleId) =>
        await _repo.GetModuleAsync(moduleId, ownerId) is { Scope: "private" } m
            ? m
            : throw new KeyNotFoundException("Modul nicht gefunden.");

    private const string SubmissionSelect = """
        SELECT id, publication_id, kind, source_module_id, staged_module_id, submitter_user_id, submitter_name, module_name,
               card_count, version, status, reason, submitted_at, decided_by, decided_at
        FROM module_submissions
        """;

    private async Task<SubmissionInfo?> GetSubmissionAsync(string id)
    {
        if (!UserService.IsGuid(id)) return null;
        var rows = await _db.QueryAsync($"{SubmissionSelect} WHERE id = @id", new[] { ("id", (object?)id) });
        return rows.Count == 0 ? null : MapSubmission(rows[0]);
    }

    private static ShareInfo MapShare(Dictionary<string, string?> r) => new()
    {
        ModuleId = r["module_id"] ?? "", ModuleName = r.GetValueOrDefault("module_name") ?? "", RecipientUserId = r["recipient_user_id"] ?? "",
        RecipientName = r.GetValueOrDefault("display_name") ?? "", RecipientEmail = r.GetValueOrDefault("email") ?? "",
        CreatedAt = DbValue.ToDateTime(r.GetValueOrDefault("created_at")),
    };

    private static SubmissionInfo MapSubmission(Dictionary<string, string?> r) => new()
    {
        Id = r["id"] ?? "", PublicationId = r.GetValueOrDefault("publication_id") ?? "", Kind = r.GetValueOrDefault("kind") ?? "new",
        SourceModuleId = r.GetValueOrDefault("source_module_id"), StagedModuleId = r.GetValueOrDefault("staged_module_id"),
        SubmitterUserId = r.GetValueOrDefault("submitter_user_id"), SubmitterName = r.GetValueOrDefault("submitter_name") ?? "",
        ModuleName = r.GetValueOrDefault("module_name") ?? "", CardCount = DbValue.ToInt(r.GetValueOrDefault("card_count")),
        Version = DbValue.ToInt(r.GetValueOrDefault("version")), Status = r.GetValueOrDefault("status") ?? "pending",
        Reason = r.GetValueOrDefault("reason") ?? "", SubmittedAt = DbValue.ToDateTime(r.GetValueOrDefault("submitted_at")),
        DecidedBy = r.GetValueOrDefault("decided_by"),
        DecidedAt = r.GetValueOrDefault("decided_at") is { Length: > 0 } d ? DbValue.ToDateTime(d) : null,
    };
}
