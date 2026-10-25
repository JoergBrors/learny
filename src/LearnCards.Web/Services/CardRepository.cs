using System.Text.Json;
using LearnCards.Web.Api;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Services.ImageStorage;

namespace LearnCards.Web.Services;

/// <summary>Datenzugriff für Module, Karten und Quiz-Ergebnisse.</summary>
public partial class CardRepository
{
    public sealed record ImportProgress(int Processed, int Total, string Module, string Term);

    /// <summary>Ergebnis eines Imports; <see cref="Errors"/> nennt (höchstens 20) abgelehnte Karten mit Grund, z. B. fehlerhafte Bilder.</summary>
    public sealed record ImportResult(int Created, int Updated, int Skipped, List<string> Errors)
    {
        public void Deconstruct(out int created, out int updated, out int skipped) { created = Created; updated = Updated; skipped = Skipped; }
    }

    private readonly IDatabase _db;
    private readonly CardImageService _images;
    private readonly QuizPoolService _quizPool;

    public CardRepository(IDatabase db, CardImageService images, QuizPoolService quizPool)
    {
        _db = db;
        _images = images;
        _quizPool = quizPool;
    }

    // ─── Module ─────────────────────────────────────────────────────────────
    // Alle Zugriffe sind auf den Eigentümer (ownerId = interne Benutzer-ID) eingeschränkt (ADR 0011).
    // Fremde oder unzugeordnete Datensätze verhalten sich wie "nicht vorhanden".

    private const string ModuleColumns =
        "m.id, m.name, m.description, m.icon, m.color, m.owner_user_id, m.scope, m.publication_id, m.global_version, m.global_status, " +
        "m.source_module_id, m.source_owner_user_id, m.source_owner_name, m.published_at, m.created_at, m.updated_at";

    private const string ModuleSelect =
        "SELECT " + ModuleColumns + ", COUNT(c.id) AS card_count, SUM(CASE WHEN c.archived = 0 THEN 1 ELSE 0 END) AS active_count " +
        "FROM modules m LEFT JOIN cards c ON c.module_id = m.id";

    private const string ModuleGroupBy = "GROUP BY " + ModuleColumns;

    public async Task<List<ModuleInfo>> ListModulesAsync(string ownerId)
    {
        var rows = await _db.QueryAsync($"{ModuleSelect} WHERE m.owner_user_id = @owner AND m.scope = 'private' {ModuleGroupBy} ORDER BY m.name",
            new[] { ("owner", (object?)ownerId) });
        var modules = rows.Select(MapModule).ToList();
        foreach (var m in modules) m.Context = ContextPrivate;
        return modules;
    }

    public async Task<ModuleInfo?> GetModuleAsync(string id, string ownerId)
    {
        if (!UserService.IsGuid(id)) return null;
        var rows = await _db.QueryAsync($"{ModuleSelect} WHERE m.id = @id AND m.owner_user_id = @owner {ModuleGroupBy}",
            new (string, object?)[] { ("id", id), ("owner", ownerId) });
        return rows.Count == 0 ? null : MapModule(rows[0]);
    }

    public async Task<ModuleInfo> CreateModuleAsync(ModuleCreateRequest req, string ownerId)
    {
        var m = new ModuleInfo
        {
            Name = req.Name.Trim(),
            Description = req.Description,
            Icon = string.IsNullOrWhiteSpace(req.Icon) ? "📚" : req.Icon,
            Color = string.IsNullOrWhiteSpace(req.Color) ? "#2AA79B" : req.Color,
            OwnerUserId = ownerId,
        };
        await _db.ExecuteAsync("""
            INSERT INTO modules (id, name, description, icon, color, owner_user_id, created_at, updated_at)
            VALUES (@id, @name, @description, @icon, @color, @owner, @created_at, @updated_at)
            """, new (string, object?)[]
        {
            ("id", m.Id), ("name", m.Name), ("description", m.Description),
            ("icon", m.Icon), ("color", m.Color), ("owner", ownerId), ("created_at", m.CreatedAt), ("updated_at", m.UpdatedAt),
        });
        return m;
    }

    public async Task<ModuleInfo> GetOrCreateModuleByNameAsync(string name, string ownerId)
    {
        var rows = await _db.QueryAsync("""
            SELECT id, name, description, icon, color, owner_user_id, created_at, updated_at
            FROM modules WHERE name = @name AND owner_user_id = @owner
            """, new (string, object?)[] { ("name", name.Trim()), ("owner", ownerId) });
        if (rows.Count > 0) return MapModule(rows[0]);
        return await CreateModuleAsync(new ModuleCreateRequest { Name = name }, ownerId);
    }

    /// <summary>
    /// Löscht ein eigenes Modul. Haben andere Benutzer bereits Lernergebnisse darauf (Freigabe), wird nicht gelöscht
    /// (die Fremdschlüssel würden deren Ergebnisse mitlöschen) — <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task<int> DeleteModuleAsync(string id, string ownerId)
    {
        if (!UserService.IsGuid(id)) return 0;
        if (await ForeignResultCountAsync("module_id", id, ownerId) > 0
            && (await GetModuleAsync(id, ownerId)) is not null)
            throw new InvalidOperationException("Das Modul kann nicht gelöscht werden: Andere Benutzer haben bereits Lernergebnisse darauf. Widerrufe die Freigaben und archiviere stattdessen die Karten.");
        var imageRefs = await _images.CollectForModuleAsync(id);   // vor dem Löschen sammeln (Zeilen verschwinden per Fremdschlüssel)
        var deleted = await _db.ExecuteAsync("DELETE FROM modules WHERE id = @id AND owner_user_id = @owner",
            new (string, object?)[] { ("id", id), ("owner", ownerId) });
        if (deleted > 0) await _images.CleanupOrphansAsync(imageRefs);   // Binärdaten ohne weitere Verweise entfernen (Kopien/Freigaben behalten ihre)
        return deleted;
    }

    // ─── Karten ─────────────────────────────────────────────────────────────

    public async Task<List<Card>> ListCardsAsync(string ownerId, string? moduleId = null, string? category = null, bool archived = false)
    {
        var sql = "SELECT * FROM cards WHERE owner_user_id = @owner AND archived = @archived";
        var args = new List<(string, object?)> { ("owner", ownerId), ("archived", archived) };
        if (!string.IsNullOrEmpty(moduleId))
        {
            if (!UserService.IsGuid(moduleId)) return new();
            sql += " AND module_id = @module_id"; args.Add(("module_id", moduleId));
        }
        if (!string.IsNullOrEmpty(category)) { sql += " AND category = @category"; args.Add(("category", category)); }
        sql += " ORDER BY category, sort_order, term";
        var cards = (await _db.QueryAsync(sql, args)).Select(MapCard).ToList();
        await _images.AttachAsync(cards);
        return cards;
    }

    public async Task<List<Card>> ListSlideCardsAsync(string ownerId, string moduleId, string? category = null)
    {
        if (!UserService.IsGuid(moduleId)) return new();
        var sql = """
            SELECT * FROM cards
            WHERE owner_user_id = @owner
              AND module_id = @module_id
              AND archived = 0
              AND slide_number IS NOT NULL
            """;
        var args = new List<(string, object?)> { ("owner", ownerId), ("module_id", moduleId) };
        if (!string.IsNullOrWhiteSpace(category))
        {
            sql += " AND category = @category";
            args.Add(("category", category));
        }

        var cards = (await _db.QueryAsync(sql + " ORDER BY slide_number, sort_order, term", args))
            .Select(MapCard)
            .ToList();
        await _images.AttachAsync(cards);
        return cards;
    }

    public async Task<List<string>> ListCategoriesAsync(string ownerId, string moduleId)
    {
        if (!UserService.IsGuid(moduleId)) return new();
        var rows = await _db.QueryAsync(
            "SELECT DISTINCT category FROM cards WHERE owner_user_id = @owner AND module_id = @m AND archived = 0 ORDER BY category",
            new (string, object?)[] { ("owner", ownerId), ("m", moduleId) });
        return rows.Select(r => r["category"] ?? "").Where(c => c.Length > 0).ToList();
    }

    public async Task<Card?> GetCardAsync(string id, string ownerId)
    {
        if (!UserService.IsGuid(id)) return null;
        var rows = await _db.QueryAsync("SELECT * FROM cards WHERE id = @id AND owner_user_id = @owner",
            new (string, object?)[] { ("id", id), ("owner", ownerId) });
        if (rows.Count == 0) return null;
        var card = MapCard(rows[0]);
        await _images.AttachAsync(new[] { card });
        return card;
    }

    public async Task<Card> CreateCardAsync(CardJson data, string ownerId)
    {
        var module = await GetOrCreateModuleByNameAsync(data.Module, ownerId);
        var card = new Card
        {
            // Eine vom Client vorgegebene ID wird nur übernommen, wenn sie noch nirgends existiert —
            // sonst würde eine Kollision fremde Karten verraten oder überschreiben.
            Id = await ResolveNewCardIdAsync(data.Id),
            ModuleId = module.Id,
            OwnerUserId = ownerId,
            Category = data.Category,
            Term = data.Term,
            Question = data.Question,
            Definition = data.Definition,
            HowItWorks = data.HowItWorks,
            Context = data.Context,
            KeyFact = data.KeyFact,
            ReferenceAnswer = data.ReferenceAnswer,
            ChatPrompt = data.ChatPrompt,
            OfficialSources = data.OfficialSources,
            SlideNumber = data.SlideNumber,
            TargetTimeSec = data.TargetTimeSec,
            Quiz = data.Quiz,
            Archived = data.Archived,
            SortOrder = data.SortOrder,
        };
        if (data.Images is { Count: > 0 }) EnsureValidImages(data.Images);
        await InsertCardAsync(card);
        await _quizPool.SyncCardQuizAsync(card);   // quiz[] → Fragenpool (ADR 0019)
        if (data.Images is { Count: > 0 })
        {
            try { card.Images = await _images.ReplaceForCardAsync(card.Id, data.Images); }
            catch
            {
                // Bildspeicher nicht erreichbar o. Ä.: Karte nicht halb angelegt zurücklassen
                await _db.ExecuteAsync("DELETE FROM cards WHERE id = @id", new[] { ("id", (object?)card.Id) });
                throw;
            }
        }
        return card;
    }

    private async Task<string> ResolveNewCardIdAsync(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || !Guid.TryParse(requested, out var g)) return Guid.NewGuid().ToString();
        var id = g.ToString();
        var exists = await _db.QueryAsync("SELECT 1 FROM cards WHERE id = @id", new[] { ("id", (object?)id) });
        return exists.Count == 0 ? id : Guid.NewGuid().ToString();
    }

    private Task InsertCardAsync(Card c) => _db.ExecuteAsync("""
        INSERT INTO cards (id, module_id, owner_user_id, category, term, question, definition, how_it_works,
                           context, key_fact, reference_answer, chat_prompt, official_sources_json,
                           slide_number, target_time_sec, quiz_json, images_json, images_migrated,
                           archived, sort_order, created_at, updated_at)
        VALUES (@id, @module_id, @owner_user_id, @category, @term, @question, @definition, @how_it_works,
                @context, @key_fact, @reference_answer, @chat_prompt, @official_sources_json,
                @slide_number, @target_time_sec, @quiz_json, @images_json, @images_migrated,
                @archived, @sort_order, @created_at, @updated_at)
        """, CardArgs(c));

    /// <summary>Die Karte wurde seit dem Laden im Editor von anderer Stelle geändert (optimistische Sperre).</summary>
    public sealed class CardConflictException : InvalidOperationException
    {
        public CardConflictException() : base("Die Karte wurde inzwischen an anderer Stelle geändert. Lade sie neu, bevor du speicherst — deine Änderungen würden sonst fremde überschreiben.") { }
    }

    /// <summary>
    /// Aktualisiert eine eigene Karte. Mit <paramref name="expectedUpdatedAt"/> (Stand beim Laden im Editor) wird erkannt, ob die Karte inzwischen
    /// anderswo geändert wurde (<see cref="CardConflictException"/>) — ohne den Wert gilt das bisherige „letzter Schreibzugriff gewinnt“ (Import, REST).
    /// </summary>
    public async Task<Card?> UpdateCardAsync(string id, CardJson data, string ownerId, DateTime? expectedUpdatedAt = null)
    {
        var card = await GetCardAsync(id, ownerId);
        if (card is null) return null;
        if (expectedUpdatedAt is { } expected && card.UpdatedAt != expected) throw new CardConflictException();
        card.Category = data.Category;
        card.Term = data.Term;
        card.Question = data.Question;
        card.Definition = data.Definition;
        card.HowItWorks = data.HowItWorks;
        card.Context = data.Context;
        card.KeyFact = data.KeyFact;
        card.ReferenceAnswer = data.ReferenceAnswer;
        card.ChatPrompt = data.ChatPrompt;
        card.OfficialSources = data.OfficialSources;
        card.SlideNumber = data.SlideNumber;
        card.TargetTimeSec = data.TargetTimeSec;
        card.Quiz = data.Quiz;
        if (data.Images is not null)   // fehlendes images = bestehende Bilder behalten; [] = entfernen
        {
            EnsureValidImages(data.Images);
            card.Images = await _images.ReplaceForCardAsync(card.Id, data.Images);
        }
        card.Archived = data.Archived;
        card.SortOrder = data.SortOrder;
        card.UpdatedAt = DateTime.UtcNow;
        await _db.ExecuteAsync("""
            UPDATE cards SET category=@category, term=@term, question=@question, definition=@definition,
                how_it_works=@how_it_works, context=@context, key_fact=@key_fact, reference_answer=@reference_answer,
                chat_prompt=@chat_prompt, official_sources_json=@official_sources_json,
                slide_number=@slide_number, target_time_sec=@target_time_sec, quiz_json=@quiz_json,
                archived=@archived, sort_order=@sort_order, updated_at=@updated_at
            WHERE id=@id AND owner_user_id=@owner_user_id
            """, CardArgs(card));
        await _quizPool.SyncCardQuizAsync(card);   // quiz[] → Fragenpool (ADR 0019)
        return await GetCardAsync(card.Id, ownerId) ?? card;   // aus der Datenbank: exakter Zeitstempel für die Konflikterkennung (X-Card-Updated-At)
    }

    /// <summary>Kartenfelder als Import-/Schreibformat (für Editor und Duplizieren). <c>Images</c> bleibt null (= vorhandene Bilder behalten), <c>Quiz</c> enthält nur die Karten-Fragen aus <c>quiz[]</c>.</summary>
    public static CardJson ToCardJson(Card c, string moduleName) => new()
    {
        Id = c.Id, Module = moduleName, Category = c.Category, Term = c.Term, Question = c.Question, Definition = c.Definition, HowItWorks = c.HowItWorks,
        Context = c.Context, KeyFact = c.KeyFact, ReferenceAnswer = c.ReferenceAnswer, ChatPrompt = c.ChatPrompt, OfficialSources = c.OfficialSources.ToList(),
        SlideNumber = c.SlideNumber, TargetTimeSec = c.TargetTimeSec, Quiz = c.Quiz.ToList(), Images = null, Archived = c.Archived, SortOrder = c.SortOrder,
    };

    /// <summary>
    /// Dupliziert eine eigene Karte (gleiches Modul): Felder, <c>quiz[]</c>, Bilder (gleiche Binärdaten, neue Bild-IDs) und die Editor-/KI-Fragen des Fragenpools.
    /// Die Kopie ist aktiv, hat den Titel „… (Kopie)“ und steht direkt hinter dem Original.
    /// </summary>
    public async Task<Card?> DuplicateCardAsync(string id, string ownerId)
    {
        var src = await GetCardAsync(id, ownerId);
        if (src is null) return null;
        var module = await GetModuleAsync(src.ModuleId, ownerId);
        if (module is null) return null;
        var data = ToCardJson(src, module.Name);
        data.Id = null; data.Archived = false; data.Term = (src.Term + " (Kopie)").Trim();
        data.SortOrder = src.SortOrder + 1;
        var copy = await CreateCardAsync(data, ownerId);
        await _images.CopyForCardAsync(src.Id, copy.Id);
        await _quizPool.CopyCardQuestionsAsync(src.Id, copy.Id);
        return await GetCardAsync(copy.Id, ownerId);
    }

    /// <summary>
    /// Setzt die Reihenfolge der Karten eines eigenen Moduls (<c>sort_order</c> = Position). Alle IDs müssen zu Karten des Moduls gehören.
    /// Die Anzeige sortiert nach Kategorie, dann <c>sort_order</c>.
    /// </summary>
    public async Task<int> ReorderCardsAsync(string moduleId, IReadOnlyList<string> orderedCardIds, string ownerId)
    {
        if (!UserService.IsGuid(moduleId) || await GetModuleAsync(moduleId, ownerId) is null) throw new KeyNotFoundException("Modul nicht gefunden.");
        if (orderedCardIds.Count != orderedCardIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()) throw new ArgumentException("Karten-IDs dürfen nicht doppelt vorkommen.");
        var own = (await _db.QueryAsync("SELECT id FROM cards WHERE module_id = @m AND owner_user_id = @o", new (string, object?)[] { ("m", moduleId), ("o", ownerId) }))
            .Select(r => r["id"] ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (orderedCardIds.Any(x => !own.Contains(x))) throw new ArgumentException("Eine Karten-ID gehört nicht zu diesem Modul.");
        var now = DateTime.UtcNow;
        for (var i = 0; i < orderedCardIds.Count; i++)
            await _db.ExecuteAsync("UPDATE cards SET sort_order = @s, updated_at = @n WHERE id = @id AND module_id = @m AND owner_user_id = @o",
                new (string, object?)[] { ("s", i + 1), ("n", now), ("id", orderedCardIds[i]), ("m", moduleId), ("o", ownerId) });
        return orderedCardIds.Count;
    }

    public async Task<Card?> SetArchivedAsync(string id, bool archived, string ownerId)
    {
        if (!UserService.IsGuid(id)) return null;
        var n = await _db.ExecuteAsync("UPDATE cards SET archived=@a, updated_at=@u WHERE id=@id AND owner_user_id=@owner",
            new (string, object?)[] { ("a", archived), ("u", DateTime.UtcNow), ("id", id), ("owner", ownerId) });
        return n == 0 ? null : await GetCardAsync(id, ownerId);
    }

    /// <summary>Löscht eine eigene Karte; bei Lernergebnissen anderer Benutzer auf dieser Karte → <see cref="InvalidOperationException"/> (archivieren statt löschen).</summary>
    public async Task<int> DeleteCardAsync(string id, string ownerId)
    {
        if (!UserService.IsGuid(id)) return 0;
        if (await ForeignResultCountAsync("card_id", id, ownerId) > 0 && (await GetCardAsync(id, ownerId)) is not null)
            throw new InvalidOperationException("Die Karte kann nicht gelöscht werden: Andere Benutzer haben bereits Lernergebnisse darauf. Archiviere sie stattdessen.");
        var imageRefs = await _images.CollectForCardAsync(id);
        var deleted = await _db.ExecuteAsync("DELETE FROM cards WHERE id = @id AND owner_user_id = @owner",
            new (string, object?)[] { ("id", id), ("owner", ownerId) });
        if (deleted > 0) await _images.CleanupOrphansAsync(imageRefs);
        return deleted;
    }

    // ─── Import ─────────────────────────────────────────────────────────────

    public async Task<ImportResult> ImportAsync(
        List<CardJson> cards,
        bool overwriteExisting,
        string ownerId,
        Func<ImportProgress, Task>? progress = null)
    {
        int created = 0, updated = 0, skipped = 0;
        var errors = new List<string>();

        for (var i = 0; i < cards.Count; i++)
        {
            var data = cards[i];
            var (ok, validationError) = data.Validate();
            if (!ok)
            {
                skipped++;
                if (errors.Count < 20) errors.Add($"Karte {i + 1} ('{data.Term}'): {validationError}");
                if (progress is not null)
                    await progress(new ImportProgress(i + 1, cards.Count, data.Module, data.Term));
                continue;
            }

            try
            {
                // Nur eigene Karten gelten als "vorhanden"; eine fremde ID führt zu einer neuen eigenen Karte.
                var existing = string.IsNullOrWhiteSpace(data.Id) ? null : await GetCardAsync(data.Id!, ownerId);

                if (existing is not null && !overwriteExisting) { skipped++; }
                else if (existing is not null)
                {
                    await UpdateCardAsync(existing.Id, data, ownerId);
                    updated++;
                }
                else
                {
                    await CreateCardAsync(data, ownerId);
                    created++;
                }
            }
            catch (Exception e) when (e is ArgumentException or ImageStorageException)
            {
                skipped++;
                if (errors.Count < 20) errors.Add($"Karte {i + 1} ('{data.Term}'): {e.Message}");
            }

            if (progress is not null)
                await progress(new ImportProgress(i + 1, cards.Count, data.Module, data.Term));
        }
        return new ImportResult(created, updated, skipped, errors);
    }

    /// <summary>Letzte Verteidigungslinie für alle Schreibpfade: Bilder müssen den Limits aus <see cref="CardImageLimits"/> entsprechen.</summary>
    private static void EnsureValidImages(List<CardImage> images)
    {
        var (ok, error) = CardImageValidator.ValidateAndNormalize(images);
        if (!ok) throw new ArgumentException(error);
    }

    // ─── Besitz / Administration (Aufrufer müssen die Admin-Rolle vorab über UserService prüfen) ───

    public async Task<OwnedContentSummary> GetOwnedSummaryAsync(string ownerId)
    {
        var m = await _db.QueryAsync("SELECT COUNT(*) AS n FROM modules WHERE owner_user_id = @o", new[] { ("o", (object?)ownerId) });
        var c = await _db.QueryAsync("""
            SELECT COALESCE(SUM(CASE WHEN archived = 0 THEN 1 ELSE 0 END), 0) AS active,
                   COALESCE(SUM(CASE WHEN archived = 0 THEN 0 ELSE 1 END), 0) AS archived
            FROM cards WHERE owner_user_id = @o
            """, new[] { ("o", (object?)ownerId) });
        return new OwnedContentSummary
        {
            Modules = DbValue.ToInt(m.FirstOrDefault()?.GetValueOrDefault("n")),
            ActiveCards = DbValue.ToInt(c.FirstOrDefault()?.GetValueOrDefault("active")),
            ArchivedCards = DbValue.ToInt(c.FirstOrDefault()?.GetValueOrDefault("archived")),
        };
    }

    /// <summary>Module ohne Eigentümer (Altbestand vor ADR 0011) — nur für die Administration.</summary>
    public async Task<List<ModuleInfo>> ListUnownedModulesAsync()
    {
        var rows = await _db.QueryAsync($"{ModuleSelect} WHERE m.owner_user_id IS NULL AND m.scope = 'private' {ModuleGroupBy} ORDER BY m.name");
        return rows.Select(MapModule).ToList();
    }

    /// <summary>
    /// Weist ein unzugeordnetes Modul samt Karten einem Benutzer zu. Fehler: "not_found", "already_owned", "name_conflict".
    /// </summary>
    public async Task<string?> AssignUnownedModuleAsync(string moduleId, string newOwnerId)
    {
        if (!UserService.IsGuid(moduleId)) return "not_found";
        var mod = await _db.QueryAsync("SELECT name, owner_user_id FROM modules WHERE id = @id AND scope = 'private'", new[] { ("id", (object?)moduleId) });
        if (mod.Count == 0) return "not_found";
        if (mod[0].GetValueOrDefault("owner_user_id") is { Length: > 0 }) return "already_owned";

        var clash = await _db.QueryAsync("SELECT 1 FROM modules WHERE owner_user_id = @o AND name = @n",
            new (string, object?)[] { ("o", newOwnerId), ("n", mod[0]["name"]) });
        if (clash.Count > 0) return "name_conflict";

        await _db.ExecuteAsync("UPDATE modules SET owner_user_id = @o WHERE id = @id AND owner_user_id IS NULL",
            new (string, object?)[] { ("o", newOwnerId), ("id", moduleId) });
        await _db.ExecuteAsync("UPDATE cards SET owner_user_id = @o WHERE module_id = @id",
            new (string, object?)[] { ("o", newOwnerId), ("id", moduleId) });
        return null;
    }

    // ─── Quiz-Ergebnisse ────────────────────────────────────────────────────

    public Task SaveQuizResultAsync(QuizResultRecord r) => _db.ExecuteAsync("""
        INSERT INTO quiz_results (id, module_id, card_id, user_sub, category, score, max_score, grade, feedback, answers_json, stats_json, context,
                                  session_id, passed, pass_percent, module_version, completed_at)
        VALUES (@id, @module_id, @card_id, @user_sub, @category, @score, @max_score, @grade, @feedback, @answers_json, @stats_json, @context,
                @session_id, @passed, @pass_percent, @module_version, @completed_at)
        """, new (string, object?)[]
    {
        ("id", r.Id), ("module_id", r.ModuleId), ("card_id", string.IsNullOrWhiteSpace(r.CardId) ? null : r.CardId), ("user_sub", r.UserSub), ("category", r.Category),
        ("score", r.Score), ("max_score", r.MaxScore), ("grade", r.Grade), ("feedback", r.Feedback),
        ("answers_json", r.AnswersJson), ("stats_json", r.StatsJson), ("context", r.Context),
        ("session_id", string.IsNullOrWhiteSpace(r.SessionId) ? null : r.SessionId), ("passed", r.Passed is null ? null : (r.Passed.Value ? 1 : 0)),
        ("pass_percent", r.PassPercent), ("module_version", r.ModuleVersion), ("completed_at", r.CompletedAt),
    });

    public async Task<List<QuizResultRecord>> QuizHistoryAsync(string userSub, string? moduleId = null, string? cardId = null, int limit = 50)
    {
        if ((!string.IsNullOrEmpty(moduleId) && !UserService.IsGuid(moduleId)) || (!string.IsNullOrEmpty(cardId) && !UserService.IsGuid(cardId)))
            return new();
        var sql = "SELECT * FROM quiz_results WHERE user_sub = @u";
        var args = new List<(string, object?)> { ("u", userSub) };
        if (!string.IsNullOrEmpty(moduleId)) { sql += " AND module_id = @m"; args.Add(("m", moduleId)); }
        if (!string.IsNullOrEmpty(cardId)) { sql += " AND card_id = @c"; args.Add(("c", cardId)); }
        sql += $" ORDER BY completed_at DESC LIMIT {Math.Clamp(limit, 1, 200)}";
        return (await _db.QueryAsync(sql, args)).Select(MapQuizResult).ToList();
    }

    public async Task<List<QuizHistoryEntry>> QuizHistoryDetailedAsync(string userSub, string? moduleId = null, string? cardId = null, int limit = 20)
    {
        var rows = await QuizHistoryAsync(userSub, moduleId, cardId, limit);
        return rows.Select(r => new QuizHistoryEntry
        {
            Result = r,
            Stats = ParseQuizStats(r.StatsJson),
            Answers = ParseGradedAnswers(r.AnswersJson),
        }).ToList();
    }

    public Task<int> DeleteQuizResultAsync(string userSub, string quizResultId) =>
        !UserService.IsGuid(quizResultId) ? Task.FromResult(0) : _db.ExecuteAsync("""
            DELETE FROM quiz_results
            WHERE id = @id AND user_sub = @user_sub
            """, new (string, object?)[]
        {
            ("id", quizResultId),
            ("user_sub", userSub),
        });

    // ─── Benutzerstatus / Präferenzen ─────────────────────────────────────

    public async Task<string> GetThemeAsync(string userSub)
    {
        var rows = await _db.QueryAsync("SELECT theme FROM user_preferences WHERE user_sub = @u", new[] { ("u", (object?)userSub) });
        return rows.FirstOrDefault()?.GetValueOrDefault("theme") ?? "";
    }

    public Task SaveThemeAsync(string userSub, string theme) =>
        UpsertAsync("user_preferences",
            new[]
            {
                ("user_sub", (object?)userSub),
                ("theme", theme),
                ("updated_at", DateTime.UtcNow),
            },
            "user_sub");

    public async Task<UserCardState?> GetUserCardStateAsync(string userSub, string cardId)
    {
        if (!UserService.IsGuid(cardId)) return null;
        var rows = await _db.QueryAsync("""
            SELECT user_sub, card_id, is_checked, marked_review, updated_at
            FROM user_card_states
            WHERE user_sub = @u AND card_id = @c
            """, new[] { ("u", (object?)userSub), ("c", (object?)cardId) });
        return rows.Count == 0 ? null : MapUserCardState(rows[0]);
    }

    public async Task<Dictionary<string, UserCardState>> GetUserCardStatesAsync(string userSub, string moduleId)
    {
        if (!UserService.IsGuid(moduleId)) return new(StringComparer.OrdinalIgnoreCase);
        var rows = await _db.QueryAsync("""
            SELECT s.user_sub, s.card_id, s.is_checked, s.marked_review, s.updated_at
            FROM user_card_states s
            JOIN cards c ON c.id = s.card_id
            WHERE s.user_sub = @u AND c.module_id = @m
            """, new[] { ("u", (object?)userSub), ("m", (object?)moduleId) });
        return rows.Select(MapUserCardState).ToDictionary(x => x.CardId, StringComparer.OrdinalIgnoreCase);
    }

    public Task SaveUserCardStateAsync(UserCardState state) =>
        UpsertAsync("user_card_states",
            new[]
            {
                ("user_sub", (object?)state.UserSub),
                ("card_id", state.CardId),
                ("is_checked", state.IsChecked),
                ("marked_review", state.MarkedReview),
                ("updated_at", state.UpdatedAt),
            },
            "user_sub", "card_id");

    public async Task<List<Card>> ListCheckedCardsAsync(string moduleId, string? category, string userSub)
    {
        var allCards = await ListReadableCardsAsync(userSub, moduleId, category);
        var states = await GetUserCardStatesAsync(userSub, moduleId);
        return allCards.Where(c => states.TryGetValue(c.Id, out var s) && s.IsChecked).ToList();
    }

    public Task SaveChatHistoryAsync(ChatHistoryEntry entry) =>
        _db.ExecuteAsync("""
            INSERT INTO chat_history (id, user_sub, card_id, original_question, normalized_question, assistant_answer, created_at)
            VALUES (@id, @user_sub, @card_id, @original_question, @normalized_question, @assistant_answer, @created_at)
            """, new (string, object?)[]
        {
            ("id", entry.Id),
            ("user_sub", entry.UserSub),
            ("card_id", entry.CardId),
            ("original_question", entry.OriginalQuestion),
            ("normalized_question", entry.NormalizedQuestion),
            ("assistant_answer", entry.AssistantAnswer),
            ("created_at", entry.CreatedAt),
        });

    public async Task<List<ChatHistoryEntry>> GetChatHistoryAsync(string userSub, string cardId, int limit = 50)
    {
        if (!UserService.IsGuid(cardId)) return new();
        var rows = await _db.QueryAsync($"""
            SELECT id, user_sub, card_id, original_question, normalized_question, assistant_answer, created_at
            FROM chat_history
            WHERE user_sub = @u AND card_id = @c
            ORDER BY created_at DESC
            LIMIT {Math.Clamp(limit, 1, 200)}
            """, new[] { ("u", (object?)userSub), ("c", (object?)cardId) });
        return rows.Select(MapChatHistoryEntry).ToList();
    }

    // ─── Mapping ────────────────────────────────────────────────────────────

    private static ModuleInfo MapModule(Dictionary<string, string?> r) => new()
    {
        Id = r.GetValueOrDefault("id") ?? "",
        Name = r.GetValueOrDefault("name") ?? "",
        Description = r.GetValueOrDefault("description") ?? "",
        Icon = r.GetValueOrDefault("icon") ?? "📚",
        Color = r.GetValueOrDefault("color") ?? "#2AA79B",
        OwnerUserId = r.GetValueOrDefault("owner_user_id"),
        Scope = r.GetValueOrDefault("scope") ?? "private",
        PublicationId = r.GetValueOrDefault("publication_id"),
        GlobalVersion = r.GetValueOrDefault("global_version") is { Length: > 0 } gv ? DbValue.ToInt(gv) : null,
        GlobalStatus = r.GetValueOrDefault("global_status"),
        SourceModuleId = r.GetValueOrDefault("source_module_id"),
        SourceOwnerUserId = r.GetValueOrDefault("source_owner_user_id"),
        SourceOwnerName = r.GetValueOrDefault("source_owner_name"),
        PublishedAt = r.GetValueOrDefault("published_at") is { Length: > 0 } pa ? DbValue.ToDateTime(pa) : null,
        CreatedAt = DbValue.ToDateTime(r.GetValueOrDefault("created_at")),
        UpdatedAt = DbValue.ToDateTime(r.GetValueOrDefault("updated_at")),
        CardCount = DbValue.ToInt(r.GetValueOrDefault("card_count")),
        ActiveCount = DbValue.ToInt(r.GetValueOrDefault("active_count")),
    };

    private static Card MapCard(Dictionary<string, string?> r) => new()
    {
        Id = r.GetValueOrDefault("id") ?? "",
        ModuleId = r.GetValueOrDefault("module_id") ?? "",
        OwnerUserId = r.GetValueOrDefault("owner_user_id"),
        Category = r.GetValueOrDefault("category") ?? "",
        Term = r.GetValueOrDefault("term") ?? "",
        Question = r.GetValueOrDefault("question") ?? "",
        Definition = r.GetValueOrDefault("definition") ?? "",
        HowItWorks = r.GetValueOrDefault("how_it_works") ?? "",
        Context = r.GetValueOrDefault("context") ?? "",
        KeyFact = r.GetValueOrDefault("key_fact") ?? "",
        ReferenceAnswer = r.GetValueOrDefault("reference_answer") ?? "",
        ChatPrompt = r.GetValueOrDefault("chat_prompt") ?? "",
        OfficialSources = ParseOfficialSources(r.GetValueOrDefault("official_sources_json")),
        SlideNumber = ParseNullableInt(r.GetValueOrDefault("slide_number")),
        TargetTimeSec = ParseNullableInt(r.GetValueOrDefault("target_time_sec")),
        Quiz = ParseCardQuiz(r.GetValueOrDefault("quiz_json")),
        Archived = DbValue.ToBool(r.GetValueOrDefault("archived")),
        SortOrder = DbValue.ToInt(r.GetValueOrDefault("sort_order")),
        CreatedAt = DbValue.ToDateTime(r.GetValueOrDefault("created_at")),
        UpdatedAt = DbValue.ToDateTime(r.GetValueOrDefault("updated_at")),
    };

    private static QuizResultRecord MapQuizResult(Dictionary<string, string?> r) => new()
    {
        Id = r.GetValueOrDefault("id") ?? "",
        ModuleId = r.GetValueOrDefault("module_id") ?? "",
        CardId = r.GetValueOrDefault("card_id") ?? "",
        UserSub = r.GetValueOrDefault("user_sub") ?? "",
        Category = r.GetValueOrDefault("category") ?? "",
        Score = DbValue.ToDouble(r.GetValueOrDefault("score")),
        MaxScore = DbValue.ToDouble(r.GetValueOrDefault("max_score")),
        Grade = r.GetValueOrDefault("grade") ?? "F",
        Feedback = r.GetValueOrDefault("feedback") ?? "",
        AnswersJson = r.GetValueOrDefault("answers_json") ?? "[]",
        StatsJson = r.GetValueOrDefault("stats_json") ?? "{}",
        Context = r.GetValueOrDefault("context") ?? "private",
        SessionId = r.GetValueOrDefault("session_id"),
        Passed = r.GetValueOrDefault("passed") is { Length: > 0 } pv ? DbValue.ToInt(pv) != 0 : null,
        PassPercent = r.GetValueOrDefault("pass_percent") is { Length: > 0 } pp ? DbValue.ToInt(pp) : null,
        ModuleVersion = r.GetValueOrDefault("module_version") is { Length: > 0 } mv ? DbValue.ToInt(mv) : null,
        CompletedAt = DbValue.ToDateTime(r.GetValueOrDefault("completed_at")),
    };

    private static UserCardState MapUserCardState(Dictionary<string, string?> r) => new()
    {
        UserSub = r.GetValueOrDefault("user_sub") ?? "",
        CardId = r.GetValueOrDefault("card_id") ?? "",
        IsChecked = DbValue.ToBool(r.GetValueOrDefault("is_checked")),
        MarkedReview = DbValue.ToBool(r.GetValueOrDefault("marked_review")),
        UpdatedAt = DbValue.ToDateTime(r.GetValueOrDefault("updated_at")),
    };

    private static ChatHistoryEntry MapChatHistoryEntry(Dictionary<string, string?> r) => new()
    {
        Id = r.GetValueOrDefault("id") ?? "",
        UserSub = r.GetValueOrDefault("user_sub") ?? "",
        CardId = r.GetValueOrDefault("card_id") ?? "",
        OriginalQuestion = r.GetValueOrDefault("original_question") ?? "",
        NormalizedQuestion = r.GetValueOrDefault("normalized_question") ?? "",
        AssistantAnswer = r.GetValueOrDefault("assistant_answer") ?? "",
        CreatedAt = DbValue.ToDateTime(r.GetValueOrDefault("created_at")),
    };

    private static (string, object?)[] CardArgs(Card c) => new (string, object?)[]
    {
        ("id", c.Id), ("module_id", c.ModuleId), ("owner_user_id", c.OwnerUserId), ("category", c.Category), ("term", c.Term),
        ("question", c.Question), ("definition", c.Definition), ("how_it_works", c.HowItWorks),
        ("context", c.Context), ("key_fact", c.KeyFact), ("reference_answer", c.ReferenceAnswer),
        ("chat_prompt", c.ChatPrompt), ("official_sources_json", JsonSerializer.Serialize(c.OfficialSources, AppJson.Options)),
        ("slide_number", c.SlideNumber), ("target_time_sec", c.TargetTimeSec), ("quiz_json", JsonSerializer.Serialize(c.Quiz, AppJson.Options)), ("images_json", "[]"), ("images_migrated", 1),
        ("archived", c.Archived), ("sort_order", c.SortOrder), ("created_at", c.CreatedAt), ("updated_at", c.UpdatedAt),
    };

    private static List<OfficialSource> ParseOfficialSources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<OfficialSource>>(json, AppJson.Options) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static List<CardQuizQuestion> ParseCardQuiz(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<CardQuizQuestion>>(json, AppJson.Options) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static int? ParseNullableInt(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : DbValue.ToInt(value);

    private static List<GradedAnswer> ParseGradedAnswers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<GradedAnswer>>(json, AppJson.Options) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static QuizSessionStats ParseQuizStats(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<QuizSessionStats>(json, AppJson.Options) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private Task<int> UpsertAsync(string table, IReadOnlyList<(string Name, object? Value)> values, params string[] keyColumns)
    {
        var existingWhere = string.Join(" AND ", keyColumns.Select(k => $"{k} = @{k}"));
        var updateColumns = values.Select(v => v.Name).Where(n => !keyColumns.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        var updateSet = string.Join(", ", updateColumns.Select(c => $"{c}=@{c}"));
        var insertColumns = string.Join(", ", values.Select(v => v.Name));
        var insertValues = string.Join(", ", values.Select(v => "@" + v.Name));

        return UpsertInternalAsync(table, existingWhere, updateSet, insertColumns, insertValues, values);
    }

    private async Task<int> UpsertInternalAsync(
        string table,
        string existingWhere,
        string updateSet,
        string insertColumns,
        string insertValues,
        IReadOnlyList<(string Name, object? Value)> values)
    {
        var exists = await _db.QueryAsync($"SELECT 1 FROM {table} WHERE {existingWhere} LIMIT 1", values);
        if (exists.Count > 0)
            return await _db.ExecuteAsync($"UPDATE {table} SET {updateSet} WHERE {existingWhere}", values);
        return await _db.ExecuteAsync($"INSERT INTO {table} ({insertColumns}) VALUES ({insertValues})", values);
    }
}
