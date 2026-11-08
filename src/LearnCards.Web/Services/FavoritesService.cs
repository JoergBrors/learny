using LearnCards.Web.Data;

namespace LearnCards.Web.Services;

/// <summary>
/// Favoriten (Stern) je Benutzer für Module, Themen und Nachweise. Es werden nur Kennungen gespeichert; die Benutzer-ID kommt ausschließlich aus dem angemeldeten Kontext. Ein Favorit gewährt nie Zugriff:
/// die Oberfläche blendet ihn nur über Einträge ein, die der Benutzer ohnehin sehen darf. Begrenzt auf <see cref="MaxPerUser"/> je Benutzer.
/// </summary>
public sealed class FavoritesService
{
    public const int MaxPerUser = 500;
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string> { "module", "topic", "credential" };

    private readonly IDatabase _db;
    public FavoritesService(IDatabase db) => _db = db;

    public async Task<HashSet<string>> ListAsync(string userId, string kind)
    {
        if (!UserService.IsGuid(userId) || !Kinds.Contains(kind)) return new();
        var rows = await _db.QueryAsync("SELECT item_id FROM user_favorites WHERE user_id=@u AND kind=@k", new (string, object?)[] { ("u", userId), ("k", kind) });
        return rows.Select(r => r["item_id"]!).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <returns>true, wenn der Eintrag danach Favorit ist.</returns>
    public async Task<bool> ToggleAsync(string userId, string kind, string itemId)
    {
        if (!UserService.IsGuid(userId)) throw new UnauthorizedAccessException("Kein Benutzerkontext.");
        if (!Kinds.Contains(kind)) throw new ArgumentException("Unbekannte Art.");
        if (!UserService.IsGuid(itemId)) throw new ArgumentException("Ungültige Kennung.");
        var args = new (string, object?)[] { ("u", userId), ("k", kind), ("i", itemId) };
        if (await _db.ExecuteAsync("DELETE FROM user_favorites WHERE user_id=@u AND kind=@k AND item_id=@i", args) > 0) return false;
        var count = DbValue.ToInt((await _db.QueryAsync("SELECT COUNT(*) AS n FROM user_favorites WHERE user_id=@u", new (string, object?)[] { ("u", userId) }))[0]["n"]);
        if (count >= MaxPerUser) throw new InvalidOperationException($"Höchstens {MaxPerUser} Favoriten möglich.");
        await _db.ExecuteAsync("INSERT INTO user_favorites (user_id, kind, item_id, created_at) VALUES (@u, @k, @i, @now) ON CONFLICT (user_id, kind, item_id) DO NOTHING",
            new (string, object?)[] { ("u", userId), ("k", kind), ("i", itemId), ("now", DateTime.UtcNow) });
        return true;
    }
}
