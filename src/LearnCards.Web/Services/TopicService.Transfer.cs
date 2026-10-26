using System.Text.Json;
using System.Text.Json.Nodes;
using LearnCards.Web.Api;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>
/// Portable Themen-JSON (ADR 0028): Export, Erstellen und Aktualisieren des <b>Entwurfs</b> aus einer Definition sowie Umstellung bestehender Entwürfe von globalen
/// Modulen auf eigene Kopien. Alles läuft über die vorhandene Entwurfslogik; Veröffentlichung, Versionen, Freigaben, Zuweisungen und Ergebnisse bleiben unberührt.
/// </summary>
public partial class TopicService
{
    public const int MaxTopicModules = 100;
    public const int MaxTopicQuestions = 500;

    private static readonly string[] DefinitionKeys = { "format", "schema_version", "name", "description", "modules", "exam_config", "topic_questions" };
    private static readonly string[] DefinitionModuleKeys = { "module_ref", "module_name", "sort_order", "exam", "final_exam_questions", "module" };

    // ─── Parsen und Validieren (ohne Datenbankzugriff) ──────────────────────

    public static TopicDefinition ParseDefinition(string json)
    {
        var def = TransferJson.Parse<TopicDefinition>(json, TransferFormats.Topic, DefinitionKeys);
        using (var doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.TryGetProperty("modules", out var mods) && mods.ValueKind == JsonValueKind.Array)
                foreach (var m in mods.EnumerateArray())
                    if (m.ValueKind == JsonValueKind.Object) TransferJson.RejectUnknown(m, DefinitionModuleKeys, "modules[].");
            if (doc.RootElement.TryGetProperty("exam_config", out var ec) && ec.ValueKind == JsonValueKind.Object) TransferJson.RejectUnknown(ec, new[] { "final_exam" }, "exam_config.");
        }
        ValidateDefinition(def);
        return def;
    }

    /// <summary>Rein fachliche Prüfung (Grenzen, Eindeutigkeit, Prüfungskonfiguration, Fragen, eingebettete Module). Wirft <see cref="ArgumentException"/>.</summary>
    public static void ValidateDefinition(TopicDefinition def)
    {
        var errors = new List<string>();
        void Err(string m) { if (errors.Count < 10) errors.Add(m); }
        if (def.Format != TransferFormats.Topic) Err($"'format' muss \"{TransferFormats.Topic}\" sein.");
        if (def.SchemaVersion != TransferFormats.SchemaVersion) Err($"'schema_version' {def.SchemaVersion} wird nicht unterstützt (unterstützt: {TransferFormats.SchemaVersion}).");
        try { def.Name = CleanName(def.Name); } catch (ArgumentException e) { Err(e.Message); }
        try { def.Description = CleanDescription(def.Description); } catch (ArgumentException e) { Err(e.Message); }
        def.Modules ??= new();
        def.TopicQuestions ??= new();
        def.ExamConfig ??= new();
        def.ExamConfig.FinalExam ??= new();
        if (def.Modules.Count > MaxTopicModules) Err($"Zu viele Module (höchstens {MaxTopicModules}).");
        if (def.TopicQuestions.Count > MaxTopicQuestions) Err($"Zu viele Themenfragen (höchstens {MaxTopicQuestions}).");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (m, i) in def.Modules.Select((m, i) => (m, i)))
        {
            var label = $"Modul {i + 1}";
            var name = (m.ModuleName ?? "").Trim();
            if (name.Length == 0) { Err($"{label}: 'module_name' ist erforderlich."); continue; }
            m.ModuleName = name;
            if (m.ModuleRef is not null && !Guid.TryParse(m.ModuleRef, out _)) Err($"{label}: 'module_ref' muss eine GUID sein.");
            else if (m.ModuleRef is not null) m.ModuleRef = Guid.Parse(m.ModuleRef).ToString();
            if (!seen.Add(m.ModuleRef ?? "name:" + name.ToLowerInvariant())) Err($"{label}: Das Modul „{name}“ ist mehrfach aufgeführt.");
            if (m.Exam is not null) foreach (var e in m.Exam.Validate("Modulprüfung " + $"„{name}“")) Err(e);
            if (m.FinalExamQuestions is < 0 or > 50) Err($"{label}: 'final_exam_questions' muss zwischen 0 und 50 liegen.");
            if (m.Module is not null)
            {
                if (m.ModuleRef is not null && m.Module.ReferenceId is not null && !string.Equals(m.Module.ReferenceId, m.ModuleRef, StringComparison.OrdinalIgnoreCase)) Err($"{label}: 'module.reference_id' muss 'module_ref' entsprechen.");
                if (m.ModuleRef is not null) m.Module.ReferenceId ??= m.ModuleRef;
                // der Anzeigename darf vom eingebetteten Namen abweichen, wenn eine Referenz die Zuordnung trägt
                else if (!string.Equals(m.Module.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase) && m.Module.ReferenceId is null) Err($"{label}: 'module.name' muss dem 'module_name' „{name}“ entsprechen.");
                try { ModuleTransferService.Validate(m.Module, $"Modul „{name}“: "); } catch (ArgumentException e) { Err(e.Message); }
            }
        }
        foreach (var e in def.ExamConfig.FinalExam.Validate()) Err(e);
        foreach (var (q, i) in def.TopicQuestions.Select((q, i) => (q, i)))
        {
            var error = QuizPoolService.Validate(new QuizQuestionInput { Type = q.Type, Question = q.Question ?? "", Options = q.Options ?? new(), CorrectIndexes = q.CorrectIndexes ?? new(), Explanation = q.Explanation ?? "" });
            if (error is not null) Err($"Themenfrage {i + 1}: {error}");
        }
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
    }

    // ─── Export ─────────────────────────────────────────────────────────────

    /// <summary>Exportiert den Entwurf eines eigenen Themas als portable Definition; optional mit den vollständigen Moduldefinitionen (dann ohne weitere Voraussetzungen importierbar).</summary>
    public async Task<TopicDefinition> ExportAsync(string ownerId, string topicId, bool includeModules)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId, allowArchived: true);
        var cfg = await LoadExamConfigAsync(topic.Id);
        var def = new TopicDefinition { Name = topic.Name, Description = topic.Description };
        var rows = await ListDraftModuleRowsAsync(topic.Id);
        var final = cfg.FinalExam;
        var counts = final.ManualCounts;
        final.ManualCounts = new();
        for (var i = 0; i < rows.Count; i++)
        {
            var access = await _repo.ResolveModuleAccessAsync(ownerId, rows[i].ModuleId);
            if (access is null) throw new InvalidOperationException("Ein zugeordnetes Modul ist nicht mehr verfügbar und muss vor dem Export aus dem Thema entfernt werden.");
            var entry = new TopicDefinitionModule
            {
                ModuleRef = access.Context == CardRepository.ContextPrivate && access.Module.OwnerUserId is { } oid ? await _repo.EnsureReferenceIdAsync(access.Module.Id, oid) : null,
                ModuleName = access.Module.Name, SortOrder = i + 1,
                Exam = cfg.ModuleExams.TryGetValue(rows[i].ModuleId, out var e) ? e : new TopicModuleExamConfig(),
                FinalExamQuestions = counts.TryGetValue(rows[i].ModuleId, out var c) ? c : null,
            };
            if (includeModules) entry.Module = await _modules.ExportAsync(ownerId, rows[i].ModuleId);
            def.Modules.Add(entry);
        }
        def.ExamConfig.FinalExam = final;
        def.TopicQuestions = (await ListQuestionsRawAsync(topic.Id, activeOnly: false)).Select(q => new TopicDefinitionQuestion
        {
            Type = q.Type, Question = q.Question, Options = q.Options.ToList(), CorrectIndexes = q.CorrectIndexes.ToList(), Explanation = q.Explanation, Active = q.Active,
        }).ToList();
        await _users.WriteAuditAsync(ownerId, "topic.export", topic.Id, includeModules ? "with-modules" : "");
        return def;
    }

    // ─── Auflösung der Modulreferenzen ──────────────────────────────────────

    private sealed record ResolvedModule(TopicDefinitionModule Entry, ModuleInfo Module, bool Created);

    /// <summary>
    /// Löst die Modulreferenzen ausschließlich gegen <b>eigene private Module</b> auf: zuerst über die stabile <c>module_ref</c> (überlebt Umbenennen), ohne Referenz über den Namen.
    /// Fehlende oder globale/fremde Module liefern <b>vor</b> jeder Änderung eine Fehlermeldung. Eingebettete Module: Besitzt der Benutzer bereits ein Modul mit derselben Referenz,
    /// wird es wiederverwendet; sonst entsteht ein neues eigenes Modul (bei späterem Fehler vom Aufrufer wieder entfernt).
    /// </summary>
    private async Task<List<ResolvedModule>> ResolveModulesAsync(string ownerId, TopicDefinition def, List<string> createdModuleIds)
    {
        var own = (await _repo.ListModulesAsync(ownerId)).GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        var plan = new List<(TopicDefinitionModule Entry, ModuleInfo? Found)>();
        foreach (var m in def.Modules.Select((m, i) => (m, i)).OrderBy(x => x.m.SortOrder ?? int.MaxValue).ThenBy(x => x.i).Select(x => x.m))
        {
            ModuleInfo? found = null;
            if (m.ModuleRef is not null) found = await _repo.FindOwnModuleByReferenceAsync(ownerId, m.ModuleRef);
            else if (m.Module is null)
            {
                if (own.TryGetValue(m.ModuleName, out var byName)) { if (byName.Count == 1) found = byName[0]; else problems.Add($"Der Modulname „{m.ModuleName}“ ist nicht eindeutig — verwende 'module_ref'."); }
            }
            if (found is null && m.Module is null && !problems.Any(p => p.Contains($"„{m.ModuleName}“")))
                problems.Add(m.ModuleRef is not null ? $"„{m.ModuleName}“ (module_ref {m.ModuleRef})" : $"„{m.ModuleName}“");
            plan.Add((m, found));
        }
        var missing = problems.Where(p => !p.StartsWith("Der Modulname")).ToList();
        var other = problems.Where(p => p.StartsWith("Der Modulname")).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"Diese Module wurden unter deinen eigenen Modulen nicht gefunden: {string.Join(", ", missing)}. Einem Thema können nur eigene, private Module zugeordnet werden — globale Module zuerst in eigene Module klonen, fehlende Module importieren oder die Moduldefinition einbetten ('module').");
        if (other.Count > 0) throw new ArgumentException(string.Join(" ", other));

        var result = new List<ResolvedModule>();
        foreach (var (entry, found) in plan)
        {
            if (found is null)
            {
                // eingebettet und noch nicht vorhanden bzw. Wiederverwendung über die Referenz im Modul
                var (module, created) = await _modules.ImportOrReuseAsync(ownerId, entry.Module!);
                if (created) createdModuleIds.Add(module.Id);
                result.Add(new ResolvedModule(entry, module, created));
            }
            else
            {
                if (await CanUseModuleInTopicAsync(ownerId, found.Id) is { } error) throw new ArgumentException(error);
                result.Add(new ResolvedModule(entry, found, false));
            }
        }
        if (result.Select(r => r.Module.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new ArgumentException("Mehrere Einträge verweisen auf dasselbe Modul.");
        return result;
    }

    // ─── Erstellen und Aktualisieren ────────────────────────────────────────

    /// <summary>Erstellt aus einer Definition ein neues privates Thema (Entwurf; keine Veröffentlichung). Bei jedem Fehler bleibt nichts zurück.</summary>
    public async Task<TopicInfo> CreateFromDefinitionAsync(string ownerId, string json)
    {
        var def = ParseDefinition(json);
        var createdModules = new List<string>();
        var newId = Guid.NewGuid().ToString();   // vorab bekannt: das Aufräumen trifft nie ein fremdes Thema
        TopicInfo? topic = null;
        try
        {
            if ((await _db.QueryAsync("SELECT 1 FROM topics WHERE owner_user_id = @o AND name = @n", new (string, object?)[] { ("o", ownerId), ("n", def.Name) })).Count > 0)
                throw new ArgumentException($"Es existiert bereits ein Thema „{def.Name}“. Wähle einen anderen Namen oder aktualisiere das Thema über die Definition.");
            var resolved = await ResolveModulesAsync(ownerId, def, createdModules);
            topic = await CreateAsync(ownerId, def.Name, def.Description, newId);
            await ApplyDefinitionAsync(topic, def, resolved);
            await _users.WriteAuditAsync(ownerId, "topic.import", topic.Id, $"modules={resolved.Count}");
            return (await GetTopicRawAsync(topic.Id))!;
        }
        catch
        {
            await DeleteDraftTopicAsync(newId);   // auch wenn CreateAsync nach dem Einfügen scheiterte; ohne Zeile ein No-op, nie ein fremdes Thema
            await DeleteCreatedModulesAsync(ownerId, createdModules);
            throw;
        }
    }

    /// <summary>
    /// Ersetzt den <b>Entwurf</b> eines eigenen Themas durch die Definition (Name, Beschreibung, Module in Reihenfolge, Prüfungen, Themenfragen).
    /// Veröffentlichte Versionen bleiben unverändert; eine neue Version entsteht nur durch die ausdrückliche Veröffentlichung. Bei Fehlern wird der vorherige Entwurf wiederhergestellt.
    /// </summary>
    public async Task<TopicInfo> UpdateFromDefinitionAsync(string ownerId, string topicId, string json)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId);
        var def = ParseDefinition(json);
        if (!string.Equals(def.Name, topic.Name, StringComparison.Ordinal)
            && (await _db.QueryAsync("SELECT 1 FROM topics WHERE owner_user_id = @o AND name = @n AND id <> @id", new (string, object?)[] { ("o", ownerId), ("n", def.Name), ("id", topic.Id) })).Count > 0)
            throw new ArgumentException("Es existiert bereits ein Thema mit diesem Namen.");
        var createdModules = new List<string>();
        var resolved = await ResolveModulesAsync(ownerId, def, createdModules);
        var backup = new DraftBackup(topic.Name, topic.Description, await ListDraftModuleRowsAsync(topic.Id), await LoadExamConfigAsync(topic.Id), await ListQuestionsRawAsync(topic.Id, activeOnly: false));
        try
        {
            await ApplyDefinitionAsync(topic, def, resolved);
            await _users.WriteAuditAsync(ownerId, "topic.definition.update", topic.Id, $"modules={resolved.Count}");
            return (await GetTopicRawAsync(topic.Id))!;
        }
        catch
        {
            try { await RestoreDraftAsync(topic.Id, backup); } catch (Exception) { /* der ursprüngliche Fehler ist maßgeblich */ }
            await DeleteCreatedModulesAsync(ownerId, createdModules);
            throw;
        }
    }

    private sealed record DraftBackup(string Name, string Description, List<DraftModuleRow> Modules, TopicExamConfig Config, List<TopicQuestion> Questions);

    private async Task ApplyDefinitionAsync(TopicInfo topic, TopicDefinition def, List<ResolvedModule> resolved)
    {
        var cfg = new TopicExamConfig { FinalExam = def.ExamConfig.FinalExam };
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < resolved.Count; i++)
        {
            cfg.ModuleExams[resolved[i].Module.Id] = resolved[i].Entry.Exam ?? new TopicModuleExamConfig();
            names[resolved[i].Module.Id] = resolved[i].Module.Name;
        }
        cfg.FinalExam.Unlock ??= new TopicUnlockRules();
        cfg.FinalExam.ManualCounts = cfg.FinalExam.Distribution == TopicFinalExamConfig.DistributionManual
            ? resolved.Where(r => r.Entry.FinalExamQuestions is not null).ToDictionary(r => r.Module.Id, r => r.Entry.FinalExamQuestions!.Value)
            : new();
        var errors = cfg.Validate(names);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));

        await _db.ExecuteAsync("UPDATE topics SET name = @n, description = @d, updated_at = @now WHERE id = @id", new (string, object?)[] { ("n", def.Name), ("d", def.Description), ("now", DateTime.UtcNow), ("id", topic.Id) });
        // bereits festgelegte Modulversionen bleiben erhalten (keine stillschweigende Übernahme neuerer Stände); neue Module werden auf den aktuellen Stand festgelegt (ADR 0030)
        var oldPins = (await ListDraftModuleRowsAsync(topic.Id)).Where(r => r.Version is not null).ToDictionary(r => r.ModuleId, r => r.Version, StringComparer.OrdinalIgnoreCase);
        await _db.ExecuteAsync("DELETE FROM topic_modules WHERE topic_id = @t", new[] { ("t", (object?)topic.Id) });
        for (var i = 0; i < resolved.Count; i++)
        {
            await _db.ExecuteAsync("INSERT INTO topic_modules (topic_id, module_id, sort_order, added_at, module_version) VALUES (@t, @m, @s, @n, @v)",
                new (string, object?)[] { ("t", topic.Id), ("m", resolved[i].Module.Id), ("s", i), ("n", DateTime.UtcNow), ("v", oldPins.GetValueOrDefault(resolved[i].Module.Id)) });
        }
        await SaveExamConfigRawAsync(topic.Id, cfg);
        await _db.ExecuteAsync("DELETE FROM topic_questions WHERE topic_id = @t", new[] { ("t", (object?)topic.Id) });
        foreach (var q in def.TopicQuestions)
            await CreateQuestionRawAsync(topic.Id, new QuizQuestionInput { Type = q.Type, Question = q.Question, Options = q.Options.ToList(), CorrectIndexes = q.CorrectIndexes.ToList(), Explanation = q.Explanation ?? "" }, q.Active);
        // Modulversionen erst zuletzt festlegen: scheitert ein früherer Schritt, entstehen keine Modulversionen (alles oder nichts, ADR 0028/0030)
        foreach (var r in resolved.Where(r => !oldPins.ContainsKey(r.Module.Id))) await PinModuleAsync(topic.OwnerUserId, topic.Id, r.Module.Id, null);
        await TouchDraftAsync(topic.Id);
    }

    private async Task RestoreDraftAsync(string topicId, DraftBackup b)
    {
        await _db.ExecuteAsync("UPDATE topics SET name = @n, description = @d WHERE id = @id", new (string, object?)[] { ("n", b.Name), ("d", b.Description), ("id", topicId) });
        await _db.ExecuteAsync("DELETE FROM topic_modules WHERE topic_id = @t", new[] { ("t", (object?)topicId) });
        foreach (var m in b.Modules)
            await _db.ExecuteAsync("INSERT INTO topic_modules (topic_id, module_id, sort_order, added_at, module_version) VALUES (@t, @m, @s, @n, @v)", new (string, object?)[] { ("t", topicId), ("m", m.ModuleId), ("s", m.SortOrder), ("n", DateTime.UtcNow), ("v", m.Version) });
        await SaveExamConfigRawAsync(topicId, b.Config);
        await _db.ExecuteAsync("DELETE FROM topic_questions WHERE topic_id = @t", new[] { ("t", (object?)topicId) });
        foreach (var q in b.Questions)
            await CreateQuestionRawAsync(topicId, new QuizQuestionInput { Type = q.Type, Question = q.Question, Options = q.Options.ToList(), CorrectIndexes = q.CorrectIndexes.ToList(), Explanation = q.Explanation }, q.Active);
    }

    /// <summary>Entfernt ein soeben angelegtes, nie veröffentlichtes Thema (nur Rollback eines fehlgeschlagenen Imports).</summary>
    private async Task DeleteDraftTopicAsync(string topicId)
    {
        try
        {
            foreach (var table in new[] { "topic_questions", "topic_modules", "topic_exam_configs" })
                await _db.ExecuteAsync($"DELETE FROM {table} WHERE topic_id = @t", new[] { ("t", (object?)topicId) });
            await _db.ExecuteAsync("DELETE FROM topics WHERE id = @t AND published_revision = 0 AND NOT EXISTS (SELECT 1 FROM topic_versions v WHERE v.topic_id = @t)", new[] { ("t", (object?)topicId) });
        }
        catch (Exception) { /* bestmögliches Aufräumen */ }
    }

    private async Task DeleteCreatedModulesAsync(string ownerId, IEnumerable<string> moduleIds)
    {
        foreach (var id in moduleIds)
            try { await _repo.DeleteModuleAsync(id, ownerId); } catch (Exception) { /* bestmögliches Aufräumen */ }
    }

    // ─── Bestehende Entwürfe mit globalen Modulen ───────────────────────────

    /// <summary>
    /// Stellt eine Zuordnung im Entwurf von einem globalen Modul auf eine neue eigene Kopie um (Klon, gleiche Position, Prüfungskonfiguration wandert mit).
    /// Das globale Original und veröffentlichte Versionen bleiben unverändert.
    /// </summary>
    public async Task<ModuleInfo> ReplaceWithOwnCloneAsync(string ownerId, string topicId, string moduleId)
    {
        var topic = await RequireOwnTopicAsync(ownerId, topicId);
        var rows = await ListDraftModuleRowsAsync(topic.Id);
        var row = rows.FirstOrDefault(r => string.Equals(r.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException("Das Modul ist dem Thema nicht zugeordnet.");
        var access = await _repo.ResolveModuleAccessAsync(ownerId, row.ModuleId);
        if (access is null || access.Context != CardRepository.ContextGlobal) throw new ArgumentException("Nur globale Module müssen durch eine eigene Kopie ersetzt werden.");
        var clone = await _modules.CloneGlobalAsync(ownerId, row.ModuleId);
        try
        {
            await _db.ExecuteAsync("UPDATE topic_modules SET module_id = @new, module_version = NULL WHERE topic_id = @t AND module_id = @old", new (string, object?)[] { ("new", clone.Id), ("t", topic.Id), ("old", row.ModuleId) });
            await PinModuleAsync(ownerId, topic.Id, clone.Id, null);
            var cfg = await LoadExamConfigAsync(topic.Id);
            if (cfg.ModuleExams.Remove(row.ModuleId, out var exam)) cfg.ModuleExams[clone.Id] = exam;
            if (cfg.FinalExam.ManualCounts.Remove(row.ModuleId, out var count)) cfg.FinalExam.ManualCounts[clone.Id] = count;
            await SaveExamConfigRawAsync(topic.Id, cfg);
            await TouchDraftAsync(topic.Id);
        }
        catch
        {
            await _db.ExecuteAsync("UPDATE topic_modules SET module_id = @old WHERE topic_id = @t AND module_id = @new", new (string, object?)[] { ("new", clone.Id), ("t", topic.Id), ("old", row.ModuleId) });
            await DeleteCreatedModulesAsync(ownerId, new[] { clone.Id });
            throw;
        }
        await _users.WriteAuditAsync(ownerId, "topic.module.replace-clone", topic.Id, $"old={row.ModuleId} new={clone.Id}");
        return clone;
    }
}
