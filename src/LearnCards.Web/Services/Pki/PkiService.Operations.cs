using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services.Pki;

public sealed partial class PkiService
{
    private static string Dn(string cn, string org) => $"CN={cn}, O=\"{org.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    // ─── Initialisierung ────────────────────────────────────────────────────

    /// <summary>
    /// Richtet die PKI einmalig ein (Root CA, Issuing CA, erstes Signaturzertifikat, erste CRLs). Läuft nur, wenn weder Zustand, Zertifikate noch Schlüsseldateien existieren;
    /// ein Neustart oder ein zweiter Aufruf erzeugt nie eine neue Root CA. Bei jedem Fehler werden Datenbankzeilen und neue Schlüsseldateien wieder entfernt.
    /// <paramref name="actorUserId"/> = null bedeutet Systemaktion (Erststart/CLI); sonst ist die Administratorrolle erforderlich.
    /// </summary>
    public async Task<PkiStatus> InitializeAsync(string? actorUserId, string? publicUrl, bool system = false)
    {
        if (!system) await RequireAdminAsync(actorUserId);
        var baseUrl = RequireBaseUrl(publicUrl);
        await _gate.WaitAsync();
        var createdFiles = new List<string>();
        var ids = new List<string>();
        var stateInserted = false;
        try
        {
            if (await GetStateRowAsync() is not null) throw new PkiException("Die PKI ist bereits eingerichtet bzw. eine Einrichtung ist offen. Eine zweite Einrichtung wird nie automatisch gestartet.");
            if (await HasAnyCertificateAsync()) throw new PkiException("Im Zertifikatsregister existieren bereits Zertifikate. Es wird keine neue PKI daneben erzeugt.");
            if (_keys.KeyFiles() is { Count: > 0 } orphans) throw new PkiException($"Im Schlüsselverzeichnis liegen bereits Schlüsseldateien ({orphans.Count}). Sie gehören zu einer früheren Einrichtung und werden nie überschrieben; Datenbank oder Schlüssel wiederherstellen (docs/pki.md).");
            var generatedPassword = _keys.EnsurePassword();
            var password = _keys.Password ?? throw new PkiException("Kein Schlüsselpasswort verfügbar.");
            var now = DateTime.UtcNow;
            var nowSec = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);
            var from = nowSec.AddMinutes(-5);
            var alg = _cfg.KeyAlgorithm;
            var org = _cfg.Organization;

            await _db.ExecuteAsync("INSERT INTO pki_state (id, status, key_algorithm, base_url, note, updated_at) VALUES (1, @s, @a, @u, '', @now)",
                new (string, object?)[] { ("s", PkiStates.Initializing), ("a", alg), ("u", baseUrl), ("now", now) });
            stateInserted = true;

            string rootId = Guid.NewGuid().ToString(), issuingId = Guid.NewGuid().ToString(), signingId = Guid.NewGuid().ToString();
            ids.AddRange(new[] { rootId, issuingId, signingId });

            using var rootKey = PkiCrypto.NewKey(alg, PkiRole.Root);
            using var root = PkiCrypto.CreateRoot(Dn("Learny Root CA", org), rootKey, alg, from, nowSec.AddYears(_cfg.RootValidityYears));
            using var issuingKey = PkiCrypto.NewKey(alg, PkiRole.Issuing);
            using var issuing = PkiCrypto.CreateIssuing(Dn("Learny Issuing CA", org), issuingKey, alg, root, from, nowSec.AddYears(_cfg.IssuingValidityYears), PkiCrypto.NewSerial(),
                CrlUrl(baseUrl, rootId), baseUrl + "/pki/certificates/root");
            var signingNotAfter = new[] { nowSec.AddDays(_cfg.SigningValidityDays), new DateTimeOffset(issuing.NotAfter).AddDays(-1) }.Min();
            using var signingKey = PkiCrypto.NewKey(alg, PkiRole.Signing);
            using var signing = PkiCrypto.CreateSigning(Dn($"Learny Document Signing {now:yyyyMM}", org), signingKey, alg, issuing, from, signingNotAfter, PkiCrypto.NewSerial(),
                CrlUrl(baseUrl, issuingId), baseUrl + "/pki/certificates/issuing");

            var rootCrl = PkiCrypto.BuildCrl(root, Array.Empty<CrlEntrySpec>(), 1, nowSec, nowSec.AddDays(_cfg.RootCrlValidityDays), alg);
            var issuingCrl = PkiCrypto.BuildCrl(issuing, Array.Empty<CrlEntrySpec>(), 1, nowSec, nowSec.AddDays(_cfg.CrlValidityDays), alg);
            foreach (var (crl, ca) in new[] { (rootCrl, root), (issuingCrl, issuing) })
                if (!PkiCrypto.ParseAndVerifyCrl(crl, ca).SignatureValid) throw new PkiException("Interne Prüfung der erzeugten CRL fehlgeschlagen.");

            var signingFile = PkiKeyStore.SigningFile(signingId);
            _keys.Save(PkiKeyStore.IssuingFile, issuing, password); createdFiles.Add(PkiKeyStore.IssuingFile);
            _keys.Save(signingFile, signing, password); createdFiles.Add(signingFile);
            _keys.SaveRoot(root, out var rootPassFile); createdFiles.Add(Path.Combine(PkiKeyStore.RootDir, PkiKeyStore.RootFile));

            var actor = system ? null : actorUserId;
            foreach (var (id, caId, cert, type, kref) in new[]
            {
                (rootId, rootId, (X509Certificate2)root, PkiCertificateTypes.RootCa, ""),
                (issuingId, rootId, (X509Certificate2)issuing, PkiCertificateTypes.IssuingCa, PkiKeyStore.IssuingFile),
                (signingId, issuingId, (X509Certificate2)signing, PkiCertificateTypes.DocumentSigning, signingFile),
            })
            {
                var (sql, args) = InsertCertificate(id, caId, cert, type, actor, kref, now);
                await _db.ExecuteAsync(sql, args);
                await AddEventAsync(id, "issued", system ? "Erststart/Systemaktion" : "Administrator", actor);
            }
            await StoreCrlAsync(rootId, 1, nowSec.UtcDateTime, nowSec.AddDays(_cfg.RootCrlValidityDays).UtcDateTime, rootCrl, 0, alg, now);
            await StoreCrlAsync(issuingId, 1, nowSec.UtcDateTime, nowSec.AddDays(_cfg.CrlValidityDays).UtcDateTime, issuingCrl, 0, alg, now);
            await _db.ExecuteAsync("UPDATE pki_state SET status = @s, initialized_at = @now, initialized_by = @by, root_cert_id = @r, issuing_cert_id = @i, updated_at = @now, note = @n WHERE id = 1",
                new (string, object?)[] { ("s", PkiStates.Initialized), ("now", now), ("by", actor), ("r", rootId), ("i", issuingId), ("n", rootPassFile ? "root-key-on-server" : "") });
            await AuditAsync(actor, "pki.initialize", rootId, $"source={(system ? "system" : "admin")} algorithm={alg} root={PkiCrypto.SerialHex(root)} issuing={PkiCrypto.SerialHex(issuing)}");
            await AuditAsync(actor, "pki.certificate.issue", signingId, $"type=document_signing serial={PkiCrypto.SerialHex(signing)}");
            await AuditAsync(actor, "pki.crl.issue", rootId, "number=1");
            await AuditAsync(actor, "pki.crl.issue", issuingId, "number=1");
            if (generatedPassword) _log.LogWarning("PKI: Schlüsselpasswort wurde erzeugt und in {Dir} abgelegt. Für Produktion PKI_KEY_PASSWORD aus einer Secret-Verwaltung setzen.", _keys.Dir);
            _log.LogWarning("PKI: Root-Schlüssel liegt verschlüsselt unter {Dir}. Offline sichern und anschließend in Administration → PKI vom Server entfernen.", Path.Combine(_keys.Dir, PkiKeyStore.RootDir));
            InvalidateCache();
        }
        catch (Exception ex)
        {
            // Rückbau: keine halb eingerichtete PKI hinterlassen (die Root wird nie "weiterverwendet")
            // Nur zurückbauen, was diese Initialisierung selbst angelegt hat — scheitert schon eine Vorbedingung (z. B. „bereits eingerichtet“), bleibt alles unangetastet
            foreach (var f in createdFiles) _keys.Delete(f);
            if (createdFiles.Any(f => f.StartsWith(PkiKeyStore.RootDir))) { try { _keys.RemoveRoot(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            try
            {
                foreach (var id in ids)
                {
                    await _db.ExecuteAsync("DELETE FROM pki_crls WHERE issuer_ca_id = @i", new (string, object?)[] { ("i", id) });
                    await _db.ExecuteAsync("DELETE FROM pki_certificate_events WHERE certificate_id = @i", new (string, object?)[] { ("i", id) });
                    await _db.ExecuteAsync("DELETE FROM pki_certificates WHERE id = @i", new (string, object?)[] { ("i", id) });
                }
                if (stateInserted) await _db.ExecuteAsync("DELETE FROM pki_state WHERE id = 1");
            }
            catch (Exception rb) { _log.LogError("PKI: Rückbau nach fehlgeschlagener Initialisierung unvollständig ({Type}).", rb.GetType().Name); }
            if (ex is PkiException) throw;
            _log.LogError("PKI: Initialisierung fehlgeschlagen ({Type}).", ex.GetType().Name);
            throw new PkiException($"Die Initialisierung ist fehlgeschlagen ({ex.GetType().Name}); es wurde nichts eingerichtet.");
        }
        finally { _gate.Release(); }
        return await GetStatusAsync();
    }

    /// <summary>Erststart: richtet die PKI einmalig automatisch ein, wenn <c>PKI_AUTO_INIT</c> aktiv ist und noch nie eine PKI existierte. Fehler sind nie ein Startabbruch, sondern im Status sichtbar.</summary>
    public async Task<string> TryAutoInitializeAsync(string? fallbackPublicUrl)
    {
        if (!_cfg.AutoInit) return "Automatische Ersteinrichtung ist deaktiviert (PKI_AUTO_INIT=false).";
        if (await GetStateRowAsync() is not null) return "";
        if (await HasAnyCertificateAsync()) return "Zertifikate vorhanden, aber kein PKI-Zustand — keine automatische Einrichtung.";
        var url = _cfg.PublicUrl.Length > 0 ? _cfg.PublicUrl : fallbackPublicUrl;
        if (string.IsNullOrWhiteSpace(url)) return "Automatische Ersteinrichtung übersprungen: PKI_PUBLIC_URL ist nicht gesetzt.";
        try { await InitializeAsync(null, url, system: true); return "Die Learny-PKI wurde beim ersten Start eingerichtet."; }
        catch (PkiException ex) { _log.LogError("PKI: automatische Ersteinrichtung nicht möglich: {Message}", ex.Message); return "Automatische Ersteinrichtung fehlgeschlagen: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException)   // nie ein Startabbruch: der Fehler bleibt im Status sichtbar
        { _log.LogError("PKI: automatische Ersteinrichtung fehlgeschlagen ({Type}).", ex.GetType().Name); return "Automatische Ersteinrichtung fehlgeschlagen (" + ex.GetType().Name + ")."; }
    }

    /// <summary>Verwirft ausdrücklich eine unvollständige Initialisierung (Zustand "initializing", nie bei abgeschlossener PKI).</summary>
    public async Task ResetIncompleteAsync(string? actorUserId, string confirmation)
    {
        var admin = await RequireAdminAsync(actorUserId);
        if (confirmation != "RESET") throw new PkiException("Bestätigung fehlt: bitte RESET eingeben.");
        await _gate.WaitAsync();
        try
        {
            var row = await GetStateRowAsync() ?? throw new PkiException("Es gibt keinen Zustand zum Zurücksetzen.");
            if (row["status"] != PkiStates.Initializing) throw new PkiException("Nur eine unvollständige Initialisierung kann zurückgesetzt werden — eine eingerichtete PKI nie.");
            if (DbValue.ToDateTime(row["updated_at"]) > DateTime.UtcNow.AddMinutes(-10)) throw new PkiException("Die Initialisierung läuft möglicherweise noch. Bitte mindestens 10 Minuten warten.");
            var certs = await ListCertificatesAsync();
            foreach (var c in certs)
            {
                await _db.ExecuteAsync("DELETE FROM pki_crls WHERE issuer_ca_id = @i", new (string, object?)[] { ("i", c.Id) });
                await _db.ExecuteAsync("DELETE FROM pki_certificate_events WHERE certificate_id = @i", new (string, object?)[] { ("i", c.Id) });
                await _db.ExecuteAsync("DELETE FROM pki_certificates WHERE id = @i", new (string, object?)[] { ("i", c.Id) });
                if (c.KeyReference.Length > 0) _keys.Delete(c.KeyReference);
            }
            _keys.Delete(PkiKeyStore.IssuingFile);
            _keys.RemoveRoot();
            await _db.ExecuteAsync("DELETE FROM pki_state WHERE id = 1");
            await AuditAsync(admin.Id, "pki.configuration.change", "pki", "reset-incomplete-initialization");
            InvalidateCache();
        }
        finally { _gate.Release(); }
    }

    // ─── Signaturzertifikat ─────────────────────────────────────────────────

    /// <summary>Stellt ein neues Signaturzertifikat aus (Ausstellung und Registereintrag zusammen); aktive Vorgänger werden "superseded" (nicht widerrufen).</summary>
    public async Task<PkiCertificate> RenewSigningCertificateAsync(string? actorUserId, bool system = false)
    {
        if (!system) await RequireAdminAsync(actorUserId);
        await _gate.WaitAsync();
        string? newKeyFile = null;
        try
        {
            var row = await GetStateRowAsync();
            if (row is null || row["status"] != PkiStates.Initialized) throw new PkiException("Die PKI ist nicht betriebsbereit.");
            var issuingRow = await GetCertificateAsync(row["issuing_cert_id"]!) ?? throw new PkiException("Issuing CA fehlt im Register.");
            if (issuingRow.Status != PkiCertificateStatuses.Active || issuingRow.NotAfter < DateTime.UtcNow) throw new PkiException("Die Issuing CA ist nicht aktiv; es werden keine Zertifikate ausgestellt.");
            var issuingKey = await LoadIssuingKeyAsync(issuingRow);
            var alg = row["key_algorithm"] ?? _cfg.KeyAlgorithm;
            var baseUrl = row["base_url"]!;
            var password = _keys.Password ?? throw new PkiException("Kein Schlüsselpasswort verfügbar.");
            var now = DateTime.UtcNow;
            var nowSec = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);
            var notAfter = new[] { nowSec.AddDays(_cfg.SigningValidityDays), new DateTimeOffset(issuingRow.NotAfter, TimeSpan.Zero).AddDays(-1) }.Min();
            if (notAfter <= nowSec.AddDays(1)) throw new PkiException("Die Issuing CA läuft zu bald ab; zuerst die CA-Hierarchie erneuern (docs/pki.md).");
            var id = Guid.NewGuid().ToString();
            using var key = PkiCrypto.NewKey(alg, PkiRole.Signing);
            using var cert = PkiCrypto.CreateSigning(Dn($"Learny Document Signing {now:yyyyMM}", _cfg.Organization), key, alg, issuingKey, nowSec.AddMinutes(-5), notAfter, PkiCrypto.NewSerial(),
                CrlUrl(baseUrl, issuingRow.Id), baseUrl + "/pki/certificates/issuing");
            newKeyFile = PkiKeyStore.SigningFile(id);
            _keys.Save(newKeyFile, cert, password);
            var actor = system ? null : actorUserId;
            var (sql, args) = InsertCertificate(id, issuingRow.Id, cert, PkiCertificateTypes.DocumentSigning, actor, newKeyFile, now);
            await _db.ExecuteAsync(sql, args);
            await AddEventAsync(id, "issued", system ? "automatische Erneuerung" : "Erneuerung durch Administrator", actor);
            foreach (var old in (await ListCertificatesAsync(PkiCertificateTypes.DocumentSigning)).Where(c => c.Id != id && c.Status == PkiCertificateStatuses.Active))
            {
                await _db.ExecuteAsync("UPDATE pki_certificates SET status = @s WHERE id = @i", new (string, object?)[] { ("s", PkiCertificateStatuses.Superseded), ("i", old.Id) });
                await AddEventAsync(old.Id, "superseded", "ersetzt durch " + id, actor);
            }
            await AuditAsync(actor, "pki.certificate.renew", id, $"serial={PkiCrypto.SerialHex(cert)}");
            InvalidateCache();
            return (await GetCertificateAsync(id))!;
        }
        catch
        {
            if (newKeyFile is not null && (await GetCertificateAsync(Path.GetFileNameWithoutExtension(newKeyFile)["signing-".Length..])) is null) _keys.Delete(newKeyFile);
            throw;
        }
        finally { _gate.Release(); }
    }

    // ─── CRL ────────────────────────────────────────────────────────────────

    private Task StoreCrlAsync(string caId, long number, DateTime thisUpdate, DateTime nextUpdate, byte[] der, int entries, string alg, DateTime now) =>
        _db.ExecuteAsync("INSERT INTO pki_crls (id, issuer_ca_id, crl_number, this_update, next_update, crl_base64, crl_sha256, signature_algorithm, entry_count, created_at) VALUES (@id, @ca, @n, @tu, @nu, @b, @h, @sa, @e, @now)",
            new (string, object?)[]
            {
                ("id", Guid.NewGuid().ToString()), ("ca", caId), ("n", number), ("tu", thisUpdate), ("nu", nextUpdate), ("b", Convert.ToBase64String(der)),
                ("h", Convert.ToHexString(SHA256.HashData(der)).ToLowerInvariant()), ("sa", PkiCrypto.IsEcdsa(alg) ? "ecdsa-with-SHA384" : "sha256WithRSAEncryption"), ("e", entries), ("now", now),
            });

    /// <summary>Stellt eine neue CRL der Issuing CA aus (Administrator). Die Root-CRL erfordert den Root-Schlüssel (<see cref="IssueRootCrlAsync"/>).</summary>
    public async Task<PkiCrl> IssueCrlAsync(string? actorUserId)
    {
        var admin = await RequireAdminAsync(actorUserId);
        var row = await GetStateRowAsync() ?? throw new PkiException("Die PKI ist nicht eingerichtet.");
        return await IssueCrlCoreAsync(row["issuing_cert_id"]!, null, admin.Id, null);
    }

    public async Task<PkiCrl> IssueRootCrlAsync(string? actorUserId, byte[]? rootPfx, string? rootPassphrase)
    {
        var admin = await RequireAdminAsync(actorUserId);
        var row = await GetStateRowAsync() ?? throw new PkiException("Die PKI ist nicht eingerichtet.");
        using var rootKey = LoadRootKey(row["root_cert_id"]!, rootPfx, rootPassphrase);
        return await IssueCrlCoreAsync(row["root_cert_id"]!, rootKey, admin.Id, null);
    }

    /// <summary>Lädt den Root-Schlüssel aus einem hochgeladenen PKCS#12 (oder, solange er noch auf dem Server liegt, aus root-offline) und prüft, dass er zum registrierten Root-Zertifikat passt.</summary>
    private X509Certificate2 LoadRootKey(string rootCertId, byte[]? pfx, string? passphrase)
    {
        X509Certificate2 k;
        try
        {
            if (pfx is { Length: > 0 }) k = PkiKeyStore.LoadBytes(pfx, passphrase ?? "");
            else if (_keys.RootKeyOnServer && _keys.ReadRootPassphrase() is { } p) k = _keys.Load(Path.Combine(PkiKeyStore.RootDir, PkiKeyStore.RootFile), p);
            else throw new PkiException("Für diese Aktion ist der Root-Schlüssel erforderlich: PKCS#12-Datei und Passphrase angeben (der Schlüssel liegt nicht auf dem Server).");
        }
        catch (CryptographicException) { throw new PkiException("Der Root-Schlüssel konnte nicht geöffnet werden (Datei oder Passphrase falsch)."); }
        var root = GetCertificateAsync(rootCertId).GetAwaiter().GetResult();
        if (root is null || !k.HasPrivateKey || !k.GetCertHashString(HashAlgorithmName.SHA256).Equals(root.CertificateSha256, StringComparison.OrdinalIgnoreCase))
        { k.Dispose(); throw new PkiException("Der Root-Schlüssel gehört nicht zur registrierten Root CA."); }
        return k;
    }

    private async Task<PkiCrl> IssueCrlCoreAsync(string caId, X509Certificate2? rootKey, string? actor, string? note)
    {
        var ca = await GetCertificateAsync(caId) ?? throw new PkiException("CA nicht gefunden.");
        if (!PkiCertificateTypes.IsCa(ca.CertificateType)) throw new PkiException("Nur CAs stellen CRLs aus.");
        var row = await GetStateRowAsync();
        var alg = row?["key_algorithm"] ?? _cfg.KeyAlgorithm;
        X509Certificate2 signer;
        if (ca.CertificateType == PkiCertificateTypes.RootCa) signer = rootKey ?? throw new PkiException("Für die Root-CRL ist der Root-Schlüssel erforderlich.");
        else signer = await LoadIssuingKeyAsync(ca);
        var validity = ca.CertificateType == PkiCertificateTypes.RootCa ? _cfg.RootCrlValidityDays : _cfg.CrlValidityDays;
        for (var attempt = 0; ; attempt++)
        {
            var entriesRows = await _db.QueryAsync("SELECT certificate_serial_number, revocation_time, reason_code FROM pki_revocations WHERE issuing_ca_id = @c AND released_at IS NULL ORDER BY created_at", new (string, object?)[] { ("c", caId) });
            var specs = entriesRows.Select(r => new CrlEntrySpec(PkiCrypto.SerialBytes(r["certificate_serial_number"]!), new DateTimeOffset(DbValue.ToDateTime(r["revocation_time"]), TimeSpan.Zero), DbValue.ToInt(r["reason_code"]))).ToList();
            var n = long.Parse((await _db.QueryAsync("SELECT COALESCE(MAX(crl_number), 0) + 1 AS n FROM pki_crls WHERE issuer_ca_id = @c", new (string, object?)[] { ("c", caId) }))[0]["n"]!);
            var now = DateTime.UtcNow;
            var nowSec = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);
            var der = PkiCrypto.BuildCrl(signer, specs, n, nowSec, nowSec.AddDays(validity), alg);
            var check = PkiCrypto.ParseAndVerifyCrl(der, ToX509(ca));
            if (!check.SignatureValid || check.Number != n) throw new PkiException("Interne Prüfung der erzeugten CRL fehlgeschlagen.");
            try { await StoreCrlAsync(caId, n, nowSec.UtcDateTime, nowSec.AddDays(validity).UtcDateTime, der, specs.Count, alg, now); }
            catch (Exception) when (attempt < 4) { continue; }   // parallele Ausstellung belegte die Nummer (Unique-Index): mit nächster Nummer wiederholen
            await AuditAsync(actor == "system" ? null : actor, "pki.crl.issue", caId, $"number={n} entries={specs.Count}{(actor == "system" ? " source=system" : "")}{(note is null ? "" : " " + note)}");
            return (await GetLatestCrlAsync(caId))!;
        }
    }

    /// <summary>Aktuelle CRL für die öffentliche Auslieferung. Eine abgelaufene Issuing-CRL wird vorher erneuert; ist sie nicht aktuell erneuerbar (Root-CRL), gilt sie nicht als aktuell (null).</summary>
    public async Task<PkiCrl?> GetCurrentCrlAsync(string caId)
    {
        var ca = await GetCertificateAsync(caId);
        if (ca is null || !PkiCertificateTypes.IsCa(ca.CertificateType)) return null;
        var crl = await GetLatestCrlAsync(caId);
        if ((crl is null || !crl.IsFresh(DateTime.UtcNow)) && ca.CertificateType == PkiCertificateTypes.IssuingCa && ca.Status != PkiCertificateStatuses.Expired)
        {
            try { crl = await IssueCrlCoreAsync(caId, null, "system", null); } catch (Exception ex) when (ex is PkiException or CryptographicException) { return null; }
        }
        return crl is not null && crl.IsFresh(DateTime.UtcNow) ? crl : null;
    }

    // ─── Widerruf ───────────────────────────────────────────────────────────

    /// <summary>
    /// Widerruft ein Zertifikat (Signatur- oder Issuing-CA-Zertifikat). Bestätigung: die Seriennummer muss eingegeben werden. Ein Widerruf der Issuing CA erfordert den Root-Schlüssel
    /// (Upload), weil die Root-CRL signiert werden muss, und sperrt die PKI. Die Root CA selbst wird nicht widerrufen (Selbstsignatur; Vertrauensanker wird aus der Konfiguration entfernt).
    /// </summary>
    public async Task RevokeAsync(string? actorUserId, string certificateId, int reasonCode, string confirmSerial, byte[]? rootPfx = null, string? rootPassphrase = null)
    {
        var admin = await RequireAdminAsync(actorUserId);
        if (!PkiReasons.Names.ContainsKey(reasonCode)) throw new PkiException("Ungültiger Widerrufsgrund (RFC 5280).");
        var cert = await GetCertificateAsync(certificateId) ?? throw new PkiException("Zertifikat nicht gefunden.");
        if (cert.CertificateType == PkiCertificateTypes.RootCa) throw new PkiException("Die Root CA wird nicht widerrufen; ein kompromittierter Root-Schlüssel erfordert eine neue PKI und das Entfernen des Vertrauensankers (docs/pki.md).");
        if (!string.Equals(confirmSerial?.Trim().TrimStart('0'), cert.SerialNumber.TrimStart('0'), StringComparison.OrdinalIgnoreCase)) throw new PkiException("Bestätigung fehlt: bitte die Seriennummer des Zertifikats eingeben.");
        await _gate.WaitAsync();
        try
        {
            if ((await _db.QueryAsync("SELECT id FROM pki_revocations WHERE certificate_id = @c AND released_at IS NULL", new (string, object?)[] { ("c", cert.Id) })).Count > 0)
                throw new PkiException("Das Zertifikat ist bereits widerrufen bzw. gesperrt.");
            X509Certificate2? rootKey = null;
            if (cert.CertificateType == PkiCertificateTypes.IssuingCa) rootKey = LoadRootKey(cert.CaId, rootPfx, rootPassphrase);
            using var _ = rootKey;
            var now = DateTime.UtcNow;
            await _db.ExecuteAsync("INSERT INTO pki_revocations (id, certificate_id, issuing_ca_id, certificate_serial_number, revocation_time, reason_code, revoked_by, created_at, audit_reference) VALUES (@id, @c, @ca, @s, @t, @r, @by, @now, @ref)",
                new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("c", cert.Id), ("ca", cert.CaId), ("s", cert.SerialNumber), ("t", now), ("r", reasonCode), ("by", admin.Id), ("now", now), ("ref", "pki.certificate.revoke") });
            await _db.ExecuteAsync("UPDATE pki_certificates SET status = @s, revoked_at = @t, revocation_reason = @r WHERE id = @i",
                new (string, object?)[] { ("s", PkiCertificateStatuses.Revoked), ("t", now), ("r", PkiReasons.Name(reasonCode)), ("i", cert.Id) });
            await AddEventAsync(cert.Id, "revoked", PkiReasons.Name(reasonCode), admin.Id);
            await AuditAsync(admin.Id, "pki.certificate.revoke", cert.Id, $"serial={cert.SerialNumber} reason={PkiReasons.Name(reasonCode)}");
            await IssueCrlCoreAsync(cert.CaId, rootKey, admin.Id, "after-revocation");
            InvalidateCache();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Hebt nur eine <c>certificateHold</c>-Sperre wieder auf (Eintrag bleibt als Historie, fehlt aber in neuen CRLs). Alle anderen Gründe sind endgültig.</summary>
    public async Task ReleaseHoldAsync(string? actorUserId, string certificateId, byte[]? rootPfx = null, string? rootPassphrase = null)
    {
        var admin = await RequireAdminAsync(actorUserId);
        var cert = await GetCertificateAsync(certificateId) ?? throw new PkiException("Zertifikat nicht gefunden.");
        await _gate.WaitAsync();
        try
        {
            var rev = (await _db.QueryAsync("SELECT id, certificate_id, issuing_ca_id, certificate_serial_number, revocation_time, reason_code, invalidity_date, revoked_by, created_at, audit_reference, released_at FROM pki_revocations WHERE certificate_id = @c AND released_at IS NULL", new (string, object?)[] { ("c", cert.Id) })).Select(MapRev).FirstOrDefault()
                ?? throw new PkiException("Das Zertifikat ist nicht gesperrt.");
            if (rev.ReasonCode != 6) throw new PkiException("Nur eine vorläufige Sperre (certificateHold) kann aufgehoben werden; andere Widerrufe sind endgültig.");
            X509Certificate2? rootKey = null;
            if (cert.CertificateType == PkiCertificateTypes.IssuingCa) rootKey = LoadRootKey(cert.CaId, rootPfx, rootPassphrase);
            using var _ = rootKey;
            var now = DateTime.UtcNow;
            await _db.ExecuteAsync("UPDATE pki_revocations SET released_at = @t WHERE id = @i", new (string, object?)[] { ("t", now), ("i", rev.Id) });
            await _db.ExecuteAsync("UPDATE pki_certificates SET status = @s, revoked_at = NULL, revocation_reason = NULL WHERE id = @i",
                new (string, object?)[] { ("s", cert.NotAfter < now ? PkiCertificateStatuses.Expired : PkiCertificateStatuses.Active), ("i", cert.Id) });
            await AddEventAsync(cert.Id, "hold-released", "", admin.Id);
            await AuditAsync(admin.Id, "pki.certificate.revoke", cert.Id, $"serial={cert.SerialNumber} hold-released");
            await IssueCrlCoreAsync(cert.CaId, rootKey, admin.Id, "after-hold-release");
            InvalidateCache();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Entfernt den Root-Schlüssel vom Server (nur nach ausdrücklicher Bestätigung, dass er offline gesichert ist). Danach braucht jede Root-Aktion einen Upload.</summary>
    public async Task RemoveRootKeyFromServerAsync(string? actorUserId, string confirmation)
    {
        var admin = await RequireAdminAsync(actorUserId);
        if (confirmation != "ROOT-SCHLUESSEL ENTFERNEN") throw new PkiException("Bestätigung fehlt: bitte ROOT-SCHLUESSEL ENTFERNEN eingeben. Der Schlüssel kann danach nicht wiederhergestellt werden — sichern Sie ihn vorher offline.");
        if (!_keys.RootKeyOnServer) throw new PkiException("Der Root-Schlüssel liegt nicht auf dem Server.");
        _keys.RemoveRoot();
        await _db.ExecuteAsync("UPDATE pki_state SET note = '', updated_at = @now WHERE id = 1", new (string, object?)[] { ("now", DateTime.UtcNow) });
        await AuditAsync(admin.Id, "pki.key.rotate", "root", "root-key-removed-from-server");
    }

    /// <summary>Wartung (Hintergrunddienst): Ablauf markieren, Issuing-CRL vor nextUpdate erneuern, Signaturzertifikat kurz vor Ablauf erneuern.</summary>
    public async Task<List<string>> RunMaintenanceAsync()
    {
        var done = new List<string>();
        var row = await GetStateRowAsync();
        if (row is null || row["status"] != PkiStates.Initialized) return done;
        var now = DateTime.UtcNow;
        foreach (var c in (await ListCertificatesAsync()).Where(c => c.Status == PkiCertificateStatuses.Active && c.NotAfter < now))
        {
            await _db.ExecuteAsync("UPDATE pki_certificates SET status = @s WHERE id = @i", new (string, object?)[] { ("s", PkiCertificateStatuses.Expired), ("i", c.Id) });
            await AddEventAsync(c.Id, "expired", "", null);
            done.Add("expired:" + c.SerialNumber);
            InvalidateCache();
        }
        var issuing = await GetCertificateAsync(row["issuing_cert_id"]!);
        if (issuing is { Status: PkiCertificateStatuses.Active })
        {
            var crl = await GetLatestCrlAsync(issuing.Id);
            if (crl is null || crl.NextUpdate < now.AddHours(_cfg.CrlRenewBeforeHours))
            {
                try { await IssueCrlCoreAsync(issuing.Id, null, "system", null); done.Add("crl"); }
                catch (Exception ex) when (ex is PkiException or CryptographicException) { _log.LogError("PKI: CRL-Erneuerung fehlgeschlagen ({Type}).", ex.GetType().Name); }
            }
            var signing = (await ListCertificatesAsync(PkiCertificateTypes.DocumentSigning)).FirstOrDefault(c => c.Status == PkiCertificateStatuses.Active);
            if (signing is null || signing.NotAfter < now.AddDays(14))
            {
                try { await RenewSigningCertificateAsync(null, system: true); done.Add("signing-renewed"); }
                catch (Exception ex) when (ex is PkiException or CryptographicException) { _log.LogError("PKI: Erneuerung des Signaturzertifikats fehlgeschlagen ({Type}).", ex.GetType().Name); }
            }
        }
        return done;
    }
}
