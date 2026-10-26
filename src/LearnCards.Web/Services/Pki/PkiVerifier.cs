using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services.Pki;

/// <summary>Getrennte Teilergebnisse der PKI-Prüfung. "Öffentlich vertrauenswürdig" ist immer false — die Learny-PKI wird von Adobe, Betriebssystemen und Vertrauenslisten nicht automatisch anerkannt.</summary>
public sealed class PkiVerification
{
    public bool ChainValid { get; set; }
    public bool ProfileOk { get; set; }
    /// <summary>Kette endet an einem Learny-Vertrauensanker aus dem Register und das Zertifikat ist dort eingetragen.</summary>
    public bool IssuerConfirmed { get; set; }
    /// <summary>good | revoked | unknown</summary>
    public string Revocation { get; set; } = "unknown";
    public bool CrlFresh { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }
    public bool PubliclyTrusted => false;
    public List<string> Problems { get; } = new();
    public string? CertificateId { get; set; }
}

/// <summary>
/// Prüfung von Zertifikatsketten gegen die Learny-PKI (ADR 0035). Der Vertrauensanker kommt ausschließlich aus dem Zertifikatsregister (optional gepinnt über
/// <c>PKI_TRUSTED_ROOT_SHA256</c>) — nie aus dem zu prüfenden Dokument. Eingebettete Zertifikate dienen nur als Hilfe beim Aufbau der Kette.
/// </summary>
public sealed class PkiVerifier
{
    private readonly PkiService _pki;
    public PkiVerifier(PkiService pki) { _pki = pki; }

    public async Task<PkiVerification> VerifyAsync(X509Certificate2 leaf, IReadOnlyList<X509Certificate2> embedded, DateTime? at = null)
    {
        var v = new PkiVerification();
        var now = DateTime.UtcNow;
        var all = await _pki.ListCertificatesAsync();
        var pins = _pki.Settings.TrustedRootSha256;
        var roots = all.Where(c => c.CertificateType == PkiCertificateTypes.RootCa && (pins.Count == 0 || pins.Contains(c.CertificateSha256))).ToList();
        if (roots.Count == 0) { v.Problems.Add("Kein Learny-Vertrauensanker vorhanden."); return v; }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        foreach (var r in roots) chain.ChainPolicy.CustomTrustStore.Add(PkiService.ToX509(r));
        foreach (var c in embedded) chain.ChainPolicy.ExtraStore.Add(c);
        foreach (var c in all.Where(c => c.CertificateType == PkiCertificateTypes.IssuingCa)) chain.ChainPolicy.ExtraStore.Add(PkiService.ToX509(c));
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;   // Widerruf prüft dieselbe Klasse unten selbst gegen Register und CRL
        chain.ChainPolicy.VerificationTime = at ?? now;
        var built = chain.Build(leaf);
        var elements = chain.ChainElements.Select(e => e.Certificate).ToList();
        v.ChainValid = built && elements.Count >= 2 && roots.Any(r => r.CertificateSha256 == elements[^1].GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant());
        if (!built) foreach (var s in chain.ChainStatus) v.Problems.Add("Kette: " + s.StatusInformation.Trim());
        else if (!v.ChainValid) v.Problems.Add("Die Kette endet nicht an einem Learny-Vertrauensanker.");

        // Profilprüfung (Key Usage, Basic Constraints, EKU)
        var profile = CheckProfile(leaf, PkiRole.Signing);
        for (var i = 1; i < elements.Count; i++) profile.AddRange(CheckProfile(elements[i], i == elements.Count - 1 ? PkiRole.Root : PkiRole.Issuing));
        v.ProfileOk = profile.Count == 0;
        v.Problems.AddRange(profile);

        // Register: Ist das Zertifikat von Learny ausgestellt?
        var sha = leaf.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
        var reg = all.FirstOrDefault(c => c.CertificateSha256 == sha);
        v.CertificateId = reg?.Id;
        v.IssuerConfirmed = v.ChainValid && reg is not null && elements.Skip(1).All(e => all.Any(c => c.CertificateSha256 == e.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant()));
        if (reg is null && v.ChainValid) v.Problems.Add("Das Zertifikat ist nicht im Learny-Zertifikatsregister eingetragen.");

        // Widerruf: Register und CRL
        if (reg is not null) await CheckRevocationAsync(v, reg, all, now);
        return v;
    }

    private async Task CheckRevocationAsync(PkiVerification v, PkiCertificate leaf, List<PkiCertificate> all, DateTime now)
    {
        var revs = await _pki.ListRevocationsAsync();
        var chainCerts = new List<PkiCertificate> { leaf };
        var cur = leaf;
        while (cur.CaId != cur.Id && all.FirstOrDefault(c => c.Id == cur.CaId) is { } up) { chainCerts.Add(up); cur = up; }
        var allGood = true; var fresh = true;
        foreach (var c in chainCerts.Where(c => c.CaId != c.Id))   // die Root selbst hat keinen Aussteller
        {
            var active = revs.FirstOrDefault(r => r.CertificateId == c.Id && r.ReleasedAt is null);
            if (active is not null) { v.Revocation = "revoked"; v.RevokedAt = active.RevocationTime; v.RevocationReason = PkiReasons.Name(active.ReasonCode); v.Problems.Add($"Zertifikat {c.SerialNumber} ist widerrufen ({PkiReasons.Name(active.ReasonCode)})."); v.CrlFresh = true; return; }
            var ca = all.FirstOrDefault(x => x.Id == c.CaId);
            var crl = ca is null ? null : await _pki.GetLatestCrlAsync(ca.Id);
            if (ca is null || crl is null) { allGood = false; fresh = false; v.Problems.Add("Keine CRL des Ausstellers vorhanden."); continue; }
            var info = PkiCrypto.ParseAndVerifyCrl(crl.Der, PkiService.ToX509(ca));
            if (!info.SignatureValid || !info.IssuerNameMatches || !info.AuthorityKeyMatches) { allGood = false; fresh = false; v.Problems.Add("Die CRL des Ausstellers ist ungültig (Signatur/Aussteller)."); continue; }
            if (!info.IsValidFor(now)) { allGood = false; fresh = false; v.Problems.Add("Die CRL des Ausstellers ist nicht aktuell (nextUpdate überschritten)."); }
            if (info.Entries.FirstOrDefault(e => e.SerialHex.TrimStart('0') == c.SerialNumber.TrimStart('0')) is { SerialHex: not null } hit && hit.Time != default)
            { v.Revocation = "revoked"; v.RevokedAt = hit.Time; v.RevocationReason = PkiReasons.Name(hit.Reason); v.Problems.Add($"Zertifikat {c.SerialNumber} steht in der CRL ({PkiReasons.Name(hit.Reason)})."); v.CrlFresh = true; return; }
        }
        v.CrlFresh = fresh;
        v.Revocation = allGood ? "good" : "unknown";
    }

    /// <summary>Strukturprüfung nach dem Rollenprofil (ADR 0035): Basic Constraints, Key Usage, EKU, Schlüsselkennungen.</summary>
    public static List<string> CheckProfile(X509Certificate2 c, PkiRole role)
    {
        var p = new List<string>();
        var bc = c.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        var ku = c.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        var eku = c.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        var name = role + ": ";
        if (PkiCrypto.SubjectKeyId(c).Length == 0) p.Add(name + "subjectKeyIdentifier fehlt.");
        if (role == PkiRole.Signing)
        {
            if (bc is not null && bc.CertificateAuthority) p.Add(name + "Signaturzertifikat darf keine CA sein.");
            if (ku is null || !ku.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)) p.Add(name + "keyUsage digitalSignature fehlt.");
            if (ku is not null && ku.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign)) p.Add(name + "keyCertSign ist für Signaturzertifikate unzulässig.");
            if (eku is null || !eku.EnhancedKeyUsages.OfType<Oid>().Any(o => o.Value == PkiCrypto.OidDocumentSigning)) p.Add(name + "extendedKeyUsage id-kp-documentSigning fehlt.");
        }
        else
        {
            if (bc is null || !bc.CertificateAuthority) p.Add(name + "basicConstraints CA:TRUE fehlt.");
            if (bc is not null && !bc.Critical) p.Add(name + "basicConstraints muss kritisch sein.");
            if (ku is null || !ku.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign) || !ku.KeyUsages.HasFlag(X509KeyUsageFlags.CrlSign)) p.Add(name + "keyUsage keyCertSign/cRLSign fehlt.");
            if (role == PkiRole.Issuing && bc is { HasPathLengthConstraint: true, PathLengthConstraint: not 0 }) p.Add(name + "pathLenConstraint der Issuing CA muss 0 sein.");
        }
        return p;
    }

    /// <summary>Selbsttest für die Administration: prüft Zustand, Profile, Kette, CRLs und eine Probesignatur mit dem Signaturschlüssel.</summary>
    public async Task<List<PkiCheck>> SelfTestAsync()
    {
        var res = new List<PkiCheck>();
        var st = await _pki.GetStatusAsync();
        res.Add(new("Zustand", st.State == PkiStates.Operational, st.State + " – " + st.Message));
        if (st.Root is null || st.Issuing is null) return res;
        var root = PkiService.ToX509(st.Root); var issuing = PkiService.ToX509(st.Issuing);
        var rootProblems = CheckProfile(root, PkiRole.Root); var issProblems = CheckProfile(issuing, PkiRole.Issuing);
        res.Add(new("Root-CA-Profil (CA:TRUE, keyCertSign, cRLSign, SKI)", rootProblems.Count == 0, string.Join(" ", rootProblems)));
        res.Add(new("Issuing-CA-Profil (CA:TRUE pathLen 0, keyCertSign, cRLSign)", issProblems.Count == 0, string.Join(" ", issProblems)));
        res.Add(new("Root-Selbstsignatur", root.SubjectName.RawData.AsSpan().SequenceEqual(root.IssuerName.RawData), ""));
        var (material, reason) = await _pki.GetSigningMaterialAsync();
        if (material is null) { res.Add(new("Signaturmaterial", false, reason ?? "nicht verfügbar")); }
        else
        {
            var v = await VerifyAsync(material.Certificate, material.Chain);
            res.Add(new("Kettenprüfung gegen den Learny-Vertrauensanker", v.ChainValid, string.Join(" ", v.Problems)));
            res.Add(new("Signaturprofil (documentSigning, digitalSignature, CA:FALSE)", v.ProfileOk, ""));
            res.Add(new("Widerrufsstatus (Register + aktuelle CRL)", v.Revocation == "good", v.Revocation + (v.CrlFresh ? "" : " (CRL nicht aktuell)")));
            var data = RandomNumberGenerator.GetBytes(64);
            bool sigOk;
            if (material.Certificate.GetRSAPrivateKey() is { } rsa) { using (rsa) { sigOk = material.Certificate.GetRSAPublicKey()!.VerifyData(data, rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); } }
            else { using var ec = material.Certificate.GetECDsaPrivateKey()!; sigOk = material.Certificate.GetECDsaPublicKey()!.VerifyData(data, ec.SignData(data, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256); }
            res.Add(new("Probesignatur mit dem Signaturschlüssel", sigOk, ""));
        }
        foreach (var (label, crl, cert) in new[] { ("Root-CRL", st.RootCrl, root), ("Issuing-CRL", st.IssuingCrl, issuing) })
        {
            var ok = false; var detail = "keine CRL";
            if (crl is not null)
            {
                var info = PkiCrypto.ParseAndVerifyCrl(crl.Der, cert);
                ok = info.IsValidFor(DateTime.UtcNow);
                detail = $"Nr. {info.Number}, Einträge {info.Entries.Count}, gültig bis {info.NextUpdate:yyyy-MM-dd HH:mm} UTC" + (info.Error is null ? "" : " – " + info.Error);
            }
            res.Add(new(label + " (Signatur, Aussteller, aktuell)", ok, detail));
        }
        return res;
    }
}
