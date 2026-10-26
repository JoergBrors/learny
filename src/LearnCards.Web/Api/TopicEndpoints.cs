using LearnCards.Web.Domain;
using LearnCards.Web.Services;
using LearnCards.Web.Services.ImageStorage;

namespace LearnCards.Web.Api;

/// <summary>
/// Themen, Freigaben, Zuweisungen, Versionen und Themenprüfungen (ADR 0027). Alle Pfade nur mit angemeldetem Benutzer; Besitz und Rollen prüft der Service.
/// Für MCP-Tokens ist nur der lesende Zugriff auf Themen und Versionen erlaubt (keine Schreib-, Freigabe-, Zuweisungs- oder Administrationspfade).
/// </summary>
public static partial class ApiEndpoints
{
    /// <summary>
    /// Zugriff für MCP-Tokens: lesend GET auf /api/topics (nie Freigaben, Zuweisungen, Versuche, Fortschritt, Benutzer) sowie schreibend ausschließlich
    /// <c>POST /api/topics/import</c> und <c>PUT /api/topics/{id}/definition</c> (eigene Themenentwürfe; Besitz prüft der Service, ADR 0028).
    /// </summary>
    private static bool TokenMayReadTopics(HttpContext http)
    {
        var path = (http.Request.Path.Value ?? "").TrimEnd('/');
        if (HttpMethods.IsPost(http.Request.Method) && path.Equals("/api/topics/import", StringComparison.OrdinalIgnoreCase)) return true;
        if (HttpMethods.IsPut(http.Request.Method) && System.Text.RegularExpressions.Regex.IsMatch(path, "^/api/topics/[0-9a-fA-F-]{36}/definition$")) return true;
        return HttpMethods.IsGet(http.Request.Method) && path.StartsWith("/api/topics", StringComparison.OrdinalIgnoreCase)
               && !path.Contains("/shares", StringComparison.OrdinalIgnoreCase) && !path.Contains("/assignments", StringComparison.OrdinalIgnoreCase)
               && !path.Contains("/attempts", StringComparison.OrdinalIgnoreCase) && !path.Contains("/progress", StringComparison.OrdinalIgnoreCase)
               && !path.Contains("/users", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IResult> GuardTopic(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (QuizRuleException e) { return Results.Json(new { detail = e.Message }, statusCode: e.Status); }
        catch (UnauthorizedAccessException e) { return Results.Json(new { detail = e.Message }, statusCode: 403); }
        catch (PayloadTooLargeException e) { return Results.Json(new { detail = e.Message }, statusCode: 413); }
        catch (KeyNotFoundException e) { return Results.Json(new { detail = e.Message }, statusCode: 404); }
        catch (ArgumentException e) { return Results.Json(new { detail = e.Message }, statusCode: 400); }
        catch (InvalidOperationException e) { return Results.Json(new { detail = e.Message }, statusCode: 409 ); }
    }

    /// <summary>Größtes Modul-/Themen-JSON in Bytes (entspricht dem Kestrel-Standardlimit; die Weboberfläche lädt höchstens 28 MiB hoch). Größere Anfragen erhalten 413, nichts wird angelegt.</summary>
    public const long MaxTransferBodyBytes = 30_000_000;

    private sealed class PayloadTooLargeException : Exception
    {
        public PayloadTooLargeException() : base($"Die JSON-Datei ist zu groß (höchstens {MaxTransferBodyBytes / 1_000_000} MB). Teile den Inhalt auf mehrere Module bzw. verwende kleinere Bilder; nichts wurde importiert.") { }
    }

    private static async Task<string> ReadBodyAsync(HttpContext http)
    {
        if (http.Request.ContentLength > MaxTransferBodyBytes) throw new PayloadTooLargeException();
        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxTransferBodyBytes;
        try
        {
            using var reader = new StreamReader(http.Request.Body, System.Text.Encoding.UTF8);
            return await reader.ReadToEndAsync(http.RequestAborted);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException e) when (e.StatusCode == 413) { throw new PayloadTooLargeException(); }
    }

    private static IResult JsonDownload(System.Text.Json.Nodes.JsonNode node, string fileName)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return Results.File(bytes, "application/json", fileName);
    }

    private static object CompetencyModelDto(ModuleCompetencyModel m) => new { learning_objectives = m.Objectives, competencies = m.Competencies };
    private static object ModuleVersionDto(ModuleVersionInfo v) => new { version = v.VersionNumber, snapshot_module_id = v.SnapshotModuleId, published_at = v.PublishedAt, note = v.Note, cards = v.Cards, content_hash = v.ContentHash };
    private static object ModuleVersionsDto(ModuleVersionOverview o) => new { module_id = o.ModuleId, reference_id = o.ReferenceId, latest_version = o.LatestVersion, has_unpublished_changes = o.HasUnpublishedChanges, versions = o.Versions.Select(ModuleVersionDto) };

    private static object ModuleSummaryDto(ModuleInfo m) => new { id = m.Id, name = m.Name, description = m.Description, icon = m.Icon, color = m.Color, card_count = m.ActiveCount, scope = m.Scope };

    /// <summary>Modul-JSON (Export, Import, Klonen): vollständig über <see cref="ModuleTransferService"/>; Bildspeicherfehler werden wie bei den Bild-Endpunkten gemeldet.</summary>
    private static void MapModuleTransferEndpoints(RouteGroupBuilder secured)
    {
        async Task<IResult> Run(Func<Task<IResult>> action)
        {
            try { return await GuardTopic(action); }
            catch (ImageStorageException e) { return ImageError(e); }
        }
        secured.MapGet("/modules/{moduleId}/export", (string moduleId, HttpContext http, ModuleTransferService modules) =>
            Run(async () => JsonDownload(TransferJson.ToJson(await modules.ExportAsync(Uid(http), moduleId, http.RequestAborted)), "modul.json")));
        // Karten eines Moduls als kanonisches Karten-JSON in begrenzten Stapeln (für MCP export_cards); gleiche Zugriffsregeln wie der Modulexport
        secured.MapGet("/modules/{moduleId}/export/cards", (string moduleId, HttpContext http, ModuleTransferService modules, bool include_archived = false, int offset = 0, int limit = 0) =>
            Run(async () =>
            {
                var b = await modules.ExportCardsAsync(Uid(http), moduleId, include_archived, offset, limit, http.RequestAborted);
                return Results.Ok(new { module = b.ModuleName, context = b.Context, total = b.Total, offset = b.Offset, count = b.Count, next_offset = b.NextOffset, has_more = b.NextOffset is not null, cards = b.Cards });
            }));
        // Lernziele und Kompetenzen (ADR 0031): lesen für alle Berechtigten, ändern nur der Eigentümer privater Module (Entwurf; Versionen bleiben unverändert)
        secured.MapGet("/modules/{moduleId}/competencies", (string moduleId, HttpContext http, CompetencyService comps) =>
            Run(async () => Results.Ok(CompetencyModelDto(await comps.GetReadableModelAsync(Uid(http), moduleId)))));
        secured.MapPut("/modules/{moduleId}/competencies", (string moduleId, CompetencyModelRequest req, HttpContext http, CompetencyService comps) =>
            Run(async () => { var model = new ModuleCompetencyModel { Objectives = req.LearningObjectives ?? new(), Competencies = req.Competencies ?? new() }; await comps.SaveModelAsync(Uid(http), moduleId, model); return Results.Ok(CompetencyModelDto(model)); }));

        // Modulversionen (ADR 0030): nur eigene private Module
        secured.MapGet("/modules/{moduleId}/versions", (string moduleId, HttpContext http, ModuleVersionService versions) =>
            Run(async () => Results.Ok(ModuleVersionsDto(await versions.GetOverviewAsync(Uid(http), moduleId)))));
        secured.MapPost("/modules/{moduleId}/versions", (string moduleId, ModuleVersionRequest? req, HttpContext http, ModuleVersionService versions) =>
            Run(async () => Results.Json(ModuleVersionDto(await versions.PublishAsync(Uid(http), moduleId, req?.Note)), statusCode: 201)));
        secured.MapPost("/modules/import",(HttpContext http, ModuleTransferService modules) =>
            Run(async () => Results.Json(ModuleSummaryDto(await modules.ImportJsonAsync(Uid(http), await ReadBodyAsync(http), http.RequestAborted)), statusCode: 201)));
        secured.MapPost("/modules/{moduleId}/clone", (string moduleId, HttpContext http, ModuleTransferService modules) =>
            Run(async () => Results.Json(ModuleSummaryDto(await modules.CloneGlobalAsync(Uid(http), moduleId, http.RequestAborted)), statusCode: 201)));
    }

    private static void MapTopicEndpoints(RouteGroupBuilder secured)
    {
        MapModuleTransferEndpoints(secured);
        var t = secured.MapGroup("/topics");

        t.MapGet("/", (HttpContext http, TopicService topics, bool include_archived = false) =>
            GuardTopic(async () => Results.Ok(await topics.ListOverviewAsync(Uid(http), include_archived))));
        t.MapPost("/", (TopicWriteRequest req, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Json(await topics.CreateAsync(Uid(http), req.Name, req.Description), statusCode: 201)));
        t.MapGet("/users", (string? q, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok((await topics.SearchUsersAsync(Uid(http), q)).Select(u => new { id = u.Id, display_name = u.DisplayName, email = u.Email }))));

        // Verwendung der eigenen Module in eigenen Themen (Entwurf/Versionen) — nur eigene Themen, nie fremde (ADR 0029)
        t.MapGet("/module-usage", (HttpContext http, TopicService topics) => GuardTopic(async () => Results.Ok(await topics.ListModuleUsageAsync(Uid(http)))));

        // Standardansicht der Themenseite: aktuelle Veröffentlichung, Entwurf nur bei tatsächlicher Abweichung (version=0 = Entwurf ausdrücklich)
        t.MapGet("/{id}/view", (string id, int? version, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(await topics.GetViewAsync(Uid(http), id, version))));

        t.MapGet("/{id}", (string id, int? version, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(await topics.GetDetailAsync(Uid(http), id, version))));
        t.MapPut("/{id}", (string id, TopicWriteRequest req, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(await topics.UpdateAsync(Uid(http), id, req.Name, req.Description))));
        t.MapPost("/{id}/archive", (string id, TopicArchiveRequest req, HttpContext http, TopicService topics) =>
            GuardTopic(async () => { await topics.SetArchivedAsync(Uid(http), id, req.Archived); return Results.NoContent(); }));
        t.MapPost("/{id}/duplicate", (string id, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Json(await topics.DuplicateAsync(Uid(http), id), statusCode: 201)));

        // Portable Themen-JSON (ADR 0028): Export, Erstellen und Aktualisieren des Entwurfs; veröffentlicht nie
        t.MapGet("/{id}/export", (string id, HttpContext http, TopicService topics, bool include_modules = false) =>
            GuardTopic(async () => JsonDownload(TransferJson.ToJson(await topics.ExportAsync(Uid(http), id, include_modules)), "thema.json")));
        t.MapPost("/import", (HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Json(await topics.CreateFromDefinitionAsync(Uid(http), await ReadBodyAsync(http)), statusCode: 201)));
        t.MapPut("/{id}/definition", (string id, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(await topics.UpdateFromDefinitionAsync(Uid(http), id, await ReadBodyAsync(http)))));
        // Neuere Modulversion ausdrücklich in den Entwurf übernehmen (ohne version: aktueller Stand des Moduls); veröffentlichte Themenversionen bleiben unverändert (ADR 0030)
        t.MapPost("/{id}/modules/{moduleId}/adopt", (string id, string moduleId, ModuleAdoptRequest? req, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(new { module_version = await topics.AdoptModuleVersionAsync(Uid(http), id, moduleId, req?.Version) })));
        t.MapPost("/{id}/modules/{moduleId}/replace-with-clone", (string id, string moduleId, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(ModuleSummaryDto(await topics.ReplaceWithOwnCloneAsync(Uid(http), id, moduleId)))));

        // Module (Zuordnung; keine Kopien)
        t.MapGet("/{id}/addable-modules", (string id, string? q, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok((await topics.SearchAddableModulesAsync(Uid(http), id, q)).Select(m => new { id = m.Id, name = m.Name, description = m.Description, icon = m.Icon, color = m.Color, scope = m.Scope, card_count = m.ActiveCount }))));
        t.MapPost("/{id}/modules", (string id, TopicModuleRequest req, HttpContext http, TopicService topics) =>
            GuardTopic(async () => { await topics.AddModuleAsync(Uid(http), id, req.ModuleId); return Results.NoContent(); }));
        t.MapDelete("/{id}/modules/{moduleId}", (string id, string moduleId, HttpContext http, TopicService topics) =>
            GuardTopic(async () => await topics.RemoveModuleAsync(Uid(http), id, moduleId) ? Results.NoContent() : Results.Json(new { detail = "Modul ist dem Thema nicht zugeordnet." }, statusCode: 404)));
        t.MapPut("/{id}/modules/order", (string id, TopicOrderRequest req, HttpContext http, TopicService topics) =>
            GuardTopic(async () => { await topics.ReorderModulesAsync(Uid(http), id, req.ModuleIds ?? new()); return Results.NoContent(); }));

        // Prüfungskonfiguration und eigene Themenfragen (Entwurf)
        t.MapGet("/{id}/exam-config", (string id, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(await topics.GetExamConfigAsync(Uid(http), id))));
        t.MapPut("/{id}/exam-config", (string id, TopicExamConfig cfg, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(await topics.SaveExamConfigAsync(Uid(http), id, cfg))));
        t.MapGet("/{id}/questions", (string id, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(await topics.ListQuestionsAsync(Uid(http), id))));
        t.MapPost("/{id}/questions", (string id, TopicQuestionRequest req, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Json(await topics.CreateQuestionAsync(Uid(http), id, req.ToInput()), statusCode: 201)));
        t.MapPut("/{id}/questions/{questionId}", (string id, string questionId, TopicQuestionRequest req, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(await topics.UpdateQuestionAsync(Uid(http), id, questionId, req.ToInput(), req.Active))));
        t.MapDelete("/{id}/questions/{questionId}", (string id, string questionId, HttpContext http, TopicService topics) =>
            GuardTopic(async () => await topics.DeleteQuestionAsync(Uid(http), id, questionId) ? Results.NoContent() : Results.Json(new { detail = "Frage nicht gefunden." }, statusCode: 404)));

        // Versionen
        t.MapGet("/{id}/versions", (string id, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(await topics.ListVersionsAsync(Uid(http), id))));
        t.MapPost("/{id}/versions", (string id, TopicPublishRequest? req, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Json(await topics.PublishAsync(Uid(http), id, req?.SubmitGlobal ?? false), statusCode: 201)));
        t.MapPost("/{id}/versions/{version:int}/submit-global", (string id, int version, HttpContext http, TopicService topics) =>
            GuardTopic(async () => { await topics.SubmitGlobalAsync(Uid(http), id, version); return Results.NoContent(); }));

        // Freigaben und Zuweisungen
        t.MapGet("/{id}/shares", (string id, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(await topics.ListSharesAsync(Uid(http), id))));
        t.MapPost("/{id}/shares", (string id, TopicUserRequest req, HttpContext http, TopicService topics) =>
            GuardTopic(async () => { await topics.GrantShareAsync(Uid(http), id, req.UserId); return Results.NoContent(); }));
        t.MapDelete("/{id}/shares/{userId}", (string id, string userId, HttpContext http, TopicService topics) =>
            GuardTopic(async () => await topics.RevokeShareAsync(Uid(http), id, userId) ? Results.NoContent() : Results.Json(new { detail = "Freigabe nicht gefunden." }, statusCode: 404)));
        // Zuweisungen gibt es hier nicht mehr: ausschließlich administrativ unter /api/topic-admin (ADR 0029)

        // Lernfortschritt und Prüfungen (Lernende und Eigentümer; Abgabe über /api/quiz/submit mit der Sitzung)
        t.MapGet("/{id}/progress", (string id, int? version, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(await topics.GetProgressAsync(Uid(http), id, version))));
        t.MapPost("/{id}/seen", (string id, TopicSeenRequest req, HttpContext http, TopicService topics) =>
            GuardTopic(async () => { await topics.MarkSeenAsync(Uid(http), id, req.Version); return Results.NoContent(); }));
        t.MapGet("/{id}/attempts", (string id, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(await topics.ListAttemptsAsync(Uid(http), id))));
        t.MapPost("/{id}/exams/module", (string id, TopicExamRequest req, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(StartDto(await topics.StartModuleExamAsync(Uid(http), id, req.ModuleId, req.Version, http.RequestAborted), null))));
        t.MapPost("/{id}/exams/final", (string id, TopicExamRequest? req, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(StartDto(await topics.StartFinalExamAsync(Uid(http), id, req?.Version, http.RequestAborted), null))));

        // Themenzuweisungen und Lernstatus (ADR 0029): Rollen prüft der Service gegen die Datenbank (Admin/Themen-Admin schreiben, Auditor liest Status). Der Pfad liegt
        // bewusst außerhalb von /api/topics* — MCP-Tokens erreichen ihn nie (TokenAllowedPrefixes/TokenMayReadTopics).
        var ta = secured.MapGroup("/topic-admin");
        ta.MapGet("/topics", (HttpContext http, TopicService topics) => GuardTopic(async () => Results.Ok(await topics.ListAssignableTopicsAsync(Uid(http)))));
        ta.MapGet("/users", (string? q, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok((await topics.SearchUsersForAssignmentAsync(Uid(http), q)).Select(u => new { id = u.Id, display_name = u.DisplayName }))));
        ta.MapGet("/assignments", (string? topic_id, HttpContext http, TopicService topics) => GuardTopic(async () => Results.Ok(await topics.ListAssignmentsAsync(Uid(http), topic_id))));
        ta.MapPost("/assignments", (TopicAssignRequest req, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Json(await topics.AssignAsync(Uid(http), req.TopicId, req.UserId, req.Version, req.Mandatory ?? true, req.DueAt), statusCode: 201)));
        ta.MapDelete("/assignments/{assignmentId}", (string assignmentId, HttpContext http, TopicService topics) =>
            GuardTopic(async () => await topics.RevokeAssignmentAsync(Uid(http), assignmentId) ? Results.NoContent() : Results.Json(new { detail = "Zuweisung nicht gefunden." }, statusCode: 404)));
        ta.MapGet("/status", (string? topic_id, string? user_id, string? status, HttpContext http, TopicService topics, bool mandatory_only = false) =>
            GuardTopic(async () => Results.Ok(await topics.ListStatusAsync(Uid(http), new TopicService.StatusFilter { TopicId = topic_id, UserId = user_id, Status = status, MandatoryOnly = mandatory_only }))));

        // Administration der globalen Themenversionen (Rolle wird im Service gegen die DB geprüft; für MCP-Tokens gesperrt)
        var a = secured.MapGroup("/admin/topics");
        a.MapGet("/pending", (HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(await topics.ListPendingGlobalAsync(Uid(http)))));
        a.MapGet("/history", (HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(await topics.ListGlobalHistoryAsync(Uid(http)))));
        a.MapGet("/versions/{versionId}", (string versionId, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok(await topics.GetVersionForReviewAsync(Uid(http), versionId))));
        a.MapGet("/versions/{versionId}/modules/{moduleId}/cards", (string versionId, string moduleId, HttpContext http, TopicService topics) =>
            GuardTopic(async () => Results.Ok((await topics.GetReviewModuleCardsAsync(Uid(http), versionId, moduleId)).Select(c => new { id = c.Id, category = c.Category, term = c.Term, question = c.Question, definition = c.Definition }))));
        a.MapPost("/versions/{versionId}/approve", (string versionId, HttpContext http, TopicService topics) =>
            GuardTopic(async () => { await topics.ApproveGlobalAsync(Uid(http), versionId); return Results.NoContent(); }));
        a.MapPost("/versions/{versionId}/reject", (string versionId, TopicRejectRequest? req, HttpContext http, TopicService topics) =>
            GuardTopic(async () => { await topics.RejectGlobalAsync(Uid(http), versionId, req?.Reason); return Results.NoContent(); }));
        a.MapPost("/{id}/deactivate", (string id, HttpContext http, TopicService topics) =>
            GuardTopic(async () => { await topics.DeactivateGlobalAsync(Uid(http), id); return Results.NoContent(); }));
    }
}

public class TopicWriteRequest { public string Name { get; set; } = ""; public string? Description { get; set; } }
public class TopicArchiveRequest { public bool Archived { get; set; } = true; }
public class TopicModuleRequest { public string ModuleId { get; set; } = ""; }
public class CompetencyModelRequest { public List<LearningObjective>? LearningObjectives { get; set; } public List<Competency>? Competencies { get; set; } }
public class ModuleVersionRequest { public string? Note { get; set; } }
public class ModuleAdoptRequest { public int? Version { get; set; } }
public class TopicOrderRequest { public List<string>? ModuleIds { get; set; } }
public class TopicUserRequest { public string UserId { get; set; } = ""; }
public class TopicAssignRequest { public string TopicId { get; set; } = ""; public string UserId { get; set; } = ""; public int? Version { get; set; } public bool? Mandatory { get; set; } public DateTime? DueAt { get; set; } }
public class TopicPublishRequest { public bool SubmitGlobal { get; set; } }
public class TopicSeenRequest { public int Version { get; set; } }
public class TopicExamRequest { public string ModuleId { get; set; } = ""; public int? Version { get; set; } }
public class TopicRejectRequest { public string? Reason { get; set; } }
public class TopicQuestionRequest
{
    public string Type { get; set; } = QuizTypes.SingleChoice;
    public string Question { get; set; } = "";
    public List<string>? Options { get; set; }
    public List<int>? CorrectIndexes { get; set; }
    public string? Explanation { get; set; }
    public bool? Active { get; set; }
    public QuizQuestionInput ToInput() => new() { Type = Type, Question = Question, Options = Options ?? new(), CorrectIndexes = CorrectIndexes ?? new(), Explanation = Explanation ?? "" };
}
