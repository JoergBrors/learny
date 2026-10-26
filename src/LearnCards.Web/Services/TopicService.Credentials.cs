using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>Themenabschluss: Grundlage für Master-Badge und CLR (ADR 0032). Nur lesend; die Vergabe übernimmt <see cref="Certificates.TopicCredentialService"/>.</summary>
public partial class TopicService
{
    internal sealed record TopicAttemptRow(string Kind, string ModuleId, double Percent, bool Passed, DateTime CompletedAt, string? ResultId);

    internal sealed class TopicCompletionBasis
    {
        public TopicInfo Topic { get; init; } = new();
        public TopicVersionInfo Version { get; init; } = new();
        public TopicSnapshot Snapshot { get; init; } = new();
        public TopicProgress Progress { get; init; } = new();
        public List<TopicAttemptRow> Attempts { get; init; } = new();
        /// <summary>Zeitpunkt der letzten Kartenmarkierung „gelernt“ in den Modulen dieser Version (Teil der Abschlussbedingung).</summary>
        public DateTime? LastCardCheckedAt { get; init; }
    }

    /// <summary>
    /// Fortschritt, Prüfungsversuche und Struktur einer veröffentlichten Themenversion für einen Benutzer. Berechtigungen prüft der Aufrufer (Versuche entstehen nur mit Zugriff);
    /// null, wenn Thema oder Version nicht existieren.
    /// </summary>
    internal async Task<TopicCompletionBasis?> GetCompletionBasisAsync(string userId, string topicId, int versionNumber)
    {
        var topic = await GetTopicRawAsync(topicId);
        if (topic is null) return null;
        var ver = await LoadVersionAsync(topic.Id, versionNumber);
        if (ver is null) return null;
        var progress = await ComputeProgressForUserAsync(userId, topic, requireAccess: false, ver);
        if (progress is null) return null;
        var attempts = (await _db.QueryAsync("""
            SELECT kind, module_id, percent, passed, completed_at, result_id FROM topic_exam_attempts WHERE user_id = @u AND topic_id = @t AND completed_at IS NOT NULL
            """, new (string, object?)[] { ("u", userId), ("t", topic.Id) }))
            .Select(a => new TopicAttemptRow(a["kind"] ?? "", a["module_id"] ?? "", DbValue.ToDouble(a.GetValueOrDefault("percent")), DbValue.ToBool(a.GetValueOrDefault("passed")),
                DbValue.ToDateTime(a.GetValueOrDefault("completed_at")), a.GetValueOrDefault("result_id") is { Length: > 0 } rid ? rid : null)).ToList();
        var snap = ReadSnapshot(ver) ?? new TopicSnapshot();
        DateTime? lastCheck = null;
        foreach (var m in snap.Modules)
        {
            var rows = await _db.QueryAsync("""
                SELECT MAX(s.updated_at) AS last FROM user_card_states s JOIN cards c ON c.id = s.card_id
                WHERE s.user_sub = @u AND s.is_checked = 1 AND c.module_id = @m AND c.archived = 0
                """, new (string, object?)[] { ("u", userId), ("m", m.ModuleId) });
            if (rows.Count > 0 && rows[0].GetValueOrDefault("last") is { Length: > 0 } l && DbValue.ToDateTime(l) is var t && (lastCheck is null || t > lastCheck)) lastCheck = t;
        }
        return new TopicCompletionBasis { Topic = topic, Version = ver, Snapshot = snap, Progress = progress, Attempts = attempts, LastCardCheckedAt = lastCheck };
    }

    /// <summary>Alle (Thema, Version)-Paare, zu denen der Benutzer abgeschlossene Prüfungsversuche hat — Kandidaten für einen Themenabschluss.</summary>
    internal async Task<List<(string TopicId, int Version)>> ListAttemptedTopicVersionsAsync(string userId) =>
        (await _db.QueryAsync("SELECT DISTINCT topic_id, topic_version FROM topic_exam_attempts WHERE user_id = @u AND completed_at IS NOT NULL", new[] { ("u", (object?)userId) }))
            .Select(r => (r["topic_id"] ?? "", DbValue.ToInt(r.GetValueOrDefault("topic_version")))).Where(x => x.Item1.Length > 0).ToList();
}
