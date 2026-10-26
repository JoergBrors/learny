using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Themen (ADR 0027) Ende-zu-Ende über die REST-API: Zuordnung ohne Kopien, Versionierung, Freigaben/Zuweisungen, globale Freigabe, Modul- und Abschlussprüfungen
/// auf der bestehenden Quiz-Engine, Berechtigungen, MCP-Lesezugriff und Gleichzeitigkeit.
/// </summary>
public static class TopicE2ETests
{
    private static StringContent Body(object? o) => new(JsonSerializer.Serialize(o ?? new { }), Encoding.UTF8, "application/json");
    private static async Task<JsonNode?> J(HttpResponseMessage r) { var t = await r.Content.ReadAsStringAsync(); return t.Length == 0 ? null : JsonNode.Parse(t); }
    private static string S(JsonNode? n, string path)
    {
        var cur = n;
        foreach (var part in path.Split('.')) cur = cur is JsonArray a && int.TryParse(part, out var i) ? a[i] : cur?[part];
        return cur?.ToString() ?? "";
    }
    private static int I(JsonNode? n, string path) => int.TryParse(S(n, path), out var v) ? v : -1;

    public static async Task RunAsync(Func<string?, Task<HttpClient>> login, Func<HttpClient> newClient, HttpClient admin, string mcpUrl, Action<bool, string> check)
    {
        Console.WriteLine("\nThemen, Versionen, Freigaben, Zuweisungen und Prüfungen (ADR 0027)");
        var tina = await login("tina"); var leo = await login("leo"); var sam = await login("sam");
        var users = new Dictionary<string, string>
        {
            ["tina"] = (await SearchAsync(leo, "", "tina"))!, ["leo"] = (await SearchAsync(tina, "", "leo"))!, ["sam"] = (await SearchAsync(tina, "", "sam"))!,
        };

        // ─── Module anlegen: 4 × 16 Karten (= 64), je 8 Single-Choice-Fragen (richtig = Index 1) ───
        async Task ImportModuleAsync(HttpClient c, string module, int cards, int withQuiz)
        {
            var list = new List<object>();
            for (var i = 0; i < cards; i++)
            {
                object? quiz = i < withQuiz ? new[] { new { type = "single_choice", question = $"{module}: Frage {i}?", options = new[] { "A", "B", "C", "D" }, correct_index = 1, explanation = $"B ist richtig ({i})." } } : null;
                list.Add(quiz is null
                    ? new { module, category = "Kat", term = $"{module}-{i}", question = $"Wie funktioniert {module} {i}?", definition = $"Definition {module} {i}" }
                    : (object)new { module, category = "Kat", term = $"{module}-{i}", question = $"Wie funktioniert {module} {i}?", definition = $"Definition {module} {i}", quiz });
            }
            var r = await c.PostAsync("/api/import/cards", Body(new { cards = list }));
            if (!r.IsSuccessStatusCode) throw new Exception("Import fehlgeschlagen: " + await r.Content.ReadAsStringAsync());
        }
        foreach (var m in new[] { "TE-Entra", "TE-Exchange", "TE-Teams", "TE-Intune" }) await ImportModuleAsync(tina, m, 16, 8);
        async Task<Dictionary<string, string>> ModuleIdsAsync(HttpClient c) =>
            ((await J(await c.GetAsync("/api/modules/")))!.AsArray()).ToDictionary(m => S(m, "name"), m => S(m, "id"));
        var mod = await ModuleIdsAsync(tina);
        var A = mod["TE-Entra"]; var B = mod["TE-Exchange"]; var C = mod["TE-Teams"]; var D = mod["TE-Intune"];

        // ─── Thema anlegen, Module zuordnen, sortieren ───
        var created = await tina.PostAsync("/api/topics/", Body(new { name = "TE Microsoft 365 Administrator – Grundlagen", description = "Grundlagen" }));
        var topicId = S(await J(created), "id");
        check(created.StatusCode == HttpStatusCode.Created && topicId.Length > 0, "T1 Thema anlegen → 201");
        check((await tina.PostAsync("/api/topics/", Body(new { name = "TE Microsoft 365 Administrator – Grundlagen" }))).StatusCode == HttpStatusCode.BadRequest, "T2 doppelter Themenname eines Eigentümers → 400");
        foreach (var m in new[] { A, B, C }) await tina.PostAsync($"/api/topics/{topicId}/modules", Body(new { module_id = m }));
        await tina.PostAsync($"/api/topics/{topicId}/modules", Body(new { module_id = A }));   // idempotent
        var order = (await tina.PutAsync($"/api/topics/{topicId}/modules/order", Body(new { module_ids = new[] { C, A, B } }))).StatusCode;
        var draft = await J(await tina.GetAsync($"/api/topics/{topicId}"));
        check(order == HttpStatusCode.NoContent && string.Join(",", draft!["modules"]!.AsArray().Select(m => S(m, "module_id"))) == $"{C},{A},{B}", "T3 drei eigene Module zugeordnet (idempotent) und sortiert: Reihenfolge C, A, B");
        check((await tina.PutAsync($"/api/topics/{topicId}/modules/order", Body(new { module_ids = new[] { A, B } }))).StatusCode == HttpStatusCode.BadRequest
              && (await tina.PutAsync($"/api/topics/{topicId}/modules/order", Body(new { module_ids = new[] { A, B, B } }))).StatusCode == HttpStatusCode.BadRequest, "T4 Reihenfolge muss genau die Module des Themas enthalten (keine Auslassung, kein Duplikat)");
        check((await ModuleIdsAsync(tina)).Count == mod.Count, "T5 Zuordnung kopiert keine Module (Modulanzahl unverändert)");
        var second = S(await J(await tina.PostAsync("/api/topics/", Body(new { name = "TE Zweites Thema" }))), "id");
        await tina.PostAsync($"/api/topics/{second}/modules", Body(new { module_id = A }));
        check(S(await J(await tina.GetAsync($"/api/topics/{second}")), "modules.0.module_id") == A && S(await J(await tina.GetAsync($"/api/topics/{topicId}")), "modules.1.module_id") == A, "T6 Ein Modul gehört gleichzeitig mehreren Themen");

        // ─── Prüfungskonfiguration, eigene Themenfragen, Zahlen ───
        for (var i = 0; i < 3; i++)
            await tina.PostAsync($"/api/topics/{topicId}/questions", Body(new { type = "single_choice", question = $"Themenfrage {i}?", options = new[] { "x", "y", "z" }, correct_indexes = new[] { 1 }, explanation = "y" }));
        object ModuleExam(bool on, int count = 3) => new { enabled = on, question_count = count, pass_percent = 60, allow_retry = true, shuffle_questions = true, single_choice = true, multiple_response = true, free_text = false };
        var cfgBody = new
        {
            module_exams = new Dictionary<string, object> { [A] = ModuleExam(true), [B] = ModuleExam(true), [C] = ModuleExam(true) },
            final_exam = new { enabled = true, question_count = 15, pass_percent = 60, allow_retry = true, random_selection = true, distribution = "even", include_topic_questions = true, single_choice = true, multiple_response = true, free_text = false, unlock = new { require_all_modules_completed = true, require_module_exams_passed = true } },
        };
        var cfgRes = await tina.PutAsync($"/api/topics/{topicId}/exam-config", Body(cfgBody));
        check(cfgRes.StatusCode == HttpStatusCode.OK, "T7 Prüfungskonfiguration (3 Modulprüfungen + Abschlussprüfung) wird gespeichert");
        var stats = (await J(await tina.GetAsync($"/api/topics/{topicId}")))!["stats"]!;
        check(I(stats, "cards") == 48 && I(stats, "choice_questions") == 24 && I(stats, "final_exam_questions") == 15 && I(stats, "modules") == 3 && I(stats, "final_exam_pool") == 27,
            $"T8 Zahlen automatisch ermittelt: 48 Lernkarten, 24 Multiple-Choice-Fragen, 15 Abschlussprüfungsfragen (Pool 27) — ist: {S(stats, "cards")}/{S(stats, "choice_questions")}/{S(stats, "final_exam_questions")}/{S(stats, "final_exam_pool")}");
        check((await tina.PutAsync($"/api/topics/{topicId}/exam-config", Body(new { module_exams = new Dictionary<string, object> { [Guid.NewGuid().ToString()] = ModuleExam(true) }, final_exam = new { enabled = false } }))).StatusCode == HttpStatusCode.BadRequest
              && (await tina.PutAsync($"/api/topics/{topicId}/exam-config", Body(new { final_exam = new { enabled = true, question_count = 0 } }))).StatusCode == HttpStatusCode.BadRequest, "T9 Ungültige Prüfungskonfiguration (fremdes Modul, Anzahl 0) → 400");

        // ─── Veröffentlichen: Version 1 ───
        check((await J(await tina.GetAsync($"/api/topics/{topicId}")))!["unpublished_changes"]!.GetValue<bool>(), "T10 Oberfläche erkennt unveröffentlichte Änderungen vor der ersten Version");
        var v1 = await tina.PostAsync($"/api/topics/{topicId}/versions", Body(new { }));
        check(v1.StatusCode == HttpStatusCode.Created && I(await J(v1), "version_number") == 1, "T11 Veröffentlichung erzeugt Version 1");
        check(!(await J(await tina.GetAsync($"/api/topics/{topicId}")))!["unpublished_changes"]!.GetValue<bool>(), "T12 Nach der Veröffentlichung gibt es keine unveröffentlichten Änderungen");
        check((await tina.PostAsync($"/api/topics/{topicId}/versions", Body(new { }))).StatusCode == HttpStatusCode.Conflict, "T13 Erneutes Veröffentlichen ohne Änderung → 409 (keine doppelte Version)");
        var v1Detail = await J(await tina.GetAsync($"/api/topics/{topicId}?version=1"));
        var snapA = S(v1Detail, "modules.1.module_id");
        check(S(v1Detail, "modules.0.name") == "TE-Teams" && snapA != A && I(v1Detail, "stats.cards") == 48 && v1Detail!["modules"]!.AsArray().Count == 3, "T14 Version 1 enthält die eingefrorenen Kopien in der festgelegten Reihenfolge (nicht die Originale)");

        // ─── Zusätzliches Modul → Entwurf, Version 1 bleibt, Version 2 ───
        await tina.PostAsync($"/api/topics/{topicId}/modules", Body(new { module_id = D }));
        check((await J(await tina.GetAsync($"/api/topics/{topicId}")))!["unpublished_changes"]!.GetValue<bool>()
              && I(await J(await tina.GetAsync($"/api/topics/{topicId}?version=1")), "stats.modules") == 3, "T15 Zusätzliches Modul erzeugt einen geänderten Entwurf; Version 1 bleibt unverändert");
        var v2 = await tina.PostAsync($"/api/topics/{topicId}/versions", Body(new { }));
        check(v2.StatusCode == HttpStatusCode.Created && I(await J(v2), "version_number") == 2, "T16 Neue Veröffentlichung erzeugt Version 2");
        var v1Again = await J(await tina.GetAsync($"/api/topics/{topicId}?version=1"));
        var v2Detail = await J(await tina.GetAsync($"/api/topics/{topicId}?version=2"));
        check(I(v1Again, "stats.modules") == 3 && I(v2Detail, "stats.modules") == 4 && I(v2Detail, "stats.cards") == 64, "T17 Version 1 bleibt unverändert lesbar; Version 2 enthält vier Module (64 Karten)");
        var snapIds1 = v1Detail!["modules"]!.AsArray().Select(m => S(m, "module_id")).ToHashSet();
        var snapIds2 = v2Detail!["modules"]!.AsArray().Select(m => S(m, "module_id")).ToList();
        check(snapIds1.All(snapIds2.Contains), "T18 Unveränderte Module werden in Version 2 wiederverwendet (gleiche eingefrorene Kopie, kein Duplikat)");
        var versions = (await J(await tina.GetAsync($"/api/topics/{topicId}/versions")))!.AsArray();
        check(versions.Count == 2 && S(versions[0], "version_number") == "2", "T19 Versionshistorie: Version 2 und 1");

        // ─── Gleichzeitigkeit: Veröffentlichung ───
        await tina.PutAsync($"/api/topics/{topicId}", Body(new { name = "TE Microsoft 365 Administrator – Grundlagen", description = "Grundlagen (aktualisiert)" }));
        var parallel = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => tina.PostAsync($"/api/topics/{topicId}/versions", Body(new { }))));
        var created201 = parallel.Count(r => r.StatusCode == HttpStatusCode.Created);
        var numbers = (await J(await tina.GetAsync($"/api/topics/{topicId}/versions")))!.AsArray().Select(v => S(v, "version_number")).ToList();
        check(created201 == 1 && numbers.Count == 3 && numbers.Distinct().Count() == 3 && numbers[0] == "3", $"T20 Sechs gleichzeitige Veröffentlichungen erzeugen genau eine Version (keine doppelte Nummer): {created201} erstellt, Versionen {string.Join(",", numbers)}");

        // ─── Freigabe, Berechtigungen ───
        var leoId = users["leo"];
        var samId = users["sam"];
        check((await tina.PostAsync($"/api/topics/{topicId}/shares", Body(new { user_id = leoId }))).StatusCode == HttpStatusCode.NoContent, "T21 Thema für einen anderen Benutzer freigegeben");
        check((await tina.PostAsync($"/api/topics/{topicId}/shares", Body(new { user_id = users["tina"] }))).StatusCode == HttpStatusCode.BadRequest, "T22 Selbstfreigabe ist verboten");
        var leoOverview = await J(await leo.GetAsync("/api/topics/"));
        check(leoOverview!["shared"]!.AsArray().Count == 1 && S(leoOverview, "shared.0.topic.id") == topicId && leoOverview["own"]!.AsArray().Count == 0, "T23 Freigegebenes Thema erscheint bei „Mit mir geteilt“");
        var leoDetail = await J(await leo.GetAsync($"/api/topics/{topicId}"));
        var latest = I(leoDetail, "version");
        check(latest == 3, "T24 Empfänger sieht die neueste veröffentlichte Version (3)");
        var latestDetail = await J(await tina.GetAsync($"/api/topics/{topicId}?version={latest}"));
        check((await leo.PutAsync($"/api/topics/{topicId}", Body(new { name = "Gekapert" }))).StatusCode == HttpStatusCode.NotFound
              && (await leo.PostAsync($"/api/topics/{topicId}/modules", Body(new { module_id = A }))).StatusCode == HttpStatusCode.NotFound
              && (await leo.PostAsync($"/api/topics/{topicId}/versions", Body(new { }))).StatusCode == HttpStatusCode.NotFound
              && (await leo.GetAsync($"/api/topics/{topicId}/shares")).StatusCode == HttpStatusCode.NotFound, "T25 Empfänger kann Thema, Module, Versionen und Freigaben nicht verwalten");
        var snapCards = (await J(await leo.GetAsync($"/api/cards/?module_id={snapIds2[0]}")))!.AsArray();
        check(snapCards.Count == 16, "T26 Empfänger kann die Karten der eingefrorenen Module lesen");
        var snapCardId = S(snapCards[0], "id");
        var delSnap = await leo.DeleteAsync($"/api/cards/{snapCardId}");
        var impSnap = await leo.PostAsync("/api/import/cards", Body(new { cards = new object[] { new { id = snapCardId, module = "Gekapert", category = "K", term = "x", question = "y", definition = "z" } } }));
        check(!delSnap.IsSuccessStatusCode && (await J(await leo.GetAsync($"/api/cards/{snapCardId}")))!["term"]!.GetValue<string>().StartsWith("TE-"), "T27 Empfänger kann Karten der Themenmodule weder löschen noch überschreiben");
        check((await sam.GetAsync($"/api/topics/{topicId}")).StatusCode == HttpStatusCode.NotFound
              && (await sam.GetAsync($"/api/topics/{topicId}/progress")).StatusCode == HttpStatusCode.NotFound
              && (await sam.GetAsync($"/api/topics/{topicId}/versions")).StatusCode == HttpStatusCode.NotFound
              && (await sam.PostAsync($"/api/topics/{topicId}/exams/module", Body(new { module_id = snapIds2[0] }))).StatusCode == HttpStatusCode.NotFound
              && (await sam.GetAsync($"/api/topics/{Guid.NewGuid()}")).StatusCode == HttpStatusCode.NotFound
              && (await sam.GetAsync("/api/topics/not-a-guid")).StatusCode == HttpStatusCode.NotFound, "T28 Fremder Benutzer: privates Thema weder lesbar noch prüfbar (auch mit erfundenen/ungültigen IDs)");
        check((await J(await sam.GetAsync($"/api/cards/?module_id={snapIds2[0]}")))!.AsArray().Count == 0, "T29 Fremder Benutzer sieht die Karten der Themenmodule nicht");
        check((await sam.PostAsync($"/api/topics/{second}/modules", Body(new { module_id = A }))).StatusCode == HttpStatusCode.NotFound, "T30 Fremde IDs: Thema/Modul eines anderen Benutzers können nicht manipuliert werden");
        var samTopic = S(await J(await sam.PostAsync("/api/topics/", Body(new { name = "TE Sam" }))), "id");
        check((await sam.PostAsync($"/api/topics/{samTopic}/modules", Body(new { module_id = A }))).StatusCode == HttpStatusCode.NotFound
              && (await sam.PostAsync($"/api/topics/{samTopic}/modules", Body(new { module_id = snapIds2[0] }))).StatusCode == HttpStatusCode.NotFound, "T31 Fremde private Module und eingefrorene Themenmodule lassen sich keinem Thema zuordnen");

        // geteiltes Modul darf nicht weiterverteilt werden
        await ImportModuleAsync(sam, "TE-Sam-Modul", 2, 0);
        var samModule = (await ModuleIdsAsync(sam))["TE-Sam-Modul"];
        await sam.PostAsync($"/api/sharing/modules/{samModule}/shares", Body(new { recipient_user_id = users["tina"] }));
        var shareToTopic = await tina.PostAsync($"/api/topics/{topicId}/modules", Body(new { module_id = samModule }));
        check(shareToTopic.StatusCode == HttpStatusCode.BadRequest && (await shareToTopic.Content.ReadAsStringAsync()).Contains("geteilte"), "T32 Mit mir geteilte Module anderer Eigentümer dürfen keinem Thema zugeordnet werden (keine Weitergabe fremder Inhalte)");

        // ─── Modulprüfung ───
        var nameToSnap = latestDetail!["modules"]!.AsArray().ToDictionary(m => S(m, "name"), m => S(m, "module_id"));
        async Task<(HttpResponseMessage Start, JsonNode? Json)> StartModuleAsync(HttpClient c, string snapModule, int? version = null)
        {
            var r = await c.PostAsync($"/api/topics/{topicId}/exams/module", Body(new { module_id = snapModule, version }));
            return (r, await J(r));
        }
        async Task<JsonNode?> SubmitAllAsync(HttpClient c, JsonNode start)
        {
            var answers = start["questions"]!.AsArray().Select(q => new { question_id = S(q, "question_id"), selected_indexes = new[] { 1 } }).ToList();
            return await J(await c.PostAsync("/api/quiz/submit", Body(new { module_id = S(start, "module_id"), session_id = S(start, "session_id"), answers })));
        }
        var (startA, startAJson) = await StartModuleAsync(leo, nameToSnap["TE-Entra"]);
        var rawStart = startAJson!.ToJsonString();
        check(startA.IsSuccessStatusCode && startAJson["questions"]!.AsArray().Count == 3 && !rawStart.Contains("correct_index") && !rawStart.Contains("explanation"), "T33 Modulprüfung startet mit der konfigurierten Fragenzahl; Lösungen und Erklärungen werden vor der Abgabe nicht ausgeliefert");
        var resultA = await SubmitAllAsync(leo, startAJson);
        check(resultA!["passed"]!.GetValue<bool>() && I(resultA, "percentage") == 100, "T34 Modulprüfung wird serverseitig ausgewertet und bestanden");
        check((await leo.PostAsync("/api/quiz/submit", Body(new { module_id = nameToSnap["TE-Entra"], answers = new object[] { new { question_id = "x", user_answer = "geraten" } } }))).StatusCode == HttpStatusCode.BadRequest, "T35 Themenmodule: Abgabe ohne gestartete Prüfungssitzung wird abgelehnt");
        check((await leo.PostAsync("/api/quiz/start", Body(new { module_id = nameToSnap["TE-Entra"], num_questions = 5 }))).StatusCode == HttpStatusCode.Forbidden, "T36 Modulweite Quizze in Themenmodulen starten nur über die Themenprüfung (403 auf /api/quiz/start)");
        var disabled = await StartModuleAsync(leo, nameToSnap["TE-Intune"]);
        check(disabled.Start.StatusCode == HttpStatusCode.Forbidden, "T37 Modul ohne aktive Prüfung kann keine Prüfung starten (403)");
        var attempts = (await J(await leo.GetAsync($"/api/topics/{topicId}/attempts")))!.AsArray();
        check(attempts.Count == 1 && S(attempts[0], "kind") == "module" && S(attempts[0], "passed") == "true" && I(attempts[0], "topic_version") == latest && S(attempts[0], "result_id").Length > 0,
            "T38 Prüfungsversuch speichert Benutzer, Themenversion, Ergebnis und Bestehen");

        // ─── Abschlussprüfung: erst nach den Voraussetzungen ───
        var blocked = await leo.PostAsync($"/api/topics/{topicId}/exams/final", Body(new { }));
        check(blocked.StatusCode == HttpStatusCode.Forbidden && (await blocked.Content.ReadAsStringAsync()).Contains("Schließe zuerst"), "T39 Abschlussprüfung ist gesperrt, solange Module nicht abgeschlossen sind (verständliche Meldung)");
        foreach (var m in new[] { "TE-Exchange", "TE-Teams" })
        {
            var (_, js) = await StartModuleAsync(leo, nameToSnap[m]);
            await SubmitAllAsync(leo, js!);
        }
        foreach (var snap in snapIds2)
            foreach (var card in (await J(await leo.GetAsync($"/api/cards/?module_id={snap}")))!.AsArray())
                await leo.PutAsync($"/api/cards/{S(card, "id")}/state", Body(new { is_checked = true, marked_review = false }));
        var progress = await J(await leo.GetAsync($"/api/topics/{topicId}/progress"));
        check(I(progress, "modules_total") == 4 && I(progress, "modules_completed") == 4 && I(progress, "module_exams_passed") == 3 && S(progress, "final_exam_available") == "true",
            $"T40 Lernfortschritt: 4 von 4 Modulen abgeschlossen, 3 Modulprüfungen bestanden, Abschlussprüfung verfügbar — ist: {S(progress, "modules_completed")}/{S(progress, "module_exams_passed")}/{S(progress, "final_exam_available")}");
        var finalStart = await J(await leo.PostAsync($"/api/topics/{topicId}/exams/final", Body(new { })));
        var finalQuestions = finalStart!["questions"]!.AsArray();
        check(finalQuestions.Count == 15 && S(finalStart, "module_id") == S(latestDetail, "exam_module_id") && S(latestDetail, "exam_module_id").Length > 0, "T41 Abschlussprüfung stellt die konfigurierten 15 Fragen aus mehreren Modulen und den Themenfragen");
        var finalResult = await SubmitAllAsync(leo, finalStart);
        check(finalResult!["passed"]!.GetValue<bool>(), "T42 Abschlussprüfung wird ausgewertet und bestanden");
        var attempts2 = (await J(await leo.GetAsync($"/api/topics/{topicId}/attempts")))!.AsArray();
        check(attempts2.Any(a => S(a, "kind") == "final" && S(a, "passed") == "true" && I(a, "topic_version") == latest), "T43 Abschlussprüfungsversuch ist mit Benutzer, Themenversion und Ergebnis gespeichert");
        check(S(await J(await leo.GetAsync($"/api/topics/{topicId}/progress")), "final_exam_passed") == "true", "T44 Fortschritt zeigt die bestandene Abschlussprüfung");

        // ─── Neue Version ändert alte Ergebnisse nicht ───
        var aCards = (await J(await tina.GetAsync($"/api/cards/?module_id={A}")))!.AsArray();
        var impChange = await tina.PostAsync("/api/import/cards", Body(new { overwrite_existing = true, cards = new object[] { new { id = S(aCards[0], "id"), module = "TE-Entra", category = "Kat", term = S(aCards[0], "term"), question = "Geänderte Frage?", definition = "Geänderte Definition" } } }));
        var changedCard = await J(await tina.GetAsync($"/api/cards/{S(aCards[0], "id")}"));
        check(impChange.IsSuccessStatusCode && S(changedCard, "definition") == "Geänderte Definition", $"T44a Ausgangslage: Karte des Originalmoduls wurde geändert ({(int)impChange.StatusCode}: {S(changedCard, "definition")})");
        var beforeAdopt = await J(await tina.GetAsync($"/api/topics/{topicId}"));
        check(!beforeAdopt!["unpublished_changes"]!.GetValue<bool>() && beforeAdopt["modules"]!.AsArray().First(m => S(m, "module_id") == A)["newer_available"]!.GetValue<bool>(),
            "T44b Geänderter Modulinhalt wird im Entwurf nur als „neuerer Stand verfügbar“ gemeldet, nicht stillschweigend übernommen (ADR 0030)");
        check((await tina.PostAsync($"/api/topics/{topicId}/modules/{A}/adopt", Body(new { }))).IsSuccessStatusCode, "T44c Neuer Modulstand wird ausdrücklich in den Entwurf übernommen");
        check((await J(await tina.GetAsync($"/api/topics/{topicId}")))!["unpublished_changes"]!.GetValue<bool>()
              && S(await J(await tina.GetAsync($"/api/topics/{topicId}")), "changed_module_names.0") == "TE-Entra", "T45 Geänderter Modulinhalt wird als unveröffentlichte Änderung erkannt (betroffenes Modul genannt)");
        var vNew = await J(await tina.PostAsync($"/api/topics/{topicId}/versions", Body(new { })));
        var newVersion = I(vNew, "version_number");
        var newDetail = await J(await tina.GetAsync($"/api/topics/{topicId}?version={newVersion}"));
        check(newVersion == latest + 1 && S(newDetail, "modules.1.module_id") != nameToSnap["TE-Entra"] && S(newDetail, "modules.2.module_id") == nameToSnap["TE-Exchange"], "T46 Nur das geänderte Modul erhält eine neue eingefrorene Kopie; unveränderte Module bleiben identisch");
        var attemptsAfter = (await J(await leo.GetAsync($"/api/topics/{topicId}/attempts")))!.AsArray();
        check(attemptsAfter.Count == attempts2.Count && attemptsAfter.All(a => I(a, "topic_version") == latest), "T47 Alte Prüfungsergebnisse bleiben ihrer ursprünglichen Themenversion zugeordnet");
        var progNew = await J(await leo.GetAsync($"/api/topics/{topicId}/progress"));
        check(I(progNew, "version") == newVersion && S(progNew, "newer_version_available") == "true"
              && progNew!["modules"]!.AsArray().First(m => S(m, "module_id") == S(newDetail, "modules.1.module_id"))["exam_passed"]!.GetValue<bool>() == false
              && progNew["modules"]!.AsArray().First(m => S(m, "module_id") == nameToSnap["TE-Exchange"])["exam_passed"]!.GetValue<bool>(), "T48 Neue Version: Hinweis „neuere Version verfügbar“; geändertes Modul erscheint wieder offen, unverändertes bleibt bestanden");

        // ─── Zuweisungen ───
        var assignTopic = S(await J(await tina.PostAsync("/api/topics/", Body(new { name = "TE Lernaufgabe" }))), "id");
        await tina.PostAsync($"/api/topics/{assignTopic}/modules", Body(new { module_id = A }));
        var adminId = S(await J(await admin.GetAsync("/api/me")), "id");
        async Task<HttpResponseMessage> AdminAssignAsync(string topic, string user, object? extra = null) =>
            await admin.PostAsync("/api/topic-admin/assignments", Body(new { topic_id = topic, user_id = user, version = (int?)null, mandatory = true }));
        check((await AdminAssignAsync(assignTopic, leoId)).StatusCode == HttpStatusCode.NotFound
              && !(await tina.PostAsync($"/api/topics/{assignTopic}/assignments", Body(new { user_id = leoId }))).IsSuccessStatusCode,
            "T49 Nicht veröffentlichtes (und nicht zuweisbares) Thema kann nicht zugewiesen werden; der Eigentümer hat keine Zuweisungsfunktion mehr (ADR 0029)");
        await tina.PostAsync($"/api/topics/{assignTopic}/versions", Body(new { }));
        check((await AdminAssignAsync(assignTopic, leoId)).StatusCode == HttpStatusCode.NotFound, "T49a Auch ein veröffentlichtes privates Thema ist für den Administrator nicht zuweisbar, solange es weder global noch mit ihm geteilt ist");
        await tina.PostAsync($"/api/topics/{assignTopic}/shares", Body(new { user_id = adminId }));
        var assignRes = await AdminAssignAsync(assignTopic, leoId);
        check(assignRes.StatusCode == HttpStatusCode.Created && S(await J(assignRes), "topic_version") == "1", "T50 Administrator weist das mit ihm geteilte Thema einem Benutzer zu (konkrete Version 1)");
        var leoAssigned = await J(await leo.GetAsync("/api/topics/"));
        check(leoAssigned!["assigned"]!.AsArray().Any(t => S(t, "topic.id") == assignTopic) && S(leoAssigned, "assigned.0.progress.modules_total") == "1", "T51 Zugewiesenes Thema erscheint bei „Mir zugewiesen“ mit Lernfortschritt");
        var assignments = await J(await admin.GetAsync($"/api/topic-admin/assignments?topic_id={assignTopic}"));
        check(assignments!.AsArray().Count == 1 && S(assignments, "0.user_id") == leoId && S(assignments, "0.progress.modules_total") == "1", "T52 Administrator sieht Zuweisungen mit Bearbeitungsstand");
        check((await admin.DeleteAsync($"/api/topic-admin/assignments/{S(assignments, "0.id")}")).StatusCode == HttpStatusCode.NoContent
              && (await leo.GetAsync($"/api/topics/{assignTopic}")).StatusCode == HttpStatusCode.NotFound, "T53 Zuweisung widerrufen: Zugriff endet");
        check((await tina.DeleteAsync($"/api/topics/{topicId}/shares/{samId}")).StatusCode == HttpStatusCode.NotFound
              && (await tina.DeleteAsync($"/api/topics/{topicId}/shares/{leoId}")).StatusCode == HttpStatusCode.NoContent
              && (await leo.GetAsync($"/api/topics/{topicId}")).StatusCode == HttpStatusCode.NotFound
              && (await J(await leo.GetAsync($"/api/topics/")))!["shared"]!.AsArray().Count == 0, "T54 Freigabe widerrufen: Zugriff endet sofort");
        check((await leo.GetAsync($"/api/topics/{topicId}/attempts")).StatusCode == HttpStatusCode.NotFound, "T55 Nach dem Widerruf ist das Thema für den Empfänger nicht mehr abrufbar");

        // ─── Zu kleiner Fragenpool ───
        await ImportModuleAsync(tina, "TE-Klein", 2, 2);
        var klein = (await ModuleIdsAsync(tina))["TE-Klein"];
        var kleinTopic = S(await J(await tina.PostAsync("/api/topics/", Body(new { name = "TE Kleiner Pool" }))), "id");
        await tina.PostAsync($"/api/topics/{kleinTopic}/modules", Body(new { module_id = klein }));
        await tina.PutAsync($"/api/topics/{kleinTopic}/exam-config", Body(new
        {
            module_exams = new Dictionary<string, object> { [klein] = ModuleExam(true, 5) },
            final_exam = new { enabled = true, question_count = 15, unlock = new { require_all_modules_completed = false, require_module_exams_passed = false }, single_choice = true, multiple_response = true, free_text = false },
        }));
        check((await J(await tina.GetAsync($"/api/topics/{kleinTopic}")))!["warnings"]!.AsArray().Count >= 2, "T56 Editor warnt vor zu kleinem Fragenpool (Modul- und Abschlussprüfung)");
        await tina.PostAsync($"/api/topics/{kleinTopic}/versions", Body(new { }));
        var kleinDetail = await J(await tina.GetAsync($"/api/topics/{kleinTopic}?version=1"));
        var kleinSnap = S(kleinDetail, "modules.0.module_id");
        var smallModule = await tina.PostAsync($"/api/topics/{kleinTopic}/exams/module", Body(new { module_id = kleinSnap }));
        var smallFinal = await tina.PostAsync($"/api/topics/{kleinTopic}/exams/final", Body(new { }));
        var smallModuleText = await smallModule.Content.ReadAsStringAsync();
        var smallFinalText = await smallFinal.Content.ReadAsStringAsync();
        check(smallModule.StatusCode == HttpStatusCode.BadRequest && smallModuleText.Contains("nur 2 passende Fragen") && smallFinal.StatusCode == HttpStatusCode.BadRequest && smallFinalText.Contains("nur 2 passende Fragen"),
            "T57 Prüfung mit zu kleinem Fragenpool startet nicht und nennt verständlich die verfügbare Anzahl (Modul- und Abschlussprüfung)");

        // ─── Globale Veröffentlichung ───
        await tina.PostAsync($"/api/topics/{topicId}/versions/{newVersion}/submit-global", Body(new { }));
        var pending = (await J(await admin.GetAsync("/api/admin/topics/pending")))!.AsArray();
        var pendingVersion = pending.FirstOrDefault(p => S(p, "topic_id") == topicId);
        check(pendingVersion is not null && S(pendingVersion, "global_status") == "pending_approval", "T58 Eingereichte globale Version erscheint in der Administration (pending_approval)");
        check((await sam.GetAsync($"/api/topics/{topicId}")).StatusCode == HttpStatusCode.NotFound && (await J(await sam.GetAsync("/api/topics/")))!["global"]!.AsArray().All(t => S(t, "topic.id") != topicId), "T59 Vor der Freigabe ist die globale Version nicht sichtbar");
        check((await tina.PostAsync($"/api/topics/{topicId}/versions/{newVersion}/submit-global", Body(new { }))).StatusCode == HttpStatusCode.Conflict, "T60 Eine bereits eingereichte Version kann nicht erneut eingereicht werden");
        check((await tina.GetAsync("/api/admin/topics/pending")).StatusCode == HttpStatusCode.Forbidden && (await tina.PostAsync($"/api/admin/topics/versions/{S(pendingVersion, "id")}/approve", Body(new { }))).StatusCode == HttpStatusCode.Forbidden, "T61 Globale Freigabe nur für Administratoren (serverseitige Rollenprüfung)");
        var review = await J(await admin.GetAsync($"/api/admin/topics/versions/{S(pendingVersion, "id")}"));
        var reviewCards = (await J(await admin.GetAsync($"/api/admin/topics/versions/{S(pendingVersion, "id")}/modules/{S(review, "modules.0.module_id")}/cards")))!.AsArray();
        check(S(review, "topic.name").Length > 0 && reviewCards.Count == 16 && (await admin.GetAsync($"/api/admin/topics/versions/{S(pendingVersion, "id")}/modules/{A}/cards")).StatusCode == HttpStatusCode.NotFound, "T62 Administrator prüft die eingefrorene Version — das private Originalmodul bleibt unzugänglich");
        var approvals = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => admin.PostAsync($"/api/admin/topics/versions/{S(pendingVersion, "id")}/approve", Body(new { }))));
        check(approvals.Count(r => r.StatusCode == HttpStatusCode.NoContent) == 1 && approvals.Count(r => r.StatusCode == HttpStatusCode.Conflict) == 3, "T63 Gleichzeitige Freigaben derselben Einreichung: genau eine gelingt (atomar)");
        var samGlobal = await J(await sam.GetAsync("/api/topics/"));
        check(samGlobal!["global"]!.AsArray().Any(t => S(t, "topic.id") == topicId && I(t, "version") == newVersion) && I(await J(await sam.GetAsync($"/api/topics/{topicId}")), "version") == newVersion, "T64 Nach der Freigabe ist die globale Version für alle sichtbar");
        var samStart = await sam.PostAsync($"/api/topics/{topicId}/exams/module", Body(new { module_id = S(newDetail, "modules.2.module_id") }));
        check(samStart.IsSuccessStatusCode, "T65 Jeder Benutzer kann eine globale Themenversion lernen und prüfen");

        // Version 5 einreichen und ablehnen → globale Version bleibt aktiv
        var addQ = await tina.PostAsync($"/api/topics/{topicId}/questions", Body(new { type = "single_choice", question = "Neue Themenfrage?", options = new[] { "x", "y", "z" }, correct_indexes = new[] { 1 }, explanation = "y" }));
        var pubGlobal = await tina.PostAsync($"/api/topics/{topicId}/versions", Body(new { submit_global = true }));
        var next = I(await J(pubGlobal), "version_number");
        check(addQ.StatusCode == HttpStatusCode.Created && pubGlobal.StatusCode == HttpStatusCode.Created && next == newVersion + 1, $"T65a Neue Themenfrage + Veröffentlichung mit globaler Einreichung → Version {newVersion + 1} ({(int)addQ.StatusCode}/{(int)pubGlobal.StatusCode}: {await pubGlobal.Content.ReadAsStringAsync()})");
        var pending2 = (await J(await admin.GetAsync("/api/admin/topics/pending")))!.AsArray().First(p => S(p, "topic_id") == topicId);
        check((await admin.PostAsync($"/api/admin/topics/versions/{S(pending2, "id")}/reject", Body(new { reason = "Inhalt unvollständig" }))).StatusCode == HttpStatusCode.NoContent, "T66 Administrator lehnt die neue globale Version ab");
        check(I(await J(await sam.GetAsync($"/api/topics/{topicId}")), "version") == newVersion, "T67 Nach Ablehnung bleibt die bisherige globale Version aktiv");
        var history = (await J(await tina.GetAsync($"/api/topics/{topicId}/versions")))!.AsArray();
        check(S(history.First(v => I(v, "version_number") == next), "global_status") == "rejected" && S(history.First(v => I(v, "version_number") == next), "decision_reason") == "Inhalt unvollständig" && S(history.First(v => I(v, "version_number") == newVersion), "global_status") == "active", "T68 Veröffentlichungsverlauf zeigt aktive und abgelehnte Version mit Begründung");
        check((await J(await admin.GetAsync("/api/admin/topics/history")))!.AsArray().Count >= 2, "T69 Administration: Veröffentlichungsverlauf");
        var dup = await J(await tina.PostAsync($"/api/topics/{topicId}/duplicate", Body(new { })));
        check(S(dup, "name").EndsWith("(Kopie)") && (await J(await tina.GetAsync($"/api/topics/{S(dup, "id")}")))!["modules"]!.AsArray().Count == 4 && (await tina.GetAsync($"/api/topics/{S(dup, "id")}/shares")).IsSuccessStatusCode
              && (await J(await tina.GetAsync($"/api/topics/{S(dup, "id")}/shares")))!.AsArray().Count == 0, "T70 Thema duplizieren: neuer Entwurf mit denselben Modulen, ohne Freigaben, Zuweisungen und Versionen");
        await tina.PostAsync($"/api/topics/{second}/archive", Body(new { archived = true }));
        check((await J(await tina.GetAsync("/api/topics/")))!["own"]!.AsArray().All(t => S(t, "topic.id") != second) && (await tina.PutAsync($"/api/topics/{second}", Body(new { name = "x" }))).StatusCode == HttpStatusCode.Conflict
              && (await J(await tina.GetAsync("/api/topics/?include_archived=true")))!["own"]!.AsArray().Any(t => S(t, "topic.id") == second), "T71 Archivieren blendet das Thema aus (nichts wird gelöscht); archivierte Themen sind nicht bearbeitbar");

        // ─── MCP-Token: nur lesend ───
        var tokenRes = await J(await tina.PostAsync("/api/mcp/links/", Body(new { name = "TE-Test", days = 30 })));
        var mcp = newClient();
        mcp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", S(tokenRes, "token"));
        var mcpList = await mcp.GetAsync("/api/topics/");
        check(mcpList.IsSuccessStatusCode && (await J(mcpList))!["own"]!.AsArray().Any(t => S(t, "topic.id") == topicId), "T72 MCP-Token darf eigene Themen lesen");
        check((await mcp.GetAsync($"/api/topics/{topicId}")).IsSuccessStatusCode && (await mcp.GetAsync($"/api/topics/{topicId}/versions")).IsSuccessStatusCode, "T73 MCP-Token darf Themendetails und Versionen lesen");
        check((await mcp.PostAsync("/api/topics/", Body(new { name = "MCP-Thema" }))).StatusCode == HttpStatusCode.Forbidden
              && (await mcp.PostAsync($"/api/topics/{topicId}/versions", Body(new { }))).StatusCode == HttpStatusCode.Forbidden
              && (await mcp.GetAsync($"/api/topics/{topicId}/shares")).StatusCode == HttpStatusCode.Forbidden
              && !(await mcp.GetAsync($"/api/topics/{topicId}/assignments")).IsSuccessStatusCode
              && (await mcp.GetAsync("/api/admin/topics/pending")).StatusCode == HttpStatusCode.Forbidden
              && (await mcp.PostAsync($"/api/topics/{topicId}/exams/final", Body(new { }))).StatusCode == HttpStatusCode.Forbidden, "T74 MCP-Token: keine Schreib-, Freigabe-, Zuweisungs-, Prüfungs- oder Administrationsaktionen");
        var mcpSam = newClient();
        var samToken = await J(await sam.PostAsync("/api/mcp/links/", Body(new { name = "TE-Sam", days = 30 })));
        mcpSam.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", S(samToken, "token"));
        check((await mcpSam.GetAsync($"/api/topics/{second}")).StatusCode == HttpStatusCode.NotFound && (await J(await mcpSam.GetAsync("/api/topics/")))!["own"]!.AsArray().All(t => S(t, "topic.id") != topicId), "T75 MCP-Token eines fremden Benutzers sieht keine privaten Themen");

        // ─── MCP-Tools (nur lesend, benutzergebunden) ───
        async Task<string> McpToolAsync(string token, string tool, object args)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, mcpUrl + "/mcp") { Content = Body(new { jsonrpc = "2.0", id = 5, method = "tools/call", @params = new { name = tool, arguments = args } }) };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var res = await newClient().SendAsync(req);
            if (!res.IsSuccessStatusCode) return "HTTP " + (int)res.StatusCode;
            return S(await J(res), "result.content.0.text");
        }
        var tinaTokenForMcp = S(tokenRes, "token");
        var mcpTopics = await McpToolAsync(tinaTokenForMcp, "list_topics", new { });
        var mcpTopic = await McpToolAsync(tinaTokenForMcp, "get_topic", new { topic_id = topicId });
        var mcpExam = await McpToolAsync(tinaTokenForMcp, "get_topic_exam_structure", new { topic_id = topicId });
        check(mcpTopics.Contains("TE Microsoft 365 Administrator") && mcpTopics.Contains("[own") && mcpTopic.Contains("TE-Entra") && mcpTopic.Contains("TE-Intune") && mcpExam.Contains("Final exam: 15 questions") && mcpExam.Contains("Module exam TE-Entra: 3 questions"),
            "T78 MCP-Tools list_topics, get_topic (enthaltene Module) und get_topic_exam_structure liefern die Themenstruktur des angemeldeten Benutzers");
        var samMcp = await McpToolAsync(S(samToken, "token"), "get_topic", new { topic_id = second });
        var samList = await McpToolAsync(S(samToken, "token"), "list_topics", new { });
        check(samMcp.StartsWith("API error 404") && !samList.Contains("TE Zweites Thema") && !samMcp.Contains("TE-Entra"), "T79 MCP: ein fremder Benutzer erhält weder Details noch Listeneinträge privater Themen");
        var mcpWrite = await McpToolAsync(tinaTokenForMcp, "approve_topic", new { topic_id = topicId });
        check(!mcpWrite.Contains("approved") && !mcpWrite.Contains("global"), "T80 MCP: keine Administrationswerkzeuge für globale Themenfreigaben (Themen-JSON-Werkzeuge siehe ADR 0028)");

        // ─── Audit ───
        var audit = (await admin.GetStringAsync("/api/admin/audit?limit=500"));
        check(new[] { "topic.create", "topic.publish", "topic.share.grant", "topic.share.revoke", "topic.assign", "topic.unassign", "topic.global.submit", "topic.global.approve", "topic.global.reject", "topic.module.add" }.All(audit.Contains),
            "T76 Freigaben, Zuweisungen, Veröffentlichungen und globale Entscheidungen werden auditiert");
        check(!audit.Contains(S(tokenRes, "token")), "T77 Audit enthält keine Tokens");
    }

    private static async Task<string?> SearchAsync(HttpClient asker, string unused, string query)
    {
        var arr = (await J(await asker.GetAsync($"/api/topics/users?q={query}")))?.AsArray();
        return arr is null || arr.Count == 0 ? null : S(arr[0], "id");
    }
}
