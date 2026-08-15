using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>Karten-Chatbot: nutzt den chat_prompt der Karte als System-Prompt.</summary>
public class ChatService
{
    private readonly CardRepository _repo;
    private readonly OpenAiClient _openAi;

    public ChatService(CardRepository repo, OpenAiClient openAi)
    {
        _repo = repo;
        _openAi = openAi;
    }

    public bool IsConfigured => _openAi.IsConfigured;

    public static string BuildSystemPrompt(Card card) =>
        BuildSourceBoundPrompt(card);

    public async IAsyncEnumerable<string> StreamAsync(
        Card card,
        IReadOnlyList<ChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_openAi.IsConfigured)
        {
            yield return "⚠️ OpenAI ist nicht konfiguriert. Setze OPENAI_API_KEY in der .env, um den KI-Chat zu aktivieren.";
            yield break;
        }

        var history = messages
            .Where(m => m.Role is "user" or "assistant")
            .Select(m => (m.Role, m.Content));

        await foreach (var token in _openAi.StreamChatAsync(BuildSystemPrompt(card), history, ct))
            yield return token;
    }

    public Task<Card?> GetCardAsync(string cardId) => _repo.GetCardAsync(cardId);

    public async Task<string> NormalizeQuestionAsync(Card card, string question, CancellationToken ct = default)
    {
        var trimmed = question.Trim();
        if (!_openAi.IsConfigured || trimmed.Length == 0) return trimmed;

        var sources = card.OfficialSources.Count == 0
            ? "Keine offiziellen Quellen hinterlegt."
            : string.Join("\n", card.OfficialSources.Select(s => $"- {s.Title}: {s.Url}"));

        var prompt = $$"""
            Formuliere die folgende Nutzerfrage als kurze, fachlich saubere und verständliche Frage um,
            ohne den Sinn zu verändern. Wenn die Frage mehrdeutig oder unklar ist, gib stattdessen eine
            Rückfrage zurück, die das fehlende Detail präzise klärt.

            Antworte ausschließlich als JSON:
            {
              "normalized_question": "...",
              "needs_clarification": true,
              "clarification_question": "..."
            }

            Thema:
            {{card.Term}}

            Offizielle Quellen:
            {{sources}}

            Nutzerfrage:
            {{trimmed}}
            """;

        try
        {
            using var doc = await _openAi.CompleteJsonAsync(prompt, 250, ct);
            var root = doc.RootElement;
            var needsClarification = root.TryGetProperty("needs_clarification", out var n) && n.ValueKind is JsonValueKind.True;
            if (needsClarification && root.TryGetProperty("clarification_question", out var cq))
                return cq.GetString() ?? trimmed;
            if (root.TryGetProperty("normalized_question", out var q))
                return q.GetString() ?? trimmed;
        }
        catch
        {
            // Fallback auf Originalfrage
        }
        return trimmed;
    }

    public Task SaveHistoryAsync(ChatHistoryEntry entry) => _repo.SaveChatHistoryAsync(entry);

    public Task<List<ChatHistoryEntry>> GetHistoryAsync(string userSub, string cardId, int limit = 50) =>
        _repo.GetChatHistoryAsync(userSub, cardId, limit);

    private static string BuildSourceBoundPrompt(Card card)
    {
        var basePrompt = !string.IsNullOrWhiteSpace(card.ChatPrompt)
            ? card.ChatPrompt.Trim()
            : $"Du bist ein Lernassistent für das Thema '{card.Term}'. Definition: {card.Definition}.";

        var sources = card.OfficialSources.Count == 0
            ? "Für diese Karte sind keine offiziellen Quellen hinterlegt."
            : string.Join("\n", card.OfficialSources.Select(s => $"- {s.Title}: {s.Url}"));

        var reference = string.IsNullOrWhiteSpace(card.ReferenceAnswer)
            ? card.Definition
            : card.ReferenceAnswer;

        return $$"""
            {{basePrompt}}

            Antworte streng quellengebunden.
            - Nutze nur die Informationen aus der Kartenreferenz und den offiziellen Quellen.
            - Erfinde keine Fakten, URLs, Produktdetails oder Best Practices.
            - Wenn etwas nicht aus den Quellen ableitbar ist, sage klar: "Dazu habe ich in den hinterlegten offiziellen Quellen keine gesicherte Information."
            - Wenn die Frage unklar, mehrdeutig oder fachlich unsauber ist, stelle zuerst eine kurze Rückfrage statt Annahmen zu treffen.
            - Verweise bei fachlichen Aussagen nach Möglichkeit auf die passende offizielle Quelle.

            Kartenreferenz:
            {{reference}}

            Offizielle Quellen:
            {{sources}}
            """;
    }
}
