using System.Collections.Concurrent;
using System.Text.Json;
using LearnCards.Web.Data;

namespace InviteRegistration;

/// <summary>Die <c>learny_*</c>-Claims des ID-Tokens einer Anmeldung (nur Hinweis, ADR 0047).</summary>
public sealed record LearnerHint(string Uid, string? GrantsVersion, IReadOnlyList<string>? Modules);

/// <summary>
/// Hält die Claims der letzten Anmeldung je Subject kurz im Speicher, bis das Portal die Sitzung anlegt (einmalig abholbar, 10 Minuten).
/// Das Token selbst wird hier nicht aufbewahrt; es werden nur die geprüften <c>learny_*</c>-Werte gelesen.
/// </summary>
public sealed class LearnerClaimsStore
{
    private readonly ConcurrentDictionary<string, (LearnerHint Hint, DateTime At)> _items = new();
    private readonly Func<DateTime> _clock; private readonly TimeSpan _life;
    public LearnerClaimsStore(Func<DateTime>? clock = null, TimeSpan? life = null) { _clock = clock ?? (() => DateTime.UtcNow); _life = life ?? TimeSpan.FromMinutes(10); }

    /// <summary>Aufruf mit einem bereits geprüften ID-Token (Signatur usw. wurden vorher kontrolliert).</summary>
    public void Put(string subject, string jwt)
    {
        var hint = Parse(jwt);
        var now = _clock();
        foreach (var k in _items.Where(kv => now - kv.Value.At > _life).Select(kv => kv.Key).ToList()) _items.TryRemove(k, out _);
        if (hint is null) { _items.TryRemove(subject, out _); return; }
        if (_items.Count >= 500) return;
        _items[subject] = (hint, now);
    }

    public LearnerHint? Take(string subject) =>
        _items.TryRemove(subject, out var v) && _clock() - v.At <= _life ? v.Hint : null;

    public static LearnerHint? Parse(string jwt)
    {
        try
        {
            var p = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            using var d = JsonDocument.Parse(Convert.FromBase64String(p.PadRight((p.Length + 3) / 4 * 4, '=')));
            var r = d.RootElement;
            if (!r.TryGetProperty("learny_uid", out var uid) || uid.ValueKind != JsonValueKind.String) return null;
            string? v = r.TryGetProperty("learny_grants_v", out var gv) && gv.ValueKind == JsonValueKind.String ? gv.GetString() : null;
            List<string>? mods = null;
            if (r.TryGetProperty("learny_modules", out var m))
            {
                // Eine Liste mit einem Eintrag kommt von Entra als einfacher Text (ADR 0047).
                if (m.ValueKind == JsonValueKind.String) mods = new() { m.GetString()! };
                else if (m.ValueKind == JsonValueKind.Array && m.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String)) mods = m.EnumerateArray().Select(x => x.GetString()!).ToList();
            }
            else if (r.TryGetProperty("learny_modules_n", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() == "0") mods = new();   // ausdrücklich keine Module
            return new LearnerHint(uid.GetString()!, v, mods);
        }
        catch (Exception e) when (e is FormatException or JsonException or IndexOutOfRangeException) { return null; }
    }
}

/// <summary>
/// Übersicht der Lernmodule ohne Listenabfrage: Die Claims der Anmeldung gelten, solange ihre Marke (<c>learny_grants_v</c>) zur aktuellen passt.
/// Die aktuelle Marke wird je Benutzer im Speicher gehalten und bei jeder Änderung durch Administratoren ungültig (<see cref="Invalidate"/>).
/// Der Zugriff auf ein Modul wird dadurch nie entschieden: <see cref="ContentAccess"/> prüft weiter gegen die Datenbank.
/// </summary>
public sealed class LearnerOverview
{
    private readonly IDatabase _db; private readonly ContentAccess _access;
    private readonly ConcurrentDictionary<string, string> _versions = new();
    private readonly ConcurrentDictionary<string, (string Name, string Scope)> _modules = new();
    public LearnerOverview(IDatabase db, ContentAccess access) { _db = db; _access = access; }

    public void Invalidate(string userId) => _versions.TryRemove(userId, out _);
    public void InvalidateAll() { _versions.Clear(); _modules.Clear(); }

    /// <summary>Aktuelle Marke (aus dem Speicher; bei fehlendem Eintrag einmal aus der Datenbank berechnet).</summary>
    public async Task<string?> CurrentVersionAsync(string userId, CancellationToken ct = default)
    {
        if (_versions.TryGetValue(userId, out var v)) return v;
        var rows = await _db.QueryAsync("SELECT u.status AS s, p.profile AS p FROM users u JOIN user_profiles p ON p.user_id=u.id WHERE u.id=@u", new (string, object?)[] { ("u", userId) }, ct);
        if (rows.Count == 0) return null;
        var status = rows[0]["s"]!;
        var mods = status == "active" ? await _access.ListReadableModulesAsync(userId, ct) : new List<string>();
        return _versions[userId] = TokenClaimsHandler.Version(rows[0]["p"]!, status, mods);
    }

    /// <summary>Modulname und Art (Katalogdaten, ändern sich selten; werden zwischengespeichert).</summary>
    public async Task<(string Id, string Name, string Scope)?> ModuleAsync(string moduleId, CancellationToken ct = default)
    {
        if (_modules.TryGetValue(moduleId, out var m)) return (moduleId, m.Name, m.Scope);
        var rows = await _db.QueryAsync("SELECT name, scope FROM poc_modules WHERE id=@m", new (string, object?)[] { ("m", moduleId) }, ct);
        if (rows.Count == 0) return null;
        _modules[moduleId] = (rows[0]["name"]!, rows[0]["scope"]!);
        return (moduleId, rows[0]["name"]!, rows[0]["scope"]!);
    }
}
