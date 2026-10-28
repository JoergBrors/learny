using System.Security.Cryptography.X509Certificates;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Services.Pki;

namespace LearnCards.Web.Services.Certificates;

/// <summary>
/// Zentrales Ausstellungsregister für Administratoren (ADR 0037). Es speichert nichts doppelt: Die Zeilen der Tabelle <c>certificates</c> (mit den Registerspalten
/// <c>is_test</c>, <c>issued_by</c>, <c>issued_by_name</c>, <c>issue_source</c>, <c>parent_id</c>), die PDF-Zeilen (<c>certificate_pdfs</c>), die PKI-Zertifikate (<c>pki_certificates</c>) und das
/// <c>audit_log</c> sind die Quellen. Listen laden nie die Credentials (JWS) selbst. Jeder Zugriff prüft die Administratorrolle im Dienst gegen die Datenbank.
/// </summary>
public sealed class CredentialRegisterService
{
    private readonly IDatabase _db;
    private readonly UserService _users;
    private readonly CertificateService _certs;
    private readonly PkiService _pki;

    public CredentialRegisterService(IDatabase db, UserService users, CertificateService certs, PkiService pki) { _db = db; _users = users; _certs = certs; _pki = pki; }

    public sealed class Query
    {
        public string? Text { get; set; }          // allgemeine Suche: ID, Nummer, Fingerprint (Präfix), Empfänger, Aussteller, Modul/Thema
        public string? Id { get; set; }
        public string? Fingerprint { get; set; }   // SHA-256 (Hex) des Badges/CLR bzw. des PDFs, vollständig oder Präfix (mind. 8 Zeichen)
        public string? Thumbprint { get; set; }    // X.509: SHA-1-Thumbprint oder SHA-256-Fingerabdruck des Signaturzertifikats
        public string? Number { get; set; }
        public string? Recipient { get; set; }
        public string? Issuer { get; set; }
        public string? Module { get; set; }
        public string? Status { get; set; }        // active | revoked
        public string? Test { get; set; }          // test | regular
        public string? Kind { get; set; }          // badge | master | clr | pdf
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public string? Sort { get; set; }          // desc (Standard) | asc
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 25;
    }

    public sealed record Row(string Id, string Number, string Kind, string TypeLabel, bool IsTest, string Title, int Version, string Recipient, string? IssuerId, string IssuerName, string Source,
        DateTime IssuedAt, string Fingerprint, string? ClrFingerprint, string? PdfSha256, string? Thumbprint, bool HasPdf, string Status, DateTime? RevokedAt, string? ParentId);

    public sealed record Page(IReadOnlyList<Row> Items, int Total, int PageNumber, int PageSize);

    public sealed record ChildRef(string Id, string Number, string Title, string Status);

    public sealed record AuditRow(DateTime At, string Action, string? ActorId, string ActorName, string Detail);

    public sealed record Detail(Row Row, string? RevokedByName, string RevokedReason, string Alg, string KeyId, string Profile, bool SignatureValid, string? SignatureError, bool? ClrSignatureValid,
        string VerifyUrl, string? PkiCertificateId, string? PkiStatus, IReadOnlyList<ChildRef> Children, ChildRef? Parent, IReadOnlyList<AuditRow> Audit, string Impact, ValidationView Validation);

    /// <summary>Lokale Prüfstatus (nie als externe 1EdTech-Prüfung zu lesen). Zustände: Gültig, Ungültig, Nicht geprüft, Schlüssel nicht erreichbar, Statusprüfung nicht möglich, Widerrufen.</summary>
    public sealed record ValidationView(string Format, string Signature, string Issuer, string KeyUrl, string KeyAccess, string Revocation, string External);

    public sealed record RevokeOutcome(bool Changed, string Id, string Number, string Status);

    private const string SelectRow = """
        SELECT c.id, c.number, c.kind, c.is_test, c.module_name, c.module_version, c.learner_name, c.issued_by, c.issued_by_name, c.issue_source, c.issued_at,
               c.badge_hash, c.clr_hash, c.status, c.revoked_at, c.parent_id, p.pdf_sha256, p.signer_thumbprint
        FROM certificates c LEFT JOIN certificate_pdfs p ON p.certificate_id = c.id
        """;

    private static string Hex(string? s) => new string((s ?? "").Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();

    private static Row MapRow(Dictionary<string, string?> r)
    {
        var isTopic = r["kind"] == "topic";
        var isTest = DbValue.ToInt(r.GetValueOrDefault("is_test")) == 1;
        var pdf = r.GetValueOrDefault("pdf_sha256") is { Length: > 0 } ps ? ps : null;
        return new Row(r["id"] ?? "", r["number"] ?? "", isTopic ? "topic" : "module", isTopic ? "Master-Badge + CLR" : "Open Badge (Modul)", isTest, r["module_name"] ?? "", DbValue.ToInt(r["module_version"]),
            r["learner_name"] ?? "", r.GetValueOrDefault("issued_by") is { Length: > 0 } ib ? ib : null,
            r.GetValueOrDefault("issued_by_name") is { Length: > 0 } inn ? inn : (r.GetValueOrDefault("issue_source") == "exam" ? "Prüfsystem" : "unbekannt (Altbestand)"),
            r.GetValueOrDefault("issue_source") is { Length: > 0 } src ? src : "unknown", DbValue.ToDateTime(r["issued_at"]), r["badge_hash"] ?? "",
            isTopic && r.GetValueOrDefault("clr_hash") is { Length: > 0 } ch ? ch : null, pdf, r.GetValueOrDefault("signer_thumbprint") is { Length: > 0 } tp ? tp : null, pdf is not null,
            r["status"] ?? "active", r.GetValueOrDefault("revoked_at") is { Length: > 0 } ra ? DbValue.ToDateTime(ra) : null, r.GetValueOrDefault("parent_id") is { Length: > 0 } pid ? pid : null);
    }

    public async Task<Page> SearchAsync(string actorId, Query q)
    {
        await _users.RequireRoleAsync(actorId, Roles.Admin);
        var where = new List<string>(); var args = new List<(string, object?)>();
        void Like(string col, string param, string? v) { if (!string.IsNullOrWhiteSpace(v)) { where.Add($"LOWER({col}) LIKE @{param}"); args.Add((param, "%" + EscapeLike(v.Trim().ToLowerInvariant()) + "%")); } }
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var t = q.Text.Trim().ToLowerInvariant(); var hex = Hex(t);
            var parts = new List<string> { "LOWER(c.number) LIKE @t", "LOWER(c.learner_name) LIKE @t", "LOWER(c.issued_by_name) LIKE @t", "LOWER(c.module_name) LIKE @t", "LOWER(CAST(c.id AS TEXT)) LIKE @t" };
            args.Add(("t", "%" + EscapeLike(t) + "%"));
            if (hex.Length >= 8) { parts.Add("c.badge_hash LIKE @hp"); parts.Add("c.clr_hash LIKE @hp"); parts.Add("LOWER(p.pdf_sha256) LIKE @hp"); args.Add(("hp", hex + "%")); }
            where.Add("(" + string.Join(" OR ", parts) + ")");
        }
        if (!string.IsNullOrWhiteSpace(q.Id)) { where.Add("LOWER(CAST(c.id AS TEXT)) = @id"); args.Add(("id", q.Id.Trim().ToLowerInvariant())); }
        if (Hex(q.Fingerprint) is { Length: >= 8 } fp) { where.Add("(c.badge_hash LIKE @fp OR c.clr_hash LIKE @fp OR LOWER(p.pdf_sha256) LIKE @fp)"); args.Add(("fp", fp + "%")); }
        else if (!string.IsNullOrWhiteSpace(q.Fingerprint)) where.Add("1 = 0");   // zu kurze Fingerprints liefern nichts statt „alles“
        if (!string.IsNullOrWhiteSpace(q.Thumbprint))
        {
            var tps = await ThumbprintsAsync(q.Thumbprint);
            if (tps.Count == 0) where.Add("1 = 0");
            else { var names = new List<string>(); for (var i = 0; i < tps.Count; i++) { names.Add("@tp" + i); args.Add(("tp" + i, tps[i])); } where.Add($"UPPER(p.signer_thumbprint) IN ({string.Join(",", names)})"); }
        }
        Like("c.number", "num", q.Number); Like("c.learner_name", "rec", q.Recipient); Like("c.issued_by_name", "iss", q.Issuer); Like("c.module_name", "mod", q.Module);
        if (q.Status is "active" or "revoked") { where.Add("c.status = @st"); args.Add(("st", q.Status)); }
        if (q.Test == "test") where.Add("c.is_test = 1"); else if (q.Test == "regular") where.Add("c.is_test = 0");
        switch (q.Kind)
        {
            case "badge": where.Add("c.kind = 'module'"); break;
            case "master": where.Add("c.kind = 'topic'"); break;
            case "clr": where.Add("c.kind = 'topic' AND c.clr_hash <> ''"); break;
            case "pdf": where.Add("p.certificate_id IS NOT NULL"); break;
        }
        if (q.From is { } from) { where.Add("c.issued_at >= @from"); args.Add(("from", from.ToUniversalTime())); }
        if (q.To is { } to) { where.Add("c.issued_at < @to"); args.Add(("to", to.ToUniversalTime().Date.AddDays(1))); }

        var cond = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        var total = DbValue.ToInt((await _db.QueryAsync("SELECT COUNT(*) AS n FROM certificates c LEFT JOIN certificate_pdfs p ON p.certificate_id = c.id" + cond, args))[0]["n"]);
        var size = Math.Clamp(q.PageSize, 1, 100); var page = Math.Max(1, q.Page);
        var order = q.Sort == "asc" ? "ASC" : "DESC";
        var rows = await _db.QueryAsync($"{SelectRow}{cond} ORDER BY c.issued_at {order}, c.id {order} LIMIT {size} OFFSET {(page - 1) * size}", args);
        return new Page(rows.Select(MapRow).ToList(), total, page, size);
    }

    private static bool IsLocalAddress(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return true;
        var h = u.Host;
        if (h is "localhost" or "::1" || h.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !h.Contains('.') && !System.Net.IPAddress.TryParse(h, out _)) return true;
        if (System.Net.IPAddress.TryParse(h, out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip)) return true;
            var b = ip.GetAddressBytes();
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 169 && b[1] == 254);
        }
        return false;
    }

    private static string EscapeLike(string s) => s.Replace("%", "").Replace("_", "");   // Platzhalter des Benutzers sind keine Suchzeichen

    /// <summary>Löst einen Eingabewert (SHA-1-Thumbprint oder SHA-256-Fingerabdruck eines PKI-Zertifikats) in die SHA-1-Thumbprints auf, die in <c>certificate_pdfs.signer_thumbprint</c> stehen.</summary>
    private async Task<List<string>> ThumbprintsAsync(string input)
    {
        var hex = Hex(input).ToUpperInvariant(); var result = new List<string>();
        if (hex.Length == 40) result.Add(hex);
        if (hex.Length == 64)
            foreach (var c in await _pki.ListCertificatesAsync())
                if (string.Equals(c.CertificateSha256, hex, StringComparison.OrdinalIgnoreCase)) result.Add(PkiService.ToX509(c).Thumbprint.ToUpperInvariant());
        return result;
    }

    public async Task<Detail?> GetDetailAsync(string actorId, string id)
    {
        await _users.RequireRoleAsync(actorId, Roles.Admin);
        if (!UserService.IsGuid(id)) return null;
        var rows = await _db.QueryAsync($"{SelectRow} WHERE c.id = @id", new[] { ("id", (object?)id) });
        if (rows.Count == 0) return null;
        var row = MapRow(rows[0]);
        var cert = await _certs.GetRawAsync(id);
        if (cert is null) return null;
        var (alg, kid, profile) = BadgeSigner.HeaderInfo(cert.BadgeJws);
        var sig = _certs.Signer.Verify(cert.BadgeJws);
        bool? clrSig = string.IsNullOrEmpty(cert.ClrJws) ? null : _certs.Signer.Verify(cert.ClrJws).SignatureValid;

        string? pkiId = null, pkiStatus = null;
        if (row.Thumbprint is { } tp)
            foreach (var c in await _pki.ListCertificatesAsync())
                if (string.Equals(PkiService.ToX509(c).Thumbprint, tp, StringComparison.OrdinalIgnoreCase)) { pkiId = c.Id; pkiStatus = c.Status; break; }

        var children = (await _db.QueryAsync("SELECT id, number, module_name, status FROM certificates WHERE parent_id = @id ORDER BY issued_at", new[] { ("id", (object?)id) }))
            .Select(r => new ChildRef(r["id"] ?? "", r["number"] ?? "", r["module_name"] ?? "", r["status"] ?? "")).ToList();
        ChildRef? parent = null;
        if (row.ParentId is { } pid && (await _db.QueryAsync("SELECT id, number, module_name, status FROM certificates WHERE id = @id", new[] { ("id", (object?)pid) })) is { Count: > 0 } pr)
            parent = new ChildRef(pr[0]["id"] ?? "", pr[0]["number"] ?? "", pr[0]["module_name"] ?? "", pr[0]["status"] ?? "");

        var audit = new List<AuditRow>();
        foreach (var a in await _db.QueryAsync("SELECT actor_user_id, action, detail, created_at FROM audit_log WHERE target = @id ORDER BY created_at DESC LIMIT 100", new[] { ("id", (object?)id) }))
        {
            var actorRef = a.GetValueOrDefault("actor_user_id");
            audit.Add(new AuditRow(DbValue.ToDateTime(a["created_at"]), a["action"] ?? "", actorRef, actorRef is { Length: > 0 } ? (await _users.GetAsync(actorRef))?.DisplayName ?? "(Benutzer nicht mehr vorhanden)" : "System", a["detail"] ?? ""));
        }
        var revokedBy = cert.RevokedBy is { Length: > 0 } rb ? (await _users.GetAsync(rb))?.DisplayName ?? "(Benutzer nicht mehr vorhanden)" : null;
        var impact = cert.IsRevoked ? "Bereits widerrufen."
            : cert.IsTopic ? $"Der Widerruf betrifft Master-Badge und CLR dieses Themenabschlusses (eine Nachweisidentität). Die {children.Count} verknüpften Modulnachweise bleiben einzeln gültig und werden nicht widerrufen."
            : parent is not null ? "Der Widerruf betrifft nur diesen Modulnachweis. Der übergeordnete Themenabschluss und das CLR bleiben unverändert gültig (eingebettete Kopien im CLR bleiben signiert, werden aber bei der Online-Prüfung als widerrufen gemeldet)."
            : "Der Widerruf betrifft nur diesen Nachweis.";
        if (row.HasPdf && !cert.IsRevoked) impact += " Das zugehörige signierte PDF bleibt unverändert; seine Online-Prüfung meldet den Nachweis danach als widerrufen. Das X.509-Signaturzertifikat wird dadurch nicht widerrufen.";
        var local = IsLocalAddress(_certs.BaseUrl);
        var kidBase = kid is null ? "" : kid.Contains("/badges/") ? kid[..kid.IndexOf("/badges/", StringComparison.Ordinal)] : "";
        var view = new ValidationView(
            profile == "ob3-jwt" ? "VC-JWT nach Open Badges 3.0 (typ JWT)" : profile == "legacy-vc+jwt" ? "VC-JWT, älteres Profil (vc+jwt, Altbestand)" : "unbekannt",
            sig.SignatureValid ? "Gültig (lokal geprüft)" : "Ungültig: " + sig.Error,
            kid is null ? "Nicht geprüft" : string.Equals(kidBase, _certs.BaseUrl, StringComparison.OrdinalIgnoreCase) ? "Gültig (Aussteller dieser Installation)" : "Aussteller-Adresse weicht von dieser Installation ab",
            kid ?? "–",
            local ? "Schlüssel nicht erreichbar: lokale Adresse, für externe Prüfer nicht abrufbar (kein Signaturfehler)" : "Öffentliche Adresse (Erreichbarkeit nicht geprüft)",
            cert.IsRevoked ? "Widerrufen" : local ? "Aktiv; Statusprüfung durch externe Prüfer nicht möglich (lokale Adresse)" : "Aktiv (Sperrliste dieser Installation)",
            "Nicht geprüft — eine lokale Prüfung ersetzt keine externe 1EdTech-Prüfung und ist keine Zertifizierung");
        return new Detail(row, revokedBy, cert.RevokedReason, alg ?? "?", kid ?? "?", profile, sig.SignatureValid, sig.Error, clrSig, $"{_certs.BaseUrl}/verify/{cert.Id}", pkiId, pkiStatus, children, parent, audit, impact, view);
    }

    /// <summary>
    /// Widerruft genau einen Nachweis (Credential-ID). Die Bestätigung verlangt die Zertifikatsnummer; der Widerruf ist idempotent (ein zweiter Aufruf erzeugt weder ein weiteres Ereignis noch einen Fehler).
    /// Signierte Originaldaten bleiben unverändert; kein kaskadierender Widerruf, kein Widerruf des X.509-Zertifikats (dafür gibt es <see cref="PkiService.RevokeAsync"/>).
    /// </summary>
    public async Task<RevokeOutcome> RevokeAsync(string actorId, string id, string reason, string confirmNumber, string correlationId)
    {
        var actor = await _users.RequireRoleAsync(actorId, Roles.Admin);
        var c = await _certs.GetRawAsync(id) ?? throw new KeyNotFoundException("Nachweis nicht gefunden.");
        reason = (reason ?? "").Trim();
        if (reason.Length == 0) throw new ArgumentException("Bitte einen Grund für den Widerruf angeben.");
        if (reason.Length > 500) reason = reason[..500];
        if (!string.Equals((confirmNumber ?? "").Trim(), c.Number, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Die Bestätigung stimmt nicht mit der Nachweisnummer überein.");
        if (c.IsRevoked) return new RevokeOutcome(false, c.Id, c.Number, "revoked");
        var n = await _db.ExecuteAsync("UPDATE certificates SET status = 'revoked', revoked_at = @t, revoked_reason = @r, revoked_by = @a WHERE id = @id AND status = 'active'",
            new (string, object?)[] { ("t", DateTime.UtcNow), ("r", reason), ("a", actor.Id), ("id", c.Id) });
        if (n == 0) return new RevokeOutcome(false, c.Id, c.Number, "revoked");   // parallel bereits widerrufen
        await _users.WriteAuditAsync(actor.Id, "credential.revoke", c.Id, $"correlation={correlationId};number={c.Number};test={(c.IsTest ? 1 : 0)};fingerprint={c.BadgeHash};reason={reason};result=ok");
        return new RevokeOutcome(true, c.Id, c.Number, "revoked");
    }
}
