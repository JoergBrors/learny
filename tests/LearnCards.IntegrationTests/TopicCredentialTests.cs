using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LearnCards.Web.Services.Certificates;

/// <summary>
/// ADR 0032: Themenabschluss — Modulzertifikate in Themen, Abschlussbedingungen, Master-Open-Badge (Open Badges 3.0) und signiertes CLR 2.0 (Struktur gegen die aus den
/// offiziellen 1EdTech-JSON-Schemas abgeleiteten Pflichtfelder), Signaturen, Manipulation, Widerruf, Idempotenz, Abschlusszeitpunkt und Datenschutz.
/// </summary>
public static class TopicCredentialTests
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

    /// <summary>Pflichtfelder und Enums der Open-Badges-3.0- und CLR-2.0-JSON-Schemas (abgeleitet aus den offiziellen Schemadateien; kein vollständiger Schema-Validator).</summary>
    public static List<string> CheckVc(JsonNode vc, string kind)
    {
        var errors = new List<string>();
        foreach (var req in new[] { "@context", "type", "id", "credentialSubject", "issuer", "validFrom" }) if (vc[req] is null) errors.Add($"{kind}: {req} fehlt");
        var types = vc["type"]?.AsArray().Select(v => v!.GetValue<string>()).ToList() ?? new();
        if (!types.Contains("VerifiableCredential")) errors.Add($"{kind}: type ohne VerifiableCredential");
        var ctx = vc["@context"]?.AsArray().Select(v => v is JsonValue ? v.GetValue<string>() : "{local}").ToList() ?? new();
        if (ctx.Count == 0 || ctx[0] != "https://www.w3.org/ns/credentials/v2") errors.Add($"{kind}: erster Kontext muss VC 2.0 sein");
        if (vc["credentialStatus"]?["type"]?.ToString() == "1EdTechRevocationList" && !(vc["@context"]!.AsArray().Any(c => c is JsonObject o && o["1EdTechRevocationList"] is not null))) errors.Add($"{kind}: lokaler Kontext für 1EdTechRevocationList fehlt (sonst „Undefined JSON-LD term“ im 1EdTech-Verifier)");
        if (kind == "clr")
        {
            if (!types.Contains("ClrCredential")) errors.Add("clr: type ohne ClrCredential");
            if (ctx.Count < 3 || !Regex.IsMatch(ctx[1], @"purl\.imsglobal\.org/spec/clr/v2p0/context.*\.json") || !Regex.IsMatch(ctx[2], @"purl\.imsglobal\.org/spec/ob/v3p0/context.*\.json")) errors.Add("clr: Kontexte (VC, CLR 2.0, OB 3.0) in dieser Reihenfolge erforderlich");
            if (vc["name"] is null) errors.Add("clr: name fehlt");
            var sub = vc["credentialSubject"]!;
            if (!(sub["type"]?.ToJsonString().Contains("ClrSubject") ?? false)) errors.Add("clr: credentialSubject.type ClrSubject fehlt");
            if (sub["verifiableCredential"] is not JsonArray { Count: > 0 } vcs) errors.Add("clr: verifiableCredential (mindestens 1) fehlt");
            else foreach (var innerRaw in vcs)
            {
                // Neues Profil (ADR 0038): eingebettete Credentials sind kompakte JWS; ihr Inhalt wird wie ein Objekt geprüft
                var inner = innerRaw is JsonValue jv && jv.TryGetValue<string>(out var jws) ? BadgeSigner.CredentialFromJws(jws) : innerRaw;
                foreach (var req in new[] { "@context", "type", "issuer", "validFrom", "credentialSubject" }) if (inner![req] is null) errors.Add($"clr: eingebettetes Credential ohne {req}");
            }
            foreach (var ach in sub["achievement"]?.AsArray() ?? new JsonArray())
                foreach (var req in new[] { "id", "type", "criteria", "description", "name" }) if (ach![req] is null) errors.Add($"clr: Achievement ohne {req}");
            foreach (var a in sub["association"]?.AsArray() ?? new JsonArray())
            {
                foreach (var req in new[] { "type", "associationType", "sourceId", "targetId" }) if (a![req] is null) errors.Add($"clr: Association ohne {req}");
                if (!new[] { "exactMatchOf", "isChildOf", "isParentOf", "isPartOf", "isPeerOf", "isRelatedTo", "precedes", "replacedBy" }.Contains(S(a, "associationType"))) errors.Add("clr: ungültiger associationType");
            }
        }
        else
        {
            if (!(types.Contains("OpenBadgeCredential") || types.Contains("AchievementCredential"))) errors.Add($"{kind}: type ohne OpenBadgeCredential");
            if (!ctx.Contains("https://purl.imsglobal.org/spec/ob/v3p0/context-3.0.3.json")) errors.Add($"{kind}: OB-3.0-Kontext fehlt");
            var ach = vc["credentialSubject"]?["achievement"];
            if (ach is null) errors.Add($"{kind}: achievement fehlt");
            else foreach (var req in new[] { "id", "type", "criteria", "description", "name" }) if (ach[req] is null) errors.Add($"{kind}: Achievement ohne {req}");
            foreach (var idObj in vc["credentialSubject"]?["identifier"]?.AsArray() ?? new JsonArray())
                foreach (var req in new[] { "type", "hashed", "identityHash", "identityType" }) if (idObj![req] is null) errors.Add($"{kind}: IdentityObject ohne {req}");
            if (ach?["achievementType"] is { } at && !new[] { "Achievement", "ApprenticeshipCertificate", "Assessment", "Assignment", "AssociateDegree", "Award", "Badge", "BachelorDegree", "Certificate", "CertificateOfCompletion", "Certification", "CommunityService", "Competency", "Course", "CoCurricular", "Degree", "Diploma", "DoctoralDegree", "Fieldwork", "GeneralEducationDevelopment", "JourneymanCertificate", "LearningProgram", "License", "Membership", "ProfessionalDoctorate", "QualityAssuranceCredential", "MasterCertificate", "MasterDegree", "MicroCredential", "ResearchDoctorate", "SecondarySchoolDiploma" }.Contains(at.GetValue<string>())) errors.Add($"{kind}: achievementType nicht im Schema");
            foreach (var ev in vc["evidence"]?.AsArray() ?? new JsonArray()) if (ev!["type"] is null) errors.Add($"{kind}: Evidence ohne type");
        }
        return errors;
    }

    public static async Task RunAsync(Func<string?, Task<HttpClient>> login, Func<HttpClient> newClient, HttpClient admin, Action<bool, string> check)
    {
        Console.WriteLine("\nThemenabschluss: Modulzertifikate, Master-Open-Badge und CLR 2.0 (ADR 0032)");
        var o = await login("tcr-owner"); var lea = await login("tcr-lea"); var oth = await login("tcr-other");
        var leaId = S(await J(await lea.GetAsync("/api/me")), "id");
        string Mod(string name, bool comps) => new JsonObject
        {
            ["format"] = "learny-module", ["schema_version"] = 1, ["name"] = name,
            ["learning_objectives"] = comps ? new JsonArray(new JsonObject { ["id"] = "o1", ["text"] = "Grundlagen" }) : new JsonArray(),
            ["competencies"] = comps ? new JsonArray(new JsonObject { ["id"] = "k1", ["objective_id"] = "o1", ["name"] = "Grundlagen anwenden", ["description"] = "", ["pass_percent"] = 60, ["min_questions"] = 1 }) : new JsonArray(),
            ["cards"] = new JsonArray(Enumerable.Range(1, 3).Select(i => (JsonNode)new JsonObject
            {
                ["category"] = "K", ["term"] = $"{name}-{i}", ["question"] = $"Frage {name} {i}?", ["definition"] = "D", ["competencies"] = comps ? new JsonArray("k1") : new JsonArray(),
                ["quiz"] = new JsonArray(new JsonObject { ["type"] = "single_choice", ["question"] = $"Quiz {name} {i}?", ["options"] = new JsonArray("falsch1", "richtig", "falsch2", "falsch3"), ["correct_index"] = 1, ["explanation"] = "w" }),
            }).ToArray()),
        }.ToJsonString();
        await o.PostAsync("/api/modules/import", Raw(Mod("TC-A", true)));
        await o.PostAsync("/api/modules/import", Raw(Mod("TC-B", false)));
        var topicDef = new JsonObject
        {
            ["format"] = "learny-topic", ["schema_version"] = 1, ["name"] = "TC Thema", ["description"] = "Themenabschluss",
            ["modules"] = new JsonArray(
                new JsonObject { ["module_name"] = "TC-A", ["exam"] = new JsonObject { ["enabled"] = true, ["question_count"] = 2, ["pass_percent"] = 60 } },
                new JsonObject { ["module_name"] = "TC-B", ["exam"] = new JsonObject { ["enabled"] = true, ["question_count"] = 2, ["pass_percent"] = 60 } }),
            ["exam_config"] = new JsonObject { ["final_exam"] = new JsonObject { ["enabled"] = true, ["question_count"] = 3, ["pass_percent"] = 60, ["unlock"] = new JsonObject { ["require_all_modules_completed"] = false, ["require_module_exams_passed"] = true } } },
        };
        var tid = S(await J(await o.PostAsync("/api/topics/import", Raw(topicDef.ToJsonString()))), "id");
        await o.PostAsync($"/api/topics/{tid}/versions", Body(new { }));
        await o.PostAsync($"/api/topics/{tid}/shares", Body(new { user_id = leaId }));
        var detail = await J(await lea.GetAsync($"/api/topics/{tid}?version=1"));
        var frozen = detail!["modules"]!.AsArray().ToDictionary(m => S(m, "name"), m => S(m, "module_id"));
        var finalModule = S(detail, "exam_module_id");

        async Task<JsonNode?> PassAsync(string urlKind, string? moduleId)
        {
            var startRes = await lea.PostAsync($"/api/topics/{tid}/exams/{urlKind}", Body(moduleId is null ? new { } : new { module_id = moduleId }));
            var start = await J(startRes);
            var answers = start!["questions"]!.AsArray().Select(q =>
            {
                var opts = q!["options"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();
                return new { question_id = S(q, "question_id"), selected_indexes = new[] { Math.Max(0, opts.IndexOf("richtig")) } };
            }).ToList();
            return await J(await lea.PostAsync("/api/quiz/submit", Body(new { module_id = S(start, "module_id"), session_id = S(start, "session_id"), answers })));
        }
        async Task<JsonArray> CertsAsync(HttpClient c) => (await J(await c.GetAsync("/api/certificates/")))!.AsArray();

        // ─── Noch nicht abgeschlossen: Modulzertifikate ja, Themenabschluss nein ───
        var resA = await PassAsync("module", frozen["TC-A"]);
        var resB = await PassAsync("module", frozen["TC-B"]);
        var certs1 = await CertsAsync(lea);
        check(S(resA, "passed") == "true" && S(resB, "passed") == "true" && certs1.Count == 2 && certs1.All(c => S(c, "kind") == "module" && S(c, "scope") == "topic" && int.Parse(S(c, "module_version")) >= 1),
            "A1 bestandene Modulprüfungen in Themen erzeugen Modulzertifikate (eigenständige Open Badges mit Modulversion)");
        check(!certs1.Any(c => S(c, "kind") == "topic"), "A2 ohne gelernte Karten und Abschlussprüfung gibt es keinen Themenabschluss");
        foreach (var m in frozen.Values)
            foreach (var card in (await J(await lea.GetAsync($"/api/cards/?module_id={m}")))!.AsArray())
                await lea.PutAsync($"/api/cards/{S(card, "id")}/state", Body(new { is_checked = true, marked_review = false }));
        check(!(await CertsAsync(lea)).Any(c => S(c, "kind") == "topic"), "A3 auch mit gelernten Karten ist das Thema ohne bestandene Abschlussprüfung nicht abgeschlossen");

        // ─── Abschluss ───
        var fin = await PassAsync("final", null);
        var certs2 = await CertsAsync(lea);
        var topicCert = certs2.SingleOrDefault(c => S(c, "kind") == "topic");
        check(S(fin, "passed") == "true" && topicCert is not null && S(topicCert, "has_clr") == "true" && S(topicCert, "module_name") == "TC Thema" && S(topicCert, "module_version") == "1" && S(topicCert, "status") == "active",
            "A4 bestandene Abschlussprüfung nach allen Modulen: Themenabschluss mit Master-Badge und CLR ausgestellt");
        var certId = S(topicCert, "id"); var completedAt = S(topicCert, "completed_at"); var number = S(topicCert, "number");

        var master = await J(await lea.GetAsync($"/api/certificates/{certId}/badge"));
        var clr = await J(await lea.GetAsync($"/api/certificates/{certId}/clr"));
        var masterErr = CheckVc(master!, "master"); var clrErr = CheckVc(clr!, "clr");
        check(masterErr.Count == 0, "B1 Master-Badge entspricht den Pflichtfeldern von Open Badges 3.0" + (masterErr.Count > 0 ? " — " + string.Join("; ", masterErr) : ""));
        check(S(master, "credentialSubject.achievement.name") == "Erfolgreicher Themenabschluss" && S(master, "credentialSubject.achievement.criteria.narrative").Contains("Alle erforderlichen Module der angegebenen Themenversion erfolgreich bestanden.")
              && S(master, "credentialSubject.achievement.criteria.narrative").Contains("Abschlussprüfung") && S(master, "credentialSubject.achievement.version") == "1",
            "B2 Master-Badge: Achievement „Erfolgreicher Themenabschluss“, Kriterien inkl. verpflichtender Abschlussprüfung, Themenversion");
        var evidenceIds = master!["evidence"]!.AsArray().Select(e => S(e, "id")).ToList();
        var moduleCertIds = certs2.Where(c => S(c, "kind") == "module").Select(c => S(c, "id")).ToList();
        check(moduleCertIds.All(id => evidenceIds.Any(e => e.EndsWith("/verify/" + id))) && evidenceIds.Any(e => e.EndsWith($"/api/public/certificates/{certId}/evidence")) && !master.ToJsonString().Contains("/clr"),
            "B3 Master-Badge verweist auf die Modulnachweise und den Snapshot (Evidence) — nicht auf das CLR (keine Zirkelverweise)");
        check(clrErr.Count == 0, "C1 CLR entspricht den Pflichtfeldern von CLR 2.0 (Kontexte, ClrCredential, ClrSubject, verifiableCredential, Achievements, Associations)" + (clrErr.Count > 0 ? " — " + string.Join("; ", clrErr) : ""));
        // Neues Profil (ADR 0038): eingebettete Credentials sind kompakte JWS; für die inhaltlichen Prüfungen wird ihre Nutzlast gelesen
        var embedded = new JsonArray(clr!["credentialSubject"]!["verifiableCredential"]!.AsArray().Select(v => v is JsonValue jv && jv.TryGetValue<string>(out var j) ? (JsonNode)BadgeSigner.CredentialFromJws(j) : v!.DeepClone()).ToArray());
        var embeddedIds = embedded.Select(v => S(v, "id")).ToList();
        check(embedded.Count == 3 && embeddedIds.Any(i => i.EndsWith("/verify/" + certId)) && moduleCertIds.All(id => embeddedIds.Any(e => e.EndsWith("/verify/" + id))) && S(clr, "id").EndsWith($"/verify/{certId}/clr")
              && clr["credentialSubject"]!["achievement"]!.AsArray().Count == 3 && clr["credentialSubject"]!["association"]!.AsArray().Count == 2 && S(clr, "credentialSubject.association.0.associationType") == "isChildOf",
            "C2 CLR bündelt Master-Badge und beide Modulabschlüsse als eingebettete Credentials, Achievements und Modul→Thema-Assoziationen");
        var compCert = embedded.FirstOrDefault(v => S(v, "credentialSubject.achievement.name") == "TC-A");
        check(compCert is not null && S(compCert, "credentialSubject.achievement.alignment.0.targetType") == "ceasn:Competency" && S(compCert, "credentialSubject.achievement.alignment.0.targetCode") == "k1",
            "C3 die Modulabschlüsse im CLR tragen die nachgewiesenen Kompetenzen (Alignment)");

        // ─── Signaturen und Verifikation ───
        async Task<JsonNode?> VerifyAsync(JsonNode n) => await J(await newClient().PostAsync("/api/public/verify", Raw(n.ToJsonString())));
        var vMaster = await VerifyAsync(master); var vClr = await VerifyAsync(clr);
        check(S(vMaster, "valid") == "true" && S(vClr, "valid") == "true" && S(vClr, "known") == "true" && S(vClr, "certificate_id") == certId, "D1 Signaturen von Master-Badge und CLR sind gültig; beide gehören zur selben Zertifikatsidentität");
        var allInnerValid = true;
        foreach (var inner in embedded) if (S(await VerifyAsync(inner!), "valid") != "true") allInnerValid = false;
        check(allInnerValid, "D2 alle eingebetteten Credentials (Module, Master) sind einzeln signaturgültig");
        var tampered = JsonNode.Parse(clr.ToJsonString())!; tampered["name"] = "Manipuliert";
        var tamperedInner = JsonNode.Parse(clr.ToJsonString())!; var forgedInner = (JsonObject)embedded[1]!.DeepClone(); forgedInner["name"] = "Manipuliert"; tamperedInner["credentialSubject"]!["verifiableCredential"]![1] = forgedInner;
        var vT = await VerifyAsync(tampered); var vTI = await VerifyAsync(tamperedInner);
        check(S(vT, "valid") == "false" && S(vT, "content_matches") == "false" && S(vTI, "valid") == "false", "D3 Manipulation des CLR (auch eines eingebetteten Credentials) wird erkannt");

        // ─── Idempotenz, Abschlusszeitpunkt, Datenschutz ───
        await Task.Delay(1100);
        foreach (var card in (await J(await lea.GetAsync($"/api/cards/?module_id={frozen["TC-A"]}")))!.AsArray())
            await lea.PutAsync($"/api/cards/{S(card, "id")}/state", Body(new { is_checked = true, marked_review = true }));   // spätere Lernaktivität
        var certs3 = await CertsAsync(lea); var again = certs3.Single(c => S(c, "kind") == "topic");
        check(certs3.Count == 3 && S(again, "id") == certId && S(again, "number") == number && S(again, "completed_at") == completedAt && (await J(await lea.GetAsync($"/api/certificates/{certId}/badge")))!.ToJsonString() == master.ToJsonString(),
            "E1 wiederholter Abruf stellt nichts neu aus; Zertifikat, Nummer, Abschlussdatum und signierte Credentials bleiben unverändert (spätere Aktivität verschiebt den Abschluss nicht)");
        check((await oth.GetAsync($"/api/certificates/{certId}/clr")).StatusCode == HttpStatusCode.NotFound && (await oth.GetAsync($"/api/certificates/{certId}/badge")).StatusCode == HttpStatusCode.NotFound && (await CertsAsync(oth)).Count == 0,
            "E2 fremde Benutzer sehen weder CLR noch Badge noch Abschlüsse");
        var pub = await (await newClient().GetAsync($"/api/public/certificates/{certId}/evidence")).Content.ReadAsStringAsync();
        var pubJson = JsonNode.Parse(pub)!;
        check(S(pubJson, "format") == "learny-topic-completion-snapshot" && pubJson["modules"]!.AsArray().Count == 2 && S(pubJson, "modules.0.competencies.0.status").Length > 0 && !pub.Contains("richtig") && !pub.Contains("tcr-lea", StringComparison.OrdinalIgnoreCase)
              && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(pub))).ToLowerInvariant() == S(topicCert, "snapshot_hash"),
            "E3 öffentlicher Themen-Snapshot: Module, Kompetenzen, Abschlussprüfung — ohne Antworten und Lernendendaten; Hash entspricht dem Zertifikat");

        // ─── PDF (ADR 0033) ───
        await PdfTests.E2EAsync(lea, oth, newClient, certId, moduleCertIds[0], clr!, master!, check);

        await AchievementTests.RunAsync(lea, oth, newClient, certId, moduleCertIds[0], check);

        // ─── Widerruf ───
        var rev = await admin.PostAsync($"/api/admin/certificates/{certId}/revoke", Body(new { reason = "Testwiderruf" }));
        var list = await J(await newClient().GetAsync("/api/public/revocation-list"));
        var revoked = list!["revoked_credentials"]!.AsArray().Select(r => S(r, "id")).ToList();
        var vMasterRev = await VerifyAsync(master); var vClrRev = await VerifyAsync(clr);
        check(rev.IsSuccessStatusCode && revoked.Any(r => r.EndsWith($"/verify/{certId}")) && revoked.Any(r => r.EndsWith($"/verify/{certId}/clr")) && S(vMasterRev, "revoked") == "true" && S(vClrRev, "revoked") == "true" && S(vClrRev, "valid") == "false",
            "F1 Widerruf des Themenabschlusses: Master-Badge und CLR gelten als widerrufen (Sperrliste, Prüfung); Modulabschlüsse bleiben einzeln gültig");
        await PdfTests.RevokedAsync(lea, newClient, certId, check);
        var vInner = await VerifyAsync(embedded.First(v => S(v, "credentialSubject.achievement.name") == "TC-A")!);
        check(S(vInner, "valid") == "true", "F2 widerrufen wird nur der Themenabschluss, nicht die zugrunde liegenden Modulnachweise");
    }
}
