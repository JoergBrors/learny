using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LearnCards.Web.Services.Pdf;

namespace LearnCards.Web.Infrastructure;

/// <summary>
/// Konfiguration der PDF-Signatur (ADR 0033). Das Signaturzertifikat ist <b>unabhängig</b> vom Badge-Schlüssel (ES256, ADR 0021): PDF-Signaturen brauchen ein X.509-Zertifikat.
/// Quellen (Vorrang in dieser Reihenfolge): <c>PDF_SIGNING_CERT_PFX_BASE64</c> (PKCS#12, Secret-Verwaltung), <c>PDF_SIGNING_CERT_FILE</c> (PKCS#12-Datei oder PEM mit Zertifikat[en]),
/// <c>PDF_SIGNING_CERT_PEM</c> + <c>PDF_SIGNING_KEY_PEM</c> (Text oder Pfad). Privater Schlüssel nie im Repository, nie in Logs oder PDFs.
/// </summary>
public class PdfSigningSettings
{
    public string CertPfxBase64 { get; set; } = "";
    public string CertFile { get; set; } = "";
    public string CertPassword { get; set; } = "";
    public string CertPem { get; set; } = "";
    public string KeyPem { get; set; } = "";
    public string Name { get; set; } = "Learny Verified";
    public string Reason { get; set; } = "Learny Verified – Digitally Signed Credential";
    public string Location { get; set; } = "";
    public string Contact { get; set; } = "";
    /// <summary>Nur Entwicklung/Test: ohne Konfiguration ein selbstsigniertes Zertifikat im Datenverzeichnis erzeugen (nicht vertrauenswürdig, in jeder Anzeige als solches erkennbar).</summary>
    public bool DevCert { get; set; }
    public string DevCertFile { get; set; } = "";

    public bool Configured => CertPfxBase64.Length > 0 || CertFile.Length > 0 || CertPem.Length > 0;

    public static PdfSigningSettings Load(string dataDir, bool isDevelopment)
    {
        string E(string key, string fallback = "") => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v.Trim() : fallback;
        return new PdfSigningSettings
        {
            CertPfxBase64 = E("PDF_SIGNING_CERT_PFX_BASE64"), CertFile = E("PDF_SIGNING_CERT_FILE"), CertPassword = Environment.GetEnvironmentVariable("PDF_SIGNING_CERT_PASSWORD") ?? "",
            CertPem = E("PDF_SIGNING_CERT_PEM"), KeyPem = E("PDF_SIGNING_KEY_PEM"),
            Name = E("PDF_SIGNING_NAME", "Learny Verified"), Reason = E("PDF_SIGNING_REASON", "Learny Verified – Digitally Signed Credential"),
            Location = E("PDF_SIGNING_LOCATION"), Contact = E("PDF_SIGNING_CONTACT"),
            DevCert = E("PDF_SIGNING_DEV_CERT") is { Length: > 0 } d ? d.Equals("true", StringComparison.OrdinalIgnoreCase) || d == "1" : isDevelopment,
            DevCertFile = Path.Combine(dataDir, "pdf-signing-dev.pfx"),
        };
    }
}

/// <summary>Das PDF-Signaturzertifikat ist nicht konfiguriert oder unbrauchbar — PDFs werden dann nicht (unsigniert) ausgeliefert.</summary>
public sealed class PdfSigningUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// Lädt das PDF-Signaturzertifikat aus der Konfiguration (einmalig, beim Start geprüft, wenn konfiguriert). Ohne Konfiguration und ohne Entwicklungszertifikat gibt es
/// bewusst keine signierten PDFs (fail-closed, ADR 0026): <see cref="GetOptions"/> wirft <see cref="PdfSigningUnavailableException"/>.
/// Voraussetzung für ein <i>vertrauenswürdiges</i> Ergebnis ist ein Zertifikat einer anerkannten Zertifizierungsstelle (z. B. AATL/eIDAS); ein selbst ausgestelltes
/// Zertifikat liefert eine kryptografisch gültige, aber nur für den Aussteller selbst bestätigbare Signatur.
/// </summary>
public sealed class PdfSigningProvider
{
    private readonly PdfSigningSettings _cfg;
    private readonly object _gate = new();
    private X509Certificate2? _cert;
    private List<X509Certificate2> _chain = new();
    private string? _error;
    private bool _loaded;

    public PdfSigningProvider(AppConfig config) { _cfg = config.PdfSigning; }

    public bool Configured => _cfg.Configured || _cfg.DevCert;
    public bool IsDevCertificate { get; private set; }

    /// <summary>Wird beim Start aufgerufen: eine konfigurierte, aber unbrauchbare Signatur ist ein Konfigurationsfehler (Startabbruch), keine stille Deaktivierung.</summary>
    public void EnsureValidAtStartup()
    {
        if (!_cfg.Configured) return;
        Load();
        if (_cert is null) throw new InvalidOperationException(_error ?? "Das PDF-Signaturzertifikat konnte nicht geladen werden.");
    }

    public string? Thumbprint { get { Load(); return _cert?.Thumbprint; } }
    public X509Certificate2? Certificate { get { Load(); return _cert; } }

    public PdfSignOptions GetOptions(DateTime? signingTime = null)
    {
        Load();
        if (_cert is null) throw new PdfSigningUnavailableException(_error ?? "Das PDF-Signaturzertifikat ist nicht konfiguriert (PDF_SIGNING_CERT_FILE bzw. PDF_SIGNING_CERT_PFX_BASE64; siehe docs/pdf-signature.md).");
        return new PdfSignOptions { Certificate = _cert, Chain = _chain, Name = _cfg.Name, Reason = _cfg.Reason, Location = _cfg.Location, ContactInfo = _cfg.Contact, SigningTime = signingTime ?? DateTime.UtcNow };
    }

    private void Load()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            try
            {
                if (_cfg.Configured) { (_cert, _chain) = LoadConfigured(_cfg); }
                else if (_cfg.DevCert) { _cert = LoadOrCreateDev(_cfg.DevCertFile); _chain = new(); IsDevCertificate = true; }
                else _error = "Das PDF-Signaturzertifikat ist nicht konfiguriert (PDF_SIGNING_CERT_FILE bzw. PDF_SIGNING_CERT_PFX_BASE64; siehe docs/pdf-signature.md).";
                if (_cert is not null)
                {
                    var now = DateTime.UtcNow;
                    if (_cert.NotAfter.ToUniversalTime() < now) { _error = $"Das PDF-Signaturzertifikat ist abgelaufen ({_cert.NotAfter:yyyy-MM-dd})."; _cert = null; }
                    else if (_cert.NotBefore.ToUniversalTime() > now.AddMinutes(5)) { _error = "Das PDF-Signaturzertifikat ist noch nicht gültig."; _cert = null; }
                    else if (!_cert.HasPrivateKey) { _error = "Zum PDF-Signaturzertifikat fehlt der private Schlüssel."; _cert = null; }
                    else if (_cert.GetRSAPublicKey() is null && _cert.GetECDsaPublicKey() is null) { _error = "Das PDF-Signaturzertifikat muss einen RSA- oder ECDSA-Schlüssel haben."; _cert = null; }
                }
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or FormatException or UnauthorizedAccessException or ArgumentException)
            {
                // nur die Art des Fehlers nennen — nie Pfade zu Secrets, Passwörter oder Schlüsselinhalt
                _error = $"Das PDF-Signaturzertifikat konnte nicht geladen werden ({ex.GetType().Name}). Prüfe Format (PKCS#12 bzw. PEM), Passwort und Leserechte.";
                _cert = null;
            }
            _loaded = true;
        }
    }

    private static (X509Certificate2 Cert, List<X509Certificate2> Chain) LoadConfigured(PdfSigningSettings c)
    {
        const X509KeyStorageFlags flags = X509KeyStorageFlags.EphemeralKeySet;
        string Pem(string valueOrPath) => valueOrPath.Contains("-----BEGIN") ? valueOrPath : File.ReadAllText(valueOrPath);
        var password = c.CertPassword.Length > 0 ? c.CertPassword : null;
        if (c.CertPfxBase64.Length > 0) return Split(X509CertificateLoader.LoadPkcs12Collection(Convert.FromBase64String(c.CertPfxBase64), password, flags));
        if (c.CertFile.Length > 0)
        {
            var bytes = File.ReadAllBytes(c.CertFile);
            if (!Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 64)).Contains("-----BEGIN")) return Split(X509CertificateLoader.LoadPkcs12Collection(bytes, password, flags));
            return FromPem(Encoding.UTF8.GetString(bytes), c.KeyPem.Length > 0 ? Pem(c.KeyPem) : Encoding.UTF8.GetString(bytes), password);
        }
        return FromPem(Pem(c.CertPem), Pem(c.KeyPem), password);
    }

    private static (X509Certificate2, List<X509Certificate2>) Split(X509Certificate2Collection all)
    {
        var withKey = all.Cast<X509Certificate2>().FirstOrDefault(x => x.HasPrivateKey) ?? throw new CryptographicException("Kein Zertifikat mit privatem Schlüssel gefunden.");
        return (withKey, all.Cast<X509Certificate2>().Where(x => x.Thumbprint != withKey.Thumbprint).ToList());
    }

    private static (X509Certificate2, List<X509Certificate2>) FromPem(string certPem, string keyPem, string? password)
    {
        var certs = new List<X509Certificate2>();
        var span = certPem.AsSpan();
        while (PemEncoding.TryFind(span, out var f))
        {
            if (span[f.Label].SequenceEqual("CERTIFICATE")) certs.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(span[f.Base64Data].ToString())));
            span = span[f.Location.End..];
        }
        if (certs.Count == 0) throw new CryptographicException("Kein Zertifikat im PEM gefunden.");
        var leaf = certs[0];
        using var tmp = password is null ? X509Certificate2.CreateFromPem(leaf.ExportCertificatePem(), keyPem) : X509Certificate2.CreateFromEncryptedPem(leaf.ExportCertificatePem(), keyPem, password);
        // Schlüssel flüchtig halten (kein Schlüsselspeicher-Eintrag): über PKCS#12 neu laden
        var withKey = X509CertificateLoader.LoadPkcs12(tmp.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.EphemeralKeySet);
        return (withKey, certs.Skip(1).ToList());
    }

    private static X509Certificate2 LoadOrCreateDev(string file)
    {
        if (File.Exists(file)) return X509CertificateLoader.LoadPkcs12FromFile(file, null, X509KeyStorageFlags.EphemeralKeySet);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=Learny Entwicklungs-Signatur (nicht vertrauenswürdig), O=LearnCards", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(3));
        var pfx = self.Export(X509ContentType.Pkcs12);
        try { using var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None); fs.Write(pfx); }
        catch (IOException) { /* paralleler Start war schneller */ }
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet);
    }
}
