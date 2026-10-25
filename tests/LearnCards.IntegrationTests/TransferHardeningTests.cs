using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// ADR 0028, Abschlussprüfung über HTTP/MCP: parallele Importe derselben Referenz, Eigentümerkontext der Referenzen, große Dateien (Limit 30 MB, Bilder),
/// vollständige MCP-Berechtigungsmatrix der neun Werkzeuge und Dienstschlüssel ohne Datenzugriff.
/// </summary>
public static class TransferHardeningTests
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

    public static async Task RunAsync(Func<string?, Task<HttpClient>> login, Func<HttpClient> newClient, string mcpUrl, string mcpKey, Func<int, int, byte[]> pngLike, Action<bool, string> check)
    {
        Console.WriteLine("\nTransfer-Härtung: Referenzen parallel, große Dateien, MCP-Berechtigungsmatrix (ADR 0028)");
        var owner = await login("hard-owner"); var other = await login("hard-other");
        async Task<JsonArray> ModulesAsync(HttpClient c) => (await J(await c.GetAsync("/api/modules/")))!.AsArray();
        async Task<string> ExportRefAsync(HttpClient c, string id) => S(await J(await c.GetAsync($"/api/modules/{id}/export")), "reference_id");
        static object Card(string module, string term) => new { module, category = "K", term, question = "Q?", definition = "D" };

        // ─── Referenzen: parallel und kontextgebunden ───
        var R = Guid.NewGuid().ToString();
        string ModJson(string name, string? reference, int cards = 2)
        {
            var o = new JsonObject { ["format"] = "learny-module", ["schema_version"] = 1, ["name"] = name, ["cards"] = new JsonArray(Enumerable.Range(0, cards).Select(i => (JsonNode)new JsonObject { ["category"] = "K", ["term"] = $"T{i}", ["question"] = "Q?", ["definition"] = "D" }).ToArray()) };
            if (reference is not null) o["reference_id"] = reference;
            return o.ToJsonString();
        }
        var par = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => owner.PostAsync("/api/modules/import", Raw(ModJson("HD-Parallel", R)))));
        var parMods = (await ModulesAsync(owner)).Where(m => S(m, "name").StartsWith("HD-Parallel")).ToList();
        var parRefs = new List<string>(); foreach (var m in parMods) parRefs.Add(await ExportRefAsync(owner, S(m, "id")));
        check(par.All(r => r.StatusCode == HttpStatusCode.Created) && parMods.Count == 6 && parRefs.Count(x => x == R) == 1 && parRefs.Distinct().Count() == 6, "H1 sechs parallele Modulimporte mit derselben Referenz: alle erfolgreich, die Referenz gehört genau einem Modul");
        var embedded = JsonNode.Parse(ModJson("HD-Eingebettet", Guid.NewGuid().ToString()))!;
        var embRef = S(embedded, "reference_id");
        string TopicWithEmbedded(string name) => new JsonObject
        {
            ["format"] = "learny-topic", ["schema_version"] = 1, ["name"] = name,
            ["modules"] = new JsonArray(new JsonObject { ["module_ref"] = embRef, ["module_name"] = "HD-Eingebettet", ["module"] = embedded.DeepClone() }),
        }.ToJsonString();
        var parTopics = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => owner.PostAsync("/api/topics/import", Raw(TopicWithEmbedded($"HD-Thema-{i}")))));
        var embMods = (await ModulesAsync(owner)).Where(m => S(m, "name").StartsWith("HD-Eingebettet")).ToList();
        var topicsDetail = new List<string>();
        foreach (var r in parTopics) topicsDetail.Add(S(await J(await owner.GetAsync($"/api/topics/{S(await J(r), "id")}")), "modules.0.module_id"));
        check(parTopics.All(r => r.StatusCode == HttpStatusCode.Created) && embMods.Count == 1 && topicsDetail.Distinct().Count() == 1, "H2 sechs parallele Themenimporte mit demselben eingebetteten Modul erzeugen genau ein Modul; alle Themen verweisen darauf");
        var otherTopic = await other.PostAsync("/api/topics/import", Raw(TopicWithEmbedded("HD-Fremd")));
        var otherMods = (await ModulesAsync(other)).Where(m => S(m, "name") == "HD-Eingebettet").ToList();
        check(otherTopic.StatusCode == HttpStatusCode.Created && otherMods.Count == 1 && S(otherMods[0], "id") != S(embMods[0], "id") && S(await J(await owner.GetAsync($"/api/modules/{S(embMods[0], "id")}/export")), "name") == "HD-Eingebettet",
            "H3 dieselbe Referenz in einem anderen Konto: eigenes, unabhängiges Modul; das Modul des ersten Benutzers bleibt unverändert");
        var foreignRefTopic = new JsonObject { ["format"] = "learny-topic", ["schema_version"] = 1, ["name"] = "HD-Fremdref", ["modules"] = new JsonArray(new JsonObject { ["module_ref"] = await ExportRefAsync(owner, S(parMods[0], "id")), ["module_name"] = "HD-Parallel" }) }.ToJsonString();
        var third = await login("hard-third");
        var fr = await third.PostAsync("/api/topics/import", Raw(foreignRefTopic));
        check(fr.StatusCode == HttpStatusCode.BadRequest && (await fr.Content.ReadAsStringAsync()).Contains("module_ref") && (await J(await third.GetAsync("/api/topics/")))!["own"]!.AsArray().Count == 0, "H4 die Referenz eines fremden Moduls löst nichts auf (Eigentümerkontext), auch nicht über den Namen");

        // ─── Große Dateien ───
        JsonObject BigModule(string name, int cards, int imagesPerCard, int imageBytes)
        {
            var arr = new JsonArray();
            for (var c = 0; c < cards; c++)
            {
                var images = new JsonArray();
                for (var i = 0; i < imagesPerCard; i++)
                    images.Add(new JsonObject { ["mime_type"] = "image/png", ["data_base64"] = Convert.ToBase64String(pngLike(imageBytes, c * 10 + i + 1)), ["alt_text"] = "Bild", ["sort_order"] = i });
                arr.Add(new JsonObject { ["category"] = "K", ["term"] = $"Gross{c}", ["question"] = "Q?", ["definition"] = "D", ["images"] = images });
            }
            return new JsonObject { ["format"] = "learny-module", ["schema_version"] = 1, ["name"] = name, ["cards"] = arr };
        }
        var big = await login("hard-big");
        var bigJson = BigModule("HD-Gross", 4, 4, 1_200_000).ToJsonString();   // 16 Bilder à 1,2 MB ≈ 25,6 MB JSON
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bigRes = await big.PostAsync("/api/modules/import", Raw(bigJson));
        var bigId = S(await J(bigRes), "id");
        var bigCards = (await J(await big.GetAsync($"/api/cards/?module_id={bigId}")))!.AsArray();
        check(bigJson.Length > 24_000_000 && bigJson.Length < 28_000_000 && bigRes.StatusCode == HttpStatusCode.Created && bigCards.Count == 4 && bigCards.All(c => S(c, "images").Length > 0 && ((JsonArray)c!["images"]!).Count == 4),
            $"H5 großes Modul-JSON ({bigJson.Length / 1_000_000} MB, 16 Bilder) wird vollständig importiert ({sw.ElapsedMilliseconds} ms)");
        var bigExport = await big.GetAsync($"/api/modules/{bigId}/export");
        var bigExportText = await bigExport.Content.ReadAsStringAsync();
        var again = await big.PostAsync("/api/modules/import", Raw(bigExportText));
        check(bigExport.IsSuccessStatusCode && again.StatusCode == HttpStatusCode.Created && S(await J(again), "name") == "HD-Gross (Import)", "H6 der große Export ist ohne Verlust wieder importierbar");
        var modsBefore = (await ModulesAsync(big)).Count;
        var tooBig = BigModule("HD-Zu-Gross", 5, 4, 1_200_000).ToJsonString();   // ≈ 32 MB
        var (tooBigStatus, tooBigText) = await PostBigAsync(big, "/api/modules/import", tooBig);
        check(tooBig.Length > 30_000_000 && (tooBigStatus == 413 && tooBigText.Contains("zu groß") && tooBigText.Contains("30 MB") || tooBigStatus == 0) && (await ModulesAsync(big)).Count == modsBefore,
            "H7 über 30 MB: 413 mit verständlicher Meldung (oder vom Server beendete Verbindung), nichts importiert (Kestrel-/Transferlimit wirksam)");
        var hugeTopic = new JsonObject { ["format"] = "learny-topic", ["schema_version"] = 1, ["name"] = "HD-Riesig", ["modules"] = new JsonArray(new JsonObject { ["module_name"] = "HD-Gross", ["module"] = JsonNode.Parse(tooBig) }) }.ToJsonString();
        var (hugeStatus, _) = await PostBigAsync(big, "/api/topics/import", hugeTopic);
        check((hugeStatus == 413 || hugeStatus == 0) && (await J(await big.GetAsync("/api/topics/")))!["own"]!.AsArray().Count == 0 && (await ModulesAsync(big)).Count == modsBefore, "H8 zu großer Themenimport (eingebettetes Modul): 413, weder Thema noch Modul");
        var bigTopic = new JsonObject { ["format"] = "learny-topic", ["schema_version"] = 1, ["name"] = "HD-Groß-Thema", ["modules"] = new JsonArray(new JsonObject { ["module_name"] = "HD-Gross-Eingebettet", ["module"] = BigModule("HD-Gross-Eingebettet", 3, 3, 1_200_000) }) }.ToJsonString();
        var bigTopicRes = await big.PostAsync("/api/topics/import", Raw(bigTopic));
        check(bigTopic.Length > 14_000_000 && bigTopicRes.StatusCode == HttpStatusCode.Created && (await ModulesAsync(big)).Any(m => S(m, "name") == "HD-Gross-Eingebettet"), "H9 großes Themen-JSON mit eingebettetem Modul (9 Bilder) wird importiert");
        var oversizeImage = BigModule("HD-Bild-Zu-Gross", 1, 1, 2 * 1024 * 1024 + 100);
        var oversizeRes = await big.PostAsync("/api/modules/import", Raw(oversizeImage.ToJsonString()));
        var manyCards = new JsonObject { ["format"] = "learny-module", ["schema_version"] = 1, ["name"] = "HD-Viele", ["cards"] = new JsonArray(Enumerable.Range(0, 2001).Select(i => (JsonNode)new JsonObject { ["category"] = "K", ["term"] = $"T{i}", ["question"] = "Q?", ["definition"] = "D" }).ToArray()) };
        var manyRes = await big.PostAsync("/api/modules/import", Raw(manyCards.ToJsonString()));
        var modsAfter = (await ModulesAsync(big)).Count;
        check(oversizeRes.StatusCode == HttpStatusCode.BadRequest && (await oversizeRes.Content.ReadAsStringAsync()).Contains("2 MiB") && manyRes.StatusCode == HttpStatusCode.BadRequest && (await manyRes.Content.ReadAsStringAsync()).Contains("Zu viele Karten") && modsAfter == modsBefore + 1,   // nur das eingebettete Modul aus H9 kam hinzu
            
            "H10 Bild über 2 MiB und mehr als 2000 Karten: verständliche 400-Meldung, kein Teilbestand (Modulanzahl unverändert)");

        // ─── MCP-Berechtigungsmatrix ───
        async Task<string> TokenAsync(HttpClient c, string name) => S(await J(await c.PostAsync("/api/mcp/links/", Body(new { name, days = 30 }))), "token");
        var ownerTok = await TokenAsync(owner, "HD-Owner"); var otherTok = await TokenAsync(other, "HD-Other");
        async Task<string> Tool(string token, string tool, object args)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, mcpUrl + "/mcp") { Content = Body(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = tool, arguments = args } }) };
            if (token.Length > 0) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var res = await newClient().SendAsync(req);
            return res.IsSuccessStatusCode ? S(await J(res), "result.content.0.text") : "HTTP " + (int)res.StatusCode;
        }
        var ownerModId = S(parMods[0], "id");
        var tid = S(await J(await owner.PostAsync("/api/topics/import", Raw(new JsonObject { ["format"] = "learny-topic", ["schema_version"] = 1, ["name"] = "HD-MCP", ["modules"] = new JsonArray(new JsonObject { ["module_ref"] = R, ["module_name"] = "HD-Parallel" }) }.ToJsonString()))), "id");
        var okDef = JsonNode.Parse(new JsonObject { ["format"] = "learny-topic", ["schema_version"] = 1, ["name"] = "HD-MCP", ["modules"] = new JsonArray(new JsonObject { ["module_ref"] = R, ["module_name"] = "HD-Parallel" }) }.ToJsonString())!;
        var globalId = (await J(await other.GetAsync("/api/modules/?scope=global")))!.AsArray().FirstOrDefault() is { } gm ? S(gm, "id") : Guid.NewGuid().ToString();
        var calls = new (string Tool, object Args)[]
        {
            ("get_topic_import_schema", new { }), ("get_module_import_schema", new { }),
            ("create_topic", new { definition = okDef }), ("import_topic", new { definition_json = okDef.ToJsonString() }),
            ("update_topic", new { topic_id = tid, definition = okDef }), ("export_topic", new { topic_id = tid }),
            ("clone_global_module", new { module_id = ownerModId }), ("export_module", new { module_id = ownerModId }),
            ("import_module", new { definition = JsonNode.Parse(ModJson("HD-MCP-Modul", null)) }),
        };
        var noToken = new List<string>(); var badToken = new List<string>();
        foreach (var (tool, args) in calls)
        {
            if (!(await Tool("", tool, args)).StartsWith("HTTP 4")) noToken.Add(tool);
            if (!(await Tool("lcm_" + new string('x', 40), tool, args)).StartsWith("HTTP 4")) badToken.Add(tool);
        }
        check(noToken.Count == 0 && badToken.Count == 0, "H11 alle neun MCP-Werkzeuge verlangen ein gültiges Benutzertoken (ohne und mit ungültigem Token: HTTP 4xx)" + (noToken.Count + badToken.Count > 0 ? $" — offen: {string.Join(",", noToken.Concat(badToken))}" : ""));
        var foreignResults = new[]
        {
            await Tool(otherTok, "update_topic", new { topic_id = tid, definition = okDef }), await Tool(otherTok, "export_topic", new { topic_id = tid }),
            await Tool(otherTok, "export_module", new { module_id = ownerModId }), await Tool(otherTok, "clone_global_module", new { module_id = ownerModId }),
            await Tool(otherTok, "create_topic", new { definition = okDef }), await Tool(otherTok, "import_topic", new { definition_json = okDef.ToJsonString() }),
        };
        check(foreignResults.All(r => r.StartsWith("API error 404") || r.StartsWith("API error 400")) && (await J(await owner.GetAsync($"/api/topics/{tid}")))!["modules"]!.AsArray().Count == 1, "H12 fremder Benutzer: fremde Themen/Module/Referenzen liefern 404/400 (kein Zugriff, keine Änderung, keine Auflösung über die fremde Referenz)");
        var manip = new[]
        {
            ("owner_user_id", (JsonNode)"x"), ("id", Guid.NewGuid().ToString()), ("version", 5), ("global_status", "active"), ("shares", new JsonArray()), ("assignments", new JsonArray()),
        };
        var manipOk = true; var manipDetail = "";
        foreach (var (k, v) in manip)
        {
            var d = JsonNode.Parse(okDef.ToJsonString())!; d.AsObject()[k] = v is JsonNode n ? n.DeepClone() : v;
            var r1 = await Tool(ownerTok, "create_topic", new { definition = d });
            var r2 = await Tool(ownerTok, "update_topic", new { topic_id = tid, definition = d });
            if (!(r1.StartsWith("API error 400") && r2.StartsWith("API error 400"))) { manipOk = false; manipDetail += k + " "; }
        }
        var modManip = JsonNode.Parse(ModJson("HD-Manip", null))!; modManip["owner_user_id"] = "x";
        manipOk &= (await Tool(ownerTok, "import_module", new { definition = modManip })).StartsWith("API error 400");
        check(manipOk && (await J(await owner.GetAsync("/api/topics/")))!["own"]!.AsArray().Count(t => S(t, "topic.name") == "HD-MCP") == 1, "H13 manipulierte JSON-Felder (Besitzer, IDs, Version, globaler Status, Freigaben, Zuweisungen) werden abgewiesen" + (manipDetail.Length > 0 ? " — offen: " + manipDetail : ""));
        var ownerOk = new[]
        {
            await Tool(ownerTok, "update_topic", new { topic_id = tid, definition = okDef }), await Tool(ownerTok, "export_topic", new { topic_id = tid }), await Tool(ownerTok, "export_module", new { module_id = ownerModId }),
            await Tool(ownerTok, "import_module", new { definition = JsonNode.Parse(ModJson("HD-MCP-Modul", null)) }),
        };
        check(ownerOk.All(r => !r.StartsWith("API error") && !r.StartsWith("HTTP")), "H14 der Eigentümer kann seine Themenentwürfe und Module über dieselben Werkzeuge verwalten");
        var pub = await owner.PostAsync($"/api/topics/{tid}/versions", Body(new { }));
        async Task<string> V1Async() { var d = await J(await owner.GetAsync($"/api/topics/{tid}?version=1")); return d!["modules"]!.ToJsonString() + d["stats"]!.ToJsonString() + d["final_exam"]!.ToJsonString() + S(d, "version") + S(d, "topic.name"); }
        var ownerBefore = await V1Async();
        var afterUpdate = await Tool(ownerTok, "update_topic", new { topic_id = tid, definition = JsonNode.Parse(new JsonObject { ["format"] = "learny-topic", ["schema_version"] = 1, ["name"] = "HD-MCP", ["description"] = "neu", ["modules"] = new JsonArray(new JsonObject { ["module_ref"] = R, ["module_name"] = "HD-Parallel" }) }.ToJsonString()) });
        var vList = (await J(await owner.GetAsync($"/api/topics/{tid}/versions")))!.AsArray();
        check(pub.StatusCode == HttpStatusCode.Created && afterUpdate.Contains("Nothing was published") && vList.Count == 1 && await V1Async() == ownerBefore, "H15 MCP-Update veröffentlicht nie und ändert keine veröffentlichte Version");
        var tools = S(await J(await PostRpc(newClient(), mcpUrl, ownerTok, "tools/list")), "result.tools");
        var nine = new[] { "get_topic_import_schema", "create_topic", "update_topic", "export_topic", "import_topic", "get_module_import_schema", "export_module", "import_module", "clone_global_module" };
        var forbidden = new[] { "publish_topic", "submit_topic_global", "approve_topic", "reject_topic", "assign_topic", "share_topic", "delete_topic", "set_topic_owner", "replace_module" };
        check(nine.All(t => tools.Contains($"\"{t}\"")) && forbidden.All(t => !tools.Contains($"\"{t}\"")), "H16 MCP bietet die neun Verwaltungswerkzeuge, aber keine Veröffentlichungs-, Freigabe-, Zuweisungs- oder Administrationswerkzeuge");
        using (var keyOnly = newClient())
        {
            keyOnly.DefaultRequestHeaders.Add("X-MCP-Key", mcpKey);
            var denied = new[]
            {
                await keyOnly.GetAsync("/api/topics/"), await keyOnly.GetAsync($"/api/topics/{tid}/export"), await keyOnly.PostAsync("/api/topics/import", Raw(okDef.ToJsonString())),
                await keyOnly.PutAsync($"/api/topics/{tid}/definition", Raw(okDef.ToJsonString())), await keyOnly.PostAsync("/api/modules/import", Raw(ModJson("HD-Key", null))),
                await keyOnly.GetAsync($"/api/modules/{ownerModId}/export"), await keyOnly.PostAsync($"/api/modules/{ownerModId}/clone", Body(new { })), await keyOnly.GetAsync("/api/modules/"),
            };
            var schemaOk = (await keyOnly.GetAsync("/api/schema/import/topic")).IsSuccessStatusCode && (await keyOnly.GetAsync("/api/schema/import/module")).IsSuccessStatusCode;
            check(denied.All(r => r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) && schemaOk && (await ModulesAsync(owner)).All(m => S(m, "name") != "HD-Key"), "H17 der Dienstschlüssel MCP_API_KEY erlaubt nur die Schemas, keinerlei Daten, Importe oder Änderungen (401)");
        }
    }

    /// <summary>Sendet einen übergroßen Body: Der Server antwortet mit 413 und schließt ggf. die Verbindung, bevor der Client fertig gesendet hat (dann 0).</summary>
    private static async Task<(int Status, string Text)> PostBigAsync(HttpClient c, string url, string json)
    {
        try { var r = await c.PostAsync(url, Raw(json)); return ((int)r.StatusCode, await r.Content.ReadAsStringAsync()); }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException) { return (0, "Verbindung vom Server beendet: " + e.Message); }
    }

    private static async Task<HttpResponseMessage> PostRpc(HttpClient c, string mcpUrl, string token, string method)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, mcpUrl + "/mcp") { Content = Body(new { jsonrpc = "2.0", id = 9, method, @params = new { } }) };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await c.SendAsync(req);
    }
}
