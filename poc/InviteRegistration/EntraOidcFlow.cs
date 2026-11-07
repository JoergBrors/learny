using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InviteRegistration;

public sealed class EntraOidcOptions
{
    /// <summary>Authority inkl. Tenant, z. B. https://&lt;sub&gt;.ciamlogin.com/&lt;tenant-id&gt;/v2.0</summary>
    public required string Authority { get; init; }
    public required string ClientId { get; init; }
    /// <summary>Nur aus Umgebung/Secret-Verwaltung; nie loggen.</summary>
    public required string ClientSecret { get; init; }
    public required string RedirectUri { get; init; }
    public string Scope { get; init; } = "openid profile email";
    /// <summary>Sendet <c>prompt=login</c>: Entra ignoriert eine bestehende Browsersitzung und verlangt die Anmeldung (bei E-Mail-OTP den Einmalcode) erneut. Eine bestehende Sitzung gilt nie als frische Verifikation;
    /// Identität und Einladung werden unabhängig davon immer vollständig geprüft. Die Wirkung bei External ID ist live zu bestätigen.</summary>
    public bool ForceReauthentication { get; init; } = true;
    /// <summary>Erwartete Tenant-ID (Claim <c>tid</c>); abweichende Tokens werden abgelehnt. Bindet die Identität an den konfigurierten External Tenant.</summary>
    public string? ExpectedTenantId { get; init; }
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Entscheidet, wann die E-Mail als bestätigt gilt, falls das Token kein <c>email_verified</c> trägt. Standard: nie (fail-closed).
    /// Wird erst nach Prüfung echter Tokens festgelegt (docs/entra-external-id/learny-integration.md).</summary>
    public Func<IReadOnlyDictionary<string, JsonElement>, bool>? EmailVerifiedFallback { get; init; }
    /// <summary>Diagnose: erhält nur Claim-Namen und ausgewählte unkritische Werte, nie das Token.</summary>
    public Action<IReadOnlyDictionary<string, JsonElement>>? OnClaimsObserved { get; init; }
    /// <summary>Nur Development-Anzeige: erhält Subject und das geprüfte ID-Token (Bearer-ähnlich, nie loggen oder speichern).</summary>
    public Action<string, string>? OnIdTokenValidated { get; init; }
    /// <summary>Diagnose: erhält nur einen kurzen Fehlercode (z. B. <c>token_http_400</c>, <c>aud</c>, <c>nonce</c>), nie Token, Code oder Antwortkörper.</summary>
    public Action<string>? OnFailure { get; init; }
}

/// <summary>
/// Authorization Code + PKCE gegen Entra External ID mit form_post. Prüft die ID-Token-Signatur (RS256 über JWKS), Issuer aus der Discovery,
/// Audience, exp/nbf, nonce und lehnt andere Algorithmen ab. Im PoC bewusst klein; in Learny ersetzt <c>OidcService</c> diese Klasse.
/// </summary>
public sealed class EntraOidcFlow : IOidcFlow
{
    private readonly HttpClient _http;
    private readonly EntraOidcOptions _o;
    private JsonElement _discovery;
    private bool _haveDiscovery;
    private Dictionary<string, RSAParameters> _keys = new();
    private DateTime _keysFetched = DateTime.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EntraOidcFlow(HttpClient http, EntraOidcOptions options)
    {
        _http = http;
        _o = options;
        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var a) || a.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Authority muss HTTPS verwenden.");
        if (!Uri.TryCreate(options.RedirectUri, UriKind.Absolute, out var r) || (r.Scheme != Uri.UriSchemeHttps && !r.IsLoopback))
            throw new InvalidOperationException("RedirectUri muss HTTPS verwenden (HTTP nur localhost).");
    }

    private string Authority => _o.Authority.TrimEnd('/');

    public string BuildAuthorizeUrl(string state, string nonce, string codeChallenge)
    {
        // Der Authorize-Endpunkt folgt aus der Authority; die Discovery wird für Token/JWKS/Issuer genutzt.
        var endpoint = Authority.Replace("/v2.0", "/oauth2/v2.0/authorize");
        var q = new Dictionary<string, string>
        {
            ["client_id"] = _o.ClientId, ["response_type"] = "code", ["response_mode"] = "form_post",
            ["redirect_uri"] = _o.RedirectUri, ["scope"] = _o.Scope, ["state"] = state, ["nonce"] = nonce,
            ["code_challenge"] = codeChallenge, ["code_challenge_method"] = "S256",
        };
        if (_o.ForceReauthentication) q["prompt"] = "login";
        return endpoint + "?" + string.Join("&", q.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
    }

    public async Task<VerifiedIdentity?> ExchangeAndValidateAsync(string code, string codeVerifier, string nonce, CancellationToken ct)
    {
        try
        {
            var disc = await GetDiscoveryAsync(ct);
            using var res = await _http.PostAsync(disc.GetProperty("token_endpoint").GetString(), new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _o.ClientId, ["client_secret"] = _o.ClientSecret, ["grant_type"] = "authorization_code",
                ["code"] = code, ["redirect_uri"] = _o.RedirectUri, ["code_verifier"] = codeVerifier, ["scope"] = _o.Scope,
            }), ct);
            if (!res.IsSuccessStatusCode) return Fail("token_http_" + (int)res.StatusCode + await ErrorCodeAsync(res, ct));   // vom Körper nur die numerische AADSTS-Nummer

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("id_token", out var idt) || idt.GetString() is not { } jwt) return Fail("no_id_token");
            return await ValidateIdTokenAsync(jwt, nonce, ct);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OperationCanceledException)
        {
            return Fail("exchange_error");
        }
    }

    /// <summary>Öffentlich für Tests: prüft ein ID-Token und liefert die Identität oder null.</summary>
    public async Task<VerifiedIdentity?> ValidateIdTokenAsync(string jwt, string expectedNonce, CancellationToken ct = default)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return Fail("jwt_format");
        using var header = JsonDocument.Parse(B64(parts[0]));
        if (header.RootElement.GetProperty("alg").GetString() != "RS256") return Fail("alg");   // kein none/HS256
        var kid = header.RootElement.TryGetProperty("kid", out var k) ? k.GetString() : null;
        if (kid is null) return Fail("kid_missing");

        var key = await GetKeyAsync(kid, ct);
        if (key is null) return Fail("kid_unknown");
        using var rsa = RSA.Create();
        rsa.ImportParameters(key.Value);
        if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), B64Bytes(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return Fail("signature");

        using var payloadDoc = JsonDocument.Parse(B64(parts[1]));
        var claims = payloadDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var disc = await GetDiscoveryAsync(ct);
        var now = _o.Clock();

        if (Str(claims, "iss") != disc.GetProperty("issuer").GetString()) return Fail("iss");
        if (!AudienceMatches(claims)) return Fail("aud");
        if (_o.ExpectedTenantId is { } expectedTid && Str(claims, "tid") != expectedTid) return Fail("tid");
        if (!claims.TryGetValue("exp", out var exp) || exp.ValueKind != JsonValueKind.Number) return Fail("exp_missing");   // exp ist Pflicht
        if (DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()).UtcDateTime + _o.ClockSkew < now) return Fail("expired");
        if (claims.TryGetValue("nbf", out var nbf) && nbf.ValueKind == JsonValueKind.Number
            && DateTimeOffset.FromUnixTimeSeconds(nbf.GetInt64()).UtcDateTime - _o.ClockSkew > now) return Fail("nbf");
        if (Str(claims, "nonce") is not { } n || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(n), Encoding.UTF8.GetBytes(expectedNonce))) return Fail("nonce");
        var sub = Str(claims, "sub");
        if (string.IsNullOrEmpty(sub)) return Fail("sub_missing");

        _o.OnClaimsObserved?.Invoke(claims);
        _o.OnIdTokenValidated?.Invoke(sub!, jwt);
        var email = Str(claims, "email") ?? FirstOf(claims, "emails") ?? (Str(claims, "preferred_username") is { } pu && pu.Contains('@') ? pu : null);
        var verified = claims.TryGetValue("email_verified", out var ev)
            ? ev.ValueKind == JsonValueKind.True || (ev.ValueKind == JsonValueKind.String && ev.GetString() == "true")
            : _o.EmailVerifiedFallback?.Invoke(claims) ?? false;
        var roles = claims.TryGetValue("roles", out var rl) && rl.ValueKind == JsonValueKind.Array
            ? rl.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : new List<string>();
        return new VerifiedIdentity(Str(claims, "iss")!, sub, email, verified, roles, Hint(claims, email), Guid.TryParse(Str(claims, "oid"), out var oidGuid) ? oidGuid.ToString() : null);
    }

    /// <summary>Kurzform für das Ereignisprotokoll: nur Anbieter (idp), Tokenversion und ob eine E-Mail vorhanden ist. Werte werden bereinigt und gekürzt.</summary>
    private static string Hint(Dictionary<string, JsonElement> claims, string? email)
    {
        static string Safe(string? v) => string.IsNullOrEmpty(v) ? "none" : new string(v.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(14).ToArray()) is { Length: > 0 } t ? t : "other";
        string? Get(string n) => claims.TryGetValue(n, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        var ver = Get("ver"); var idp = Get("idp");
        if (idp is null && claims.ContainsKey("idp")) idp = "nonstring";   // Claim vorhanden, aber kein Text
        // Eine Tenant-Domain als Anbieter (lokales Konto) wird nur als „tenant“ geführt, nie als Name.
        if (idp is not null && idp.Contains('.') && idp.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase)) idp = "tenant";
        return $"idp-{Safe(idp)}.ver-{Safe(ver?.Replace('.', '-'))}.em-{(string.IsNullOrWhiteSpace(email) ? 0 : 1)}.ev-{(claims.TryGetValue("email_verified", out var evc) ? (evc.ValueKind == JsonValueKind.True ? "t" : evc.ValueKind == JsonValueKind.False ? "f" : "x") : "n")}";
    }

    /// <summary>Nur die numerische Fehlernummer (error_codes[0], z. B. 501461) aus der Fehlerantwort; nie Text, Tokens oder Details.</summary>
    private static async Task<string> ErrorCodeAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            using var d = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (d.RootElement.TryGetProperty("error_codes", out var ec) && ec.ValueKind == JsonValueKind.Array && ec.GetArrayLength() > 0 && ec[0].TryGetInt32(out var n)) return "_aadsts" + n;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        return "";
    }

    private VerifiedIdentity? Fail(string code) { _o.OnFailure?.Invoke(code); return null; }

    private bool AudienceMatches(Dictionary<string, JsonElement> claims)
    {
        if (!claims.TryGetValue("aud", out var aud)) return false;
        return aud.ValueKind == JsonValueKind.String ? aud.GetString() == _o.ClientId
            : aud.ValueKind == JsonValueKind.Array && aud.EnumerateArray().Any(a => a.GetString() == _o.ClientId) && aud.GetArrayLength() == 1;
    }

    private async Task<JsonElement> GetDiscoveryAsync(CancellationToken ct)
    {
        if (_haveDiscovery) return _discovery;
        await _gate.WaitAsync(ct);
        try
        {
            if (_haveDiscovery) return _discovery;
            var text = await _http.GetStringAsync(Authority + "/.well-known/openid-configuration", ct);
            using var d = JsonDocument.Parse(text);
            _discovery = d.RootElement.Clone();
            _haveDiscovery = true;
            return _discovery;
        }
        finally { _gate.Release(); }
    }

    private async Task<RSAParameters?> GetKeyAsync(string kid, CancellationToken ct)
    {
        if (_keys.TryGetValue(kid, out var cached)) return cached;
        // Unbekannte kid: JWKS höchstens einmal pro Minute neu laden (Schlüsselrotation, kein Abruf-Missbrauch).
        if (_o.Clock() - _keysFetched < TimeSpan.FromMinutes(1) && _keys.Count > 0) return null;
        var disc = await GetDiscoveryAsync(ct);
        using var jwks = JsonDocument.Parse(await _http.GetStringAsync(disc.GetProperty("jwks_uri").GetString(), ct));
        var fresh = new Dictionary<string, RSAParameters>();
        foreach (var jwk in jwks.RootElement.GetProperty("keys").EnumerateArray())
        {
            if (jwk.TryGetProperty("kty", out var kt) && kt.GetString() == "RSA" && jwk.TryGetProperty("kid", out var id) && id.GetString() is { } kk
                && jwk.TryGetProperty("n", out var n) && jwk.TryGetProperty("e", out var e))
                fresh[kk] = new RSAParameters { Modulus = B64Bytes(n.GetString()!), Exponent = B64Bytes(e.GetString()!) };
        }
        _keys = fresh; _keysFetched = _o.Clock();
        return _keys.TryGetValue(kid, out var found) ? found : null;
    }

    private static string? Str(Dictionary<string, JsonElement> c, string name) => c.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string? FirstOf(Dictionary<string, JsonElement> c, string name) =>
        c.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 ? v[0].GetString() : null;
    private static byte[] B64Bytes(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }
    private static string B64(string s) => Encoding.UTF8.GetString(B64Bytes(s));
}

/// <summary>Richtlinien für das Signal "E-Mail bestätigt", wenn das Token kein <c>email_verified</c> trägt (am echten External-ID-Token geprüft: idp=mail, kein email_verified).</summary>
public static class EmailVerificationPolicies
{
    /// <summary>Nur die Anmeldung per E-Mail-Einmalcode (<c>idp = "mail"</c>, Tokenversion 2.0) beweist die Kontrolle über das Postfach. Alle anderen Anbieter (Google, Microsoft, Föderation) gelten nicht als bestätigt.</summary>
    public static bool OtpIdpOnly(IReadOnlyDictionary<string, JsonElement> claims) =>
        claims.TryGetValue("idp", out var idp) && idp.ValueKind == JsonValueKind.String && idp.GetString() == "mail"
        && claims.TryGetValue("ver", out var ver) && ver.ValueKind == JsonValueKind.String && ver.GetString() == "2.0"
        && claims.TryGetValue("email", out var e) && e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString());

    /// <summary>
    /// Wie <see cref="OtpIdpOnly"/>, zusätzlich lokale Konten des External Tenants: Das Token trägt dann <b>keinen</b> <c>idp</c>-Claim (am echten Token beobachtet: idp fehlt, ver = 2.0, email vorhanden).
    /// Entra bestätigt die Adresse bei der Registrierung eines lokalen Kontos per Einmalcode. Föderierte Anbieter (Google, Microsoft, OIDC) tragen immer einen <c>idp</c> und bleiben gesperrt.
    /// Der Tenant ist über <c>ExpectedTenantId</c> bereits gebunden; die Adresse muss weiterhin exakt zur Einladung passen.
    /// </summary>
    public static bool OtpOrLocalAccount(IReadOnlyDictionary<string, JsonElement> claims) =>
        OtpIdpOnly(claims)
        || (!claims.ContainsKey("idp")
            && claims.TryGetValue("ver", out var ver) && ver.ValueKind == JsonValueKind.String && ver.GetString() == "2.0"
            && HasEmail(claims));

    /// <summary>Gleiche Herkunft wie die Identität: Claim <c>email</c>, sonst erster Eintrag von <c>emails</c>, sonst <c>preferred_username</c> mit @ (lokale Konten tragen die Adresse oft nur dort).</summary>
    private static bool HasEmail(IReadOnlyDictionary<string, JsonElement> c)
    {
        static bool Text(JsonElement v) => v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString());
        if (c.TryGetValue("email", out var e) && Text(e)) return true;
        if (c.TryGetValue("emails", out var es) && es.ValueKind == JsonValueKind.Array && es.EnumerateArray().Any(Text)) return true;
        return c.TryGetValue("preferred_username", out var pu) && Text(pu) && pu.GetString()!.Contains('@');
    }
}

