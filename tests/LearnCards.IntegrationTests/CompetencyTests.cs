using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// ADR 0031: Kompetenzmodell (Lernziele, Kompetenzen, Kartenzuordnung), Nachweis nur bei ausreichender Prüfung, unveränderlicher Prüfungssnapshot,
/// Modulzertifikat als Open Badge 3.0 mit Alignment/ResultDescription/Result/Evidence, Datenschutz und Kompatibilität ohne Kompetenzmodell.
/// </summary>
public static class CompetencyTests
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

    public static async Task RunAsync(Func<string?, Task<HttpClient>> login, Func<HttpClient> newClient, Action<bool, string> check)
    {
        Console.WriteLine("\nKompetenzmodell, Prüfungssnapshot und Open Badge mit Kompetenznachweisen (ADR 0031)");
        var o = await login("cp-owner"); var x = await login("cp-other");
        object Card(string module, string term, string[] comps) => new
        {
            module, category = "K", term, question = "Frage " + term + "?", definition = "Definition " + term, competencies = comps,
            quiz = new[] { new { type = "single_choice", question = "Quiz " + term + "?", options = new[] { "falsch1", "richtig", "falsch2", "falsch3" }, correct_index = 1, explanation = "weil" } },
        };
        var cards = new List<object>();
        foreach (var t in new[] { "N1", "N2", "N3" }) cards.Add(Card("CP-Modul", t, new[] { "net" }));
        foreach (var t in new[] { "S1", "S2" }) cards.Add(Card("CP-Modul", t, new[] { "sec", "extra" }));
        await o.PostAsync("/api/import/cards", Body(new { cards }));
        var modId = S((await J(await o.GetAsync("/api/modules/")))!.AsArray().Single(m => S(m, "name") == "CP-Modul"), "id");
        var model = new
        {
            learning_objectives = new[] { new { id = "o1", text = "Netzwerke verstehen" }, new { id = "o2", text = "Sicherheit anwenden" } },
            competencies = new[]
            {
                new { id = "net", objective_id = "o1", name = "Netzwerke konfigurieren", description = "Grundlagen", pass_percent = 60, min_questions = 2 },
                new { id = "sec", objective_id = "o2", name = "Sicherheit anwenden", description = "", pass_percent = 60, min_questions = 2 },
                new { id = "extra", objective_id = "o2", name = "Erweiterte Sicherheit", description = "", pass_percent = 60, min_questions = 3 },
            },
        };

        // ─── Modell: Validierung und Rechte ───
        check((await o.PutAsync($"/api/modules/{modId}/competencies", Body(new { learning_objectives = new[] { new { id = "Gross ID", text = "x" } }, competencies = Array.Empty<object>() }))).StatusCode == HttpStatusCode.BadRequest
              && (await o.PutAsync($"/api/modules/{modId}/competencies", Body(new { learning_objectives = Array.Empty<object>(), competencies = new[] { new { id = "k", objective_id = "fehlt", name = "K", description = "", pass_percent = 60, min_questions = 1 } } }))).StatusCode == HttpStatusCode.BadRequest
              && (await o.PutAsync($"/api/modules/{modId}/competencies", Body(new { learning_objectives = Array.Empty<object>(), competencies = new[] { new { id = "k", name = "K", description = "", pass_percent = 0, min_questions = 1 } } }))).StatusCode == HttpStatusCode.BadRequest,
            "K1 ungültige Kompetenz-IDs, unbekannte Lernziele und ungültige Grenzen werden abgewiesen");
        check((await x.PutAsync($"/api/modules/{modId}/competencies", Body(model))).StatusCode == HttpStatusCode.NotFound && (await x.GetAsync($"/api/modules/{modId}/competencies")).StatusCode == HttpStatusCode.NotFound, "K2 fremder Benutzer kann Lernziele/Kompetenzen weder lesen noch ändern (404)");
        check((await o.PutAsync($"/api/modules/{modId}/competencies", Body(model))).IsSuccessStatusCode && S(await J(await o.GetAsync($"/api/modules/{modId}/competencies")), "competencies.0.id") == "net", "K3 Eigentümer legt Lernziele und Kompetenzen fest");
        var withComps = (await J(await o.GetAsync($"/api/cards/?module_id={modId}")))!.AsArray();
        check(withComps.All(c => c!["competencies"]!.AsArray().Count > 0), "K4 Karten tragen ihre Kompetenz-IDs (Import, Lesen)");
        var badCard = await o.PutAsync($"/api/cards/{S(withComps[0], "id")}", Body(new { module = "CP-Modul", category = "K", term = "N1", question = "Q?", definition = "D", competencies = new[] { "UNGÜLTIG!" } }));
        var keepCard = await o.PutAsync($"/api/cards/{S(withComps[0], "id")}", Body(new { module = "CP-Modul", category = "K", term = "N1", question = "Frage N1?", definition = "Definition N1", quiz = new[] { new { type = "single_choice", question = "Quiz N1?", options = new[] { "falsch1", "richtig", "falsch2", "falsch3" }, correct_index = 1, explanation = "weil" } } }));
        check(badCard.StatusCode == HttpStatusCode.BadRequest && S(await J(keepCard), "competencies.0") == "net", "K5 ungültige Kompetenz-ID wird abgewiesen; fehlendes competencies-Feld behält die Zuordnung");

        // ─── Prüfung: 3 Netzwerkfragen richtig, 2 Sicherheitsfragen falsch → 60 % bestanden ───
        foreach (var c in withComps) await o.PutAsync($"/api/cards/{S(c, "id")}/state", Body(new { is_checked = true, marked_review = false }));   // das Modulquiz prüft gelernte Karten
        var start = await J(await o.PostAsync("/api/quiz/start", Body(new { module_id = modId, category = "", card_id = "", num_questions = 5 })));
        var qs = start!["questions"]!.AsArray();
        object[] Answers(Func<string, bool> right) => qs.Select(q =>
        {
            var opts = q!["options"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();
            var term = S(q, "question");
            var pick = right(term) ? opts.IndexOf("richtig") : opts.IndexOf("falsch1");
            return (object)new { question = term, question_id = S(q, "question_id"), source_card_id = S(q, "source_card_id"), user_answer = "", selected_indexes = new[] { pick } };
        }).ToArray();
        var submit = await o.PostAsync("/api/quiz/submit", Body(new { module_id = modId, category = "", card_id = "", session_id = S(start, "session_id"), answers = Answers(t => t.Contains("N")) }));
        var res = await J(submit);
        var certId = S(res, "certificate_id");
        check(qs.Count == 5 && S(res, "passed") == "true" && certId.Length > 0, $"P1 Modulquiz mit 5 Fragen bestanden (60 %), Zertifikat ausgestellt ({S(res, "percentage")} %)");

        var evidenceRes = await newClient().GetAsync($"/api/public/certificates/{certId}/evidence");
        var evidenceText = await evidenceRes.Content.ReadAsStringAsync();
        var snap = JsonNode.Parse(evidenceText)!;
        var byId = snap["competencies"]!.AsArray().ToDictionary(c => S(c, "id"), c => c!);
        check(evidenceRes.IsSuccessStatusCode && byId["net"]["status"]!.GetValue<string>() == "demonstrated" && byId["sec"]["status"]!.GetValue<string>() == "not_demonstrated" && byId["extra"]["status"]!.GetValue<string>() == "insufficiently_tested",
            "P2 Nachweis je Kompetenz: nachgewiesen / nicht nachgewiesen / nicht ausreichend geprüft — „Modul bestanden“ ersetzt keinen Kompetenznachweis");
        check(S(byId["net"], "questions_asked") == "3" && S(byId["net"], "percent") == "100" && S(byId["sec"], "percent") == "0" && S(byId["extra"], "questions_asked") == "2" && S(byId["extra"], "min_questions") == "3"
              && S(snap, "module.version").Length > 0 && int.Parse(S(snap, "module.version")) >= 1 && S(snap, "exam.pass_percent") == "60" && S(snap, "exam.criteria_version").Length == 64 && S(snap, "objectives.0.id") == "o1",
            "P3 Snapshot enthält geprüfte Fragen, Ergebnis, Modulversion, Bestehensgrenze, Lernziele und Bewertungskriterien-Version");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidenceText))).ToLowerInvariant();
        var certDto = await J(await o.GetAsync($"/api/certificates/{certId}"));
        var badge = await J(await o.GetAsync($"/api/certificates/{certId}/badge"));
        check(S(badge, "evidence.0.description").Contains(hash) && S(badge, "evidence.0.id").EndsWith($"/api/public/certificates/{certId}/evidence"), "P4 Credential verweist auf den Snapshot (Evidence-URL) und nennt dessen SHA-256 — der Hash entspricht dem ausgelieferten Snapshot");
        check(!evidenceText.Contains("richtig") && !evidenceText.Contains("falsch1") && !evidenceText.Contains("Quiz N") && !evidenceText.Contains("cp-owner", StringComparison.OrdinalIgnoreCase) && !evidenceText.Contains("user_id") && !evidenceText.Contains("answer"),
            "P5 Snapshot enthält weder Antworten noch Fragetexte noch Lernendendaten");

        // ─── Open Badge 3.0: Struktur (aus den offiziellen JSON-Schemas abgeleitete Pflichtfelder und Enums) ───
        var ach = badge!["credentialSubject"]!["achievement"]!;
        var ctx = badge["@context"]!.AsArray().Select(v => v is JsonValue ? v.GetValue<string>() : "{local}").ToList();
        bool ObNormative()
        {
            if (ctx[0] != "https://www.w3.org/ns/credentials/v2" || !ctx.Contains("https://purl.imsglobal.org/spec/ob/v3p0/context-3.0.3.json")) return false;
            foreach (var req in new[] { "id", "type", "credentialSubject", "issuer", "validFrom" }) if (badge[req] is null) return false;
            var types = badge["type"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();
            if (!types.Contains("VerifiableCredential") || !(types.Contains("OpenBadgeCredential") || types.Contains("AchievementCredential"))) return false;
            foreach (var req in new[] { "id", "type", "criteria", "description", "name" }) if (ach[req] is null) return false;
            foreach (var al in ach["alignment"]!.AsArray())
            {
                foreach (var req in new[] { "type", "targetName", "targetUrl" }) if (al![req] is null) return false;
                if (S(al, "targetType") != "ceasn:Competency") return false;
            }
            var rdIds = new HashSet<string>();
            foreach (var rd in ach["resultDescription"]!.AsArray())
            {
                foreach (var req in new[] { "id", "type", "name", "resultType" }) if (rd![req] is null) return false;
                if (!new[] { "GradePointAverage", "LetterGrade", "Percent", "PerformanceLevel", "PredictedScore", "RawScore", "Result", "RubricCriterion", "RubricCriterionLevel", "RubricScore", "ScaledScore", "Status" }.Contains(S(rd, "resultType"))) return false;
                rdIds.Add(S(rd, "id"));
            }
            foreach (var r in badge["credentialSubject"]!["result"]!.AsArray())
            {
                if (r!["type"] is null) return false;
                if (r["status"] is { } st && !new[] { "Completed", "Enrolled", "Failed", "InProgress", "OnHold", "Provisional", "Withdrew" }.Contains(st.GetValue<string>())) return false;
                if (r["resultDescription"] is { } rid && !rdIds.Contains(rid.GetValue<string>())) return false;   // jede Result verweist auf eine vorhandene ResultDescription
            }
            foreach (var ev in badge["evidence"]!.AsArray()) if (ev!["type"] is null) return false;
            return true;
        }
        check(ObNormative(), "O1 Open Badge 3.0: Pflichtfelder, Kontext, Alignment (ceasn:Competency), ResultDescription/Result-Verweise, Evidence entsprechen den offiziellen Schemas");
        check(ach["alignment"]!.AsArray().Count == 1 && S(ach, "alignment.0.targetCode") == "net" && ach["resultDescription"]!.AsArray().Count == 1 && badge["credentialSubject"]!["result"]!.AsArray().Count == 2
              && !badge.ToJsonString().Contains("Sicherheit anwenden") && !badge.ToJsonString().Contains("Erweiterte Sicherheit"),
            "O2 bescheinigt wird nur die nachgewiesene Kompetenz; nicht nachgewiesene und nicht ausreichend geprüfte tauchen im Credential nicht als erreicht auf");
        check(S(ach, "version") == S(certDto, "module_version") && S(ach, "criteria.narrative").Contains("Netzwerke konfigurieren") && S(ach, "criteria.id").EndsWith("/criteria"), "O3 Modulversion, Bewertungskriterien (Narrative, Kriterien-URL) stehen im Achievement");
        var verifyRes = await newClient().PostAsync("/api/public/verify", Raw((await o.GetAsync($"/api/certificates/{certId}/badge")).Content.ReadAsStringAsync().Result));
        var verify = await J(verifyRes);
        check(S(verify, "valid") == "true" && S(verify, "signature_valid") == "true" && S(verify, "known") == "true", "O4 Signatur und Registrierung des Badges sind gültig");

        // ─── Unveränderlichkeit ───
        var versions = await J(await o.GetAsync($"/api/modules/{modId}/versions"));
        var snapModule = S(versions, "versions.0.snapshot_module_id");
        var frozen = await J(await o.GetAsync($"/api/modules/{snapModule}/competencies"));
        await o.PutAsync($"/api/modules/{modId}/competencies", Body(new { learning_objectives = Array.Empty<object>(), competencies = new[] { new { id = "net", name = "Umbenannt", description = "", pass_percent = 99, min_questions = 1 } } }));
        var again = await (await newClient().GetAsync($"/api/public/certificates/{certId}/evidence")).Content.ReadAsStringAsync();
        var frozenAgain = await J(await o.GetAsync($"/api/modules/{snapModule}/competencies"));
        var badgeAgain = await J(await o.GetAsync($"/api/certificates/{certId}/badge"));
        check(again == evidenceText && S(frozen, "competencies.0.name") == "Netzwerke konfigurieren" && frozen!.ToJsonString() == frozenAgain!.ToJsonString() && S(badgeAgain, "credentialSubject.achievement.alignment.0.targetName") == "Netzwerke konfigurieren",
            "U1 spätere Änderung des Kompetenzmodells verändert weder Snapshot noch Credential noch die eingefrorene Modulversion");
        check((await o.GetAsync($"/api/modules/{modId}/versions")).IsSuccessStatusCode && S(await J(await o.GetAsync($"/api/modules/{modId}/versions")), "has_unpublished_changes") == "true", "U2 geändertes Kompetenzmodell gilt als unveröffentlichte Änderung des Moduls (Teil der Modulversion)");

        // ─── Export/Import und Klonen ───
        await o.PutAsync($"/api/modules/{modId}/competencies", Body(model));
        var export = await J(await o.GetAsync($"/api/modules/{modId}/export"));
        check(S(export, "competencies.2.id") == "extra" && S(export, "learning_objectives.1.id") == "o2" && export!["cards"]!.AsArray().Any(c => S(c, "competencies.0") == "sec"), "E1 Modul-Export enthält Lernziele, Kompetenzen und die Kartenzuordnung");
        export!["name"] = "CP-Import"; export.AsObject().Remove("reference_id");
        var imp = await o.PostAsync("/api/modules/import", Raw(export.ToJsonString()));
        var impId = S(await J(imp), "id");
        check(imp.IsSuccessStatusCode && S(await J(await o.GetAsync($"/api/modules/{impId}/competencies")), "competencies.1.id") == "sec" && (await J(await o.GetAsync($"/api/cards/?module_id={impId}")))!.AsArray().Count(c => S(c, "competencies.0") == "net") == 3,
            "E2 Import stellt Lernziele, Kompetenzen und Kartenzuordnung wieder her");
        var bad = JsonNode.Parse(export.ToJsonString())!; bad["name"] = "CP-Bad"; bad["competencies"]![0]!["objective_id"] = "unbekannt";
        check((await o.PostAsync("/api/modules/import", Raw(bad.ToJsonString()))).StatusCode == HttpStatusCode.BadRequest && (await J(await o.GetAsync("/api/modules/")))!.AsArray().All(m => S(m, "name") != "CP-Bad"), "E3 ungültiges Kompetenzmodell im Import: 400, nichts wird angelegt");

        // ─── Kompatibilität: Modul ohne Kompetenzmodell ───
        await o.PostAsync("/api/import/cards", Body(new { cards = new object[] { new { module = "CP-Ohne", category = "K", term = "O1", question = "Q?", definition = "D", quiz = new[] { new { type = "single_choice", question = "Quiz O1?", options = new[] { "falsch1", "richtig", "falsch2", "falsch3" }, correct_index = 1, explanation = "w" } } } } }));
        var plain = S((await J(await o.GetAsync("/api/modules/")))!.AsArray().Single(m => S(m, "name") == "CP-Ohne"), "id");
        foreach (var c in (await J(await o.GetAsync($"/api/cards/?module_id={plain}")))!.AsArray()) await o.PutAsync($"/api/cards/{S(c, "id")}/state", Body(new { is_checked = true, marked_review = false }));
        var st2 = await J(await o.PostAsync("/api/quiz/start", Body(new { module_id = plain, category = "", card_id = "", num_questions = 1 })));
        var q2 = st2!["questions"]![0]!; var opts2 = q2["options"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();
        var r2 = await J(await o.PostAsync("/api/quiz/submit", Body(new { module_id = plain, category = "", card_id = "", session_id = S(st2, "session_id"), answers = new[] { new { question = S(q2, "question"), question_id = S(q2, "question_id"), source_card_id = S(q2, "source_card_id"), user_answer = "", selected_indexes = new[] { opts2.IndexOf("richtig") } } } })));
        var cert2 = S(r2, "certificate_id");
        var badge2 = await J(await o.GetAsync($"/api/certificates/{cert2}/badge"));
        var ev2 = JsonNode.Parse(await newClient().GetStringAsync($"/api/public/certificates/{cert2}/evidence"));
        check(cert2.Length > 0 && badge2!["credentialSubject"]!["achievement"]!["alignment"] is null && ev2!["competencies"]!.AsArray().Count == 0 && S(await J(await newClient().PostAsync("/api/public/verify", Raw(badge2.ToJsonString()))), "valid") == "true",
            "C1 Modul ohne Kompetenzmodell bleibt funktionsfähig: Zertifikat und Snapshot ohne Kompetenzen, gültiges Badge");
        check((await newClient().GetAsync($"/api/public/certificates/{Guid.NewGuid()}/evidence")).StatusCode == HttpStatusCode.NotFound, "C2 unbekannter Nachweis: 404");
    }
}
