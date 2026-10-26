using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services.Pki;

/// <summary>
/// Schlüsselspeicher der Learny-PKI (ADR 0035): private Schlüssel liegen ausschließlich als AES-256-verschlüsselte PKCS#12-Dateien (PBES2/PBKDF2-SHA256) im
/// Schlüsselverzeichnis <c>PKI_KEY_DIR</c> — nie in der Datenbank, nie im Webroot, nie in Logs. Auf Linux gilt Modus 0600/0700, auf Windows eine ACL nur für den
/// aktuellen Dienstkonto-Benutzer, SYSTEM und Administratoren. Das Passwort stammt aus <c>PKI_KEY_PASSWORD</c>; ist keines gesetzt, wird beim Erststart einmalig
/// ein zufälliges erzeugt und (wie beim Badge-Schlüssel) mit restriktiven Rechten neben den Schlüsseln abgelegt — für Produktion in die Secret-Verwaltung übernehmen.
/// </summary>
public sealed class PkiKeyStore
{
    public const string IssuingFile = "issuing-ca.p12";
    public const string RootDir = "root-offline";
    public const string RootFile = "root-ca.p12";
    /// <summary>PBKDF2-Iterationen (HMAC-SHA-256) für Schlüsseldatei und MAC; die Ladegrenzen werden bewusst darüber festgelegt (die .NET-Standardgrenze ist niedriger).</summary>
    private const int Iterations = 400_000;
    private static readonly Pkcs12LoaderLimits Limits = new(Pkcs12LoaderLimits.Defaults) { MacIterationLimit = 1_000_000, IndividualKdfIterationLimit = 1_000_000, TotalKdfIterationLimit = 4_000_000 };
    private const string PasswordFile = "pki-key.pass";
    private const string RootPassFile = "ROOT-PASSPHRASE.txt";

    private readonly PkiSettings _cfg;
    public PkiKeyStore(AppConfig config) { _cfg = config.Pki; }

    public string Dir => _cfg.KeyDir;
    public string PathOf(string name) => Path.Combine(_cfg.KeyDir, name);
    public string RootPath => Path.Combine(_cfg.KeyDir, RootDir, RootFile);
    public bool RootKeyOnServer => File.Exists(RootPath);
    public bool Exists(string name) => File.Exists(PathOf(name));

    /// <summary>Dateiname der Signaturschlüssel: <c>signing-{certificateId}.p12</c> (nur die ID, kein Schlüsselmaterial).</summary>
    public static string SigningFile(string certificateId) => $"signing-{certificateId}.p12";

    /// <summary>Alle Schlüsseldateien (außer Passwortdatei) — dient der Erkennung von Altlasten vor einer Initialisierung.</summary>
    public List<string> KeyFiles()
    {
        if (!Directory.Exists(Dir)) return new();
        return Directory.EnumerateFiles(Dir, "*.p12", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Dir, f)).ToList();
    }

    public string? Password
    {
        get
        {
            if (_cfg.KeyPassword.Length > 0) return _cfg.KeyPassword;
            try { var f = PathOf(PasswordFile); return File.Exists(f) ? File.ReadAllText(f).Trim() : null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>Passphrase des Root-Schlüssels, solange er auf dem Server liegt: PKI_ROOT_PASSPHRASE oder die einmalig notierte Datei.</summary>
    public string? ReadRootPassphrase()
    {
        if (_cfg.RootPassphrase.Length > 0) return _cfg.RootPassphrase;
        try
        {
            var f = Path.Combine(Dir, RootDir, RootPassFile);
            return File.Exists(f) ? File.ReadAllLines(f).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public bool PasswordFromFile => _cfg.KeyPassword.Length == 0 && File.Exists(PathOf(PasswordFile));

    /// <summary>Stellt ein Schlüsselpasswort sicher. Gibt zurück, ob es neu erzeugt wurde. Ein bereits vorhandenes wird nie überschrieben.</summary>
    public bool EnsurePassword()
    {
        if (Password is not null) return false;
        EnsureDir(Dir);
        WriteSecret(PathOf(PasswordFile), Convert.ToBase64String(RandomNumberGenerator.GetBytes(36)));
        return true;
    }

    public void Save(string name, X509Certificate2 certWithKey, string password, IEnumerable<X509Certificate2>? chain = null)
    {
        var path = PathOf(name);
        EnsureDir(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) throw new IOException("Die Schlüsseldatei existiert bereits und wird nie überschrieben.");
        var pfx = ExportEncrypted(certWithKey, password);
        WriteBytes(path, pfx);
    }

    public static byte[] ExportEncrypted(X509Certificate2 certWithKey, string password) =>
        certWithKey.ExportPkcs12(new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, Iterations), password);

    public X509Certificate2 Load(string name, string? password = null)
    {
        var pw = password ?? Password ?? throw new CryptographicException("Kein Schlüsselpasswort verfügbar.");
        return X509CertificateLoader.LoadPkcs12FromFile(PathOf(name), pw, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable, Limits);
    }

    public static X509Certificate2 LoadBytes(byte[] pfx, string password) =>
        X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable, Limits);

    /// <summary>Beim Erststart: Root-Schlüssel verschlüsselt ablegen (zur Auslagerung offline); ohne gesetzte Passphrase wird eine zufällige einmalig daneben notiert.</summary>
    public string SaveRoot(X509Certificate2 rootWithKey, out bool passphraseFileWritten)
    {
        var pass = _cfg.RootPassphrase;
        passphraseFileWritten = false;
        if (pass.Length == 0)
        {
            pass = Convert.ToBase64String(RandomNumberGenerator.GetBytes(30));
            EnsureDir(Path.GetDirectoryName(RootPath)!);
            WriteSecret(Path.Combine(Dir, RootDir, RootPassFile), "Passphrase der Datei root-ca.p12 — Datei und Passphrase offline sichern und vom Server entfernen (Administration → PKI).\n" + pass + "\n");
            passphraseFileWritten = true;
        }
        Save(Path.Combine(RootDir, RootFile), rootWithKey, pass);
        return pass;
    }

    public void RemoveRoot()
    {
        var dir = Path.Combine(Dir, RootDir);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    public void Delete(string name)
    {
        try { var p = PathOf(name); if (File.Exists(p)) File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ─── Dateirechte ───────────────────────────────────────────────────────

    private static void EnsureDir(string dir)
    {
        if (!Directory.Exists(dir))
        {
            if (!OperatingSystem.IsWindows()) Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else Directory.CreateDirectory(dir);
        }
        RestrictDir(dir);
    }

    private static void WriteBytes(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        if (OperatingSystem.IsWindows()) { File.WriteAllBytes(tmp, data); RestrictFile(tmp); }
        else
        {
            using (var fs = new FileStream(tmp, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
                fs.Write(data);
        }
        File.Move(tmp, path);
    }

    private static void WriteSecret(string path, string text) => WriteBytes(path, System.Text.Encoding.UTF8.GetBytes(text));

    private static void RestrictDir(string dir)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new DirectoryInfo(dir);
                var sec = info.GetAccessControl();
                sec.SetAccessRuleProtection(true, false);
                foreach (var r in sec.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList()) sec.RemoveAccessRuleAll(r);
                foreach (var sid in Principals())
                    sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(sec);
            }
            else File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException) { /* Rechte nicht änderbar (z. B. gemountetes Volume): Betreiber muss sie extern setzen, siehe docs/pki.md */ }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var sec = info.GetAccessControl();
            sec.SetAccessRuleProtection(true, false);
            foreach (var r in sec.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList()) sec.RemoveAccessRuleAll(r);
            foreach (var sid in Principals()) sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(sec);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException) { }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<SecurityIdentifier> Principals()
    {
        yield return WindowsIdentity.GetCurrent().User!;
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    }
}
