using LearnCards.Web.Data;

namespace LearnCards.Web.Services;

/// <summary>
/// Startziel nach der Anmeldung: zuerst dorthin, wo dem Benutzer <b>Themen zugewiesen</b> sind (und noch nicht abgeschlossen; bei eigenen/globalen Pflichtthemen direkt das Thema), sonst zum Modul mit den <b>aktuellsten Lernkarten</b>
/// (zuletzt geänderte aktive Karte unter allen Modulen, die der Benutzer lernen darf), sonst die Modulübersicht. Es werden nur Daten gelesen, die der Benutzer ohnehin sehen darf
/// (dieselben Dienste wie die Seiten, inklusive der Einschränkung externer Lernprofile auf Zugewiesenes); das Ziel ist immer eine interne Adresse.
/// </summary>
public sealed class LandingService
{
    private readonly TopicService _topics;
    private readonly CardRepository _repo;
    private readonly IDatabase _db;

    public LandingService(TopicService topics, CardRepository repo, IDatabase db) { _topics = topics; _repo = repo; _db = db; }

    /// <summary>Kennungen der lernbaren Module (eigene, geteilte, globale; externe Lernprofile nur Zugewiesenes), deren aktive Karten zuletzt geändert wurden, neueste zuerst.</summary>
    public async Task<List<string>> RecentModuleIdsAsync(string? userId, int take)
    {
        if (!UserService.IsGuid(userId)) return new();
        var ids = (await _repo.ListModulesAsync(userId!)).Select(m => m.Id)
            .Concat((await _repo.ListSharedWithMeAsync(userId!)).Select(m => m.Id))
            .Concat((await _repo.ListGlobalModulesAsync(userId!)).Select(m => m.Id))
            .Where(UserService.IsGuid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) return new();
        var args = ids.Select((id, i) => ("m" + i, (object?)id)).ToList();
        var rows = await _db.QueryAsync($"SELECT module_id, MAX(updated_at) AS last FROM cards WHERE archived = 0 AND module_id IN ({string.Join(",", args.Select(a => "@" + a.Item1))}) GROUP BY module_id ORDER BY last DESC LIMIT {Math.Clamp(take, 1, 50)}", args);
        return rows.Select(r => r["module_id"]!).ToList();
    }

    public async Task<string> ResolveAsync(string? userId)
    {
        if (!UserService.IsGuid(userId)) return "/";
        try
        {
            // 1) zugewiesene, noch nicht abgeschlossene Themen (Pflicht zuerst ist dort sichtbar)
            var overview = await _topics.ListOverviewAsync(userId!);
            if (overview.Assigned.Any(t => t.Progress?.Status != "completed")) return "/topics?tab=assigned";
            // eigene oder globale Themen mit ausdrücklicher Pflichtzuweisung erscheinen nicht unter „Mir zugewiesen“: dann direkt das erste offene Pflichtthema
            if (overview.Mandatory.FirstOrDefault() is { } mandatory) return $"/topics/{mandatory.Topic.Id}?version={mandatory.Version}";

            // 2) Modul mit der zuletzt geänderten aktiven Karte unter den lernbaren Modulen
            var ids = (await _repo.ListModulesAsync(userId!)).Select(m => m.Id)
                .Concat((await _repo.ListSharedWithMeAsync(userId!)).Select(m => m.Id))
                .Concat((await _repo.ListGlobalModulesAsync(userId!)).Select(m => m.Id))
                .Where(UserService.IsGuid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (ids.Count > 0)
            {
                var args = ids.Select((id, i) => ("m" + i, (object?)id)).ToList();
                var rows = await _db.QueryAsync($"SELECT module_id FROM cards WHERE archived = 0 AND module_id IN ({string.Join(",", args.Select(a => "@" + a.Item1))}) ORDER BY updated_at DESC LIMIT 1", args);
                if (rows.Count > 0) return "/module/" + rows[0]["module_id"];
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException) { /* Startziel ist nur Komfort: Fallback unten */ }
        return "/";
    }
}
