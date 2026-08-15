using System.Text.Json;

namespace LearnCards.Web.Api;

/// <summary>Globale JSON-Optionen: snake_case — kompatibel zum Python-Original und zum Karten-JSON-Format.</summary>
public static class AppJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };
}
