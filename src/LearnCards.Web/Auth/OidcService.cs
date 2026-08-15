using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Auth;

/// <summary>
/// OpenID-Connect-Client (Authorization Code Flow + PKCE, Confidential Client) —
/// implementiert direkt gegen Discovery/Token/JWKS-Endpunkte, ohne NuGet-Abhängigkeit.
/// Funktioniert mit Entra ID und jedem standardkonformen OIDC-Provider.
/// </summary>
public class OidcService
{
    private readonly AppConfig _cfg;
    private readonly IHttpClientFactory _httpFactory;
    private DiscoveryDocument? _discovery;
    private JsonDocument? _jwks;
    private DateTime _jwksLoaded = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public OidcService(AppConfig cfg, IHttpClientFactory httpFactory)
    {
        _cfg = cfg;
        _httpFactory = httpFactory;
    }

    public record DiscoveryDocument(
        string Issuer,
        string AuthorizationEndpoint,
        string TokenEndpoint,
        string JwksUri,
        string? EndSessionEndpoint);

    public async Task<DiscoveryDocument> GetDiscoveryAsync(CancellationToken ct = default)
    {
        if (_discovery is not null) return _discovery;
        await _lock.WaitAsync(ct);
        try
        {
            if (_discovery is not null) return _discovery;
            var http = _httpFactory.CreateClient("oidc");
            var url = _cfg.OidcIssuer + "/.well-known/openid-configuration";
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
            var root = doc.RootElement;
            _discovery = new DiscoveryDocument(
                root.GetProperty("issuer").GetString()!,
                root.GetProperty("authorization_endpoint").GetString()!,
                root.GetProperty("token_endpoint").GetString()!,
                root.GetProperty("jwks_uri").GetString()!,
                root.TryGetProperty("end_session_endpoint", out var es) ? es.GetString() : null);
            return _discovery;
        }
        finally { _lock.Release(); }
    }

    public async Task<JsonDocument> GetJwksAsync(CancellationToken ct = default)
    {
        if (_jwks is not null && DateTime.UtcNow - _jwksLoaded < TimeSpan.FromHours(12)) return _jwks;
        var disco = await GetDiscoveryAsync(ct);
        var http = _httpFactory.CreateClient("oidc");
        var fresh = JsonDocument.Parse(await http.GetStringAsync(disco.JwksUri, ct));
        await _lock.WaitAsync(ct);
        try { _jwks?.Dispose(); _jwks = fresh; _jwksLoaded = DateTime.UtcNow; }
        finally { _lock.Release(); }
        return fresh;
    }

    public void InvalidateJwks() => _jwksLoaded = DateTime.MinValue;

    // ─── Authorization Request ──────────────────────────────────────────────

    public record AuthState(string State, string Nonce, string CodeVerifier, string ReturnUrl);

    public static AuthState CreateState(string returnUrl) => new(
        State: RandomToken(24),
        Nonce: RandomToken(24),
        CodeVerifier: RandomToken(48),
        ReturnUrl: returnUrl);

    public async Task<string> BuildAuthorizeUrlAsync(AuthState state, string redirectUri, CancellationToken ct = default)
    {
        var disco = await GetDiscoveryAsync(ct);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(state.CodeVerifier)));
        var query = new Dictionary<string, string>
        {
            ["client_id"] = _cfg.OidcClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = _cfg.OidcScope,
            ["state"] = state.State,
            ["nonce"] = state.Nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        return disco.AuthorizationEndpoint + "?" +
               string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    // ─── Token Exchange ─────────────────────────────────────────────────────

    public async Task<(string IdToken, string? AccessToken)> ExchangeCodeAsync(
        string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        var disco = await GetDiscoveryAsync(ct);
        var http = _httpFactory.CreateClient("oidc");
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _cfg.OidcClientId,
            ["client_secret"] = _cfg.OidcClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        };
        using var res = await http.PostAsync(disco.TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token-Endpoint-Fehler {(int)res.StatusCode}: {body[..Math.Min(body.Length, 500)]}");

        using var doc = JsonDocument.Parse(body);
        var idToken = doc.RootElement.GetProperty("id_token").GetString()
            ?? throw new InvalidOperationException("Antwort enthält kein id_token.");
        var accessToken = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        return (idToken, accessToken);
    }

    // ─── ID-Token-Validierung (RS256 gegen JWKS) ────────────────────────────

    public async Task<Dictionary<string, JsonElement>> ValidateIdTokenAsync(string idToken, string expectedNonce, CancellationToken ct = default)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3) throw new InvalidOperationException("Ungültiges JWT-Format.");

        var header = JsonDocument.Parse(Base64UrlDecode(parts[0])).RootElement;
        var payloadBytes = Base64UrlDecode(parts[1]);
        var payload = JsonDocument.Parse(payloadBytes).RootElement;
        var signature = Base64UrlDecode(parts[2]);
        var signedData = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);

        var alg = header.TryGetProperty("alg", out var a) ? a.GetString() : null;
        if (alg != "RS256") throw new InvalidOperationException($"Nicht unterstützter Signatur-Algorithmus: {alg}");
        var kid = header.TryGetProperty("kid", out var k) ? k.GetString() : null;

        var valid = await VerifySignatureAsync(signedData, signature, kid, ct);
        if (!valid)
        {
            // Schlüsselrotation: JWKS neu laden und einmal wiederholen
            InvalidateJwks();
            valid = await VerifySignatureAsync(signedData, signature, kid, ct);
        }
        if (!valid) throw new InvalidOperationException("ID-Token-Signatur ungültig.");

        // Claims prüfen
        var disco = await GetDiscoveryAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const long skew = 300;

        var iss = payload.GetProperty("iss").GetString();
        var expectedIssuer = disco.Issuer;
        if (!string.Equals(iss?.TrimEnd('/'), expectedIssuer.TrimEnd('/'), StringComparison.Ordinal) &&
            !string.Equals(iss?.TrimEnd('/'), _cfg.OidcIssuer.TrimEnd('/'), StringComparison.Ordinal))
            throw new InvalidOperationException($"Issuer stimmt nicht: {iss}");

        var audOk = payload.TryGetProperty("aud", out var aud) && aud.ValueKind switch
        {
            JsonValueKind.String => aud.GetString() == _cfg.OidcClientId,
            JsonValueKind.Array => aud.EnumerateArray().Any(x => x.GetString() == _cfg.OidcClientId),
            _ => false,
        };
        if (!audOk) throw new InvalidOperationException("Audience stimmt nicht mit der Client-ID überein.");

        if (payload.TryGetProperty("exp", out var exp) && exp.GetInt64() + skew < now)
            throw new InvalidOperationException("ID-Token ist abgelaufen.");
        if (payload.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() - skew > now)
            throw new InvalidOperationException("ID-Token ist noch nicht gültig (nbf).");

        var nonce = payload.TryGetProperty("nonce", out var n) ? n.GetString() : null;
        if (nonce != expectedNonce) throw new InvalidOperationException("Nonce stimmt nicht — mögliche Replay-Attacke.");

        var claims = new Dictionary<string, JsonElement>();
        foreach (var prop in payload.EnumerateObject())
            claims[prop.Name] = prop.Value.Clone();
        return claims;
    }

    private async Task<bool> VerifySignatureAsync(byte[] signedData, byte[] signature, string? kid, CancellationToken ct)
    {
        var jwks = await GetJwksAsync(ct);
        var keys = jwks.RootElement.GetProperty("keys").EnumerateArray().ToList();
        var candidates = keys.Where(x => kid is null || (x.TryGetProperty("kid", out var kk) && kk.GetString() == kid)).ToList();
        if (candidates.Count == 0) candidates = keys;

        foreach (var key in candidates)
        {
            if (!key.TryGetProperty("n", out var nEl) || !key.TryGetProperty("e", out var eEl)) continue;
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = Base64UrlDecode(nEl.GetString()!),
                Exponent = Base64UrlDecode(eEl.GetString()!),
            });
            if (rsa.VerifyData(signedData, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return true;
        }
        return false;
    }

    // ─── Helfer ─────────────────────────────────────────────────────────────

    private static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        switch (b.Length % 4) { case 2: b += "=="; break; case 3: b += "="; break; }
        return Convert.FromBase64String(b);
    }
}
