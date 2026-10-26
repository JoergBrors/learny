using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LearnCards.Web.Services.Pki;

public enum PkiRole { Root, Issuing, Signing }

public sealed record CrlEntrySpec(byte[] Serial, DateTimeOffset RevocationTime, int ReasonCode, DateTimeOffset? InvalidityDate = null);

public sealed class CrlInfo
{
    public bool Parsed { get; set; }
    public bool SignatureValid { get; set; }
    public string? Error { get; set; }
    public int Version { get; set; }
    public DateTime ThisUpdate { get; set; }
    public DateTime? NextUpdate { get; set; }
    public BigInteger? Number { get; set; }
    public string? AuthorityKeyId { get; set; }
    public string SignatureAlgorithm { get; set; } = "";
    public bool IssuerNameMatches { get; set; }
    public bool AuthorityKeyMatches { get; set; }
    public List<(string SerialHex, DateTime Time, int Reason)> Entries { get; } = new();
    public bool IsValidFor(DateTime now) => Parsed && SignatureValid && IssuerNameMatches && AuthorityKeyMatches && Number is not null && NextUpdate is { } n && n > now && ThisUpdate <= now.AddMinutes(5);
}

/// <summary>
/// X.509-Profile und CRL-Erzeugung der Learny-PKI (ADR 0035) mit den .NET-Kryptoklassen (RFC 5280). Keine proprietären Formate: Zertifikate sind X.509 v3, CRLs X.509 v2 (DER),
/// signiert mit RSASSA-PKCS1-v1_5/SHA-256 (RFC 8017/4055) bzw. ECDSA/SHA-384. Erweiterungen je Rolle:
/// <list type="bullet">
/// <item><b>Root CA</b>: basicConstraints CA:TRUE (pathLen 1, kritisch), keyUsage keyCertSign+cRLSign (kritisch), subjectKeyIdentifier, authorityKeyIdentifier.</item>
/// <item><b>Issuing CA</b>: basicConstraints CA:TRUE (pathLen 0, kritisch), keyUsage keyCertSign+cRLSign (kritisch), SKI, AKI, cRLDistributionPoints (Root-CRL), authorityInfoAccess (caIssuers).</item>
/// <item><b>Dokumentsignatur</b>: basicConstraints CA:FALSE (kritisch), keyUsage digitalSignature+contentCommitment (kritisch), extendedKeyUsage id-kp-documentSigning (RFC 9336), SKI, AKI, CRL-DP, AIA.</item>
/// </list>
/// Es werden keine Certificate Policies und keine OCSP-Adressen eingetragen (kein OCSP-Responder, keine selbst erfundenen OIDs).
/// </summary>
public static class PkiCrypto
{
    public const string OidDocumentSigning = "1.3.6.1.5.5.7.3.36";   // id-kp-documentSigning (RFC 9336)
    private const string OidCrlNumber = "2.5.29.20", OidReasonCode = "2.5.29.21", OidInvalidityDate = "2.5.29.24", OidAki = "2.5.29.35";

    public static bool IsEcdsa(string alg) => alg == "ecdsa";

    public static AsymmetricAlgorithm NewKey(string alg, PkiRole role) => IsEcdsa(alg)
        ? ECDsa.Create(role == PkiRole.Signing ? ECCurve.NamedCurves.nistP256 : ECCurve.NamedCurves.nistP384)
        : RSA.Create(role == PkiRole.Root ? 4096 : 3072);

    public static HashAlgorithmName HashFor(string alg) => IsEcdsa(alg) ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256;

    private static CertificateRequest Request(string subject, AsymmetricAlgorithm key, string alg) => key switch
    {
        RSA rsa => new CertificateRequest(subject, rsa, HashFor(alg), RSASignaturePadding.Pkcs1),
        ECDsa ec => new CertificateRequest(subject, ec, HashFor(alg)),
        _ => throw new ArgumentException("Nur RSA und ECDSA werden unterstützt."),
    };

    /// <summary>Zufällige, positive Seriennummer (128 Bit, höchstes Bit 0, erstes Byte ≠ 0); Eindeutigkeit je CA erzwingt zusätzlich der Datenbankindex (nie wiederverwendet).</summary>
    public static byte[] NewSerial()
    {
        var s = RandomNumberGenerator.GetBytes(16);
        s[0] &= 0x7F;
        if (s[0] == 0) s[0] = 0x01;
        return s;
    }

    public static string SerialHex(ReadOnlySpan<byte> serialBigEndian)
    {
        var i = 0;
        while (i < serialBigEndian.Length - 1 && serialBigEndian[i] == 0) i++;
        return Convert.ToHexString(serialBigEndian[i..]).ToLowerInvariant();
    }

    public static string SerialHex(X509Certificate2 cert) => SerialHex(cert.SerialNumberBytes.Span);
    public static byte[] SerialBytes(string hex) => Convert.FromHexString(hex.Length % 2 == 0 ? hex : "0" + hex);

    private static X509Certificate2 WithKey(X509Certificate2 pub, AsymmetricAlgorithm key) => key switch
    {
        RSA r => pub.CopyWithPrivateKey(r),
        ECDsa e => pub.CopyWithPrivateKey(e),
        _ => throw new ArgumentException("Nur RSA und ECDSA werden unterstützt."),
    };

    public static X509Certificate2 CreateRoot(string subject, AsymmetricAlgorithm key, string alg, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var req = Request(subject, key, alg);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var ski = new X509SubjectKeyIdentifierExtension(req.PublicKey, false);
        req.CertificateExtensions.Add(ski);
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(ski));
        using var self = req.Create(new X500DistinguishedName(subject), SelfSignedGenerator(key, alg), notBefore, notAfter, NewSerial());
        return WithKey(self, key);
    }

    private static X509SignatureGenerator SelfSignedGenerator(AsymmetricAlgorithm key, string alg) => key switch
    {
        RSA r => X509SignatureGenerator.CreateForRSA(r, RSASignaturePadding.Pkcs1),
        ECDsa e => X509SignatureGenerator.CreateForECDsa(e),
        _ => throw new ArgumentException(),
    };

    public static X509Certificate2 CreateIssuing(string subject, AsymmetricAlgorithm key, string alg, X509Certificate2 rootWithKey, DateTimeOffset notBefore, DateTimeOffset notAfter, byte[] serial, string rootCrlUrl, string rootCertUrl)
    {
        var req = Request(subject, key, alg);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(rootWithKey, true, false));
        req.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(new[] { rootCrlUrl }));
        req.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(null, new[] { rootCertUrl }));
        using var pub = req.Create(rootWithKey, notBefore, notAfter, serial);
        return WithKey(pub, key);
    }

    public static X509Certificate2 CreateSigning(string subject, AsymmetricAlgorithm key, string alg, X509Certificate2 issuingWithKey, DateTimeOffset notBefore, DateTimeOffset notAfter, byte[] serial, string crlUrl, string issuerCertUrl)
    {
        var req = Request(subject, key, alg);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(OidDocumentSigning, "id-kp-documentSigning") }, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuingWithKey, true, false));
        req.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(new[] { crlUrl }));
        req.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(null, new[] { issuerCertUrl }));
        using var pub = req.Create(issuingWithKey, notBefore, notAfter, serial);
        return WithKey(pub, key);
    }

    public static string KeyAlgorithmName(X509Certificate2 c)
    {
        if (c.GetRSAPublicKey() is { } rsa) { using (rsa) return "RSA-" + rsa.KeySize; }
        if (c.GetECDsaPublicKey() is { } ec) { using (ec) return "ECDSA-" + (ec.KeySize == 384 ? "P384" : ec.KeySize == 256 ? "P256" : "P" + ec.KeySize); }
        return c.PublicKey.Oid.FriendlyName ?? c.PublicKey.Oid.Value ?? "";
    }

    public static string SubjectKeyId(X509Certificate2 c) => c.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifier?.ToLowerInvariant() ?? "";

    // ─── CRL ────────────────────────────────────────────────────────────────

    /// <summary>X.509-v2-CRL (DER) mit cRLNumber und authorityKeyIdentifier, signiert vom ausstellenden CA-Zertifikat (mit privatem Schlüssel).</summary>
    public static byte[] BuildCrl(X509Certificate2 issuerWithKey, IEnumerable<CrlEntrySpec> entries, long crlNumber, DateTimeOffset thisUpdate, DateTimeOffset nextUpdate, string alg)
    {
        var b = new CertificateRevocationListBuilder();
        foreach (var e in entries) b.AddEntry(e.Serial, e.RevocationTime, (X509RevocationReason)e.ReasonCode);
        return issuerWithKey.GetRSAPublicKey() is not null
            ? b.Build(issuerWithKey, new BigInteger(crlNumber), nextUpdate, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, thisUpdate)
            : b.Build(issuerWithKey, new BigInteger(crlNumber), nextUpdate, HashAlgorithmName.SHA384, null, thisUpdate);
    }

    private static DateTime ReadTime(AsnReader r) => (r.PeekTag().TagValue == 23 ? r.ReadUtcTime() : r.ReadGeneralizedTime()).UtcDateTime;

    private static (HashAlgorithmName Hash, bool Rsa)? SigAlg(string oid) => oid switch
    {
        "1.2.840.113549.1.1.11" => (HashAlgorithmName.SHA256, true), "1.2.840.113549.1.1.12" => (HashAlgorithmName.SHA384, true), "1.2.840.113549.1.1.13" => (HashAlgorithmName.SHA512, true),
        "1.2.840.10045.4.3.2" => (HashAlgorithmName.SHA256, false), "1.2.840.10045.4.3.3" => (HashAlgorithmName.SHA384, false), "1.2.840.10045.4.3.4" => (HashAlgorithmName.SHA512, false),
        _ => null,
    };

    /// <summary>
    /// Liest eine CRL (X.509 v2, DER) unabhängig von der Erzeugung und prüft Signatur, Ausstellername und authorityKeyIdentifier gegen das ausstellende CA-Zertifikat.
    /// Zeitliche Gültigkeit bewertet der Aufrufer (<see cref="CrlInfo.IsValidFor"/>).
    /// </summary>
    public static CrlInfo ParseAndVerifyCrl(byte[] der, X509Certificate2 issuer)
    {
        var info = new CrlInfo();
        try
        {
            var seq = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
            var tbsRaw = seq.PeekEncodedValue().ToArray();
            var tbs = seq.ReadSequence();
            var outerAlg = seq.ReadSequence().ReadObjectIdentifier();
            var signature = seq.ReadBitString(out _);
            if (tbs.PeekTag().TagValue == 2) info.Version = (int)tbs.ReadInteger() + 1;
            var innerAlg = tbs.ReadSequence().ReadObjectIdentifier();
            var issuerName = tbs.ReadEncodedValue().ToArray();
            info.ThisUpdate = ReadTime(tbs);
            if (tbs.HasData && (tbs.PeekTag().TagValue is 23 or 24)) info.NextUpdate = ReadTime(tbs);
            if (tbs.HasData && tbs.PeekTag().TagValue == 16 && tbs.PeekTag().TagClass == TagClass.Universal)
            {
                var list = tbs.ReadSequence();
                while (list.HasData)
                {
                    var e = list.ReadSequence();
                    var serial = e.ReadInteger().ToByteArray(isUnsigned: true, isBigEndian: true);
                    var time = ReadTime(e);
                    var reason = 0;
                    if (e.HasData)
                    {
                        var exts = e.ReadSequence();
                        while (exts.HasData)
                        {
                            var ext = exts.ReadSequence(); var oid = ext.ReadObjectIdentifier();
                            if (ext.HasData && ext.PeekTag().TagValue == 1) ext.ReadBoolean();
                            var val = ext.ReadOctetString();
                            if (oid == OidReasonCode) reason = (int)new BigInteger(new AsnReader(val, AsnEncodingRules.DER).ReadEnumeratedBytes().Span, isUnsigned: true, isBigEndian: true);
                        }
                    }
                    info.Entries.Add((SerialHex(serial), time, reason));
                }
            }
            if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                var exts = tbs.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
                while (exts.HasData)
                {
                    var ext = exts.ReadSequence(); var oid = ext.ReadObjectIdentifier();
                    if (ext.HasData && ext.PeekTag().TagValue == 1) ext.ReadBoolean();
                    var val = ext.ReadOctetString();
                    if (oid == OidCrlNumber) info.Number = new AsnReader(val, AsnEncodingRules.DER).ReadInteger();
                    else if (oid == OidAki)
                    {
                        var aki = new AsnReader(val, AsnEncodingRules.DER).ReadSequence();
                        if (aki.HasData && aki.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
                            info.AuthorityKeyId = Convert.ToHexString(aki.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0))).ToLowerInvariant();
                    }
                }
            }
            info.Parsed = true;
            info.SignatureAlgorithm = outerAlg;
            info.IssuerNameMatches = issuerName.AsSpan().SequenceEqual(issuer.SubjectName.RawData);
            info.AuthorityKeyMatches = info.AuthorityKeyId is not null && info.AuthorityKeyId == SubjectKeyId(issuer);
            if (outerAlg != innerAlg) { info.Error = "Signaturalgorithmen in der CRL stimmen nicht überein."; return info; }
            if (SigAlg(outerAlg) is not { } alg) { info.Error = "Nicht unterstützter CRL-Signaturalgorithmus (" + outerAlg + ")."; return info; }
            if (alg.Rsa)
            {
                using var rsa = issuer.GetRSAPublicKey();
                info.SignatureValid = rsa is not null && rsa.VerifyData(tbsRaw, signature, alg.Hash, RSASignaturePadding.Pkcs1);
            }
            else
            {
                using var ec = issuer.GetECDsaPublicKey();
                info.SignatureValid = ec is not null && ec.VerifyData(tbsRaw, signature, alg.Hash, DSASignatureFormat.Rfc3279DerSequence);
            }
            if (!info.SignatureValid) info.Error = "Die CRL-Signatur stimmt nicht mit dem Ausstellerzertifikat überein.";
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or InvalidOperationException or ArgumentException or OverflowException)
        {
            info.Error ??= "Die CRL konnte nicht gelesen werden (" + ex.GetType().Name + ").";
        }
        return info;
    }
}
