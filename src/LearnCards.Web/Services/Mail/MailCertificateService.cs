using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services.Mail;

/// <summary>Anzeige des Mail-Zertifikats. Enthält nie den privaten Schlüssel.</summary>
public sealed record MailCertificateInfo(string Thumbprint, string Subject, DateTime NotBefore, DateTime NotAfter, DateTime CreatedAt, bool Usable);

/// <summary>
/// Client-Zertifikat der Mail-App, das <b>auf der Plattform</b> entsteht (ADR 0050): Der private Schlüssel wird hier erzeugt, mit AES-256-GCM verschlüsselt im Schlüsselverzeichnis (<c>MAIL_KEY_DIR</c>,
/// ausserhalb des Webroots) abgelegt und verlässt die Plattform nie; heruntergeladen wird nur das öffentliche Zertifikat (.cer), das in Entra bei der Mail-App hinterlegt wird.
/// Der Dateischlüssel wird per HKDF aus <c>INVITE_TOKEN_KEY</c> abgeleitet (Verlust oder Wechsel dieses Schlüssels macht das Zertifikat unbrauchbar: dann neu erzeugen und die neue .cer hochladen — es wird nie still ein neues erzeugt).
/// Erzeugen, Anzeigen und Herunterladen nur für Administratoren (Rolle gegen die Datenbank). Ein vorhandenes Zertifikat wird nur auf ausdrückliche Bestätigung ersetzt. Die Ausstellung aus der Learny-PKI und die Rotation sind Phase D.
/// </summary>
public sealed class MailCertificateService
{
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("learny-mail-client-cert.v1");
    private readonly UserService _users;
    private readonly string _dir;
    private readonly byte[] _masterKey;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private (long Stamp, X509Certificate2 Cert)? _cache;

    public MailCertificateService(UserService users, AppConfig cfg) : this(users, cfg.Mail.KeyDir, cfg.Invitations.TokenKey) { }

    public MailCertificateService(UserService users, string keyDir, byte[] masterKey, Func<DateTime>? clock = null)
    {
        _users = users; _dir = keyDir; _masterKey = masterKey; _clock = clock ?? (() => DateTime.UtcNow);
    }

    private string PfxPath => Path.Combine(_dir, "mail-client.pfx.enc");
    private byte[]? FileKey() => _masterKey.Length >= 32 ? HKDF.DeriveKey(HashAlgorithmName.SHA256, _masterKey, 32, info: Info) : null;

    public async Task<MailCertificateInfo?> GetInfoAsync(string? actorUserId)
    {
        await _users.RequireRoleAsync(actorUserId, Roles.Admin);
        if (!File.Exists(PfxPath)) return null;
        using var cert = TryLoad();
        return cert is null
            ? new MailCertificateInfo("", "", default, default, File.GetLastWriteTimeUtc(PfxPath), false)
            : new MailCertificateInfo(cert.Thumbprint, cert.Subject, cert.NotBefore.ToUniversalTime(), cert.NotAfter.ToUniversalTime(), File.GetLastWriteTimeUtc(PfxPath), true);
    }

    /// <summary>Nur das öffentliche Zertifikat (DER, .cer) — nie der private Schlüssel.</summary>
    public async Task<byte[]?> GetPublicCertificateAsync(string? actorUserId)
    {
        await _users.RequireRoleAsync(actorUserId, Roles.Admin);
        using var cert = TryLoad();
        return cert?.Export(X509ContentType.Cert);
    }

    /// <summary>Erzeugt ein neues selbstsigniertes Zertifikat (RSA 3072, 24 Monate). Ein vorhandenes wird nur mit <paramref name="replaceExisting"/> ersetzt; der alte Schlüssel ist danach verloren.</summary>
    public async Task<MailCertificateInfo> GenerateAsync(string? actorUserId, bool replaceExisting = false)
    {
        var actor = await _users.RequireRoleAsync(actorUserId, Roles.Admin);
        var key = FileKey() ?? throw new InvalidOperationException("INVITE_TOKEN_KEY fehlt: ohne ihn kann der private Schlüssel nicht geschützt abgelegt werden.");
        if (File.Exists(PfxPath) && !replaceExisting)
            throw new InvalidOperationException("Es existiert bereits ein Zertifikat. Ersetzen muss ausdrücklich bestätigt werden (der bisherige private Schlüssel geht verloren).");

        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest("CN=Learny-Mail-Client", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var now = _clock();
        using var cert = req.CreateSelfSigned(new DateTimeOffset(now.AddMinutes(-5), TimeSpan.Zero), new DateTimeOffset(now.AddMonths(24), TimeSpan.Zero));
        var pfx = cert.Export(X509ContentType.Pkcs12, "");
        try
        {
            Directory.CreateDirectory(_dir);
            var tmp = PfxPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tmp, Encrypt(pfx, key));
            File.Move(tmp, PfxPath, true);
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        lock (_gate) { _cache = null; }
        await _users.WriteAuditAsync(actor.Id, "mail.certificate.create", "", $"thumbprint={cert.Thumbprint} replaced={(replaceExisting ? 1 : 0)}");   // Thumbprint ist öffentlich; nie Schlüssel
        return new MailCertificateInfo(cert.Thumbprint, cert.Subject, cert.NotBefore.ToUniversalTime(), cert.NotAfter.ToUniversalTime(), now, true);
    }

    /// <summary>Zertifikat mit privatem Schlüssel für den Versand (nur im Speicher) oder null, wenn keines vorhanden/entschlüsselbar ist.</summary>
    public X509Certificate2? GetSendingCertificate()
    {
        lock (_gate)
        {
            if (!File.Exists(PfxPath)) { _cache = null; return null; }
            var fi = new FileInfo(PfxPath); var stamp = fi.LastWriteTimeUtc.Ticks ^ fi.Length;
            if (_cache is { } c && c.Stamp == stamp) return c.Cert;
            var cert = TryLoad();
            _cache = cert is null ? null : (stamp, cert);
            return cert;
        }
    }

    private X509Certificate2? TryLoad()
    {
        try
        {
            var key = FileKey(); if (key is null || !File.Exists(PfxPath)) return null;
            var plain = Decrypt(File.ReadAllBytes(PfxPath), key);
            try { return X509CertificateLoader.LoadPkcs12(plain, "", X509KeyStorageFlags.EphemeralKeySet); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception e) when (e is CryptographicException or IOException) { return null; }   // anderer Schlüssel oder beschädigt: nie still ersetzen
    }

    private static byte[] Encrypt(byte[] plain, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[plain.Length];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, Info);
        return nonce.Concat(tag).Concat(cipher).ToArray();
    }

    private static byte[] Decrypt(byte[] blob, byte[] key)
    {
        if (blob.Length < 28) throw new CryptographicException();
        var plain = new byte[blob.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, Info);
        return plain;
    }
}

/// <summary>Wirksame Empfängerliste und ihre Quelle (<c>datenbank</c> = in der Plattform gepflegt, <c>umgebung</c> = MAIL_ALLOWED_RECIPIENTS).</summary>
public sealed record MailRecipientsInfo(IReadOnlyList<string> Recipients, string Source, DateTime? UpdatedAt);

/// <summary>
/// Erlaubte Empfänger der Einladungs- und Schulungsmails (nicht der Anmeldecodes), von Administratoren in der Plattform änderbar und dauerhaft in der Datenbank gespeichert (Tabelle <c>mail_settings</c>, ADR 0050).
/// Ein gespeicherter Wert überschreibt <c>MAIL_ALLOWED_RECIPIENTS</c>; „zurücksetzen“ entfernt ihn (dann gilt wieder die Umgebung). Änderungen wirken ohne Neustart (kurzer Zwischenspeicher, auch bei mehreren Instanzen).
/// Leer = niemand (fail-closed); <c>*</c> = jede Adresse. Im Audit stehen nur Anzahl und Platzhalter-Flag, nie Adressen.
/// </summary>
public sealed class MailRecipientsService
{
    private readonly Data.IDatabase _db;
    private readonly UserService _users;
    private readonly ExchangeMailSettings _env;
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _ttl;
    private (DateTime At, MailRecipientsInfo Info)? _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MailRecipientsService(Data.IDatabase db, UserService users, AppConfig cfg) : this(db, users, cfg.Mail) { }
    public MailRecipientsService(Data.IDatabase db, UserService users, ExchangeMailSettings env, Func<DateTime>? clock = null, TimeSpan? ttl = null)
    { _db = db; _users = users; _env = env; _clock = clock ?? (() => DateTime.UtcNow); _ttl = ttl ?? TimeSpan.FromSeconds(15); }

    /// <summary>Wirksame Liste für den Versand (ohne Rollenprüfung, nur intern).</summary>
    public async Task<MailRecipientsInfo> GetEffectiveAsync(CancellationToken ct = default)
    {
        if (_cache is { } c && _clock() - c.At < _ttl) return c.Info;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is { } c2 && _clock() - c2.At < _ttl) return c2.Info;
            var rows = await _db.QueryAsync("SELECT allowed_recipients, updated_at FROM mail_settings WHERE id=1", null, ct);
            MailRecipientsInfo info;
            try
            {
                info = rows.Count == 0
                    ? new MailRecipientsInfo(_env.AllowedRecipients, "umgebung", null)
                    : new MailRecipientsInfo(ExchangeMailSettings.ParseRecipients(rows[0]["allowed_recipients"]), "datenbank", Data.DbValue.ToDateTime(rows[0]["updated_at"]));
            }
            catch (ArgumentException) { info = new MailRecipientsInfo(Array.Empty<string>(), "datenbank", null); }   // beschädigter Eintrag: niemand (fail-closed)
            _cache = (_clock(), info);
            return info;
        }
        finally { _gate.Release(); }
    }

    public async Task<MailRecipientsInfo> GetAsync(string? actorUserId)
    {
        await _users.RequireRoleAsync(actorUserId, Roles.Admin);
        _cache = null;
        return await GetEffectiveAsync();
    }

    /// <summary>Speichert die Liste dauerhaft (nur Administratoren). Ungültige Eingaben ändern nichts (<see cref="ArgumentException"/>).</summary>
    public async Task<MailRecipientsInfo> SaveAsync(string? actorUserId, string? input)
    {
        var actor = await _users.RequireRoleAsync(actorUserId, Roles.Admin);
        var list = ExchangeMailSettings.ParseRecipients(input);
        var now = _clock();
        await _db.ExecuteAsync("""
            INSERT INTO mail_settings (id, allowed_recipients, updated_at, updated_by) VALUES (1, @r, @now, @by)
            ON CONFLICT (id) DO UPDATE SET allowed_recipients = @r, updated_at = @now, updated_by = @by
            """, new (string, object?)[] { ("r", string.Join(",", list)), ("now", now), ("by", actor.Id) });
        _cache = null;
        await _users.WriteAuditAsync(actor.Id, "mail.recipients.update", "", $"count={list.Count} wildcard={(list.Contains("*") ? 1 : 0)}");
        return await GetEffectiveAsync();
    }

    /// <summary>Entfernt den gespeicherten Wert: danach gilt wieder <c>MAIL_ALLOWED_RECIPIENTS</c> aus der Umgebung.</summary>
    public async Task<MailRecipientsInfo> ResetAsync(string? actorUserId)
    {
        var actor = await _users.RequireRoleAsync(actorUserId, Roles.Admin);
        await _db.ExecuteAsync("DELETE FROM mail_settings WHERE id=1");
        _cache = null;
        await _users.WriteAuditAsync(actor.Id, "mail.recipients.reset", "", "");
        return await GetEffectiveAsync();
    }
}

/// <summary>
/// Mailtransport mit dem passenden Zertifikat und der wirksamen Empfängerliste: Datei/Secret aus der Konfiguration (Betreiberentscheidung) oder das auf der Plattform erzeugte Zertifikat. Beides wird bei
/// jedem Aufruf neu aufgelöst, damit ein neu erzeugtes Zertifikat oder eine geänderte Empfängerliste ohne Neustart wirkt. Ohne Zertifikat wird nichts gesendet (<c>mail_certificate_missing</c>); ohne <c>MAIL_ENABLED</c> sowieso nicht.
/// </summary>
public sealed class PlatformMailer : IInvitationMailer, IOtpMailer, ITrainingMailer
{
    private readonly ExchangeMailSettings _s;
    private readonly MailCertificateService _certs;
    private readonly MailRecipientsService _recipients;
    private readonly Func<HttpClient> _http;
    private (string Key, ExchangeOnlineMailService Service)? _current;
    private readonly object _gate = new();

    public PlatformMailer(ExchangeMailSettings settings, MailCertificateService certs, MailRecipientsService recipients, Func<HttpClient> http)
    { _s = settings; _certs = certs; _recipients = recipients; _http = http; }

    private async Task<ExchangeOnlineMailService?> ResolveAsync(CancellationToken ct)
    {
        var rec = await _recipients.GetEffectiveAsync(ct);
        lock (_gate)
        {
            if (!_s.Enabled) return Cached("disabled", () => _s.ToOptions());
            if (_s.UsesPlatformCertificate)
            {
                var cert = _certs.GetSendingCertificate();
                if (cert is null) { _current = null; return null; }
                return Cached(cert.Thumbprint + "|" + string.Join(",", rec.Recipients), () => _s.ToOptions(cert, rec.Recipients));
            }
            return Cached("file|" + string.Join(",", rec.Recipients), () => _s.ToOptions(null, rec.Recipients));
        }
    }

    private ExchangeOnlineMailService Cached(string key, Func<ExchangeMailOptions> options)
    {
        if (_current is { } c && c.Key == key) return c.Service;
        var svc = new ExchangeOnlineMailService(_http(), options());
        _current = (key, svc);
        return svc;
    }

    private static readonly MailSendResult Missing = new(MailOutcome.Rejected, "mail_certificate_missing");

    public async Task<MailSendResult> SendAsync(InvitationMail mail, CancellationToken ct = default) => await ResolveAsync(ct) is { } s ? await s.SendAsync(mail, ct) : Missing;
    public async Task<MailSendResult> SendOtpAsync(OtpMail mail, CancellationToken ct = default) => await ResolveAsync(ct) is { } s ? await s.SendOtpAsync(mail, ct) : Missing;
    public async Task<MailSendResult> SendTrainingAsync(TrainingMail mail, CancellationToken ct = default) => await ResolveAsync(ct) is { } s ? await s.SendTrainingAsync(mail, ct) : Missing;
}
