using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// ADR 0028: Themen nur aus eigenen Modulen, Klonen globaler Module, vollständiger Modul-JSON-Export/-Import, portables Themen-JSON,
/// Entwurfsaktualisierung ohne Veröffentlichung und die schreibenden MCP-Werkzeuge für eigene Themenentwürfe.
/// </summary>
public static class TopicTransferTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAQCAIAAAD4YuoOAAAAI0lEQVR42mPg94mhKWIAYq3ls2mERi0YtWDUglELRpIFNEUAOleXE3NVou0AAAAASUVORK5CYII=";

    private static StringContent Body(object? o) => new(JsonSerializer.Serialize(o ?? new { }), Encoding.UTF8, "application/json");
    private static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");
    private static async Task<JsonNode?> J(HttpResponseMessage r) { var t = await r.Content.ReadAsStringAsync(); try { return t.Length == 0 ? null : JsonNode.Parse(t); } catch (JsonException) { return null; } }
    private static string S(JsonNode? n, string path)
    {
        var cur = n;
        foreach (var part in path.Split('.')) cur = cur is JsonArray a && int.TryParse(part, out var i) ? (i < a.Count ? a[i] : null) : cur?[part];
        return cur?.ToString() ?? "";
    }
    private static int I(JsonNode? n, string path) => int.TryParse(S(n, path), out var v) ? v : -1;

    public static async Task RunAsync(Func<string?, Task<HttpClient>> login, Func<HttpClient> newClient, HttpClient admin, string mcpUrl, Action<bool, string> check)
    {
        Console.WriteLine("\nThemenmodule, Modul-/Themen-JSON, Klonen und MCP-Themenverwaltung (ADR 0028)");
        var tina = await login("tina28"); var leo = await login("leo28"); var sam = await login("sam28");

        async Task<JsonArray> ModulesAsync(HttpClient c, string scope = "") => (await J(await c.GetAsync("/api/modules/" + (scope.Length > 0 ? "?scope=" + scope : ""))))!.AsArray();
        async Task<string> ModuleIdAsync(HttpClient c, string name, string scope = "") => S((await ModulesAsync(c, scope)).FirstOrDefault(m => S(m, "name") == name), "id");

        // ─── Ausgangsdaten: eigene Module von tina, davon eines mit Bild, Quiz und Pool-Frage; ein globales Modul ───
        var seed = new List<object>();
        for (var i = 0; i < 4; i++)
            seed.Add(new { module = "TT-Basis", category = "K", term = $"Basis-{i}", question = $"Frage {i}?", definition = $"Definition {i} mit **Markup**",
                quiz = new[] { new { type = "single_choice", question = $"Basis Quizfrage {i}?", options = new[] { "A", "B", "C", "D" }, correct_index = 1, explanation = "B" } } });
        seed.Add(new { module = "TT-Bild", category = "K", term = "Bildkarte", question = "Was zeigt das Bild?", definition = "Ein Testbild",
            images = new[] { new { mime_type = "image/png", data_base64 = Png, alt_text = "Testbild", caption = "Bildunterschrift" } },
            quiz = new[] { new { type = "single_choice", question = "Bildfrage?", options = new[] { "1", "2", "3", "4" }, correct_index = 0, explanation = "1" } } });
        for (var i = 0; i < 3; i++)
            seed.Add(new { module = "TT-Bild", category = "K", term = $"Bild-{i}", question = $"Zweite Frage {i}?", definition = $"Text {i}" });
        seed.Add(new { module = "TT-Glob", category = "K", term = "Global-1", question = "Globale Frage 1?", definition = "Globale Definition",
            images = new[] { new { mime_type = "image/png", data_base64 = Png, alt_text = "Globales Bild" } },
            quiz = new[] { new { type = "single_choice", question = "Globale Quizfrage?", options = new[] { "a", "b", "c", "d" }, correct_index = 2, explanation = "c" } } });
        seed.Add(new { module = "TT-Glob", category = "K", term = "Global-2", question = "Globale Frage 2?", definition = "Noch eine" });
        check((await tina.PostAsync("/api/import/cards", Body(new { cards = seed }))).IsSuccessStatusCode, "T81 Ausgangsdaten (Module mit Bild und Quiz) importiert");
        var basis = await ModuleIdAsync(tina, "TT-Basis"); var bild = await ModuleIdAsync(tina, "TT-Bild"); var glob = await ModuleIdAsync(tina, "TT-Glob");
        // Editor-Frage im Pool + Quizkonfiguration für TT-Bild
        await tina.PostAsync($"/api/modules/{bild}/quiz-questions", Body(new { type = "single_choice", question = "Poolfrage?", options = new[] { "w", "x", "y", "z" }, correct_indexes = new[] { 2 }, explanation = "y" }));
        await tina.PutAsync($"/api/modules/{bild}/quiz-config", Body(new { enabled = true, allow_multiple_choice = true, single_choice = true, multiple_response = true, free_text = false, origin = "fixed", strategy = "none", scope = "module", question_count = 3, pass_percent = 70, allow_retry = true, shuffle_questions = true, shuffle_answers = false, show_correct_answers = true }));

        var sub = await tina.PostAsync($"/api/sharing/modules/{glob}/submissions", null);
        var subId = S(await J(sub), "id");
        check(sub.StatusCode == HttpStatusCode.Created && (await admin.PostAsync($"/api/admin/submissions/{subId}/approve", null)).StatusCode == HttpStatusCode.NoContent, "T82 globales Modul veröffentlicht (Einreichung + Administratorfreigabe)");
        var globalId = await ModuleIdAsync(leo, "TT-Glob", "global");
        check(globalId.Length > 0 && globalId != glob, "T83 das globale Modul ist für andere Benutzer sichtbar (eigene Identität)");

        // ─── B: Themen nur aus eigenen Modulen ───
        var leoTopic = S(await J(await leo.PostAsync("/api/topics/", Body(new { name = "TT Leo Thema" }))), "id");
        var addGlobal = await leo.PostAsync($"/api/topics/{leoTopic}/modules", Body(new { module_id = globalId }));
        check(addGlobal.StatusCode == HttpStatusCode.BadRequest && (await addGlobal.Content.ReadAsStringAsync()).Contains("klone"), "T84 ein globales Modul kann einem Thema nicht direkt zugeordnet werden (Hinweis auf das Klonen)");
        check((await leo.PostAsync($"/api/topics/{leoTopic}/modules", Body(new { module_id = basis }))).StatusCode == HttpStatusCode.NotFound
              && (await leo.PostAsync($"/api/topics/{leoTopic}/modules", Body(new { module_id = Guid.NewGuid().ToString() }))).StatusCode == HttpStatusCode.NotFound
              && (await leo.PostAsync($"/api/topics/{leoTopic}/modules", Body(new { module_id = "kein-guid" }))).StatusCode == HttpStatusCode.NotFound, "T85 fremde private, erfundene und ungültige Modul-IDs werden bei manipulierten Anfragen abgelehnt");
        await tina.PostAsync($"/api/sharing/modules/{basis}/shares", Body(new { recipient_user_id = await UserIdAsync(tina, "leo28") }));
        check((await leo.PostAsync($"/api/topics/{leoTopic}/modules", Body(new { module_id = basis }))).StatusCode == HttpStatusCode.BadRequest, "T86 ein mit dem Benutzer geteiltes fremdes Modul wird abgelehnt");
        check((await J(await leo.GetAsync($"/api/topics/{leoTopic}/addable-modules")))!.AsArray().All(m => S(m, "scope") == "private"), "T87 die Modulauswahl bietet ausschließlich eigene private Module an");

        // ─── C: Klonen ───
        var beforeGlobal = await J(await leo.GetAsync($"/api/cards/?module_id={globalId}"));
        var cloneRes = await leo.PostAsync($"/api/modules/{globalId}/clone", Body(new { }));
        var clone = await J(cloneRes);
        var cloneId = S(clone, "id");
        check(cloneRes.StatusCode == HttpStatusCode.Created && S(clone, "name") == "TT-Glob" && cloneId != globalId && I(clone, "card_count") == 2, "T88 globales Modul klonen → 201, Name gleicher Name (kein Konflikt), neue ID, 2 Karten");
        var cloneCards = (await J(await leo.GetAsync($"/api/cards/?module_id={cloneId}")))!.AsArray();
        var cloneCard = cloneCards.First(c => S(c, "term") == "Global-1");
        var cloneFull = await J(await leo.GetAsync($"/api/cards/{S(cloneCard, "id")}?include_image_data=true"));
        check(cloneCards.Count == 2 && S(cloneFull, "images.0.data_base64") == Png && S(cloneFull, "images.0.alt_text") == "Globales Bild" && S(cloneFull, "quiz.0.question") == "Globale Quizfrage?"
              && S(cloneCard, "id") != S(beforeGlobal!.AsArray().First(c => S(c, "term") == "Global-1"), "id"), "T89 Klon enthält Karten, Bild (identische Bytes), Karten-Quiz und neue Karten-IDs");
        check((await ModulesAsync(leo)).Any(m => S(m, "id") == cloneId && S(m, "context") == "private") && (await ModulesAsync(tina)).All(m => S(m, "id") != cloneId), "T90 der Klon ist ein privates Modul des klonenden Benutzers (andere sehen es nicht)");
        var afterGlobal = await J(await leo.GetAsync($"/api/cards/?module_id={globalId}"));
        check(afterGlobal!.ToJsonString() == beforeGlobal!.ToJsonString() && (await ModulesAsync(leo, "global")).Count(m => S(m, "name") == "TT-Glob") == 1, "T91 das globale Original bleibt unverändert");
        var clone2 = await J(await leo.PostAsync($"/api/modules/{globalId}/clone", Body(new { })));
        check(S(clone2, "name") == "TT-Glob (Kopie)" && S(await J(await leo.PostAsync($"/api/modules/{globalId}/clone", Body(new { }))), "name") == "TT-Glob (Kopie 2)", "T92 bei Namensgleichheit entstehen eindeutige Namen „(Kopie)“, „(Kopie 2)“");
        check((await tina.PostAsync($"/api/modules/{basis}/clone", Body(new { }))).StatusCode == HttpStatusCode.NotFound
              && (await leo.PostAsync($"/api/modules/{basis}/clone", Body(new { }))).StatusCode == HttpStatusCode.NotFound
              && (await leo.PostAsync($"/api/modules/{cloneId}/clone", Body(new { }))).StatusCode == HttpStatusCode.NotFound, "T93 nur aktive globale Module sind Klonquellen (fremde/eigene private und geteilte Module nicht)");
        check((await leo.PostAsync($"/api/topics/{leoTopic}/modules", Body(new { module_id = cloneId }))).StatusCode == HttpStatusCode.NoContent
              && (await leo.GetAsync($"/api/topics/{leoTopic}/addable-modules")).IsSuccessStatusCode, "T94 der Klon ist sofort im Thema verwendbar");

        // ─── D: Modul-JSON ───
        var exportRes = await tina.GetAsync($"/api/modules/{bild}/export");
        var exportText = await exportRes.Content.ReadAsStringAsync();
        var exported = JsonNode.Parse(exportText)!;
        check(exportRes.IsSuccessStatusCode && S(exported, "format") == "learny-module" && I(exported, "schema_version") == 1 && exported["cards"]!.AsArray().Count == 4
              && S(exported["cards"]!.AsArray().First(c => S(c, "term") == "Bildkarte"), "images.0.data_base64").Length > 20 && !exportText.Contains("storage_key") && !exportText.Contains("\"image_id\"") && !exportText.Contains("\"provider\"")
              && exported["pool_questions"]!.AsArray().Count == 1 && I(exported, "quiz_config.question_count") == 3, "T95 Modul-Export: Format/Version, Karten, Bild als Base64, Quizkonfiguration, Pool-Frage; keine internen Bildfelder");
        check((await sam.GetAsync($"/api/modules/{bild}/export")).StatusCode == HttpStatusCode.NotFound && (await leo.GetAsync($"/api/modules/{bild}/export")).StatusCode == HttpStatusCode.NotFound, "T96 fremde private Module sind nicht exportierbar");
        var imp = await sam.PostAsync("/api/modules/import", Raw(exportText));
        var impId = S(await J(imp), "id");
        var impCards = (await J(await sam.GetAsync($"/api/cards/?module_id={impId}")))!.AsArray();
        var impImgCard = impCards.First(c => S(c, "term") == "Bildkarte");
        var impFull = await J(await sam.GetAsync($"/api/cards/{S(impImgCard, "id")}?include_image_data=true"));
        var impPool = (await J(await sam.GetAsync($"/api/modules/{impId}/quiz-questions")))!.AsArray();
        var impCfg = await J(await sam.GetAsync($"/api/modules/{impId}/quiz-config"));
        check(imp.StatusCode == HttpStatusCode.Created && S(await J(imp), "name") == "TT-Bild" && impCards.Count == 4 && S(impFull, "images.0.data_base64") == Png && S(impFull, "images.0.caption") == "Bildunterschrift"
              && S(impFull, "quiz.0.question") == "Bildfrage?" && impPool.Any(q => S(q, "question") == "Poolfrage?") && impPool.Count(q => S(q, "question") == "Bildfrage?") == 1
              && (S(impCfg, "config.question_count") == "3" || S(impCfg, "question_count") == "3"), "T97 Import in einem anderen Benutzerkonto: Karten, Bild, Karten-Quiz (nicht doppelt), Pool-Frage und Konfiguration vorhanden");
        var imp2 = await J(await sam.PostAsync("/api/modules/import", Raw(exportText)));
        check(S(imp2, "name") == "TT-Bild (Import)" && S(imp2, "id") != impId && (await ModulesAsync(sam)).Count(m => S(m, "name") == "TT-Bild") == 1, "T98 ein zweiter Import überschreibt nichts, sondern erzeugt „TT-Bild (Import)“");
        var modsBefore = (await ModulesAsync(sam)).Count;
        string Mut(Action<JsonNode> change) { var n = JsonNode.Parse(exportText)!; change(n); return n.ToJsonString(); }
        var badCases = new Dictionary<string, string>
        {
            ["kein JSON"] = "{ kaputt",
            ["falsches Format"] = Mut(n => n["format"] = "learny-topic"),
            ["unbekannte Schema-Version"] = Mut(n => n["schema_version"] = 99),
            ["Karte ohne Begriff (nach gültigen Karten)"] = Mut(n => n["cards"]![3]!["term"] = ""),
            ["ungültiges Bild"] = Mut(n => n["cards"]!.AsArray().First(c => c!["images"]!.AsArray().Count > 0)!["images"]![0]!["data_base64"] = "Zm9v"),
            ["unbekanntes Feld"] = Mut(n => n["erfunden"] = 1),
            ["ungültige Quizkonfiguration"] = Mut(n => n["quiz_config"]!["pass_percent"] = 500),
            ["ungültige Pool-Frage"] = Mut(n => n["pool_questions"]![0]!["options"] = new JsonArray("a")),
        };
        var allRejected = true; var failures = new List<string>();
        foreach (var (label, json) in badCases)
        {
            var r = await sam.PostAsync("/api/modules/import", Raw(json));
            if (r.StatusCode != HttpStatusCode.BadRequest) { allRejected = false; failures.Add($"{label}={(int)r.StatusCode}"); }
        }
        check(allRejected && (await ModulesAsync(sam)).Count == modsBefore, "T99 ungültiges Modul-JSON wird mit 400 abgewiesen und hinterlässt kein (halbes) Modul" + (failures.Count > 0 ? " — " + string.Join(", ", failures) : ""));
        check((await leo.PostAsync("/api/import/cards", Body(new { cards = new[] { new { module = "TT-Alt", category = "K", term = "A", question = "Q?", definition = "D" } } }))).IsSuccessStatusCode
              && (await ModulesAsync(leo)).Any(m => S(m, "name") == "TT-Alt"), "T100 der bisherige Karten-Import bleibt unverändert funktionsfähig");
        var schemaClient = tina;
        var mSchema = await schemaClient.GetAsync("/api/schema/import/module"); var tSchema = await schemaClient.GetAsync("/api/schema/import/topic");
        check(mSchema.IsSuccessStatusCode && S(await J(mSchema), "name") == "learny-module" && tSchema.IsSuccessStatusCode && S(await J(tSchema), "name") == "learny-topic" && (await schemaClient.GetAsync("/api/schema/import/cards")).IsSuccessStatusCode,
            "T101 Schemas für Modul- und Themen-JSON sind über die API abrufbar; das Karten-Schema bleibt");

        // ─── E: Themen-JSON ───
        var tt = S(await J(await tina.PostAsync("/api/topics/", Body(new { name = "TT Quelle", description = "Quellthema" }))), "id");
        await tina.PostAsync($"/api/topics/{tt}/modules", Body(new { module_id = bild }));
        await tina.PostAsync($"/api/topics/{tt}/modules", Body(new { module_id = basis }));
        await tina.PutAsync($"/api/topics/{tt}/modules/order", Body(new { module_ids = new[] { basis, bild } }));
        object Exam(int count, int pass) => new { enabled = true, question_count = count, pass_percent = pass, allow_retry = true, shuffle_questions = true, single_choice = true, multiple_response = true, free_text = false, min_percent_for_next = 50 };
        var cfgPut = await tina.PutAsync($"/api/topics/{tt}/exam-config", Body(new
        {
            module_exams = new Dictionary<string, object> { [basis] = Exam(3, 60), [bild] = Exam(2, 70) },
            final_exam = new { enabled = true, question_count = 4, pass_percent = 65, allow_retry = true, random_selection = true, distribution = "manual", manual_counts = new Dictionary<string, int> { [basis] = 2, [bild] = 1 }, include_topic_questions = true, single_choice = true, multiple_response = true, free_text = false, unlock = new { require_all_modules_completed = false, require_module_exams_passed = true } },
        }));
        await tina.PostAsync($"/api/topics/{tt}/questions", Body(new { type = "single_choice", question = "Themenfrage A?", options = new[] { "x", "y", "z" }, correct_indexes = new[] { 1 }, explanation = "y" }));
        await tina.PostAsync($"/api/topics/{tt}/questions", Body(new { type = "multiple_response", question = "Themenfrage B?", options = new[] { "x", "y", "z" }, correct_indexes = new[] { 0, 2 }, explanation = "x und z" }));
        check(cfgPut.IsSuccessStatusCode, "T102 Quellthema mit Modulprüfungen, manueller Abschlussprüfung und Themenfragen eingerichtet");
        var tExportRes = await tina.GetAsync($"/api/topics/{tt}/export");
        var tExportText = await tExportRes.Content.ReadAsStringAsync();
        var tExport = JsonNode.Parse(tExportText)!;
        check(tExportRes.IsSuccessStatusCode && S(tExport, "format") == "learny-topic" && S(tExport, "modules.0.module_name") == "TT-Basis" && S(tExport, "modules.1.module_name") == "TT-Bild"
              && I(tExport, "modules.0.exam.question_count") == 3 && I(tExport, "modules.0.final_exam_questions") == 2 && I(tExport, "modules.1.final_exam_questions") == 1 && S(tExport, "exam_config.final_exam.distribution") == "manual"
              && tExport["topic_questions"]!.AsArray().Count == 2 && !tExportText.Contains(tt) && !tExportText.Contains(basis) && !tExportText.Contains("owner") && !tExportText.Contains("\"id\""), "T103 Themen-Export: Reihenfolge, Prüfungen, manuelle Verteilung je Modulname, Themenfragen — ohne IDs/Besitzer");
        check((await leo.GetAsync($"/api/topics/{tt}/export")).StatusCode == HttpStatusCode.NotFound && (await sam.PutAsync($"/api/topics/{tt}/definition", Raw(tExportText))).StatusCode == HttpStatusCode.NotFound, "T104 fremde Themen sind weder exportier- noch aktualisierbar");

        string Rename(string json, string name) { var n = JsonNode.Parse(json)!; n["name"] = name; return n.ToJsonString(); }
        var impTopic = await tina.PostAsync("/api/topics/import", Raw(Rename(tExportText, "TT Import")));
        var impTopicId = S(await J(impTopic), "id");
        var impDetail = await J(await tina.GetAsync($"/api/topics/{impTopicId}"));
        var impCfg2 = await J(await tina.GetAsync($"/api/topics/{impTopicId}/exam-config"));
        check(impTopic.StatusCode == HttpStatusCode.Created && S(impDetail, "modules.0.name") == "TT-Basis" && S(impDetail, "modules.1.name") == "TT-Bild" && S(impDetail, "version") == "0"
              && I(impCfg2, $"module_exams.{basis}.question_count") == 3 && I(impCfg2, $"module_exams.{bild}.pass_percent") == 70 && I(impCfg2, "final_exam.manual_counts." + basis) == 2 && I(impCfg2, "final_exam.pass_percent") == 65
              && (await J(await tina.GetAsync($"/api/topics/{impTopicId}/questions")))!.AsArray().Count == 2 && (await J(await tina.GetAsync($"/api/topics/{impTopicId}/versions")))!.AsArray().Count == 0,
            "T105 Themen-Import erzeugt einen Entwurf (Version 0, keine Veröffentlichung) mit gleicher Reihenfolge, Prüfungskonfiguration und Themenfragen");
        var dupImport = await tina.PostAsync("/api/topics/import", Raw(Rename(tExportText, "TT Import")));
        check(dupImport.StatusCode == HttpStatusCode.BadRequest, "T106 ein Thema mit vorhandenem Namen wird nicht überschrieben (400)");

        var leoTopicsBefore = (await J(await leo.GetAsync("/api/topics/")))!["own"]!.AsArray().Count;
        var leoMods = (await ModulesAsync(leo)).Count;
        var leoMissing = await leo.PostAsync("/api/topics/import", Raw(Rename(tExportText, "TT Leo Import")));
        var leoMissingText = await leoMissing.Content.ReadAsStringAsync();
        check(leoMissing.StatusCode == HttpStatusCode.BadRequest && leoMissingText.Contains("TT-Basis") && leoMissingText.Contains("nicht gefunden")
              && (await J(await leo.GetAsync("/api/topics/")))!["own"]!.AsArray().Count == leoTopicsBefore && (await ModulesAsync(leo)).Count == leoMods, "T107 fehlende Module führen zu einer klaren Fehlermeldung ohne Teiländerungen");
        string WithModule(string json, string moduleName) { var n = JsonNode.Parse(json)!; n["name"] = "TT Global-Ref"; n["modules"] = new JsonArray(new JsonObject { ["module_name"] = moduleName }); n["exam_config"] = new JsonObject(); n["topic_questions"] = new JsonArray(); return n.ToJsonString(); }
        var globRef = await sam.PostAsync("/api/topics/import", Raw(WithModule(tExportText, "TT-Glob")));
        check(globRef.StatusCode == HttpStatusCode.BadRequest && (await globRef.Content.ReadAsStringAsync()).Contains("klonen") && (await sam.PostAsync("/api/topics/import", Raw(WithModule(tExportText, "TT-Basis")))).StatusCode == HttpStatusCode.BadRequest,
            "T108 globale und fremde (auch geteilte) Module sind keine gültigen Modulreferenzen");
        var cloneRef = await leo.PostAsync("/api/topics/import", Raw(WithModule(tExportText, "TT-Glob")));
        check(cloneRef.StatusCode == HttpStatusCode.Created, "T109 der Name des eigenen Klons ist eine gültige Modulreferenz");

        var tFullRes = await tina.GetAsync($"/api/topics/{tt}/export?include_modules=true");
        var tFullText = await tFullRes.Content.ReadAsStringAsync();
        var samModsBefore = (await ModulesAsync(sam)).Count;
        var samFull = await sam.PostAsync("/api/topics/import", Raw(Rename(tFullText, "TT Sam Komplett")));
        var samFullId = S(await J(samFull), "id");
        var samDetail = await J(await sam.GetAsync($"/api/topics/{samFullId}"));
        check(tFullRes.IsSuccessStatusCode && samFull.StatusCode == HttpStatusCode.Created && S(samDetail, "modules.0.name") == "TT-Basis" && I(samDetail, "stats.cards") == 8 && (await ModulesAsync(sam)).Count == samModsBefore + 1,   // TT-Bild besitzt sam bereits (gleiche Referenz) und wird wiederverwendet
            
            "T110 Export mit eingebetteten Modulen ist in einem fremden Konto ohne Vorbedingungen importierbar (Module entstehen als eigene private Module)");
        var samFullBad = JsonNode.Parse(Rename(tFullText, "TT Sam Kaputt"))!;
        samFullBad["modules"]![1]!["module"]!["cards"]![0]!["term"] = "";
        var samModsNow = (await ModulesAsync(sam)).Count; var samTopicsNow = (await J(await sam.GetAsync("/api/topics/")))!["own"]!.AsArray().Count;
        check((await sam.PostAsync("/api/topics/import", Raw(samFullBad.ToJsonString()))).StatusCode == HttpStatusCode.BadRequest
              && (await ModulesAsync(sam)).Count == samModsNow && (await J(await sam.GetAsync("/api/topics/")))!["own"]!.AsArray().Count == samTopicsNow, "T111 fehlerhafte eingebettete Module: nichts wird angelegt (kein Thema, keine Module)");

        var badTopic = new Dictionary<string, string>
        {
            ["kein JSON"] = "[",
            ["falsches Format"] = Raw1(tExportText, n => n["format"] = "learny-module"),
            ["Version"] = Raw1(tExportText, n => n["schema_version"] = 2),
            ["unbekanntes Feld"] = Raw1(tExportText, n => n["owner_user_id"] = "x"),
            ["unbekanntes Modulfeld"] = Raw1(tExportText, n => n["modules"]![0]!["module_id"] = Guid.NewGuid().ToString()),
            ["doppeltes Modul"] = Raw1(tExportText, n => n["modules"]!.AsArray().Add(JsonNode.Parse(n["modules"]![0]!.ToJsonString()))),
            ["ungültige Prüfung"] = Raw1(tExportText, n => n["modules"]![0]!["exam"]!["pass_percent"] = 0),
            ["ungültige Themenfrage"] = Raw1(tExportText, n => n["topic_questions"]![0]!["options"] = new JsonArray("x")),
            ["leerer Name"] = Raw1(tExportText, n => n["name"] = " "),
        };
        var topicsNow = (await J(await tina.GetAsync("/api/topics/")))!["own"]!.AsArray().Count;
        var failures2 = new List<string>();
        foreach (var (label, json) in badTopic)
            if ((await tina.PostAsync("/api/topics/import", Raw(json))).StatusCode != HttpStatusCode.BadRequest) failures2.Add(label);
        check(failures2.Count == 0 && (await J(await tina.GetAsync("/api/topics/")))!["own"]!.AsArray().Count == topicsNow, "T112 ungültige Themen-Definitionen werden abgewiesen, ohne ein Thema anzulegen" + (failures2.Count > 0 ? " — " + string.Join(", ", failures2) : ""));

        // ─── F: Aktualisieren des Entwurfs, Versionierung ───
        var pub = await tina.PostAsync($"/api/topics/{tt}/versions", Body(new { }));
        var v1 = await J(await tina.GetAsync($"/api/topics/{tt}?version=1"));
        var v1Modules = string.Join(",", v1!["modules"]!.AsArray().Select(m => S(m, "module_id")));
        check(pub.StatusCode == HttpStatusCode.Created && I(v1, "version") == 1, "T113 Version 1 des Quellthemas veröffentlicht");
        var upd = JsonNode.Parse(tExportText)!;
        upd["description"] = "Aktualisiert per JSON";
        upd["modules"] = new JsonArray(upd["modules"]![1]!.DeepClone());   // nur TT-Bild
        upd["exam_config"]!["final_exam"]!["distribution"] = "even";
        upd["topic_questions"]!.AsArray().RemoveAt(1);
        var updRes = await tina.PutAsync($"/api/topics/{tt}/definition", Raw(upd.ToJsonString()));
        var draftAfter = await J(await tina.GetAsync($"/api/topics/{tt}"));
        var v1After = await J(await tina.GetAsync($"/api/topics/{tt}?version=1"));
        check(updRes.IsSuccessStatusCode && draftAfter!["modules"]!.AsArray().Count == 1 && S(draftAfter, "topic.description") == "Aktualisiert per JSON" && (await J(await tina.GetAsync($"/api/topics/{tt}/questions")))!.AsArray().Count == 1
              && S(draftAfter, "unpublished_changes") == "true", "T114 Update ersetzt den Entwurf (Module, Beschreibung, Verteilung, Themenfragen) und markiert unveröffentlichte Änderungen");
        check(string.Join(",", v1After!["modules"]!.AsArray().Select(m => S(m, "module_id"))) == v1Modules && v1After["modules"]!.AsArray().Count == 2 && (await J(await tina.GetAsync($"/api/topics/{tt}/versions")))!.AsArray().Count == 1,
            "T115 die veröffentlichte Version 1 bleibt unverändert; es entstand keine neue Version");
        var before = JsonNode.Parse(draftAfter!.ToJsonString())!;
        var badUpd = JsonNode.Parse(tExportText)!; badUpd["modules"]![0]!["module_name"] = "TT-Gibt-es-nicht"; badUpd["modules"]![0]!["module_ref"] = Guid.NewGuid().ToString();
        var badUpdRes = await tina.PutAsync($"/api/topics/{tt}/definition", Raw(badUpd.ToJsonString()));
        var draftSame = await J(await tina.GetAsync($"/api/topics/{tt}"));
        check(badUpdRes.StatusCode == HttpStatusCode.BadRequest && string.Join(",", draftSame!["modules"]!.AsArray().Select(m => S(m, "module_id"))) == string.Join(",", before["modules"]!.AsArray().Select(m => S(m, "module_id")))
              && S(draftSame, "topic.description") == "Aktualisiert per JSON" && (await J(await tina.GetAsync($"/api/topics/{tt}/questions")))!.AsArray().Count == 1, "T116 fehlerhafte Aktualisierung ändert den Entwurf nicht");
        var nameClash = JsonNode.Parse(tExportText)!; nameClash["name"] = "TT Import";
        check((await tina.PutAsync($"/api/topics/{tt}/definition", Raw(nameClash.ToJsonString()))).StatusCode == HttpStatusCode.BadRequest, "T117 Umbenennen auf einen vorhandenen Themennamen wird abgelehnt");
        var pub2 = await tina.PostAsync($"/api/topics/{tt}/versions", Body(new { }));
        var v2 = await J(await tina.GetAsync($"/api/topics/{tt}?version=2"));
        var v1Still = await J(await tina.GetAsync($"/api/topics/{tt}?version=1"));
        check(pub2.StatusCode == HttpStatusCode.Created && I(v2, "version") == 2 && v2!["modules"]!.AsArray().Count == 1 && v1Still!["modules"]!.AsArray().Count == 2, "T118 erst die ausdrückliche Veröffentlichung erzeugt Version 2; Version 1 bleibt nutzbar");
        var v1Mod = S(v1Still, "modules.0.module_id");
        var examStart = await tina.PostAsync($"/api/topics/{tt}/exams/module", Body(new { module_id = v1Mod, version = 1 }));
        check(examStart.IsSuccessStatusCode, "T119 Modulprüfung einer älteren Themenversion startet weiterhin (ohne OpenAI-Schlüssel)");
        var impExam = await tina.PostAsync($"/api/topics/{impTopicId}/versions", Body(new { }));
        check(impExam.StatusCode == HttpStatusCode.Created, "T120 ein per JSON erzeugter Entwurf lässt sich ausdrücklich veröffentlichen");

        // ─── Modulreferenz (reference_id / module_ref): stabil, unabhängig vom Anzeigenamen ───
        var refA = S(JsonNode.Parse(tExportText), "modules.0.module_ref"); var refB = S(JsonNode.Parse(tExportText), "modules.1.module_ref");
        var exp1 = JsonNode.Parse(await (await tina.GetAsync($"/api/modules/{basis}/export")).Content.ReadAsStringAsync())!;
        var exp2 = JsonNode.Parse(await (await tina.GetAsync($"/api/modules/{basis}/export")).Content.ReadAsStringAsync())!;
        check(Guid.TryParse(refA, out _) && refA != refB && S(exp1, "reference_id") == refA && S(exp2, "reference_id") == refA, "T133 Modulexport und Themenexport tragen dieselbe stabile Referenz (GUID); wiederholte Exporte ändern sie nicht");
        var renamed = JsonNode.Parse(Rename(tExportText, "TT Umbenannt-Import"))!;
        renamed["modules"]![0]!["module_name"] = "Völlig anderer Anzeigename";
        var renRes = await tina.PostAsync("/api/topics/import", Raw(renamed.ToJsonString()));
        var renDetail = await J(await tina.GetAsync($"/api/topics/{S(await J(renRes), "id")}"));
        check(renRes.StatusCode == HttpStatusCode.Created && S(renDetail, "modules.0.module_id") == basis && S(renDetail, "modules.0.name") == "TT-Basis", "T134 die Referenz löst das Modul unabhängig vom (geänderten) Anzeigenamen auf");
        var wrongRef = JsonNode.Parse(Rename(tExportText, "TT Falsche Referenz"))!;
        wrongRef["modules"]![0]!["module_ref"] = Guid.NewGuid().ToString();
        var wrongRes = await tina.PostAsync("/api/topics/import", Raw(wrongRef.ToJsonString()));
        check(wrongRes.StatusCode == HttpStatusCode.BadRequest && (await wrongRes.Content.ReadAsStringAsync()).Contains("module_ref"), "T135 eine unbekannte Referenz wird nicht still über den Namen aufgelöst, sondern gemeldet");
        var noRef = JsonNode.Parse(Rename(tExportText, "TT Ohne Referenz"))!;
        foreach (var m in noRef["modules"]!.AsArray()) m!.AsObject().Remove("module_ref");
        check((await tina.PostAsync("/api/topics/import", Raw(noRef.ToJsonString()))).StatusCode == HttpStatusCode.Created
              && (await tina.PostAsync("/api/topics/import", Raw(Rename(tExportText, "TT Doppelref").Replace(refB, refA)))).StatusCode == HttpStatusCode.BadRequest
              && (await tina.PostAsync("/api/topics/import", Raw(Raw1(tExportText, n => { n["name"] = "TT Kaputtref"; n["modules"]![0]!["module_ref"] = "keine-guid"; })))).StatusCode == HttpStatusCode.BadRequest,
            "T136 ohne Referenz gilt weiter der Name (Altformat); doppelte und ungültige Referenzen werden abgewiesen");
        var modsBeforeReuse = (await ModulesAsync(sam)).Count;
        var reuse = await sam.PostAsync("/api/topics/import", Raw(Rename(tFullText, "TT Sam Wiederverwendung")));
        var reuseDetail = await J(await sam.GetAsync($"/api/topics/{S(await J(reuse), "id")}"));
        check(reuse.StatusCode == HttpStatusCode.Created && (await ModulesAsync(sam)).Count == modsBeforeReuse && S(reuseDetail, "modules.0.module_id") == S(samDetail, "modules.0.module_id") && I(reuseDetail, "stats.cards") == 8,
            "T137 eingebettete Module mit bekannter Referenz werden wiederverwendet (keine neuen Module, nichts überschrieben)");
        var cloneRef2 = await J(await leo.GetAsync("/api/modules/"));
        var origRef = S(JsonNode.Parse(await (await tina.GetAsync($"/api/modules/{glob}/export")).Content.ReadAsStringAsync()), "reference_id");
        var cloneExp = JsonNode.Parse(await (await leo.GetAsync($"/api/modules/{cloneId}/export")).Content.ReadAsStringAsync())!;
        var importedTwice = JsonNode.Parse(await (await sam.GetAsync($"/api/modules/{impId}/export")).Content.ReadAsStringAsync())!;
        var imp2Export = JsonNode.Parse(await (await sam.GetAsync($"/api/modules/{S(imp2, "id")}/export")).Content.ReadAsStringAsync())!;
        check(S(cloneExp, "reference_id") != origRef && S(cloneExp, "reference_id").Length > 0 && S(importedTwice, "reference_id") == S(exported, "reference_id") && S(imp2Export, "reference_id") != S(importedTwice, "reference_id"),
            "T138 Klone erhalten eine eigene Referenz; ein Import übernimmt sie nur, wenn der Benutzer sie noch nicht verwendet");

        // ─── G: MCP ───
        async Task<string> TokenAsync(HttpClient c, string name) => S(await J(await c.PostAsync("/api/mcp/links/", Body(new { name, days = 30 }))), "token");
        var tinaTok = await TokenAsync(tina, "TT-MCP-Tina"); var samTok = await TokenAsync(sam, "TT-MCP-Sam");
        async Task<string> Tool(string token, string tool, object args)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, mcpUrl + "/mcp") { Content = Body(new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = tool, arguments = args } }) };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var res = await newClient().SendAsync(req);
            return res.IsSuccessStatusCode ? S(await J(res), "result.content.0.text") : "HTTP " + (int)res.StatusCode;
        }
        var tSchemaText = await Tool(tinaTok, "get_topic_import_schema", new { });
        var mSchemaText = await Tool(tinaTok, "get_module_import_schema", new { });
        check(tSchemaText.Contains("learny-topic") && tSchemaText.Contains("module_name") && mSchemaText.Contains("learny-module") && mSchemaText.Contains("pool_questions"), "T121 MCP: get_topic_import_schema und get_module_import_schema liefern die Live-Schemas");
        var def = JsonNode.Parse(Rename(tExportText, "TT MCP Thema"))!;
        var mcpCreate = await Tool(tinaTok, "create_topic", new { definition = def });
        var mcpTopicId = (await J(await tina.GetAsync("/api/topics/")))!["own"]!.AsArray().FirstOrDefault(t => S(t, "topic.name") == "TT MCP Thema") is { } mt ? S(mt, "topic.id") : "";
        check(mcpCreate.Contains("Topic created as draft") && mcpTopicId.Length > 0 && I(await J(await tina.GetAsync($"/api/topics/{mcpTopicId}")), "version") == 0, "T122 MCP create_topic legt einen privaten Entwurf an (nichts veröffentlicht)");
        def["description"] = "Per MCP geändert";
        var mcpUpdate = await Tool(tinaTok, "update_topic", new { topic_id = mcpTopicId, definition = def });
        check(mcpUpdate.Contains("Topic draft updated") && S(await J(await tina.GetAsync($"/api/topics/{mcpTopicId}")), "topic.description") == "Per MCP geändert", "T123 MCP update_topic aktualisiert den Entwurf");
        var mcpExport = await Tool(tinaTok, "export_topic", new { topic_id = mcpTopicId });
        check(mcpExport.Contains("learny-topic") && mcpExport.Contains("Per MCP ge") && !mcpExport.Contains(mcpTopicId), "T124 MCP export_topic liefert die portable Definition ohne IDs");
        var mcpImport = await Tool(tinaTok, "import_topic", new { definition_json = Rename(mcpExport, "TT MCP Import") });
        check(mcpImport.Contains("Topic imported as draft") && (await J(await tina.GetAsync("/api/topics/")))!["own"]!.AsArray().Any(t => S(t, "topic.name") == "TT MCP Import"), "T125 MCP import_topic importiert eine Definition als Text");
        check((await Tool(tinaTok, "create_topic", new { definition = Rename(mcpExport, "x") })).Contains("required") && (await Tool(tinaTok, "import_topic", new { definition_json = "{ kaputt" })).Contains("must be a JSON object")
              && (await Tool(samTok, "create_topic", new { definition = JsonNode.Parse(WithModule(tExportText, "TT-Glob")) })).Contains("API error 400"), "T126 MCP: ungültige Eingaben und globale Modulreferenzen liefern Fehlermeldungen statt Änderungen");
        var samUpd = await Tool(samTok, "update_topic", new { topic_id = mcpTopicId, definition = def });
        var samExp = await Tool(samTok, "export_topic", new { topic_id = mcpTopicId });
        check(samUpd.StartsWith("API error 404") && samExp.StartsWith("API error 404") && S(await J(await tina.GetAsync($"/api/topics/{mcpTopicId}")), "topic.description") == "Per MCP geändert", "T127 MCP: fremde Themenentwürfe lassen sich weder ändern noch exportieren");
        var mcpClone = await Tool(samTok, "clone_global_module", new { module_id = globalId });
        var mcpModExport = await Tool(tinaTok, "export_module", new { module_id = bild });
        var mcpModImport = await Tool(samTok, "import_module", new { definition = JsonNode.Parse(mcpModExport) });
        check(mcpClone.Contains("TT-Glob") && mcpModExport.Contains("learny-module") && mcpModImport.Contains("TT-Bild (Import") && (await ModulesAsync(sam)).Any(m => S(m, "name") == "TT-Glob"), "T128 MCP: clone_global_module, export_module und import_module nutzen dieselben Dienste");
        var listTools = S(await J(await PostRpc(newClient(), mcpUrl, tinaTok, "tools/list")), "result.tools");
        check(new[] { "approve_topic", "publish_topic", "assign_topic", "share_topic", "reject_topic", "delete_topic" }.All(t => !listTools.Contains($"\"{t}\"")) && listTools.Contains("\"create_topic\"") && listTools.Contains("\"update_topic\""),
            "T129 MCP bietet keine Administrations-, Veröffentlichungs-, Freigabe- oder Zuweisungswerkzeuge");
        using (var raw = newClient())
        {
            raw.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tinaTok);
            check((await raw.PostAsync("/api/topics/import", Raw(Rename(tExportText, "TT Token Direkt")))).StatusCode == HttpStatusCode.Created
                  && (await raw.PutAsync($"/api/topics/{mcpTopicId}/definition", Raw(def.ToJsonString()))).IsSuccessStatusCode
                  && (await raw.PostAsync("/api/topics/", Body(new { name = "Direkt" }))).StatusCode == HttpStatusCode.Forbidden
                  && (await raw.PostAsync($"/api/topics/{mcpTopicId}/versions", Body(new { }))).StatusCode == HttpStatusCode.Forbidden
                  && (await raw.PostAsync($"/api/topics/{mcpTopicId}/assignments", Body(new { user_id = Guid.NewGuid() }))).StatusCode == HttpStatusCode.Forbidden
                  && (await raw.PostAsync($"/api/topics/{mcpTopicId}/modules/{basis}/replace-with-clone", Body(new { }))).StatusCode == HttpStatusCode.Forbidden
                  && (await raw.PostAsync("/api/admin/topics/versions/x/approve", Body(new { }))).StatusCode == HttpStatusCode.Forbidden,
                "T130 MCP-Token darf nur Themen importieren und den eigenen Entwurf aktualisieren — Veröffentlichen, Zuweisen, Ersetzen und Administration bleiben gesperrt (403)");
        }
        using (var keyOnly = newClient())
        {
            keyOnly.DefaultRequestHeaders.Add("X-MCP-Key", "falscher-schluessel");
            check((await keyOnly.PostAsync("/api/topics/import", Raw(tExportText))).StatusCode == HttpStatusCode.Unauthorized
                  && (await newClient().PostAsync("/api/topics/import", Raw(tExportText))).StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Found
                  && (await newClient().PostAsync("/api/modules/import", Raw(exportText))).StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Found,
                "T131 ohne gültiges Benutzertoken (auch mit falschem Dienstschlüssel) ist kein Import möglich");
        }
        var audit = await admin.GetStringAsync("/api/admin/audit?limit=500");
        check(new[] { "topic.import", "topic.definition.update", "topic.export", "module.clone", "module.import" }.All(audit.Contains) && !audit.Contains(tinaTok), "T132 Import, Aktualisierung, Export und Klonen werden auditiert; keine Tokens im Audit");
    }

    private static string Raw1(string json, Action<JsonNode> change) { var n = JsonNode.Parse(json)!; change(n); return n.ToJsonString(); }

    private static async Task<HttpResponseMessage> PostRpc(HttpClient c, string mcpUrl, string token, string method)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, mcpUrl + "/mcp") { Content = Body(new { jsonrpc = "2.0", id = 9, method, @params = new { } }) };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await c.SendAsync(req);
    }

    private static async Task<string> UserIdAsync(HttpClient asker, string query)
    {
        var arr = (await J(await asker.GetAsync($"/api/topics/users?q={query}")))?.AsArray();
        return arr is null || arr.Count == 0 ? "" : S(arr[0], "id");
    }
}
