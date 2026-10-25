using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LearnCards.McpServer;

/// <summary>
/// OAuth-Autorisierungsserver des MCP-Servers (ADR 0013). Authorization Code + PKCE (S256) mit dynamischer Client-Registrierung.
/// Die <b>Benutzeranmeldung und Zustimmung findet bei Learny statt</b> (Entra ID): Dieser Dienst authentifiziert niemanden selbst,
/// sondern leitet an die Learny-Zustimmungsseite weiter und erhält von dort einen einmaligen Code, den er serverseitig gegen ein
/// benutzergebundenes Learny-Token tauscht. Dieses Token ist zugleich das Access Token des MCP-Clients — der MCP-Server hält
/// keinen Benutzerzustand, Widerruf und Ablauf entscheidet die Learny-API.
/// </summary>
public sealed class ProprietaryOAuthService
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    private static readonly string[] ForbiddenRedirectSchemes = { "javascript", "data", "file", "vbscript", "about", "blob" };

    private readonly ProprietaryOAuthSettings _settings;
    private readonly ConcurrentDictionary<string, OAuthClientRegistration> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingAuthorization> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OAuthAuthorizationCode> _authorizationCodes = new(StringComparer.Ordinal);

    public ProprietaryOAuthService(ProprietaryOAuthSettings settings)
    {
        _settings = settings;
        foreach (var client in settings.Clients.Where(c => !string.IsNullOrWhiteSpace(c.ClientId)))
        {
            var registration = new OAuthClientRegistration(
                client.ClientId,
                client.ClientSecret,
                client.Scopes.Count == 0 ? new[] { "mcp" } : client.Scopes.ToArray(),
                client.RedirectUris.Count == 0 ? Array.Empty<string>() : client.RedirectUris.ToArray(),
                string.IsNullOrWhiteSpace(client.TokenEndpointAuthMethod)
                    ? (string.IsNullOrWhiteSpace(client.ClientSecret) ? "none" : "client_secret_post")
                    : client.TokenEndpointAuthMethod,
                client.ApplicationType,
                DateTimeOffset.UtcNow,
                false,
                client.ClientName);
            _clients[registration.ClientId] = registration;
        }
    }

    public bool IsEnabled => _settings.Enabled;

    /// <summary>Übernimmt einen aus Learny nachgeladenen Client (ADR 0025); bereits bekannte Clients bleiben unverändert.</summary>
    public void Adopt(OAuthClientRegistration client) => _clients.TryAdd(client.ClientId, client);

    /// <summary>
    /// Lädt einen unbekannten dynamisch registrierten Client aus Learny nach (nach einem Neustart des MCP-Servers). Nur Clients dieses Dienstes
    /// (Präfix "learncards-") werden angefragt.
    /// </summary>
    public async Task EnsureClientAsync(string? clientId, LearnCardsApiClient api, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 100 || !clientId.StartsWith("learncards-", StringComparison.Ordinal) || FindClient(clientId) is not null) return;
        if (await api.LoadOAuthClientAsync(clientId, ct) is { } loaded) Adopt(loaded);
    }

    public OAuthClientRegistration? FindClient(string clientId)
        => _clients.TryGetValue(clientId, out var client) ? client : null;

    public OAuthClientRegistration RegisterClient(
        string? clientName,
        IReadOnlyList<string> redirectUris,
        string tokenEndpointAuthMethod,
        IReadOnlyList<string> scopes,
        string applicationType)
    {
        if (tokenEndpointAuthMethod is not ("none" or "client_secret_post"))
            throw new InvalidOperationException("invalid_client_metadata");

        var normalizedRedirectUris = redirectUris
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedRedirectUris.Length == 0 || normalizedRedirectUris.Any(u => !IsAcceptableRedirectUri(u)))
            throw new InvalidOperationException("invalid_redirect_uri");

        var allowedScopes = new[] { "mcp" };   // einziger Scope; unbekannte angefragte Scopes werden ignoriert

        var clientId = "learncards-" + Guid.NewGuid().ToString("N");
        var clientSecret = tokenEndpointAuthMethod == "client_secret_post" ? Base64UrlEncode(RandomNumberGenerator.GetBytes(32)) : "";
        var registration = new OAuthClientRegistration(
            clientId,
            clientSecret,
            allowedScopes,
            normalizedRedirectUris,
            tokenEndpointAuthMethod,
            string.IsNullOrWhiteSpace(applicationType) ? "web" : applicationType,
            DateTimeOffset.UtcNow,
            true,
            clientName is { Length: > 100 } ? clientName[..100] : clientName);
        _clients[clientId] = registration;
        return registration;
    }

    public OAuthAuthorizeRequest ValidateAuthorizationRequest(
        string clientId,
        string redirectUri,
        string? scope,
        string? state,
        string? codeChallenge,
        string? codeChallengeMethod,
        string? resource,
        string issuerBaseUrl)
    {
        var client = FindClient(clientId) ?? throw new InvalidOperationException("invalid_client");
        if (client.RedirectUris.Length == 0 || !client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
            throw new InvalidOperationException("invalid_redirect_uri");

        var requestedScopes = ResolveScopes(client, scope);
        if (requestedScopes.Any(s => !client.Scopes.Contains(s, StringComparer.Ordinal)))
            throw new InvalidOperationException("invalid_scope");

        var normalizedResource = NormalizeResource(resource);
        if (normalizedResource is not null && !normalizedResource.Equals(CanonicalResource(issuerBaseUrl), StringComparison.Ordinal))
            throw new InvalidOperationException("invalid_target");

        // PKCE ist Pflicht, ausschließlich S256 (kein "plain")
        if (string.IsNullOrWhiteSpace(codeChallenge))
            throw new InvalidOperationException("invalid_request");
        var method = string.IsNullOrWhiteSpace(codeChallengeMethod) ? "S256" : codeChallengeMethod;
        if (method != "S256")
            throw new InvalidOperationException("invalid_request");

        return new OAuthAuthorizeRequest(
            client,
            redirectUri,
            requestedScopes.ToArray(),
            state ?? "",
            codeChallenge,
            method,
            normalizedResource ?? CanonicalResource(issuerBaseUrl));
    }

    /// <summary>Merkt die geprüfte Anfrage, während der Benutzer bei Learny zustimmt. Die ID dient als "state" gegenüber Learny.</summary>
    public string CreatePending(OAuthAuthorizeRequest request)
    {
        Cleanup();
        var id = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        _pending[id] = new PendingAuthorization(request, DateTimeOffset.UtcNow.Add(PendingLifetime));
        return id;
    }

    /// <summary>Entnimmt (und löscht) die wartende Anfrage; null wenn unbekannt oder abgelaufen.</summary>
    public OAuthAuthorizeRequest? TakePending(string id)
        => !string.IsNullOrEmpty(id) && _pending.TryRemove(id, out var p) && p.ExpiresAt > DateTimeOffset.UtcNow ? p.Request : null;

    /// <summary>Erzeugt den Autorisierungscode für den MCP-Client; er trägt das bereits getauschte Benutzer-Token.</summary>
    public string IssueAuthorizationCode(OAuthAuthorizeRequest request, string userToken, DateTimeOffset tokenExpiresAt)
    {
        var code = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        _authorizationCodes[code] = new OAuthAuthorizationCode(
            code,
            request.Client.ClientId,
            request.RedirectUri,
            request.Scopes,
            request.CodeChallenge,
            request.CodeChallengeMethod,
            request.Resource,
            userToken,
            tokenExpiresAt,
            DateTimeOffset.UtcNow.Add(CodeLifetime));
        return code;
    }

    public OAuthTokenResult ExchangeAuthorizationCode(
        string clientId,
        string? clientSecret,
        string code,
        string redirectUri,
        string? codeVerifier,
        string? resource,
        string issuerBaseUrl)
    {
        var client = ValidateClientAuthentication(clientId, clientSecret);
        if (!_authorizationCodes.TryRemove(code ?? "", out var authCode))   // einmalig: Entnahme macht den Code ungültig
            throw new InvalidOperationException("invalid_grant");

        if (!string.Equals(authCode.ClientId, client.ClientId, StringComparison.Ordinal)
            || !string.Equals(authCode.RedirectUri, redirectUri, StringComparison.Ordinal)
            || authCode.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("invalid_grant");

        ValidatePkce(authCode, codeVerifier);

        var requestedResource = NormalizeResource(resource);
        if (requestedResource is not null && !string.Equals(requestedResource, authCode.Resource, StringComparison.Ordinal))
            throw new InvalidOperationException("invalid_target");

        var seconds = (int)Math.Max(1, (authCode.UserTokenExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        return new OAuthTokenResult(authCode.UserToken, "Bearer", seconds, string.Join(' ', authCode.Scopes));
    }

    public JsonObject AuthorizationServerMetadata(string issuerBaseUrl) => new()
    {
        ["issuer"] = issuerBaseUrl.TrimEnd('/'),
        ["authorization_endpoint"] = issuerBaseUrl.TrimEnd('/') + "/oauth/authorize",
        ["token_endpoint"] = issuerBaseUrl.TrimEnd('/') + "/oauth/token",
        ["registration_endpoint"] = issuerBaseUrl.TrimEnd('/') + "/oauth/register",
        ["grant_types_supported"] = new JsonArray("authorization_code"),
        ["response_types_supported"] = new JsonArray("code"),
        ["response_modes_supported"] = new JsonArray("query"),
        ["code_challenge_methods_supported"] = new JsonArray("S256"),
        ["token_endpoint_auth_methods_supported"] = new JsonArray("client_secret_post", "none"),
        ["scopes_supported"] = new JsonArray("mcp"),
        ["service_documentation"] = issuerBaseUrl.TrimEnd('/') + "/metadata",
    };

    private OAuthClientRegistration ValidateClientAuthentication(string clientId, string? clientSecret)
    {
        var client = FindClient(clientId) ?? throw new InvalidOperationException("invalid_client");
        if (client.TokenEndpointAuthMethod == "none")
            return client;

        if (clientSecret is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(client.ClientSecret), Encoding.UTF8.GetBytes(clientSecret)))
            throw new InvalidOperationException("invalid_client");

        return client;
    }

    private static void ValidatePkce(OAuthAuthorizationCode authCode, string? codeVerifier)
    {
        if (string.IsNullOrWhiteSpace(codeVerifier))
            throw new InvalidOperationException("invalid_grant");

        var expected = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(authCode.CodeChallenge)))
            throw new InvalidOperationException("invalid_grant");
    }

    private static HashSet<string> ResolveScopes(OAuthClientRegistration client, string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return client.Scopes.ToHashSet(StringComparer.Ordinal);

        var requestedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        return requestedScopes.Count == 0 ? client.Scopes.ToHashSet(StringComparer.Ordinal) : requestedScopes;
    }

    /// <summary>https, Loopback-http (native Apps) oder benutzerdefiniertes Schema — nie javascript:/data:/file:.</summary>
    private static bool IsAcceptableRedirectUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || !string.IsNullOrEmpty(u.Fragment)) return false;
        if (ForbiddenRedirectSchemes.Contains(u.Scheme, StringComparer.OrdinalIgnoreCase)) return false;
        if (u.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) return u.IsLoopback;
        return true;
    }

    private void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _pending.Where(kv => kv.Value.ExpiresAt <= now).ToList()) _pending.TryRemove(kv.Key, out _);
        foreach (var kv in _authorizationCodes.Where(kv => kv.Value.ExpiresAt <= now).ToList()) _authorizationCodes.TryRemove(kv.Key, out _);
    }

    public static string CanonicalResource(string issuerBaseUrl) => issuerBaseUrl.TrimEnd('/') + "/mcp";

    private static string? NormalizeResource(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
            return null;

        if (!Uri.TryCreate(resource, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("invalid_target");

        return uri.ToString().TrimEnd('/');
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record OAuthClientRegistration(
    string ClientId,
    string ClientSecret,
    string[] Scopes,
    string[] RedirectUris,
    string TokenEndpointAuthMethod,
    string ApplicationType,
    DateTimeOffset IssuedAt,
    bool IsDynamic,
    string? ClientName);

public sealed record OAuthAuthorizeRequest(
    OAuthClientRegistration Client,
    string RedirectUri,
    string[] Scopes,
    string State,
    string CodeChallenge,
    string CodeChallengeMethod,
    string Resource);

public sealed record PendingAuthorization(OAuthAuthorizeRequest Request, DateTimeOffset ExpiresAt);

public sealed record OAuthAuthorizationCode(
    string Code,
    string ClientId,
    string RedirectUri,
    string[] Scopes,
    string CodeChallenge,
    string CodeChallengeMethod,
    string Resource,
    string UserToken,
    DateTimeOffset UserTokenExpiresAt,
    DateTimeOffset ExpiresAt);

public readonly record struct OAuthTokenResult(string AccessToken, string TokenType, int ExpiresIn, string Scope);
