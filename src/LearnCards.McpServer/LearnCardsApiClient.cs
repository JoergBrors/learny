using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LearnCards.McpServer;

/// <summary>
/// Benutzerkontext der aktuellen HTTP-Anfrage (ADR 0013): das Learny-Token des angemeldeten Benutzers.
/// Gesetzt vom /mcp-Endpunkt; bei stdio nicht gesetzt (dann gilt das konfigurierte persönliche Token).
/// </summary>
public static class McpUserContext
{
    private static readonly AsyncLocal<string?> _httpToken = new();
    private static readonly AsyncLocal<bool> _isHttp = new();

    public static bool IsHttpRequest => _isHttp.Value;
    public static string? HttpToken => _httpToken.Value;

    public static void SetHttpRequest(string token)
    {
        _isHttp.Value = true;
        _httpToken.Value = token;
    }
}

/// <summary>
/// Dünner HTTP-Client für die LearnCards-API. Alle Daten-Aufrufe laufen mit dem Token des Benutzers
/// (HTTP: aus dem Authorization-Header der Anfrage, stdio: aus LEARNCARDS_MCP_TOKEN). Der gemeinsame Dienstschlüssel (X-MCP-Key)
/// dient nur dem Tausch von Anmelde-Codes und dem Abruf des öffentlichen Schemas und gewährt keinen Datenzugriff.
/// </summary>
public sealed class LearnCardsApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _serviceKey;
    private readonly string _stdioToken;

    public LearnCardsApiClient(string baseUrl, string serviceKey, string stdioToken)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _serviceKey = serviceKey ?? "";
        _stdioToken = stdioToken ?? "";
    }

    public Task<JsonNode?> PostAsync(string path, JsonNode body, CancellationToken ct) => SendAsync(HttpMethod.Post, path, body, ct);

    public Task<JsonNode?> PatchAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Patch, path, null, ct);

    public Task<JsonNode?> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);

    /// <summary>Prüft ein Benutzer-Token gegen die API (401 → McpApiException).</summary>
    public Task<JsonNode?> WhoAmIAsync(string token, CancellationToken ct) => SendAsync(HttpMethod.Get, "mcp/whoami", null, ct, token);

    /// <summary>Tauscht den Übergabecode der Learny-Zustimmung (Dienstschlüssel) gegen ein benutzergebundenes Token.</summary>
    public async Task<JsonNode?> ExchangeLinkCodeAsync(string code, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "mcp/link/exchange")
        {
            Content = new StringContent(new JsonObject { ["code"] = code }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (_serviceKey.Length > 0) req.Headers.Add("X-MCP-Key", _serviceKey);
        return await ReadAsync(await _http.SendAsync(req, ct), ct);
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct, string? explicitToken = null)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        var token = explicitToken ?? (McpUserContext.IsHttpRequest ? McpUserContext.HttpToken : _stdioToken);
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        else if (path.StartsWith("schema/", StringComparison.Ordinal) && _serviceKey.Length > 0)
            req.Headers.Add("X-MCP-Key", _serviceKey);   // öffentliches Schema ohne Benutzerbezug
        else
            throw new McpApiException(401, McpUserContext.IsHttpRequest
                ? "Kein Benutzer-Token in der Anfrage."
                : "Kein MCP-Token konfiguriert. Erzeuge im Learny-Profil ein persönliches Token und setze LEARNCARDS_MCP_TOKEN.");

        return await ReadAsync(await _http.SendAsync(req, ct), ct);
    }

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new McpApiException((int)res.StatusCode, text);
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
    }

    public void Dispose() => _http.Dispose();
}

public sealed class McpApiException(int statusCode, string body) : Exception($"API-Fehler {statusCode}: {body}")
{
    public int StatusCode { get; } = statusCode;
}
