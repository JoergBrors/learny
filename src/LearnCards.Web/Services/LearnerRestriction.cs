using LearnCards.Web.Data;

namespace LearnCards.Web.Services;

/// <summary>
/// Einzige Definition „eingeschränkter Lernender“ (ADR 0049): externes Lernprofil <b>oder</b> Identität der Art <c>external</c> (auch ohne Profil, fail-closed). Solche Benutzer lernen nur ausdrücklich
/// zugewiesene Inhalte und dürfen <b>keine</b> eigenen Module oder Themen anlegen, nichts importieren, klonen, teilen oder veröffentlichen und <b>keine</b> MCP-Zugänge nutzen.
/// </summary>
public static class LearnerRestriction
{
    public const string Message = "Externe Lernprofile dürfen keine eigenen Inhalte anlegen, teilen oder veröffentlichen und keine MCP-Zugänge nutzen.";

    public static async Task<bool> IsRestrictedAsync(IDatabase db, string userId)
    {
        if (!UserService.IsGuid(userId)) return true;   // ungültige IDs: kein Zugriff
        var rows = await db.QueryAsync("""
            SELECT (SELECT COUNT(*) FROM user_profiles WHERE user_id = @u) AS p,
                   (SELECT COUNT(*) FROM user_identities WHERE user_id = @u AND kind = 'external') AS e
            """, new[] { ("u", (object?)userId) });
        return DbValue.ToInt(rows[0]["p"]) > 0 || DbValue.ToInt(rows[0]["e"]) > 0;
    }
}
