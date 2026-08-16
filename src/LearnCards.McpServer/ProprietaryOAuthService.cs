using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LearnCards.McpServer;

public sealed class ProprietaryOAuthService(ProprietaryOAuthSettings settings)
{
    public bool IsEnabled => settings.Enabled;

    public OAuthTokenResult IssueClientCredentialsToken(string clientId, string clientSecret, string? scope)
    {
        var client = settings.Clients.FirstOrDefault(c => c.ClientId == clientId);
        if (client is null || client.ClientSecret != clientSecret)
            throw new InvalidOperationException("invalid_client");

        var requestedScopes = string.IsNullOrWhiteSpace(scope)
            ? new HashSet<string>(client.Scopes, StringComparer.Ordinal)
            : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);

        if (requestedScopes.Count == 0)
            requestedScopes = client.Scopes.ToHashSet(StringComparer.Ordinal);

        if (requestedScopes.Any(s => !client.Scopes.Contains(s, StringComparer.Ordinal)))
            throw new InvalidOperationException("invalid_scope");

        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(Math.Max(settings.TokenLifetimeMinutes, 1));
        var payload = new JsonObject
        {
            ["iss"] = settings.Issuer,
            ["aud"] = settings.Audience,
            ["sub"] = clientId,
            ["client_id"] = clientId,
            ["scope"] = string.Join(' ', requestedScopes.OrderBy(s => s, StringComparer.Ordinal)),
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

    public bool TryValidateBearerToken(string token, out OAuthPrincipal principal, out string error)
    {
        principal = default;
        error = "invalid_token";
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(settings.SigningKey))
            return false;

        var parts = token.Split('.');
        if (parts.Length != 3)
            return false;

        var signingInput = $"{parts[0]}.{parts[1]}";
        var expected = Base64UrlEncode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.SigningKey), Encoding.ASCII.GetBytes(signingInput)));
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
        if (payload["iss"]?.GetValue<string>() != settings.Issuer) return false;
        if (payload["aud"]?.GetValue<string>() != settings.Audience) return false;
        if (payload["exp"]?.GetValue<long>() < now) return false;
        if (payload["nbf"]?.GetValue<long>() > now) return false;

        principal = new OAuthPrincipal(
            payload["client_id"]?.GetValue<string>() ?? payload["sub"]?.GetValue<string>() ?? "",
            payload["scope"]?.GetValue<string>() ?? "");
        return principal.ClientId.Length > 0;
    }

    public JsonObject AuthorizationServerMetadata(string issuerBaseUrl) => new()
    {
        ["issuer"] = issuerBaseUrl.TrimEnd('/'),
        ["token_endpoint"] = issuerBaseUrl.TrimEnd('/') + "/oauth/token",
        ["grant_types_supported"] = new JsonArray("client_credentials"),
        ["token_endpoint_auth_methods_supported"] = new JsonArray("client_secret_post"),
        ["scopes_supported"] = new JsonArray(settings.Clients
            .SelectMany(c => c.Scopes)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .Select(s => (JsonNode)s)
            .ToArray()),
    };

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
        var signature = Base64UrlEncode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.SigningKey), Encoding.ASCII.GetBytes(signingInput)));
        return $"{signingInput}.{signature}";
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

public readonly record struct OAuthTokenResult(string AccessToken, string TokenType, int ExpiresIn, string Scope);

public readonly record struct OAuthPrincipal(string ClientId, string Scope);
