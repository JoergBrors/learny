using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Themenseite: Standardansicht = aktuelle Veröffentlichung; Entwurf nur bei tatsächlicher Abweichung (Struktur oder Modulinhalt), nie nur wegen eines Zeitstempels.</summary>
public static class TopicViewTests
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

    public static async Task RunAsync(Func<string?, Task<HttpClient>> login, Action<bool, string> check)
    {
        Console.WriteLine("\nThemenseite: Entwurf nur bei tatsächlichen Änderungen (ADR 0027)");
        var o = await login("tv-owner");
        string Mod(string name, string term) => new JsonObject { ["format"] = "learny-module", ["schema_version"] = 1, ["name"] = name, ["cards"] = new JsonArray(new JsonObject { ["category"] = "K", ["term"] = term, ["question"] = "Q?", ["definition"] = "D" }) }.ToJsonString();
        await o.PostAsync("/api/modules/import", Raw(Mod("TV-A", "A1")));
        await o.PostAsync("/api/modules/import", Raw(Mod("TV-B", "B1")));
        string Topic(params string[] mods) => new JsonObject { ["format"] = "learny-topic", ["schema_version"] = 1, ["name"] = "TV Thema", ["modules"] = new JsonArray(mods.Select(m => (JsonNode)new JsonObject { ["module_name"] = m }).ToArray()) }.ToJsonString();
        var id = S(await J(await o.PostAsync("/api/topics/import", Raw(Topic("TV-A")))), "id");
        async Task<JsonNode?> View(string q = "") => await J(await o.GetAsync($"/api/topics/{id}/view{q}"));

        var v = await View();
        check(S(v, "version") == "0" && S(v, "draft_differs") == "true", "V1 nie veröffentlichtes Thema zeigt den Entwurf");
        await o.PostAsync($"/api/topics/{id}/versions", Body(new { }));
        v = await View();
        check(S(v, "version") == "1" && S(v, "draft_differs") == "false", "V2 nach Veröffentlichung ohne Änderung: Standardansicht ist Version 1, kein gesonderter Entwurf");
        check(S(await View("?version=0"), "version") == "0" && S(await View("?version=1"), "version") == "1", "V3 Entwurf bzw. Version sind ausdrücklich wählbar");

        await o.PutAsync($"/api/topics/{id}/definition", Raw(Topic("TV-A", "TV-B")));
        v = await View();
        check(S(v, "version") == "0" && S(v, "draft_differs") == "true", "V4 geänderte Themenstruktur: Standardansicht ist der Entwurf (unveröffentlichte Änderungen)");
        check(S(await View("?version=1"), "version") == "1" && S(await View("?version=1"), "draft_differs") == "true", "V5 Version 1 bleibt wählbar und weist auf den abweichenden Entwurf hin");
        await o.PostAsync($"/api/topics/{id}/versions", Body(new { }));
        v = await View();
        check(S(v, "version") == "2" && S(v, "draft_differs") == "false", "V6 nach Version 2: wieder Standardansicht = aktuelle Veröffentlichung");

        var modA = S((await J(await o.GetAsync("/api/modules/")))!.AsArray().Single(m => S(m, "name") == "TV-A"), "id");
        var card = (await J(await o.GetAsync($"/api/cards/?module_id={modA}")))!.AsArray().Single();
        await o.PutAsync($"/api/cards/{S(card, "id")}", Body(new { module = "TV-A", category = "K", term = "A1", question = "Q?", definition = "D geändert" }));
        v = await View();
        check(S(v, "version") == "2" && S(v, "draft_differs") == "false", "V7 geänderter Modulinhalt ändert veröffentlichte Themen nicht");
        var draft = await View("?version=0");
        check(S(draft, "modules.0.newer_available") == "true" && S(draft, "modules.0.module_has_unpublished_changes") == "true" && S(draft, "unpublished_changes") == "false",
            "V8 geänderter Modulinhalt: Hinweis „neuere Modulversion“ im Entwurf, aber keine stillschweigende Übernahme");
        var adopt = await o.PostAsync($"/api/topics/{id}/modules/{modA}/adopt", Body(new { }));
        v = await View();
        check(adopt.IsSuccessStatusCode && S(v, "version") == "0" && S(v, "draft_differs") == "true", "V9 ausdrückliche Übernahme der neuen Modulversion macht den Entwurf zu einer unveröffentlichten Änderung");
        await o.PutAsync($"/api/cards/{S(card, "id")}", Body(new { module = "TV-A", category = "K", term = "A1", question = "Q?", definition = "D" }));
    }
}
