using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LearnCards.Web.Services.Pdf;

/// <summary>Ergebnis der Prüfung einer losgelösten CMS-Signatur.</summary>
public sealed class CmsVerification
{
    public bool Parsed { get; set; }
    public bool SignatureValid { get; set; }
    public bool DigestMatches { get; set; }
    public string? Error { get; set; }
    public X509Certificate2? Signer { get; set; }
    public List<X509Certificate2> Certificates { get; } = new();
    public DateTime? SigningTime { get; set; }
    public string SignatureAlgorithm { get; set; } = "";
    public bool HasSigningCertificateV2 { get; set; }
}

/// <summary>
/// CMS-SignedData (RFC 5652) im CAdES-Stil für PDF-Signaturen (<c>ETSI.CAdES.detached</c>): signierte Attribute contentType, messageDigest, signingTime und
/// signing-certificate-v2 (RFC 5035), SHA-256, RSA PKCS#1 v1.5 oder ECDSA. Gebaut ausschließlich mit <c>System.Formats.Asn1</c> und den .NET-Kryptoklassen (keine Zusatzpakete).
/// Es entsteht eine technisch gültige Signatur; ihre Vertrauenswürdigkeit hängt vom Zertifikat ab (Selbstsigniert = nur für den Aussteller selbst prüfbar).
/// </summary>
public static class PdfCms
{
    private const string OidData = "1.2.840.113549.1.7.1", OidSignedData = "1.2.840.113549.1.7.2", OidSha256 = "2.16.840.1.101.3.4.2.1";
    private const string OidContentType = "1.2.840.113549.1.9.3", OidMessageDigest = "1.2.840.113549.1.9.4", OidSigningTime = "1.2.840.113549.1.9.5", OidSigningCertV2 = "1.2.840.113549.1.9.16.2.47";
    private const string OidRsaSha256 = "1.2.840.113549.1.1.11", OidEcdsaSha256 = "1.2.840.10045.4.3.2";

    public static byte[] CreateDetached(byte[] messageDigest, X509Certificate2 signer, IEnumerable<X509Certificate2> extraCerts, DateTime signingTimeUtc)
    {
        // Signierte Attribute (DER SET OF – sortiert)
        var attrs = new AsnWriter(AsnEncodingRules.DER);
        using (attrs.PushSetOf())
        {
            WriteAttr(attrs, OidContentType, w => w.WriteObjectIdentifier(OidData));
            WriteAttr(attrs, OidSigningTime, w => w.WriteUtcTime(new DateTimeOffset(DateTime.SpecifyKind(signingTimeUtc, DateTimeKind.Utc))));
            WriteAttr(attrs, OidMessageDigest, w => w.WriteOctetString(messageDigest));
            WriteAttr(attrs, OidSigningCertV2, w =>
            {
                using (w.PushSequence())          // SigningCertificateV2
                using (w.PushSequence())          // certs: SEQUENCE OF ESSCertIDv2
                using (w.PushSequence())          // ESSCertIDv2 (hashAlgorithm = SHA-256 ist der Standardwert und entfällt)
                    w.WriteOctetString(SHA256.HashData(signer.RawData));
            });
        }
        var signedAttrs = attrs.Encode();   // Tag 0x31 – das wird signiert

        byte[] signature; string sigAlg; bool rsa;
        using (var key = (AsymmetricAlgorithm?)signer.GetRSAPrivateKey() ?? signer.GetECDsaPrivateKey() ?? throw new InvalidOperationException("Das Signaturzertifikat enthält keinen unterstützten privaten Schlüssel (RSA oder ECDSA erforderlich)."))
        {
            if (key is RSA r) { signature = r.SignData(signedAttrs, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); sigAlg = OidRsaSha256; rsa = true; }
            else { signature = ((ECDsa)key).SignData(signedAttrs, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence); sigAlg = OidEcdsaSha256; rsa = false; }
        }

        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(OidSignedData);
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (w.PushSequence())          // SignedData
            {
                w.WriteInteger(1);
                using (w.PushSetOf()) using (w.PushSequence()) w.WriteObjectIdentifier(OidSha256);
                using (w.PushSequence()) w.WriteObjectIdentifier(OidData);   // detached: kein Inhalt
                using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    w.WriteEncodedValue(signer.RawData);
                    foreach (var c in extraCerts) if (c.Thumbprint != signer.Thumbprint) w.WriteEncodedValue(c.RawData);
                }
                using (w.PushSetOf())
                using (w.PushSequence())      // SignerInfo
                {
                    w.WriteInteger(1);
                    using (w.PushSequence())  // IssuerAndSerialNumber
                    {
                        w.WriteEncodedValue(signer.IssuerName.RawData);
                        w.WriteInteger(new System.Numerics.BigInteger(signer.GetSerialNumber(), isUnsigned: true, isBigEndian: false));
                    }
                    using (w.PushSequence()) w.WriteObjectIdentifier(OidSha256);
                    var implicitAttrs = (byte[])signedAttrs.Clone(); implicitAttrs[0] = 0xA0;   // [0] IMPLICIT
                    w.WriteEncodedValue(implicitAttrs);
                    using (w.PushSequence()) { w.WriteObjectIdentifier(sigAlg); if (rsa) w.WriteNull(); }
                    w.WriteOctetString(signature);
                }
            }
        }
        return w.Encode();

        static void WriteAttr(AsnWriter wr, string oid, Action<AsnWriter> value)
        {
            using (wr.PushSequence()) { wr.WriteObjectIdentifier(oid); using (wr.PushSetOf()) value(wr); }
        }
    }

    /// <summary>Prüft eine losgelöste CMS-Signatur gegen den Hash (SHA-256) der signierten Daten: Aufbau, messageDigest, Signatur des Unterzeichners. Die Zertifikatskette wird hier nicht bewertet.</summary>
    public static CmsVerification VerifyDetached(byte[] cms, byte[] contentSha256)
    {
        var result = new CmsVerification();
        try
        {
            var top = new AsnReader(cms, AsnEncodingRules.BER).ReadSequence();
            if (top.ReadObjectIdentifier() != OidSignedData) { result.Error = "Keine CMS-SignedData-Struktur."; return result; }
            var sd = top.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            sd.ReadInteger();
            sd.ReadSetOf();                      // digestAlgorithms
            sd.ReadSequence();                   // encapContentInfo (detached)
            if (sd.HasData && sd.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                var certs = sd.ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0));
                while (certs.HasData) result.Certificates.Add(X509CertificateLoader.LoadCertificate(certs.ReadEncodedValue().ToArray()));
            }
            if (sd.HasData && sd.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1))) sd.ReadEncodedValue();   // CRLs ignorieren
            var si = sd.ReadSetOf().ReadSequence();
            si.ReadInteger();
            var sid = si.ReadSequence();
            var issuer = sid.ReadEncodedValue().ToArray(); var serial = sid.ReadInteger();
            var digestAlg = si.ReadSequence().ReadObjectIdentifier();
            if (digestAlg != OidSha256) { result.Error = "Nur SHA-256 wird unterstützt."; return result; }
            var attrsRaw = si.ReadEncodedValue().ToArray();    // [0] IMPLICIT SET OF
            var sigAlg = si.ReadSequence().ReadObjectIdentifier();
            var signature = si.ReadOctetString();
            result.Parsed = true; result.SignatureAlgorithm = sigAlg == OidRsaSha256 ? "RSA-SHA256" : sigAlg == OidEcdsaSha256 ? "ECDSA-SHA256" : sigAlg;

            // Attribute lesen
            byte[]? digestAttr = null;
            var attrReader = new AsnReader(attrsRaw, AsnEncodingRules.BER).ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0));
            while (attrReader.HasData)
            {
                var a = attrReader.ReadSequence(); var oid = a.ReadObjectIdentifier(); var vals = a.ReadSetOf();
                if (oid == OidMessageDigest) digestAttr = vals.ReadOctetString();
                else if (oid == OidSigningTime) { var t = vals.PeekTag(); result.SigningTime = (t.TagValue == 23 ? vals.ReadUtcTime() : vals.ReadGeneralizedTime()).UtcDateTime; }
                else if (oid == OidSigningCertV2) result.HasSigningCertificateV2 = true;
            }
            result.DigestMatches = digestAttr is not null && digestAttr.AsSpan().SequenceEqual(contentSha256);

            result.Signer = result.Certificates.FirstOrDefault(c => c.IssuerName.RawData.AsSpan().SequenceEqual(IssuerContent(issuer)) && new System.Numerics.BigInteger(c.GetSerialNumber(), isUnsigned: true, isBigEndian: false) == serial)
                            ?? result.Certificates.FirstOrDefault(c => c.IssuerName.RawData.AsSpan().SequenceEqual(issuer) && new System.Numerics.BigInteger(c.GetSerialNumber(), isUnsigned: true, isBigEndian: false) == serial);
            if (result.Signer is null) { result.Error = "Das Unterzeichner-Zertifikat ist nicht in der Signatur enthalten."; return result; }

            var signedBytes = (byte[])attrsRaw.Clone(); signedBytes[0] = 0x31;   // zurück zu SET OF
            if (sigAlg == OidRsaSha256)
            {
                using var rsa = result.Signer.GetRSAPublicKey();
                result.SignatureValid = rsa is not null && rsa.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            else if (sigAlg == OidEcdsaSha256)
            {
                using var ec = result.Signer.GetECDsaPublicKey();
                result.SignatureValid = ec is not null && ec.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            }
            else result.Error = "Nicht unterstützter Signaturalgorithmus.";
            if (!result.SignatureValid && result.Error is null) result.Error = "Die Signatur stimmt nicht mit dem Zertifikat überein.";
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or InvalidOperationException or ArgumentException or FormatException)
        {
            result.Error ??= "Die Signatur konnte nicht gelesen werden (" + ex.GetType().Name + ").";
        }
        return result;
    }

    private static byte[] IssuerContent(byte[] encoded) => encoded;
}
