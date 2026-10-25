using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LearnCards.Web.Api;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services.Certificates;

/// <summary>Ergebnis der Prüfung eines Badges. Enthält bewusst keine Lernendendaten.</summary>
public sealed class BadgeVerification
{
    public bool SignatureValid { get; set; }
    public string? SignatureError { get; set; }
    /// <summary>Wahr, wenn der mitgelieferte JSON-Inhalt exakt dem signierten Inhalt entspricht (Integrität); null, wenn nur eine JWS geprüft wurde.</summary>
    public bool? ContentMatches { get; set; }
    public bool IssuerValid { get; set; }
    public bool Known { get; set; }
    public bool Revoked { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public string BadgeHash { get; set; } = "";
    public string? CertificateId { get; set; }
    public string? Number { get; set; }
    public string? ModuleName { get; set; }
    public int? ModuleVersion { get; set; }
    public DateTime? IssuedAt { get; set; }
    public string ChainStatus { get; set; } = "none";
    public string? ChainNetwork { get; set; }
    public string? TxHash { get; set; }
    public bool Valid => SignatureValid && IssuerValid && ContentMatches != false && !Revoked;
}

/// <summary>
/// Zertifikate und Open Badges 3.0 (ADR 0021): Ausstellung nach bestandenem Modulquiz, Signierung (VC-JWT, ES256), Abruf, Widerruf, öffentliche Prüfung.
/// Die Quizbewertung bleibt im <c>QuizService</c>; dieser Dienst wird nur mit dem dort berechneten, serverseitig verifizierten Ergebnis aufgerufen.
/// </summary>
public sealed class CertificateService
{
    private readonly IDatabase _db;
    private readonly BadgeSigner _signer;
    private readonly ChainSettingsStore _chain;
    private readonly UserService _users;
    private readonly AppConfig _cfg;

    public CertificateService(IDatabase db, BadgeSigner signer, ChainSettingsStore chain, UserService users, AppConfig cfg)
    {
        _db = db; _signer = signer; _chain = chain; _users = users; _cfg = cfg;
    }

    public string BaseUrl => string.IsNullOrEmpty(_cfg.Blockchain.PublicBaseUrl) ? "http://localhost" : _cfg.Blockchain.PublicBaseUrl;
    public string IssuerId => BaseUrl + "/badges/issuer";
    public string KeyId => IssuerId + "#" + _signer.KeyId;
    public BadgeSigner Signer => _signer;

    // ─── Ausstellung ────────────────────────────────────────────────────────

    /// <summary>
    /// Stellt nach einem bestandenen, sitzungsverifizierten Modulquiz ein Zertifikat aus (idempotent je Benutzer, Modul und Version).
    /// Fehler dürfen das Quizergebnis nie gefährden — der Aufrufer fängt Ausnahmen.
    /// </summary>
    public async Task<Certificate?> TryIssueAsync(QuizResultRecord result, string moduleName, string moduleScope, int? moduleVersion)
    {
        if (result.Passed != true || string.IsNullOrEmpty(result.SessionId) || !string.IsNullOrEmpty(result.CardId)) return null;
        var version = moduleVersion ?? 0;
        var existing = await FindAsync(result.UserSub, result.ModuleId, version);
        if (existing is not null) return existing;

        var learner = (await _users.GetAsync(result.UserSub))?.DisplayName ?? "Lernende/r";
        var percent = result.MaxScore > 0 ? Math.Round(result.Score / result.MaxScore * 100, 1) : 0;
        var settings = await _chain.GetAsync();
        var cert = new Certificate
        {
            Number = NewNumber(), UserId = result.UserSub, ModuleId = result.ModuleId, ModuleName = moduleName, ModuleVersion = version, ModuleScope = moduleScope,
            ResultId = result.Id, LearnerName = learner, ScorePercent = percent, PassPercent = result.PassPercent ?? 0, IssuedAt = DateTime.UtcNow,
        };
        var credential = BuildCredential(cert);
        cert.BadgeJws = _signer.Sign(credential, KeyId);
        cert.BadgeHash = BadgeSigner.HashHex(cert.BadgeJws);
        cert.LeafHash = MerkleTree.Hex(MerkleTree.Leaf(MerkleTree.FromHex(cert.BadgeHash)));
        if (settings.Enabled) { cert.ChainStatus = Domain.ChainStatus.Pending; cert.ChainNetwork = settings.Network; cert.ChainId = settings.ChainId; }

        try { await InsertAsync(cert); }
        catch (Exception)
        {
            // paralleler Abschluss: das bereits vorhandene Zertifikat gilt
            var raced = await FindAsync(result.UserSub, result.ModuleId, version);
            if (raced is not null) return raced;
            throw;
        }
        await _users.WriteAuditAsync(result.UserSub, "certificate.issue", cert.Id, $"number={cert.Number};module={cert.ModuleId};version={version}");
        return cert;
    }

    private static string NewNumber()
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";   // Crockford-Base32 (ohne verwechselbare Zeichen)
        var bytes = RandomNumberGenerator.GetBytes(8);
        return $"LC-{DateTime.UtcNow.Year}-" + new string(bytes.Select(b => alphabet[b % 32]).ToArray());
    }

    /// <summary>Open-Badge-3.0-Credential (W3C VC 2.0). Der Inhalt ist nach der Signierung unveränderlich; die Blockchain-Verankerung steht in einem separaten Nachweis-Paket.</summary>
    public JsonObject BuildCredential(Certificate c)
    {
        var achievementId = $"{BaseUrl}/badges/achievements/{c.ModuleId}/{c.ModuleVersion}";
        return new JsonObject
        {
            ["@context"] = new JsonArray("https://www.w3.org/ns/credentials/v2", "https://purl.imsglobal.org/spec/ob/v3p0/context-3.0.3.json"),
            ["id"] = $"{BaseUrl}/verify/{c.Id}",
            ["type"] = new JsonArray("VerifiableCredential", "OpenBadgeCredential"),
            ["issuer"] = new JsonObject
            {
                ["id"] = IssuerId, ["type"] = new JsonArray("Profile"), ["name"] = "LearnCards", ["url"] = BaseUrl,
            },
            ["validFrom"] = c.IssuedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["name"] = $"{c.ModuleName} — Abschluss",
            ["credentialSubject"] = new JsonObject
            {
                ["type"] = new JsonArray("AchievementSubject"),
                ["identifier"] = new JsonArray(new JsonObject { ["type"] = "IdentityObject", ["hashed"] = false, ["identityType"] = "name", ["identity"] = c.LearnerName }),
                ["achievement"] = new JsonObject
                {
                    ["id"] = achievementId, ["type"] = new JsonArray("Achievement"), ["achievementType"] = "Course", ["name"] = c.ModuleName,
                    ["description"] = $"Das Lernmodul „{c.ModuleName}“ wurde erfolgreich abgeschlossen.",
                    ["criteria"] = new JsonObject { ["narrative"] = $"Das Modulquiz wurde mit mindestens {c.PassPercent} % bestanden." },
                },
                ["result"] = new JsonArray(new JsonObject
                {
                    ["type"] = new JsonArray("Result"), ["status"] = "Completed", ["value"] = c.ScorePercent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " %",
                }),
            },
            ["credentialStatus"] = new JsonObject { ["id"] = BaseUrl + "/api/public/revocation-list", ["type"] = "1EdTechRevocationList" },
        };
    }

    /// <summary>Signiertes Open Badge als JSON: Credential plus eingebettete Signatur (<c>proof.jwt</c>, VC-JWT).</summary>
    public JsonObject SignedBadgeJson(Certificate c)
    {
        var credential = (JsonObject)JsonNode.Parse(BadgeSigner.PayloadText(c.BadgeJws))!;
        credential["proof"] = new JsonObject { ["type"] = "JwtProof2020", ["jwt"] = c.BadgeJws };
        return credential;
    }

    // ─── Abruf ──────────────────────────────────────────────────────────────

    public async Task<List<Certificate>> ListForUserAsync(string userId) =>
        (await _db.QueryAsync("SELECT * FROM certificates WHERE user_id = @u ORDER BY issued_at DESC", new[] { ("u", (object?)userId) })).Select(Map).ToList();

    public async Task<Certificate?> GetForUserAsync(string userId, string id)
    {
        if (!UserService.IsGuid(id)) return null;
        var rows = await _db.QueryAsync("SELECT * FROM certificates WHERE id = @id AND user_id = @u", new (string, object?)[] { ("id", id), ("u", userId) });
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    /// <summary>Zertifikat ohne Besitzprüfung — nur für Dienste (Verankerung) und öffentliche, datensparsame Ausgaben.</summary>
    public async Task<Certificate?> GetRawAsync(string id)
    {
        if (!UserService.IsGuid(id)) return null;
        var rows = await _db.QueryAsync("SELECT * FROM certificates WHERE id = @id", new[] { ("id", (object?)id) });
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    private async Task<Certificate?> FindAsync(string userId, string moduleId, int version)
    {
        var rows = await _db.QueryAsync("SELECT * FROM certificates WHERE user_id = @u AND module_id = @m AND module_version = @v",
            new (string, object?)[] { ("u", userId), ("m", moduleId), ("v", version) });
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    /// <summary>Datensparsamer öffentlicher Status: keine Lernendendaten; der Modulname nur bei global veröffentlichten Modulen.</summary>
    public async Task<object?> GetPublicStatusAsync(string id)
    {
        var c = await GetRawAsync(id);
        if (c is null) return null;
        var batch = c.BatchId is null ? null : await GetBatchAsync(c.BatchId);
        return new
        {
            id = c.Id, number = c.Number, status = c.IsRevoked ? "revoked" : "active", revoked_at = c.RevokedAt, revoked_reason = c.IsRevoked ? c.RevokedReason : null,
            module_name = c.ModuleScope == "global" ? c.ModuleName : null, module_version = c.ModuleVersion, issued_at = c.IssuedAt,
            badge_hash = c.BadgeHash, issuer = IssuerId,
            chain = new
            {
                status = c.ChainStatus, label = Domain.ChainStatus.Label(c.ChainStatus, c.IsRevoked), network = c.ChainNetwork, network_label = ChainNetworks.Label(c.ChainNetwork), chain_id = c.ChainId,
                simulated = c.ChainNetwork == ChainNetworks.Simulation, tx_hash = batch?.TxHash, merkle_root = batch?.MerkleRoot,
                explorer_url = batch?.TxHash is { Length: > 0 } tx ? ChainNetworks.ExplorerTx(c.ChainNetwork, tx) : null,
            },
        };
    }

    /// <summary>Nachweis-Paket für die unabhängige Blockchain-Prüfung: Hashes, Merkle-Pfad, Wurzel, Netzwerk, Transaktion. Enthält keine Lernendendaten.</summary>
    public async Task<JsonObject?> BuildProofPackageAsync(Certificate c)
    {
        if (c.MerkleProofJson is null) return null;
        var batch = c.BatchId is null ? null : await GetBatchAsync(c.BatchId);
        return new JsonObject
        {
            ["type"] = "LearnCardsAnchorProof", ["version"] = 1, ["certificate_number"] = c.Number, ["badge_hash"] = c.BadgeHash, ["leaf_hash"] = c.LeafHash,
            ["leaf_rule"] = "leaf = SHA-256(0x00 || badge_hash); node = SHA-256(0x01 || left || right); odd node is promoted unchanged",
            ["merkle_root"] = batch?.MerkleRoot, ["merkle_proof"] = JsonNode.Parse(c.MerkleProofJson),
            ["network"] = c.ChainNetwork, ["chain_id"] = c.ChainId, ["simulated"] = c.ChainNetwork == ChainNetworks.Simulation,
            ["transaction_hash"] = batch?.TxHash, ["block_number"] = batch?.BlockNumber, ["anchor_data"] = batch is null ? null : AnchorService.AnchorData(batch.MerkleRoot),
            ["anchor_wallet"] = _cfg.Blockchain.WalletAddress.Length > 0 ? _cfg.Blockchain.WalletAddress : null,
            ["explorer_url"] = batch?.TxHash is { Length: > 0 } tx ? ChainNetworks.ExplorerTx(c.ChainNetwork, tx) : null,
        };
    }

    public async Task<AnchorBatch?> GetBatchAsync(string batchId)
    {
        if (!UserService.IsGuid(batchId)) return null;
        var rows = await _db.QueryAsync("SELECT * FROM anchor_batches WHERE id = @id", new[] { ("id", (object?)batchId) });
        return rows.Count == 0 ? null : AnchorService.MapBatch(rows[0]);
    }

    // ─── Prüfung ────────────────────────────────────────────────────────────

    /// <summary>Prüft ein eingereichtes Badge (JSON mit <c>proof.jwt</c> oder kompakte JWS): Signatur, Aussteller, Integrität, Registrierung, Widerruf.</summary>
    public async Task<BadgeVerification> VerifyAsync(string input)
    {
        var v = new BadgeVerification();
        input = (input ?? "").Trim();
        string? jws = null; JsonNode? embedded = null;
        try
        {
            if (input.StartsWith('{'))
            {
                var obj = JsonNode.Parse(input) as JsonObject;
                jws = obj?["proof"]?["jwt"]?.GetValue<string>();
                if (obj is not null) { obj = (JsonObject)obj.DeepClone(); obj.Remove("proof"); embedded = obj; }
            }
            else jws = input;
        }
        catch (JsonException) { v.SignatureError = "Die Eingabe ist kein gültiges JSON."; return v; }
        if (string.IsNullOrWhiteSpace(jws)) { v.SignatureError ??= "Es wurde keine Signatur (proof.jwt) gefunden."; return v; }

        var jv = _signer.Verify(jws);
        v.SignatureValid = jv.SignatureValid; v.SignatureError = jv.Error;
        v.IssuerValid = jv.SignatureValid && jv.Payload?["issuer"]?["id"]?.GetValue<string>() == IssuerId;
        if (jv.SignatureValid && embedded is not null) v.ContentMatches = JsonNode.DeepEquals(embedded, jv.Payload);
        v.BadgeHash = BadgeSigner.HashHex(jws);
        if (jv.Payload is not null)
        {
            v.ModuleName = jv.Payload["credentialSubject"]?["achievement"]?["name"]?.GetValue<string>();
            if (DateTime.TryParse(jv.Payload["validFrom"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var from)) v.IssuedAt = from;
        }
        var rows = await _db.QueryAsync("SELECT * FROM certificates WHERE badge_hash = @h", new[] { ("h", (object?)v.BadgeHash) });
        if (rows.Count > 0 && jv.SignatureValid)
        {
            var c = Map(rows[0]);
            v.Known = true; v.CertificateId = c.Id; v.Number = c.Number; v.ModuleVersion = c.ModuleVersion; v.Revoked = c.IsRevoked; v.RevokedAt = c.RevokedAt; v.RevokedReason = c.IsRevoked ? c.RevokedReason : null;
            v.ChainStatus = c.ChainStatus; v.ChainNetwork = c.ChainNetwork;
            if (c.BatchId is not null) v.TxHash = (await GetBatchAsync(c.BatchId))?.TxHash;
        }
        return v;
    }

    // ─── Widerruf, Verwaltung, Prüfung durch Auditoren ─────────────────────

    public async Task<object> RevocationListAsync()
    {
        var rows = await _db.QueryAsync("SELECT id, revoked_reason FROM certificates WHERE status = 'revoked'");
        return new
        {
            context = new[] { "https://www.w3.org/ns/credentials/v2", "https://purl.imsglobal.org/spec/ob/v3p0/context-3.0.3.json" },
            type = "1EdTechRevocationList", issuer = IssuerId,
            revokedCredentials = rows.Select(r => new { id = $"{BaseUrl}/verify/{r["id"]}", revocationReason = r["revoked_reason"] ?? "" }).ToList(),
        };
    }

    /// <summary>Widerruft ein Zertifikat (nur Administratoren). Das signierte Badge bleibt erhalten, gilt aber bei jeder Prüfung als widerrufen.</summary>
    public async Task RevokeAsync(string actorId, string certificateId, string reason)
    {
        var actor = await _users.RequireRoleAsync(actorId, Roles.Admin);
        var c = await GetRawAsync(certificateId) ?? throw new KeyNotFoundException("Zertifikat nicht gefunden.");
        if (c.IsRevoked) throw new InvalidOperationException("Das Zertifikat ist bereits widerrufen.");
        reason = (reason ?? "").Trim();
        if (reason.Length == 0) throw new ArgumentException("Bitte einen Grund für den Widerruf angeben.");
        if (reason.Length > 500) reason = reason[..500];
        await _db.ExecuteAsync("UPDATE certificates SET status = 'revoked', revoked_at = @t, revoked_reason = @r, revoked_by = @a WHERE id = @id AND status = 'active'",
            new (string, object?)[] { ("t", DateTime.UtcNow), ("r", reason), ("a", actor.Id), ("id", c.Id) });
        await _users.WriteAuditAsync(actor.Id, "certificate.revoke", c.Id, $"number={c.Number}");
    }

    public sealed record AdminCertificateRow(string Id, string Number, string ModuleLabel, int ModuleVersion, string Scope, DateTime IssuedAt, string Status, string ChainStatus, string Network, string? LearnerName);

    /// <summary>
    /// Verwaltungsliste für Administratoren: Zertifikatsnummer, Status, Verankerung. Lernendenname und Modulname nur bei global veröffentlichten Modulen —
    /// private Lerninhalte bleiben auch Administratoren verborgen (ADR 0011/0015).
    /// </summary>
    public async Task<List<AdminCertificateRow>> ListForAdminAsync(string actorId, string? filter = null, int limit = 200)
    {
        await _users.RequireRoleAsync(actorId, Roles.Admin);
        var sql = "SELECT * FROM certificates";
        if (filter == "revoked") sql += " WHERE status = 'revoked'";
        else if (filter is "pending" or "failed" or "cost_blocked" or "batched" or "anchoring" or "confirmed") sql += $" WHERE chain_status = '{filter}'";
        var rows = await _db.QueryAsync(sql + $" ORDER BY issued_at DESC LIMIT {Math.Clamp(limit, 1, 1000)}");
        return rows.Select(Map).Select(c => new AdminCertificateRow(c.Id, c.Number, c.ModuleScope == "global" ? c.ModuleName : "(privates Modul)", c.ModuleVersion, c.ModuleScope, c.IssuedAt,
            c.Status, c.ChainStatus, c.ChainNetwork, c.ModuleScope == "global" ? c.LearnerName : null)).ToList();
    }

    public sealed record AuditorFilter(string? Learner, string? Module, DateTime? From, DateTime? To, string? Status, string? Network, string? ChainStatus);

    /// <summary>
    /// Zertifikate, die ein Auditor sehen darf: ausschließlich zu <b>global veröffentlichten</b> Modulen (ADR 0015: Auditoren lesen globale Inhalte und Ergebnisse).
    /// Keine privaten oder nur freigegebenen Module, keine Quizantworten, keine Chatverläufe.
    /// </summary>
    public async Task<List<Certificate>> ListForAuditorAsync(string actorId, AuditorFilter f, int limit = 200)
    {
        await _users.RequireAnyRoleAsync(actorId, Roles.Auditor, Roles.Admin);
        var sql = "SELECT * FROM certificates WHERE module_scope = 'global'";
        var args = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(f.Learner)) { sql += " AND LOWER(learner_name) LIKE @l"; args.Add(("l", "%" + f.Learner.Trim().ToLowerInvariant() + "%")); }
        if (!string.IsNullOrWhiteSpace(f.Module)) { sql += " AND LOWER(module_name) LIKE @m"; args.Add(("m", "%" + f.Module.Trim().ToLowerInvariant() + "%")); }
        if (f.From is { } from) { sql += " AND issued_at >= @from"; args.Add(("from", from)); }
        if (f.To is { } to) { sql += " AND issued_at < @to"; args.Add(("to", to.AddDays(1))); }
        if (f.Status is "active" or "revoked") { sql += " AND status = @st"; args.Add(("st", f.Status)); }
        if (!string.IsNullOrWhiteSpace(f.Network)) { sql += " AND chain_network = @net"; args.Add(("net", f.Network)); }
        if (!string.IsNullOrWhiteSpace(f.ChainStatus)) { sql += " AND chain_status = @cs"; args.Add(("cs", f.ChainStatus)); }
        var rows = await _db.QueryAsync(sql + $" ORDER BY issued_at DESC LIMIT {Math.Clamp(limit, 1, 1000)}", args);
        return rows.Select(Map).ToList();
    }

    /// <summary>Ein Zertifikat für Auditoren (nur global veröffentlichte Module) — mit Protokolleintrag der Einsicht.</summary>
    public async Task<Certificate?> GetForAuditorAsync(string actorId, string id)
    {
        await _users.RequireAnyRoleAsync(actorId, Roles.Auditor, Roles.Admin);
        var c = await GetRawAsync(id);
        if (c is null || c.ModuleScope != "global") return null;
        await _users.WriteAuditAsync(actorId, "certificate.audit.view", c.Id, $"number={c.Number}");
        return c;
    }

    public async Task RecordVerificationAsync(string actorId, string detail) => await _users.WriteAuditAsync(actorId, "certificate.verify", "", detail);

    /// <summary>Eigene Prüfhistorie (Prüfungen und Einsichten) eines Auditors/Administrators.</summary>
    public async Task<List<AuditEntry>> VerificationHistoryAsync(string actorId, int limit = 100)
    {
        await _users.RequireAnyRoleAsync(actorId, Roles.Auditor, Roles.Admin);
        var rows = await _db.QueryAsync($"SELECT id, actor_user_id, action, target, detail, created_at FROM audit_log WHERE actor_user_id = @a AND action LIKE 'certificate.%' AND action <> 'certificate.issue' ORDER BY created_at DESC LIMIT {Math.Clamp(limit, 1, 500)}",
            new[] { ("a", (object?)actorId) });
        return rows.Select(r => new AuditEntry { Id = r["id"] ?? "", ActorUserId = r["actor_user_id"], Action = r["action"] ?? "", Target = r["target"] ?? "", Detail = r["detail"] ?? "", CreatedAt = DbValue.ToDateTime(r["created_at"]) }).ToList();
    }

    // ─── Persistenz ─────────────────────────────────────────────────────────

    private Task InsertAsync(Certificate c) => _db.ExecuteAsync("""
        INSERT INTO certificates (id, number, user_id, module_id, module_name, module_version, module_scope, result_id, learner_name, score_percent, pass_percent, issued_at,
                                  badge_jws, badge_hash, leaf_hash, status, chain_status, chain_network, chain_id, chain_updated_at)
        VALUES (@id, @number, @user_id, @module_id, @module_name, @module_version, @module_scope, @result_id, @learner_name, @score_percent, @pass_percent, @issued_at,
                @badge_jws, @badge_hash, @leaf_hash, 'active', @chain_status, @chain_network, @chain_id, @now)
        """, new (string, object?)[]
    {
        ("id", c.Id), ("number", c.Number), ("user_id", c.UserId), ("module_id", c.ModuleId), ("module_name", c.ModuleName), ("module_version", c.ModuleVersion), ("module_scope", c.ModuleScope),
        ("result_id", c.ResultId), ("learner_name", c.LearnerName), ("score_percent", c.ScorePercent), ("pass_percent", c.PassPercent), ("issued_at", c.IssuedAt),
        ("badge_jws", c.BadgeJws), ("badge_hash", c.BadgeHash), ("leaf_hash", c.LeafHash), ("chain_status", c.ChainStatus), ("chain_network", c.ChainNetwork), ("chain_id", c.ChainId), ("now", DateTime.UtcNow),
    });

    public static Certificate Map(Dictionary<string, string?> r) => new()
    {
        Id = r["id"] ?? "", Number = r["number"] ?? "", UserId = r["user_id"] ?? "", ModuleId = r["module_id"] ?? "", ModuleName = r["module_name"] ?? "", ModuleVersion = DbValue.ToInt(r["module_version"]),
        ModuleScope = r["module_scope"] ?? "private", ResultId = r.GetValueOrDefault("result_id"), LearnerName = r["learner_name"] ?? "", ScorePercent = DbValue.ToDouble(r["score_percent"]),
        PassPercent = DbValue.ToInt(r["pass_percent"]), IssuedAt = DbValue.ToDateTime(r["issued_at"]), BadgeJws = r["badge_jws"] ?? "", BadgeHash = r["badge_hash"] ?? "", LeafHash = r["leaf_hash"] ?? "",
        Status = r["status"] ?? "active", RevokedAt = r.GetValueOrDefault("revoked_at") is { Length: > 0 } ra ? DbValue.ToDateTime(ra) : null, RevokedReason = r.GetValueOrDefault("revoked_reason") ?? "", RevokedBy = r.GetValueOrDefault("revoked_by"),
        ChainStatus = r["chain_status"] ?? Domain.ChainStatus.None, ChainNetwork = r["chain_network"] ?? "", ChainId = DbValue.ToInt(r["chain_id"]), BatchId = r.GetValueOrDefault("batch_id") is { Length: > 0 } b ? b : null,
        MerkleProofJson = r.GetValueOrDefault("merkle_proof_json") is { Length: > 0 } mp ? mp : null, ChainError = r.GetValueOrDefault("chain_error") ?? "", ChainAttempts = DbValue.ToInt(r["chain_attempts"]),
        ChainUpdatedAt = r.GetValueOrDefault("chain_updated_at") is { Length: > 0 } cu ? DbValue.ToDateTime(cu) : null, AnchoredAt = r.GetValueOrDefault("anchored_at") is { Length: > 0 } aa ? DbValue.ToDateTime(aa) : null,
    };
}
