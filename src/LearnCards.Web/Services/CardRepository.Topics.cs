using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnCards.Web.Api;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>
/// Eingefrorene Modulkopien für Themenversionen und Zugriff darauf (ADR 0027). Die Kopien haben <c>scope = 'topic'</c> und keinen Eigentümer;
/// sie erscheinen in keiner Modulliste und sind ausschließlich über den Zugriffsentscheid <see cref="ResolveModuleAccessAsync"/> lesbar.
/// </summary>
public partial class CardRepository
{
    public const string ContextTopic = "topic";
    public const string ScopeTopic = "topic";

    /// <summary>
    /// Darf der Benutzer das eingefrorene Themenmodul lernen? Ja bei veröffentlichten Versionen, wenn er Eigentümer des Themas ist, eine aktive Freigabe
    /// oder Zuweisung hat — oder die Version global aktiv ist. Entwürfe, fehlgeschlagene Versionen und fremde private Themen verhalten sich wie „nicht vorhanden“.
    /// </summary>
    private async Task<bool> CanLearnTopicModuleAsync(string userId, string moduleId)
    {
        var rows = await _db.QueryAsync("""
            SELECT 1
            FROM topic_version_modules vm
            JOIN topic_versions v ON v.id = vm.version_id
            JOIN topics t ON t.id = v.topic_id
            WHERE vm.module_id = @m AND v.status = 'published'
              AND ( t.owner_user_id = @u
                    OR EXISTS (SELECT 1 FROM topic_shares s WHERE s.topic_id = t.id AND s.target_user_id = @u AND s.revoked_at IS NULL)
                    OR EXISTS (SELECT 1 FROM topic_assignments a WHERE a.topic_id = t.id AND a.user_id = @u AND a.status = 'active' AND (a.topic_version IS NULL OR a.topic_version = v.version_number))
                    OR v.global_status = 'active' )
            LIMIT 1
            """, new (string, object?)[] { ("m", moduleId), ("u", userId) });
        return rows.Count > 0;
    }

    /// <summary>Ist die eingefrorene Kopie eine Version eines Moduls dieses Benutzers (ADR 0030)? Dann darf er sie lesen (nie ändern).</summary>
    private async Task<bool> OwnsModuleVersionSnapshotAsync(string userId, string snapshotModuleId) =>
        (await _db.QueryAsync("""
            SELECT 1 FROM module_versions mv JOIN modules m ON m.id = mv.module_id
            WHERE mv.snapshot_module_id = @s AND m.owner_user_id = @u LIMIT 1
            """, new (string, object?)[] { ("s", snapshotModuleId), ("u", userId) })).Count > 0;

    /// <summary>Ist die eingefrorene Kopie das (kartenlose) Prüfungsmodul der Abschlussprüfung einer Themenversion?</summary>
    public async Task<bool> IsTopicFinalExamModuleAsync(string moduleId) =>
        (await _db.QueryAsync("SELECT 1 FROM topic_version_modules WHERE module_id = @m AND role = 'final_exam' LIMIT 1", new[] { ("m", (object?)moduleId) })).Count > 0;

    /// <summary>Kopiert Lernziele und Kompetenzen eines Moduls in ein anderes (eingefrorene Kopien); ohne Quellmodell passiert nichts.</summary>
    private async Task CopyCompetencyModelAsync(string sourceModuleId, string targetModuleId)
    {
        var rows = await _db.QueryAsync("SELECT definition_json FROM module_competencies WHERE module_id = @m", new[] { ("m", (object?)sourceModuleId) });
        if (rows.Count == 0) return;
        await _db.ExecuteAsync("INSERT INTO module_competencies (module_id, definition_json, updated_at) VALUES (@m, @j, @t)", new (string, object?)[] { ("m", targetModuleId), ("j", rows[0]["definition_json"] ?? "{}"), ("t", DateTime.UtcNow) });
    }

    /// <summary>Alle aktiven Karten eines Moduls (inkl. Bildmetadaten) — nur für interne Snapshot-Erzeugung und Prüfungsaufbau, nach vorheriger Berechtigungsprüfung.</summary>
    public async Task<List<Card>> ListActiveCardsRawAsync(string moduleId)
    {
        var cards = await ListAllCardsOfModuleRawAsync(moduleId);
        return cards.Where(c => !c.Archived).ToList();
    }

    /// <summary>
    /// Inhaltskennung eines Moduls (SHA-256 über Metadaten, aktive Karten, Bildprüfsummen, aktive Pool-Fragen und Quizkonfiguration).
    /// Unveränderte Module werden in neuen Themenversionen wiederverwendet (gleiche eingefrorene Kopie), geänderte erhalten eine neue Kopie.
    /// </summary>
    public async Task<string> ComputeModuleContentHashAsync(ModuleInfo module)
    {
        var cards = (await ListActiveCardsRawAsync(module.Id)).OrderBy(c => c.SortOrder).ThenBy(c => c.Term, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        void Add(params object?[] parts) { foreach (var p in parts) sb.Append(p?.ToString()).Append('\u001f'); sb.Append('\u001e'); }
        Add("m", module.Name, module.Description, module.Icon, module.Color);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            index[c.Id] = i;
            Add("c", i, c.Category, c.Term, c.Question, c.Definition, c.HowItWorks, c.Context, c.KeyFact, c.ReferenceAnswer, c.ChatPrompt, c.SlideNumber, c.TargetTimeSec,
                JsonSerializer.Serialize(c.OfficialSources, AppJson.Options), JsonSerializer.Serialize(c.Quiz, AppJson.Options), string.Join(",", c.Competencies.OrderBy(x => x, StringComparer.Ordinal)),
                string.Join(",", c.Images.OrderBy(im => im.SortOrder).Select(im => im.Checksum + ":" + im.SortOrder)));
        }
        foreach (var q in (await _quizPool.ListAsync(module.Id, null, null, true)).OrderBy(q => q.Question, StringComparer.Ordinal).ThenBy(q => q.Type, StringComparer.Ordinal))
            Add("q", q.CardId is not null && index.TryGetValue(q.CardId, out var ci) ? ci : -1, q.Type, q.Question, string.Join("|", q.Options), string.Join(",", q.CorrectIndexes), q.Explanation, q.Origin);
        Add("cfg", JsonSerializer.Serialize(await _quizPool.GetConfigAsync(module.Id), AppJson.Options));
        var comp = await _db.QueryAsync("SELECT definition_json FROM module_competencies WHERE module_id = @m", new[] { ("m", (object?)module.Id) });
        if (comp.Count > 0) Add("comp", comp[0]["definition_json"]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    // ─── Modulreferenz (ADR 0028) ───────────────────────────────────────────

    /// <summary>Eigenes privates Modul mit dieser Referenz (GUID) oder null.</summary>
    public async Task<ModuleInfo?> FindOwnModuleByReferenceAsync(string ownerId, string referenceId)
    {
        if (!Guid.TryParse(referenceId, out var g)) return null;
        var rows = await _db.QueryAsync($"{ModuleSelect} WHERE m.owner_user_id = @o AND m.scope = 'private' AND m.reference_id = @r {ModuleGroupBy}",
            new (string, object?)[] { ("o", ownerId), ("r", g.ToString()) });
        return rows.Count == 0 ? null : MapModule(rows[0]);
    }

    /// <summary>Stellt sicher, dass ein eigenes Modul eine Referenz hat (vergibt bei Bedarf eine neue GUID) und liefert sie.</summary>
    public async Task<string> EnsureReferenceIdAsync(string moduleId, string ownerId)
    {
        var rows = await _db.QueryAsync("SELECT reference_id FROM modules WHERE id = @id AND owner_user_id = @o AND scope = 'private'", new (string, object?)[] { ("id", moduleId), ("o", ownerId) });
        if (rows.Count == 0) throw new KeyNotFoundException("Modul nicht gefunden.");
        if (rows[0]["reference_id"] is { Length: > 0 } existing) return existing;
        var fresh = Guid.NewGuid().ToString();
        await _db.ExecuteAsync("UPDATE modules SET reference_id = @r WHERE id = @id AND owner_user_id = @o AND reference_id IS NULL", new (string, object?)[] { ("r", fresh), ("id", moduleId), ("o", ownerId) });
        return (await _db.QueryAsync("SELECT reference_id FROM modules WHERE id = @id", new[] { ("id", (object?)moduleId) }))[0]["reference_id"] ?? fresh;
    }

    /// <summary>Setzt die Referenz eines neuen eigenen Moduls, sofern der Eigentümer sie noch nicht verwendet; sonst bleibt sie leer (wird später neu vergeben).</summary>
    public async Task<bool> TryAssignReferenceIdAsync(string moduleId, string ownerId, string referenceId)
    {
        if (!Guid.TryParse(referenceId, out var g) || await FindOwnModuleByReferenceAsync(ownerId, g.ToString()) is not null) return false;
        try { return await _db.ExecuteAsync("UPDATE modules SET reference_id = @r WHERE id = @id AND owner_user_id = @o AND reference_id IS NULL", new (string, object?)[] { ("r", g.ToString()), ("id", moduleId), ("o", ownerId) }) > 0; }
        catch (Exception)
        {
            // paralleler Import war schneller (Unique-Index je Eigentümer): die Referenz bleibt bei dem anderen Modul
            if (await FindOwnModuleByReferenceAsync(ownerId, g.ToString()) is not null) return false;
            throw;
        }
    }

    /// <summary>
    /// Legt eine eingefrorene Kopie des Moduls samt aktiver Karten, Bildern, Quizkonfiguration und aktiven Pool-Fragen als Themenmodul an (<c>scope = 'topic'</c>).
    /// Quelle kann ein eigenes oder ein aktives globales Modul sein; die Berechtigung prüft der Aufrufer (<c>TopicService</c>).
    /// </summary>
    public async Task<ModuleInfo> CreateTopicSnapshotModuleAsync(ModuleInfo source, int topicVersion)
    {
        var sourceCards = await ListActiveCardsRawAsync(source.Id);
        var copy = new ModuleInfo
        {
            Name = source.Name, Description = source.Description, Icon = source.Icon, Color = source.Color,
            OwnerUserId = null, Scope = ScopeTopic, SourceModuleId = source.Id,
        };
        await InsertTopicModuleRowAsync(copy, source.Id);

        var cardIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in sourceCards)
        {
            var copyCard = new Card
            {
                ModuleId = copy.Id, OwnerUserId = null, Category = c.Category, Term = c.Term, Question = c.Question, Definition = c.Definition,
                HowItWorks = c.HowItWorks, Context = c.Context, KeyFact = c.KeyFact, ReferenceAnswer = c.ReferenceAnswer, ChatPrompt = c.ChatPrompt,
                OfficialSources = c.OfficialSources, SlideNumber = c.SlideNumber, TargetTimeSec = c.TargetTimeSec, Quiz = c.Quiz, Competencies = c.Competencies, Archived = false, SortOrder = c.SortOrder,
            };
            await InsertCardAsync(copyCard);
            await _images.CopyForCardAsync(c.Id, copyCard.Id);
            cardIdMap[c.Id] = copyCard.Id;
        }
        await _quizPool.CopyForStagedModuleAsync(source.Id, copy.Id, cardIdMap, topicVersion);
        await _db.ExecuteAsync("UPDATE cards SET quiz_synced = 1 WHERE module_id = @m", new[] { ("m", (object?)copy.Id) });
        await CopyCompetencyModelAsync(source.Id, copy.Id);   // Lernziele und Kompetenzen gehören zur Modulversion (ADR 0031)
        return copy;
    }

    /// <summary>
    /// Legt das (kartenlose) Prüfungsmodul einer Themenversion an: Es trägt die eigenen Themenfragen der Abschlussprüfung im bestehenden Fragenpool
    /// und die Quizkonfiguration der Abschlussprüfung — die Quiz-Engine arbeitet dadurch unverändert (Sitzung, Bewertung, Ergebnis).
    /// </summary>
    public async Task<ModuleInfo> CreateTopicExamModuleAsync(string name, string description, IEnumerable<TopicQuestion> questions, QuizConfig config, int topicVersion)
    {
        var module = new ModuleInfo { Name = name, Description = description, Icon = "🎓", OwnerUserId = null, Scope = ScopeTopic };
        await InsertTopicModuleRowAsync(module, null);
        await _quizPool.SaveConfigAsync(module.Id, config);
        foreach (var q in questions.Where(q => q.Active))
        {
            await _quizPool.CreateAsync(module.Id, new QuizQuestionInput
            {
                Type = q.Type, Question = q.Question, Options = q.Options.ToList(), CorrectIndexes = q.CorrectIndexes.ToList(), Explanation = q.Explanation,
            }, QuizOrigins.Manual, QuizSources.Editor, topicVersion);
        }
        return module;
    }

    private Task InsertTopicModuleRowAsync(ModuleInfo m, string? sourceModuleId) => _db.ExecuteAsync("""
        INSERT INTO modules (id, name, description, icon, color, owner_user_id, scope, source_module_id, created_at, updated_at)
        VALUES (@id, @name, @description, @icon, @color, NULL, 'topic', @src, @created_at, @updated_at)
        """, new (string, object?)[]
    {
        ("id", m.Id), ("name", m.Name), ("description", m.Description), ("icon", m.Icon), ("color", m.Color), ("src", sourceModuleId),
        ("created_at", m.CreatedAt), ("updated_at", m.UpdatedAt),
    });

    /// <summary>
    /// Verbindet ein abgegebenes Quizergebnis mit dem Themenprüfungsversuch (über die Quizsitzung). Idempotent; Benutzer und Sitzung müssen übereinstimmen.
    /// </summary>
    public async Task CompleteTopicAttemptAsync(QuizResultRecord result)
    {
        if (string.IsNullOrEmpty(result.SessionId)) return;
        var pct = result.MaxScore > 0 ? Math.Round(result.Score / result.MaxScore * 100, 1) : 0;
        await _db.ExecuteAsync("""
            UPDATE topic_exam_attempts SET result_id = @r, score = @s, max_score = @mx, percent = @p, passed = @pass, completed_at = @n
            WHERE session_id = @sid AND user_id = @u AND completed_at IS NULL
            """, new (string, object?)[]
        {
            ("r", result.Id), ("s", result.Score), ("mx", result.MaxScore), ("p", pct), ("pass", result.Passed == true ? 1 : 0), ("n", DateTime.UtcNow),
            ("sid", result.SessionId), ("u", result.UserSub),
        });
    }
}
