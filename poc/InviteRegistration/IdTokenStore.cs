using System.Collections.Concurrent;

namespace InviteRegistration;

/// <summary>
/// Nur für die Entwicklungsanzeige „Mein Anmelde-Token“: hält das zuletzt geprüfte ID-Token je Subject kurz im Speicher (nie in Datenbank, Logs oder Dateien).
/// Ein ID-Token gilt nur für die Client-App; trotzdem wird es nur dem eigenen Benutzer und nur in Development gezeigt.
/// </summary>
public sealed class IdTokenStore
{
    private readonly ConcurrentDictionary<string, (string Jwt, DateTime At)> _tokens = new();
    private readonly TimeSpan _life; private readonly int _max; private readonly Func<DateTime> _clock;
    public IdTokenStore(TimeSpan? life = null, int max = 200, Func<DateTime>? clock = null) { _life = life ?? TimeSpan.FromHours(8); _max = max; _clock = clock ?? (() => DateTime.UtcNow); }

    public void Put(string subject, string jwt)
    {
        var now = _clock();
        foreach (var k in _tokens.Where(kv => now - kv.Value.At > _life).Select(kv => kv.Key).ToList()) _tokens.TryRemove(k, out _);
        if (_tokens.Count >= _max) _tokens.TryRemove(_tokens.OrderBy(kv => kv.Value.At).First().Key, out _);
        _tokens[subject] = (jwt, now);
    }

    public (string Jwt, DateTime At)? Get(string subject) =>
        _tokens.TryGetValue(subject, out var t) && _clock() - t.At <= _life ? t : null;
}
