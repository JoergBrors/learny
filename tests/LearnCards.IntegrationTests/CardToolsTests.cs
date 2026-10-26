using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// MCP-Werkzeuge get_card, update_card und export_cards (ADR 0007 Ergänzung): vollständiges Lesen, Ändern ausschließlich eigener privater Karten
/// (Bilder-/Quizsemantik, Konflikterkennung), Stapel-Export, Reimport über import_cards und Berechtigungen auch über MCP.
/// </summary>
public static class CardToolsTests
{
    private static StringContent Body(object? o) => new(JsonSerializer.Serialize(o ?? new { }), Encoding.UTF8, "application/json");
    private static async Task<JsonNode?> J(HttpResponseMessage r) { var t = await r.Content.ReadAsStringAsync(); try { return t.Length == 0 ? null : JsonNode.Parse(t); } catch (JsonException) { return null; } }
    private static string S(JsonNode? n, string path)
    {
        var cur = n;
        foreach (var part in path.Split('.')) cur = cur is JsonArray a && int.TryParse(part, out var i) ? (i < a.Count ? a[i] : null) : cur?[part];
        return cur?.ToString() ?? "";
    }

    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAQCAIAAAD4YuoOAAAAI0lEQVR42mPg94mhKWIAYq3ls2mERi0YtWDUglELRpIFNEUAOleXE3NVou0AAAAASUVORK5CYII=";

    public static async Task RunAsync(Func<string?, Task<HttpClient>> login, Func<HttpClient> newClient, HttpClient admin, string mcpUrl, string mcpKey, Func<int, int, byte[]> pngLike, Action<bool, string> check)
    {
        Console.WriteLine("\nMCP-Werkzeuge get_card, update_card, export_cards");
        var ann = await login("ct-ann"); var ben = await login("ct-ben"); var cat = await login("ct-cat");
        async Task<string> TokenAsync(HttpClient c, string name) => S(await J(await c.PostAsync("/api/mcp/links/", Body(new { name, days = 30 }))), "token");
        var annTok = await TokenAsync(ann, "CT-Ann"); var benTok = await TokenAsync(ben, "CT-Ben");
        async Task<string> Tool(string token, string tool, object args)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, mcpUrl + "/mcp") { Content = Body(new { jsonrpc = "2.0", id = 5, method = "tools/call", @params = new { name = tool, arguments = args } }) };
            if (token.Length > 0) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var res = await newClient().SendAsync(req);
            return res.IsSuccessStatusCode ? S(await J(res), "result.content.0.text") : "HTTP " + (int)res.StatusCode;
        }
        static JsonNode? Parse(string text) { try { return JsonNode.Parse(text); } catch (JsonException) { return null; } }
        static object Quiz(string q) => new { type = "single_choice", question = q, options = new[] { "a", "b", "c", "d" }, correct_index = 2, explanation = "weil c" };
        var benId = S(await J(await ben.GetAsync("/api/me")), "id");

        // ─── Ausgangsdaten: Anns Modul mit Bild-/Quizkarte, Pool-Karte, archivierter Karte ───
        var imported = await ann.PostAsync("/api/import/cards", Body(new
        {
            cards = new object[]
            {
                new { module = "CT-Modul", category = "Netz", term = "CT-Voll", question = "Frage **voll**?", definition = "Def mit *Rich* und [Link](https://example.com)", how_it_works = "H", context = "C", key_fact = "K", reference_answer = "R",
                      chat_prompt = "P", official_sources = new[] { new { title = "Doku", url = "https://learn.microsoft.com/x", publisher = "MS" } }, slide_number = 3, target_time_sec = 90,
                      quiz = new[] { Quiz("Q1?") }, images = new[] { new { mime_type = "image/png", data_base64 = Png, alt_text = "Alt", caption = "Cap" } }, sort_order = 1 },
                new { module = "CT-Modul", category = "Netz", term = "CT-Zwei", question = "Frage 2?", definition = "Def 2", sort_order = 2 },
                new { module = "CT-Modul", category = "Netz", term = "CT-Archiv", question = "Frage 3?", definition = "Def 3", archived = true, sort_order = 3 },
            },
        }));
        var mods = (await J(await ann.GetAsync("/api/modules/")))!.AsArray();
        var modId = S(mods.Single(m => S(m, "name") == "CT-Modul"), "id");
        var own = (await J(await ann.GetAsync($"/api/cards/?module_id={modId}")))!.AsArray();
        var fullId = S(own.Single(c => S(c, "term") == "CT-Voll"), "id");
        var zweiId = S(own.Single(c => S(c, "term") == "CT-Zwei"), "id");
        check(imported.IsSuccessStatusCode && own.Count == 2, "C0 Ausgangsdaten: Modul mit drei Karten (eine archiviert)");

        // ─── A: vollständiges Lesen ───
        var full = Parse(await Tool(annTok, "get_card", new { card_id = fullId }));
        var fields = new[] { "id", "module_id", "module", "category", "term", "question", "definition", "how_it_works", "context", "key_fact", "reference_answer", "chat_prompt", "official_sources", "slide_number", "target_time_sec", "quiz", "images", "archived", "sort_order", "updated_at", "module_context", "can_edit" };
        check(full is JsonObject fo && fields.All(f => fo.ContainsKey(f)) && S(full, "module") == "CT-Modul" && S(full, "reference_answer") == "R" && S(full, "slide_number") == "3" && S(full, "official_sources.0.publisher") == "MS"
              && S(full, "quiz.0.correct_index") == "2" && S(full, "images.0.data_base64") == Png && S(full, "can_edit") == "true", "A1 get_card liefert alle kanonischen Felder inkl. Quiz und echter Base64-Bilddaten");
        var fullText = await Tool(annTok, "get_card", new { card_id = fullId });
        check(!fullText.Contains("storage_key", StringComparison.OrdinalIgnoreCase) && !fullText.Contains("provider", StringComparison.OrdinalIgnoreCase) && !fullText.Contains("sig=") && !fullText.Contains("sv="), "A2 keine Speicherschlüssel, Provider oder signierte URLs in get_card");
        var meta = Parse(await Tool(annTok, "get_card", new { card_id = fullId, include_image_data = false }));
        check(S(meta, "images.0.mime_type") == "image/png" && meta!["images"]![0]!["data_base64"] is null, "A3 include_image_data=false liefert nur Bildmetadaten");

        var benMine = Parse(await Tool(benTok, "get_card", new { card_id = fullId }));
        check((await Tool(benTok, "get_card", new { card_id = fullId })).Contains("404") && benMine is null, "A4 fremde private Karte: 404 über MCP");
        check((await ben.GetAsync($"/api/cards/{fullId}")).StatusCode == HttpStatusCode.NotFound, "A4b fremde private Karte: 404 über REST");

        // freigegebenes Modul
        check((await ann.PostAsync($"/api/sharing/modules/{modId}/shares", Body(new { recipient_user_id = benId }))).StatusCode == HttpStatusCode.NoContent, "A5 Freigabe an Ben");
        var shared = Parse(await Tool(benTok, "get_card", new { card_id = fullId }));
        check(S(shared, "term") == "CT-Voll" && S(shared, "module_context") == "shared" && S(shared, "can_edit") == "false" && S(shared, "images.0.data_base64") == Png, "A5 freigegebene Karte ist lesbar (context=shared, can_edit=false, inkl. Bilddaten)");

        // ─── B: Aktualisieren ───
        var upd = full!.DeepClone().AsObject();
        upd["term"] = "CT-Voll v2"; upd["definition"] = "Neu mit **Fett**";
        upd["official_sources"] = new JsonArray(new JsonObject { ["title"] = "Neu", ["url"] = "https://learn.microsoft.com/neu", ["publisher"] = "MS" });
        upd["quiz"] = new JsonArray(JsonNode.Parse(JsonSerializer.Serialize(Quiz("Q-neu?"))), JsonNode.Parse(JsonSerializer.Serialize(Quiz("Q-neu2?"))));
        upd["sort_order"] = 5; upd["slide_number"] = 4;
        var r1 = await Tool(annTok, "update_card", new { card_id = fullId, card_definition = upd, expected_updated_at = S(full, "updated_at") });
        var after = Parse(await Tool(annTok, "get_card", new { card_id = fullId }));
        check(r1.StartsWith("Card updated") && S(after, "term") == "CT-Voll v2" && S(after, "definition") == "Neu mit **Fett**" && S(after, "official_sources.0.title") == "Neu" && S(after, "quiz.1.question") == "Q-neu2?"
              && S(after, "sort_order") == "5" && S(after, "slide_number") == "4" && S(after, "images.0.data_base64") == Png && S(after, "module_id") == modId,
            "B1 update_card: Text, Quellen, Quiz, Reihenfolge und Folieninfo ändern; Bild bleibt (voller Round-Trip aus get_card); Karte bleibt im Modul" + (r1.StartsWith("Card updated") ? "" : " — " + r1));

        // Bilder: fehlendes Feld = behalten
        var noImages = after!.DeepClone().AsObject(); noImages.Remove("images"); noImages["key_fact"] = "K2";
        await Tool(annTok, "update_card", new { card_id = fullId, card_definition = noImages });
        var keep = Parse(await Tool(annTok, "get_card", new { card_id = fullId }));
        check(S(keep, "key_fact") == "K2" && S(keep, "images.0.alt_text") == "Alt" && S(keep, "images.0.data_base64") == Png, "B2 fehlendes images-Feld erhält bestehende Bilder");
        // nur image_id behält ebenfalls, neue Bilder ersetzen
        var imgId = S(keep, "images.0.image_id");
        var byId = keep!.DeepClone().AsObject(); byId["images"] = new JsonArray(new JsonObject { ["image_id"] = imgId, ["alt_text"] = "Alt neu" });
        await Tool(annTok, "update_card", new { card_id = fullId, card_definition = byId });
        var kept = Parse(await Tool(annTok, "get_card", new { card_id = fullId }));
        check(S(kept, "images.0.alt_text") == "Alt neu" && S(kept, "images.0.data_base64") == Png && kept!["images"]!.AsArray().Count == 1, "B3 images mit nur image_id behält das Bild (Metadaten änderbar)");
        var bad = keep.DeepClone().AsObject(); bad["images"] = new JsonArray(new JsonObject { ["mime_type"] = "image/png", ["data_base64"] = "kein-base64!" });
        var badRes = await Tool(annTok, "update_card", new { card_id = fullId, card_definition = bad });
        check(badRes.StartsWith("API error 400") && S(Parse(await Tool(annTok, "get_card", new { card_id = fullId })), "images.0.data_base64") == Png, "B4 ungültige Bilddaten werden abgewiesen, das vorhandene Bild bleibt");
        var newImg = Convert.ToBase64String(pngLike(300, 7));
        var repl = keep.DeepClone().AsObject(); repl["images"] = new JsonArray(new JsonObject { ["mime_type"] = "image/png", ["data_base64"] = newImg, ["alt_text"] = "Neu" });
        await Tool(annTok, "update_card", new { card_id = fullId, card_definition = repl });
        var replaced = Parse(await Tool(annTok, "get_card", new { card_id = fullId }));
        check(S(replaced, "images.0.data_base64") == newImg && replaced!["images"]!.AsArray().Count == 1, "B5 übergebene images[] ersetzen die Bilder (validiert, Bytes identisch)");
        var clear = replaced.DeepClone().AsObject(); clear["images"] = new JsonArray();
        await Tool(annTok, "update_card", new { card_id = fullId, card_definition = clear });
        var cleared = Parse(await Tool(annTok, "get_card", new { card_id = fullId }));
        check(cleared!["images"]!.AsArray().Count == 0, "B6 explizites images=[] entfernt die Bilder");

        // Konflikt
        var stale = S(full, "updated_at");
        var conflict = await Tool(annTok, "update_card", new { card_id = fullId, card_definition = cleared, expected_updated_at = stale });
        check(conflict.StartsWith("API error 409") && conflict.Contains("card_conflict"), "B7 Konflikterkennung: veralteter expected_updated_at → 409");
        var fresh = await Tool(annTok, "update_card", new { card_id = fullId, card_definition = cleared, expected_updated_at = S(cleared, "updated_at") });
        check(fresh.StartsWith("Card updated"), "B7b aktueller expected_updated_at wird akzeptiert");

        // Berechtigungen
        var foreign = await Tool(benTok, "update_card", new { card_id = fullId, card_definition = cleared });
        var foreignOther = await Tool(await TokenAsync(cat, "CT-Cat"), "update_card", new { card_id = fullId, card_definition = cleared });
        check(foreign.StartsWith("API error 404") && foreign.Contains("card_read_only"), "B8 freigegebene Karte ist nicht änderbar (404, code card_read_only)");
        check(foreignOther.StartsWith("API error 404") && S(Parse(await Tool(annTok, "get_card", new { card_id = fullId })), "term") == "CT-Voll v2", "B9 fremde private Karte: 404, Inhalt unverändert");

        // global: Einreichung + Freigabe
        var submission = await J(await ann.PostAsync($"/api/sharing/modules/{modId}/submissions", null));
        await admin.PostAsync($"/api/admin/submissions/{S(submission, "id")}/approve", null);
        var globalMod = (await J(await cat.GetAsync("/api/modules/?scope=global")))!.AsArray().FirstOrDefault(m => S(m, "name") == "CT-Modul");
        var globalCard = globalMod is null ? null : (await J(await cat.GetAsync($"/api/cards/?module_id={S(globalMod, "id")}")))!.AsArray().FirstOrDefault(c => S(c, "term") == "CT-Voll v2");
        var catTok = await TokenAsync(cat, "CT-Cat2");
        var globalRead = globalCard is null ? null : Parse(await Tool(catTok, "get_card", new { card_id = S(globalCard, "id") }));
        check(globalRead is not null && S(globalRead, "module_context") == "global" && S(globalRead, "can_edit") == "false", "A6 globale Karte ist lesbar (context=global, can_edit=false)");
        var globalUpd = globalCard is null ? "" : await Tool(catTok, "update_card", new { card_id = S(globalCard, "id"), card_definition = cleared });
        var globalUpdOwner = globalCard is null ? "" : await Tool(annTok, "update_card", new { card_id = S(globalCard, "id"), card_definition = cleared });
        check(globalUpd.StartsWith("API error 404") && globalUpd.Contains("card_read_only") && globalUpdOwner.StartsWith("API error 404") && S(Parse(await Tool(catTok, "get_card", new { card_id = S(globalCard, "id") })), "term") == "CT-Voll v2",
            "B10 globale Karten sind nicht änderbar (Leser 404/card_read_only, auch nicht durch den ursprünglichen Eigentümer: 404); Inhalt unverändert");

        // ─── C: Export und Reimport ───
        var archivedFlag = await Tool(annTok, "export_cards", new { module_id = modId });
        var exp = Parse(archivedFlag);
        check(exp!["cards"]!.AsArray().Count == 2 && S(exp, "batch.total") == "2" && S(exp, "batch.has_more") == "false" && !archivedFlag.Contains("owner"), "C1 export_cards ohne include_archived: aktive Karten, keine Eigentümerangaben");
        var expAll = Parse(await Tool(annTok, "export_cards", new { module_id = modId, include_archived = true }));
        var archivedCard = expAll!["cards"]!.AsArray().FirstOrDefault(c => S(c, "term") == "CT-Archiv");
        check(expAll["cards"]!.AsArray().Count == 3 && S(archivedCard, "archived") == "true", "C2 include_archived exportiert archivierte Karten mit archived=true");
        check(expAll["cards"]!.AsArray().Select(c => S(c, "term")).SequenceEqual(new[] { "CT-Zwei", "CT-Archiv", "CT-Voll v2" }) && expAll["cards"]!.AsArray().Select(c => int.Parse(S(c, "sort_order"))).SequenceEqual(new[] { 2, 3, 5 }), "C3 Kartenreihenfolge (sort_order) bleibt erhalten");
        var benExport = await Tool(benTok, "export_cards", new { module_id = modId });
        check(benExport.StartsWith("API error 404") || benExport.StartsWith("API error"), "C4 freigegebenes Modul einer anderen Person ist nicht exportierbar");
        check((await Tool(await TokenAsync(cat, "CT-Cat3"), "export_cards", new { module_id = modId })).StartsWith("API error 404"), "C4b fremdes privates Modul: 404");

        // Reimport in ein anderes Modul
        var toReimport = expAll["cards"]!.AsArray().Select(c => { var o = c!.DeepClone().AsObject(); o["module"] = "CT-Reimport"; o.Remove("id"); return (JsonNode)o; }).ToArray();
        var imp = await Tool(annTok, "import_cards", new { cards = new JsonArray(toReimport) });
        var reMod = S((await J(await ann.GetAsync("/api/modules/")))!.AsArray().Single(m => S(m, "name") == "CT-Reimport"), "id");
        var reExp = Parse(await Tool(annTok, "export_cards", new { module_id = reMod, include_archived = true }));
        bool Same(JsonNode a, JsonNode b)
        {
            var x = a.DeepClone().AsObject(); var y = b.DeepClone().AsObject();
            foreach (var o in new[] { x, y }) { o.Remove("id"); o.Remove("module"); }
            return x.ToJsonString() == y.ToJsonString();
        }
        var reCards = reExp!["cards"]!.AsArray();
        check(imp.Contains("3 created") && reCards.Count == 3 && expAll["cards"]!.AsArray().Select((c, i) => Same(c!, reCards[i]!)).All(b => b),
            "C5 Export → import_cards → Export: Texte, Quellen, Quiz, Reihenfolge, Archivstatus identisch");
        var reVoll = reCards.Single(c => S(c, "term") == "CT-Voll v2");
        check(S(reVoll, "quiz.1.question") == "Q-neu2?" && S(reVoll, "official_sources.0.title") == "Neu", "C6 Quizdefinitionen und Quellen überstehen den Reimport");

        // Bilder im Export/Reimport + Stapel
        var withImg = cleared.DeepClone().AsObject(); var bigB64 = Convert.ToBase64String(pngLike(1_500_000, 11));
        withImg["images"] = new JsonArray(new JsonObject { ["mime_type"] = "image/png", ["data_base64"] = Png, ["alt_text"] = "Klein" });
        await Tool(annTok, "update_card", new { card_id = fullId, card_definition = withImg });
        var bigCards = Enumerable.Range(0, 4).Select(i => (JsonNode)new JsonObject
        {
            ["module"] = "CT-Gross", ["category"] = "G", ["term"] = $"G{i:00}", ["question"] = "Q?", ["definition"] = "D", ["sort_order"] = i,
            ["images"] = new JsonArray(Enumerable.Range(0, 2).Select(k => (JsonNode)new JsonObject { ["mime_type"] = "image/png", ["data_base64"] = Convert.ToBase64String(pngLike(1_500_000, 20 + i * 2 + k)), ["alt_text"] = "g" }).ToArray()),
        }).ToArray();
        await ann.PostAsync("/api/import/cards", Body(new { cards = new JsonArray(bigCards) }));
        var bigId = S((await J(await ann.GetAsync("/api/modules/")))!.AsArray().Single(m => S(m, "name") == "CT-Gross"), "id");
        var batchNo = 0; var offset = 0; var terms = new List<string>(); var imgOk = true;
        while (true)
        {
            var batch = Parse(await Tool(annTok, "export_cards", new { module_id = bigId, offset }));
            if (batch is null) { imgOk = false; break; }
            batchNo++;
            foreach (var c in batch["cards"]!.AsArray()) { terms.Add(S(c, "term")); imgOk &= c!["images"]!.AsArray().Count == 2 && S(c, "images.0.data_base64").Length > 1_000_000; }
            if (S(batch, "batch.has_more") != "true") break;
            offset = int.Parse(S(batch, "batch.next_offset"));
            if (batchNo > 10) break;
        }
        check(terms.SequenceEqual(new[] { "G00", "G01", "G02", "G03" }) && batchNo >= 2 && imgOk, $"C7 große Module (8 × 1,5 MB Bilder): Export in begrenzten Stapeln ({batchNo}), vollständig und ohne Doppelte, Bilder als echte Base64-Daten");
        var lim = Parse(await Tool(annTok, "export_cards", new { module_id = modId, include_archived = true, limit = 1 }));
        check(lim!["cards"]!.AsArray().Count == 1 && S(lim, "batch.has_more") == "true" && S(lim, "batch.next_offset") == "1" && S(lim, "batch.total") == "3", "C8 limit/offset: ein Stapel mit has_more und next_offset");
        var reImg = Parse(await Tool(annTok, "export_cards", new { module_id = modId }));
        var imgCard = reImg!["cards"]!.AsArray().Single(c => S(c, "term") == "CT-Voll v2");
        var imgImport = await Tool(annTok, "import_cards", new { cards = new JsonArray(reImg["cards"]!.AsArray().Select(c => { var o = c!.DeepClone().AsObject(); o["module"] = "CT-Reimport-Bild"; o.Remove("id"); return (JsonNode)o; }).ToArray()) });
        var reImgMod = S((await J(await ann.GetAsync("/api/modules/")))!.AsArray().Single(m => S(m, "name") == "CT-Reimport-Bild"), "id");
        var reImgCards = Parse(await Tool(annTok, "export_cards", new { module_id = reImgMod }));
        check(imgImport.Contains("2 created") && S(reImgCards!["cards"]!.AsArray().Single(c => S(c, "term") == "CT-Voll v2"), "images.0.data_base64") == S(imgCard, "images.0.data_base64") && S(imgCard, "images.0.data_base64") == Png, "C9 Bilder bleiben beim Reimport byte-identisch erhalten");
        // Überschreiben bestehender Karten per ID
        var withIds = Parse(await Tool(annTok, "export_cards", new { module_id = modId }));
        var overwrite = await Tool(annTok, "import_cards", new { cards = withIds!["cards"], overwrite_existing = true });
        check(overwrite.Contains("2 updated") && S(Parse(await Tool(annTok, "get_card", new { card_id = fullId })), "images.0.data_base64") == Png, "C10 Export mit IDs + import_cards(overwrite_existing=true) aktualisiert dieselben Karten (Bilder bleiben)");

        // ─── D: MCP-Oberfläche ───
        using var rpc = new HttpRequestMessage(HttpMethod.Post, mcpUrl + "/mcp") { Content = Body(new { jsonrpc = "2.0", id = 9, method = "tools/list", @params = new { } }) };
        rpc.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", annTok);
        var toolList = S(await J(await newClient().SendAsync(rpc)), "result.tools");
        var old = new[] { "get_import_schema", "add_card", "import_cards", "list_cards", "archive_card", "restore_card", "export_module", "import_module" };
        check(new[] { "get_card", "update_card", "export_cards" }.All(t => toolList.Contains($"\"{t}\"")) && old.All(t => toolList.Contains($"\"{t}\"")), "D1 tools/list: neue und bestehende Werkzeuge sind registriert");
        var tools = JsonNode.Parse(toolList)!.AsArray();
        JsonNode? T(string n) => tools.First(t => S(t, "name") == n);
        check(S(T("update_card"), "inputSchema.required").Contains("card_definition") && S(T("get_card"), "inputSchema.required").Contains("card_id") && S(T("export_cards"), "inputSchema.properties.limit.maximum") == "200"
              && S(T("update_card"), "description").Contains("get_import_schema") && S(T("export_cards"), "description").Contains("get_import_schema"), "D2 Tool-Schemas vollständig; Beschreibungen verweisen auf get_import_schema");
        var noTok = new[] { await Tool("", "get_card", new { card_id = fullId }), await Tool("", "update_card", new { card_id = fullId, card_definition = cleared }), await Tool("", "export_cards", new { module_id = modId }) };
        var badTok = new[] { await Tool("lcm_" + new string('x', 40), "get_card", new { card_id = fullId }), await Tool("lcm_" + new string('x', 40), "update_card", new { card_id = fullId, card_definition = cleared }), await Tool("lcm_" + new string('x', 40), "export_cards", new { module_id = modId }) };
        check(noTok.Concat(badTok).All(r => r.StartsWith("HTTP 4")), "D3 ohne und mit ungültigem Token: HTTP 4xx für alle drei Werkzeuge");
        using (var keyOnly = newClient())
        {
            keyOnly.DefaultRequestHeaders.Add("X-MCP-Key", mcpKey);
            var denied = new[]
            {
                await keyOnly.GetAsync($"/api/cards/{fullId}?include_image_data=true"), await keyOnly.GetAsync($"/api/modules/{modId}/export/cards"),
                await keyOnly.PutAsync($"/api/cards/{fullId}", Body(cleared)),
            };
            check(denied.All(r => r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden), "D4 der Dienstschlüssel allein erlaubt weder Lesen noch Ändern noch Export (401/403)");
        }
        var tokenHttp = newClient(); tokenHttp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", annTok);
        check((await tokenHttp.GetAsync($"/api/modules/{modId}/export/cards?include_archived=true")).IsSuccessStatusCode && (await tokenHttp.GetAsync("/api/admin/users")).StatusCode == HttpStatusCode.Forbidden, "D5 MCP-Token erreicht den Karten-Export, aber weiterhin keine administrativen Endpunkte");
    }
}
