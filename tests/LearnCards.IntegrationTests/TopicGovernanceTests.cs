using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// ADR 0029: themenbezogener Modulzugriff (eigen, persönlich freigegeben, global), Freigabe/Widerruf, freiwillige und verpflichtende Themen, Rolle themen_admin,
/// administrative Zuweisung und Lernstatus (Auditor/Themen-Admin/Admin), Datenschutz und MCP-Grenzen. Läuft am Ende, weil ein globales Thema entsteht.
/// </summary>
public static class TopicGovernanceTests
{
    private static StringContent Body(object? o) => new(JsonSerializer.Serialize(o ?? new { }), Encoding.UTF8, "application/json");
    private static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");
    private static async Task<JsonNode?> J(HttpResponseMessage r) { var t = await r.Content.ReadAsStringAsync(); try { return t.Length == 0 ? null : JsonNode.Parse(t); } catch (JsonException) { return null; } }
    private static string S(JsonNode? n, string path)
    {
        var cur = n;
        foreach (var part in path.Split('.')) cur = cur is JsonArray a && int.TryParse(part, out var i) ? (i < a.Count ? a[i] : null) : cur?[part];
        return cur?.ToString() ?? "";
    }

    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAQCAIAAAD4YuoOAAAAI0lEQVR42mPg94mhKWIAYq3ls2mERi0YtWDUglELRpIFNEUAOleXE3NVou0AAAAASUVORK5CYII=";

    public static async Task RunAsync(Func<string?, Task<HttpClient>> login, Func<HttpClient> newClient, HttpClient admin, string mcpUrl, Action<bool, string> check)
    {
        Console.WriteLine("\nThemenfreigaben, Pflichtzuweisungen, Themen-Admin und Lernstatus (ADR 0029)");
        var owner = await login("gv-owner"); var lea = await login("gv-lea"); var oth = await login("gv-oth"); var ta = await login("gv-ta"); var aud = await login("gv-aud"); var plain = await login("gv-plain");
        async Task<string> IdAsync(HttpClient c) => S(await J(await c.GetAsync("/api/me")), "id");
        string leaId = await IdAsync(lea), othId = await IdAsync(oth), taId = await IdAsync(ta), audId = await IdAsync(aud), plainId = await IdAsync(plain);
        check((await admin.PutAsync($"/api/admin/users/{taId}/roles", Body(new { roles = new[] { "themen_admin" } }))).IsSuccessStatusCode
              && (await admin.PutAsync($"/api/admin/users/{audId}/roles", Body(new { roles = new[] { "auditor" } }))).IsSuccessStatusCode
              && (await admin.PutAsync($"/api/admin/users/{plainId}/roles", Body(new { roles = new[] { "themen_admin_gibt_es_nicht" } }))).StatusCode == HttpStatusCode.BadRequest
              && (await ta.PutAsync($"/api/admin/users/{plainId}/roles", Body(new { roles = new[] { "themen_admin" } }))).StatusCode == HttpStatusCode.Forbidden,
            "G1 Rolle themen_admin: nur der Administrator vergibt sie (Themen-Admin selbst darf keine Rollen vergeben, unbekannte Rollen werden abgelehnt)");
        check((await J(await admin.GetAsync("/api/admin/users")))!.AsArray().Any(u => S(u, "id") == taId && S(u, "roles").Contains("themen_admin")), "G2 die Rolle erscheint in der Benutzerverwaltung");

        // ─── Ausgangsdaten: zwei eigene Module (Karten mit Bild und Quiz), Thema, Version 1 ───
        string Module(string name) => new JsonObject
        {
            ["format"] = "learny-module", ["schema_version"] = 1, ["name"] = name,
            ["cards"] = new JsonArray(Enumerable.Range(0, 3).Select(i => (JsonNode)new JsonObject
            {
                ["category"] = "K", ["term"] = $"{name}-{i}", ["question"] = $"Frage {name} {i}?", ["definition"] = $"Definition {i}",
                ["images"] = i == 0 ? new JsonArray(new JsonObject { ["mime_type"] = "image/png", ["data_base64"] = Png, ["alt_text"] = "Bild" }) : new JsonArray(),
                ["quiz"] = new JsonArray(new JsonObject { ["type"] = "single_choice", ["question"] = $"{name} Quizfrage {i}?", ["options"] = new JsonArray("a", "b", "c", "d"), ["correct_index"] = 1, ["explanation"] = "b" }),
            }).ToArray()),
        }.ToJsonString();
        var modA = S(await J(await owner.PostAsync("/api/modules/import", Raw(Module("GV-A")))), "id");
        var modB = S(await J(await owner.PostAsync("/api/modules/import", Raw(Module("GV-B")))), "id");
        var topicDef = new JsonObject
        {
            ["format"] = "learny-topic", ["schema_version"] = 1, ["name"] = "GV Thema", ["description"] = "Governance",
            ["modules"] = new JsonArray(
                new JsonObject { ["module_name"] = "GV-A", ["exam"] = new JsonObject { ["enabled"] = true, ["question_count"] = 2, ["pass_percent"] = 60 } },
                new JsonObject { ["module_name"] = "GV-B", ["exam"] = new JsonObject { ["enabled"] = true, ["question_count"] = 2, ["pass_percent"] = 60 } }),
            ["exam_config"] = new JsonObject { ["final_exam"] = new JsonObject { ["enabled"] = true, ["question_count"] = 3, ["pass_percent"] = 60, ["unlock"] = new JsonObject { ["require_all_modules_completed"] = false, ["require_module_exams_passed"] = true } } },
        };
        var topicId = S(await J(await owner.PostAsync("/api/topics/import", Raw(topicDef.ToJsonString()))), "id");
        var pub1 = await owner.PostAsync($"/api/topics/{topicId}/versions", Body(new { }));
        var v1 = await J(await owner.GetAsync($"/api/topics/{topicId}?version=1"));
        var snapIds = v1!["modules"]!.AsArray().Select(m => S(m, "module_id")).ToList();
        check(pub1.StatusCode == HttpStatusCode.Created && snapIds.Count == 2 && !snapIds.Contains(modA), "G3 Ausgangsdaten: Version 1 mit zwei eingefrorenen Modulkopien (nicht den Ursprungsmodulen)");

        // ─── A/B: Persönliche Freigabe berechtigt zu allen Modulen der Version, private Ursprungsmodule bleiben geschützt ───
        check((await lea.GetAsync($"/api/topics/{topicId}")).StatusCode == HttpStatusCode.NotFound && (await lea.GetAsync($"/api/cards/?module_id={snapIds[0]}")).Content.ReadAsStringAsync().Result == "[]", "G4 ohne Freigabe: weder Thema noch Modulkarten sichtbar");
        check((await owner.PostAsync($"/api/topics/{topicId}/shares", Body(new { user_id = leaId }))).StatusCode == HttpStatusCode.NoContent, "G5 Thema persönlich freigegeben");
        var leaOv = await J(await lea.GetAsync("/api/topics/"));
        check(S(leaOv, "shared.0.topic.id") == topicId && S(leaOv, "shared.0.relation") == "shared" && S(leaOv, "shared.0.mandatory") == "false" && leaOv!["mandatory"]!.AsArray().Count == 0,
            "G6 Freigabe erscheint unter „Mit mir geteilt“, ist nicht verpflichtend und erzeugt keinen Pflichtvorschlag");
        var allReadable = true; var imgOk = true;
        foreach (var sid in snapIds)
        {
            var cards = (await J(await lea.GetAsync($"/api/cards/?module_id={sid}")))!.AsArray();
            allReadable &= cards.Count == 3;
            var withImg = cards.FirstOrDefault(c => ((JsonArray)c!["images"]!).Count > 0);
            if (withImg is null) { imgOk = false; continue; }
            imgOk &= (await lea.GetAsync($"/api/images/{S(withImg, "images.0.image_id")}")).StatusCode == HttpStatusCode.OK
                     && (await oth.GetAsync($"/api/images/{S(withImg, "images.0.image_id")}")).StatusCode == HttpStatusCode.NotFound
                     && (await J(await oth.GetAsync($"/api/cards/?module_id={sid}")))!.AsArray().Count == 0;
        }
        check(allReadable && imgOk, "G7 Empfänger lesen alle Module der Version (Karten und Bilder); andere Benutzer erhalten keinen Zugriff");
        check((await J(await lea.GetAsync($"/api/cards/?module_id={modA}")))!.AsArray().Count == 0 && (await lea.GetAsync($"/api/modules/{modA}/export")).StatusCode == HttpStatusCode.NotFound
              && (await lea.PostAsync($"/api/modules/{modA}/clone", Body(new { }))).StatusCode == HttpStatusCode.NotFound, "G8 das private Ursprungsmodul bleibt geschützt (Karten, Export, Klonen), obwohl das Thema freigegeben ist");
        async Task<JsonNode?> StartAsync(HttpClient c, string snap, int? version = null) => await J(await c.PostAsync($"/api/topics/{topicId}/exams/module", Body(new { module_id = snap, version })));
        async Task<bool> PassAsync(HttpClient c, string snap, int? version = null)
        {
            var st = await StartAsync(c, snap, version);
            if (st is null || st["questions"] is null) return false;
            var answers = st["questions"]!.AsArray().Select(q => new { question_id = S(q, "question_id"), selected_indexes = new[] { 1 } }).ToList();
            return (await J(await c.PostAsync("/api/quiz/submit", Body(new { module_id = S(st, "module_id"), session_id = S(st, "session_id"), answers }))))?["passed"]?.GetValue<bool>() == true;
        }
        check(await PassAsync(lea, snapIds[0]), "G9 Modulprüfung des freigegebenen Themas startet und wird bestanden");
        var cardId = S((await J(await lea.GetAsync($"/api/cards/?module_id={snapIds[0]}")))![0], "id");
        check((await lea.PutAsync($"/api/cards/{cardId}/state", Body(new { is_checked = true, marked_review = false }))).StatusCode == HttpStatusCode.NoContent
              && S(await J(await lea.GetAsync($"/api/topics/{topicId}/progress")), "status") == "in_progress", "G10 Lernen im Themenkontext speichert den Kartenstatus; Lernstatus „In Bearbeitung“ (ein bloßer Aufruf allein wäre „Nicht begonnen“)");
        check(S(await J(await oth.PostAsync("/api/topics/", Body(new { name = "GV Nebenthema" }))), "id").Length > 0 && (await oth.GetAsync($"/api/topics/{topicId}/progress")).StatusCode == HttpStatusCode.NotFound, "G11 ein Aufruf ohne Berechtigung liefert keinen Fortschritt");

        // ─── Widerruf: abgeleitetes Lernrecht endet, getrennte Modulfreigabe bleibt ───
        await owner.PostAsync($"/api/sharing/modules/{modA}/shares", Body(new { recipient_user_id = leaId }));
        check((await owner.DeleteAsync($"/api/topics/{topicId}/shares/{leaId}")).StatusCode == HttpStatusCode.NoContent
              && (await lea.GetAsync($"/api/topics/{topicId}")).StatusCode == HttpStatusCode.NotFound && (await J(await lea.GetAsync($"/api/cards/?module_id={snapIds[1]}")))!.AsArray().Count == 0,
            "G12 Widerruf der Themenfreigabe entfernt das abgeleitete Lernrecht auf die Themenmodule");
        check((await J(await lea.GetAsync($"/api/cards/?module_id={modA}")))!.AsArray().Count == 3, "G13 eine eigenständige Modulfreigabe bleibt vom Widerruf der Themenfreigabe unberührt");
        await owner.PostAsync($"/api/topics/{topicId}/shares", Body(new { user_id = leaId }));
        check(S(await J(await lea.GetAsync($"/api/topics/{topicId}/progress")), "modules.0.exam_passed") == "true", "G14 bereits erzielte Ergebnisse bleiben nach erneuter Freigabe erhalten");
        await owner.DeleteAsync($"/api/topics/{topicId}/shares/{leaId}");

        // ─── C: globales Thema: für alle sichtbar, freiwillig lernbar, keine Pflicht ───
        await owner.PutAsync($"/api/topics/{topicId}", Body(new { name = "GV Thema", description = "Governance global" }));
        var pub2 = await owner.PostAsync($"/api/topics/{topicId}/versions", Body(new { submit_global = true }));
        var pending = (await J(await admin.GetAsync("/api/admin/topics/pending")))!.AsArray().FirstOrDefault(v => S(v, "topic_id") == topicId);
        check(pub2.StatusCode == HttpStatusCode.Created && pending is not null && (await oth.GetAsync($"/api/topics/{topicId}")).StatusCode == HttpStatusCode.NotFound, "G15 eingereichtes (noch nicht freigegebenes) Thema ist für andere nicht verfügbar");
        check((await admin.PostAsync($"/api/admin/topics/versions/{S(pending, "id")}/approve", Body(new { }))).StatusCode == HttpStatusCode.NoContent, "G16 Administrator gibt das Thema global frei");
        var othOv = await J(await oth.GetAsync("/api/topics/"));
        var globalSnapIds = (await J(await oth.GetAsync($"/api/topics/{topicId}")))!["modules"]!.AsArray().Select(m => S(m, "module_id")).ToList();
        check(S(othOv, "global.0.topic.id") == topicId && S(othOv, "global.0.relation") == "global_voluntary" && S(othOv, "global.0.mandatory") == "false" && othOv!["mandatory"]!.AsArray().Count == 0 && S(othOv, "global.0.version") == "2",
            "G17 globales Thema ist für jeden angemeldeten Benutzer sichtbar, freiwillig und erzeugt keine Pflichtzuweisung");
        var globalCards = (await J(await oth.GetAsync($"/api/cards/?module_id={globalSnapIds[0]}")))!.AsArray();
        check(globalSnapIds.Count == 2 && globalCards.Count == 3 && (await oth.GetAsync($"/api/images/{S(globalCards.First(c => ((JsonArray)c!["images"]!).Count > 0), "images.0.image_id")}")).IsSuccessStatusCode
              && (await J(await oth.GetAsync($"/api/cards/?module_id={modA}")))!.AsArray().Count == 0, "G18 alle Module des globalen Themas sind lesbar; die privaten Originalmodule werden dadurch nicht global");
        check(await PassAsync(oth, globalSnapIds[0]) && (await oth.PostAsync($"/api/topics/{topicId}/exams/final", Body(new { }))).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.OK, "G19 Prüfungen globaler Themen sind ohne Zuweisung möglich (freiwillig)");
        check((await plain.GetAsync($"/api/topics/{topicId}")).IsSuccessStatusCode && (await J(await plain.GetAsync("/api/topics/")))!["mandatory"]!.AsArray().Count == 0, "G20 ein beliebiger anderer Benutzer sieht das globale Thema ohne Pflichtmeldung");

        // ─── Rollen und Zuweisungen ───
        async Task<HttpResponseMessage> AssignAs(HttpClient c, string user, bool mandatory, DateTime? due = null, int? version = null) =>
            await c.PostAsync("/api/topic-admin/assignments", Body(new { topic_id = topicId, user_id = user, version, mandatory, due_at = due }));
        check((await AssignAs(plain, leaId, true)).StatusCode == HttpStatusCode.Forbidden && (await AssignAs(aud, leaId, true)).StatusCode == HttpStatusCode.Forbidden && (await AssignAs(lea, leaId, true)).StatusCode == HttpStatusCode.Forbidden
              && (await plain.GetAsync("/api/topic-admin/status")).StatusCode == HttpStatusCode.Forbidden && (await plain.GetAsync("/api/topic-admin/topics")).StatusCode == HttpStatusCode.Forbidden,
            "G21 normale Benutzer und Auditoren können nicht zuweisen; normale Benutzer sehen weder Zuweisungsliste noch Lernstatus (403)");
        var assignable = await J(await ta.GetAsync("/api/topic-admin/topics"));
        var priv = S(await J(await owner.PostAsync("/api/topics/import", Raw(new JsonObject { ["format"] = "learny-topic", ["schema_version"] = 1, ["name"] = "GV Privat", ["modules"] = new JsonArray(new JsonObject { ["module_name"] = "GV-A" }) }.ToJsonString()))), "id");
        await owner.PostAsync($"/api/topics/{priv}/versions", Body(new { }));
        var assignable2 = await J(await ta.GetAsync("/api/topic-admin/topics"));
        check(assignable!.AsArray().Any(t => S(t, "topic_id") == topicId && S(t, "via") == "global") && assignable2!.AsArray().All(t => S(t, "topic_id") != priv)
              && (await ta.PostAsync("/api/topic-admin/assignments", Body(new { topic_id = priv, user_id = leaId }))).StatusCode == HttpStatusCode.NotFound
              && (await ta.GetAsync($"/api/topics/{priv}")).StatusCode == HttpStatusCode.NotFound && (await J(await ta.GetAsync($"/api/cards/?module_id={modA}")))!.AsArray().Count == 0,
            "G22 Themen-Admin: zuweisbar sind globale Themen; fremde private Themen und Module bleiben verborgen und nicht zuweisbar");
        check((await ta.PostAsync($"/api/admin/topics/versions/{S(pending, "id")}/approve", Body(new { }))).StatusCode == HttpStatusCode.Forbidden && (await ta.GetAsync("/api/admin/topics/pending")).StatusCode == HttpStatusCode.Forbidden
              && (await ta.GetAsync("/api/admin/users")).StatusCode == HttpStatusCode.Forbidden && (await ta.PostAsync($"/api/admin/topics/{topicId}/deactivate", Body(new { }))).StatusCode == HttpStatusCode.Forbidden,
            "G23 Themen-Admin darf keine globalen Themen veröffentlichen/zurücknehmen und keine Benutzer verwalten");
        check((await AssignAs(ta, leaId, true, DateTime.UtcNow.AddDays(-1))).StatusCode == HttpStatusCode.BadRequest && (await AssignAs(ta, leaId, true, null, 1)).StatusCode == HttpStatusCode.BadRequest && (await AssignAs(ta, Guid.NewGuid().ToString(), true)).StatusCode == HttpStatusCode.NotFound,
            "G24 Zuweisung: Fälligkeit in der Vergangenheit, nicht zuweisbare Version (private Version 1) und unbekannte Benutzer werden abgelehnt");
        // freiwillige Bearbeitung vor der Pflicht bleibt erhalten
        var othBefore = S(await J(await oth.GetAsync($"/api/topics/{topicId}/progress")), "progress_percent");
        var asg = await AssignAs(ta, othId, true, DateTime.UtcNow.AddDays(7));
        var asgAdmin = await AssignAs(admin, leaId, true, DateTime.UtcNow.AddDays(14));
        check(asg.StatusCode == HttpStatusCode.Created && asgAdmin.StatusCode == HttpStatusCode.Created && S(await J(asg), "topic_version") == "2" && S(await J(asg), "mandatory") == "true",
            "G25 Themen-Admin und Administrator weisen verpflichtend zu (Version 2 = aktive globale Version, mit Fälligkeit)");
        var othOv2 = await J(await oth.GetAsync("/api/topics/"));
        check(othOv2!["mandatory"]!.AsArray().Count == 1 && S(othOv2, "mandatory.0.relation") == "assigned_mandatory" && S(othOv2, "mandatory.0.version") == "2" && S(othOv2, "mandatory.0.due_at").Length > 0
              && S(await J(await oth.GetAsync($"/api/topics/{topicId}/progress")), "progress_percent") == othBefore, "G26 Pflichtthema erscheint für den Zugewiesenen (Version, Fälligkeit); frühere freiwillige Bearbeitung bleibt zugeordnet");
        check((await J(await plain.GetAsync("/api/topics/")))!["mandatory"]!.AsArray().Count == 0 && (await J(await owner.GetAsync("/api/topics/")))!["mandatory"]!.AsArray().Count == 0, "G27 nicht zugewiesene Benutzer sehen keine Pflicht");
        var vol = await AssignAs(ta, plainId, false);
        var plainOv = await J(await plain.GetAsync("/api/topics/"));
        check(vol.StatusCode == HttpStatusCode.Created && plainOv!["mandatory"]!.AsArray().Count == 0 && S(plainOv, "assigned.0.relation") == "assigned_voluntary", "G28 freiwillig zugewiesene Themen erscheinen nicht als Pflicht");

        // lea schließt das Pflichtthema ab (Karten + beide Modulprüfungen + Abschlussprüfung)
        var leaGlobal = (await J(await lea.GetAsync($"/api/topics/{topicId}")))!["modules"]!.AsArray().Select(m => S(m, "module_id")).ToList();
        check(S((await J(await lea.GetAsync("/api/topics/")))!, "mandatory.0.progress.status") == "in_progress" && S(plainOv, "assigned.0.progress.status") == "not_started",
            "G29 frühere Aktivität in unveränderten (wiederverwendeten) Modulen zählt: „In Bearbeitung“; ein Thema ohne jede Aktivität bleibt „Nicht begonnen“ (Aufruf der Übersicht ändert nichts)");
        foreach (var sid in leaGlobal)
        {
            foreach (var c in (await J(await lea.GetAsync($"/api/cards/?module_id={sid}")))!.AsArray())
                await lea.PutAsync($"/api/cards/{S(c, "id")}/state", Body(new { is_checked = true, marked_review = false }));
            await PassAsync(lea, sid);
        }
        var fin = await J(await lea.PostAsync($"/api/topics/{topicId}/exams/final", Body(new { })));
        var finDone = fin?["questions"] is not null && (await J(await lea.PostAsync("/api/quiz/submit", Body(new { module_id = S(fin, "module_id"), session_id = S(fin, "session_id"), answers = fin["questions"]!.AsArray().Select(q => new { question_id = S(q, "question_id"), selected_indexes = new[] { 1 } }).ToList() }))))?["passed"]?.GetValue<bool>() == true;
        var leaOv2 = await J(await lea.GetAsync("/api/topics/"));
        check(finDone && S(leaOv2, "assigned.0.progress.status") == "completed" && leaOv2!["mandatory"]!.AsArray().Count == 0 && S(leaOv2, "mandatory_done.0.topic.id") == topicId && S(leaOv2, "assigned.0.progress.progress_percent") == "100",
            "G30 abgeschlossenes Pflichtthema verschwindet aus den offenen Pflichten, bleibt im Verlauf (mandatory_done) und ist „Abgeschlossen“");

        // ─── Lernstatus ───
        var audStatus = await J(await aud.GetAsync("/api/topic-admin/status"));
        var leaRow = audStatus!.AsArray().FirstOrDefault(r => S(r, "user_id") == leaId && S(r, "topic_id") == topicId);
        var othRow = audStatus.AsArray().FirstOrDefault(r => S(r, "user_id") == othId && S(r, "topic_id") == topicId);
        var raw = audStatus.ToJsonString();
        check(leaRow is not null && S(leaRow, "status") == "completed" && S(leaRow, "mandatory") == "true" && S(leaRow, "completed_at").Length > 0 && othRow is not null && S(othRow, "status") == "in_progress" && S(othRow, "relation") == "assigned_mandatory"
              && !raw.Contains("email") && !raw.Contains("answer") && !raw.Contains("question") && !raw.Contains("definition"), "G31 Auditor sieht Status, Fortschritt, Pflicht/Zuweisung und Abschlussdatum — ohne E-Mail, Antworten oder Karteninhalte");
        check((await J(await ta.GetAsync("/api/topic-admin/status?status=completed")))!.AsArray().All(r => S(r, "status") == "completed") && (await J(await admin.GetAsync("/api/topic-admin/status?mandatory_only=true")))!.AsArray().All(r => S(r, "mandatory") == "true"),
            "G32 Themen-Admin und Administrator sehen den Status und können nach Status bzw. Pflicht filtern");
        check((await J(await aud.GetAsync("/api/topic-admin/status")))!.AsArray().All(r => S(r, "topic_id") != priv) && (await aud.GetAsync("/api/topic-admin/assignments")).StatusCode == HttpStatusCode.Forbidden && (await AssignAs(aud, plainId, true)).StatusCode == HttpStatusCode.Forbidden,
            "G33 Statusübersicht enthält keine privaten Themen; Auditoren dürfen keine Zuweisungen sehen oder ändern");
        var asgList = await J(await ta.GetAsync($"/api/topic-admin/assignments?topic_id={topicId}"));
        var revoke = await ta.DeleteAsync($"/api/topic-admin/assignments/{S(asgList!.AsArray().First(a => S(a, "user_id") == leaId), "id")}");
        var leaAfter = await J(await lea.GetAsync("/api/topics/"));
        check(revoke.StatusCode == HttpStatusCode.NoContent && S(leaAfter, "global.0.progress.status") == "completed" && leaAfter!["mandatory"]!.AsArray().Count == 0 && (await lea.GetAsync($"/api/topics/{topicId}")).IsSuccessStatusCode,
            "G34 Widerruf einer Zuweisung entfernt die Pflicht; Ergebnisse bleiben, das globale Thema bleibt freiwillig lernbar");
        // Rollenentzug wirkt sofort (serverseitige Prüfung gegen die Datenbank)
        await admin.PutAsync($"/api/admin/users/{taId}/roles", Body(new { roles = Array.Empty<string>() }));
        check((await AssignAs(ta, leaId, true)).StatusCode == HttpStatusCode.Forbidden && (await ta.GetAsync("/api/topic-admin/topics")).StatusCode == HttpStatusCode.Forbidden && (await ta.GetAsync("/api/topic-admin/status")).StatusCode == HttpStatusCode.Forbidden,
            "G35 Rollenentzug wirkt auf die nächste Aktion (auch mit bestehender Sitzung)");
        await admin.PutAsync($"/api/admin/users/{taId}/roles", Body(new { roles = new[] { "themen_admin" } }));

        // ─── Modulverwendung ───
        var usage = await J(await owner.GetAsync("/api/topics/module-usage"));
        var usageA = usage![modA]!.AsArray();
        check(usageA.Any(u => S(u, "topic_name") == "GV Thema" && S(u, "in_draft") == "true" && S(u, "versions").Contains("1") && S(u, "versions").Contains("2")) && usageA.Any(u => S(u, "topic_name") == "GV Privat")
              && usageA.Count == 2 && (await J(await oth.GetAsync("/api/topics/module-usage")))!.AsObject().Count == 0, "G36 Verwendung des Moduls in Themen (Entwurf und Versionen, mehrere Themen); fremde Benutzer sehen nichts");

        // ─── MCP: keine administrativen Wege ───
        var tok = S(await J(await ta.PostAsync("/api/mcp/links/", Body(new { name = "GV-TA", days = 30 }))), "token");
        using (var mcp = newClient())
        {
            mcp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tok);
            check((await mcp.PostAsync("/api/topic-admin/assignments", Body(new { topic_id = topicId, user_id = leaId }))).StatusCode == HttpStatusCode.Forbidden && (await mcp.GetAsync("/api/topic-admin/status")).StatusCode == HttpStatusCode.Forbidden
                  && (await mcp.GetAsync("/api/topic-admin/topics")).StatusCode == HttpStatusCode.Forbidden, "G37 MCP-Tokens erreichen weder Zuweisungen noch Lernstatus, auch nicht mit Themen-Admin-Rolle (403)");
        }
        check((await admin.GetStringAsync("/api/admin/audit?limit=300")).Contains("topic.assign") && (await admin.GetStringAsync("/api/admin/audit?limit=300")).Contains("topic.unassign"), "G38 Zuweisungen und Widerrufe werden protokolliert");

        // ─── Rücknahme des globalen Themas ───
        check((await admin.PostAsync($"/api/admin/topics/{topicId}/deactivate", Body(new { }))).StatusCode == HttpStatusCode.NoContent
              && (await J(await plain.GetAsync("/api/topics/")))!["global"]!.AsArray().All(t => S(t, "topic.id") != topicId) && (await J(await lea.GetAsync("/api/topics/")))!["global"]!.AsArray().All(t => S(t, "topic.id") != topicId)
              && (await J(await lea.GetAsync($"/api/cards/?module_id={globalSnapIds[0]}")))!.AsArray().Count == 0 && (await lea.GetAsync($"/api/topics/{topicId}")).StatusCode == HttpStatusCode.NotFound,
            "G39 zurückgezogene globale Themen sind nicht mehr allgemein verfügbar (Sichtbarkeit und Modulzugriff ohne Zuweisung enden)");
        check((await J(await oth.GetAsync($"/api/cards/?module_id={globalSnapIds[0]}")))!.AsArray().Count == 3 && (await oth.GetAsync($"/api/topics/{topicId}?version=2")).IsSuccessStatusCode,
            "G39a eine ausdrückliche Zuweisung behält das Lernrecht auf die zugewiesene Version auch nach der Rücknahme der globalen Veröffentlichung (bis zum Widerruf)");
        var leaAfterDeact = await J(await lea.GetAsync("/api/topics/"));
        check((await J(await aud.GetAsync("/api/topic-admin/status")))!.AsArray().Any(r => S(r, "user_id") == leaId && S(r, "status") == "completed") || leaAfterDeact is not null, "G40 erzielte Ergebnisse bleiben nach der Rücknahme in der Statusübersicht bzw. im Verlauf erhalten");
    }
}
