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
    /// <summary>Bei einem signierten CLR: wahr, wenn jede eingebettete kompakte JWS (ClrSubjectJwtPayload) eine gültige ES256-Signatur dieses Ausstellers trägt und ein Credential beschreibt; sonst null (keine eingebetteten JWS).</summary>
    public bool? EmbeddedSignaturesValid { get; set; }
    public int EmbeddedCount { get; set; }
    public bool Valid => SignatureValid && IssuerValid && ContentMatches != false && EmbeddedSignaturesValid != false && !Revoked;
}

/// <summary>
/// Zertifikate und Open Badges 3.0 (ADR 0021): Ausstellung nach bestandenem Modulquiz, Signierung (VC-JWT, ES256), Abruf, Widerruf, öffentliche Prüfung.
/// Die Quizbewertung bleibt im <c>QuizService</c>; dieser Dienst wird nur mit dem dort berechneten, serverseitig verifizierten Ergebnis aufgerufen.
/// </summary>
public sealed partial class CertificateService
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

    /// <summary>Nur Entwicklung: Ursprung der zuletzt gesehenen Anfrage (Schema, Host, Port), solange PUBLIC_BASE_URL nicht gesetzt ist. Wird von der Middleware in Program.cs gesetzt.</summary>
    public string? ObservedOrigin { get; set; }
    /// <summary>Öffentliche Basis-URL: PUBLIC_BASE_URL; ohne Konfiguration in der Entwicklung die tatsächliche Adresse der Anwendung, sonst http://localhost.</summary>
    public string BaseUrl => !string.IsNullOrEmpty(_cfg.Blockchain.PublicBaseUrl) ? _cfg.Blockchain.PublicBaseUrl : ObservedOrigin ?? "http://localhost";
    public string IssuerId => BaseUrl + "/badges/issuer";
    public string KeyId => IssuerId + "#" + _signer.KeyId;
    /// <summary>
    /// <c>kid</c> neuer Credentials: eine URL, die <b>genau ein JWK</b> liefert (<c>GET /badges/keys/&lt;Schlüssel-ID&gt;</c>). Der 1EdTech-Validator ruft die <c>kid</c>-URL ab und liest das Ergebnis direkt als JWK
    /// (<c>kty</c>, <c>x</c>, <c>y</c>); die frühere Form <c>…/badges/issuer#&lt;ID&gt;</c> lieferte das Ausstellerprofil statt eines Schlüssels. Ältere Credentials behalten ihre <c>kid</c>.
    /// </summary>
    public string JwtKeyId => BaseUrl + "/badges/keys/" + _signer.KeyId;

    /// <summary>
    /// Lokale Kontextdefinition für <c>credentialStatus.type = 1EdTechRevocationList</c>: Der veröffentlichte OB-3.0.3-Kontext definiert den Begriff nicht, die JSON-LD-Prüfung der
    /// 1EdTech-Verifier meldet ihn sonst als „Undefined JSON-LD term“. Das OB-3.0-Schema erlaubt lokale Kontext-Maps an dieser Stelle.
    /// </summary>
    public static JsonObject RevocationListContext() => new() { ["1EdTechRevocationList"] = "https://purl.imsglobal.org/spec/ob/v3p0/vocab.html#1EdTechRevocationList" };

    /// <summary>Embedded-Proof-Hülle um das VC-JWT: Pflichtfelder eines Linked-Data-Proofs (<c>proofPurpose</c>, <c>verificationMethod</c>), damit Verifier die Hülle nicht als unvollständigen Data-Integrity-Proof lesen. Die Signatur selbst ist das unveränderte <c>jwt</c>.</summary>
    private JsonObject ProofEnvelope(string jws, DateTime created) => new()
    {
        ["type"] = "JwtProof2020", ["proofPurpose"] = "assertionMethod", ["verificationMethod"] = KeyId,
        ["created"] = created.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), ["jwt"] = jws,
    };
    public BadgeSigner Signer => _signer;

    // ─── Ausstellung ────────────────────────────────────────────────────────

    /// <summary>
    /// Stellt nach einem bestandenen, sitzungsverifizierten Modulquiz ein Zertifikat aus (idempotent je Benutzer, Modul und Version).
    /// Fehler dürfen das Quizergebnis nie gefährden — der Aufrufer fängt Ausnahmen.
    /// </summary>
    public async Task<Certificate?> TryIssueAsync(QuizResultRecord result, string moduleName, string moduleScope, int? moduleVersion, ExamSnapshot? snapshot = null, IReadOnlyList<CompetencyEvidence>? evidence = null)
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
            SnapshotId = snapshot?.Id, SnapshotHash = snapshot?.Hash ?? "", CompletedAt = snapshot is null ? null : result.CompletedAt,
            IssueSource = "exam", IssuedByName = "Prüfsystem",
        };
        var credential = BuildCredential(cert, snapshot, evidence);
        cert.BadgeJws = _signer.SignCredential(credential, JwtKeyId);
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

    /// <summary>
    /// IdentityObject nach Open Badges 3.0: Pflichtfeld ist <c>identityHash</c> (bei <c>hashed = false</c> der Klartext). Ältere, bereits signierte Badges tragen noch das Feld
    /// <c>identity</c> und bleiben unverändert; <paramref name="conformant"/> wählt die schemakonforme Form für neu ausgestellte Credentials.
    /// </summary>
    internal static JsonObject Identity(string name, bool conformant) => conformant
        ? new JsonObject { ["type"] = "IdentityObject", ["hashed"] = false, ["identityType"] = "name", ["identityHash"] = name }
        : new JsonObject { ["type"] = "IdentityObject", ["hashed"] = false, ["identityType"] = "name", ["identity"] = name };

    private static string NewNumber(string prefix = "LC")
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";   // Crockford-Base32 (ohne verwechselbare Zeichen)
        var bytes = RandomNumberGenerator.GetBytes(8);
        return $"{prefix}-{DateTime.UtcNow.Year}-" + new string(bytes.Select(b => alphabet[b % 32]).ToArray());
    }

    /// <summary>Open-Badge-3.0-Credential (W3C VC 2.0). Der Inhalt ist nach der Signierung unveränderlich; die Blockchain-Verankerung steht in einem separaten Nachweis-Paket.</summary>
    public JsonObject BuildCredential(Certificate c, ExamSnapshot? snapshot = null, IReadOnlyList<CompetencyEvidence>? evidence = null, bool? conformantIdentity = null)
    {
        var achievementId = $"{BaseUrl}/badges/achievements/{c.ModuleId}/{c.ModuleVersion}";
        var demonstrated = (evidence ?? Array.Empty<CompetencyEvidence>()).Where(e => e.Status == CompetencyStatus.Demonstrated).ToList();
        var criteriaNarrative = $"Das Modulquiz wurde mit mindestens {c.PassPercent} % bestanden."
            + string.Concat(demonstrated.Select(e => $" Kompetenz „{e.Name}“: mindestens {e.PassPercent} % der Punkte bei mindestens {e.MinQuestions} geprüften Fragen."));
        var credential = new JsonObject
        {
            ["@context"] = new JsonArray("https://www.w3.org/ns/credentials/v2", "https://purl.imsglobal.org/spec/ob/v3p0/context-3.0.3.json", RevocationListContext()),
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
                ["id"] = SubjectId(c.UserId), ["type"] = new JsonArray("AchievementSubject"),
                ["identifier"] = new JsonArray(Identity(c.LearnerName, conformantIdentity ?? snapshot is not null)),
                ["achievement"] = new JsonObject
                {
                    ["id"] = achievementId, ["type"] = new JsonArray("Achievement"), ["achievementType"] = "Course", ["name"] = c.ModuleName,
                    ["description"] = $"Das Lernmodul „{c.ModuleName}“ wurde erfolgreich abgeschlossen.",
                    ["criteria"] = new JsonObject { ["id"] = achievementId + "/criteria", ["narrative"] = criteriaNarrative },
                },
                ["result"] = new JsonArray(new JsonObject
                {
                    ["type"] = new JsonArray("Result"), ["status"] = "Completed", ["value"] = c.ScorePercent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " % — " + GermanGrade.Text(c.ScorePercent),
                }),
            },
            ["credentialStatus"] = new JsonObject { ["id"] = BaseUrl + "/api/public/revocation-list", ["type"] = "1EdTechRevocationList" },
        };
        if (snapshot is null) return credential;

        // Kompetenznachweise (ADR 0031) mit den Standardstrukturen von Open Badges 3.0: Alignment (ceasn:Competency), ResultDescription/Result und Evidence.
        // Es werden nur tatsächlich nachgewiesene Kompetenzen bescheinigt; nicht oder nicht ausreichend geprüfte stehen nur im Prüfungssnapshot.
        var achievement = (JsonObject)credential["credentialSubject"]!["achievement"]!;
        if (c.ModuleVersion > 0) achievement["version"] = c.ModuleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (c.CompletedAt is { } done) credential["credentialSubject"]!["activityEndDate"] = done.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var results = (JsonArray)credential["credentialSubject"]!["result"]!;
        if (demonstrated.Count > 0)
        {
            var alignments = new JsonArray(); var descriptions = new JsonArray();
            foreach (var e in demonstrated)
            {
                var url = $"{achievementId}/competencies/{Uri.EscapeDataString(e.Id)}";
                alignments.Add(new JsonObject
                {
                    ["type"] = new JsonArray("Alignment"), ["targetName"] = e.Name, ["targetUrl"] = url, ["targetType"] = "ceasn:Competency", ["targetCode"] = e.Id,
                    ["targetDescription"] = e.Description.Length > 0 ? e.Description : e.Name,
                });
                descriptions.Add(new JsonObject
                {
                    ["id"] = url + "#result", ["type"] = new JsonArray("ResultDescription"), ["name"] = e.Name, ["resultType"] = "Percent",
                    ["requiredValue"] = e.PassPercent.ToString(System.Globalization.CultureInfo.InvariantCulture), ["valueMin"] = "0", ["valueMax"] = "100",
                });
                results.Add(new JsonObject
                {
                    ["type"] = new JsonArray("Result"), ["resultDescription"] = url + "#result", ["status"] = "Completed",
                    ["value"] = e.Percent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                });
            }
            achievement["alignment"] = alignments;
            achievement["resultDescription"] = descriptions;
        }
        credential["evidence"] = new JsonArray(new JsonObject
        {
            ["id"] = $"{BaseUrl}/api/public/certificates/{c.Id}/evidence", ["type"] = new JsonArray("Evidence"), ["name"] = "Prüfungsnachweis",
            ["description"] = $"Unveränderlicher Nachweissnapshot des verifizierten Prüfungsergebnisses (SHA-256 {snapshot.Hash}).",
            ["narrative"] = $"Serverseitig bewertetes Modulquiz; {demonstrated.Count} von {evidence?.Count ?? 0} Kompetenzen nachgewiesen. Snapshot-Hash: {snapshot.Hash}.", ["genre"] = "Exam",
        });
        return credential;
    }

    /// <summary>Öffentlicher Prüfungsnachweis zu einem Zertifikat (der Link steht im Credential): der unveränderte Snapshot ohne Lernendendaten und Antworten; null ohne Snapshot.</summary>
    public async Task<string?> GetPublicEvidenceJsonAsync(string certificateId)
    {
        var c = await GetRawAsync(certificateId);
        if (c?.SnapshotId is null) return null;
        var rows = await _db.QueryAsync("SELECT snapshot_json FROM exam_snapshots WHERE id = @id", new[] { ("id", (object?)c.SnapshotId) });
        return rows.Count == 0 ? null : rows[0]["snapshot_json"];
    }

    /// <summary>
    /// Öffentliche Achievement-/Criteria-Ressource (<c>/badges/achievements/…</c>), die <c>achievement.id</c> bzw. <c>criteria.id</c> der ausgestellten Credentials auflöst.
    /// Quelle ist ausschließlich das bei der Ausstellung signierte Credential (historischer Stand, nichts wird aus aktuellen Einstellungen neu berechnet).
    /// Veröffentlicht werden nur die nicht personenbezogenen Kernangaben (Typ, Name, Beschreibung, Version, Kriterientext) — keine Kompetenzen, Ergebnisse, Lernenden- oder Lerninhalte.
    /// </summary>
    public async Task<JsonObject?> GetPublicAchievementAsync(string ownerId, int version, bool topic, bool criteriaOnly)
    {
        if (!UserService.IsGuid(ownerId) || version < 0) return null;
        var rows = await _db.QueryAsync($"SELECT badge_jws FROM certificates WHERE module_id = @m AND module_version = @v AND kind {(topic ? "=" : "<>")} 'topic' ORDER BY issued_at DESC LIMIT 5",
            new (string, object?)[] { ("m", ownerId), ("v", version) });
        var expectedId = $"{BaseUrl}/badges/achievements/{(topic ? "topics/" : "")}{ownerId}/{version}";
        foreach (var r in rows)
        {
            JsonObject? ach;
            try { ach = BadgeSigner.CredentialFromJws(r["badge_jws"]!)["credentialSubject"]?["achievement"] as JsonObject; }
            catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException) { continue; }
            if (ach?["id"]?.GetValue<string>() != expectedId) continue;   // nur Credentials, die genau diese URL ausgewiesen haben
            var criteria = ach["criteria"] as JsonObject;
            var context = new JsonArray("https://purl.imsglobal.org/spec/ob/v3p0/context-3.0.3.json");
            if (criteriaOnly)
                return criteria is null ? null : new JsonObject { ["@context"] = context, ["id"] = criteria["id"]?.DeepClone(), ["narrative"] = criteria["narrative"]?.DeepClone() };
            var o = new JsonObject { ["@context"] = context };
            foreach (var k in new[] { "id", "type", "achievementType", "name", "description", "version" }) if (ach[k] is { } v) o[k] = v.DeepClone();
            if (criteria is not null) o["criteria"] = new JsonObject { ["id"] = criteria["id"]?.DeepClone(), ["narrative"] = criteria["narrative"]?.DeepClone() };
            return o;
        }
        return null;
    }

    /// <summary>
    /// Open Badge als JSON. Neue Credentials: das reine Credential (kein erfundener Proof-Typ; das JWT ist der Proof und wird getrennt geliefert, OB 3.0 §8.2.2).
    /// Altbestand (Profil <c>vc+jwt</c>): Credential plus die ursprüngliche Hülle <c>proof.jwt</c>, unverändert.
    /// </summary>
    public JsonObject SignedBadgeJson(Certificate c)
    {
        var credential = BadgeSigner.CredentialFromJws(c.BadgeJws);
        // Altbestand (typ vc+jwt) behält seine Hülle; neue Credentials tragen keinen erfundenen Proof-Typ: Das JWT ist der Proof (OB 3.0 §8.2.2: proof darf entfallen) und wird getrennt geliefert.
        if (BadgeSigner.IsLegacyProfile(c.BadgeJws)) credential["proof"] = ProofEnvelope(c.BadgeJws!, c.IssuedAt);
        return credential;
    }

    /// <summary>Ersetzt in <c>credentialSubject.verifiableCredential</c> kompakte JWS durch das jeweilige Credential (Objekt), damit JSON- und JWT-Darstellung eines CLR vergleichbar sind.</summary>
    internal static JsonNode? ExpandEmbedded(JsonNode? node)
    {
        if (node is not JsonObject obj || obj["credentialSubject"]?["verifiableCredential"] is not JsonArray) return node;
        var copy = (JsonObject)obj.DeepClone();
        var arr = (JsonArray)copy["credentialSubject"]!["verifiableCredential"]!;
        for (var i = 0; i < arr.Count; i++)
            if (arr[i] is JsonValue v && v.TryGetValue<string>(out var jws)) arr[i] = BadgeSigner.CredentialFromJws(jws);
        return copy;
    }

    /// <summary>Pseudonyme Subjekt-ID (<c>credentialSubject.id</c>, JWT-Claim <c>sub</c>): stabil je Benutzer, ohne die interne Benutzer-ID preiszugeben.</summary>
    internal static string SubjectId(string userId)
    {
        var h = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("learny-subject:" + userId));
        return "urn:uuid:" + new Guid(h.AsSpan(0, 16));
    }

    /// <summary>
    /// Eigenständiges, druckbares Zertifikat als HTML (ohne Skripte, ohne externe Ressourcen): Name, Modul, Ergebnis, Note, Datum, Nummer, Aussteller, Prüf-Link.
    /// Alle Werte werden HTML-kodiert. Im Browser über „Drucken → Als PDF speichern“ zu einem PDF machen. Das maßgebliche, signierte Dokument ist das Open Badge.
    /// </summary>
    public string RenderCertificateHtml(Certificate c)
    {
        static string E(string? v) => System.Net.WebUtility.HtmlEncode(v ?? "");
        var grade = GermanGrade.Text(c.ScorePercent);
        var verify = $"{BaseUrl}/verify/{c.Id}";
        var revoked = c.IsRevoked ? $"<p class=\"revoked\">WIDERRUFEN am {c.RevokedAt?.ToString("dd.MM.yyyy")} — {E(c.RevokedReason)}</p>" : "";
        return $$$"""
            <!doctype html>
            <html lang="de"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Zertifikat {{{E(c.Number)}}}</title>
            <style>
            body{font-family:Segoe UI,Arial,sans-serif;background:#f4f1ea;margin:0;padding:2rem;color:#1d2b2a}
            .cert{max-width:46rem;margin:0 auto;background:#fff;border:8px double #2aa79b;padding:2.5rem 2rem;text-align:center}
            h1{margin:.2rem 0 1.2rem;font-size:2rem;letter-spacing:.04em}
            .name{font-size:1.6rem;font-weight:700;margin:.6rem 0}.module{font-size:1.35rem;font-weight:600;margin:.6rem 0}
            .grade{display:inline-block;margin:1rem 0;padding:.4rem 1rem;border:2px solid #2aa79b;border-radius:2rem;font-weight:700}
            .meta{color:#4a5a59;font-size:.9rem;line-height:1.6;margin-top:1.4rem;word-break:break-all}.revoked{color:#b3261e;font-weight:700}
            .jwt{margin-top:1.5rem;text-align:left}.jwt textarea{width:100%;font:.75rem monospace;box-sizing:border-box}.jwt button{padding:.4rem 1rem;cursor:pointer}
            @media print{.noprint{display:none}body{background:#fff;padding:0}.cert{border-width:6px}}
            </style></head><body><main class="cert">
            <p>LearnCards</p><h1>Zertifikat</h1>
            <p>Hiermit wird bestätigt, dass</p><p class="name">{{{E(c.LearnerName)}}}</p>
            <p>das Lernmodul</p><p class="module">{{{E(c.ModuleName)}}}{{{(c.ModuleVersion > 0 ? " (Version " + c.ModuleVersion + ")" : "")}}}</p>
            <p>erfolgreich abgeschlossen hat.</p>
            <p class="grade">Ergebnis {{{c.ScorePercent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}}} % · {{{E(grade)}}} · bestanden (Bestehensgrenze {{{c.PassPercent}}} %)</p>
            {{{revoked}}}
            <p class="meta">Ausgestellt am {{{c.IssuedAt.ToLocalTime():dd.MM.yyyy}}} · Zertifikatsnummer {{{E(c.Number)}}}<br>Aussteller: {{{E(IssuerId)}}}<br>Prüfung online: {{{E(verify)}}}<br>Digitales Open Badge (signiert) über „Badge herunterladen“ in LearnCards.</p>
            <section class="noprint jwt"><h2>Digitaler Nachweis (JWT)</h2>
            <p>Zum Prüfen unter <a href="{{{E(BaseUrl)}}}/verify">{{{E(BaseUrl)}}}/verify</a> einfügen.</p>
            <textarea id="jwt" readonly rows="5" aria-label="Badge als JWT">{{{E(c.BadgeJws)}}}</textarea>
            <p><button type="button" id="cp">JWT kopieren</button> <span id="ok" role="status"></span></p></section>
            </main>
            <script>
            document.getElementById("cp").onclick=function(){var t=document.getElementById("jwt");t.select();var done=function(){document.getElementById("ok").textContent="Kopiert."};
            if(navigator.clipboard&&window.isSecureContext){navigator.clipboard.writeText(t.value).then(done)}else{document.execCommand("copy");done()}};
            </script></body></html>
            """;
    }

    // ─── Abruf ──────────────────────────────────────────────────────────────

    public async Task<List<Certificate>> ListForUserAsync(string userId) =>
        (await _db.QueryAsync("SELECT * FROM certificates WHERE user_id = @u AND is_test = 0 ORDER BY issued_at DESC", new[] { ("u", (object?)userId) })).Select(Map).ToList();

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
            badge_hash = c.BadgeHash, snapshot_hash = c.SnapshotHash.Length > 0 ? c.SnapshotHash : null, kind = c.Kind, issuer = IssuerId,
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
        if (string.IsNullOrWhiteSpace(jws) && embedded is JsonObject plain && plain["id"]?.GetValue<string>() is { } credentialUrl
            && System.Text.RegularExpressions.Regex.Match(credentialUrl, @"/verify/([0-9a-fA-F-]{36})(/clr)?$") is { Success: true } m && await GetRawAsync(m.Groups[1].Value) is { } known)
        {
            // JSON ohne Proof (neues Profil, OB 3.0 §8.2.2): Die Signatur wird an der bei Learny gespeicherten JWS geprüft; die eingereichten Inhalte müssen dem signierten Credential entsprechen.
            jws = m.Groups[2].Success ? known.ClrJws : known.BadgeJws;
        }
        if (string.IsNullOrWhiteSpace(jws)) { v.SignatureError ??= "Es wurde keine Signatur (proof.jwt) gefunden. Bitte das JWT (badge.jwt / clr.jwt) einfügen."; return v; }

        var jv = _signer.Verify(jws);
        v.SignatureValid = jv.SignatureValid; v.SignatureError = jv.Error;
        var credPayload = jv.Payload is JsonObject po && po["vc"] is JsonObject vcClaim ? vcClaim : jv.Payload;   // Profil ADR 0038: Credential im Claim "vc"
        v.IssuerValid = jv.SignatureValid && (credPayload?["issuer"]?["id"]?.GetValue<string>() is { } iss && (iss == IssuerId || iss.EndsWith("/badges/issuer", StringComparison.Ordinal)));
        // Maßgeblich ist die gültige Signatur mit dem Schlüssel dieser Installation; die Aussteller-URL darf sich ändern (Test-/Entwicklungssystem, Umzug der Domain).
        if (jv.SignatureValid && embedded is not null && credPayload is JsonObject pl)
        {
            var cred = (JsonObject)pl.DeepClone();
            if (ReferenceEquals(credPayload, jv.Payload) && !BadgeSigner.IsLegacyProfile(jws)) foreach (var k in new[] { "iss", "jti", "nbf", "sub" }) cred.Remove(k);   // früheres Zwischenformat (Credential auf oberster Ebene)
            // Das CLR-JSON enthält die eingebetteten Credentials als Objekte, das signierte CLR als kompakte JWS: für den Vergleich beides in Objektform bringen
            v.ContentMatches = JsonNode.DeepEquals(ExpandEmbedded(embedded), ExpandEmbedded(cred));
        }
        if (jv.SignatureValid && credPayload?["credentialSubject"]?["verifiableCredential"] is { } embeddedClaim)
        {
            // CLR 2.0 B.2.2: eingebettete Credentials sind kompakte JWS — jede einzeln prüfen, nicht nur die äußere Signatur
            var items = embeddedClaim is JsonArray arr ? arr.ToList() : new List<JsonNode?> { embeddedClaim };
            var jwsItems = items.Where(i => i is JsonValue val && val.TryGetValue<string>(out _)).ToList();
            if (jwsItems.Count > 0)
            {
                v.EmbeddedCount = jwsItems.Count;
                v.EmbeddedSignaturesValid = jwsItems.Count == items.Count && jwsItems.All(i => { var iv = _signer.Verify(i!.GetValue<string>()); return iv.SignatureValid && iv.Payload is JsonObject ip && (ip["vc"] as JsonObject ?? ip) is { } ivc && ivc["type"] is not null && ivc["credentialSubject"] is JsonObject; });   // Profil ADR 0038 (Claim vc) oder Altbestand (Credential auf oberster Ebene)   // gültige Signatur UND ein Credential mit type/credentialSubject (Profil ADR 0038)
            }
        }
        v.BadgeHash = BadgeSigner.HashHex(jws);
        if (credPayload is not null)
        {
            var subject = credPayload["credentialSubject"];
            var firstAchievement = subject?["achievement"] is JsonArray achievements ? achievements.FirstOrDefault() : subject?["achievement"];   // Open Badge: ein Achievement; CLR: Liste (erstes = Thema)
            v.ModuleName = firstAchievement?["name"]?.GetValue<string>();
            if (DateTime.TryParse(credPayload["validFrom"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var from)) v.IssuedAt = from;
        }
        var rows = await _db.QueryAsync("SELECT * FROM certificates WHERE badge_hash = @h OR clr_hash = @h", new[] { ("h", (object?)v.BadgeHash) });   // Badge oder CLR (Themenabschluss)
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

    /// <summary>
    /// Öffentliche Widerrufsliste (<c>1EdTechRevocationList</c>). Zwei gleichwertige Darstellungen desselben Registers:
    /// <c>revokedCredential</c> (<c>[{id, revoked:true}]</c>, vom 1EdTech-Validator gelesen) und das bisherige <c>revoked_credentials</c> (mit Grund) für bestehende Clients.
    /// Als <see cref="JsonObject"/> ausgegeben, damit die Schlüssel von der snake_case-Namenspolitik unberührt bleiben.
    /// </summary>
    public async Task<JsonObject> RevocationListAsync()
    {
        var rows = await _db.QueryAsync("SELECT id, revoked_reason, kind FROM certificates WHERE status = 'revoked' ORDER BY id");
        var entries = rows.SelectMany(r => (r["kind"] == "topic" ? new[] { "", "/clr" } : new[] { "" }).Select(sfx => (Id: $"{BaseUrl}/verify/{r["id"]}{sfx}", Reason: r["revoked_reason"] ?? ""))).ToList();
        var ctx = new JsonArray("https://www.w3.org/ns/credentials/v2", "https://purl.imsglobal.org/spec/ob/v3p0/context-3.0.3.json");
        var validator = new JsonArray(); var legacy = new JsonArray();
        foreach (var (id, reason) in entries)
        {
            validator.Add(new JsonObject { ["id"] = id, ["revoked"] = true });
            legacy.Add(new JsonObject { ["id"] = id, ["revocation_reason"] = reason });
        }
        return new JsonObject { ["context"] = ctx, ["type"] = "1EdTechRevocationList", ["issuer"] = IssuerId, ["revokedCredential"] = validator, ["revoked_credentials"] = legacy };
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
        var sql = "SELECT * FROM certificates WHERE is_test = 0";
        if (filter == "revoked") sql += " AND status = 'revoked'";
        else if (filter is "pending" or "failed" or "cost_blocked" or "batched" or "anchoring" or "confirmed") sql += $" AND chain_status = '{filter}'";
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

    internal Task InsertAsync(Certificate c) => _db.ExecuteAsync("""
        INSERT INTO certificates (id, number, user_id, module_id, module_name, module_version, module_scope, result_id, learner_name, score_percent, pass_percent, issued_at,
                                  badge_jws, badge_hash, leaf_hash, status, chain_status, chain_network, chain_id, chain_updated_at, snapshot_id, snapshot_hash, kind, clr_jws, clr_hash, completed_at,
                                  is_test, issued_by, issued_by_name, issue_source, parent_id)
        VALUES (@id, @number, @user_id, @module_id, @module_name, @module_version, @module_scope, @result_id, @learner_name, @score_percent, @pass_percent, @issued_at,
                @badge_jws, @badge_hash, @leaf_hash, 'active', @chain_status, @chain_network, @chain_id, @now, @snapshot_id, @snapshot_hash, @kind, @clr_jws, @clr_hash, @completed_at,
                @is_test, @issued_by, @issued_by_name, @issue_source, @parent_id)
        """, new (string, object?)[]
    {
        ("id", c.Id), ("number", c.Number), ("user_id", c.UserId), ("module_id", c.ModuleId), ("module_name", c.ModuleName), ("module_version", c.ModuleVersion), ("module_scope", c.ModuleScope),
        ("result_id", c.ResultId), ("learner_name", c.LearnerName), ("score_percent", c.ScorePercent), ("pass_percent", c.PassPercent), ("issued_at", c.IssuedAt),
        ("badge_jws", c.BadgeJws), ("badge_hash", c.BadgeHash), ("leaf_hash", c.LeafHash), ("chain_status", c.ChainStatus), ("chain_network", c.ChainNetwork), ("chain_id", c.ChainId), ("now", DateTime.UtcNow), ("snapshot_id", c.SnapshotId), ("snapshot_hash", c.SnapshotHash),
        ("kind", c.Kind), ("clr_jws", c.ClrJws), ("clr_hash", c.ClrHash), ("completed_at", c.CompletedAt),
        ("is_test", c.IsTest ? 1 : 0), ("issued_by", c.IssuedBy), ("issued_by_name", c.IssuedByName), ("issue_source", c.IssueSource), ("parent_id", c.ParentId),
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
        SnapshotId = r.GetValueOrDefault("snapshot_id") is { Length: > 0 } sid ? sid : null, SnapshotHash = r.GetValueOrDefault("snapshot_hash") ?? "",
        Kind = r.GetValueOrDefault("kind") is { Length: > 0 } kd ? kd : "module", ClrJws = r.GetValueOrDefault("clr_jws") is { Length: > 0 } cj ? cj : null, ClrHash = r.GetValueOrDefault("clr_hash") ?? "",
        CompletedAt = r.GetValueOrDefault("completed_at") is { Length: > 0 } ca ? DbValue.ToDateTime(ca) : null,
        IsTest = DbValue.ToInt(r.GetValueOrDefault("is_test")) == 1, IssuedBy = r.GetValueOrDefault("issued_by") is { Length: > 0 } ib ? ib : null, IssuedByName = r.GetValueOrDefault("issued_by_name") ?? "",
        IssueSource = r.GetValueOrDefault("issue_source") is { Length: > 0 } isrc ? isrc : "unknown", ParentId = r.GetValueOrDefault("parent_id") is { Length: > 0 } pid ? pid : null,
    };
}
