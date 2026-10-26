using System.Globalization;

namespace LearnCards.Web.Infrastructure;

/// <summary>
/// Konfiguration der Learny-PKI (ADR 0035). Die PKI ist optional und wird nie durch einen Neustart eingerichtet, sondern nur durch die ausdrückliche Initialisierung
/// (Administration → PKI oder <c>--pki-init</c>). Private Schlüssel liegen als passwortgeschützte PKCS#12-Dateien in <see cref="KeyDir"/> (außerhalb des Webroots) — nie in der Datenbank.
/// Das Schlüsselpasswort stammt ausschließlich aus der geschützten Konfiguration (<c>PKI_KEY_PASSWORD</c>, Secret-Verwaltung/.env).
/// </summary>
public class PkiSettings
{
    public const int MinPasswordLength = 16;

    /// <summary>Verzeichnis der verschlüsselten Schlüsseldateien (Standard: <c>data/pki</c>, im Container ein persistentes Volume).</summary>
    public string KeyDir { get; set; } = "";
    public string KeyPassword { get; set; } = "";
    /// <summary><c>rsa</c> (Root 4096, Issuing/Signatur 3072 Bit) oder <c>ecdsa</c> (P-384 für CAs, P-256 für Signaturzertifikate); jeweils SHA-2.</summary>
    public string KeyAlgorithm { get; set; } = "rsa";
    public string Organization { get; set; } = "Learny";
    public int RootValidityYears { get; set; } = 20;
    public int IssuingValidityYears { get; set; } = 10;
    public int SigningValidityDays { get; set; } = 730;
    /// <summary>Gültigkeit einer CRL der Issuing CA (nextUpdate − thisUpdate).</summary>
    public int CrlValidityDays { get; set; } = 7;
    /// <summary>Gültigkeit der Root-CRL (sie kann nur mit dem offline verwahrten Root-Schlüssel erneuert werden).</summary>
    public int RootCrlValidityDays { get; set; } = 365;
    public int CrlRenewBeforeHours { get; set; } = 48;
    public int ExpiryWarningDays { get; set; } = 60;
    /// <summary>Öffentliche Basis-URL der Anwendung (z. B. https://learny.example.org). Sie steht unveränderlich in den CRL-Verteilungspunkten der ausgestellten Zertifikate.</summary>
    public string PublicUrl { get; set; } = "";
    /// <summary>Erststart: Ist die PKI noch nie eingerichtet worden und die Konfiguration vollständig, wird sie einmalig automatisch initialisiert (nie bei späteren Starts, nie über vorhandene Zertifikate).</summary>
    public bool AutoInit { get; set; } = true;
    /// <summary>Passphrase des beim Erststart abgelegten Root-Schlüssels; leer = zufällig erzeugt und einmalig neben der Root-Datei abgelegt.</summary>
    public string RootPassphrase { get; set; } = "";

    /// <summary>Optionaler Vertrauensanker-Pin (SHA-256-Fingerabdrücke der Root CA, kommagetrennt). Gesetzt, gelten nur Roots aus dem Register, die dazu passen — unabhängig von der Datenbank.</summary>
    public List<string> TrustedRootSha256 { get; set; } = new();

    public bool PasswordConfigured => KeyPassword.Length >= MinPasswordLength;

    public static PkiSettings Load(string dataDir)
    {
        string E(string key, string fallback = "") => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v.Trim() : fallback;
        int I(string key, int fallback, int min, int max) => int.TryParse(E(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, min, max) : fallback;
        return new PkiSettings
        {
            KeyDir = E("PKI_KEY_DIR", Path.Combine(dataDir, "pki")),
            KeyPassword = Environment.GetEnvironmentVariable("PKI_KEY_PASSWORD") ?? "",
            KeyAlgorithm = E("PKI_KEY_ALGORITHM", "rsa").ToLowerInvariant(),
            Organization = E("PKI_ORGANIZATION", "Learny"),
            RootValidityYears = I("PKI_ROOT_VALIDITY_YEARS", 20, 2, 40),
            IssuingValidityYears = I("PKI_ISSUING_VALIDITY_YEARS", 10, 1, 30),
            SigningValidityDays = I("PKI_SIGNING_VALIDITY_DAYS", 730, 30, 1825),
            CrlValidityDays = I("PKI_CRL_VALIDITY_DAYS", 7, 1, 30),
            RootCrlValidityDays = I("PKI_ROOT_CRL_VALIDITY_DAYS", 365, 30, 1825),
            CrlRenewBeforeHours = I("PKI_CRL_RENEW_BEFORE_HOURS", 48, 1, 24 * 14),
            ExpiryWarningDays = I("PKI_EXPIRY_WARNING_DAYS", 60, 1, 365),
            PublicUrl = E("PKI_PUBLIC_URL").TrimEnd('/'),
            AutoInit = !string.Equals(E("PKI_AUTO_INIT", "true"), "false", StringComparison.OrdinalIgnoreCase),
            TrustedRootSha256 = E("PKI_TRUSTED_ROOT_SHA256").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Replace(":", "").ToLowerInvariant()).ToList(),
            RootPassphrase = Environment.GetEnvironmentVariable("PKI_ROOT_PASSPHRASE") ?? "",
        };
    }

    /// <summary>Konfigurationsfehler (leer = gültig). Ein gesetztes, aber unzulässiges Passwort ist ein Startfehler; ein fehlendes verhindert nur die Initialisierung bzw. den Betrieb.</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (KeyAlgorithm is not ("rsa" or "ecdsa")) errors.Add($"PKI_KEY_ALGORITHM='{KeyAlgorithm}' ist ungültig (erlaubt: rsa, ecdsa).");
        if (KeyPassword.Length > 0 && KeyPassword.Length < MinPasswordLength) errors.Add($"PKI_KEY_PASSWORD ist zu kurz (mindestens {MinPasswordLength} Zeichen).");
        if (IssuingValidityYears >= RootValidityYears) errors.Add("PKI_ISSUING_VALIDITY_YEARS muss kleiner als PKI_ROOT_VALIDITY_YEARS sein.");
        if (SigningValidityDays > IssuingValidityYears * 365 - 30) errors.Add("PKI_SIGNING_VALIDITY_DAYS muss deutlich kürzer als die Gültigkeit der Issuing CA sein.");
        if (PublicUrl.Length > 0 && !(Uri.TryCreate(PublicUrl, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https") && u.Query.Length == 0 && u.UserInfo.Length == 0)) errors.Add("PKI_PUBLIC_URL muss eine absolute http(s)-URL ohne Zugangsdaten sein.");
        if (RootPassphrase.Length > 0 && RootPassphrase.Length < MinPasswordLength) errors.Add($"PKI_ROOT_PASSPHRASE ist zu kurz (mindestens {MinPasswordLength} Zeichen).");
        if (string.IsNullOrWhiteSpace(Organization) || Organization.Any(char.IsControl)) errors.Add("PKI_ORGANIZATION ist ungültig.");
        return errors;
    }
}
