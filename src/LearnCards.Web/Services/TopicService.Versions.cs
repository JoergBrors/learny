using System.Text.Json;
using LearnCards.Web.Api;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>Themenversionen: eingefrorene Snapshots, Veröffentlichung, globale Freigabe durch Administratoren (ADR 0027, Muster aus ADR 0015).</summary>
public partial class TopicService
{
    /// <summary>Persistierte Struktur einer Version (<c>topic_versions.snapshot_json</c>); die Inhalte selbst liegen in den eingefrorenen Modulkopien.</summary>
    internal sealed class TopicSnapshot
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public TopicStats Stats { get; set; } = new();
        /// <summary>Schlüssel = ID der eingefrorenen Modulkopie.</summary>
        public TopicExamConfig ExamConfig { get; set; } = new();
        public List<TopicModuleView> Modules { get; set; } = new();
        public string ExamModuleId { get; set; } = "";
        /// <summary>Inhaltskennung des Entwurfs bei der Veröffentlichung (ADR 0030); leer bei Altbestand (dann gilt der Revisionszähler).</summary>
        public string DraftFingerprint { get; set; } = "";
    }

    /// <summary>
    /// Inhaltskennung des Entwurfs: Name, Beschreibung, Module in Reihenfolge mit festgelegter Modulversion, Prüfungskonfiguration und aktive Themenfragen.
    /// Gleiche Kennung = gleicher veröffentlichungsfähiger Inhalt — unabhängig von Zeitstempeln und Revisionszählern (zurückgenommene Änderungen zählen nicht).
    /// </summary>
    private async Task<string> ComputeDraftFingerprintAsync(TopicInfo topic)
    {
        var rows = await ListDraftModuleRowsAsync(topic.Id);
        var cfg = await LoadExamConfigAsync(topic.Id);
        var sb = new System.Text.StringBuilder();
        void Add(params object?[] parts) { foreach (var p in parts) sb.Append(p?.ToString()).Append('\u001f'); sb.Append('\u001e'); }
        Add("t", topic.Name, topic.Description);
        foreach (var r in rows) Add("m", r.ModuleId.ToLowerInvariant(), r.Version, JsonSerializer.Serialize(cfg.ModuleExams.TryGetValue(r.ModuleId, out var e) ? e : new TopicModuleExamConfig(), AppJson.Options));
        var fin = cfg.FinalExam;
        var finNode = (System.Text.Json.Nodes.JsonObject)JsonSerializer.SerializeToNode(fin, AppJson.Options)!;
        finNode["manual_counts"] = JsonSerializer.SerializeToNode(fin.ManualCounts.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key.ToLowerInvariant(), k => k.Value), AppJson.Options);
        Add("f", finNode.ToJsonString());
        foreach (var q in (await ListQuestionsRawAsync(topic.Id, activeOnly: true)).OrderBy(q => q.Question, StringComparer.Ordinal).ThenBy(q => q.Type, StringComparer.Ordinal))
            Add("q", q.Type, q.Question, string.Join("|", q.Options), string.Join(",", q.CorrectIndexes), q.Explanation);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>Weicht der Entwurf von der neuesten Veröffentlichung ab? Mit gespeicherter Inhaltskennung exakt, sonst (Altbestand) über den Revisionszähler.</summary>
    private async Task<bool> DraftDiffersFromLatestAsync(TopicInfo topic, TopicVersionInfo? latest)
    {
        if (latest is null) return true;
        var snap = ReadSnapshot(latest);
        if (snap is null || snap.DraftFingerprint.Length == 0) return topic.HasUnpublishedChanges;
        return !string.Equals(snap.DraftFingerprint, await ComputeDraftFingerprintAsync(topic), StringComparison.Ordinal);
    }

    // ─── Zählungen ──────────────────────────────────────────────────────────

    private async Task<int> ActiveCardCountAsync(string moduleId) =>
        DbValue.ToInt((await _db.QueryAsync("SELECT COUNT(*) AS n FROM cards WHERE module_id = @m AND archived = 0", new[] { ("m", (object?)moduleId) }))[0]["n"]);

    private async Task<Dictionary<string, int>> QuestionCountsByTypeAsync(string moduleId)
    {
        var rows = await _db.QueryAsync("SELECT type, COUNT(*) AS n FROM quiz_questions WHERE module_id = @m AND active = 1 GROUP BY type", new[] { ("m", (object?)moduleId) });
        return rows.ToDictionary(r => r["type"] ?? "", r => DbValue.ToInt(r["n"]));
    }

    private static int Choice(Dictionary<string, int> c) => c.GetValueOrDefault(QuizTypes.SingleChoice) + c.GetValueOrDefault(QuizTypes.MultipleResponse);

    private async Task<TopicModuleView> ViewOfModuleAsync(ModuleInfo m, int position, TopicModuleExamConfig exam)
    {
        var counts = await QuestionCountsByTypeAsync(m.Id);
        return new TopicModuleView
        {
            ModuleId = m.Id, SourceModuleId = m.SourceModuleId, Position = position, Name = m.Name, Description = m.Description, Icon = m.Icon, Color = m.Color,
            Cards = await ActiveCardCountAsync(m.Id), ChoiceQuestions = Choice(counts), PoolQuestions = counts.Values.Sum(), Exam = exam,
        };
    }

    /// <summary>Module des Entwurfs mit aktuellen Zahlen (Originale; nicht mehr zugreifbare Module sind als <c>Unavailable</c> markiert).</summary>
    private async Task<List<TopicModuleView>> DraftModuleViewsAsync(string topicId, string viewerId)
    {
        var cfg = await LoadExamConfigAsync(topicId);
        var list = new List<TopicModuleView>();
        var pos = 0;
        foreach (var row in await ListDraftModuleRowsAsync(topicId))
        {
            var access = await _repo.ResolveModuleAccessAsync(viewerId, row.ModuleId);
            var exam = cfg.ModuleExams.TryGetValue(row.ModuleId, out var e) ? e : new TopicModuleExamConfig();
            if (access is null || access.Context is not (CardRepository.ContextPrivate or CardRepository.ContextGlobal))
            {
                list.Add(new TopicModuleView { ModuleId = row.ModuleId, Position = pos++, Name = "(nicht mehr verfügbar)", Unavailable = true, Exam = exam });
                continue;
            }
            if (row.Version is int pinned && await _moduleVersions.GetVersionRawAsync(row.ModuleId, pinned) is { } ver && await _repo.GetModuleRawAsync(ver.SnapshotModuleId) is { } frozen)
            {
                // Anzeige aus dem festgelegten Modulstand (nicht aus dem veränderlichen Original)
                var view = await ViewOfModuleAsync(frozen, pos++, exam);
                view.ModuleId = row.ModuleId; view.SourceModuleId = row.ModuleId; view.Version = ver.VersionNumber;
                var (latest, changed) = await _moduleVersions.GetStateAsync(access.Module);
                view.LatestVersion = latest; view.ModuleHasUnpublishedChanges = changed; view.NewerAvailable = latest > ver.VersionNumber || changed;
                list.Add(view);
            }
            else list.Add(await ViewOfModuleAsync(access.Module, pos++, exam));
        }
        return list;
    }

    private async Task<int> FinalPoolSizeAsync(IEnumerable<string> moduleIds, TopicFinalExamConfig fin, int topicQuestions)
    {
        var allowed = fin.AllowedTypes();
        var total = 0;
        foreach (var id in moduleIds) { var c = await QuestionCountsByTypeAsync(id); total += allowed.Sum(t => c.GetValueOrDefault(t)); }
        return total + (fin.IncludeTopicQuestions ? topicQuestions : 0);
    }

    private async Task<TopicStats> DraftStatsAsync(string topicId, string viewerId)
    {
        var views = (await DraftModuleViewsAsync(topicId, viewerId)).Where(v => !v.Unavailable).ToList();
        var fin = (await LoadExamConfigAsync(topicId)).FinalExam;
        var topicQs = (await ListQuestionsRawAsync(topicId, activeOnly: true)).Where(q => fin.AllowedTypes().Contains(q.Type)).Count();
        return new TopicStats
        {
            Modules = views.Count, Cards = views.Sum(v => v.Cards), ChoiceQuestions = views.Sum(v => v.ChoiceQuestions), PoolQuestions = views.Sum(v => v.PoolQuestions),
            TopicQuestions = (await ListQuestionsRawAsync(topicId, activeOnly: true)).Count, FinalExamQuestions = fin.Enabled ? fin.QuestionCount : 0,
            FinalExamPool = fin.Enabled ? await FinalPoolSizeAsync(views.Select(v => v.ModuleId), fin, topicQs) : 0,
        };
    }

    // ─── Versionen lesen ────────────────────────────────────────────────────

    private static TopicVersionInfo MapVersion(Dictionary<string, string?> r)
    {
        var v = new TopicVersionInfo
        {
            Id = r["id"] ?? "", TopicId = r.GetValueOrDefault("topic_id") ?? "", VersionNumber = DbValue.ToInt(r.GetValueOrDefault("version_number")),
            Status = r.GetValueOrDefault("status") ?? "", GlobalStatus = r.GetValueOrDefault("global_status") ?? TopicGlobalStatuses.None,
            Name = r.GetValueOrDefault("name") ?? "", Description = r.GetValueOrDefault("description") ?? "", SnapshotJson = r.GetValueOrDefault("snapshot_json") ?? "{}",
            CreatedAt = DbValue.ToDateTime(r.GetValueOrDefault("created_at")), PublishedBy = r.GetValueOrDefault("published_by"),
            PublishedAt = r.GetValueOrDefault("published_at") is { Length: > 0 } p ? DbValue.ToDateTime(p) : null,
            SubmittedAt = r.GetValueOrDefault("submitted_at") is { Length: > 0 } s ? DbValue.ToDateTime(s) : null,
            DecidedAt = r.GetValueOrDefault("decided_at") is { Length: > 0 } d ? DbValue.ToDateTime(d) : null,
            DecidedBy = r.GetValueOrDefault("decided_by"), DecisionReason = r.GetValueOrDefault("decision_reason") ?? "",
            PublishedByName = r.GetValueOrDefault("published_by_name") ?? "", OwnerName = r.GetValueOrDefault("owner_name") ?? "",
        };
        v.Stats = ReadSnapshot(v)?.Stats ?? new TopicStats();
        return v;
    }

    internal static TopicSnapshot? ReadSnapshot(TopicVersionInfo v)
    {
        try { return JsonSerializer.Deserialize<TopicSnapshot>(v.SnapshotJson, AppJson.Options); }
        catch (JsonException) { return null; }
    }

    private const string VersionSelect = """
        SELECT v.*, pu.display_name AS published_by_name, ou.display_name AS owner_name
        FROM topic_versions v
        JOIN topics t ON t.id = v.topic_id
        LEFT JOIN users pu ON CAST(pu.id AS TEXT) = v.published_by
        LEFT JOIN users ou ON ou.id = t.owner_user_id
        """;

    private async Task<TopicVersionInfo?> LoadVersionAsync(string topicId, int versionNumber)
    {
        var rows = await _db.QueryAsync(VersionSelect + " WHERE v.topic_id = @t AND v.version_number = @n AND v.status = 'published'", new (string, object?)[] { ("t", topicId), ("n", versionNumber) });
        return rows.Count == 0 ? null : MapVersion(rows[0]);
    }

    private async Task<TopicVersionInfo?> LoadVersionByIdAsync(string versionId)
    {
        if (!UserService.IsGuid(versionId)) return null;
        var rows = await _db.QueryAsync(VersionSelect + " WHERE v.id = @id", new[] { ("id", (object?)versionId) });
        return rows.Count == 0 ? null : MapVersion(rows[0]);
    }

    private async Task<TopicVersionInfo?> LoadLatestPublishedAsync(string topicId)
    {
        var rows = await _db.QueryAsync(VersionSelect + " WHERE v.topic_id = @t AND v.status = 'published' ORDER BY v.version_number DESC LIMIT 1", new[] { ("t", (object?)topicId) });
        return rows.Count == 0 ? null : MapVersion(rows[0]);
    }

    /// <summary>Versionshistorie (nur Eigentümer): alle veröffentlichten Versionen, neueste zuerst; fehlgeschlagene Veröffentlichungen werden nicht angezeigt.</summary>
    public async Task<List<TopicVersionInfo>> ListVersionsAsync(string ownerId, string topicId)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId, allowArchived: true);
        return (await _db.QueryAsync(VersionSelect + " WHERE v.topic_id = @t AND v.status = 'published' ORDER BY v.version_number DESC", new[] { ("t", (object?)topic.Id) })).Select(MapVersion).ToList();
    }

    // ─── Detail ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Thema aus Sicht des Benutzers. Eigentümer ohne Versionsangabe: Entwurf. Sonst eine veröffentlichte Version (Eigentümer: jede; Freigabe/Zuweisung: jede veröffentlichte;
    /// nur global: die aktive globale Version). Alles andere verhält sich wie „nicht gefunden“.
    /// </summary>
    public async Task<TopicDetail> GetDetailAsync(string userId, string topicId, int? version = null)
    {
        var topic = await GetTopicRawAsync(topicId) ?? throw new KeyNotFoundException("Thema nicht gefunden.");
        var isOwner = string.Equals(topic.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase);

        if (isOwner && version is null)
        {
            var detail = new TopicDetail
            {
                Topic = topic, Version = 0, IsOwner = true, Section = TopicSections.Own, Modules = await DraftModuleViewsAsync(topic.Id, userId),
                FinalExam = (await LoadExamConfigAsync(topic.Id)).FinalExam, Stats = await DraftStatsAsync(topic.Id, userId), GlobalStatus = await LatestGlobalStatusAsync(topic.Id),
            };
            await FillDraftChangesAndWarningsAsync(detail, userId);
            return detail;
        }

        var section = await SectionForAsync(userId, topic);
        var ver = isOwner
            ? (version is int vn && vn > 0 ? await LoadVersionAsync(topic.Id, vn) : await LoadLatestPublishedAsync(topic.Id))
            : await ResolveVersionForLearnerAsync(userId, topic, section, version);
        if (ver is null) throw new KeyNotFoundException("Thema nicht gefunden.");
        return DetailFromVersion(topic, ver, isOwner, section);
    }

    /// <summary>
    /// Ansicht für die Themenseite. Ohne Versionsangabe zeigt der Eigentümer die aktuelle Veröffentlichung, solange der Entwurf nicht tatsächlich abweicht
    /// (Revisionszähler plus Inhaltskennung aller Module, kein Zeitstempel); ein nie veröffentlichtes Thema zeigt den Entwurf. <c>version = 0</c> verlangt ausdrücklich den Entwurf.
    /// <see cref="GetDetailAsync"/> bleibt unverändert (Entwurf ohne Versionsangabe; Editor und API).
    /// </summary>
    public async Task<TopicDetail> GetViewAsync(string userId, string topicId, int? version = null)
    {
        var topic = await GetTopicRawAsync(topicId) ?? throw new KeyNotFoundException("Thema nicht gefunden.");
        if (!string.Equals(topic.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase)) return await GetDetailAsync(userId, topicId, version is > 0 ? version : null);

        var latest = await LoadLatestPublishedAsync(topic.Id);
        if (version is > 0 || (version is null && latest is not null))
        {
            var draft = await GetDetailAsync(userId, topicId);
            if (version is null && draft.UnpublishedChanges) { draft.DraftDiffers = true; return draft; }
            var d = await GetDetailAsync(userId, topicId, version ?? latest!.VersionNumber);
            d.DraftDiffers = draft.UnpublishedChanges;
            return d;
        }
        var dr = await GetDetailAsync(userId, topicId);
        dr.DraftDiffers = dr.UnpublishedChanges || latest is null;
        return dr;
    }

    private static TopicDetail DetailFromVersion(TopicInfo topic, TopicVersionInfo ver, bool isOwner, string section)
    {
        var snap = ReadSnapshot(ver) ?? new TopicSnapshot();
        var t = new TopicInfo
        {
            Id = topic.Id, OwnerUserId = topic.OwnerUserId, OwnerName = topic.OwnerName, Name = snap.Name.Length > 0 ? snap.Name : topic.Name,
            Description = snap.Description, Status = topic.Status, DraftRevision = topic.DraftRevision, PublishedRevision = topic.PublishedRevision,
            LatestVersion = topic.LatestVersion, GlobalVersion = topic.GlobalVersion, CreatedAt = topic.CreatedAt, UpdatedAt = topic.UpdatedAt,
        };
        return new TopicDetail
        {
            Topic = t, Version = ver.VersionNumber, VersionId = ver.Id, IsOwner = isOwner, Section = section, Modules = snap.Modules, FinalExam = snap.ExamConfig.FinalExam,
            Stats = snap.Stats, GlobalStatus = ver.GlobalStatus, ExamModuleId = snap.ExamModuleId,
        };
    }

    private async Task FillDraftChangesAndWarningsAsync(TopicDetail d, string userId)
    {
        var latest = await LoadLatestPublishedAsync(d.Topic.Id);
        var previous = latest is null ? new Dictionary<string, string>() : await PreviousModuleCopiesAsync(d.Topic.Id);
        d.UnpublishedChanges = latest is null ? d.Modules.Count > 0 : await DraftDiffersFromLatestAsync(d.Topic, latest);
        var pins = (await ListDraftModuleRowsAsync(d.Topic.Id)).ToDictionary(r => r.ModuleId, r => r.Version, StringComparer.OrdinalIgnoreCase);
        foreach (var m in d.Modules.Where(m => !m.Unavailable))
        {
            // Änderung = festgelegte Modulversion weicht von der in der letzten Themenversion eingefrorenen Kopie ab (ADR 0030) — neuere Modulstände allein ändern den Entwurf nicht
            var frozen = pins.GetValueOrDefault(m.ModuleId) is int pv ? (await _moduleVersions.GetVersionRawAsync(m.ModuleId, pv))?.SnapshotModuleId : null;
            if (latest is not null && (frozen is null || !previous.TryGetValue(m.ModuleId, out var prev) || !string.Equals(prev, frozen, StringComparison.OrdinalIgnoreCase)))
            {
                d.UnpublishedChanges = true;
                d.ChangedModuleNames.Add(m.Name);
            }
        }
        if (latest is not null && previous.Keys.Except(d.Modules.Select(m => m.ModuleId), StringComparer.OrdinalIgnoreCase).Any()) d.UnpublishedChanges = true;
        foreach (var m in d.Modules.Where(m => !m.Unavailable))
        {
            var acc = await _repo.ResolveModuleAccessAsync(userId, m.ModuleId);
            if (acc is not null && await CanUseModuleInTopicAsync(userId, m.ModuleId) is not null)
            {
                m.RequiresOwnCopy = true;
                d.Warnings.Add($"Das Modul „{m.Name}“ ist kein eigenes Modul. Ersetze es durch eine eigene Kopie, bevor du veröffentlichst.");
            }
        }
        foreach (var m in d.Modules.Where(m => m.Unavailable)) d.Warnings.Add("Ein zugeordnetes Modul ist nicht mehr verfügbar und muss vor der Veröffentlichung entfernt werden.");
        foreach (var m in d.Modules.Where(m => !m.Unavailable && m.Exam.Enabled))
        {
            var avail = Math.Max(0, m.PoolQuestions);
            if (avail < m.Exam.QuestionCount) d.Warnings.Add($"Modulprüfung „{m.Name}“: {m.Exam.QuestionCount} Fragen vorgesehen, aber nur {avail} im Fragenpool.");
        }
        if (d.FinalExam.Enabled && d.Stats.FinalExamPool < d.FinalExam.QuestionCount)
            d.Warnings.Add($"Abschlussprüfung: {d.FinalExam.QuestionCount} Fragen vorgesehen, aber nur {d.Stats.FinalExamPool} passende Fragen verfügbar.");
        if (d.Modules.Count == 0) d.Warnings.Add("Das Thema enthält noch kein Modul.");
    }

    /// <summary>Inhaltskennungen der Module der zuletzt veröffentlichten Version, nach ursprünglicher Modul-ID.</summary>
    /// <summary>Quellmodul → ID der eingefrorenen Modulkopie in der neuesten veröffentlichten Themenversion.</summary>
    private async Task<Dictionary<string, string>> PreviousModuleCopiesAsync(string topicId)
    {
        var latest = await LoadLatestPublishedAsync(topicId);
        if (latest is null) return new();
        var rows = await _db.QueryAsync("SELECT source_module_id, module_id FROM topic_version_modules WHERE version_id = @v AND role = 'module'", new[] { ("v", (object?)latest.Id) });
        return rows.Where(r => !string.IsNullOrEmpty(r.GetValueOrDefault("source_module_id"))).ToDictionary(r => r["source_module_id"]!, r => r["module_id"] ?? "", StringComparer.OrdinalIgnoreCase);
    }

    // ─── Veröffentlichen ────────────────────────────────────────────────────

    /// <summary>
    /// Veröffentlicht den Entwurf als neue, unveränderliche Version. Verhindert doppelte Versionsnummern (UNIQUE-Schutz plus laufende Veröffentlichung), erkennt fehlende
    /// Änderungen und friert Module (nur geänderte werden neu kopiert), Fragenpools und Prüfungskonfiguration ein.
    /// </summary>
    public async Task<TopicVersionInfo> PublishAsync(string ownerId, string topicId, bool submitGlobal = false)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId);
        var rows = await ListDraftModuleRowsAsync(topic.Id);
        if (rows.Count == 0) throw new ArgumentException("Das Thema enthält noch kein Modul — ordne mindestens ein Modul zu.");

        var sources = new List<ModuleInfo>();
        foreach (var row in rows)
        {
            var access = await _repo.ResolveModuleAccessAsync(ownerId, row.ModuleId);
            if (access is null)
                throw new ArgumentException("Ein zugeordnetes Modul ist nicht mehr verfügbar. Entferne es aus dem Thema, bevor du veröffentlichst.");
            if (await CanUseModuleInTopicAsync(ownerId, row.ModuleId) is { } notOwn)
                throw new ArgumentException($"Das Modul „{access.Module.Name}“ ist kein eigenes privates Modul: {notOwn} Über „Durch eigene Kopie ersetzen“ stellst du die Zuordnung um.");
            sources.Add(access.Module);
        }
        var cfg = await LoadExamConfigAsync(topic.Id);
        var names = sources.ToDictionary(s => s.Id, s => s.Name, StringComparer.OrdinalIgnoreCase);
        var errors = cfg.Validate(names);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));

        // Gibt es überhaupt etwas Neues? (Themenänderung oder geänderter Modulinhalt)
        var latest = await LoadLatestPublishedAsync(topic.Id);
        var previous = await PreviousModuleCopiesAsync(topic.Id);
        // festgelegte Modulversionen (ADR 0030): die eingefrorene Kopie der Modulversion ist der Inhalt der Themenversion — nie der veränderliche aktuelle Stand
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // Quellmodul → ID der eingefrorenen Modulkopie
        var versionOf = new Dictionary<string, ModuleVersionInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var ver = row.Version is int pv ? await _moduleVersions.GetVersionRawAsync(row.ModuleId, pv) : null;
            if (ver is null) throw new ArgumentException("Für ein Modul ist keine Modulversion festgelegt. Öffne das Thema im Editor und übernimm eine Modulversion.");
            hashes[row.ModuleId] = ver.SnapshotModuleId; versionOf[row.ModuleId] = ver;
        }
        var fingerprint = await ComputeDraftFingerprintAsync(topic);
        var changed = await DraftDiffersFromLatestAsync(topic, latest)
                      || sources.Any(s => !previous.TryGetValue(s.Id, out var h) || !string.Equals(h, hashes[s.Id], StringComparison.OrdinalIgnoreCase)) || previous.Keys.Except(hashes.Keys, StringComparer.OrdinalIgnoreCase).Any();
        if (!changed) throw new InvalidOperationException($"Keine Änderungen seit Version {latest!.VersionNumber}. Ändere das Thema oder eines seiner Module, bevor du eine neue Version veröffentlichst.");
        if ((await _db.QueryAsync("SELECT 1 FROM topic_versions WHERE topic_id = @t AND status = 'building'", new[] { ("t", (object?)topic.Id) })).Count > 0)
            throw new InvalidOperationException("Eine Veröffentlichung dieses Themas läuft bereits.");

        // Versionsnummer atomar belegen: die UNIQUE-Bedingung (topic_id, version_number) verhindert doppelte Nummern bei gleichzeitigen Aufrufen
        var versionId = Guid.NewGuid().ToString();
        var next = DbValue.ToInt((await _db.QueryAsync("SELECT COALESCE(MAX(version_number), 0) AS n FROM topic_versions WHERE topic_id = @t", new[] { ("t", (object?)topic.Id) }))[0]["n"]) + 1;
        var revisionAtStart = topic.DraftRevision;
        try
        {
            await _db.ExecuteAsync("""
                INSERT INTO topic_versions (id, topic_id, version_number, status, global_status, name, description, snapshot_json, created_at, created_by)
                VALUES (@id, @t, @n, 'building', 'none', @name, @d, '{}', @now, @u)
                """, new (string, object?)[] { ("id", versionId), ("t", topic.Id), ("n", next), ("name", topic.Name), ("d", topic.Description), ("now", DateTime.UtcNow), ("u", ownerId) });
        }
        catch (Exception)
        {
            // UNIQUE (topic_id, version_number): gleichzeitig hat ein anderer Aufruf diese Nummer belegt
            if ((await _db.QueryAsync("SELECT 1 FROM topic_versions WHERE topic_id = @t AND version_number = @n", new (string, object?)[] { ("t", topic.Id), ("n", next) })).Count > 0)
                throw new InvalidOperationException("Gleichzeitig wurde eine andere Version veröffentlicht. Lade das Thema neu.");
            throw;
        }

        // Wettlauf-Schutz nach der Belegung: Läuft eine ältere Veröffentlichung noch oder hat eine parallele Veröffentlichung inzwischen genau diesen Stand eingefroren
        // (die Prüfung oben las ggf. veralteten Zustand), wird die eigene Zeile verworfen — so entsteht bei gleichzeitigen Aufrufen genau eine Version.
        var fresh = await RequireOwnTopicAsync(ownerId, topicId);
        var olderBuilding = (await _db.QueryAsync("SELECT 1 FROM topic_versions WHERE topic_id = @t AND status = 'building' AND version_number < @n", new (string, object?)[] { ("t", topic.Id), ("n", next) })).Count > 0;
        var freshPrevious = await PreviousModuleCopiesAsync(topic.Id);
        var freshLatest = await LoadLatestPublishedAsync(topic.Id);
        var stillChanged = await DraftDiffersFromLatestAsync(fresh, freshLatest)
                           || sources.Any(s => !freshPrevious.TryGetValue(s.Id, out var h) || !string.Equals(h, hashes[s.Id], StringComparison.OrdinalIgnoreCase)) || freshPrevious.Keys.Except(hashes.Keys, StringComparer.OrdinalIgnoreCase).Any();
        if (olderBuilding || !stillChanged)
        {
            await _db.ExecuteAsync("DELETE FROM topic_versions WHERE id = @id AND status = 'building'", new[] { ("id", (object?)versionId) });
            throw new InvalidOperationException(olderBuilding ? "Eine Veröffentlichung dieses Themas läuft bereits." : $"Keine Änderungen seit Version {freshLatest!.VersionNumber}. Ändere das Thema oder eines seiner Module, bevor du eine neue Version veröffentlichst.");
        }

        try
        {
            var snapshot = await BuildSnapshotAsync(topic, versionId, next, sources, hashes, versionOf, cfg);
            snapshot.DraftFingerprint = fingerprint;
            // Reihenfolge: erst die Revision, dann der Status — eine parallele Veröffentlichung sieht so entweder „läuft noch“ oder den vollständigen neuen Stand
            await _db.ExecuteAsync("UPDATE topics SET published_revision = @r, updated_at = @n WHERE id = @t AND published_revision < @r", new (string, object?)[] { ("r", revisionAtStart), ("n", DateTime.UtcNow), ("t", topic.Id) });
            await _db.ExecuteAsync("""
                UPDATE topic_versions SET status = 'published', snapshot_json = @j, published_at = @n, published_by = @u WHERE id = @id AND status = 'building'
                """, new (string, object?)[] { ("j", JsonSerializer.Serialize(snapshot, AppJson.Options)), ("n", DateTime.UtcNow), ("u", ownerId), ("id", versionId) });
        }
        catch
        {
            await _db.ExecuteAsync("UPDATE topics SET published_revision = @old WHERE id = @t AND published_revision = @r", new (string, object?)[] { ("old", topic.PublishedRevision), ("r", revisionAtStart), ("t", topic.Id) });
            await _db.ExecuteAsync("UPDATE topic_versions SET status = 'failed' WHERE id = @id AND status = 'building'", new[] { ("id", (object?)versionId) });
            throw;
        }
        await _users.WriteAuditAsync(ownerId, "topic.publish", topic.Id, $"version={next}");
        var published = (await LoadVersionAsync(topic.Id, next))!;
        if (submitGlobal) await SubmitGlobalAsync(ownerId, topic.Id, next);
        return published;
    }

    private async Task<TopicSnapshot> BuildSnapshotAsync(TopicInfo topic, string versionId, int versionNumber, List<ModuleInfo> sources, Dictionary<string, string> frozenIds, Dictionary<string, ModuleVersionInfo> versionOf, TopicExamConfig draftCfg)
    {
        var snap = new TopicSnapshot { Name = topic.Name, Description = topic.Description };
        var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // Quelle → Kopie

        for (var i = 0; i < sources.Count; i++)
        {
            var src = sources[i];
            // Die eingefrorene Kopie gehört der festgelegten Modulversion und wird von allen Themenversionen mit dieser Modulversion geteilt
            // (gleiche Karten-IDs ⇒ Lernstand und bestandene Modulprüfungen bleiben erhalten). Es entsteht keine neue Kopie des aktuellen Moduls (ADR 0030).
            var copyId = frozenIds[src.Id];
            idMap[src.Id] = copyId;
            await _db.ExecuteAsync("INSERT INTO topic_version_modules (version_id, module_id, role, position, source_module_id, content_hash) VALUES (@v, @m, 'module', @p, @s, @h)",
                new (string, object?)[] { ("v", versionId), ("m", copyId), ("p", i), ("s", src.Id), ("h", versionOf[src.Id].ContentHash) });
            var copy = (await _repo.GetModuleRawAsync(copyId))!;
            var exam = draftCfg.ModuleExams.TryGetValue(src.Id, out var e) ? e : new TopicModuleExamConfig();
            snap.Modules.Add(await ViewOfModuleAsync(copy, i, exam));
            snap.Modules[^1].SourceModuleId = src.Id;
            snap.Modules[^1].Version = versionOf[src.Id].VersionNumber;
            snap.ExamConfig.ModuleExams[copyId] = exam;
        }

        var fin = draftCfg.FinalExam;
        fin.ManualCounts = fin.ManualCounts.Where(kv => idMap.ContainsKey(kv.Key)).ToDictionary(kv => idMap[kv.Key], kv => kv.Value);
        snap.ExamConfig.FinalExam = fin;

        var topicQuestions = await ListQuestionsRawAsync(topic.Id, activeOnly: true);
        if (fin.Enabled)
        {
            var exam = await _repo.CreateTopicExamModuleAsync($"Abschlussprüfung – {topic.Name}", topic.Name, topicQuestions, fin.ToQuizConfig(), versionNumber);
            snap.ExamModuleId = exam.Id;
            await _db.ExecuteAsync("INSERT INTO topic_version_modules (version_id, module_id, role, position, source_module_id, content_hash) VALUES (@v, @m, 'final_exam', 1000, NULL, '')",
                new (string, object?)[] { ("v", versionId), ("m", exam.Id) });
        }
        var allowed = fin.AllowedTypes();
        snap.Stats = new TopicStats
        {
            Modules = snap.Modules.Count, Cards = snap.Modules.Sum(m => m.Cards), ChoiceQuestions = snap.Modules.Sum(m => m.ChoiceQuestions), PoolQuestions = snap.Modules.Sum(m => m.PoolQuestions),
            TopicQuestions = topicQuestions.Count, FinalExamQuestions = fin.Enabled ? fin.QuestionCount : 0,
            FinalExamPool = fin.Enabled ? await FinalPoolSizeAsync(snap.Modules.Select(m => m.ModuleId), fin, topicQuestions.Count(q => allowed.Contains(q.Type))) : 0,
        };
        return snap;
    }

    // ─── Globale Veröffentlichung ───────────────────────────────────────────

    /// <summary>
    /// Reicht eine veröffentlichte Version zur globalen Veröffentlichung ein (Status <c>pending_approval</c>). Bis zur Freigabe bleibt die bisherige globale Version aktiv;
    /// pro Thema ist höchstens eine Einreichung offen.
    /// </summary>
    public async Task SubmitGlobalAsync(string ownerId, string topicId, int versionNumber)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId);
        var ver = await LoadVersionAsync(topic.Id, versionNumber) ?? throw new KeyNotFoundException("Version nicht gefunden.");
        var n = await _db.ExecuteAsync("""
            UPDATE topic_versions SET global_status = 'pending_approval', submitted_at = @n, decided_at = NULL, decided_by = NULL, decision_reason = ''
            WHERE id = @id AND status = 'published' AND global_status IN ('none', 'rejected')
              AND NOT EXISTS (SELECT 1 FROM topic_versions p WHERE p.topic_id = @t AND p.global_status = 'pending_approval')
            """, new (string, object?)[] { ("n", DateTime.UtcNow), ("id", ver.Id), ("t", topic.Id) });
        if (n == 0)
        {
            var pending = (await _db.QueryAsync("SELECT 1 FROM topic_versions WHERE topic_id = @t AND global_status = 'pending_approval'", new[] { ("t", (object?)topic.Id) })).Count > 0;
            throw new InvalidOperationException(pending ? "Für dieses Thema liegt bereits eine Einreichung zur Freigabe vor." : "Diese Version kann nicht eingereicht werden (bereits global veröffentlicht oder ersetzt).");
        }
        await _users.WriteAuditAsync(ownerId, "topic.global.submit", topic.Id, $"version={versionNumber}");
    }

    /// <summary>Offene Einreichungen globaler Themenversionen (nur Administrator).</summary>
    public async Task<List<TopicVersionInfo>> ListPendingGlobalAsync(string actorId)
    {
        await _users.RequireRoleAsync(actorId, Roles.Admin);
        return (await _db.QueryAsync(VersionSelect + " WHERE v.global_status = 'pending_approval' AND v.status = 'published' ORDER BY v.submitted_at")).Select(MapVersion).ToList();
    }

    /// <summary>Veröffentlichungsverlauf aller globalen Themenversionen (Administrator oder Auditor, lesend).</summary>
    public async Task<List<TopicVersionInfo>> ListGlobalHistoryAsync(string actorId)
    {
        await _users.RequireAnyRoleAsync(actorId, Roles.Admin, Roles.Auditor);
        return (await _db.QueryAsync(VersionSelect + " WHERE v.global_status <> 'none' AND v.status = 'published' ORDER BY COALESCE(v.decided_at, v.submitted_at) DESC, v.version_number DESC LIMIT 200")).Select(MapVersion).ToList();
    }

    /// <summary>Vorschau der <b>eingefrorenen</b> Version für den Administrator. Private Originalmodule werden nie gelesen.</summary>
    public async Task<TopicDetail> GetVersionForReviewAsync(string actorId, string versionId)
    {
        await _users.RequireAnyRoleAsync(actorId, Roles.Admin, Roles.Auditor);
        var ver = await LoadVersionByIdAsync(versionId);
        if (ver is null || ver.Status != TopicVersionStatuses.Published || ver.GlobalStatus == TopicGlobalStatuses.None) throw new KeyNotFoundException("Version nicht gefunden.");
        var topic = await GetTopicRawAsync(ver.TopicId) ?? throw new KeyNotFoundException("Version nicht gefunden.");
        return DetailFromVersion(topic, ver, isOwner: false, TopicSections.Global);
    }

    /// <summary>Karten eines Moduls der zur Prüfung vorliegenden Version (nur Administrator/Auditor, nur eingefrorene Kopien einer eingereichten Version).</summary>
    public async Task<List<Card>> GetReviewModuleCardsAsync(string actorId, string versionId, string moduleId)
    {
        await _users.RequireAnyRoleAsync(actorId, Roles.Admin, Roles.Auditor);
        var ver = await LoadVersionByIdAsync(versionId);
        if (ver is null || ver.GlobalStatus == TopicGlobalStatuses.None || !UserService.IsGuid(moduleId)) throw new KeyNotFoundException("Modul nicht gefunden.");
        if ((await _db.QueryAsync("SELECT 1 FROM topic_version_modules WHERE version_id = @v AND module_id = @m", new (string, object?)[] { ("v", ver.Id), ("m", moduleId) })).Count == 0)
            throw new KeyNotFoundException("Modul nicht gefunden.");
        return await _repo.ListActiveCardsRawAsync(moduleId);
    }

    /// <summary>Gibt eine eingereichte Version global frei. Atomar: nur ein Administrator kann eine offene Einreichung entscheiden; die bisher aktive globale Version wird „ersetzt“.</summary>
    public async Task ApproveGlobalAsync(string actorId, string versionId)
    {
        var actor = await _users.RequireRoleAsync(actorId, Roles.Admin);
        var ver = await LoadVersionByIdAsync(versionId) ?? throw new KeyNotFoundException("Version nicht gefunden.");
        var claimed = await _db.ExecuteAsync("UPDATE topic_versions SET global_status = 'active', decided_at = @n, decided_by = @a WHERE id = @id AND global_status = 'pending_approval' AND status = 'published'",
            new (string, object?)[] { ("n", DateTime.UtcNow), ("a", actor.Id), ("id", ver.Id) });
        if (claimed == 0) throw new InvalidOperationException("Die Einreichung wurde bereits entschieden oder ist nicht mehr offen.");
        await _db.ExecuteAsync("UPDATE topic_versions SET global_status = 'superseded' WHERE topic_id = @t AND id <> @id AND global_status = 'active'", new (string, object?)[] { ("t", ver.TopicId), ("id", ver.Id) });
        await _db.ExecuteAsync("UPDATE topics SET global_version = @v, updated_at = @n WHERE id = @t", new (string, object?)[] { ("v", ver.VersionNumber), ("n", DateTime.UtcNow), ("t", ver.TopicId) });
        await _users.WriteAuditAsync(actor.Id, "topic.global.approve", ver.TopicId, $"version={ver.VersionNumber}");
    }

    /// <summary>Lehnt eine eingereichte Version ab. Die bisher aktive globale Version bleibt unverändert aktiv.</summary>
    public async Task RejectGlobalAsync(string actorId, string versionId, string? reason)
    {
        var actor = await _users.RequireRoleAsync(actorId, Roles.Admin);
        var ver = await LoadVersionByIdAsync(versionId) ?? throw new KeyNotFoundException("Version nicht gefunden.");
        var text = (reason ?? "").Trim();
        if (text.Length > 500) text = text[..500];
        var claimed = await _db.ExecuteAsync("UPDATE topic_versions SET global_status = 'rejected', decided_at = @n, decided_by = @a, decision_reason = @r WHERE id = @id AND global_status = 'pending_approval'",
            new (string, object?)[] { ("n", DateTime.UtcNow), ("a", actor.Id), ("r", text), ("id", ver.Id) });
        if (claimed == 0) throw new InvalidOperationException("Die Einreichung wurde bereits entschieden oder ist nicht mehr offen.");
        await _users.WriteAuditAsync(actor.Id, "topic.global.reject", ver.TopicId, $"version={ver.VersionNumber}");
    }

    /// <summary>Nimmt die aktive globale Version zurück (nichts wird gelöscht; Ergebnisse und die Version bleiben erhalten).</summary>
    public async Task DeactivateGlobalAsync(string actorId, string topicId)
    {
        var actor = await _users.RequireRoleAsync(actorId, Roles.Admin);
        var topic = await GetTopicRawAsync(topicId) ?? throw new KeyNotFoundException("Thema nicht gefunden.");
        var n = await _db.ExecuteAsync("UPDATE topic_versions SET global_status = 'deactivated', decided_at = @n, decided_by = @a WHERE topic_id = @t AND global_status = 'active'",
            new (string, object?)[] { ("n", DateTime.UtcNow), ("a", actor.Id), ("t", topic.Id) });
        if (n == 0) throw new InvalidOperationException("Dieses Thema ist nicht global veröffentlicht.");
        await _db.ExecuteAsync("UPDATE topics SET global_version = 0, updated_at = @n WHERE id = @t", new (string, object?)[] { ("n", DateTime.UtcNow), ("t", topic.Id) });
        await _users.WriteAuditAsync(actor.Id, "topic.global.deactivate", topic.Id, "");
    }
}
