using System.Security.Cryptography;
using System.Text;
using LearnCards.Web.Data;

namespace LearnCards.Web.Auth;

/// <summary>
/// Einmaligkeit des OIDC-<c>state</c> in der Datenbank (ADR 0048): Ein Rücksprung kann auf <b>keiner</b> Instanz ein zweites Mal verwendet werden
/// (Mehrinstanzbetrieb, Token-Replay). Gespeichert wird nur der SHA-256-Hash des state; abgelaufene Einträge werden bei jedem Aufruf entfernt.
/// Der Anspruch ist eine einzige Anweisung (<c>INSERT … ON CONFLICT DO NOTHING</c>), also auch bei parallelen Anfragen atomar.
/// </summary>
public class OidcStateStore
{
    private readonly IDatabase _db;
    public OidcStateStore(IDatabase db) => _db = db;

    /// <summary>true = erstmalig verbraucht; false = bereits verwendet.</summary>
    public async Task<bool> TryConsumeAsync(string state, DateTime? now = null, CancellationToken ct = default)
    {
        var t = now ?? DateTime.UtcNow;
        await _db.ExecuteAsync("DELETE FROM oidc_consumed_states WHERE expires_at < @now", new (string, object?)[] { ("now", t) }, ct);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
        var n = await _db.ExecuteAsync("INSERT INTO oidc_consumed_states (state_hash, expires_at) VALUES (@h, @exp) ON CONFLICT (state_hash) DO NOTHING",
            new (string, object?)[] { ("h", hash), ("exp", t + OidcCallback.StateLifetime + TimeSpan.FromMinutes(1)) }, ct);
        return n == 1;
    }
}
