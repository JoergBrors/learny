using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LearnCards.Web.Data;

namespace InviteRegistration;

/// <summary>Voreinstellungen aus der Umgebung (MAIL_TENANT_ID, MAIL_CLIENT_ID, MAIL_SENDER): gelten, solange nichts gespeichert wurde.</summary>
public sealed record MailDefaults(string TenantId = "", string ClientId = "", string Sender = "learny@brors.link", string AppName = "Learny-PoC-Mail");

/// <summary>Anzeige der Mail-Einrichtung. Enthält nie den privaten Schlüssel.</summary>
public sealed record MailSettingsView(string TenantId, string ClientId, string Sender, IReadOnlyList<string> AllowedRecipients, bool Enabled, bool IsPreselected,
    string? CertThumbprint, string? CertSubject, DateTime? CertNotAfter, DateTime? CertCreatedAt, bool CertUsable);

/// <summary>
/// Mail-Einrichtung im Portal: App-Registrierung (Tenant, Client-ID, Absender, Empfänger), Client-Zertifikat und Aktivierung.
/// Der private Schlüssel entsteht im Portal, wird mit AES-256-GCM (Schlüssel per HKDF aus dem Token-Schlüssel) verschlüsselt im Datenverzeichnis abgelegt
/// und verlässt es nie; heruntergeladen wird nur das öffentliche Zertifikat. Ohne Zertifikat oder ohne ausdrückliche Aktivierung wird nichts gesendet (ADR 0026).
/// </summary>
public sealed class MailSettingsService
{
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("learny-poc-mail-cert.v1");
    private readonly IDatabase _db; private readonly string _dir; private readonly byte[] _key; private readonly MailDefaults _defaults;
    private readonly IReadOnlyCollection<string> _forbidden; private readonly Func<DateTime> _clock;
    /// <summary>Ereignisprotokoll (vom Portal gesetzt): Änderungen werden ohne Schlüssel und Freitext protokolliert.</summary>
    public InviteEvents? Events { get; set; }

    public MailSettingsService(IDatabase db, string certDir, byte[] tokenKey, MailDefaults defaults, IReadOnlyCollection<string>? forbiddenTenantIds = null, Func<DateTime>? clock = null)
    {
        if (tokenKey.Length < 32) throw new ArgumentException("Schlüssel zu kurz.");
        _db = db; _dir = certDir; _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, tokenKey, 32, info: Info); _defaults = defaults;
        _forbidden = forbiddenTenantIds ?? Array.Empty<string>(); _clock = clock ?? (() => DateTime.UtcNow);
    }

    private string PfxPath => Path.Combine(_dir, "mail-client.pfx.enc");

    public async Task InitAsync()
    {
        await _db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS mail_settings (
                id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, client_id TEXT NOT NULL, sender TEXT NOT NULL, allowed_recipients TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 0, cert_thumbprint TEXT NULL, cert_subject TEXT NULL, cert_not_after TEXT NULL, cert_created_at TEXT NULL,
                updated_at TEXT NOT NULL, updated_by TEXT NULL
            )
            """);
    }

    public async Task<MailSettingsView> GetAsync(CancellationToken ct = default)
    {
        var rows = await _db.QueryAsync("SELECT * FROM mail_settings WHERE id=1", Array.Empty<(string, object?)>(), ct);
        if (rows.Count == 0)
            return new MailSettingsView(_defaults.TenantId, _defaults.ClientId, _defaults.Sender, Array.Empty<string>(), false, true, null, null, null, null, false);
        var r = rows[0];
        var usable = r["cert_thumbprint"] is not null && File.Exists(PfxPath);
        return new MailSettingsView(r["tenant_id"]!, r["client_id"]!, r["sender"]!, Split(r["allowed_recipients"]!), DbValue.ToInt(r["enabled"]) == 1, false,
            r["cert_thumbprint"], r["cert_subject"], r["cert_not_after"] is null ? null : DbValue.ToDateTime(r["cert_not_after"]), r["cert_created_at"] is null ? null : DbValue.ToDateTime(r["cert_created_at"]), usable);
    }

    /// <summary>Speichert App-Registrierung und Aktivierung. Gibt false bei ungültiger Eingabe zurück (nichts wird geändert).</summary>
    public async Task<bool> SaveAsync(string actor, string tenantId, string clientId, string sender, string allowedRecipients, bool enabled, CancellationToken ct = default)
    {
        tenantId = tenantId.Trim(); clientId = clientId.Trim();
        if (!Guid.TryParse(tenantId, out var t) || !Guid.TryParse(clientId, out var c)) return false;
        tenantId = t.ToString(); clientId = c.ToString();
        if (_forbidden.Any(f => string.Equals(f, tenantId, StringComparison.OrdinalIgnoreCase))) return false;   // nie der External-ID-Tenant
        var mailbox = InvitationService.NormalizeEmail(sender);
        if (mailbox is null) return false;
        var list = new List<string>();
        foreach (var raw in allowedRecipients.Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = raw.StartsWith('@') ? (raw.Length > 3 && raw.IndexOf('.') > 1 && !raw[1..].Contains('@') ? raw.ToLowerInvariant() : null) : InvitationService.NormalizeEmail(raw);
            if (entry is null) return false;
            list.Add(entry);
        }
        var current = await GetAsync(ct);
        if (enabled && (!current.CertUsable || list.Count == 0)) return false;   // Aktivieren braucht Zertifikat und mindestens einen erlaubten Empfänger

        var now = _clock();
        var args = new (string, object?)[] { ("t", tenantId), ("c", clientId), ("s", mailbox), ("a", string.Join(",", list)), ("e", enabled ? 1 : 0), ("now", now), ("by", actor) };
        if ((await _db.QueryAsync("SELECT 1 AS x FROM mail_settings WHERE id=1", Array.Empty<(string, object?)>(), ct)).Count == 0)
            await _db.ExecuteAsync("INSERT INTO mail_settings (id, tenant_id, client_id, sender, allowed_recipients, enabled, updated_at, updated_by) VALUES (1,@t,@c,@s,@a,@e,@now,@by)", args, ct);
        else
            await _db.ExecuteAsync("UPDATE mail_settings SET tenant_id=@t, client_id=@c, sender=@s, allowed_recipients=@a, enabled=@e, updated_at=@now, updated_by=@by WHERE id=1", args, ct);
        if (Events is not null) await Events.LogAsync(EventTypes.MailSettingsChanged, null, null, enabled ? "enabled" : "disabled", actor, ct);
        return true;
    }

    /// <summary>Erzeugt ein neues selbstsigniertes Client-Zertifikat (RSA 3072, 24 Monate). Ein vorhandenes wird ersetzt und der Versand deaktiviert, bis das neue in Entra hinterlegt ist.</summary>
    public async Task<MailSettingsView> GenerateCertificateAsync(string actor, CancellationToken ct = default)
    {
        var settings = await GetAsync(ct);
        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest("CN=" + _defaults.AppName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var now = _clock();
        using var cert = req.CreateSelfSigned(new DateTimeOffset(now.AddMinutes(-5), TimeSpan.Zero), new DateTimeOffset(now.AddMonths(24), TimeSpan.Zero));
        var pfx = cert.Export(X509ContentType.Pkcs12, "");
        Directory.CreateDirectory(_dir);
        var tmp = PfxPath + ".tmp";
        await File.WriteAllBytesAsync(tmp, Encrypt(pfx), ct);
        File.Move(tmp, PfxPath, true);
        CryptographicOperations.ZeroMemory(pfx);

        var args = new (string, object?)[] { ("th", cert.Thumbprint), ("sj", cert.Subject), ("na", cert.NotAfter.ToUniversalTime()), ("now", now), ("by", actor),
            ("t", settings.TenantId), ("c", settings.ClientId), ("s", settings.Sender), ("a", string.Join(",", settings.AllowedRecipients)) };
        if ((await _db.QueryAsync("SELECT 1 AS x FROM mail_settings WHERE id=1", Array.Empty<(string, object?)>(), ct)).Count == 0)
            await _db.ExecuteAsync("INSERT INTO mail_settings (id, tenant_id, client_id, sender, allowed_recipients, enabled, cert_thumbprint, cert_subject, cert_not_after, cert_created_at, updated_at, updated_by) VALUES (1,@t,@c,@s,@a,0,@th,@sj,@na,@now,@now,@by)", args, ct);
        else
            await _db.ExecuteAsync("UPDATE mail_settings SET enabled=0, cert_thumbprint=@th, cert_subject=@sj, cert_not_after=@na, cert_created_at=@now, updated_at=@now, updated_by=@by WHERE id=1", args, ct);
        if (Events is not null) await Events.LogAsync(EventTypes.MailCertificateCreated, null, null, null, actor, ct);
        return await GetAsync(ct);
    }

    /// <summary>Nur das öffentliche Zertifikat (DER, .cer). Nie der private Schlüssel.</summary>
    public async Task<byte[]?> GetPublicCertificateAsync(CancellationToken ct = default)
    {
        using var cert = LoadCertificate(await GetAsync(ct));
        return cert?.Export(X509ContentType.Cert);
    }

    /// <summary>Optionen für den Versand oder null, wenn nicht aktiviert/unvollständig. Das Zertifikat bleibt im Speicher (ephemer).</summary>
    public async Task<ExchangeMailOptions?> BuildOptionsAsync(CancellationToken ct = default)
    {
        var s = await GetAsync(ct);
        if (s.IsPreselected || !s.Enabled) return null;
        var cert = LoadCertificate(s);
        if (cert is null) return null;
        return new ExchangeMailOptions
        {
            Enabled = true, TenantId = s.TenantId, ClientId = s.ClientId, ClientCertificate = cert, SenderMailbox = s.Sender,
            AllowedRecipients = s.AllowedRecipients, ForbiddenTenantIds = _forbidden,
        };
    }

    private X509Certificate2? LoadCertificate(MailSettingsView s)
    {
        if (!s.CertUsable) return null;
        try
        {
            var cert = X509CertificateLoader.LoadPkcs12(Decrypt(File.ReadAllBytes(PfxPath)), "", X509KeyStorageFlags.EphemeralKeySet);
            return string.Equals(cert.Thumbprint, s.CertThumbprint, StringComparison.OrdinalIgnoreCase) ? cert : null;
        }
        catch (Exception e) when (e is CryptographicException or IOException) { return null; }   // z. B. anderer Token-Schlüssel: neu erzeugen
    }

    private byte[] Encrypt(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[plain.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, Info);
        return nonce.Concat(tag).Concat(cipher).ToArray();
    }

    private byte[] Decrypt(byte[] blob)
    {
        if (blob.Length < 28) throw new CryptographicException();
        var plain = new byte[blob.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, Info);
        return plain;
    }

    private static string[] Split(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Versender, der die gespeicherte Einrichtung bei jedem Aufruf neu liest: Änderungen im Portal wirken sofort, ohne Neustart.
/// Fällt auf einen umgebungskonfigurierten Dienst zurück, wenn im Portal nichts aktiviert wurde; sonst fail-closed „mail_disabled“.</summary>
public sealed class ConfigurableMailer : IInvitationMailer, IOtpMailer
{
    private readonly MailSettingsService _settings; private readonly ExchangeOnlineMailService? _fallback; private readonly Func<HttpClient> _http;
    private string? _key; private ExchangeOnlineMailService? _current;

    public ConfigurableMailer(MailSettingsService settings, ExchangeOnlineMailService? fallback = null, Func<HttpClient>? http = null)
    { _settings = settings; _fallback = fallback; _http = http ?? (() => new HttpClient()); }

    private async Task<ExchangeOnlineMailService?> ResolveAsync(CancellationToken ct)
    {
        var o = await _settings.BuildOptionsAsync(ct);
        if (o is null) { _current = null; _key = null; return _fallback; }
        var key = o.TenantId + "|" + o.ClientId + "|" + o.ClientCertificate!.Thumbprint + "|" + o.SenderMailbox + "|" + string.Join(",", o.AllowedRecipients);
        if (_current is null || key != _key) { _current = new ExchangeOnlineMailService(_http(), o); _key = key; }   // Tokencache bleibt zwischen Aufrufen erhalten
        return _current;
    }

    public async Task<MailSendResult> SendAsync(InvitationMail mail, CancellationToken ct = default) =>
        await ResolveAsync(ct) is { } s ? await s.SendAsync(mail, ct) : new(MailOutcome.Rejected, "mail_disabled");

    public async Task<MailSendResult> SendOtpAsync(OtpMail mail, CancellationToken ct = default) =>
        await ResolveAsync(ct) is { } s ? await s.SendOtpAsync(mail, ct) : new(MailOutcome.Rejected, "mail_disabled");
}
