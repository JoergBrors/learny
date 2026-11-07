using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace InviteRegistration;

/// <summary>Inhalt der Einladungsmail. Enthält den Klartextlink nur im Speicher, nie persistiert (ADR 0044).</summary>
public sealed record InvitationMail(string RecipientEmail, string Link, DateTime ExpiresAt, string? DisplayName = null);

public enum MailOutcome
{
    /// <summary>Graph hat die Nachricht zur Zustellung angenommen (HTTP 202). Keine Zustellgarantie.</summary>
    Accepted,
    /// <summary>Eindeutig nicht versendet (Konfiguration, Berechtigung, Ablehnung).</summary>
    Rejected,
    /// <summary>Ergebnis unklar (Zeitüberschreitung, Netzwerk, 5xx): die Mail kann trotzdem versendet worden sein.</summary>
    Ambiguous,
}

/// <param name="RetryAfter">Vom Dienst verlangte Wartezeit (z. B. HTTP 429 mit Retry-After), begrenzt auf <see cref="ExchangeMailOptions.MaxRetryAfter"/>.</param>
public sealed record MailSendResult(MailOutcome Outcome, string Code, TimeSpan? RetryAfter = null);

/// <summary>Anmeldecode, den Microsoft Entra erzeugt hat. Wird nur durchgereicht: nie erzeugt, verändert, gespeichert oder protokolliert.</summary>
public sealed record OtpMail(string RecipientEmail, string Code);

/// <summary>Versand von Entra-Einmalcodes (Custom Authentication Extension OnOtpSend). Microsoft Entra bleibt für die Validierung des Codes verantwortlich.</summary>
public interface IOtpMailer
{
    Task<MailSendResult> SendOtpAsync(OtpMail mail, CancellationToken ct = default);
}

/// <summary>Reiner Transport: baut die Nachricht und sendet sie. Kennt weder Einladungsstatus noch Token-Lebenszyklus.</summary>
public interface IInvitationMailer
{
    Task<MailSendResult> SendAsync(InvitationMail mail, CancellationToken ct = default);
}

public sealed class ExchangeMailOptions
{
    /// <summary>Standard aus (ADR 0026): ohne ausdrückliche Aktivierung wird nichts gesendet.</summary>
    public bool Enabled { get; init; }
    /// <summary>Tenant der Mail-App (Workforce-/Exchange-Tenant), nie der External-ID-Tenant.</summary>
    public string TenantId { get; init; } = "";
    /// <summary>Tenant-IDs, mit denen nie gesendet wird (z. B. der External-ID-Tenant). Schutz gegen Verwechslung.</summary>
    public IReadOnlyCollection<string> ForbiddenTenantIds { get; init; } = Array.Empty<string>();
    public string ClientId { get; init; } = "";
    /// <summary>Zertifikat (bevorzugt, nur Public-Key im Verzeichnis, privater Schlüssel im Zertifikatsspeicher/Key Vault). Genau eines von Zertifikat oder Secret.</summary>
    public X509Certificate2? ClientCertificate { get; init; }
    /// <summary>Alternative: Geheimnis nur aus der Umgebung/Secret-Verwaltung; nie loggen.</summary>
    public string ClientSecret { get; init; } = "";
    /// <summary>Das eine erlaubte Absenderpostfach (muss mit dem RBAC-Scope übereinstimmen). Kommt nie aus Anfragen.</summary>
    public string SenderMailbox { get; init; } = "";
    /// <summary>Erlaubte Empfänger: vollständige Adressen oder "@domain". Leer = niemand (fail-closed).</summary>
    public IReadOnlyCollection<string> AllowedRecipients { get; init; } = Array.Empty<string>();
    public string AuthorityHost { get; init; } = "https://login.microsoftonline.com";
    public string GraphBase { get; init; } = "https://graph.microsoft.com/v1.0";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromHours(1);
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;
}

/// <summary>
/// Versand über Microsoft Graph (<c>POST /users/{postfach}/sendMail</c>) mit Anwendungsberechtigung (Client-Credentials, Zertifikat bevorzugt).
/// "Application Mail.Send" wird über Exchange Online RBAC for Applications auf das Absenderpostfach begrenzt (docs/entra-external-id/mail-permissions.md).
/// Der Dienst schreibt nichts in Logs; Fehlercodes enthalten nie Token, Geheimnisse oder Links.
/// </summary>
public sealed class ExchangeOnlineMailService : IInvitationMailer, IOtpMailer
{
    private readonly HttpClient _http;
    private readonly ExchangeMailOptions _o;
    private string? _token;
    private DateTime _tokenExpires;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    public ExchangeOnlineMailService(HttpClient http, ExchangeMailOptions options)
    {
        _http = http;
        _o = options;
    }

    public async Task<MailSendResult> SendAsync(InvitationMail mail, CancellationToken ct = default)
    {
        var pre = Preflight(mail.RecipientEmail, out var to);
        if (pre is not null) return pre;
        if (mail.Link.Any(c => c is '\r' or '\n')) return new(MailOutcome.Rejected, "invalid_message");
        if (!RecipientAllowed(to!)) return new(MailOutcome.Rejected, "recipient_not_allowed");
        return await SendMessageAsync(to!, InvitationMailTemplate.Subject, "HTML", InvitationMailTemplate.BuildHtml(mail), ct);
    }

    /// <summary>OTP-Versand: gleicher Absender, gleiche App-Identität, gleicher Postfachscope. Die Empfängerprüfung (Einladung/bekannter Benutzer) liegt vor dem Aufruf im Extension-Handler,
    /// nicht in der statischen Allowlist der Einladungsmails.</summary>
    public async Task<MailSendResult> SendOtpAsync(OtpMail mail, CancellationToken ct = default)
    {
        var pre = Preflight(mail.RecipientEmail, out var to);
        if (pre is not null) return pre;
        if (!OtpMailTemplate.IsValidCode(mail.Code)) return new(MailOutcome.Rejected, "invalid_message");
        return await SendMessageAsync(to!, OtpMailTemplate.Subject, "HTML", OtpMailTemplate.BuildHtml(mail.Code), ct);
    }

    private MailSendResult? Preflight(string recipient, out string? to)
    {
        to = null;
        if (!_o.Enabled) return new(MailOutcome.Rejected, "mail_disabled");
        var hasCert = _o.ClientCertificate is not null; var hasSecret = !string.IsNullOrWhiteSpace(_o.ClientSecret);
        if (string.IsNullOrWhiteSpace(_o.TenantId) || string.IsNullOrWhiteSpace(_o.ClientId) || hasCert == hasSecret
            || InvitationService.NormalizeEmail(_o.SenderMailbox) is null)
            return new(MailOutcome.Rejected, "mail_not_configured");
        if (_o.ForbiddenTenantIds.Any(t => string.Equals(t, _o.TenantId, StringComparison.OrdinalIgnoreCase)))
            return new(MailOutcome.Rejected, "wrong_tenant");
        to = InvitationService.NormalizeEmail(recipient);
        return to is null ? new(MailOutcome.Rejected, "invalid_message") : null;
    }

    private async Task<MailSendResult> SendMessageAsync(string to, string subject, string contentType, string content, CancellationToken ct)
    {
        string? token;
        try { token = await GetTokenAsync(ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(MailOutcome.Rejected, "auth_timeout"); }   // nichts gesendet
        catch (HttpRequestException) { return new(MailOutcome.Rejected, "auth_network"); }
        if (token is null) return new(MailOutcome.Rejected, "auth_failed");

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                message = new
                {
                    subject,
                    body = new { contentType, content },
                    toRecipients = new[] { new { emailAddress = new { address = to } } },
                },
                saveToSentItems = false,
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_o.GraphBase.TrimEnd('/')}/users/{Uri.EscapeDataString(_o.SenderMailbox)}/sendMail")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_o.RequestTimeout);
            using var res = await _http.SendAsync(req, cts.Token);
            switch ((int)res.StatusCode)
            {
                case 202: return new(MailOutcome.Accepted, "accepted");
                case 401: InvalidateToken(); return new(MailOutcome.Rejected, "unauthorized");
                case 403: return new(MailOutcome.Rejected, "permission_denied");   // z. B. Postfach außerhalb des RBAC-Scopes
                case 404: return new(MailOutcome.Rejected, "mailbox_not_found");
                case 429: return new(MailOutcome.Rejected, "throttled", ParseRetryAfter(res));
                case >= 400 and < 500: return new(MailOutcome.Rejected, "rejected_" + (int)res.StatusCode);
                default: return new(MailOutcome.Ambiguous, "server_" + (int)res.StatusCode, ParseRetryAfter(res));
            }
        }
        catch (OperationCanceledException) { return new(MailOutcome.Ambiguous, "timeout"); }
        catch (HttpRequestException) { return new(MailOutcome.Ambiguous, "network"); }
    }

    private bool RecipientAllowed(string address) =>
        _o.AllowedRecipients.Any(a => a.StartsWith('@')
            ? address.EndsWith(a.ToLowerInvariant(), StringComparison.Ordinal)
            : string.Equals(address, a.Trim().ToLowerInvariant(), StringComparison.Ordinal));

    private TimeSpan? ParseRetryAfter(HttpResponseMessage res)
    {
        var h = res.Headers.RetryAfter;
        if (h is null) return null;
        var wait = h.Delta ?? (h.Date is { } d ? d - new DateTimeOffset(_o.Clock(), TimeSpan.Zero) : (TimeSpan?)null);
        if (wait is null) return null;
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        return wait > _o.MaxRetryAfter ? _o.MaxRetryAfter : wait;
    }

    private async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (_token is not null && _tokenExpires > _o.Clock().AddMinutes(1)) return _token;
            var endpoint = $"{_o.AuthorityHost.TrimEnd('/')}/{Uri.EscapeDataString(_o.TenantId)}/oauth2/v2.0/token";
            var form = new Dictionary<string, string>
            {
                ["client_id"] = _o.ClientId,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials",
            };
            if (_o.ClientCertificate is { } cert)
            {
                form["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
                form["client_assertion"] = BuildAssertion(cert, endpoint);
            }
            else form["client_secret"] = _o.ClientSecret;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_o.RequestTimeout);
            using var res = await _http.PostAsync(endpoint, new FormUrlEncodedContent(form), cts.Token);
            if (res.StatusCode != HttpStatusCode.OK) return null;   // Antwortkörper wird nie weitergegeben
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
            _token = doc.RootElement.GetProperty("access_token").GetString();
            _tokenExpires = _o.Clock().AddSeconds(doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 300);
            return _token;
        }
        catch (JsonException) { return null; }
        catch (KeyNotFoundException) { return null; }
        catch (CryptographicException) { return null; }   // z. B. Zertifikat ohne privaten Schlüssel
        finally { _tokenGate.Release(); }
    }

    /// <summary>Client-Assertion (RFC 7523) mit dem privaten Schlüssel des Zertifikats; nur der öffentliche Teil liegt in Entra.</summary>
    private string BuildAssertion(X509Certificate2 cert, string audience)
    {
        var now = new DateTimeOffset(_o.Clock(), TimeSpan.Zero).ToUnixTimeSeconds();
        var header = B64u(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["alg"] = "RS256", ["typ"] = "JWT", ["x5t#S256"] = B64u(cert.GetCertHash(HashAlgorithmName.SHA256)) }));
        var payload = B64u(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = audience, ["iss"] = _o.ClientId, ["sub"] = _o.ClientId, ["jti"] = Guid.NewGuid().ToString(), ["nbf"] = now, ["exp"] = now + 600,
        }));
        using var rsa = cert.GetRSAPrivateKey() ?? throw new CryptographicException("Kein privater RSA-Schlüssel.");
        return header + "." + payload + "." + B64u(rsa.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static string B64u(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private void InvalidateToken() => _token = null;
}
