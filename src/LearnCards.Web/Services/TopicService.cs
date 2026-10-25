using System.Text.Json;
using LearnCards.Web.Api;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>
/// Themenverwaltung (ADR 0027): Thema → Lernmodule → Lernkarten und Modulprüfungen → Abschlussprüfung.
/// Dieser Teil: Anlegen/Bearbeiten, Modulzuordnung (ohne Kopien), Prüfungskonfiguration, eigene Themenfragen, Freigaben, Zuweisungen, Übersichten.
/// Besitz und Berechtigungen werden hier serverseitig erzwungen; die Benutzer-ID stammt nie aus Request-Daten (ADR 0011).
/// Veröffentlichung/Versionen: <c>TopicService.Versions.cs</c>, Fortschritt und Prüfungen: <c>TopicService.Exams.cs</c>.
/// </summary>
public partial class TopicService
{
    private readonly IDatabase _db;
    private readonly CardRepository _repo;
    private readonly UserService _users;
    private readonly QuizPoolService _pool;
    private readonly QuizService _quiz;
    private readonly ModuleTransferService _modules;

    public TopicService(IDatabase db, CardRepository repo, UserService users, QuizPoolService pool, QuizService quiz, ModuleTransferService modules)
    {
        _modules = modules;
        _db = db;
        _repo = repo;
        _users = users;
        _pool = pool;
        _quiz = quiz;
    }

    public const int MaxNameLength = 120;
    public const int MaxDescriptionLength = 2000;

    // ─── Lesen einzelner Themen ─────────────────────────────────────────────

    private const string TopicSelect = """
        SELECT t.id, t.owner_user_id, t.name, t.description, t.status, t.draft_revision, t.published_revision, t.global_version, t.created_at, t.updated_at,
               u.display_name AS owner_name,
               (SELECT MAX(v.version_number) FROM topic_versions v WHERE v.topic_id = t.id AND v.status = 'published') AS latest_version
        FROM topics t LEFT JOIN users u ON u.id = t.owner_user_id
        """;

    private static TopicInfo MapTopic(Dictionary<string, string?> r) => new()
    {
        Id = r["id"] ?? "", OwnerUserId = r.GetValueOrDefault("owner_user_id") ?? "", OwnerName = r.GetValueOrDefault("owner_name") ?? "",
        Name = r.GetValueOrDefault("name") ?? "", Description = r.GetValueOrDefault("description") ?? "", Status = r.GetValueOrDefault("status") ?? TopicStatuses.Active,
        DraftRevision = DbValue.ToInt(r.GetValueOrDefault("draft_revision")), PublishedRevision = DbValue.ToInt(r.GetValueOrDefault("published_revision")),
        LatestVersion = DbValue.ToInt(r.GetValueOrDefault("latest_version")), GlobalVersion = DbValue.ToInt(r.GetValueOrDefault("global_version")),
        CreatedAt = DbValue.ToDateTime(r.GetValueOrDefault("created_at")), UpdatedAt = DbValue.ToDateTime(r.GetValueOrDefault("updated_at")),
    };

    /// <summary>Thema ohne Berechtigungsprüfung — nur intern, nie direkt an Benutzer ausliefern.</summary>
    private async Task<TopicInfo?> GetTopicRawAsync(string topicId)
    {
        if (!UserService.IsGuid(topicId)) return null;
        var rows = await _db.QueryAsync(TopicSelect + " WHERE t.id = @id", new[] { ("id", (object?)topicId) });
        return rows.Count == 0 ? null : MapTopic(rows[0]);
    }

    /// <summary>Thema des Benutzers; fremde, unbekannte und ungültige IDs verhalten sich gleich („nicht gefunden“).</summary>
    private async Task<TopicInfo> RequireOwnTopicAsync(string userId, string topicId, bool allowArchived = false)
    {
        var topic = await GetTopicRawAsync(topicId);
        if (topic is null || !string.Equals(topic.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase)) throw new KeyNotFoundException("Thema nicht gefunden.");
        if (!allowArchived && topic.Status == TopicStatuses.Archived) throw new InvalidOperationException("Das Thema ist archiviert. Hebe die Archivierung auf, um es zu bearbeiten.");
        return topic;
    }

    private Task TouchDraftAsync(string topicId) =>
        _db.ExecuteAsync("UPDATE topics SET draft_revision = draft_revision + 1, updated_at = @n WHERE id = @id", new (string, object?)[] { ("n", DateTime.UtcNow), ("id", topicId) });

    private static string CleanName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new ArgumentException("Der Name des Themas darf nicht leer sein.");
        if (n.Length > MaxNameLength) throw new ArgumentException($"Der Name ist zu lang (höchstens {MaxNameLength} Zeichen).");
        return n;
    }

    private static string CleanDescription(string? d)
    {
        var s = (d ?? "").Trim();
        if (s.Length > MaxDescriptionLength) throw new ArgumentException($"Die Beschreibung ist zu lang (höchstens {MaxDescriptionLength} Zeichen).");
        return s;
    }

    // ─── Anlegen, Bearbeiten, Archivieren, Duplizieren ──────────────────────

    public async Task<TopicInfo> CreateAsync(string ownerId, string name, string? description, string? newId = null)
    {
        var owner = await _users.GetAsync(ownerId);
        if (owner is null || !owner.IsActive) throw new UnauthorizedAccessException("Benutzer nicht aktiv.");
        name = CleanName(name);
        description = CleanDescription(description);
        if ((await _db.QueryAsync("SELECT 1 FROM topics WHERE owner_user_id = @o AND name = @n", new (string, object?)[] { ("o", ownerId), ("n", name) })).Count > 0)
            throw new ArgumentException("Es existiert bereits ein Thema mit diesem Namen.");
        var now = DateTime.UtcNow;
        var id = newId ?? Guid.NewGuid().ToString();
        await _db.ExecuteAsync("""
            INSERT INTO topics (id, owner_user_id, name, description, status, draft_revision, published_revision, global_version, created_at, updated_at)
            VALUES (@id, @o, @n, @d, 'active', 1, 0, 0, @now, @now)
            """, new (string, object?)[] { ("id", id), ("o", ownerId), ("n", name), ("d", description), ("now", now) });
        await SaveExamConfigRawAsync(id, new TopicExamConfig());
        await _users.WriteAuditAsync(ownerId, "topic.create", id, name);
        return (await GetTopicRawAsync(id))!;
    }

    public async Task<TopicInfo> UpdateAsync(string ownerId, string topicId, string name, string? description)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId);
        name = CleanName(name);
        description = CleanDescription(description);
        if (!string.Equals(name, topic.Name, StringComparison.Ordinal)
            && (await _db.QueryAsync("SELECT 1 FROM topics WHERE owner_user_id = @o AND name = @n AND id <> @id", new (string, object?)[] { ("o", ownerId), ("n", name), ("id", topic.Id) })).Count > 0)
            throw new ArgumentException("Es existiert bereits ein Thema mit diesem Namen.");
        await _db.ExecuteAsync("UPDATE topics SET name = @n, description = @d, draft_revision = draft_revision + 1, updated_at = @now WHERE id = @id",
            new (string, object?)[] { ("n", name), ("d", description), ("now", DateTime.UtcNow), ("id", topic.Id) });
        await _users.WriteAuditAsync(ownerId, "topic.update", topic.Id, name);
        return (await GetTopicRawAsync(topic.Id))!;
    }

    /// <summary>Archiviert ein Thema bzw. hebt die Archivierung auf. Nichts wird gelöscht: Versionen, Freigaben, Zuweisungen und Ergebnisse bleiben erhalten.</summary>
    public async Task SetArchivedAsync(string ownerId, string topicId, bool archived)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId, allowArchived: true);
        await _db.ExecuteAsync("UPDATE topics SET status = @s, updated_at = @n WHERE id = @id",
            new (string, object?)[] { ("s", archived ? TopicStatuses.Archived : TopicStatuses.Active), ("n", DateTime.UtcNow), ("id", topic.Id) });
        await _users.WriteAuditAsync(ownerId, archived ? "topic.archive" : "topic.unarchive", topic.Id, topic.Name);
    }

    /// <summary>
    /// Dupliziert ein eigenes Thema als neuen Entwurf: Name, Beschreibung, Modulzuordnung (nur Module, die der Benutzer weiterhin verwenden darf), Prüfungskonfiguration
    /// und eigene Themenfragen. Freigaben, Zuweisungen, Versionen und Ergebnisse werden nicht übernommen; Module werden nicht kopiert.
    /// </summary>
    public async Task<TopicInfo> DuplicateAsync(string ownerId, string topicId)
    {
        var source = await RequireOwnTopicAsync(ownerId, topicId, allowArchived: true);
        var baseName = source.Name.Length > MaxNameLength - 12 ? source.Name[..(MaxNameLength - 12)] : source.Name;
        var name = baseName + " (Kopie)";
        for (var i = 2; (await _db.QueryAsync("SELECT 1 FROM topics WHERE owner_user_id = @o AND name = @n", new (string, object?)[] { ("o", ownerId), ("n", name) })).Count > 0; i++)
            name = $"{baseName} (Kopie {i})";
        var copy = await CreateAsync(ownerId, name, source.Description);

        var kept = new List<string>();
        foreach (var m in await ListDraftModuleRowsAsync(source.Id))
        {
            if (await CanUseModuleInTopicAsync(ownerId, m.ModuleId) is not null) continue;
            await _db.ExecuteAsync("INSERT INTO topic_modules (topic_id, module_id, sort_order, added_at) VALUES (@t, @m, @s, @n)",
                new (string, object?)[] { ("t", copy.Id), ("m", m.ModuleId), ("s", kept.Count), ("n", DateTime.UtcNow) });
            kept.Add(m.ModuleId);
        }
        var cfg = await LoadExamConfigAsync(source.Id);
        cfg.ModuleExams = cfg.ModuleExams.Where(kv => kept.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value);
        cfg.FinalExam.ManualCounts = cfg.FinalExam.ManualCounts.Where(kv => kept.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value);
        await SaveExamConfigRawAsync(copy.Id, cfg);
        foreach (var q in await ListQuestionsRawAsync(source.Id, activeOnly: false))
        {
            await CreateQuestionRawAsync(copy.Id, new QuizQuestionInput { Type = q.Type, Question = q.Question, Options = q.Options.ToList(), CorrectIndexes = q.CorrectIndexes.ToList(), Explanation = q.Explanation }, q.Active);
        }
        await TouchDraftAsync(copy.Id);
        await _users.WriteAuditAsync(ownerId, "topic.duplicate", copy.Id, $"source={source.Id}");
        return (await GetTopicRawAsync(copy.Id))!;
    }

    // ─── Module im Entwurf ──────────────────────────────────────────────────

    private sealed record DraftModuleRow(string ModuleId, int SortOrder);

    private async Task<List<DraftModuleRow>> ListDraftModuleRowsAsync(string topicId) =>
        (await _db.QueryAsync("SELECT module_id, sort_order FROM topic_modules WHERE topic_id = @t ORDER BY sort_order, added_at", new[] { ("t", (object?)topicId) }))
            .Select(r => new DraftModuleRow(r["module_id"] ?? "", DbValue.ToInt(r.GetValueOrDefault("sort_order")))).ToList();

    /// <summary>
    /// Darf der Benutzer das Modul in einem Thema verwenden? Zentrale Regel für UI, REST, MCP und JSON-Import (ADR 0028): <b>nur eigene, private Module</b> des Themeneigentümers.
    /// Nicht erlaubt: globale Originale (vorher in ein eigenes Modul klonen), mit dem Benutzer geteilte Module, eingefrorene Themenmodule, fremde Module. Liefert einen Fehlertext oder null.
    /// </summary>
    private async Task<string?> CanUseModuleInTopicAsync(string userId, string moduleId)
    {
        var access = await _repo.ResolveModuleAccessAsync(userId, moduleId);
        if (access is null) return "Das Modul wurde nicht gefunden.";
        if (access.Context == CardRepository.ContextPrivate && access.Module.Scope == "private"
            && string.Equals(access.Module.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase)) return null;
        return access.Context switch
        {
            CardRepository.ContextGlobal => "Globale Module können einem Thema nicht direkt zugeordnet werden — klone das Modul zuerst in deine eigenen Module.",
            CardRepository.ContextShared => "Mit dir geteilte Module anderer Benutzer können keinem Thema zugeordnet werden — nur eigene, private Module.",
            _ => "Dieses Modul kann keinem Thema zugeordnet werden — nur eigene, private Module.",
        };
    }

    public async Task AddModuleAsync(string ownerId, string topicId, string moduleId)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId);
        if (!UserService.IsGuid(moduleId)) throw new KeyNotFoundException("Das Modul wurde nicht gefunden.");
        var error = await CanUseModuleInTopicAsync(ownerId, moduleId);
        if (error is not null) throw error.Contains("nicht gefunden") ? new KeyNotFoundException(error) : new ArgumentException(error);   // gleiche Prüfung für UI, REST, MCP, Import
        if ((await _db.QueryAsync("SELECT 1 FROM topic_modules WHERE topic_id = @t AND module_id = @m", new (string, object?)[] { ("t", topic.Id), ("m", moduleId) })).Count > 0) return;   // idempotent
        var max = (await _db.QueryAsync("SELECT COALESCE(MAX(sort_order), -1) AS m FROM topic_modules WHERE topic_id = @t", new[] { ("t", (object?)topic.Id) })).FirstOrDefault();
        await _db.ExecuteAsync("INSERT INTO topic_modules (topic_id, module_id, sort_order, added_at) VALUES (@t, @m, @s, @n)",
            new (string, object?)[] { ("t", topic.Id), ("m", moduleId), ("s", DbValue.ToInt(max?.GetValueOrDefault("m")) + 1), ("n", DateTime.UtcNow) });
        await TouchDraftAsync(topic.Id);
        await _users.WriteAuditAsync(ownerId, "topic.module.add", topic.Id, $"module={moduleId}");
    }

    public async Task<bool> RemoveModuleAsync(string ownerId, string topicId, string moduleId)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId);
        if (!UserService.IsGuid(moduleId)) return false;
        var n = await _db.ExecuteAsync("DELETE FROM topic_modules WHERE topic_id = @t AND module_id = @m", new (string, object?)[] { ("t", topic.Id), ("m", moduleId) });
        if (n == 0) return false;
        // verbleibende Module lückenlos nummerieren; Prüfungskonfiguration des Moduls entfällt aus dem Entwurf
        var rest = await ListDraftModuleRowsAsync(topic.Id);
        for (var i = 0; i < rest.Count; i++)
            await _db.ExecuteAsync("UPDATE topic_modules SET sort_order = @s WHERE topic_id = @t AND module_id = @m", new (string, object?)[] { ("s", i), ("t", topic.Id), ("m", rest[i].ModuleId) });
        var cfg = await LoadExamConfigAsync(topic.Id);
        if (cfg.ModuleExams.Remove(moduleId) | cfg.FinalExam.ManualCounts.Remove(moduleId)) await SaveExamConfigRawAsync(topic.Id, cfg);
        await TouchDraftAsync(topic.Id);
        await _users.WriteAuditAsync(ownerId, "topic.module.remove", topic.Id, $"module={moduleId}");
        return true;
    }

    /// <summary>Setzt die Reihenfolge. Die Liste muss genau die Module des Themas enthalten (keine fremden IDs, keine Duplikate).</summary>
    public async Task ReorderModulesAsync(string ownerId, string topicId, IReadOnlyList<string> orderedModuleIds)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId);
        var current = (await ListDraftModuleRowsAsync(topic.Id)).Select(r => r.ModuleId).ToList();
        var requested = orderedModuleIds.Select(i => (i ?? "").Trim()).ToList();
        if (requested.Count != current.Count || requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Count
            || !current.All(c => requested.Contains(c, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Die Reihenfolge muss genau die Module des Themas enthalten.");
        for (var i = 0; i < requested.Count; i++)
        {
            var id = current.First(c => string.Equals(c, requested[i], StringComparison.OrdinalIgnoreCase));
            await _db.ExecuteAsync("UPDATE topic_modules SET sort_order = @s WHERE topic_id = @t AND module_id = @m", new (string, object?)[] { ("s", i), ("t", topic.Id), ("m", id) });
        }
        await TouchDraftAsync(topic.Id);
        await _users.WriteAuditAsync(ownerId, "topic.module.reorder", topic.Id, string.Join(",", requested));
    }

    /// <summary>Eigene private Module, die dem Thema noch nicht zugeordnet sind (Suche nach Name; für die Auswahl im Editor).</summary>
    public async Task<List<ModuleInfo>> SearchAddableModulesAsync(string ownerId, string topicId, string? query)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId);
        var taken = (await ListDraftModuleRowsAsync(topic.Id)).Select(r => r.ModuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var q = (query ?? "").Trim();
        var all = await _repo.ListModulesAsync(ownerId);
        return all.Where(m => !taken.Contains(m.Id) && (q.Length == 0 || m.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).Take(30).ToList();
    }

    // ─── Prüfungskonfiguration und eigene Themenfragen (Entwurf) ────────────

    public async Task<TopicExamConfig> LoadExamConfigAsync(string topicId)
    {
        var rows = await _db.QueryAsync("SELECT config_json FROM topic_exam_configs WHERE topic_id = @t", new[] { ("t", (object?)topicId) });
        if (rows.Count == 0) return new TopicExamConfig();
        try { return JsonSerializer.Deserialize<TopicExamConfig>(rows[0]["config_json"] ?? "{}", AppJson.Options) ?? new TopicExamConfig(); }
        catch (JsonException) { return new TopicExamConfig(); }
    }

    private async Task SaveExamConfigRawAsync(string topicId, TopicExamConfig cfg)
    {
        var json = JsonSerializer.Serialize(cfg, AppJson.Options);
        var n = await _db.ExecuteAsync("UPDATE topic_exam_configs SET config_json = @j, updated_at = @n WHERE topic_id = @t", new (string, object?)[] { ("j", json), ("n", DateTime.UtcNow), ("t", topicId) });
        if (n == 0)
        {
            try { await _db.ExecuteAsync("INSERT INTO topic_exam_configs (topic_id, config_json, updated_at) VALUES (@t, @j, @n)", new (string, object?)[] { ("t", topicId), ("j", json), ("n", DateTime.UtcNow) }); }
            catch (Exception)   // paralleles Anlegen: dann aktualisieren
            {
                await _db.ExecuteAsync("UPDATE topic_exam_configs SET config_json = @j, updated_at = @n WHERE topic_id = @t", new (string, object?)[] { ("j", json), ("n", DateTime.UtcNow), ("t", topicId) });
            }
        }
    }

    /// <summary>Prüfungskonfiguration des Entwurfs für den Eigentümer; für jedes Modul des Themas ist ein Eintrag vorhanden (Standard: Prüfung inaktiv).</summary>
    public async Task<TopicExamConfig> GetExamConfigAsync(string ownerId, string topicId)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId, allowArchived: true);
        var cfg = await LoadExamConfigAsync(topic.Id);
        foreach (var m in await ListDraftModuleRowsAsync(topic.Id)) if (!cfg.ModuleExams.ContainsKey(m.ModuleId)) cfg.ModuleExams[m.ModuleId] = new TopicModuleExamConfig();
        return cfg;
    }

    public async Task<TopicExamConfig> SaveExamConfigAsync(string ownerId, string topicId, TopicExamConfig cfg)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId);
        var moduleIds = (await ListDraftModuleRowsAsync(topic.Id)).Select(r => r.ModuleId).ToList();
        var normalized = new TopicExamConfig { FinalExam = cfg.FinalExam ?? new TopicFinalExamConfig() };
        foreach (var (key, value) in cfg.ModuleExams ?? new())
        {
            var module = moduleIds.FirstOrDefault(m => string.Equals(m, key, StringComparison.OrdinalIgnoreCase));
            if (module is null) throw new ArgumentException("Die Prüfungskonfiguration enthält ein Modul, das nicht zum Thema gehört.");
            normalized.ModuleExams[module] = value ?? new TopicModuleExamConfig();
        }
        normalized.FinalExam.ManualCounts = (normalized.FinalExam.ManualCounts ?? new()).ToDictionary(
            kv => moduleIds.FirstOrDefault(m => string.Equals(m, kv.Key, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("Die manuelle Verteilung enthält ein Modul, das nicht zum Thema gehört."), kv => kv.Value);
        normalized.FinalExam.Unlock ??= new TopicUnlockRules();
        var names = (await DraftModuleViewsAsync(topic.Id, ownerId)).ToDictionary(v => v.ModuleId, v => v.Name, StringComparer.OrdinalIgnoreCase);
        var errors = normalized.Validate(names);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        await SaveExamConfigRawAsync(topic.Id, normalized);
        await TouchDraftAsync(topic.Id);
        await _users.WriteAuditAsync(ownerId, "topic.exam.config", topic.Id, "");
        return await GetExamConfigAsync(ownerId, topicId);
    }

    private static TopicQuestion MapQuestion(Dictionary<string, string?> r)
    {
        List<string> options; List<int> correct;
        try { options = JsonSerializer.Deserialize<List<string>>(r.GetValueOrDefault("options_json") ?? "[]") ?? new(); } catch (JsonException) { options = new(); }
        try { correct = JsonSerializer.Deserialize<List<int>>(r.GetValueOrDefault("correct_json") ?? "[]") ?? new(); } catch (JsonException) { correct = new(); }
        return new TopicQuestion
        {
            Id = r["id"] ?? "", TopicId = r.GetValueOrDefault("topic_id") ?? "", Type = r.GetValueOrDefault("type") ?? QuizTypes.SingleChoice, Question = r.GetValueOrDefault("question") ?? "",
            Options = options, CorrectIndexes = correct, Explanation = r.GetValueOrDefault("explanation") ?? "", Active = DbValue.ToBool(r.GetValueOrDefault("active")),
            CreatedAt = DbValue.ToDateTime(r.GetValueOrDefault("created_at")), UpdatedAt = DbValue.ToDateTime(r.GetValueOrDefault("updated_at")),
        };
    }

    private async Task<List<TopicQuestion>> ListQuestionsRawAsync(string topicId, bool activeOnly) =>
        (await _db.QueryAsync("SELECT * FROM topic_questions WHERE topic_id = @t" + (activeOnly ? " AND active = 1" : "") + " ORDER BY created_at, id", new[] { ("t", (object?)topicId) })).Select(MapQuestion).ToList();

    private async Task<TopicQuestion> CreateQuestionRawAsync(string topicId, QuizQuestionInput input, bool active)
    {
        var error = QuizPoolService.Validate(input);
        if (error is not null) throw new ArgumentException(error);
        var q = new TopicQuestion
        {
            TopicId = topicId, Type = input.Type, Question = input.Question.Trim(), Options = input.Options.Select(o => o.Trim()).ToList(),
            CorrectIndexes = input.CorrectIndexes.OrderBy(i => i).ToList(), Explanation = (input.Explanation ?? "").Trim(), Active = active,
        };
        await _db.ExecuteAsync("""
            INSERT INTO topic_questions (id, topic_id, type, question, options_json, correct_json, explanation, active, created_at, updated_at)
            VALUES (@id, @t, @type, @q, @o, @c, @e, @a, @now, @now)
            """, new (string, object?)[]
        {
            ("id", q.Id), ("t", topicId), ("type", q.Type), ("q", q.Question), ("o", JsonSerializer.Serialize(q.Options)), ("c", JsonSerializer.Serialize(q.CorrectIndexes)),
            ("e", q.Explanation), ("a", active ? 1 : 0), ("now", NextQuestionTimestamp()),
        });
        return q;
    }

    private static long _lastQuestionMs;

    /// <summary>Streng aufsteigende Zeitstempel (Millisekunden) für Themenfragen: die Reihenfolge (ORDER BY created_at) bleibt auch bei Importen im selben Takt stabil.</summary>
    private static DateTime NextQuestionTimestamp()
    {
        var now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        long prev, next;
        do { prev = Interlocked.Read(ref _lastQuestionMs); next = Math.Max(now, prev + 1); }
        while (Interlocked.CompareExchange(ref _lastQuestionMs, next, prev) != prev);
        return new DateTime(next * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }

    public async Task<List<TopicQuestion>> ListQuestionsAsync(string ownerId, string topicId)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId, allowArchived: true);
        return await ListQuestionsRawAsync(topic.Id, activeOnly: false);
    }

    public async Task<TopicQuestion> CreateQuestionAsync(string ownerId, string topicId, QuizQuestionInput input)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId);
        if ((await _db.QueryAsync("SELECT COUNT(*) AS n FROM topic_questions WHERE topic_id = @t", new[] { ("t", (object?)topic.Id) })).Select(r => DbValue.ToInt(r["n"])).FirstOrDefault() >= 500)
            throw new ArgumentException("Ein Thema kann höchstens 500 eigene Fragen enthalten.");
        var q = await CreateQuestionRawAsync(topic.Id, input, active: true);
        await TouchDraftAsync(topic.Id);
        await _users.WriteAuditAsync(ownerId, "topic.question.create", topic.Id, q.Id);
        return q;
    }

    public async Task<TopicQuestion> UpdateQuestionAsync(string ownerId, string topicId, string questionId, QuizQuestionInput input, bool? active = null)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId);
        var existing = (await ListQuestionsRawAsync(topic.Id, false)).FirstOrDefault(q => string.Equals(q.Id, questionId, StringComparison.OrdinalIgnoreCase))
                       ?? throw new KeyNotFoundException("Frage nicht gefunden.");
        var error = QuizPoolService.Validate(input);
        if (error is not null) throw new ArgumentException(error);
        await _db.ExecuteAsync("UPDATE topic_questions SET type=@type, question=@q, options_json=@o, correct_json=@c, explanation=@e, active=@a, updated_at=@n WHERE id=@id AND topic_id=@t",
            new (string, object?)[]
            {
                ("type", input.Type), ("q", input.Question.Trim()), ("o", JsonSerializer.Serialize(input.Options.Select(o => o.Trim()).ToList())),
                ("c", JsonSerializer.Serialize(input.CorrectIndexes.OrderBy(i => i).ToList())), ("e", (input.Explanation ?? "").Trim()),
                ("a", (active ?? existing.Active) ? 1 : 0), ("n", DateTime.UtcNow), ("id", existing.Id), ("t", topic.Id),
            });
        await TouchDraftAsync(topic.Id);
        await _users.WriteAuditAsync(ownerId, "topic.question.update", topic.Id, existing.Id);
        return (await ListQuestionsRawAsync(topic.Id, false)).First(q => q.Id == existing.Id);
    }

    /// <summary>Entfernt eine eigene Frage aus dem Entwurf (bereits veröffentlichte Versionen behalten ihre eingefrorene Kopie).</summary>
    public async Task<bool> DeleteQuestionAsync(string ownerId, string topicId, string questionId)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId);
        if (!UserService.IsGuid(questionId)) return false;
        var n = await _db.ExecuteAsync("DELETE FROM topic_questions WHERE id = @id AND topic_id = @t", new (string, object?)[] { ("id", questionId), ("t", topic.Id) });
        if (n > 0) { await TouchDraftAsync(topic.Id); await _users.WriteAuditAsync(ownerId, "topic.question.delete", topic.Id, questionId); }
        return n > 0;
    }

    // ─── Freigaben ──────────────────────────────────────────────────────────

    /// <summary>Gibt ein veröffentlichtes Thema für einen aktiven Benutzer zum Lernen frei (Leserecht und Prüfungen; keine Bearbeitung der Module).</summary>
    public async Task GrantShareAsync(string ownerId, string topicId, string targetUserId)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId);
        if (!UserService.IsGuid(targetUserId)) throw new KeyNotFoundException("Empfänger nicht gefunden.");
        if (string.Equals(ownerId, targetUserId, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Ein Thema kann nicht für sich selbst freigegeben werden.");
        var target = await _users.GetAsync(targetUserId);
        if (target is null || !target.IsActive) throw new KeyNotFoundException("Empfänger nicht gefunden.");
        if (topic.LatestVersion == 0) throw new InvalidOperationException("Veröffentliche zuerst eine Version des Themas — Freigaben beziehen sich auf veröffentlichte Versionen.");

        var existing = await _db.QueryAsync("SELECT id FROM topic_shares WHERE topic_id = @t AND target_user_id = @u", new (string, object?)[] { ("t", topic.Id), ("u", target.Id) });
        if (existing.Count > 0)
            await _db.ExecuteAsync("UPDATE topic_shares SET revoked_at = NULL, created_by = @o WHERE topic_id = @t AND target_user_id = @u", new (string, object?)[] { ("o", ownerId), ("t", topic.Id), ("u", target.Id) });
        else
            await _db.ExecuteAsync("INSERT INTO topic_shares (id, topic_id, target_user_id, permission, created_by, created_at) VALUES (@id, @t, @u, 'learn', @o, @n)",
                new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("t", topic.Id), ("u", target.Id), ("o", ownerId), ("n", DateTime.UtcNow) });
        await _users.WriteAuditAsync(ownerId, "topic.share.grant", topic.Id, $"user={target.Id}");
    }

    public async Task<bool> RevokeShareAsync(string ownerId, string topicId, string targetUserId)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId, allowArchived: true);
        if (!UserService.IsGuid(targetUserId)) return false;
        var n = await _db.ExecuteAsync("UPDATE topic_shares SET revoked_at = @n WHERE topic_id = @t AND target_user_id = @u AND revoked_at IS NULL",
            new (string, object?)[] { ("n", DateTime.UtcNow), ("t", topic.Id), ("u", targetUserId) });
        if (n > 0) await _users.WriteAuditAsync(ownerId, "topic.share.revoke", topic.Id, $"user={targetUserId}");
        return n > 0;
    }

    public async Task<List<TopicShareInfo>> ListSharesAsync(string ownerId, string topicId)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId, allowArchived: true);
        return (await _db.QueryAsync("""
            SELECT s.id, s.topic_id, s.target_user_id, s.permission, s.created_at, u.display_name
            FROM topic_shares s JOIN users u ON u.id = s.target_user_id
            WHERE s.topic_id = @t AND s.revoked_at IS NULL ORDER BY u.display_name
            """, new[] { ("t", (object?)topic.Id) }))
            .Select(r => new TopicShareInfo
            {
                Id = r["id"] ?? "", TopicId = topic.Id, UserId = r["target_user_id"] ?? "", UserName = r.GetValueOrDefault("display_name") ?? "",
                Permission = r.GetValueOrDefault("permission") ?? "learn", CreatedAt = DbValue.ToDateTime(r.GetValueOrDefault("created_at")),
            }).ToList();
    }

    public Task<List<UserSearchResult>> SearchUsersAsync(string userId, string? query) => _users.SearchUsersAsync(userId, query);

    // Zuweisungen: ausschließlich administrativ (Admin/Themen-Admin) in TopicService.Assignments.cs (ADR 0029); der Eigentümer weist nicht mehr zu.

    // ─── Überblick ──────────────────────────────────────────────────────────

    public sealed class TopicOverview
    {
        public List<TopicSummary> Own { get; set; } = new();
        public List<TopicSummary> Shared { get; set; } = new();
        public List<TopicSummary> Assigned { get; set; } = new();
        public List<TopicSummary> Global { get; set; } = new();
        /// <summary>Offene Pflichtzuweisungen des Benutzers (nicht abgeschlossen), überfällige zuerst.</summary>
        public List<TopicSummary> Mandatory { get; set; } = new();
        /// <summary>Abgeschlossene Pflichtzuweisungen (Lernverlauf; keine offene Pflicht mehr).</summary>
        public List<TopicSummary> MandatoryDone { get; set; } = new();
    }

    /// <summary>Vier Bereiche: meine Themen, mit mir geteilte, mir zugewiesene und globale Themen. Archivierte Themen erscheinen nur mit <paramref name="includeArchived"/>.</summary>
    public async Task<TopicOverview> ListOverviewAsync(string userId, bool includeArchived = false)
    {
        var overview = new TopicOverview();
        var archivedFilter = includeArchived ? "" : " AND t.status = 'active'";

        foreach (var r in await _db.QueryAsync(TopicSelect + " WHERE t.owner_user_id = @u" + archivedFilter + " ORDER BY t.name", new[] { ("u", (object?)userId) }))
        {
            var topic = MapTopic(r);
            var summary = new TopicSummary { Topic = topic, Section = TopicSections.Own, Version = topic.LatestVersion, GlobalStatus = await LatestGlobalStatusAsync(topic.Id) };
            summary.Stats = await DraftStatsAsync(topic.Id, userId);
            summary.SharedWithCount = DbValue.ToInt((await _db.QueryAsync("SELECT COUNT(*) AS n FROM topic_shares WHERE topic_id = @t AND revoked_at IS NULL", new[] { ("t", (object?)topic.Id) }))[0]["n"]);
            summary.AssignedCount = DbValue.ToInt((await _db.QueryAsync("SELECT COUNT(*) AS n FROM topic_assignments WHERE topic_id = @t AND status = 'active'", new[] { ("t", (object?)topic.Id) }))[0]["n"]);
            overview.Own.Add(summary);
        }

        var assignedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in await _db.QueryAsync(TopicSelect + " JOIN topic_assignments a ON a.topic_id = t.id AND a.user_id = @u AND a.status = 'active' WHERE t.owner_user_id <> @u" + archivedFilter + " ORDER BY t.name",
                     new[] { ("u", (object?)userId) }))
        {
            var s = await LearnerSummaryAsync(userId, MapTopic(r), TopicSections.Assigned);
            if (s is null) continue;
            assignedIds.Add(s.Topic.Id);
            overview.Assigned.Add(s);
        }
        var ownAssigned = new List<TopicSummary>();
        // Pflicht auch für Themen, die der Benutzer selbst besitzt oder die global sind: eine ausdrückliche Zuweisung bleibt eine Zuweisung
        foreach (var r in await _db.QueryAsync(TopicSelect + " JOIN topic_assignments a ON a.topic_id = t.id AND a.user_id = @u AND a.status = 'active' WHERE t.owner_user_id = @u" + archivedFilter, new[] { ("u", (object?)userId) }))
        {
            var s = await LearnerSummaryAsync(userId, MapTopic(r), TopicSections.Assigned);
            if (s is not null) { assignedIds.Add(s.Topic.Id); ownAssigned.Add(s); }   // erscheint in „Meine Themen“, zusätzlich nur als Pflicht
        }
        foreach (var r in await _db.QueryAsync(TopicSelect + " JOIN topic_shares sh ON sh.topic_id = t.id AND sh.target_user_id = @u AND sh.revoked_at IS NULL WHERE t.owner_user_id <> @u" + archivedFilter + " ORDER BY t.name",
                     new[] { ("u", (object?)userId) }))
        {
            var topic = MapTopic(r);
            if (assignedIds.Contains(topic.Id)) continue;   // zugewiesen hat Vorrang vor geteilt
            var s = await LearnerSummaryAsync(userId, topic, TopicSections.Shared);
            if (s is not null) overview.Shared.Add(s);
        }
        foreach (var r in await _db.QueryAsync(TopicSelect + " WHERE EXISTS (SELECT 1 FROM topic_versions gv WHERE gv.topic_id = t.id AND gv.global_status = 'active' AND gv.status = 'published') ORDER BY t.name"))
        {
            var s = await LearnerSummaryAsync(userId, MapTopic(r), TopicSections.Global, globalOnly: true);
            if (s is not null) overview.Global.Add(s);
        }
        foreach (var s in overview.Assigned.Concat(ownAssigned).Where(x => x.Mandatory))
            (s.Progress?.Status == TopicLearnStatuses.Completed ? overview.MandatoryDone : overview.Mandatory).Add(s);
        overview.Mandatory = overview.Mandatory.OrderByDescending(x => x.Overdue).ThenBy(x => x.DueAt ?? DateTime.MaxValue).ThenBy(x => x.Topic.Name).ToList();
        return overview;
    }

    private async Task<TopicSummary?> LearnerSummaryAsync(string userId, TopicInfo topic, string section, bool globalOnly = false)
    {
        var version = await ResolveVersionForLearnerAsync(userId, topic, globalOnly ? TopicSections.Global : section, requestedVersion: null);
        if (version is null) return null;
        var summary = new TopicSummary { Topic = topic, Section = section, Version = version.VersionNumber, GlobalStatus = version.GlobalStatus, Stats = version.Stats };
        summary.Progress = await ComputeProgressForUserAsync(userId, topic, requireAccess: false, version);
        summary.NewerVersionAvailable = summary.Progress?.NewerVersionAvailable ?? false;
        // Beziehung: ausdrückliche Zuweisung (verpflichtend oder freiwillig) > persönliche Freigabe > globale Sichtbarkeit; global allein ist nie Pflicht
        var asg = (await _db.QueryAsync("SELECT mandatory, due_at, assigned_at, topic_version FROM topic_assignments WHERE topic_id = @t AND user_id = @u AND status = 'active'", new (string, object?)[] { ("t", topic.Id), ("u", userId) })).FirstOrDefault();
        if (asg is not null)
        {
            summary.Mandatory = DbValue.ToBool(asg.GetValueOrDefault("mandatory"));
            summary.Relation = summary.Mandatory ? TopicRelations.AssignedMandatory : TopicRelations.AssignedVoluntary;
            summary.AssignedAt = DbValue.ToDateTime(asg.GetValueOrDefault("assigned_at"));
            summary.DueAt = asg.GetValueOrDefault("due_at") is { Length: > 0 } d ? DbValue.ToDateTime(d) : null;
            summary.AssignedVersion = asg.GetValueOrDefault("topic_version") is { Length: > 0 } av ? DbValue.ToInt(av) : null;
            summary.Overdue = summary.Mandatory && summary.DueAt is DateTime due && due < DateTime.UtcNow && summary.Progress?.Status != TopicLearnStatuses.Completed;
        }
        else summary.Relation = section == TopicSections.Shared ? TopicRelations.Shared : TopicRelations.GlobalVoluntary;
        return summary;
    }

    private async Task<string> LatestGlobalStatusAsync(string topicId)
    {
        var rows = await _db.QueryAsync("SELECT global_status FROM topic_versions WHERE topic_id = @t AND status = 'published' AND global_status <> 'none' ORDER BY version_number DESC LIMIT 1", new[] { ("t", (object?)topicId) });
        return rows.FirstOrDefault()?["global_status"] ?? TopicGlobalStatuses.None;
    }
}
