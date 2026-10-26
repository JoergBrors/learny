using System.Text.Json.Nodes;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services.Certificates;

/// <summary>
/// Themenabschluss (ADR 0032): Prüft, ob ein Benutzer eine veröffentlichte Themenversion tatsächlich abgeschlossen hat, und stellt dann — einmalig und mit unverändertem
/// Abschlusszeitpunkt — Modulzertifikate, Master-Badge und CLR aus. Abschluss bedeutet: alle Module der Version abgeschlossen (Karten gelernt, aktive Modulprüfungen bestanden),
/// die Abschlussprüfung bestanden (falls vorgesehen) und mindestens eine serverseitig verifizierte Prüfung. Die Bewertung selbst bleibt im <see cref="QuizService"/>.
/// </summary>
public sealed class TopicCredentialService
{
    private readonly TopicService _topics;
    private readonly CardRepository _repo;
    private readonly QuizService _quiz;
    private readonly CertificateService _certs;
    private readonly CompetencyService _competencies;
    private readonly IDatabase _db;

    public TopicCredentialService(TopicService topics, CardRepository repo, QuizService quiz, CertificateService certs, CompetencyService competencies, IDatabase db)
    {
        _topics = topics; _repo = repo; _quiz = quiz; _certs = certs; _competencies = competencies; _db = db;
    }

    /// <summary>Nach einem Themenprüfungsergebnis: prüft die zugehörige Themenversion auf Abschluss.</summary>
    public async Task<Certificate?> EnsureForResultAsync(string userId, string resultId)
    {
        var rows = await _db.QueryAsync("SELECT topic_id, topic_version FROM topic_exam_attempts WHERE user_id = @u AND result_id = @r", new (string, object?)[] { ("u", userId), ("r", resultId) });
        if (rows.Count == 0) return null;
        return await EnsureCompletionAsync(userId, rows[0]["topic_id"] ?? "", DbValue.ToInt(rows[0].GetValueOrDefault("topic_version")));
    }

    /// <summary>Prüft alle Themenversionen mit Prüfungsversuchen des Benutzers (z. B. beim Öffnen von „Meine Abschlüsse“) und stellt fehlende Abschlüsse aus.</summary>
    public async Task EnsureAllForUserAsync(string userId)
    {
        foreach (var (topicId, version) in await _topics.ListAttemptedTopicVersionsAsync(userId))
        {
            try { await EnsureCompletionAsync(userId, topicId, version); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* ein fehlerhafter Einzelfall darf die übrigen Abschlüsse nicht verhindern */ }
        }
    }

    public async Task<Certificate?> EnsureCompletionAsync(string userId, string topicId, int version)
    {
        var existing = await _certs.FindTopicAsync(userId, topicId, version);
        if (existing is not null) return existing;
        var basis = await _topics.GetCompletionBasisAsync(userId, topicId, version);
        if (basis is null || basis.Progress.Status != TopicLearnStatuses.Completed) return null;

        // ─── Verifizierte Prüfungen je Modul (Modulabschlüsse) ───
        var quizResults = await _repo.QuizHistoryAsync(userId, null, null, 200);
        var moduleCerts = new List<Certificate>();
        var moduleLines = new JsonArray();
        var passTimes = new List<DateTime>();
        var examBest = new List<double>(); var examPass = new List<int>();
        foreach (var m in basis.Snapshot.Modules)
        {
            var cfg = basis.Snapshot.ExamConfig.ModuleExams.TryGetValue(m.ModuleId, out var c) ? c : new TopicModuleExamConfig();
            var line = new JsonObject { ["name"] = m.Name, ["version"] = m.Version, ["exam_enabled"] = cfg.Enabled };
            if (cfg.Enabled)
            {
                var passes = basis.Attempts.Where(a => a.Kind == TopicExamKinds.Module && string.Equals(a.ModuleId, m.ModuleId, StringComparison.OrdinalIgnoreCase) && a.Passed && a.ResultId is not null).ToList();
                var best = passes.OrderByDescending(a => a.Percent).ThenBy(a => a.CompletedAt).FirstOrDefault();
                if (best is null) return null;   // Fortschritt und Versuche stimmen nicht überein: nichts bescheinigen
                var record = quizResults.FirstOrDefault(r => r.Id == best.ResultId);
                var frozen = await _repo.GetModuleRawAsync(m.ModuleId);
                if (record is null || frozen is null) return null;
                var cert = await _quiz.IssueModuleCredentialAsync(record, frozen, CardRepository.ContextTopic);
                if (cert is null) return null;
                moduleCerts.Add(cert);
                passTimes.Add(passes.Min(a => a.CompletedAt));
                examBest.Add(best.Percent); examPass.Add(record.PassPercent ?? 0);
                line["certificate_id"] = cert.Id; line["certificate_number"] = cert.Number; line["percent"] = Math.Round(best.Percent, 1); line["pass_percent"] = record.PassPercent ?? 0; line["passed"] = true;
                line["completed_at"] = Iso(passes.Min(a => a.CompletedAt)); line["snapshot_hash"] = cert.SnapshotHash;
                if (cert.SnapshotId is { } sid && await _competencies.GetAsync(sid) is { } ms && JsonNode.Parse(ms.Json)?["competencies"] is JsonArray comps)
                    line["competencies"] = new JsonArray(comps.Select(k => (JsonNode)new JsonObject { ["id"] = k!["id"]!.DeepClone(), ["name"] = k["name"]!.DeepClone(), ["status"] = k["status"]!.DeepClone(), ["percent"] = k["percent"]!.DeepClone() }).ToArray());
            }
            moduleLines.Add(line);
        }

        // ─── Abschlussprüfung ───
        var fin = basis.Snapshot.ExamConfig.FinalExam;
        JsonObject? finalLine = null; string? finalResultId = null;
        if (fin.Enabled)
        {
            var passes = basis.Attempts.Where(a => a.Kind == TopicExamKinds.Final && a.Passed).ToList();
            var best = passes.OrderByDescending(a => a.Percent).ThenBy(a => a.CompletedAt).FirstOrDefault();
            if (best is null) return null;
            passTimes.Add(passes.Min(a => a.CompletedAt)); finalResultId = best.ResultId;
            finalLine = new JsonObject
            {
                ["name"] = $"Abschlussprüfung – {basis.Version.Name}", ["version"] = basis.Version.VersionNumber, ["pass_percent"] = fin.PassPercent, ["percent"] = Math.Round(best.Percent, 1), ["passed"] = true,
                ["completed_at"] = Iso(passes.Min(a => a.CompletedAt)), ["questions"] = fin.QuestionCount,
            };
        }
        if (moduleCerts.Count == 0 && finalLine is null) return null;   // ohne verifizierte Prüfung gibt es nichts zu bescheinigen

        // Abschlusszeitpunkt: wann die letzte Voraussetzung erfüllt war (spätester erster Prüfungserfolg bzw. spätestes Lernen) — nie die letzte beliebige Aktivität
        var completedAt = passTimes.Count == 0 ? basis.Progress.CompletedAt ?? DateTime.UtcNow : passTimes.Max();
        if (basis.LastCardCheckedAt is { } lc && lc > completedAt) completedAt = lc;
        var score = finalLine is not null ? finalLine["percent"]!.GetValue<double>() : examBest.Average();
        var passPercent = finalLine is not null ? fin.PassPercent : (int)Math.Round(examPass.Average());

        var certId = Guid.NewGuid().ToString();
        var doc = new JsonObject
        {
            ["format"] = "learny-topic-completion-snapshot", ["schema_version"] = 1, ["snapshot_id"] = null,
            ["topic"] = new JsonObject { ["name"] = basis.Version.Name, ["version"] = basis.Version.VersionNumber },
            ["completed_at"] = Iso(completedAt),
            ["criteria"] = new JsonObject
            {
                ["modules_required"] = basis.Snapshot.Modules.Count, ["module_exams_required"] = moduleCerts.Count, ["final_exam_required"] = fin.Enabled,
                ["narrative"] = "Alle erforderlichen Module der angegebenen Themenversion erfolgreich bestanden." + (fin.Enabled ? " Zusätzlich wurde die verpflichtende Abschlussprüfung bestanden." : ""),
            },
            ["modules"] = moduleLines, ["final_exam"] = finalLine,
        };
        var snap = new ExamSnapshot { ResultId = certId, UserId = userId, ModuleId = topicId, ModuleVersion = version, Kind = "topic" };
        doc["snapshot_id"] = snap.Id;
        snap = await _competencies.StoreSnapshotAsync(snap, doc);
        return await _certs.IssueTopicAsync(new CertificateService.TopicCompletionInput(userId, topicId, basis.Version.Name, version, completedAt, score, passPercent, finalResultId ?? moduleCerts.LastOrDefault()?.ResultId,
            fin.Enabled, moduleCerts), certId, snap);
    }

    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
