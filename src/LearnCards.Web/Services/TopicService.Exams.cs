using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>Aufbau einer Themenprüfung für die bestehende Quiz-Engine (ADR 0019/0027): Quellen und Anzahl je Modul, Konfiguration, Bindung an die Themenversion.</summary>
public sealed class TopicExamPlan
{
    public string TopicId { get; set; } = "";
    public string TopicName { get; set; } = "";
    public string VersionId { get; set; } = "";
    public int VersionNumber { get; set; }
    public string Kind { get; set; } = TopicExamKinds.Module;
    /// <summary>Modul, an das Sitzung und Ergebnis gebunden werden (Modulkopie bzw. Prüfungsmodul der Version).</summary>
    public string SessionModuleId { get; set; } = "";
    public string Label { get; set; } = "";
    public QuizConfig Config { get; set; } = new();
    /// <summary>Quelle (eingefrorene Modulkopie) und Anzahl der Fragen daraus.</summary>
    public List<(string ModuleId, int Count)> Sources { get; set; } = new();
    public int ExpectedCount => Sources.Sum(s => s.Count);
}

public partial class TopicService
{
    // ─── Zugriff und Versionsauflösung ──────────────────────────────────────

    private async Task<bool> HasPrivateAccessAsync(string userId, TopicInfo topic)
    {
        if (string.Equals(topic.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase)) return true;
        var rows = await _db.QueryAsync("""
            SELECT 1 FROM topic_shares WHERE topic_id = @t AND target_user_id = @u AND revoked_at IS NULL
            UNION ALL
            SELECT 1 FROM topic_assignments WHERE topic_id = @t AND user_id = @u AND status = 'active'
            """, new (string, object?)[] { ("t", topic.Id), ("u", userId) });
        return rows.Count > 0;
    }

    private async Task<string> SectionForAsync(string userId, TopicInfo topic)
    {
        if (string.Equals(topic.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase)) return TopicSections.Own;
        if ((await _db.QueryAsync("SELECT 1 FROM topic_assignments WHERE topic_id = @t AND user_id = @u AND status = 'active'", new (string, object?)[] { ("t", topic.Id), ("u", userId) })).Count > 0)
            return TopicSections.Assigned;
        if ((await _db.QueryAsync("SELECT 1 FROM topic_shares WHERE topic_id = @t AND target_user_id = @u AND revoked_at IS NULL", new (string, object?)[] { ("t", topic.Id), ("u", userId) })).Count > 0)
            return TopicSections.Shared;
        return TopicSections.Global;
    }

    /// <summary>
    /// Die Version, die der Benutzer lernen darf: Eigentümer, Freigabe und Zuweisung → neueste veröffentlichte (bzw. angeforderte); sonst nur die aktive globale Version.
    /// <c>null</c> = kein Zugriff (verhält sich wie „nicht vorhanden“).
    /// </summary>
    private async Task<TopicVersionInfo?> ResolveVersionForLearnerAsync(string userId, TopicInfo topic, string section, int? requestedVersion)
    {
        var privateAccess = section != TopicSections.Global && await HasPrivateAccessAsync(userId, topic);
        if (requestedVersion is int rv && rv > 0)
        {
            var ver = await LoadVersionAsync(topic.Id, rv);
            if (ver is null) return null;
            return privateAccess || ver.GlobalStatus == TopicGlobalStatuses.Active ? ver : null;
        }
        if (privateAccess) return await LoadLatestPublishedAsync(topic.Id);
        var rows = await _db.QueryAsync(VersionSelect + " WHERE v.topic_id = @t AND v.status = 'published' AND v.global_status = 'active' LIMIT 1", new[] { ("t", (object?)topic.Id) });
        return rows.Count == 0 ? null : MapVersion(rows[0]);
    }

    /// <summary>Vermerkt die zuletzt geöffnete Version (Grundlage des Hinweises „neue Version verfügbar“).</summary>
    public async Task MarkSeenAsync(string userId, string topicId, int version)
    {
        var topic = await GetTopicRawAsync(topicId) ?? throw new KeyNotFoundException("Thema nicht gefunden.");
        var section = await SectionForAsync(userId, topic);
        var ver = await ResolveVersionForLearnerAsync(userId, topic, section, version) ?? throw new KeyNotFoundException("Thema nicht gefunden.");
        await TouchStateAsync(userId, topic.Id, ver.VersionNumber);
    }

    private async Task TouchStateAsync(string userId, string topicId, int? seenVersion)
    {
        var now = DateTime.UtcNow;
        var n = await _db.ExecuteAsync(seenVersion is null
            ? "UPDATE topic_user_state SET last_activity_at = @n WHERE topic_id = @t AND user_id = @u"
            : "UPDATE topic_user_state SET seen_version = CASE WHEN seen_version < @v THEN @v ELSE seen_version END, last_activity_at = @n WHERE topic_id = @t AND user_id = @u",
            new (string, object?)[] { ("n", now), ("t", topicId), ("u", userId), ("v", seenVersion ?? 0) });
        if (n > 0) return;
        try { await _db.ExecuteAsync("INSERT INTO topic_user_state (topic_id, user_id, seen_version, last_activity_at) VALUES (@t, @u, @v, @n)", new (string, object?)[] { ("t", topicId), ("u", userId), ("v", seenVersion ?? 0), ("n", now) }); }
        catch (Exception) { /* paralleles Anlegen: der Wert wird beim nächsten Aufruf gesetzt */ }
    }

    // ─── Fortschritt ────────────────────────────────────────────────────────

    /// <summary>Lernfortschritt des angemeldeten Benutzers für die Version, die er lernen darf.</summary>
    public async Task<TopicProgress> GetProgressAsync(string userId, string topicId, int? version = null)
    {
        var topic = await GetTopicRawAsync(topicId) ?? throw new KeyNotFoundException("Thema nicht gefunden.");
        var section = await SectionForAsync(userId, topic);
        var ver = section == TopicSections.Own
            ? (version is int v && v > 0 ? await LoadVersionAsync(topic.Id, v) : await LoadLatestPublishedAsync(topic.Id))
            : await ResolveVersionForLearnerAsync(userId, topic, section, version);
        if (ver is null) throw new KeyNotFoundException("Thema nicht gefunden.");
        return (await ComputeProgressForUserAsync(userId, topic, requireAccess: true, ver))!;
    }

    private async Task<TopicProgress?> ComputeProgressForUserAsync(string userId, TopicInfo topic, bool requireAccess, TopicVersionInfo? ver = null)
    {
        if (ver is null)
        {
            var section = await SectionForAsync(userId, topic);
            ver = section == TopicSections.Own ? await LoadLatestPublishedAsync(topic.Id) : await ResolveVersionForLearnerAsync(userId, topic, section, null);
            if (ver is null) { if (requireAccess) throw new KeyNotFoundException("Thema nicht gefunden."); return null; }
        }
        var snap = ReadSnapshot(ver) ?? new TopicSnapshot();
        var progress = new TopicProgress { Version = ver.VersionNumber, ModulesTotal = snap.Modules.Count, FinalExamEnabled = snap.ExamConfig.FinalExam.Enabled };

        var attempts = (await _db.QueryAsync("""
            SELECT kind, module_id, topic_version, percent, passed, completed_at FROM topic_exam_attempts WHERE user_id = @u AND topic_id = @t AND completed_at IS NOT NULL
            """, new (string, object?)[] { ("u", userId), ("t", topic.Id) })).ToList();

        DateTime? last = null;
        void Seen(DateTime? t) { if (t is DateTime d && (last is null || d > last)) last = d; }
        foreach (var a in attempts) Seen(DbValue.ToDateTime(a.GetValueOrDefault("completed_at")));

        TopicModuleProgress? previous = null;
        TopicModuleExamConfig? previousCfg = null;
        foreach (var m in snap.Modules)
        {
            var cfg = snap.ExamConfig.ModuleExams.TryGetValue(m.ModuleId, out var c) ? c : new TopicModuleExamConfig();
            var state = (await _db.QueryAsync("""
                SELECT COUNT(*) AS total, SUM(CASE WHEN s.is_checked = 1 THEN 1 ELSE 0 END) AS checked, MAX(s.updated_at) AS last
                FROM cards c LEFT JOIN user_card_states s ON s.card_id = c.id AND s.user_sub = @u
                WHERE c.module_id = @m AND c.archived = 0
                """, new (string, object?)[] { ("u", userId), ("m", m.ModuleId) }))[0];
            var total = DbValue.ToInt(state["total"]); var checkedCount = DbValue.ToInt(state.GetValueOrDefault("checked"));
            if (state.GetValueOrDefault("last") is { Length: > 0 } l) Seen(DbValue.ToDateTime(l));
            var exams = attempts.Where(a => a["kind"] == TopicExamKinds.Module && string.Equals(a["module_id"], m.ModuleId, StringComparison.OrdinalIgnoreCase)).ToList();
            var passed = exams.Any(a => DbValue.ToBool(a.GetValueOrDefault("passed")));
            var best = exams.Count == 0 ? (double?)null : exams.Max(a => DbValue.ToDouble(a.GetValueOrDefault("percent")));
            var mp = new TopicModuleProgress
            {
                ModuleId = m.ModuleId, Name = m.Name, Cards = total, CardsChecked = checkedCount, ExamEnabled = cfg.Enabled, ExamPassed = passed, ExamBestPercent = best, ExamAttempts = exams.Count,
                Started = checkedCount > 0 || exams.Count > 0,
                Completed = (total == 0 || checkedCount >= total) && (!cfg.Enabled || passed) && (total > 0 || cfg.Enabled),
            };
            if (previous is not null && previousCfg is { Enabled: true, MinPercentForNext: > 0 } && (previous.ExamBestPercent ?? 0) < previousCfg.MinPercentForNext)
            {
                mp.Unlocked = false;
                mp.UnlockHint = $"Erreiche zuerst mindestens {previousCfg.MinPercentForNext} % in der Modulprüfung „{previous.Name}“.";
            }
            progress.Modules.Add(mp);
            previous = mp; previousCfg = cfg;
        }
        progress.ModulesStarted = progress.Modules.Count(m => m.Started);
        progress.ModulesCompleted = progress.Modules.Count(m => m.Completed);
        progress.ModuleExamsTotal = progress.Modules.Count(m => m.ExamEnabled);
        progress.ModuleExamsPassed = progress.Modules.Count(m => m.ExamEnabled && m.ExamPassed);

        // Abschlussprüfung: Versuche beziehen sich auf die Version, die der Benutzer gerade lernt
        var finals = attempts.Where(a => a["kind"] == TopicExamKinds.Final && DbValue.ToInt(a.GetValueOrDefault("topic_version")) == ver.VersionNumber).ToList();
        progress.FinalExamAttempts = finals.Count;
        progress.FinalExamPassed = finals.Any(a => DbValue.ToBool(a.GetValueOrDefault("passed")));
        progress.FinalExamBestPercent = finals.Count == 0 ? null : finals.Max(a => DbValue.ToDouble(a.GetValueOrDefault("percent")));
        var fin = snap.ExamConfig.FinalExam;
        if (fin.Enabled)
        {
            var reason = FinalExamBlockedReason(fin, progress);
            progress.FinalExamAvailable = reason.Length == 0;
            progress.FinalExamBlockedReason = reason;
        }

        var state2 = (await _db.QueryAsync("SELECT seen_version, last_activity_at FROM topic_user_state WHERE topic_id = @t AND user_id = @u", new (string, object?)[] { ("t", topic.Id), ("u", userId) })).FirstOrDefault();
        progress.SeenVersion = DbValue.ToInt(state2?.GetValueOrDefault("seen_version"));
        if (state2?.GetValueOrDefault("last_activity_at") is { Length: > 0 } la) Seen(DbValue.ToDateTime(la));
        progress.LastActivityAt = last;
        progress.NewerVersionAvailable = progress.SeenVersion > 0 && ver.VersionNumber > progress.SeenVersion;
        return progress;
    }

    private static string FinalExamBlockedReason(TopicFinalExamConfig fin, TopicProgress p)
    {
        if (fin.Unlock.RequireAllModulesCompleted && p.ModulesCompleted < p.ModulesTotal)
            return $"Schließe zuerst alle Module ab ({p.ModulesCompleted} von {p.ModulesTotal} abgeschlossen).";
        if (fin.Unlock.RequireModuleExamsPassed && p.ModuleExamsPassed < p.ModuleExamsTotal)
            return $"Bestehe zuerst alle Modulprüfungen ({p.ModuleExamsPassed} von {p.ModuleExamsTotal} bestanden).";
        if (!fin.AllowRetry && p.FinalExamAttempts > 0) return "Wiederholungen sind für die Abschlussprüfung nicht erlaubt.";
        return "";
    }

    // ─── Prüfungen starten ──────────────────────────────────────────────────

    private async Task<(TopicInfo Topic, TopicVersionInfo Version, TopicSnapshot Snapshot)> RequireExamContextAsync(string userId, string topicId, int? version)
    {
        var topic = await GetTopicRawAsync(topicId) ?? throw new KeyNotFoundException("Thema nicht gefunden.");
        var section = await SectionForAsync(userId, topic);
        var ver = section == TopicSections.Own
            ? (version is int v && v > 0 ? await LoadVersionAsync(topic.Id, v) : await LoadLatestPublishedAsync(topic.Id))
            : await ResolveVersionForLearnerAsync(userId, topic, section, version);
        if (ver is null) throw new KeyNotFoundException("Thema nicht gefunden.");
        return (topic, ver, ReadSnapshot(ver) ?? throw new InvalidOperationException("Die Themenversion ist beschädigt."));
    }

    private static string TypeList(IEnumerable<string> types) => string.Join(", ", types.Select(t => t switch
    {
        QuizTypes.SingleChoice => "Single Choice", QuizTypes.MultipleResponse => "Multiple Response", QuizTypes.FreeText => "Freitext", _ => t,
    }));

    /// <summary>Startet die Modulprüfung eines Themas (serverseitige Sitzung der bestehenden Quiz-Engine; Lösungen verlassen den Server nicht).</summary>
    public async Task<QuizStartResponse> StartModuleExamAsync(string userId, string topicId, string moduleId, int? version = null, CancellationToken ct = default)
    {
        var (topic, ver, snap) = await RequireExamContextAsync(userId, topicId, version);
        var index = snap.Modules.FindIndex(m => string.Equals(m.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new KeyNotFoundException("Modul nicht gefunden.");
        var module = snap.Modules[index];
        if (!snap.ExamConfig.ModuleExams.TryGetValue(module.ModuleId, out var cfg) || !cfg.Enabled)
            throw new QuizRuleException("Für dieses Modul ist keine Prüfung aktiv.", 403);

        var progress = (await ComputeProgressForUserAsync(userId, topic, requireAccess: true, ver))!;
        var mp = progress.Modules[index];
        if (!mp.Unlocked) throw new QuizRuleException(mp.UnlockHint, 403);
        if (!cfg.AllowRetry && mp.ExamAttempts > 0) throw new QuizRuleException("Wiederholungen sind für diese Modulprüfung nicht erlaubt.", 409);

        var allowed = cfg.AllowedTypes();
        var counts = await QuestionCountsByTypeAsync(module.ModuleId);
        var available = allowed.Sum(t => counts.GetValueOrDefault(t));
        if (available < cfg.QuestionCount)
            throw new ArgumentException($"Die Modulprüfung „{module.Name}“ benötigt {cfg.QuestionCount} Fragen, es sind aber nur {available} passende Fragen verfügbar (erlaubte Fragetypen: {TypeList(allowed)}). Ergänze Fragen im Modul oder reduziere die Anzahl.");

        var plan = new TopicExamPlan
        {
            TopicId = topic.Id, TopicName = snap.Name, VersionId = ver.Id, VersionNumber = ver.VersionNumber, Kind = TopicExamKinds.Module, SessionModuleId = module.ModuleId,
            Label = $"{snap.Name} · {module.Name}", Config = cfg.ToQuizConfig(), Sources = new() { (module.ModuleId, cfg.QuestionCount) },
        };
        return await StartAndRecordAsync(userId, plan, ct);
    }

    /// <summary>Startet die Abschlussprüfung: Fragen aus mehreren Modulen (nach der konfigurierten Verteilung) plus eigene Themenfragen.</summary>
    public async Task<QuizStartResponse> StartFinalExamAsync(string userId, string topicId, int? version = null, CancellationToken ct = default)
    {
        var (topic, ver, snap) = await RequireExamContextAsync(userId, topicId, version);
        var fin = snap.ExamConfig.FinalExam;
        if (!fin.Enabled || string.IsNullOrEmpty(snap.ExamModuleId)) throw new QuizRuleException("Für dieses Thema ist keine Abschlussprüfung aktiv.", 403);

        var progress = (await ComputeProgressForUserAsync(userId, topic, requireAccess: true, ver))!;
        var blocked = FinalExamBlockedReason(fin, progress);
        if (blocked.Length > 0) throw new QuizRuleException(blocked, blocked.StartsWith("Wiederholungen") ? 409 : 403);

        var allowed = fin.AllowedTypes();
        var sources = new List<(string Id, int Available)>();
        foreach (var m in snap.Modules)
        {
            var c = await QuestionCountsByTypeAsync(m.ModuleId);
            sources.Add((m.ModuleId, allowed.Sum(t => c.GetValueOrDefault(t))));
        }
        var topicAvailable = 0;
        if (fin.IncludeTopicQuestions)
        {
            var c = await QuestionCountsByTypeAsync(snap.ExamModuleId);
            topicAvailable = allowed.Sum(t => c.GetValueOrDefault(t));
            if (topicAvailable > 0) sources.Add((snap.ExamModuleId, topicAvailable));
        }
        var alloc = AllocateQuestionCounts(sources, fin.QuestionCount, fin.Distribution, fin.ManualCounts, snap.ExamModuleId, out var error);
        if (alloc is null)
        {
            var moduleAvail = sources.Where(s => s.Id != snap.ExamModuleId).Sum(s => s.Available);
            throw new ArgumentException($"{error} (Fragen der Module: {moduleAvail}, eigene Themenfragen: {topicAvailable}; erlaubte Fragetypen: {TypeList(allowed)}). Ergänze Fragen oder reduziere die Anzahl.");
        }
        var plan = new TopicExamPlan
        {
            TopicId = topic.Id, TopicName = snap.Name, VersionId = ver.Id, VersionNumber = ver.VersionNumber, Kind = TopicExamKinds.Final, SessionModuleId = snap.ExamModuleId,
            Label = $"{snap.Name} · Abschlussprüfung", Config = fin.ToQuizConfig(), Sources = alloc.Select(kv => (kv.Key, kv.Value)).ToList(),
        };
        plan.Config.QuestionCount = Math.Clamp(plan.ExpectedCount, 1, 50);
        return await StartAndRecordAsync(userId, plan, ct);
    }

    private async Task<QuizStartResponse> StartAndRecordAsync(string userId, TopicExamPlan plan, CancellationToken ct)
    {
        var response = await _quiz.StartTopicExamSessionAsync(plan, userId, ct);
        await _db.ExecuteAsync("""
            INSERT INTO topic_exam_attempts (id, user_id, topic_id, topic_version_id, topic_version, kind, module_id, session_id, started_at)
            VALUES (@id, @u, @t, @vid, @vn, @k, @m, @s, @n)
            """, new (string, object?)[]
        {
            ("id", Guid.NewGuid().ToString()), ("u", userId), ("t", plan.TopicId), ("vid", plan.VersionId), ("vn", plan.VersionNumber), ("k", plan.Kind),
            ("m", plan.SessionModuleId), ("s", response.SessionId), ("n", DateTime.UtcNow),
        });
        await TouchStateAsync(userId, plan.TopicId, plan.VersionNumber);
        return response;
    }

    /// <summary>
    /// Verteilt <paramref name="total"/> Fragen auf die Quellen (even: reihum, proportional: nach Größe des Fragenpools, manual: vorgegebene Anzahlen, Rest aus den Themenfragen
    /// bzw. reihum). Liefert null mit Fehlertext, wenn der Pool zu klein oder die manuelle Verteilung unzulässig ist.
    /// </summary>
    public static Dictionary<string, int>? AllocateQuestionCounts(IReadOnlyList<(string Id, int Available)> sources, int total, string mode,
        IReadOnlyDictionary<string, int>? manual, string? topicSourceId, out string? error)
    {
        error = null;
        var avail = sources.Sum(s => s.Available);
        if (avail < total)
        {
            error = $"Die Abschlussprüfung benötigt {total} Fragen, es sind aber nur {avail} passende Fragen verfügbar";
            return null;
        }
        var result = sources.ToDictionary(s => s.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var cap = sources.ToDictionary(s => s.Id, s => s.Available, StringComparer.OrdinalIgnoreCase);
        var left = total;

        void RoundRobin(IEnumerable<string> order)
        {
            var ids = order.ToList();
            while (left > 0 && ids.Any(i => result[i] < cap[i]))
                foreach (var id in ids) { if (left == 0) break; if (result[id] < cap[id]) { result[id]++; left--; } }
        }

        if (mode == TopicFinalExamConfig.DistributionManual)
        {
            var wanted = 0;
            foreach (var s in sources.Where(s => s.Id != topicSourceId))
            {
                var w = manual is not null && manual.TryGetValue(s.Id, out var v) ? v : 0;
                if (w > s.Available) { error = $"Die manuelle Verteilung verlangt {w} Fragen aus einem Modul, es sind aber nur {s.Available} verfügbar"; return null; }
                result[s.Id] = w; wanted += w;
            }
            if (wanted > total) { error = $"Die manuelle Verteilung ({wanted}) übersteigt die Anzahl der Fragen ({total})"; return null; }
            left = total - wanted;
            if (topicSourceId is not null && result.ContainsKey(topicSourceId))
            {
                var take = Math.Min(left, cap[topicSourceId]); result[topicSourceId] = take; left -= take;
            }
            RoundRobin(sources.Select(s => s.Id));
        }
        else if (mode == TopicFinalExamConfig.DistributionProportional)
        {
            var sumA = (double)avail;
            var quotas = sources.Select(s => (s.Id, Quota: total * s.Available / sumA)).ToList();
            foreach (var q in quotas) { var f = (int)Math.Min(Math.Floor(q.Quota), cap[q.Id]); result[q.Id] = f; left -= f; }
            foreach (var q in quotas.OrderByDescending(q => q.Quota - Math.Floor(q.Quota)))
                if (left > 0 && result[q.Id] < cap[q.Id]) { result[q.Id]++; left--; }
            RoundRobin(sources.Select(s => s.Id));
        }
        else RoundRobin(sources.Select(s => s.Id));

        if (left != 0) { error = $"Die Abschlussprüfung benötigt {total} Fragen, es konnten aber nur {total - left} verteilt werden"; return null; }
        return result.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // ─── Versuche ───────────────────────────────────────────────────────────

    /// <summary>Eigene Prüfungsversuche eines Themas (alle Versionen), neueste zuerst. Abgeschlossene Versuche bleiben der ursprünglichen Version zugeordnet.</summary>
    public async Task<List<TopicExamAttempt>> ListAttemptsAsync(string userId, string topicId)
    {
        var topic = await GetTopicRawAsync(topicId) ?? throw new KeyNotFoundException("Thema nicht gefunden.");
        var section = await SectionForAsync(userId, topic);
        if (section == TopicSections.Global && await ResolveVersionForLearnerAsync(userId, topic, section, null) is null) throw new KeyNotFoundException("Thema nicht gefunden.");
        return (await _db.QueryAsync("SELECT * FROM topic_exam_attempts WHERE user_id = @u AND topic_id = @t ORDER BY started_at DESC LIMIT 200", new (string, object?)[] { ("u", userId), ("t", topic.Id) }))
            .Select(r => new TopicExamAttempt
            {
                Id = r["id"] ?? "", UserId = userId, TopicId = topic.Id, TopicVersionId = r["topic_version_id"] ?? "", TopicVersion = DbValue.ToInt(r.GetValueOrDefault("topic_version")),
                Kind = r["kind"] ?? "", ModuleId = r["module_id"] ?? "", SessionId = r["session_id"] ?? "", ResultId = r.GetValueOrDefault("result_id"),
                Score = r.GetValueOrDefault("score") is { Length: > 0 } s ? DbValue.ToDouble(s) : null, MaxScore = r.GetValueOrDefault("max_score") is { Length: > 0 } m ? DbValue.ToDouble(m) : null,
                Percent = r.GetValueOrDefault("percent") is { Length: > 0 } p ? DbValue.ToDouble(p) : null,
                Passed = r.GetValueOrDefault("passed") is { Length: > 0 } ps ? DbValue.ToBool(ps) : null,
                StartedAt = DbValue.ToDateTime(r.GetValueOrDefault("started_at")), CompletedAt = r.GetValueOrDefault("completed_at") is { Length: > 0 } c ? DbValue.ToDateTime(c) : null,
            }).ToList();
    }
}
