using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LearnCards.Web.Services.Mail;

/// <summary>Wer darf einen Anmeldecode erhalten? Verhindert, dass die Anmeldeseite als offener Mailversand für beliebige Adressen missbraucht wird.</summary>
public interface IOtpRecipientPolicy
{
    Task<bool> IsAllowedAsync(string email, CancellationToken ct);
}

/// <summary>Nur Adressen mit gültiger Einladung (pending, nicht abgelaufen, nicht widerrufen) oder aktivem externen Learny-Benutzer. Alle anderen erhalten keinen Code (fail-closed, ADR 0026). Das ist keine Berechtigung: die Registrierung prüft der InvitationService im Callback.</summary>
public sealed class InvitationOtpRecipientPolicy : IOtpRecipientPolicy
{
    private readonly InvitationService _inv;
    public InvitationOtpRecipientPolicy(InvitationService inv) => _inv = inv;
    public Task<bool> IsAllowedAsync(string email, CancellationToken ct) => _inv.MayReceiveSignInCodeAsync(email, ct);
}

/// <summary>Einfaches Gleitfenster-Limit im Speicher (je Schlüssel).</summary>
public sealed class SlidingWindowLimiter
{
    private readonly int _max; private readonly TimeSpan _window; private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _hits = new();
    public SlidingWindowLimiter(int max, TimeSpan window, Func<DateTime>? clock = null) { _max = max; _window = window; _clock = clock ?? (() => DateTime.UtcNow); }

    /// <returns>null bei Erlaubnis, sonst die Wartezeit bis zum nächsten freien Platz.</returns>
    public TimeSpan? TryAcquire(string key)
    {
        var now = _clock(); var q = _hits.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() >= _window) q.Dequeue();
            if (q.Count >= _max) return _window - (now - q.Peek());
            q.Enqueue(now);
            return null;
        }
    }
}

public sealed class OtpExtensionOptions
{
    /// <summary>Authority des External Tenants, z. B. https://&lt;sub&gt;.ciamlogin.com/&lt;tenant-id&gt;/v2.0 (Issuer-Prüfung über die Discovery).</summary>
    public required string Authority { get; init; }
    /// <summary>Erwartete Audience(s): Client-ID und/oder Identifier-URI der API-App-Registrierung der Extension.</summary>
    public required IReadOnlyCollection<string> Audiences { get; init; }
    public required string ExpectedTenantId { get; init; }
    /// <summary>Aufrufer: Microsofts Authentication-Events-Dienst (azp/appid laut Dokumentation).</summary>
    public string RequiredCallerAppId { get; init; } = "99045fe1-7639-4a75-9d4a-577b6ca3810f";
    /// <summary>Nur für Development/Tests: http auf Loopback für die Discovery erlauben. Standard: false (HTTPS Pflicht).</summary>
    public bool AllowLoopbackHttp { get; init; }
    public int MaxBodyBytes { get; init; } = 8 * 1024;
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int GlobalPerMinute { get; init; } = 60;
    public int PerRecipientPer10Minutes { get; init; } = 3;
    public TimeSpan DedupeWindow { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromMinutes(2);
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;
    /// <summary>Optionaler Audit-Haken: erhält nur Ereignis und Ergebniscode, nie Code, Token oder Adresse.</summary>
    public Func<string, string, Task>? Audit { get; init; }
}

public sealed record OtpHttpResult(int Status, string? Json = null, TimeSpan? RetryAfter = null);

/// <summary>Prüft das Bearer-Token der Custom Authentication Extension (RS256 über JWKS, Issuer aus der Discovery, Audience, Aufrufer-App, Tenant).</summary>
public sealed class ExtensionTokenValidator
{
    private readonly HttpClient _http; private readonly OtpExtensionOptions _o;
    private JsonElement _discovery; private bool _haveDiscovery;
    private Dictionary<string, RSAParameters> _keys = new(); private DateTime _fetched = DateTime.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ExtensionTokenValidator(HttpClient http, OtpExtensionOptions options)
    {
        _http = http; _o = options;
        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var a) || !(a.Scheme == Uri.UriSchemeHttps || (options.AllowLoopbackHttp && a.IsLoopback))) throw new InvalidOperationException("Authority muss HTTPS verwenden.");
        if (options.Audiences.Count == 0) throw new InvalidOperationException("Mindestens eine Audience ist Pflicht.");
    }

    public async Task<bool> ValidateAsync(string? authorizationHeader, CancellationToken ct) => (await ValidateDetailedAsync(authorizationHeader, ct)).Ok;

    /// <summary>Wie <see cref="ValidateAsync"/>, nennt aber den Ablehnungsgrund als Kurzcode (nur für das Betreiber-Log; nie Token-Inhalt).</summary>
    public async Task<(bool Ok, string Reason)> ValidateDetailedAsync(string? authorizationHeader, CancellationToken ct)
    {
        try
        {
            if (authorizationHeader is null || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return (false, "no_bearer");
            var jwt = authorizationHeader[7..].Trim();
            var parts = jwt.Split('.');
            if (parts.Length != 3 || jwt.Length > 8192) return (false, "format");
            using var header = JsonDocument.Parse(B64(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "RS256") return (false, "alg");
            var kid = header.RootElement.TryGetProperty("kid", out var k) ? k.GetString() : null;
            if (kid is null || await GetKeyAsync(kid, ct) is not { } key) return (false, "kid");
            using var rsa = RSA.Create(); rsa.ImportParameters(key);
            if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), B64Bytes(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return (false, "signature");

            using var doc = JsonDocument.Parse(B64(parts[1]));
            var c = doc.RootElement; var now = _o.Clock();
            string? S(string n) => c.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var disc = await GetDiscoveryAsync(ct);
            if (S("iss") != disc.GetProperty("issuer").GetString()) return (false, "issuer");
            if (S("aud") is not { } aud || !_o.Audiences.Contains(aud, StringComparer.Ordinal)) return (false, "audience");
            // V2-Token: azp; V1-Token: appid
            var caller = S("azp") ?? S("appid");
            if (caller != _o.RequiredCallerAppId) return (false, "caller");
            if (S("tid") != _o.ExpectedTenantId) return (false, "tenant");
            if (!c.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number) return (false, "expired");
            if (DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()).UtcDateTime + _o.ClockSkew < now) return (false, "expired");
            if (c.TryGetProperty("nbf", out var nbf) && nbf.ValueKind == JsonValueKind.Number && DateTimeOffset.FromUnixTimeSeconds(nbf.GetInt64()).UtcDateTime - _o.ClockSkew > now) return (false, "not_yet");
            return (true, "");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or FormatException or HttpRequestException or CryptographicException or InvalidOperationException or OperationCanceledException)
        {
            return (false, "invalid_token");
        }
    }

    private async Task<JsonElement> GetDiscoveryAsync(CancellationToken ct)
    {
        if (_haveDiscovery) return _discovery;
        await _gate.WaitAsync(ct);
        try
        {
            if (_haveDiscovery) return _discovery;
            using var d = JsonDocument.Parse(await _http.GetStringAsync(_o.Authority.TrimEnd('/') + "/.well-known/openid-configuration", ct));
            _discovery = d.RootElement.Clone(); _haveDiscovery = true; return _discovery;
        }
        finally { _gate.Release(); }
    }

    private async Task<RSAParameters?> GetKeyAsync(string kid, CancellationToken ct)
    {
        if (_keys.TryGetValue(kid, out var cached)) return cached;
        if (_o.Clock() - _fetched < TimeSpan.FromMinutes(1) && _keys.Count > 0) return null;   // unbekannte kid: höchstens 1 Abruf pro Minute
        var disc = await GetDiscoveryAsync(ct);
        using var jwks = JsonDocument.Parse(await _http.GetStringAsync(disc.GetProperty("jwks_uri").GetString(), ct));
        var fresh = new Dictionary<string, RSAParameters>();
        foreach (var jwk in jwks.RootElement.GetProperty("keys").EnumerateArray())
            if (jwk.TryGetProperty("kty", out var kt) && kt.GetString() == "RSA" && jwk.TryGetProperty("kid", out var id) && id.GetString() is { } kk && jwk.TryGetProperty("n", out var n) && jwk.TryGetProperty("e", out var e))
                fresh[kk] = new RSAParameters { Modulus = B64Bytes(n.GetString()!), Exponent = B64Bytes(e.GetString()!) };
        _keys = fresh; _fetched = _o.Clock();
        return _keys.TryGetValue(kid, out var found) ? found : null;
    }

    private static byte[] B64Bytes(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }
    private static string B64(string s) => Encoding.UTF8.GetString(B64Bytes(s));
}

/// <summary>
/// Handler der Entra-Extension "OnOtpSend" (emailOtpSend). Entra erzeugt und validiert den Code; dieser Handler versendet ihn nur über den ExchangeOnlineMailService.
/// Nie: Code erzeugen/ändern/speichern/protokollieren. Bei jedem Fehler wird nicht "continue" geantwortet, damit Entra keine erfolgreiche Zustellung annimmt
/// (ohne konfigurierten Rückfall auf Microsoft sendet Entra dann keinen Code).
/// </summary>
public sealed class OtpSendHandler
{
    private const string EventType = "microsoft.graph.authenticationEvent.emailOtpSend";
    private readonly ExtensionTokenValidator _tokens; private readonly IOtpMailer _mailer; private readonly IOtpRecipientPolicy _policy; private readonly OtpExtensionOptions _o;
    private readonly SlidingWindowLimiter _global, _perRecipient;
    private readonly byte[] _dedupeKey = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, DateTime> _delivered = new();

    public OtpSendHandler(ExtensionTokenValidator tokens, IOtpMailer mailer, IOtpRecipientPolicy policy, OtpExtensionOptions options)
    {
        _tokens = tokens; _mailer = mailer; _policy = policy; _o = options;
        _global = new SlidingWindowLimiter(options.GlobalPerMinute, TimeSpan.FromMinutes(1), options.Clock);
        _perRecipient = new SlidingWindowLimiter(options.PerRecipientPer10Minutes, TimeSpan.FromMinutes(10), options.Clock);
    }

    public async Task<OtpHttpResult> HandleAsync(string? authorization, string body, CancellationToken ct)
    {
        if (body.Length > _o.MaxBodyBytes) return await Done(413, "too_large");
        var auth = await _tokens.ValidateDetailedAsync(authorization, ct);
        if (!auth.Ok) return await Done(401, "unauthorized:" + auth.Reason);

        string email, code;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() != EventType) return await Done(400, "bad_schema");
            var data = root.GetProperty("data");
            if (data.TryGetProperty("tenantId", out var tid) && tid.ValueKind == JsonValueKind.String && tid.GetString() != _o.ExpectedTenantId) return await Done(403, "wrong_tenant");
            var ctxEl = data.GetProperty("otpContext");
            // Die Dokumentation schreibt den Code uneinheitlich (oneTimeCode / onetimecode); beide werden akzeptiert.
            var codeEl = ctxEl.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, "oneTimeCode", StringComparison.OrdinalIgnoreCase));
            if (codeEl.Value.ValueKind != JsonValueKind.String) return await Done(400, "bad_schema");
            code = codeEl.Value.GetString()!;
            email = InvitationService.NormalizeEmail(ctxEl.GetProperty("identifier").GetString()) ?? "";
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return await Done(400, "bad_schema"); }
        if (email.Length == 0 || !OtpMailTemplate.IsValidCode(code)) return await Done(400, "bad_schema");

        if (_global.TryAcquire("global") is { } g) return await Done(429, "rate_limited", g);
        if (_perRecipient.TryAcquire(email) is { } r) return await Done(429, "rate_limited", r);
        if (!await _policy.IsAllowedAsync(email, ct)) return await Done(403, "recipient_not_allowed");

        // Wiederholter Aufruf (z. B. Wiederholung durch Entra): nach erfolgreicher Annahme nicht erneut senden. Gespeichert wird nur ein Schlüssel-Hash, kurz im Speicher.
        var key = Convert.ToHexString(HMACSHA256.HashData(_dedupeKey, Encoding.UTF8.GetBytes(email + "|" + code)));
        var now = _o.Clock();
        foreach (var old in _delivered.Where(kv => now - kv.Value > _o.DedupeWindow).Select(kv => kv.Key).ToList()) _delivered.TryRemove(old, out _);
        if (_delivered.ContainsKey(key)) return await Done(200, "duplicate_ignored", continueBody: true);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_o.SendTimeout);
        MailSendResult res;
        try { res = await _mailer.SendOtpAsync(new OtpMail(email, code), cts.Token); }
        catch (Exception) { res = new(MailOutcome.Ambiguous, "mailer_exception"); }

        switch (res.Outcome)
        {
            case MailOutcome.Accepted:
                _delivered[key] = now;
                return await Done(200, "accepted", continueBody: true);
            case MailOutcome.Rejected:
                return await Done(502, res.Code, res.RetryAfter);   // nichts gesendet
            default:
                return await Done(504, res.Code, res.RetryAfter);   // unklar: nicht als erfolgreich melden
        }
    }

    private async Task<OtpHttpResult> Done(int status, string code, TimeSpan? retryAfter = null, bool continueBody = false)
    {
        if (_o.Audit is not null) await _o.Audit("otp_send", $"{status}:{code}");
        return new OtpHttpResult(status, continueBody
            ? "{\"data\":{\"@odata.type\":\"microsoft.graph.OnOtpSendResponseData\",\"actions\":[{\"@odata.type\":\"microsoft.graph.OtpSend.continueWithDefaultBehavior\"}]}}"
            : null, retryAfter);
    }
}
