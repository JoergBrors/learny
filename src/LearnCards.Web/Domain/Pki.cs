namespace LearnCards.Web.Domain;

/// <summary>Zustand der Learny-PKI (ADR 0035).</summary>
public static class PkiStates
{
    public const string NotConfigured = "not_configured";                  // Konfiguration unvollständig (z. B. PKI_KEY_PASSWORD fehlt)
    public const string InitializationRequired = "initialization_required"; // konfigurierbar, aber noch nicht eingerichtet
    public const string Initializing = "initializing";
    public const string Initialized = "initialized";                      // in der Datenbank eingerichtet, noch nicht geprüft
    public const string Operational = "operational";                      // Schlüssel, Kette, Signaturzertifikat und CRL geprüft
    public const string KeyUnavailable = "key_unavailable";               // Schlüsseldatei fehlt/unlesbar/passt nicht — Signierung gesperrt
    public const string MaintenanceRequired = "maintenance_required";     // z. B. CRL abgelaufen, Signaturzertifikat abgelaufen/widerrufen, Initialisierung unvollständig
    public const string Locked = "locked";                                // z. B. Issuing CA widerrufen
}

public static class PkiCertificateTypes
{
    public const string RootCa = "root_ca";
    public const string IssuingCa = "issuing_ca";
    public const string DocumentSigning = "document_signing";
    public static bool IsCa(string type) => type is RootCa or IssuingCa;
}

public static class PkiCertificateStatuses
{
    public const string Active = "active";
    public const string Revoked = "revoked";
    public const string Expired = "expired";
    public const string Superseded = "superseded";
}

/// <summary>Widerrufsgründe nach RFC 5280 §5.3.1 (CRLReason). Wert 7 ist unbenutzt, 8 (removeFromCRL) gehört nur in Delta-CRLs und wird nicht angeboten.</summary>
public static class PkiReasons
{
    public static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [0] = "unspecified", [1] = "keyCompromise", [2] = "cACompromise", [3] = "affiliationChanged", [4] = "superseded",
        [5] = "cessationOfOperation", [6] = "certificateHold", [9] = "privilegeWithdrawn", [10] = "aACompromise",
    };
    public static string Name(int code) => Names.TryGetValue(code, out var n) ? n : "unknown";
    public static bool TryParse(string? text, out int code)
    {
        code = -1;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (int.TryParse(text, out var n) && Names.ContainsKey(n)) { code = n; return true; }
        foreach (var (k, v) in Names) if (string.Equals(v, text.Trim(), StringComparison.OrdinalIgnoreCase)) { code = k; return true; }
        return false;
    }
}

/// <summary>Eine Zeile des Zertifikatsregisters (<c>pki_certificates</c>). Das öffentliche Zertifikat steht vollständig (PEM) in der Datenbank, der private Schlüssel nie.</summary>
public class PkiCertificate
{
    public string Id { get; set; } = "";
    /// <summary>ID des ausstellenden CA-Zertifikats; bei der Root CA die eigene ID.</summary>
    public string CaId { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string CertificateType { get; set; } = "";
    public string CertificatePem { get; set; } = "";
    public string CertificateSha256 { get; set; } = "";
    public string SubjectKeyId { get; set; } = "";
    public string PublicKeyAlgorithm { get; set; } = "";
    public string SignatureAlgorithm { get; set; } = "";
    public DateTime NotBefore { get; set; }
    public DateTime NotAfter { get; set; }
    public DateTime IssuedAt { get; set; }
    public string? IssuedBy { get; set; }
    public string Status { get; set; } = PkiCertificateStatuses.Active;
    public DateTime? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }
    /// <summary>Verweis auf die Schlüsseldatei im Schlüsselspeicher (Dateiname) — kein Schlüsselmaterial; leer bei der Root CA (offline).</summary>
    public string KeyReference { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class PkiRevocation
{
    public string Id { get; set; } = "";
    public string CertificateId { get; set; } = "";
    public string IssuingCaId { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public DateTime RevocationTime { get; set; }
    public int ReasonCode { get; set; }
    public DateTime? InvalidityDate { get; set; }
    public string? RevokedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string AuditReference { get; set; } = "";
    /// <summary>Nur bei <c>certificateHold</c>: Zeitpunkt der Wiederfreigabe (der Eintrag bleibt als Historie erhalten, fehlt aber in neuen CRLs).</summary>
    public DateTime? ReleasedAt { get; set; }
}

public class PkiCrl
{
    public string Id { get; set; } = "";
    public string IssuerCaId { get; set; } = "";
    public long CrlNumber { get; set; }
    public DateTime ThisUpdate { get; set; }
    public DateTime NextUpdate { get; set; }
    public byte[] Der { get; set; } = Array.Empty<byte>();
    public string Sha256 { get; set; } = "";
    public string SignatureAlgorithm { get; set; } = "";
    public int EntryCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsFresh(DateTime now) => ThisUpdate <= now.AddMinutes(5) && NextUpdate > now;
}

public class PkiCertificateEvent
{
    public string Id { get; set; } = "";
    public string CertificateId { get; set; } = "";
    public string Event { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? Actor { get; set; }
    public DateTime CreatedAt { get; set; }
}
