using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Minimaler OpenAI-Mock (<c>POST /v1/chat/completions</c>) für die Quiz-Tests (ADR 0019): erzeugt Fragen zu den im Prompt genannten Karten
/// (inkl. ungültiger Fragen, die verworfen werden müssen) und bewertet Freitext. Zählt die Aufrufe.
/// </summary>
public sealed partial class MockOpenAi : IAsyncDisposable
{
    private readonly HttpListener _l = new();
    private int _counter;
    public int Port { get; }
    public int GenerateCalls, GradeCalls;
    /// <summary>ok | fail (HTTP 500) | dup (liefert dieselbe Frage doppelt)</summary>
    public volatile string Mode = "ok";
    public string BaseUrl => $"http://127.0.0.1:{Port}/v1";

    public MockOpenAi()
    {
        var t = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        t.Start(); Port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
        _l.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _l.Start();
        _ = Task.Run(async () =>
        {
            while (_l.IsListening)
            {
                HttpListenerContext c;
                try { c = await _l.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Handle(c));
            }
        });
    }

    [GeneratedRegex(@"Karten-ID: ([0-9a-fA-F-]{36})")] private static partial Regex CardIdRx();
    [GeneratedRegex(@"Erzeuge genau (\d+) Fragen")] private static partial Regex CountRx();
    [GeneratedRegex(@"^Frage \d+:", RegexOptions.Multiline)] private static partial Regex QaRx();

    private async Task Handle(HttpListenerContext c)
    {
        try
        {
            using var reader = new StreamReader(c.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var prompt = JsonDocument.Parse(body).RootElement.GetProperty("messages")[0].GetProperty("content").GetString() ?? "";
            if (Mode == "fail") { c.Response.StatusCode = 500; c.Response.Close(); return; }

            string content;
            if (prompt.Contains("Erzeuge genau")) { Interlocked.Increment(ref GenerateCalls); content = Generate(prompt); }
            else if (prompt.Contains("bewertest eine Prüfung")) { Interlocked.Increment(ref GradeCalls); content = Grade(prompt); }
            else content = "{}";

            var resp = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content } } } });
            var bytes = Encoding.UTF8.GetBytes(resp);
            c.Response.StatusCode = 200; c.Response.ContentType = "application/json"; c.Response.ContentLength64 = bytes.Length;
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }
        catch { try { c.Response.Abort(); } catch { } }
    }

    private string Generate(string prompt)
    {
        var cards = CardIdRx().Matches(prompt).Select(m => m.Groups[1].Value).Distinct().ToList();
        var count = int.Parse(CountRx().Match(prompt).Groups[1].Value);
        var types = new[] { "single_choice", "multiple_response", "free_text" }.Where(t => prompt.Contains("- " + t + ":")).ToList();
        var list = new List<object>();
        for (var i = 0; i < count && cards.Count > 0; i++)
        {
            var n = Interlocked.Increment(ref _counter);
            var type = types[i % types.Count];
            var card = cards[i % cards.Count];
            if (Mode == "dup") n = 1;
            list.Add(type switch
            {
                "single_choice" => new { type, question = $"KI-Frage {n} (Single)?", options = new[] { "a", "b", "c", "d" }, correct_indexes = new[] { 1 }, explanation = "b ist richtig", source_card_id = card },
                "multiple_response" => new { type, question = $"KI-Frage {n} (Multi)?", options = new[] { "a", "b", "c", "d" }, correct_indexes = new[] { 0, 2 }, explanation = "a und c", source_card_id = card },
                _ => new { type, question = $"KI-Frage {n} (Frei)?", options = Array.Empty<string>(), correct_indexes = Array.Empty<int>(), explanation = "Musterlösung " + n, source_card_id = card },
            });
        }
        if (cards.Count > 0 && Mode != "fail")
        {
            // Ungültiges, das die Anwendung verwerfen muss
            list.Add(new { type = "essay", question = "Unbekannter Typ?", options = Array.Empty<string>(), correct_indexes = Array.Empty<int>(), explanation = "x", source_card_id = cards[0] });
            list.Add(new { type = "single_choice", question = "Fremde Karte?", options = new[] { "a", "b", "c", "d" }, correct_indexes = new[] { 0 }, explanation = "x", source_card_id = Guid.NewGuid().ToString() });
            list.Add(new { type = "single_choice", question = "Nur drei Optionen?", options = new[] { "a", "b", "c" }, correct_indexes = new[] { 0 }, explanation = "x", source_card_id = cards[0] });
            list.Add(new { type = "single_choice", question = "Index außerhalb?", options = new[] { "a", "b", "c", "d" }, correct_indexes = new[] { 9 }, explanation = "x", source_card_id = cards[0] });
        }
        return JsonSerializer.Serialize(new { questions = list });
    }

    private string Grade(string prompt)
    {
        var n = QaRx().Matches(prompt).Count;
        var graded = Enumerable.Range(0, n).Select(i => new { question = "q", user_answer = "a", score = 8, max = 10, feedback = "mock-bewertung", reference_answer = "ref", completed_solution = "sol", official_sources = Array.Empty<object>() });
        return JsonSerializer.Serialize(new { graded, overall_feedback = "mock-gesamt" });
    }

    public ValueTask DisposeAsync() { try { _l.Stop(); _l.Close(); } catch { } return ValueTask.CompletedTask; }
}
