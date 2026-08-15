using System.Text;
using System.Text.Json;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services;

/// <summary>
/// Schlanker OpenAI-Client (Chat Completions) auf HttpClient-Basis — ohne SDK-Abhängigkeit.
/// Über OPENAI_BASE_URL auch mit Azure-OpenAI-kompatiblen Endpunkten oder Test-Mocks nutzbar.
/// </summary>
public class OpenAiClient
{
    private readonly HttpClient _http;
    private readonly AppConfig _cfg;

    public OpenAiClient(AppConfig cfg, IHttpClientFactory factory)
    {
        _cfg = cfg;
        _http = factory.CreateClient("openai");
        _http.BaseAddress = new Uri(cfg.OpenAiBaseUrl + "/");
        _http.Timeout = TimeSpan.FromSeconds(120);
        if (cfg.OpenAiConfigured)
            _http.DefaultRequestHeaders.Authorization = new("Bearer", cfg.OpenAiApiKey);
    }

    public bool IsConfigured => _cfg.OpenAiConfigured;

    /// <summary>Eine Completion mit erzwungenem JSON-Objekt als Antwort.</summary>
    public async Task<JsonDocument> CompleteJsonAsync(string userPrompt, int maxTokens, CancellationToken ct = default)
    {
        var payload = new
        {
            model = _cfg.OpenAiModel,
            messages = new[] { new { role = "user", content = userPrompt } },
            response_format = new { type = "json_object" },
            max_tokens = maxTokens,
        };
        using var res = await _http.PostAsync("chat/completions",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI-Fehler {(int)res.StatusCode}: {Truncate(body, 400)}");

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
        return JsonDocument.Parse(content);
    }

    /// <summary>Streamt Antwort-Tokens für den Karten-Chat.</summary>
    public async IAsyncEnumerable<string> StreamChatAsync(
        string systemPrompt,
        IEnumerable<(string Role, string Content)> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var allMessages = new List<object> { new { role = "system", content = systemPrompt } };
        allMessages.AddRange(messages.Select(m => (object)new { role = m.Role, content = m.Content }));

        var payload = new
        {
            model = _cfg.OpenAiModel,
            messages = allMessages,
            max_tokens = 800,
            stream = true,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"OpenAI-Fehler {(int)res.StatusCode}: {Truncate(err, 400)}");
        }

        using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;

            string? delta = null;
            try
            {
                using var chunk = JsonDocument.Parse(data);
                if (chunk.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("delta", out var d)
                    && d.TryGetProperty("content", out var c))
                    delta = c.GetString();
            }
            catch (JsonException) { /* unvollständige Chunks ignorieren */ }
            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }

    private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "…";
}
