using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnCards.Web.Api;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>
/// Persistenter Fragenpool und Quizkonfiguration (ADR 0019). Reine Datenzugriffsschicht ohne Benutzer-/Berechtigungslogik —
/// die Berechtigung (Eigentümer darf ändern, Lernende nur lesen/spielen) entscheidet der <see cref="QuizService"/> über
/// <c>CardRepository.ResolveModuleAccessAsync</c>.
/// <para>
/// <b>Quelle der Wahrheit:</b> Jede Frage hat genau einen Ort (<see cref="QuizPoolQuestion.Source"/>):
/// <c>card_json</c> = <c>quiz[]</c> der Karte (kanonischer Import-/Export-Vertrag, ADR 0006) — der Pool enthält davon nur eine Projektion, die bei jedem Kartenspeichern
/// exakt nachgeführt wird (neu → einfügen, entfallen → entfernen, unverändert → Zeile und Aktivstatus bleiben); <c>ai</c> = KI-generiert, nur im Pool; <c>editor</c> = nur im Pool.
/// Eine Frage wird nie unabhängig an zwei Orten weiterentwickelt.
/// </para>
/// </summary>
public class QuizPoolService
{
    private readonly IDatabase _db;

    public QuizPoolService(IDatabase db) => _db = db;

    // ─── Konfiguration ──────────────────────────────────────────────────────

    public async Task<QuizConfig> GetConfigAsync(string moduleId, CancellationToken ct = default)
    {
        if (!UserService.IsGuid(moduleId)) return new QuizConfig();
        var rows = await _db.QueryAsync("SELECT config_json FROM quiz_configs WHERE module_id = @m", new[] { ("m", (object?)moduleId) }, ct);
        if (rows.Count == 0) return new QuizConfig();
        try { return JsonSerializer.Deserialize<QuizConfig>(rows[0]["config_json"] ?? "{}", AppJson.Options) ?? new QuizConfig(); }
        catch (JsonException) { return new QuizConfig(); }
    }

    /// <summary>Speichert die Konfiguration; ungültige Kombinationen werden mit allen Gründen abgelehnt (<see cref="ArgumentException"/>).</summary>
    public async Task SaveConfigAsync(string moduleId, QuizConfig config, CancellationToken ct = default)
    {
        var errors = config.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        var json = JsonSerializer.Serialize(config, AppJson.Options);
        var now = DateTime.UtcNow;
        var exists = await _db.QueryAsync("SELECT 1 FROM quiz_configs WHERE module_id = @m", new[] { ("m", (object?)moduleId) }, ct);
        if (exists.Count > 0)
            await _db.ExecuteAsync("UPDATE quiz_configs SET config_json = @j, updated_at = @n WHERE module_id = @m",
                new (string, object?)[] { ("j", json), ("n", now), ("m", moduleId) }, ct);
        else
            await _db.ExecuteAsync("INSERT INTO quiz_configs (module_id, config_json, updated_at) VALUES (@m, @j, @n)",
                new (string, object?)[] { ("m", moduleId), ("j", json), ("n", now) }, ct);
    }

    // ─── Pool lesen ─────────────────────────────────────────────────────────

    public async Task<List<QuizPoolQuestion>> ListAsync(string moduleId, string? cardId = null, string? origin = null, bool? active = null, CancellationToken ct = default)
    {
        if (!UserService.IsGuid(moduleId)) return new();
        var sql = "SELECT * FROM quiz_questions WHERE module_id = @m";
        var args = new List<(string, object?)> { ("m", moduleId) };
        if (!string.IsNullOrEmpty(cardId))
        {
            if (!UserService.IsGuid(cardId)) return new();
            sql += " AND card_id = @c"; args.Add(("c", cardId));
        }
        if (!string.IsNullOrEmpty(origin)) { sql += " AND origin = @o"; args.Add(("o", origin)); }
        if (active is not null) { sql += " AND active = @a"; args.Add(("a", active.Value ? 1 : 0)); }
        sql += " ORDER BY created_at, id";
        return (await _db.QueryAsync(sql, args, ct)).Select(Map).ToList();
    }

    public async Task<QuizPoolQuestion?> GetAsync(string moduleId, string questionId, CancellationToken ct = default)
    {
        if (!UserService.IsGuid(moduleId) || !UserService.IsGuid(questionId)) return null;
        var rows = await _db.QueryAsync("SELECT * FROM quiz_questions WHERE id = @id AND module_id = @m",
            new (string, object?)[] { ("id", questionId), ("m", moduleId) }, ct);
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    // ─── Pool schreiben ─────────────────────────────────────────────────────

    /// <summary>Validiert eine Frage; Rückgabe: Fehlertext oder <c>null</c>.</summary>
    public static string? Validate(QuizQuestionInput q)
    {
        if (string.IsNullOrWhiteSpace(q.Question)) return "Die Frage darf nicht leer sein.";
        if (q.Question.Length > 2000) return "Die Frage ist zu lang (höchstens 2000 Zeichen).";
        if (string.IsNullOrWhiteSpace(q.Explanation)) return "Erklärung bzw. Musterlösung fehlt.";
        switch (q.Type)
        {
            case QuizTypes.SingleChoice:
                if (q.Options.Count < 2 || q.Options.Count > 6 || q.Options.Any(string.IsNullOrWhiteSpace)) return "single_choice benötigt 2 bis 6 nicht-leere Optionen.";
                if (q.CorrectIndexes.Count != 1 || q.CorrectIndexes[0] < 0 || q.CorrectIndexes[0] >= q.Options.Count) return "single_choice benötigt genau eine gültige richtige Option.";
                break;
            case QuizTypes.MultipleResponse:
                if (q.Options.Count < 3 || q.Options.Count > 6 || q.Options.Any(string.IsNullOrWhiteSpace)) return "multiple_response benötigt 3 bis 6 nicht-leere Optionen.";
                if (q.CorrectIndexes.Count == 0 || q.CorrectIndexes.Distinct().Count() != q.CorrectIndexes.Count || q.CorrectIndexes.Any(i => i < 0 || i >= q.Options.Count))
                    return "multiple_response benötigt mindestens eine, eindeutige, gültige richtige Option.";
                break;
            case QuizTypes.FreeText:
                if (q.Options.Count != 0 || q.CorrectIndexes.Count != 0) return "free_text hat keine Optionen und keine richtigen Indizes.";
                break;
            default:
                return "Unbekannter Fragetyp.";
        }
        if (q.Options.Any(o => o.Length > 500)) return "Eine Option ist zu lang (höchstens 500 Zeichen).";
        if (q.Options.Select(Normalize).Distinct().Count() != q.Options.Count) return "Optionen dürfen sich nicht doppeln.";
        return null;
    }

    /// <summary>
    /// Fügt eine Frage ein. Gleiche Frage (Fingerprint) im Modul → die vorhandene wird zurückgegeben, <c>Created=false</c> (keine Doppelten).
    /// </summary>
    public async Task<(QuizPoolQuestion Question, bool Created)> CreateAsync(string moduleId, QuizQuestionInput input, string origin, string source, int? moduleVersion = null, CancellationToken ct = default)
    {
        var error = Validate(input);
        if (error is not null) throw new ArgumentException(error);
        if (!string.IsNullOrEmpty(input.CardId) && !UserService.IsGuid(input.CardId)) throw new ArgumentException("Ungültige Karten-ID.");
        var fp = Fingerprint(input.CardId, input);
        var existing = await _db.QueryAsync("SELECT * FROM quiz_questions WHERE module_id = @m AND fingerprint = @f",
            new (string, object?)[] { ("m", moduleId), ("f", fp) }, ct);
        if (existing.Count > 0) return (Map(existing[0]), false);

        var now = DateTime.UtcNow;
        var q = new QuizPoolQuestion
        {
            ModuleId = moduleId, CardId = string.IsNullOrEmpty(input.CardId) ? null : input.CardId, Type = input.Type, Question = input.Question.Trim(),
            Options = input.Options.Select(o => o.Trim()).ToList(), CorrectIndexes = input.CorrectIndexes.OrderBy(i => i).ToList(), Explanation = input.Explanation.Trim(),
            Origin = origin, Source = source, Active = true, ModuleVersion = moduleVersion, Fingerprint = fp, CreatedAt = now, UpdatedAt = now,
        };
        await InsertAsync(q, ct);
        return (q, true);
    }

    /// <summary>Ändert eine Frage. Fragen aus <c>quiz[]</c> der Karte werden über die Karte geändert (eine Quelle der Wahrheit).</summary>
    public async Task<QuizPoolQuestion> UpdateAsync(string moduleId, string questionId, QuizQuestionInput input, CancellationToken ct = default)
    {
        var q = await GetAsync(moduleId, questionId, ct) ?? throw new KeyNotFoundException("Frage nicht gefunden.");
        if (q.Source == QuizSources.CardJson)
            throw new InvalidOperationException("Diese Frage stammt aus dem quiz[] der Karte und wird über die Karte geändert.");
        var error = Validate(input);
        if (error is not null) throw new ArgumentException(error);
        input.CardId ??= q.CardId;
        var fp = Fingerprint(input.CardId, input);
        var clash = await _db.QueryAsync("SELECT id FROM quiz_questions WHERE module_id = @m AND fingerprint = @f AND id <> @id",
            new (string, object?)[] { ("m", moduleId), ("f", fp), ("id", questionId) }, ct);
        if (clash.Count > 0) throw new InvalidOperationException("Eine gleiche Frage existiert bereits in diesem Modul.");
        q.CardId = string.IsNullOrEmpty(input.CardId) ? null : input.CardId; q.Type = input.Type; q.Question = input.Question.Trim();
        q.Options = input.Options.Select(o => o.Trim()).ToList(); q.CorrectIndexes = input.CorrectIndexes.OrderBy(i => i).ToList();
        q.Explanation = input.Explanation.Trim(); q.Fingerprint = fp; q.UpdatedAt = DateTime.UtcNow;
        await _db.ExecuteAsync("""
            UPDATE quiz_questions SET card_id=@card_id, type=@type, question=@question, options_json=@options_json, correct_json=@correct_json,
                explanation=@explanation, fingerprint=@fingerprint, updated_at=@updated_at
            WHERE id=@id AND module_id=@module_id
            """, Args(q), ct);
        return q;
    }

    /// <summary>Aktiviert/deaktiviert eine Frage. Ergebnisse bleiben unberührt (sie speichern ihre Fragen als Text).</summary>
    public async Task<bool> SetActiveAsync(string moduleId, string questionId, bool active, CancellationToken ct = default)
    {
        if (!UserService.IsGuid(moduleId) || !UserService.IsGuid(questionId)) return false;
        return await _db.ExecuteAsync("UPDATE quiz_questions SET active = @a, updated_at = @n WHERE id = @id AND module_id = @m",
            new (string, object?)[] { ("a", active ? 1 : 0), ("n", DateTime.UtcNow), ("id", questionId), ("m", moduleId) }, ct) > 0;
    }

    // ─── Abbildung quiz[] ↔ Pool ────────────────────────────────────────────

    /// <summary>Kanonische Karten-Quizfrage → Pool-Eingabe.</summary>
    public static QuizQuestionInput FromCardQuiz(string cardId, CardQuizQuestion c) => new()
    {
        CardId = cardId, Type = c.Type, Question = c.Question, Explanation = c.Explanation,
        Options = c.Type == QuizTypes.FreeText ? new() : c.Options.ToList(),
        CorrectIndexes = c.Type switch
        {
            QuizTypes.SingleChoice => new() { c.CorrectIndex },
            QuizTypes.MultipleResponse => (c.CorrectIndexes ?? new()).ToList(),
            _ => new(),
        },
    };

    /// <summary>
    /// Führt die Projektion von <c>quiz[]</c> einer Karte im Pool nach (siehe Klassenkommentar). Ungültige Einträge werden übersprungen.
    /// Setzt <c>cards.quiz_synced = 1</c>.
    /// </summary>
    public async Task SyncCardQuizAsync(Card card, CancellationToken ct = default)
    {
        if (!UserService.IsGuid(card.Id) || !UserService.IsGuid(card.ModuleId)) return;
        var desired = new Dictionary<string, QuizQuestionInput>();
        foreach (var c in card.Quiz)
        {
            if (!c.Validate().Ok) continue;
            var input = FromCardQuiz(card.Id, c);
            if (Validate(input) is not null) continue;
            desired[Fingerprint(card.Id, input)] = input;
        }

        var existing = (await _db.QueryAsync("SELECT * FROM quiz_questions WHERE card_id = @c AND source = @s",
            new (string, object?)[] { ("c", card.Id), ("s", QuizSources.CardJson) }, ct)).Select(Map).ToList();
        foreach (var e in existing)
        {
            if (desired.TryGetValue(e.Fingerprint, out var d))
            {
                if (!string.Equals(e.Explanation, d.Explanation.Trim(), StringComparison.Ordinal))
                    await _db.ExecuteAsync("UPDATE quiz_questions SET explanation = @x, updated_at = @n WHERE id = @id",
                        new (string, object?)[] { ("x", d.Explanation.Trim()), ("n", DateTime.UtcNow), ("id", e.Id) }, ct);
                desired.Remove(e.Fingerprint);   // bleibt unverändert (inkl. Aktivstatus)
            }
            else
            {
                await _db.ExecuteAsync("DELETE FROM quiz_questions WHERE id = @id", new[] { ("id", (object?)e.Id) }, ct);   // aus quiz[] entfallen
            }
        }
        foreach (var d in desired.Values)
            await CreateAsync(card.ModuleId, d, QuizOrigins.Manual, QuizSources.CardJson, null, ct);
        await _db.ExecuteAsync("UPDATE cards SET quiz_synced = 1 WHERE id = @id", new[] { ("id", (object?)card.Id) }, ct);
    }

    /// <summary>
    /// Startmigration: überführt <c>quiz[]</c> aller noch nicht übernommenen Karten in den Pool. Idempotent und verlustfrei —
    /// <c>cards.quiz_json</c> bleibt unverändert erhalten. Liefert die Anzahl bearbeiteter Karten.
    /// </summary>
    public async Task<int> SyncLegacyAsync(Func<string, Task<Card?>> loadCard, CancellationToken ct = default)
    {
        await _db.ExecuteAsync("UPDATE cards SET quiz_synced = 1 WHERE quiz_synced = 0 AND (quiz_json IS NULL OR quiz_json = '[]' OR quiz_json = '')", null, ct);
        var count = 0;
        var last = Guid.Empty.ToString();
        while (true)
        {
            var batch = await _db.QueryAsync("SELECT id FROM cards WHERE quiz_synced = 0 AND id > @last ORDER BY id LIMIT 50", new[] { ("last", (object?)last) }, ct);
            if (batch.Count == 0) break;
            foreach (var row in batch)
            {
                last = row["id"]!;
                var card = await loadCard(last);
                if (card is null) continue;
                await SyncCardQuizAsync(card, ct);
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Kopiert Konfiguration und aktive Pool-Fragen in eine neue (eingefrorene) Modulversion. <paramref name="cardIdMap"/> bildet alte → neue Karten-IDs ab.
    /// Danach ändern weder Lernende noch die Quelle die Fragen der Version.
    /// </summary>
    public async Task CopyForStagedModuleAsync(string sourceModuleId, string targetModuleId, IReadOnlyDictionary<string, string> cardIdMap, int version, CancellationToken ct = default)
    {
        var cfg = await GetConfigAsync(sourceModuleId, ct);
        await SaveConfigAsync(targetModuleId, cfg.Validate().Count == 0 ? cfg : new QuizConfig(), ct);
        foreach (var q in await ListAsync(sourceModuleId, null, null, true, ct))
        {
            string? newCard = null;
            if (!string.IsNullOrEmpty(q.CardId) && !cardIdMap.TryGetValue(q.CardId, out newCard)) continue;   // Karte nicht in der Kopie (z. B. archiviert)
            var copy = new QuizPoolQuestion
            {
                ModuleId = targetModuleId, CardId = newCard, Type = q.Type, Question = q.Question, Options = q.Options.ToList(), CorrectIndexes = q.CorrectIndexes.ToList(),
                Explanation = q.Explanation, Origin = q.Origin, Source = q.Source, Active = true, ModuleVersion = version,
                CreatedAt = q.CreatedAt, UpdatedAt = DateTime.UtcNow,
            };
            copy.Fingerprint = Fingerprint(newCard, ToInput(copy));
            await InsertAsync(copy, ct);
        }
    }

    // ─── Quizsitzungen (Lösungen nur serverseitig) ─────────────────────────

    public Task SaveSessionAsync(QuizSessionRecord s) => _db.ExecuteAsync("""
        INSERT INTO quiz_sessions (id, user_sub, module_id, card_id, category, context, module_version, config_json, questions_json, status, created_at)
        VALUES (@id, @user_sub, @module_id, @card_id, @category, @context, @module_version, @config_json, @questions_json, 'open', @created_at)
        """, new (string, object?)[]
    {
        ("id", s.Id), ("user_sub", s.UserSub), ("module_id", s.ModuleId), ("card_id", s.CardId), ("category", s.Category), ("context", s.Context),
        ("module_version", s.ModuleVersion), ("config_json", JsonSerializer.Serialize(s.Config, AppJson.Options)),
        ("questions_json", JsonSerializer.Serialize(s.Questions, AppJson.Options)), ("created_at", s.CreatedAt),
    });

    /// <summary>Lädt eine Sitzung des Benutzers; fremde oder unbekannte Sitzungen verhalten sich wie „nicht vorhanden“.</summary>
    public async Task<QuizSessionRecord?> LoadSessionAsync(string sessionId, string userSub)
    {
        if (!UserService.IsGuid(sessionId)) return null;
        var rows = await _db.QueryAsync("SELECT * FROM quiz_sessions WHERE id = @id AND user_sub = @u",
            new (string, object?)[] { ("id", sessionId), ("u", userSub) });
        if (rows.Count == 0) return null;
        var r = rows[0];
        return new QuizSessionRecord
        {
            Id = r["id"] ?? "", UserSub = r["user_sub"] ?? "", ModuleId = r["module_id"] ?? "", CardId = r.GetValueOrDefault("card_id") is { Length: > 0 } c ? c : null,
            Category = r.GetValueOrDefault("category") ?? "", Context = r.GetValueOrDefault("context") ?? "private",
            ModuleVersion = r.GetValueOrDefault("module_version") is { Length: > 0 } v ? DbValue.ToInt(v) : null,
            Config = Deserialize<QuizConfig>(r.GetValueOrDefault("config_json")) ?? new QuizConfig(),
            Questions = Deserialize<List<QuizSessionQuestion>>(r.GetValueOrDefault("questions_json")) ?? new(),
            Status = r.GetValueOrDefault("status") ?? "open", CreatedAt = DbValue.ToDateTime(r.GetValueOrDefault("created_at")),
        };
    }

    /// <summary>Atomar „abgegeben“ setzen; <c>false</c> = Sitzung war nicht offen (bereits abgegeben).</summary>
    public async Task<bool> ClaimSessionAsync(string sessionId, string userSub) =>
        await _db.ExecuteAsync("UPDATE quiz_sessions SET status = 'submitted' WHERE id = @id AND user_sub = @u AND status = 'open'",
            new (string, object?)[] { ("id", sessionId), ("u", userSub) }) > 0;

    /// <summary>Gültigkeit einer Sitzung ab Start; danach wird die Abgabe abgelehnt (410).</summary>
    public static readonly TimeSpan SessionTtl = TimeSpan.FromHours(4);
    /// <summary>Aufbewahrung der Sitzungen (abgegeben oder abgelaufen) bis zur Bereinigung. Ergebnisse (<c>quiz_results</c>) sind davon unabhängig und bleiben erhalten.</summary>
    public static readonly TimeSpan SessionRetention = TimeSpan.FromHours(24);

    public static bool IsExpired(QuizSessionRecord s) => DateTime.UtcNow - s.CreatedAt > SessionTtl;

    /// <summary>
    /// Entfernt Sitzungen, die älter als <see cref="SessionRetention"/> sind (begrenzt auf <paramref name="maxBatches"/> × <paramref name="batchSize"/> Zeilen je Aufruf;
    /// wiederholbar). Gültige und kürzlich abgegebene Sitzungen bleiben; gespeicherte Ergebnisse werden nie berührt.
    /// </summary>
    public async Task<int> CleanupSessionsAsync(int batchSize = 500, int maxBatches = 4, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - SessionRetention;
        var total = 0;
        for (var i = 0; i < maxBatches; i++)
        {
            var n = await _db.ExecuteAsync(
                "DELETE FROM quiz_sessions WHERE id IN (SELECT id FROM quiz_sessions WHERE created_at < @c ORDER BY created_at LIMIT " + Math.Clamp(batchSize, 1, 1000) + ")",
                new[] { ("c", (object?)cutoff) }, ct);
            total += n;
            if (n < batchSize) break;
        }
        return total;
    }

    public Task ReleaseSessionAsync(string sessionId, string userSub) =>
        _db.ExecuteAsync("UPDATE quiz_sessions SET status = 'open' WHERE id = @id AND user_sub = @u", new (string, object?)[] { ("id", sessionId), ("u", userSub) });

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, AppJson.Options); } catch (JsonException) { return default; }
    }

    // ─── Hilfen ─────────────────────────────────────────────────────────────

    public static QuizQuestionInput ToInput(QuizPoolQuestion q) => new()
    {
        CardId = q.CardId, Type = q.Type, Question = q.Question, Options = q.Options.ToList(), CorrectIndexes = q.CorrectIndexes.ToList(), Explanation = q.Explanation,
    };

    /// <summary>Stabiler Fingerprint einer fachlichen Frage (Karte, Typ, Fragetext, Optionen, richtige Optionen — unabhängig von Reihenfolge und Groß-/Kleinschreibung).</summary>
    public static string Fingerprint(string? cardId, QuizQuestionInput q)
    {
        var options = q.Options.Select(Normalize).OrderBy(o => o, StringComparer.Ordinal).ToList();
        var correct = q.CorrectIndexes.Where(i => i >= 0 && i < q.Options.Count).Select(i => Normalize(q.Options[i])).OrderBy(o => o, StringComparer.Ordinal).ToList();
        var text = string.Join("\u001f", (cardId ?? "").ToLowerInvariant(), q.Type, Normalize(q.Question), string.Join("\u001e", options), string.Join("\u001e", correct));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string Normalize(string s) => string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private Task InsertAsync(QuizPoolQuestion q, CancellationToken ct) => _db.ExecuteAsync("""
        INSERT INTO quiz_questions (id, module_id, card_id, type, question, options_json, correct_json, explanation, origin, source, active, module_version, fingerprint, created_at, updated_at)
        VALUES (@id, @module_id, @card_id, @type, @question, @options_json, @correct_json, @explanation, @origin, @source, @active, @module_version, @fingerprint, @created_at, @updated_at)
        """, Args(q), ct);

    private static (string, object?)[] Args(QuizPoolQuestion q) => new (string, object?)[]
    {
        ("id", q.Id), ("module_id", q.ModuleId), ("card_id", q.CardId), ("type", q.Type), ("question", q.Question),
        ("options_json", JsonSerializer.Serialize(q.Options, AppJson.Options)), ("correct_json", JsonSerializer.Serialize(q.CorrectIndexes, AppJson.Options)),
        ("explanation", q.Explanation), ("origin", q.Origin), ("source", q.Source), ("active", q.Active ? 1 : 0), ("module_version", q.ModuleVersion),
        ("fingerprint", q.Fingerprint), ("created_at", q.CreatedAt), ("updated_at", q.UpdatedAt),
    };

    private static QuizPoolQuestion Map(Dictionary<string, string?> r) => new()
    {
        Id = r["id"] ?? "", ModuleId = r["module_id"] ?? "", CardId = r.GetValueOrDefault("card_id") is { Length: > 0 } c ? c : null,
        Type = r["type"] ?? QuizTypes.SingleChoice, Question = r["question"] ?? "",
        Options = ParseList<string>(r.GetValueOrDefault("options_json")), CorrectIndexes = ParseList<int>(r.GetValueOrDefault("correct_json")),
        Explanation = r.GetValueOrDefault("explanation") ?? "", Origin = r["origin"] ?? QuizOrigins.Manual, Source = r["source"] ?? QuizSources.Editor,
        Active = DbValue.ToInt(r.GetValueOrDefault("active")) != 0,
        ModuleVersion = r.GetValueOrDefault("module_version") is { Length: > 0 } v ? DbValue.ToInt(v) : null,
        Fingerprint = r.GetValueOrDefault("fingerprint") ?? "",
        CreatedAt = DbValue.ToDateTime(r.GetValueOrDefault("created_at")), UpdatedAt = DbValue.ToDateTime(r.GetValueOrDefault("updated_at")),
    };

    private static List<T> ParseList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<T>>(json, AppJson.Options) ?? new(); }
        catch (JsonException) { return new(); }
    }
}
