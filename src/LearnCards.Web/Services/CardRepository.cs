using System.Text.Json;
using LearnCards.Web.Api;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>Datenzugriff für Module, Karten und Quiz-Ergebnisse.</summary>
public class CardRepository
{
    public sealed record ImportProgress(int Processed, int Total, string Module, string Term);

    private readonly IDatabase _db;

    public CardRepository(IDatabase db) => _db = db;

    // ─── Module ─────────────────────────────────────────────────────────────

    public async Task<List<ModuleInfo>> ListModulesAsync()
    {
        var rows = await _db.QueryAsync("""
            SELECT m.id, m.name, m.description, m.icon, m.color, m.created_at, m.updated_at,
                   COUNT(c.id) AS card_count,
                   SUM(CASE WHEN c.archived = 0 THEN 1 ELSE 0 END) AS active_count
            FROM modules m
            LEFT JOIN cards c ON c.module_id = m.id
            GROUP BY m.id, m.name, m.description, m.icon, m.color, m.created_at, m.updated_at
            ORDER BY m.name
            """);
        return rows.Select(MapModule).ToList();
    }

    public async Task<ModuleInfo?> GetModuleAsync(string id)
    {
        var rows = await _db.QueryAsync("""
            SELECT m.id, m.name, m.description, m.icon, m.color, m.created_at, m.updated_at,
                   COUNT(c.id) AS card_count,
                   SUM(CASE WHEN c.archived = 0 THEN 1 ELSE 0 END) AS active_count
            FROM modules m
            LEFT JOIN cards c ON c.module_id = m.id
            WHERE m.id = @id
            GROUP BY m.id, m.name, m.description, m.icon, m.color, m.created_at, m.updated_at
            """, new[] { ("id", (object?)id) });
        return rows.Count == 0 ? null : MapModule(rows[0]);
    }

    public async Task<ModuleInfo> CreateModuleAsync(ModuleCreateRequest req)
    {
        var m = new ModuleInfo
        {
            Name = req.Name.Trim(),
            Description = req.Description,
            Icon = string.IsNullOrWhiteSpace(req.Icon) ? "📚" : req.Icon,
            Color = string.IsNullOrWhiteSpace(req.Color) ? "#2AA79B" : req.Color,
        };
        await _db.ExecuteAsync("""
            INSERT INTO modules (id, name, description, icon, color, created_at, updated_at)
            VALUES (@id, @name, @description, @icon, @color, @created_at, @updated_at)
            """, new (string, object?)[]
        {
            ("id", m.Id), ("name", m.Name), ("description", m.Description),
            ("icon", m.Icon), ("color", m.Color), ("created_at", m.CreatedAt), ("updated_at", m.UpdatedAt),
        });
        return m;
    }

    public async Task<ModuleInfo> GetOrCreateModuleByNameAsync(string name)
    {
        var rows = await _db.QueryAsync("SELECT id, name, description, icon, color, created_at, updated_at FROM modules WHERE name = @name",
            new[] { ("name", (object?)name.Trim()) });
        if (rows.Count > 0) return MapModule(rows[0]);
        return await CreateModuleAsync(new ModuleCreateRequest { Name = name });
    }

    public Task<int> DeleteModuleAsync(string id) =>
        _db.ExecuteAsync("DELETE FROM modules WHERE id = @id", new[] { ("id", (object?)id) });

    // ─── Karten ─────────────────────────────────────────────────────────────

    public async Task<List<Card>> ListCardsAsync(string? moduleId = null, string? category = null, bool archived = false)
    {
        var sql = "SELECT * FROM cards WHERE archived = @archived";
        var args = new List<(string, object?)> { ("archived", archived) };
        if (!string.IsNullOrEmpty(moduleId)) { sql += " AND module_id = @module_id"; args.Add(("module_id", moduleId)); }
        if (!string.IsNullOrEmpty(category)) { sql += " AND category = @category"; args.Add(("category", category)); }
        sql += " ORDER BY category, sort_order, term";
        return (await _db.QueryAsync(sql, args)).Select(MapCard).ToList();
    }

    public async Task<List<Card>> ListSlideCardsAsync(string moduleId, string? category = null)
    {
        var sql = """
            SELECT * FROM cards
            WHERE module_id = @module_id
              AND archived = 0
              AND slide_number IS NOT NULL
            """;
        var args = new List<(string, object?)> { ("module_id", moduleId) };
        if (!string.IsNullOrWhiteSpace(category))
        {
            sql += " AND category = @category";
            args.Add(("category", category));
        }

        return (await _db.QueryAsync(sql + " ORDER BY slide_number, sort_order, term", args))
            .Select(MapCard)
            .ToList();
    }

    public async Task<List<string>> ListCategoriesAsync(string moduleId)
    {
        var rows = await _db.QueryAsync(
            "SELECT DISTINCT category FROM cards WHERE module_id = @m AND archived = 0 ORDER BY category",
            new[] { ("m", (object?)moduleId) });
        return rows.Select(r => r["category"] ?? "").Where(c => c.Length > 0).ToList();
    }

    public async Task<Card?> GetCardAsync(string id)
    {
        var rows = await _db.QueryAsync("SELECT * FROM cards WHERE id = @id", new[] { ("id", (object?)id) });
        return rows.Count == 0 ? null : MapCard(rows[0]);
    }

    public async Task<Card> CreateCardAsync(CardJson data)
    {
        var module = await GetOrCreateModuleByNameAsync(data.Module);
        var card = new Card
        {
            Id = string.IsNullOrWhiteSpace(data.Id) ? Guid.NewGuid().ToString() : data.Id!,
            ModuleId = module.Id,
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
        await InsertCardAsync(card);
        return card;
    }

    private Task InsertCardAsync(Card c) => _db.ExecuteAsync("""
        INSERT INTO cards (id, module_id, category, term, question, definition, how_it_works,
                           context, key_fact, reference_answer, chat_prompt, official_sources_json,
                           slide_number, target_time_sec, quiz_json,
                           archived, sort_order, created_at, updated_at)
        VALUES (@id, @module_id, @category, @term, @question, @definition, @how_it_works,
                @context, @key_fact, @reference_answer, @chat_prompt, @official_sources_json,
                @slide_number, @target_time_sec, @quiz_json,
                @archived, @sort_order, @created_at, @updated_at)
        """, CardArgs(c));

    public async Task<Card?> UpdateCardAsync(string id, CardJson data)
    {
        var card = await GetCardAsync(id);
        if (card is null) return null;
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
        card.Archived = data.Archived;
        card.SortOrder = data.SortOrder;
        card.UpdatedAt = DateTime.UtcNow;
        await _db.ExecuteAsync("""
            UPDATE cards SET category=@category, term=@term, question=@question, definition=@definition,
                how_it_works=@how_it_works, context=@context, key_fact=@key_fact, reference_answer=@reference_answer,
                chat_prompt=@chat_prompt, official_sources_json=@official_sources_json,
                slide_number=@slide_number, target_time_sec=@target_time_sec, quiz_json=@quiz_json,
                archived=@archived, sort_order=@sort_order, updated_at=@updated_at
            WHERE id=@id
            """, CardArgs(card));
        return card;
    }

    public async Task<Card?> SetArchivedAsync(string id, bool archived)
    {
        var n = await _db.ExecuteAsync("UPDATE cards SET archived=@a, updated_at=@u WHERE id=@id",
            new (string, object?)[] { ("a", archived), ("u", DateTime.UtcNow), ("id", id) });
        return n == 0 ? null : await GetCardAsync(id);
    }

    public Task<int> DeleteCardAsync(string id) =>
        _db.ExecuteAsync("DELETE FROM cards WHERE id = @id", new[] { ("id", (object?)id) });

    // ─── Import ─────────────────────────────────────────────────────────────

    public async Task<(int Created, int Updated, int Skipped)> ImportAsync(
        List<CardJson> cards,
        bool overwriteExisting,
        Func<ImportProgress, Task>? progress = null)
    {
        int created = 0, updated = 0, skipped = 0;
        var moduleCache = new Dictionary<string, ModuleInfo>();

        for (var i = 0; i < cards.Count; i++)
        {
            var data = cards[i];
            var (ok, _) = data.Validate();
            if (!ok)
            {
                skipped++;
                if (progress is not null)
                    await progress(new ImportProgress(i + 1, cards.Count, data.Module, data.Term));
                continue;
            }

            if (!moduleCache.TryGetValue(data.Module, out var module))
            {
                module = await GetOrCreateModuleByNameAsync(data.Module);
                moduleCache[data.Module] = module;
            }

            var cardId = string.IsNullOrWhiteSpace(data.Id) ? Guid.NewGuid().ToString() : data.Id!;
            var existing = await GetCardAsync(cardId);

            if (existing is not null && !overwriteExisting) { skipped++; continue; }

            if (existing is not null)
            {
                await UpdateCardAsync(cardId, data);
                updated++;
            }
            else
            {
                data.Id = cardId;
                await CreateCardAsync(data);
                created++;
            }

            if (progress is not null)
                await progress(new ImportProgress(i + 1, cards.Count, data.Module, data.Term));
        }
        return (created, updated, skipped);
    }

    // ─── Quiz-Ergebnisse ────────────────────────────────────────────────────

    public Task SaveQuizResultAsync(QuizResultRecord r) => _db.ExecuteAsync("""
        INSERT INTO quiz_results (id, module_id, card_id, user_sub, category, score, max_score, grade, feedback, answers_json, stats_json, completed_at)
        VALUES (@id, @module_id, @card_id, @user_sub, @category, @score, @max_score, @grade, @feedback, @answers_json, @stats_json, @completed_at)
        """, new (string, object?)[]
    {
        ("id", r.Id), ("module_id", r.ModuleId), ("card_id", string.IsNullOrWhiteSpace(r.CardId) ? null : r.CardId), ("user_sub", r.UserSub), ("category", r.Category),
        ("score", r.Score), ("max_score", r.MaxScore), ("grade", r.Grade), ("feedback", r.Feedback),
        ("answers_json", r.AnswersJson), ("stats_json", r.StatsJson), ("completed_at", r.CompletedAt),
    });

    public async Task<List<QuizResultRecord>> QuizHistoryAsync(string userSub, string? moduleId = null, string? cardId = null, int limit = 50)
    {
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
        _db.ExecuteAsync("""
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
        var rows = await _db.QueryAsync("""
            SELECT user_sub, card_id, is_checked, marked_review, updated_at
            FROM user_card_states
            WHERE user_sub = @u AND card_id = @c
            """, new[] { ("u", (object?)userSub), ("c", (object?)cardId) });
        return rows.Count == 0 ? null : MapUserCardState(rows[0]);
    }

    public async Task<Dictionary<string, UserCardState>> GetUserCardStatesAsync(string userSub, string moduleId)
    {
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
        var allCards = await ListCardsAsync(moduleId, category, archived: false);
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
        CreatedAt = DbValue.ToDateTime(r.GetValueOrDefault("created_at")),
        UpdatedAt = DbValue.ToDateTime(r.GetValueOrDefault("updated_at")),
        CardCount = DbValue.ToInt(r.GetValueOrDefault("card_count")),
        ActiveCount = DbValue.ToInt(r.GetValueOrDefault("active_count")),
    };

    private static Card MapCard(Dictionary<string, string?> r) => new()
    {
        Id = r.GetValueOrDefault("id") ?? "",
        ModuleId = r.GetValueOrDefault("module_id") ?? "",
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
        ("id", c.Id), ("module_id", c.ModuleId), ("category", c.Category), ("term", c.Term),
        ("question", c.Question), ("definition", c.Definition), ("how_it_works", c.HowItWorks),
        ("context", c.Context), ("key_fact", c.KeyFact), ("reference_answer", c.ReferenceAnswer),
        ("chat_prompt", c.ChatPrompt), ("official_sources_json", JsonSerializer.Serialize(c.OfficialSources, AppJson.Options)),
        ("slide_number", c.SlideNumber), ("target_time_sec", c.TargetTimeSec), ("quiz_json", JsonSerializer.Serialize(c.Quiz, AppJson.Options)),
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
