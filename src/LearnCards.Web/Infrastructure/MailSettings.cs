using System.Security.Cryptography.X509Certificates;
using LearnCards.Web.Services;
using LearnCards.Web.Services.Mail;

namespace LearnCards.Web.Infrastructure;

/// <summary>
/// Konfiguration des Mailversands über Exchange Online / Microsoft Graph (ADR 0044) und der OTP-Extension (ADR 0045). Standard: aus. Ist <c>MAIL_ENABLED</c> gesetzt, müssen alle
/// Pflichtwerte vorhanden sein — sonst Startabbruch (ADR 0026), nie ein stiller Rückfall. Zertifikat oder Geheimnis stammen nur aus Datei bzw. Umgebung/Geheimnisverwaltung,
/// nie aus der Datenbank; Fehlertexte nennen keinen Wert. Die Mail-App liegt im Workforce-Tenant, nie im External-ID-Tenant (Verwechslungsschutz).
/// Die Zertifikatsverwaltung über die Learny-PKI und die Rotation sind Phase D.
/// </summary>
public sealed class ExchangeMailSettings
{
    public bool Enabled { get; init; }
    public string TenantId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string Sender { get; init; } = "";
    public IReadOnlyList<string> AllowedRecipients { get; init; } = Array.Empty<string>();
    public string CertificatePath { get; init; } = "";
    public string CertificatePassword { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public IReadOnlyList<string> ForbiddenTenantIds { get; init; } = Array.Empty<string>();
    /// <summary>Schlüsselverzeichnis des auf der Plattform erzeugten Zertifikats (MAIL_KEY_DIR; Standard data/mail, ausserhalb des Webroots).</summary>
    public string KeyDir { get; init; } = "";
    /// <summary>true, wenn weder Zertifikatsdatei noch Secret konfiguriert sind: das Zertifikat wird dann in der Administration auf der Plattform erzeugt (privater Schlüssel verlässt sie nie).</summary>
    public bool UsesPlatformCertificate => CertificatePath.Length == 0 && ClientSecret.Length == 0;

    public static ExchangeMailSettings Load(Func<string, string> env, string externalTenantId, bool isDevelopment, string dataDir = "data")
    {
        var enabled = env("MAIL_ENABLED").Trim() is "1" or "true" or "True" or "TRUE";
        var s = new ExchangeMailSettings
        {
            Enabled = enabled,
            TenantId = env("MAIL_TENANT_ID").Trim(),
            ClientId = env("MAIL_CLIENT_ID").Trim(),
            Sender = env("MAIL_SENDER").Trim(),
            AllowedRecipients = env("MAIL_ALLOWED_RECIPIENTS").Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToList(),
            CertificatePath = env("MAIL_CLIENT_CERT_PATH").Trim(),
            CertificatePassword = env("MAIL_CLIENT_CERT_PASSWORD"),
            ClientSecret = env("MAIL_CLIENT_SECRET"),
            ForbiddenTenantIds = string.IsNullOrWhiteSpace(externalTenantId) ? Array.Empty<string>() : new[] { externalTenantId },
            KeyDir = env("MAIL_KEY_DIR") is { Length: > 0 } kd ? kd.Trim() : Path.Combine(dataDir, "mail"),
        };
        if (enabled) s.EnsureValid(isDevelopment);
        return s;
    }

    public void EnsureValid(bool isDevelopment)
    {
        if (!Guid.TryParse(TenantId, out _)) throw new InvalidOperationException("MAIL_TENANT_ID muss die Verzeichnis-ID (GUID) des Tenants der Mail-App sein.");
        if (ForbiddenTenantIds.Any(t => string.Equals(t, TenantId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("MAIL_TENANT_ID darf nicht der External-ID-Tenant sein (die Mail-App liegt im Workforce-Tenant, ADR 0044).");
        if (!Guid.TryParse(ClientId, out _)) throw new InvalidOperationException("MAIL_CLIENT_ID muss die Anwendungs-ID (GUID) der Mail-App sein.");
        if (InvitationService.NormalizeEmail(Sender) is null) throw new InvalidOperationException("MAIL_SENDER muss die Adresse des Absenderpostfachs sein (Exchange-RBAC-Scope).");
        var hasCert = CertificatePath.Length > 0; var hasSecret = ClientSecret.Length > 0;
        if (hasCert && hasSecret) throw new InvalidOperationException("Für die Mail-App höchstens eines setzen: MAIL_CLIENT_CERT_PATH oder MAIL_CLIENT_SECRET (ohne beide wird das Zertifikat auf der Plattform erzeugt: Administration → Mailversand).");
        if (hasSecret && !isDevelopment) throw new InvalidOperationException("MAIL_CLIENT_SECRET ist außerhalb von Development nicht zulässig; ein Zertifikat (MAIL_CLIENT_CERT_PATH) verwenden.");
        if (hasCert && !File.Exists(CertificatePath)) throw new InvalidOperationException("MAIL_CLIENT_CERT_PATH verweist auf keine lesbare Datei.");
        try { ParseRecipients(string.Join(",", AllowedRecipients)); }
        catch (ArgumentException) { throw new InvalidOperationException("MAIL_ALLOWED_RECIPIENTS enthält einen ungültigen Eintrag (erlaubt: vollständige Adressen, @domain oder *)."); }
    }

    /// <summary>Zerlegt und prüft eine Empfängerliste (Komma, Semikolon, Leerzeichen, Zeilenumbruch): vollständige Adressen, <c>@domain</c> oder <c>*</c>. Leer = niemand. Wirft <see cref="ArgumentException"/> bei ungültigen Einträgen.</summary>
    public static List<string> ParseRecipients(string? input)
    {
        var list = new List<string>();
        foreach (var raw in (input ?? "").Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var r = raw.ToLowerInvariant();
            var ok = r == "*" || (r.StartsWith('@') ? r.Length > 3 && r.IndexOf('.') > 1 && !r[1..].Contains('@') : InvitationService.NormalizeEmail(r) is not null);
            if (!ok) throw new ArgumentException("Ungültiger Empfängereintrag (erlaubt: vollständige Adressen, @domain oder *).");
            if (!list.Contains(r)) list.Add(r);
        }
        return list;
    }

    /// <summary>Optionen für den Dienst. Das Zertifikat wird nur im Speicher gehalten (ephemer).</summary>
    public ExchangeMailOptions ToOptions(X509Certificate2? platformCertificate = null, IReadOnlyCollection<string>? recipients = null)
    {
        X509Certificate2? cert = platformCertificate;
        if (cert is null && Enabled && CertificatePath.Length > 0)
            cert = X509CertificateLoader.LoadPkcs12FromFile(CertificatePath, CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
        return new ExchangeMailOptions
        {
            Enabled = Enabled, TenantId = TenantId, ClientId = ClientId, ClientCertificate = cert, ClientSecret = cert is null ? ClientSecret : "",
            SenderMailbox = Sender, AllowedRecipients = recipients ?? AllowedRecipients, ForbiddenTenantIds = ForbiddenTenantIds,
        };
    }
}

/// <summary>
/// Anbindung der Entra-Custom-Authentication-Extension „OnOtpSend“ (ADR 0045): <c>POST /extensions/otp-send</c>. Standard: aus. Setzt External ID und den Mailversand voraus.
/// Die Audience(s) sind Pflicht; ohne sie keine Aktivierung (fail-closed).
/// </summary>
public sealed class OtpExtensionSettings
{
    public bool Enabled { get; init; }
    public IReadOnlyList<string> Audiences { get; init; } = Array.Empty<string>();
    /// <summary>Discovery-Basis zur Prüfung der Extension-Token; Standard: <c>EXTID_AUTHORITY</c>.</summary>
    public string Authority { get; init; } = "";
    public bool AllowLoopbackHttp { get; init; }
    public int GlobalPerMinute { get; init; } = 60;
    public int PerRecipientPer10Minutes { get; init; } = 3;

    public static OtpExtensionSettings Load(Func<string, string> env, ExternalIdSettings ext, ExchangeMailSettings mail, bool isDevelopment = false)
    {
        var enabled = env("EXTID_OTP_ENABLED").Trim() is "1" or "true" or "True" or "TRUE";
        if (!enabled) return new OtpExtensionSettings();
        if (!ext.Enabled) throw new InvalidOperationException("EXTID_OTP_ENABLED setzt EXTID_ENABLED voraus.");
        if (!mail.Enabled) throw new InvalidOperationException("EXTID_OTP_ENABLED setzt MAIL_ENABLED voraus (der Anmeldecode wird über das Learny-Postfach versendet).");
        var audiences = env("EXTID_OTP_AUDIENCES").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (audiences.Count == 0) throw new InvalidOperationException("EXTID_OTP_AUDIENCES fehlt (Client-ID und/oder Identifier-URI der API-App der Extension).");
        var authority = env("EXTID_OTP_AUTHORITY").Trim().TrimEnd('/');
        if (authority.Length == 0) authority = ext.Authority;
        var loopback = isDevelopment && Uri.TryCreate(authority, UriKind.Absolute, out var lu) && lu.Scheme == Uri.UriSchemeHttp && lu.IsLoopback;
        if (!loopback && (!Uri.TryCreate(authority, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)) throw new InvalidOperationException("EXTID_OTP_AUTHORITY muss eine HTTPS-Adresse sein (http nur auf Loopback in Development).");
        return new OtpExtensionSettings
        {
            Enabled = true, Audiences = audiences, Authority = authority, AllowLoopbackHttp = loopback,
            GlobalPerMinute = int.TryParse(env("EXTID_OTP_GLOBAL_PER_MINUTE"), out var g) && g is >= 1 and <= 600 ? g : 60,
            PerRecipientPer10Minutes = int.TryParse(env("EXTID_OTP_PER_RECIPIENT_10MIN"), out var r) && r is >= 1 and <= 20 ? r : 3,
        };
    }
}
