using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services.Pki;

public sealed class PkiException(string message) : InvalidOperationException(message);

/// <summary>Signaturmaterial für das PDF-Signieren aus der Learny-PKI: Zertifikat mit Schlüssel und Kette (Issuing CA, Root CA).</summary>
public sealed record PkiSigningMaterial(X509Certificate2 Certificate, IReadOnlyList<X509Certificate2> Chain, string CertificateId);

public sealed class PkiStatus
{
    public string State { get; set; } = PkiStates.InitializationRequired;
    public string Message { get; set; } = "";
    public List<string> Warnings { get; } = new();
    public PkiCertificate? Root { get; set; }
    public PkiCertificate? Issuing { get; set; }
    public PkiCertificate? Signing { get; set; }
    public PkiCrl? RootCrl { get; set; }
    public PkiCrl? IssuingCrl { get; set; }
    public bool RootKeyOnServer { get; set; }
    public string KeyAlgorithm { get; set; } = "";
    public string PublicUrl { get; set; } = "";
    public bool AutoInitEnabled { get; set; }
    public DateTime? InitializedAt { get; set; }
    public bool CanInitialize => State is PkiStates.InitializationRequired;
}

public sealed record PkiCheck(string Name, bool Ok, string Detail);

/// <summary>
/// Learny-PKI (ADR 0035): Einrichtung, Ausstellung, Widerruf und CRL. Jede schreibende Operation prüft die Administratorrolle gegen die Datenbank
/// (<see cref="UserService.RequireRoleAsync"/>); Systemaktionen (Erststart, Wartung) laufen ohne Benutzer und sind als solche im Audit erkennbar.
/// Private Schlüssel kennt nur der <see cref="PkiKeyStore"/> (verschlüsselte Dateien), die Datenbank hält ausschließlich öffentliche Zertifikate und Metadaten.
/// </summary>
public sealed partial class PkiService
{
    private readonly IDatabase _db;
    private readonly UserService _users;
    private readonly PkiSettings _cfg;
    private readonly PkiKeyStore _keys;
    private readonly ILogger<PkiService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheLock = new();
    private (DateTime At, PkiSigningMaterial? Material, string? Error)? _signingCache;
    private X509Certificate2? _issuingKey;

    private const string CertCols = "id, ca_id, serial_number, subject, issuer, certificate_type, certificate_pem, certificate_sha256, subject_key_id, public_key_algorithm, signature_algorithm, not_before, not_after, issued_at, issued_by, status, revoked_at, revocation_reason, key_reference, created_at";

    public PkiService(IDatabase db, UserService users, AppConfig config, PkiKeyStore keys, ILogger<PkiService> log)
    { _db = db; _users = users; _cfg = config.Pki; _keys = keys; _log = log; }

    public PkiSettings Settings => _cfg;
    public PkiKeyStore Keys => _keys;

    // ─── Zeilenabbildung ────────────────────────────────────────────────────

    private static PkiCertificate Map(Dictionary<string, string?> r) => new()
    {
        Id = r["id"]!, CaId = r["ca_id"]!, SerialNumber = r["serial_number"]!, Subject = r["subject"]!, Issuer = r["issuer"]!, CertificateType = r["certificate_type"]!,
        CertificatePem = r["certificate_pem"]!, CertificateSha256 = r["certificate_sha256"]!, SubjectKeyId = r["subject_key_id"] ?? "", PublicKeyAlgorithm = r["public_key_algorithm"]!,
        SignatureAlgorithm = r["signature_algorithm"]!, NotBefore = DbValue.ToDateTime(r["not_before"]), NotAfter = DbValue.ToDateTime(r["not_after"]), IssuedAt = DbValue.ToDateTime(r["issued_at"]),
        IssuedBy = r["issued_by"], Status = r["status"]!, RevokedAt = r["revoked_at"] is { Length: > 0 } ra ? DbValue.ToDateTime(ra) : null, RevocationReason = r["revocation_reason"],
        KeyReference = r["key_reference"] ?? "", CreatedAt = DbValue.ToDateTime(r["created_at"]),
    };

    private static PkiCrl MapCrl(Dictionary<string, string?> r) => new()
    {
        Id = r["id"]!, IssuerCaId = r["issuer_ca_id"]!, CrlNumber = long.Parse(r["crl_number"]!), ThisUpdate = DbValue.ToDateTime(r["this_update"]), NextUpdate = DbValue.ToDateTime(r["next_update"]),
        Der = Convert.FromBase64String(r["crl_base64"]!), Sha256 = r["crl_sha256"]!, SignatureAlgorithm = r["signature_algorithm"]!, EntryCount = DbValue.ToInt(r["entry_count"]), CreatedAt = DbValue.ToDateTime(r["created_at"]),
    };

    private static PkiRevocation MapRev(Dictionary<string, string?> r) => new()
    {
        Id = r["id"]!, CertificateId = r["certificate_id"]!, IssuingCaId = r["issuing_ca_id"]!, SerialNumber = r["certificate_serial_number"]!, RevocationTime = DbValue.ToDateTime(r["revocation_time"]),
        ReasonCode = DbValue.ToInt(r["reason_code"]), InvalidityDate = r["invalidity_date"] is { Length: > 0 } d ? DbValue.ToDateTime(d) : null, RevokedBy = r["revoked_by"], CreatedAt = DbValue.ToDateTime(r["created_at"]),
        AuditReference = r["audit_reference"] ?? "", ReleasedAt = r["released_at"] is { Length: > 0 } rl ? DbValue.ToDateTime(rl) : null,
    };

    public static X509Certificate2 ToX509(PkiCertificate c) => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(string.Concat(c.CertificatePem.Split('\n').Where(l => !l.StartsWith("-----")).Select(l => l.Trim()))));

    // ─── Abfragen ───────────────────────────────────────────────────────────

    public async Task<PkiCertificate?> GetCertificateAsync(string id) =>
        (await _db.QueryAsync($"SELECT {CertCols} FROM pki_certificates WHERE id = @id", new (string, object?)[] { ("id", id) })).Select(Map).FirstOrDefault();

    public async Task<PkiCertificate?> FindBySha256Async(string sha256) =>
        (await _db.QueryAsync($"SELECT {CertCols} FROM pki_certificates WHERE certificate_sha256 = @h", new (string, object?)[] { ("h", sha256) })).Select(Map).FirstOrDefault();

    public async Task<List<PkiCertificate>> ListCertificatesAsync(string? type = null) =>
        (type is null
            ? await _db.QueryAsync($"SELECT {CertCols} FROM pki_certificates ORDER BY issued_at DESC")
            : await _db.QueryAsync($"SELECT {CertCols} FROM pki_certificates WHERE certificate_type = @t ORDER BY issued_at DESC", new (string, object?)[] { ("t", type) })).Select(Map).ToList();   // kein „@t IS NULL“: PostgreSQL kann den Typ untypisierter Parameter dort nicht ableiten

    public async Task<List<PkiRevocation>> ListRevocationsAsync() =>
        (await _db.QueryAsync("SELECT id, certificate_id, issuing_ca_id, certificate_serial_number, revocation_time, reason_code, invalidity_date, revoked_by, created_at, audit_reference, released_at FROM pki_revocations ORDER BY created_at DESC")).Select(MapRev).ToList();

    public async Task<List<PkiCertificateEvent>> ListEventsAsync(string certificateId) =>
        (await _db.QueryAsync("SELECT id, certificate_id, event, detail, actor, created_at FROM pki_certificate_events WHERE certificate_id = @c ORDER BY created_at", new (string, object?)[] { ("c", certificateId) }))
        .Select(r => new PkiCertificateEvent { Id = r["id"]!, CertificateId = r["certificate_id"]!, Event = r["event"]!, Detail = r["detail"] ?? "", Actor = r["actor"], CreatedAt = DbValue.ToDateTime(r["created_at"]) }).ToList();

    public async Task<PkiCrl?> GetLatestCrlAsync(string caId) =>
        (await _db.QueryAsync("SELECT id, issuer_ca_id, crl_number, this_update, next_update, crl_base64, crl_sha256, signature_algorithm, entry_count, created_at FROM pki_crls WHERE issuer_ca_id = @c ORDER BY crl_number DESC LIMIT 1",
            new (string, object?)[] { ("c", caId) })).Select(MapCrl).FirstOrDefault();

    public async Task<List<PkiCrl>> ListCrlsAsync(int limit = 20) =>
        (await _db.QueryAsync("SELECT id, issuer_ca_id, crl_number, this_update, next_update, crl_base64, crl_sha256, signature_algorithm, entry_count, created_at FROM pki_crls ORDER BY created_at DESC LIMIT " + Math.Clamp(limit, 1, 200)))
        .Select(MapCrl).ToList();

    private async Task<Dictionary<string, string?>?> GetStateRowAsync() =>
        (await _db.QueryAsync("SELECT id, status, initialized_at, initialized_by, root_cert_id, issuing_cert_id, key_algorithm, base_url, note, updated_at FROM pki_state WHERE id = 1")).FirstOrDefault();

    public async Task<bool> HasAnyCertificateAsync() => (await _db.QueryAsync("SELECT id FROM pki_certificates LIMIT 1")).Count > 0;

    // ─── Status ─────────────────────────────────────────────────────────────

    public async Task<PkiStatus> GetStatusAsync()
    {
        var st = new PkiStatus { KeyAlgorithm = _cfg.KeyAlgorithm, PublicUrl = _cfg.PublicUrl, AutoInitEnabled = _cfg.AutoInit, RootKeyOnServer = _keys.RootKeyOnServer };
        var row = await GetStateRowAsync();
        if (row is null)
        {
            st.State = PkiStates.InitializationRequired;
            st.Message = "Die Learny-PKI ist noch nicht eingerichtet. Ein Administrator kann sie unter Administration → PKI initialisieren"
                + (_cfg.AutoInit ? (_cfg.PublicUrl.Length == 0 ? " (automatische Ersteinrichtung wartet auf PKI_PUBLIC_URL)." : " (automatische Ersteinrichtung beim Start ist aktiviert).") : ".");
            return st;
        }
        st.InitializedAt = row["initialized_at"] is { Length: > 0 } ia ? DbValue.ToDateTime(ia) : null;
        st.KeyAlgorithm = row["key_algorithm"] ?? st.KeyAlgorithm;
        if (row["base_url"] is { Length: > 0 } bu) st.PublicUrl = bu;
        if (row["status"] == PkiStates.Initializing)
        {
            var stale = DbValue.ToDateTime(row["updated_at"]) < DateTime.UtcNow.AddMinutes(-10);
            st.State = stale ? PkiStates.MaintenanceRequired : PkiStates.Initializing;
            st.Message = stale ? "Eine frühere Initialisierung wurde nicht abgeschlossen. Prüfen Sie die Protokolle und setzen Sie den unvollständigen Zustand ausdrücklich zurück." : "Die Initialisierung läuft.";
            return st;
        }
        var now = DateTime.UtcNow;
        st.Root = row["root_cert_id"] is { Length: > 0 } rid ? await GetCertificateAsync(rid) : null;
        st.Issuing = row["issuing_cert_id"] is { Length: > 0 } iid ? await GetCertificateAsync(iid) : null;
        if (st.Root is null || st.Issuing is null) { st.State = PkiStates.MaintenanceRequired; st.Message = "Die Zertifikate der CA-Hierarchie fehlen im Register."; return st; }
        st.Signing = (await ListCertificatesAsync(PkiCertificateTypes.DocumentSigning)).FirstOrDefault(c => c.Status == PkiCertificateStatuses.Active);
        st.RootCrl = await GetLatestCrlAsync(st.Root.Id);
        st.IssuingCrl = await GetLatestCrlAsync(st.Issuing.Id);

        if (st.RootKeyOnServer) st.Warnings.Add("Der Root-Schlüssel liegt noch auf dem Server (Verzeichnis root-offline). Sichern Sie ihn mit der Passphrase offline und entfernen Sie ihn danach unter Administration → PKI.");
        if (_keys.PasswordFromFile) st.Warnings.Add("Das Schlüsselpasswort liegt als Datei neben den Schlüsseln. Übernehmen Sie es als PKI_KEY_PASSWORD in die Secret-Verwaltung und entfernen Sie die Datei pki-key.pass.");
        if (st.Root.NotAfter < now.AddDays(_cfg.ExpiryWarningDays)) st.Warnings.Add($"Die Root CA läuft am {st.Root.NotAfter:yyyy-MM-dd} ab.");
        if (st.Issuing.NotAfter < now.AddDays(_cfg.ExpiryWarningDays)) st.Warnings.Add($"Die Issuing CA läuft am {st.Issuing.NotAfter:yyyy-MM-dd} ab.");
        if (st.Signing is not null && st.Signing.NotAfter < now.AddDays(_cfg.ExpiryWarningDays)) st.Warnings.Add($"Das Signaturzertifikat läuft am {st.Signing.NotAfter:yyyy-MM-dd} ab — bitte erneuern.");
        if (st.RootCrl is null || st.RootCrl.NextUpdate < now.AddDays(_cfg.ExpiryWarningDays)) st.Warnings.Add("Die Root-CRL läuft bald ab oder fehlt; sie kann nur mit dem Root-Schlüssel erneuert werden.");

        if (st.Issuing.Status == PkiCertificateStatuses.Revoked || st.Root.Status == PkiCertificateStatuses.Revoked)
        { st.State = PkiStates.Locked; st.Message = "Die Issuing CA ist widerrufen. Es werden keine Zertifikate mehr ausgestellt und keine PDFs signiert."; return st; }
        if (st.Issuing.NotAfter < now) { st.State = PkiStates.MaintenanceRequired; st.Message = "Das Issuing-CA-Zertifikat ist abgelaufen."; return st; }
        var (material, error) = await TryLoadSigningAsync(forceCheckOnly: true);
        if (error is not null && error.StartsWith("KEY:")) { st.State = PkiStates.KeyUnavailable; st.Message = error[4..] + " Die Signierung ist gesperrt; es wird nie ein neuer Root-Schlüssel erzeugt."; return st; }
        if (st.Signing is null || st.Signing.NotAfter < now) { st.State = PkiStates.MaintenanceRequired; st.Message = "Es gibt kein gültiges Signaturzertifikat. Bitte unter Administration → PKI erneuern."; return st; }
        if (st.IssuingCrl is null || !st.IssuingCrl.IsFresh(now)) { st.State = PkiStates.MaintenanceRequired; st.Message = "Die aktuelle CRL der Issuing CA ist abgelaufen. Bitte eine neue CRL ausstellen."; return st; }
        if (error is not null) { st.State = PkiStates.MaintenanceRequired; st.Message = error; return st; }
        _ = material;
        st.State = PkiStates.Operational;
        st.Message = "Die Learny-PKI ist betriebsbereit. Sie begründet keine automatische öffentliche Anerkennung (Adobe, Betriebssysteme, Vertrauenslisten).";
        return st;
    }

    // ─── Schlüssel und Signaturmaterial ─────────────────────────────────────

    private async Task<X509Certificate2> LoadIssuingKeyAsync(PkiCertificate issuing, bool verifyFile = false)
    {
        if (!verifyFile && _issuingKey is { } k && k.Thumbprint.Equals(ToX509(issuing).Thumbprint, StringComparison.OrdinalIgnoreCase)) return k;
        if (!_keys.Exists(PkiKeyStore.IssuingFile)) throw new PkiException("Die Schlüsseldatei der Issuing CA fehlt.");
        X509Certificate2 loaded;
        try { loaded = _keys.Load(PkiKeyStore.IssuingFile); }
        catch (CryptographicException) { throw new PkiException("Die Schlüsseldatei der Issuing CA ist beschädigt oder das Schlüsselpasswort stimmt nicht."); }
        if (!loaded.GetCertHashString(HashAlgorithmName.SHA256).Equals(issuing.CertificateSha256, StringComparison.OrdinalIgnoreCase) || !loaded.HasPrivateKey)
            throw new PkiException("Die Schlüsseldatei der Issuing CA passt nicht zum registrierten Zertifikat.");
        _issuingKey = loaded;
        await Task.CompletedTask;
        return loaded;
    }

    /// <summary>Signaturmaterial für PDFs. Fail-closed: Gibt null zurück (mit Grund), wenn die PKI nicht betriebsbereit ist. Eine abgelaufene Issuing-CRL wird bei Bedarf erneuert.</summary>
    public async Task<(PkiSigningMaterial? Material, string? Reason)> GetSigningMaterialAsync()
    {
        lock (_cacheLock) if (_signingCache is { } c && c.At > DateTime.UtcNow.AddSeconds(-30)) return (c.Material, c.Error?.Replace("KEY:", ""));
        var status = await GetStatusAsyncForSigning();
        lock (_cacheLock) _signingCache = (DateTime.UtcNow, status.Material, status.Error);
        return (status.Material, status.Error?.Replace("KEY:", ""));
    }

    private async Task<(PkiSigningMaterial? Material, string? Error)> GetStatusAsyncForSigning()
    {
        var row = await GetStateRowAsync();
        if (row is null || row["status"] != PkiStates.Initialized) return (null, "Die Learny-PKI ist nicht eingerichtet.");
        var issuing = row["issuing_cert_id"] is { Length: > 0 } iid ? await GetCertificateAsync(iid) : null;
        if (issuing is not null && issuing.Status == PkiCertificateStatuses.Active)
        {
            var crl = await GetLatestCrlAsync(issuing.Id);
            if (crl is null || crl.NextUpdate < DateTime.UtcNow.AddHours(_cfg.CrlRenewBeforeHours))
            {
                try { await IssueCrlCoreAsync(issuing.Id, null, "system", null); }
                catch (Exception ex) when (ex is PkiException or CryptographicException) { _log.LogWarning("PKI: CRL konnte nicht erneuert werden ({Type}).", ex.GetType().Name); }
            }
        }
        return await TryLoadSigningAsync(forceCheckOnly: false);
    }

    private async Task<(PkiSigningMaterial? Material, string? Error)> TryLoadSigningAsync(bool forceCheckOnly)
    {
        var row = await GetStateRowAsync();
        if (row is null) return (null, "Die Learny-PKI ist nicht eingerichtet.");
        var root = row["root_cert_id"] is { Length: > 0 } rid ? await GetCertificateAsync(rid) : null;
        var issuing = row["issuing_cert_id"] is { Length: > 0 } iid ? await GetCertificateAsync(iid) : null;
        if (root is null || issuing is null) return (null, "Die CA-Zertifikate fehlen im Register.");
        if (issuing.Status != PkiCertificateStatuses.Active) return (null, "Die Issuing CA ist nicht aktiv.");
        var now = DateTime.UtcNow;
        var signing = (await ListCertificatesAsync(PkiCertificateTypes.DocumentSigning)).Where(c => c.Status == PkiCertificateStatuses.Active && c.NotBefore <= now.AddMinutes(5) && c.NotAfter > now).OrderByDescending(c => c.IssuedAt).FirstOrDefault();
        try
        {
            await LoadIssuingKeyAsync(issuing, verifyFile: forceCheckOnly);   // Statusprüfung liest die Datei immer neu (Sicherungs-/Schlüsselverlust sichtbar machen)
            if (signing is null) return (null, "Es gibt kein gültiges Signaturzertifikat.");
            if (!_keys.Exists(signing.KeyReference)) return (null, "KEY:Die Schlüsseldatei des Signaturzertifikats fehlt.");
            X509Certificate2 loaded;
            try { loaded = _keys.Load(signing.KeyReference); }
            catch (CryptographicException) { return (null, "KEY:Die Schlüsseldatei des Signaturzertifikats ist beschädigt oder das Schlüsselpasswort stimmt nicht."); }
            if (!loaded.GetCertHashString(HashAlgorithmName.SHA256).Equals(signing.CertificateSha256, StringComparison.OrdinalIgnoreCase) || !loaded.HasPrivateKey)
                return (null, "KEY:Die Schlüsseldatei passt nicht zum registrierten Signaturzertifikat.");
            if (forceCheckOnly) { loaded.Dispose(); return (null, null); }
            var chain = new List<X509Certificate2> { ToX509(issuing), ToX509(root) };
            return (new PkiSigningMaterial(loaded, chain, signing.Id), null);
        }
        catch (PkiException ex) { return (null, "KEY:" + ex.Message); }
    }

    private void InvalidateCache() { lock (_cacheLock) _signingCache = null; }

    // ─── Persistenz-Helfer ──────────────────────────────────────────────────

    private static string Pem(X509Certificate2 c) => c.ExportCertificatePem();

    private (string Sql, (string, object?)[] Args) InsertCertificate(string id, string caId, X509Certificate2 cert, string type, string? actor, string keyRef, DateTime now) =>
        ($"INSERT INTO pki_certificates ({CertCols}) VALUES (@id, @ca, @serial, @subject, @issuer, @type, @pem, @sha, @ski, @kalg, @salg, @nb, @na, @ia, @by, 'active', NULL, NULL, @kref, @now)",
        new (string, object?)[]
        {
            ("id", id), ("ca", caId), ("serial", PkiCrypto.SerialHex(cert)), ("subject", cert.Subject), ("issuer", cert.Issuer), ("type", type), ("pem", Pem(cert)),
            ("sha", cert.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant()), ("ski", PkiCrypto.SubjectKeyId(cert)), ("kalg", PkiCrypto.KeyAlgorithmName(cert)),
            ("salg", cert.SignatureAlgorithm.FriendlyName ?? cert.SignatureAlgorithm.Value ?? ""), ("nb", cert.NotBefore.ToUniversalTime()), ("na", cert.NotAfter.ToUniversalTime()),
            ("ia", now), ("by", actor), ("kref", keyRef), ("now", now),
        });

    private Task AddEventAsync(string certId, string ev, string detail, string? actor) =>
        _db.ExecuteAsync("INSERT INTO pki_certificate_events (id, certificate_id, event, detail, actor, created_at) VALUES (@id, @c, @e, @d, @a, @now)",
            new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("c", certId), ("e", ev), ("d", detail), ("a", actor), ("now", DateTime.UtcNow) });

    private Task AuditAsync(string? actor, string action, string target, string detail) => _users.WriteAuditAsync(actor, action, target, detail);

    private async Task<AppUser> RequireAdminAsync(string? actor) => await _users.RequireRoleAsync(actor, Roles.Admin);

    private string RequireBaseUrl(string? candidate)
    {
        var url = (candidate ?? "").Trim().TrimEnd('/');
        if (url.Length == 0) url = _cfg.PublicUrl;
        if (!(Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https") && u.UserInfo.Length == 0 && u.Query.Length == 0 && u.Fragment.Length == 0))
            throw new PkiException("Es fehlt eine gültige öffentliche Basis-URL (PKI_PUBLIC_URL, z. B. https://learny.example.org). Sie wird unveränderlich in die CRL-Verteilungspunkte der Zertifikate geschrieben.");
        return u.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public static string CrlUrl(string baseUrl, string caId) => $"{baseUrl}/pki/crl/{caId}.crl";
}
