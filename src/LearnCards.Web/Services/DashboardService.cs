using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Services.Certificates;

namespace LearnCards.Web.Services;

public sealed record DashTopic(string Id, string Name, int Version, bool Mandatory, bool Overdue, DateTime? Due, string Status, int Percent, int ModulesCompleted, int ModulesTotal);
public sealed record DashItem(string Kind, string Title, string Meta, string Url, int? Percent);
public sealed record DashProgress(int Completed, int Total, int Percent);
public sealed record DashCertificate(string Id, string Name, bool IsTopic, DateTime At);
public sealed record DashboardData(string ContinueUrl, bool HasContinue, IReadOnlyList<DashTopic> Topics, IReadOnlyList<DashItem> Next, DashProgress? Progress, IReadOnlyList<DashCertificate> Certificates, int CertificateCount, bool HasOverdue);

/// <summary>
/// Datenquelle des persönlichen Dashboards. Ausschließlich benutzerbezogene Daten aus den bestehenden Diensten (Themenübersicht, Modulzugriff, Lernstatus, Nachweise) — nichts Erfundenes:
/// ohne Daten bleiben die Bereiche leer. Berechtigungen entscheiden die genutzten Dienste (externe Lernprofile sehen nur ausdrücklich Zugewiesenes); die Benutzer-ID stammt aus dem
/// Anmeldekontext. Es gibt keine eigene Persistenz und keine zweite Fortschrittslogik.
/// </summary>
public sealed class DashboardService
{
    private readonly TopicService _topics;
    private readonly CardRepository _repo;
    private readonly CertificateService _certs;
    private readonly LandingService _landing;
    private readonly IDatabase _db;

    public DashboardService(TopicService topics, CardRepository repo, CertificateService certs, LandingService landing, IDatabase db)
    { _topics = topics; _repo = repo; _certs = certs; _landing = landing; _db = db; }

    public async Task<DashboardData> LoadAsync(string userId)
    {
        if (!UserService.IsGuid(userId)) throw new UnauthorizedAccessException("Kein Benutzerkontext.");
        var overview = await _topics.ListOverviewAsync(userId);

        // Zugewiesene und verpflichtende Themen (auch eigene/globale mit ausdrücklicher Pflichtzuweisung), ohne Doppelte
        var relevant = overview.Assigned.Concat(overview.Mandatory).Concat(overview.MandatoryDone)
            .GroupBy(t => t.Topic.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        static bool Done(TopicSummary t) => t.Progress?.Status == "completed";
        var ordered = relevant.OrderBy(Done).ThenByDescending(t => t.Overdue).ThenByDescending(t => t.Mandatory).ThenBy(t => t.DueAt ?? DateTime.MaxValue).ThenBy(t => t.Topic.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var topics = ordered.Take(5).Select(t => new DashTopic(t.Topic.Id, t.Topic.Name, t.Version, t.Mandatory, t.Overdue, t.DueAt, t.Progress?.Status ?? "not_started",
            t.Progress?.ProgressPercent ?? 0, t.Progress?.ModulesCompleted ?? 0, t.Progress?.ModulesTotal ?? 0)).ToList();

        // Gesamtfortschritt nur aus eindeutig berechenbaren Daten (abgeschlossene / vorhandene Module der relevanten Themen)
        var total = relevant.Sum(t => t.Progress?.ModulesTotal ?? 0);
        var completed = relevant.Sum(t => t.Progress?.ModulesCompleted ?? 0);
        var progress = total > 0 ? new DashProgress(completed, total, (int)Math.Round(completed * 100.0 / total)) : null;

        // Nächste Lerninhalte: offene zugewiesene Themen, danach zuletzt bearbeitete Module (nur mit Zugriff)
        var next = new List<DashItem>();
        foreach (var t in ordered.Where(x => !Done(x)).Take(2))
            next.Add(new DashItem("topic", t.Topic.Name, t.Mandatory ? "Pflicht" + (t.DueAt is DateTime d ? $" · fällig {d.ToLocalTime():dd.MM.yyyy}" : "") : "Zugewiesen · freiwillig",
                $"/topics/{t.Topic.Id}?version={t.Version}", t.Progress?.ProgressPercent));
        foreach (var id in await _landing.RecentModuleIdsAsync(userId, 6))
        {
            if (next.Count >= 4) break;
            var access = await _repo.ResolveModuleAccessAsync(userId, id);
            if (access is null) continue;
            var active = Convert.ToInt32((await _db.QueryAsync("SELECT COUNT(*) AS n FROM cards WHERE module_id=@m AND archived=0", new[] { ("m", (object?)id) }))[0]["n"]);
            var states = await _repo.GetUserCardStatesAsync(userId, id);
            next.Add(new DashItem("module", access.Module.Name, $"{active} Lernkarten", $"/module/{id}", active > 0 ? (int)Math.Round(states.Values.Count(s => s.IsChecked) * 100.0 / active) : null));
        }

        var certs = (await _certs.ListForUserAsync(userId)).Where(c => !c.IsRevoked).OrderByDescending(c => c.CompletedAt ?? c.IssuedAt).ToList();
        var cont = await _landing.ResolveAsync(userId);
        return new DashboardData(cont, cont != "/", topics, next, progress,
            certs.Take(3).Select(c => new DashCertificate(c.Id, c.ModuleName, c.IsTopic, c.CompletedAt ?? c.IssuedAt)).ToList(), certs.Count, relevant.Any(t => t.Overdue));
    }
}
