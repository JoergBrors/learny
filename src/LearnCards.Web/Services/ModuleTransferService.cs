using System.Text.Json;
using System.Text.Json.Nodes;
using LearnCards.Web.Api;
using LearnCards.Web.Domain;
using LearnCards.Web.Services.ImageStorage;

namespace LearnCards.Web.Services;

/// <summary>
/// Vollständiger JSON-Export/-Import von Modulen und Klonen globaler Module in eigene Module (ADR 0028).
/// Es gibt genau einen Weg: Klonen = Export des globalen Moduls + Import als neues eigenes Modul. Dadurch gelten für UI, REST und MCP dieselben
/// Validatoren, Bildregeln (<see cref="CardImageService"/>) und die Quiz-Synchronisation (<c>quiz[]</c> der Karte bleibt Quelle der Wahrheit).
/// </summary>
public class ModuleTransferService
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> RefGates = new();

    public const int MaxCards = 2000;
    public const int MaxPoolQuestions = 5000;

    private readonly CardRepository _repo;
    private readonly CardImageService _images;
    private readonly QuizPoolService _pool;
    private readonly UserService _users;

    public ModuleTransferService(CardRepository repo, CardImageService images, QuizPoolService pool, UserService users)
    {
        _repo = repo;
        _images = images;
        _pool = pool;
        _users = users;
    }

    // ─── Export ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Exportiert ein eigenes oder ein aktives globales Modul als portable Definition inkl. Bildern (Base64), Karten-Quiz, Konfiguration und zusätzlicher Pool-Fragen.
    /// Mit dem Benutzer geteilte Module fremder Eigentümer sind nicht exportierbar.
    /// </summary>
    public async Task<ModuleDefinition> ExportAsync(string userId, string moduleId, CancellationToken ct = default)
    {
        var access = UserService.IsGuid(moduleId) ? await _repo.ResolveModuleAccessAsync(userId, moduleId) : null;
        if (access is null || access.Context is not (CardRepository.ContextPrivate or CardRepository.ContextGlobal)) throw new KeyNotFoundException("Modul nicht gefunden.");
        return await BuildDefinitionAsync(access.Module, includeArchived: access.Context == CardRepository.ContextPrivate, ct);
    }

    private async Task<ModuleDefinition> BuildDefinitionAsync(ModuleInfo module, bool includeArchived, CancellationToken ct)
    {
        var cards = (await _repo.ListAllCardsOfModuleRawAsync(module.Id)).Where(c => includeArchived || !c.Archived)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Category, StringComparer.Ordinal).ThenBy(c => c.Term, StringComparer.Ordinal).ToList();
        var def = new ModuleDefinition
        {
            Name = module.Name, Description = module.Description, Icon = module.Icon, Color = module.Color,
            ReferenceId = module.Scope == "private" && module.OwnerUserId is { } ownerId ? await _repo.EnsureReferenceIdAsync(module.Id, ownerId) : null,
            QuizConfig = await _pool.GetConfigAsync(module.Id, ct),
        };
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cards)
        {
            var json = CardRepository.ToCardJson(c, module.Name);
            json.Id = null;   // IDs sind installationsspezifisch und werden beim Import neu vergeben
            json.Images = new List<CardImage>();
            foreach (var img in c.Images.OrderBy(i => i.SortOrder))
                json.Images.Add(new CardImage
                {
                    MimeType = img.MimeType, AltText = img.AltText, Caption = img.Caption, SortOrder = img.SortOrder,
                    DataBase64 = Convert.ToBase64String((await _images.GetContentAsync(img, ct)).Data),
                });
            index[c.Id] = def.Cards.Count;
            def.Cards.Add(json);
        }
        foreach (var q in await _pool.ListAsync(module.Id, null, null, null, ct))
        {
            if (q.Source == QuizSources.CardJson) continue;   // Quelle ist quiz[] der Karte
            if (q.CardId is not null && !index.ContainsKey(q.CardId)) continue;   // Karte nicht Teil des Exports
            def.PoolQuestions.Add(new ModulePoolQuestionDefinition
            {
                CardIndex = q.CardId is null ? null : index[q.CardId], Type = q.Type, Question = q.Question, Options = q.Options.ToList(),
                CorrectIndexes = q.CorrectIndexes.ToList(), Explanation = q.Explanation, Origin = q.Origin, Active = q.Active,
            });
        }
        return def;
    }

    // ─── Parsen und Validieren (ohne Datenbankzugriff) ──────────────────────

    /// <summary>Liest eine Moduldefinition aus JSON; prüft Format, Version und unbekannte Felder und liefert verständliche Fehler (<see cref="ArgumentException"/>).</summary>
    public static ModuleDefinition Parse(string json)
    {
        var def = TransferJson.Parse<ModuleDefinition>(json, TransferFormats.Module, new[] { "format", "schema_version", "reference_id", "name", "description", "icon", "color", "cards", "quiz_config", "pool_questions" });
        Validate(def, "");
        return def;
    }

    /// <summary>Vollständige fachliche Prüfung; wirft <see cref="ArgumentException"/> mit allen gefundenen Fehlern (höchstens 10).</summary>
    public static void Validate(ModuleDefinition def, string path)
    {
        var errors = new List<string>();
        void Err(string m) { if (errors.Count < 10) errors.Add(path + m); }
        if (def.Format != TransferFormats.Module) Err($"'format' muss \"{TransferFormats.Module}\" sein.");
        if (def.SchemaVersion != TransferFormats.SchemaVersion) Err($"'schema_version' {def.SchemaVersion} wird nicht unterstützt (unterstützt: {TransferFormats.SchemaVersion}).");
        if (def.ReferenceId is not null && !Guid.TryParse(def.ReferenceId, out _)) Err("'reference_id' muss eine GUID sein.");
        var name = (def.Name ?? "").Trim();
        if (name.Length == 0) Err("'name' ist erforderlich.");
        else if (name.Length > TopicService.MaxNameLength) Err($"'name' ist zu lang (höchstens {TopicService.MaxNameLength} Zeichen).");
        if ((def.Description ?? "").Length > 2000) Err("'description' ist zu lang (höchstens 2000 Zeichen).");
        def.Cards ??= new();
        def.PoolQuestions ??= new();
        if (def.Cards.Count > MaxCards) Err($"Zu viele Karten (höchstens {MaxCards}).");
        if (def.PoolQuestions.Count > MaxPoolQuestions) Err($"Zu viele Pool-Fragen (höchstens {MaxPoolQuestions}).");
        if (errors.Count == 0)
        {
            for (var i = 0; i < def.Cards.Count && errors.Count < 10; i++)
            {
                var c = def.Cards[i];
                c.Module = name;   // die Karte gehört zu diesem Modul, unabhängig vom Feld im Export
                var (ok, error) = c.Validate();
                if (!ok) Err($"Karte {i + 1} ('{c.Term}'): {error}");
                else if (c.Images is { Count: > 0 })
                {
                    var (imgOk, imgError) = CardImageValidator.ValidateAndNormalize(c.Images);
                    if (!imgOk) Err($"Karte {i + 1} ('{c.Term}'): {imgError}");
                }
            }
            if (def.QuizConfig is not null)
                foreach (var e in def.QuizConfig.Validate()) Err("quiz_config: " + e);
            for (var i = 0; i < def.PoolQuestions.Count && errors.Count < 10; i++)
            {
                var q = def.PoolQuestions[i];
                var error = QuizPoolService.Validate(new QuizQuestionInput { Type = q.Type, Question = q.Question, Options = q.Options ?? new(), CorrectIndexes = q.CorrectIndexes ?? new(), Explanation = q.Explanation ?? "" });
                if (error is not null) Err($"Pool-Frage {i + 1}: {error}");
                if (q.Origin is not (QuizOrigins.Manual or QuizOrigins.Ai)) Err($"Pool-Frage {i + 1}: 'origin' muss manual oder ai sein.");
                if (q.CardIndex is { } ci && (ci < 0 || ci >= def.Cards.Count)) Err($"Pool-Frage {i + 1}: 'card_index' verweist auf keine Karte.");
            }
        }
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
    }

    // ─── Import ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Legt aus einer (bereits validierten) Definition ein neues privates Modul des Benutzers an. Ein vorhandenes Modul wird nie überschrieben: bei Namensgleichheit
    /// entsteht „Name (Import)“ bzw. „Name (Import 2)“. Alle IDs sind neu. Bei einem Fehler wird das angefangene Modul vollständig wieder entfernt.
    /// </summary>
    public Task<ModuleInfo> ImportAsync(string userId, ModuleDefinition def, string suffix = "Import", CancellationToken ct = default) => ImportCoreAsync(userId, def, suffix, ct);

    public async Task<ModuleInfo> ImportJsonAsync(string userId, string json, CancellationToken ct = default)
    {
        var def = Parse(json);
        var module = await ImportCoreAsync(userId, def, "Import", ct);
        await _users.WriteAuditAsync(userId, "module.import", module.Id, $"cards={def.Cards.Count}");
        return module;
    }

    private async Task<ModuleInfo> ImportCoreAsync(string userId, ModuleDefinition def, string suffix, CancellationToken ct)
    {
        Validate(def, "");
        var owner = await _users.GetAsync(userId);
        if (owner is null || !owner.IsActive) throw new UnauthorizedAccessException("Benutzer nicht aktiv.");
        ModuleInfo module;
        for (var attempt = 0; ; attempt++)
        {
            // parallele Importe desselben Namens: der Unique-Index (Eigentümer, Name) entscheidet; wer verliert, wählt einen neuen freien Namen
            var name = await UniqueNameAsync(userId, def.Name.Trim(), suffix);
            try { module = await _repo.CreateModuleAsync(new ModuleCreateRequest { Name = name, Description = def.Description ?? "", Icon = def.Icon ?? "", Color = def.Color ?? "" }, userId); break; }
            catch (Exception)
            {
                if (attempt >= 8 || !(await _repo.ListModulesAsync(userId)).Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))) throw;
            }
        }
        try
        {
            var created = new List<Card>();
            foreach (var cj in def.Cards)
            {
                ct.ThrowIfCancellationRequested();
                cj.Id = null;
                cj.Module = module.Name;
                created.Add(await _repo.CreateCardAsync(cj, userId));
            }
            if (def.QuizConfig is not null) await _pool.SaveConfigAsync(module.Id, def.QuizConfig, ct);
            foreach (var q in def.PoolQuestions)
            {
                var card = q.CardIndex is { } ci ? created[ci] : null;
                var input = new QuizQuestionInput { CardId = card?.Id, Type = q.Type, Question = q.Question, Options = q.Options.ToList(), CorrectIndexes = q.CorrectIndexes.ToList(), Explanation = q.Explanation ?? "" };
                var (question, _) = await _pool.CreateAsync(module.Id, input, q.Origin, q.Origin == QuizOrigins.Ai ? QuizSources.Ai : QuizSources.Editor, null, ct);
                if (!q.Active) await _pool.SetActiveAsync(module.Id, question.Id, false, ct);
            }
            if (def.ReferenceId is not null) await _repo.TryAssignReferenceIdAsync(module.Id, userId, def.ReferenceId);   // nur wenn der Eigentümer sie noch nicht verwendet
            return (await _repo.GetModuleAsync(module.Id, userId))!;
        }
        catch
        {
            try { await _repo.DeleteModuleAsync(module.Id, userId); } catch (Exception) { /* Aufräumen ist bestmöglich; der ursprüngliche Fehler ist maßgeblich */ }
            throw;
        }
    }

    private async Task<string> UniqueNameAsync(string userId, string baseName, string suffix)
    {
        var existing = (await _repo.ListModulesAsync(userId)).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (baseName.Length > TopicService.MaxNameLength - 16) baseName = baseName[..(TopicService.MaxNameLength - 16)];
        if (!existing.Contains(baseName)) return baseName;
        var name = $"{baseName} ({suffix})";
        for (var i = 2; existing.Contains(name); i++) name = $"{baseName} ({suffix} {i})";
        return name;
    }

    /// <summary>
    /// Für eingebettete Module in Themen-JSON: Besitzt der Benutzer bereits ein Modul mit der <c>reference_id</c> der Definition, wird dieses verwendet (nichts angelegt,
    /// nichts überschrieben, Anzeigename darf abweichen); sonst wird die Definition als neues eigenes Modul importiert (<c>Created = true</c>).
    /// </summary>
    public async Task<(ModuleInfo Module, bool Created)> ImportOrReuseAsync(string userId, ModuleDefinition def, CancellationToken ct = default)
    {
        if (def.ReferenceId is null || !Guid.TryParse(def.ReferenceId, out var g)) return (await ImportCoreAsync(userId, def, "Import", ct), true);
        // je Eigentümer und Referenz serialisieren: parallele Importe derselben Referenz erzeugen genau ein Modul, die übrigen verwenden es
        var gate = RefGates.GetOrAdd(userId + ":" + g, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (await _repo.FindOwnModuleByReferenceAsync(userId, g.ToString()) is { } existing) return (existing, false);
            return (await ImportCoreAsync(userId, def, "Import", ct), true);
        }
        finally { gate.Release(); }
    }

    // ─── Klonen ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Klont ein <b>aktives globales Modul</b> in ein neues privates Modul des Benutzers („Name (Kopie)“). Übernommen werden Karten, Reihenfolge, Bilder, Karten-Quiz,
    /// Quizkonfiguration und zusätzliche Pool-Fragen; nicht übernommen werden Eigentümer, Freigaben, Veröffentlichungsstatus, Lernstände, Ergebnisse und Zertifikate.
    /// Fremde private, mit dem Benutzer geteilte und eigene Module sind keine Klonquellen (Eigenes lässt sich im Karteneditor duplizieren bzw. exportieren).
    /// </summary>
    public async Task<ModuleInfo> CloneGlobalAsync(string userId, string moduleId, CancellationToken ct = default)
    {
        var access = UserService.IsGuid(moduleId) ? await _repo.ResolveModuleAccessAsync(userId, moduleId) : null;
        if (access is null || access.Context != CardRepository.ContextGlobal) throw new KeyNotFoundException("Globales Modul nicht gefunden.");
        var def = await BuildDefinitionAsync(access.Module, includeArchived: false, ct);
        def.ReferenceId = null;   // der Klon ist ein unabhängiges Objekt mit eigener Referenz
        var clone = await ImportCoreAsync(userId, def, "Kopie", ct);
        await _users.WriteAuditAsync(userId, "module.clone", clone.Id, $"source={access.Module.Id}");
        return clone;
    }
}

/// <summary>Gemeinsame JSON-Eingangsprüfung für portable Definitionen (Syntax, Objekt, Format, Version, unbekannte Felder).</summary>
public static class TransferJson
{
    public static T Parse<T>(string json, string expectedFormat, IReadOnlyCollection<string> allowedKeys)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Die JSON-Definition ist leer.");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException e) { throw new ArgumentException("Ungültiges JSON: " + e.Message); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Die JSON-Definition muss ein Objekt sein.");
            RejectUnknown(doc.RootElement, allowedKeys, "");
            if (!doc.RootElement.TryGetProperty("format", out var f) || f.ValueKind != JsonValueKind.String || f.GetString() != expectedFormat)
                throw new ArgumentException($"'format' muss \"{expectedFormat}\" sein.");
            if (!doc.RootElement.TryGetProperty("schema_version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var sv))
                throw new ArgumentException("'schema_version' (Zahl) ist erforderlich.");
            if (sv != TransferFormats.SchemaVersion) throw new ArgumentException($"'schema_version' {sv} wird nicht unterstützt (unterstützt: {TransferFormats.SchemaVersion}).");
            try { return doc.RootElement.Deserialize<T>(AppJson.Options) ?? throw new ArgumentException("Die JSON-Definition ist leer."); }
            catch (JsonException e) { throw new ArgumentException("Die JSON-Definition entspricht nicht dem Schema: " + e.Message); }
        }
    }

    public static void RejectUnknown(JsonElement obj, IReadOnlyCollection<string> allowed, string path)
    {
        foreach (var p in obj.EnumerateObject())
            if (!allowed.Contains(p.Name, StringComparer.Ordinal))
                throw new ArgumentException($"Unbekanntes Feld '{path}{p.Name}'. Erlaubt: {string.Join(", ", allowed)}.");
    }

    /// <summary>Serialisiert eine Definition als JSON-Baum ohne interne Bildfelder (Speicherschlüssel, Provider usw. verlassen den Server nie).</summary>
    public static JsonNode ToJson<T>(T definition)
    {
        var node = JsonSerializer.SerializeToNode(definition, AppJson.Options)!;
        Clean(node);
        return node;
    }

    private static readonly string[] ImageNoise = { "image_id", "card_id", "provider", "storage_key", "size_bytes", "checksum", "decoded_size" };

    private static void Clean(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                if (o.ContainsKey("data_base64") && o.ContainsKey("mime_type")) foreach (var k in ImageNoise) o.Remove(k);
                foreach (var kv in o.ToList()) Clean(kv.Value);
                break;
            case JsonArray a:
                foreach (var i in a) Clean(i);
                break;
        }
    }
}
