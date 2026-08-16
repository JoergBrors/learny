using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LearnCards.McpServer;

public sealed class ProprietaryOAuthService
{
    private readonly ProprietaryOAuthSettings _settings;
    private readonly ConcurrentDictionary<string, OAuthClientRegistration> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OAuthAuthorizationCode> _authorizationCodes = new(StringComparer.Ordinal);

    public ProprietaryOAuthService(ProprietaryOAuthSettings settings)
    {
        _settings = settings;
        foreach (var client in settings.Clients)
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

    public OAuthClientRegistration? FindClient(string clientId)
        => _clients.TryGetValue(clientId, out var client) ? client : null;

    public OAuthClientRegistration RegisterClient(
        string? clientName,
        IReadOnlyList<string> redirectUris,
        string tokenEndpointAuthMethod,
        IReadOnlyList<string> scopes,
        string applicationType)
    {
        if (redirectUris.Count == 0)
            throw new InvalidOperationException("invalid_redirect_uri");

        if (tokenEndpointAuthMethod is not ("none" or "client_secret_post"))
            throw new InvalidOperationException("invalid_client_metadata");

        var normalizedRedirectUris = redirectUris
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedRedirectUris.Length == 0)
            throw new InvalidOperationException("invalid_redirect_uri");

        var allowedScopes = scopes.Count == 0
            ? _settings.Clients.SelectMany(c => c.Scopes).Distinct(StringComparer.Ordinal).DefaultIfEmpty("mcp").ToArray()
            : scopes.Distinct(StringComparer.Ordinal).ToArray();

        var clientId = "learncards-" + Guid.NewGuid().ToString("N");
        var clientSecret = tokenEndpointAuthMethod == "client_secret_post"
            ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            : "";
        var registration = new OAuthClientRegistration(
            clientId,
            clientSecret,
            allowedScopes,
            normalizedRedirectUris,
            tokenEndpointAuthMethod,
            string.IsNullOrWhiteSpace(applicationType) ? "web" : applicationType,
            DateTimeOffset.UtcNow,
            true,
            clientName);
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

        var normalizedResource = NormalizeResource(resource, issuerBaseUrl);
        if (normalizedResource is not null && !normalizedResource.Equals(CanonicalResource(issuerBaseUrl), StringComparison.Ordinal))
            throw new InvalidOperationException("invalid_target");

        if (string.IsNullOrWhiteSpace(codeChallenge))
            throw new InvalidOperationException("invalid_request");

        var method = string.IsNullOrWhiteSpace(codeChallengeMethod) ? "S256" : codeChallengeMethod;
        if (method is not ("S256" or "plain"))
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

    public string IssueAuthorizationCode(OAuthAuthorizeRequest request)
    {
        var code = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        _authorizationCodes[code] = new OAuthAuthorizationCode(
            code,
            request.Client.ClientId,
            request.RedirectUri,
            request.Scopes,
            request.CodeChallenge,
            request.CodeChallengeMethod,
            request.Resource,
            now.AddMinutes(10));
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
        if (!_authorizationCodes.TryRemove(code, out var authCode))
            throw new InvalidOperationException("invalid_grant");

        if (!string.Equals(authCode.ClientId, client.ClientId, StringComparison.Ordinal))
            throw new InvalidOperationException("invalid_grant");
        if (!string.Equals(authCode.RedirectUri, redirectUri, StringComparison.Ordinal))
            throw new InvalidOperationException("invalid_grant");
        if (authCode.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("invalid_grant");

        ValidatePkce(authCode, codeVerifier);

        var requestedResource = NormalizeResource(resource, issuerBaseUrl);
        if (requestedResource is not null && !string.Equals(requestedResource, authCode.Resource, StringComparison.Ordinal))
            throw new InvalidOperationException("invalid_target");

        return IssueAccessToken(client.ClientId, authCode.Scopes, authCode.Resource);
    }

    public OAuthTokenResult IssueClientCredentialsToken(string clientId, string clientSecret, string? scope)
    {
        var client = ValidateClientAuthentication(clientId, clientSecret);
        var requestedScopes = ResolveScopes(client, scope);
        if (requestedScopes.Count == 0)
            requestedScopes = client.Scopes.ToHashSet(StringComparer.Ordinal);

        if (requestedScopes.Any(s => !client.Scopes.Contains(s, StringComparer.Ordinal)))
            throw new InvalidOperationException("invalid_scope");

        return IssueAccessToken(client.ClientId, requestedScopes, CanonicalResource(_settings.Issuer));
    }

    public bool TryValidateBearerToken(string token, out OAuthPrincipal principal, out string error)
    {
        principal = default;
        error = "invalid_token";
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(_settings.SigningKey))
            return false;

        var parts = token.Split('.');
        if (parts.Length != 3)
            return false;

        var signingInput = $"{parts[0]}.{parts[1]}";
        var expected = Base64UrlEncode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_settings.SigningKey), Encoding.ASCII.GetBytes(signingInput)));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[2]), Encoding.ASCII.GetBytes(expected)))
            return false;

        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(Base64UrlDecodeToString(parts[1]))?.AsObject();
        }
        catch
        {
            return false;
        }

        if (payload is null)
            return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (payload["iss"]?.GetValue<string>() != _settings.Issuer) return false;
        if (payload["aud"]?.GetValue<string>() != _settings.Audience) return false;
        if (payload["exp"]?.GetValue<long>() < now) return false;
        if (payload["nbf"]?.GetValue<long>() > now) return false;

        principal = new OAuthPrincipal(
            payload["client_id"]?.GetValue<string>() ?? payload["sub"]?.GetValue<string>() ?? "",
            payload["scope"]?.GetValue<string>() ?? "",
            payload["resource"]?.GetValue<string>() ?? "");
        return principal.ClientId.Length > 0;
    }

    public JsonObject AuthorizationServerMetadata(string issuerBaseUrl) => new()
    {
        ["issuer"] = issuerBaseUrl.TrimEnd('/'),
        ["authorization_endpoint"] = issuerBaseUrl.TrimEnd('/') + "/oauth/authorize",
        ["token_endpoint"] = issuerBaseUrl.TrimEnd('/') + "/oauth/token",
        ["registration_endpoint"] = issuerBaseUrl.TrimEnd('/') + "/oauth/register",
        ["grant_types_supported"] = new JsonArray("authorization_code", "client_credentials"),
        ["response_types_supported"] = new JsonArray("code"),
        ["code_challenge_methods_supported"] = new JsonArray("S256", "plain"),
        ["token_endpoint_auth_methods_supported"] = new JsonArray("client_secret_post", "none"),
        ["scopes_supported"] = new JsonArray(AllScopes().Select(s => (JsonNode)s).ToArray()),
        ["service_documentation"] = issuerBaseUrl.TrimEnd('/') + "/metadata",
    };

    private OAuthClientRegistration ValidateClientAuthentication(string clientId, string? clientSecret)
    {
        var client = FindClient(clientId);
        if (client is null)
            throw new InvalidOperationException("invalid_client");

        if (client.TokenEndpointAuthMethod == "none")
            return client;

        if (client.ClientSecret != clientSecret)
            throw new InvalidOperationException("invalid_client");

        return client;
    }

    private OAuthTokenResult IssueAccessToken(string clientId, IEnumerable<string> scopes, string resource)
    {
        var scopeText = string.Join(' ', scopes.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal));
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(Math.Max(_settings.TokenLifetimeMinutes, 1));
        var payload = new JsonObject
        {
            ["iss"] = _settings.Issuer,
            ["aud"] = _settings.Audience,
            ["sub"] = clientId,
            ["client_id"] = clientId,
            ["scope"] = scopeText,
            ["resource"] = resource,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };

        return new OAuthTokenResult(
            CreateJwt(payload),
            "Bearer",
            (int)(expires - now).TotalSeconds,
            payload["scope"]!.GetValue<string>());
    }

    private static void ValidatePkce(OAuthAuthorizationCode authCode, string? codeVerifier)
    {
        if (string.IsNullOrWhiteSpace(codeVerifier))
            throw new InvalidOperationException("invalid_grant");

        var expected = authCode.CodeChallengeMethod switch
        {
            "plain" => codeVerifier,
            _ => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier))),
        };

        if (!string.Equals(expected, authCode.CodeChallenge, StringComparison.Ordinal))
            throw new InvalidOperationException("invalid_grant");
    }

    private HashSet<string> ResolveScopes(OAuthClientRegistration client, string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return client.Scopes.ToHashSet(StringComparer.Ordinal);

        var requestedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (requestedScopes.Count == 0)
            requestedScopes = client.Scopes.ToHashSet(StringComparer.Ordinal);
        return requestedScopes;
    }

    private IEnumerable<string> AllScopes()
        => _clients.Values.SelectMany(c => c.Scopes).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal);

    private string CreateJwt(JsonObject payload)
    {
        var header = new JsonObject
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT",
        };

        var encodedHeader = Base64UrlEncode(Encoding.UTF8.GetBytes(header.ToJsonString()));
        var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var signingInput = $"{encodedHeader}.{encodedPayload}";
        var signature = Base64UrlEncode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_settings.SigningKey), Encoding.ASCII.GetBytes(signingInput)));
        return $"{signingInput}.{signature}";
    }

    public static string CanonicalResource(string issuerBaseUrl) => issuerBaseUrl.TrimEnd('/') + "/mcp";

    private static string? NormalizeResource(string? resource, string issuerBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(resource))
            return null;

        if (!Uri.TryCreate(resource, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("invalid_target");

        return uri.ToString().TrimEnd('/');
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Base64UrlDecodeToString(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => "",
        };
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
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

public sealed record OAuthAuthorizationCode(
    string Code,
    string ClientId,
    string RedirectUri,
    string[] Scopes,
    string CodeChallenge,
    string CodeChallengeMethod,
    string Resource,
    DateTimeOffset ExpiresAt);

public readonly record struct OAuthTokenResult(string AccessToken, string TokenType, int ExpiresIn, string Scope);

public readonly record struct OAuthPrincipal(string ClientId, string Scope, string Resource);
