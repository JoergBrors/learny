using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LearnCards.McpServer;

/// <summary>Dünner HTTP-Client für die LearnCards-API, authentifiziert per X-MCP-Key.</summary>
public sealed class LearnCardsApiClient : IDisposable
{
    private readonly HttpClient _http;

    public LearnCardsApiClient(string baseUrl, string apiKey)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("X-MCP-Key", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<JsonNode?> PostAsync(string path, JsonNode body, CancellationToken ct)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await _http.PostAsync(path, content, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new McpApiException((int)res.StatusCode, text);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    public async Task<JsonNode?> PatchAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, path);
        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new McpApiException((int)res.StatusCode, text);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    public async Task<JsonNode?> GetAsync(string path, CancellationToken ct)
    {
        using var res = await _http.GetAsync(path, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new McpApiException((int)res.StatusCode, text);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    public void Dispose() => _http.Dispose();
}

public sealed class McpApiException(int statusCode, string body) : Exception($"API-Fehler {statusCode}: {body}")
{
    public int StatusCode { get; } = statusCode;
}
