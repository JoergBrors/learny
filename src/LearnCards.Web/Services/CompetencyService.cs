using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LearnCards.Web.Api;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>
/// Kompetenzmodell und Prüfungsnachweise (ADR 0031). Lernziele/Kompetenzen gehören zum Modul (und damit zur Modulversion); die Zuordnung zu Karten steht in <c>competencies[]</c> der Karte.
/// Nachweise entstehen ausschließlich aus dem serverseitig bewerteten Ergebnis (<see cref="QuizService"/>): Dieser Dienst bewertet nichts selbst, er ordnet die dort bewerteten
/// Fragen (über ihre Karte) den Kompetenzen zu. Ein unveränderlicher <see cref="ExamSnapshot"/> hält den Stand je Abschluss fest.
/// </summary>
public class CompetencyService
{
    private readonly IDatabase _db;
    private readonly CardRepository _repo;
    private readonly UserService _users;

    public CompetencyService(IDatabase db, CardRepository repo, UserService users)
    {
        _db = db;
        _repo = repo;
        _users = users;
    }

    // ─── Modell ─────────────────────────────────────────────────────────────

    /// <summary>Modell eines Moduls ohne Besitzprüfung (nur nach Berechtigungsprüfung des Aufrufers bzw. für interne Dienste).</summary>
    public async Task<ModuleCompetencyModel> GetModelRawAsync(string moduleId)
    {
        if (!UserService.IsGuid(moduleId)) return new();
        var rows = await _db.QueryAsync("SELECT definition_json FROM module_competencies WHERE module_id = @m", new[] { ("m", (object?)moduleId) });
        if (rows.Count == 0) return new();
        try { return JsonSerializer.Deserialize<ModuleCompetencyModel>(rows[0]["definition_json"] ?? "{}", AppJson.Options) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Lernziele und Kompetenzen eines lesbaren Moduls (eigen, freigegeben, global, Themenkontext).</summary>
    public async Task<ModuleCompetencyModel> GetReadableModelAsync(string userId, string moduleId)
    {
        if (await _repo.ResolveModuleAccessAsync(userId, moduleId) is null) throw new KeyNotFoundException("Modul nicht gefunden.");
        return await GetModelRawAsync(moduleId);
    }

    /// <summary>Ersetzt Lernziele und Kompetenzen eines eigenen privaten Moduls. Der Entwurf ändert sich; veröffentlichte Modulversionen bleiben unverändert.</summary>
    public async Task SaveModelAsync(string ownerId, string moduleId, ModuleCompetencyModel model)
    {
        var module = UserService.IsGuid(moduleId) ? await _repo.GetModuleAsync(moduleId, ownerId) : null;
        if (module is null || module.Scope != "private") throw new KeyNotFoundException("Modul nicht gefunden.");
        Normalize(model);
        var errors = model.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        await WriteRawAsync(module.Id, model);
        await _users.WriteAuditAsync(ownerId, "module.competencies.save", module.Id, $"objectives={model.Objectives.Count};competencies={model.Competencies.Count}");
    }

    private static void Normalize(ModuleCompetencyModel m)
    {
        foreach (var o in m.Objectives) { o.Id = (o.Id ?? "").Trim(); o.Text = (o.Text ?? "").Trim(); }
        foreach (var c in m.Competencies) { c.Id = (c.Id ?? "").Trim(); c.Name = (c.Name ?? "").Trim(); c.Description = (c.Description ?? "").Trim(); c.ObjectiveId = string.IsNullOrWhiteSpace(c.ObjectiveId) ? null : c.ObjectiveId.Trim(); }
    }

    /// <summary>Schreibt das Modell ohne Besitzprüfung (Import, Kopien, Snapshots — der Aufrufer hat bereits geprüft).</summary>
    public async Task WriteRawAsync(string moduleId, ModuleCompetencyModel model)
    {
        if (model.IsEmpty) { await _db.ExecuteAsync("DELETE FROM module_competencies WHERE module_id = @m", new[] { ("m", (object?)moduleId) }); return; }
        var json = JsonSerializer.Serialize(model, AppJson.Options);
        var n = await _db.ExecuteAsync("UPDATE module_competencies SET definition_json = @j, updated_at = @t WHERE module_id = @m", new (string, object?)[] { ("j", json), ("t", DateTime.UtcNow), ("m", moduleId) });
        if (n == 0) await _db.ExecuteAsync("INSERT INTO module_competencies (module_id, definition_json, updated_at) VALUES (@m, @j, @t)", new (string, object?)[] { ("m", moduleId), ("j", json), ("t", DateTime.UtcNow) });
    }

    /// <summary>Kennung der Bewertungskriterien (Version): SHA-256 des kanonischen Modells.</summary>
    public static string CriteriaVersion(ModuleCompetencyModel model) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(model, AppJson.Options)))).ToLowerInvariant();

    // ─── Bewertung der Kompetenzen ──────────────────────────────────────────

    /// <summary>Bewertete Frage eines Prüfungsversuchs (aus <c>QuizService</c>): Karte, erreichte und maximale Punkte.</summary>
    public sealed record GradedItem(string? CardId, double Score, double Max);

    /// <summary>
    /// Ordnet die bewerteten Fragen eines Versuchs den Kompetenzen zu. Nur Fragen mit Karte zählen (Fragen ohne Karte lassen sich keiner Kompetenz zuordnen).
    /// Status je Kompetenz: <c>demonstrated</c> (genug Fragen und Bestehensgrenze erreicht), <c>not_demonstrated</c> (genug Fragen, Grenze verfehlt) oder
    /// <c>insufficiently_tested</c> (weniger als die erforderlichen Fragen geprüft) — „Modul bestanden“ ersetzt nie einen Kompetenznachweis.
    /// </summary>
    public async Task<List<CompetencyEvidence>> EvaluateAsync(string moduleId, IReadOnlyList<GradedItem> graded)
    {
        var model = await GetModelRawAsync(moduleId);
        var result = new List<CompetencyEvidence>();
        if (model.Competencies.Count == 0) return result;
        var cardComps = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in await _db.QueryAsync("SELECT id, competencies_json FROM cards WHERE module_id = @m", new[] { ("m", (object?)moduleId) }))
            cardComps[r["id"] ?? ""] = CardRepository.ParseCompetencies(r.GetValueOrDefault("competencies_json")).ToHashSet(StringComparer.Ordinal);
        var objectives = model.Objectives.ToDictionary(o => o.Id, o => o.Text, StringComparer.Ordinal);
        foreach (var c in model.Competencies)
        {
            var items = graded.Where(g => g.CardId is { Length: > 0 } id && cardComps.TryGetValue(id, out var set) && set.Contains(c.Id)).ToList();
            var earned = items.Sum(i => i.Score); var max = items.Sum(i => i.Max);
            var pct = max > 0 ? Math.Round(earned / max * 100, 1) : 0;
            var status = items.Count < c.MinQuestions || max <= 0 ? CompetencyStatus.InsufficientlyTested : pct >= c.PassPercent ? CompetencyStatus.Demonstrated : CompetencyStatus.NotDemonstrated;
            result.Add(new CompetencyEvidence
            {
                Id = c.Id, Name = c.Name, Description = c.Description, ObjectiveId = c.ObjectiveId, Objective = c.ObjectiveId is { } oid && objectives.TryGetValue(oid, out var ot) ? ot : "",
                PassPercent = c.PassPercent, MinQuestions = c.MinQuestions, QuestionsAsked = items.Count, Earned = Math.Round(earned, 2), Max = Math.Round(max, 2), Percent = pct, Status = status,
            });
        }
        return result;
    }

    // ─── Prüfungssnapshot ───────────────────────────────────────────────────

    /// <summary>
    /// Legt den unveränderlichen Nachweissnapshot eines verifizierten, bestandenen Modulabschlusses an (idempotent je Ergebnis). Der Inhalt ist kanonisches JSON
    /// ohne Lernendendaten, Antworten und Fragetexte; der Hash ist SHA-256 über genau diese Zeichenfolge.
    /// </summary>
    public async Task<ExamSnapshot> CreateSnapshotAsync(QuizResultRecord result, ModuleInfo module, int moduleVersion, string moduleContentHash, int questionCount, List<CompetencyEvidence> evidence)
    {
        var existing = await GetByResultAsync(result.Id);
        if (existing is not null) return existing;
        var model = await GetModelRawAsync(module.Id);
        var snap = new ExamSnapshot { ResultId = result.Id, UserId = result.UserSub, ModuleId = module.Id, ModuleVersion = moduleVersion };
        var percent = result.MaxScore > 0 ? Math.Round(result.Score / result.MaxScore * 100, 1) : 0;
        var doc = new JsonObject
        {
            ["format"] = "learny-exam-snapshot", ["schema_version"] = 1, ["snapshot_id"] = snap.Id,
            ["module"] = new JsonObject { ["id"] = module.Id, ["name"] = module.Name, ["version"] = moduleVersion, ["scope"] = result.Context, ["content_hash"] = moduleContentHash },
            ["exam"] = new JsonObject
            {
                ["method"] = "module_quiz", ["pass_percent"] = result.PassPercent ?? 0, ["questions"] = questionCount, ["criteria_version"] = CriteriaVersion(model),
                ["completed_at"] = result.CompletedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            },
            ["result"] = new JsonObject { ["score"] = Math.Round(result.Score, 2), ["max_score"] = Math.Round(result.MaxScore, 2), ["percent"] = percent, ["passed"] = result.Passed == true },
            ["objectives"] = new JsonArray(model.Objectives.Select(o => (JsonNode)new JsonObject { ["id"] = o.Id, ["text"] = o.Text }).ToArray()),
            ["competencies"] = new JsonArray(evidence.Select(e => (JsonNode)new JsonObject
            {
                ["id"] = e.Id, ["name"] = e.Name, ["description"] = e.Description, ["objective_id"] = e.ObjectiveId, ["method"] = e.Method, ["pass_percent"] = e.PassPercent,
                ["min_questions"] = e.MinQuestions, ["questions_asked"] = e.QuestionsAsked, ["earned"] = e.Earned, ["max"] = e.Max, ["percent"] = e.Percent, ["status"] = e.Status,
            }).ToArray()),
        };
        return await StoreSnapshotAsync(snap, doc);
    }

    /// <summary>Speichert einen Snapshot (kanonisches JSON, Hash = SHA-256 darüber). Idempotent je <c>ResultId</c>: bei paralleler Anlage gilt der vorhandene.</summary>
    public async Task<ExamSnapshot> StoreSnapshotAsync(ExamSnapshot snap, JsonObject doc)
    {
        snap.Json = doc.ToJsonString();
        snap.Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snap.Json))).ToLowerInvariant();
        try
        {
            await _db.ExecuteAsync("""
                INSERT INTO exam_snapshots (id, result_id, user_id, module_id, module_version, kind, snapshot_json, snapshot_hash, created_at)
                VALUES (@id, @r, @u, @m, @v, @k, @j, @h, @c)
                """, new (string, object?)[] { ("id", snap.Id), ("r", snap.ResultId), ("u", snap.UserId), ("m", snap.ModuleId), ("v", snap.ModuleVersion), ("k", snap.Kind), ("j", snap.Json), ("h", snap.Hash), ("c", snap.CreatedAt) });
        }
        catch (Exception)
        {
            var raced = await GetByResultAsync(snap.ResultId);
            if (raced is not null) return raced;
            throw;
        }
        return snap;
    }

    private static ExamSnapshot Map(Dictionary<string, string?> r) => new()
    {
        Id = r["id"] ?? "", ResultId = r["result_id"] ?? "", UserId = r["user_id"] ?? "", ModuleId = r["module_id"] ?? "", ModuleVersion = DbValue.ToInt(r.GetValueOrDefault("module_version")), Kind = r.GetValueOrDefault("kind") ?? "module",
        Json = r["snapshot_json"] ?? "{}", Hash = r["snapshot_hash"] ?? "", CreatedAt = DbValue.ToDateTime(r.GetValueOrDefault("created_at")),
    };

    public async Task<ExamSnapshot?> GetByResultAsync(string resultId)
    {
        if (!UserService.IsGuid(resultId)) return null;
        var rows = await _db.QueryAsync("SELECT * FROM exam_snapshots WHERE result_id = @r", new[] { ("r", (object?)resultId) });
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    public async Task<ExamSnapshot?> GetAsync(string id)
    {
        if (!UserService.IsGuid(id)) return null;
        var rows = await _db.QueryAsync("SELECT * FROM exam_snapshots WHERE id = @id", new[] { ("id", (object?)id) });
        return rows.Count == 0 ? null : Map(rows[0]);
    }
}
