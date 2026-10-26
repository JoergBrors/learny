using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// ADR 0030: Modulversionen (Entwurf, fortlaufende unveränderliche Versionen, Änderungsindikator) und Themen mit festgelegten Modulversionen
/// (keine stillschweigende Übernahme, ausdrückliche Übernahme, persönliche Freigabe, Schutz der privaten Originale).
/// </summary>
public static class ModuleVersionTests
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
        Console.WriteLine("\nModulversionen und Themen mit festgelegten Modulversionen (ADR 0030)");
        var o = await login("mv-owner"); var x = await login("mv-other"); var lea = await login("mv-lea");
        var leaId = S(await J(await lea.GetAsync("/api/me")), "id");
        string Mod(string name, string def) => new JsonObject
        {
            ["format"] = "learny-module", ["schema_version"] = 1, ["name"] = name,
            ["cards"] = new JsonArray(new JsonObject { ["category"] = "K", ["term"] = name + "-1", ["question"] = "Q?", ["definition"] = def }),
        }.ToJsonString();
        var modId = S(await J(await o.PostAsync("/api/modules/import", Raw(Mod("MV-Modul", "Alt")))), "id");
        async Task<JsonNode?> Versions(HttpClient c) => await J(await c.GetAsync($"/api/modules/{modId}/versions"));
        async Task Edit(string def)
        {
            var card = (await J(await o.GetAsync($"/api/cards/?module_id={modId}")))!.AsArray().Single();
            await o.PutAsync($"/api/cards/{S(card, "id")}", Body(new { module = "MV-Modul", category = "K", term = "MV-Modul-1", question = "Q?", definition = def }));
        }
        async Task<string> DefOf(HttpClient c, string moduleId) => S((await J(await c.GetAsync($"/api/cards/?module_id={moduleId}")))!.AsArray().FirstOrDefault(), "definition");

        // ─── Modulversionen ───
        var v0 = await Versions(o);
        check(S(v0, "latest_version") == "0" && S(v0, "has_unpublished_changes") == "true" && S(v0, "reference_id").Length > 0, "M1 neues Modul: keine Version, Entwurf mit Änderungen, stabile reference_id");
        var p1 = await o.PostAsync($"/api/modules/{modId}/versions", Body(new { note = "Erste Fassung" }));
        var p1j = await J(p1);
        check(p1.StatusCode == HttpStatusCode.Created && S(p1j, "version") == "1" && S(p1j, "note") == "Erste Fassung", "M2 Modulversion 1 veröffentlicht");
        check((await o.PostAsync($"/api/modules/{modId}/versions", Body(new { }))).StatusCode == HttpStatusCode.Conflict && S(await Versions(o), "has_unpublished_changes") == "false", "M3 ohne Änderung keine weitere Version (409), Entwurf = Version 1");
        await Edit("Neu");
        check(S(await Versions(o), "has_unpublished_changes") == "true", "M4 geänderter Entwurf wird als unveröffentlichte Änderung angezeigt");
        var p2 = await J(await o.PostAsync($"/api/modules/{modId}/versions", Body(new { })));
        var vs = (await Versions(o))!["versions"]!.AsArray();
        var snap1 = S(vs.Single(v => S(v, "version") == "1"), "snapshot_module_id"); var snap2 = S(p2, "snapshot_module_id");
        check(S(p2, "version") == "2" && vs.Count == 2 && await DefOf(o, snap1) == "Alt" && await DefOf(o, snap2) == "Neu", "M5 Version 2 angelegt; Version 1 bleibt unverändert lesbar (alter Inhalt)");
        var ownerView = await o.GetAsync($"/module/{snap1}");
        check(ownerView.StatusCode == HttpStatusCode.OK, "M5b der Eigentümer kann den Stand einer früheren Modulversion in der Oberfläche öffnen (nur lesend)");
        await Edit("Noch neuer");
        check(await DefOf(o, snap1) == "Alt" && await DefOf(o, snap2) == "Neu" && await DefOf(o, modId) == "Noch neuer", "M6 weitere Bearbeitung ändert keine veröffentlichte Version");
        check((await x.GetAsync($"/api/modules/{modId}/versions")).StatusCode == HttpStatusCode.NotFound && (await x.PostAsync($"/api/modules/{modId}/versions", Body(new { }))).StatusCode == HttpStatusCode.NotFound
              && (await x.GetAsync($"/api/cards/?module_id={snap1}")).IsSuccessStatusCode && (await J(await x.GetAsync($"/api/cards/?module_id={snap1}")))!.AsArray().Count == 0, "M7 fremder Benutzer: Versionen, Veröffentlichen und die eingefrorenen Kopien bleiben unzugänglich");
        var snapCard = (await J(await o.GetAsync($"/api/cards/?module_id={snap2}")))!.AsArray().Single();
        check((await o.PutAsync($"/api/cards/{S(snapCard, "id")}", Body(new { module = "x", category = "K", term = "t", question = "Q?", definition = "Manipulation" }))).StatusCode == HttpStatusCode.NotFound && await DefOf(o, snap2) == "Neu", "M8 auch der Eigentümer kann eine veröffentlichte Modulversion nicht ändern");

        // ─── Thema bindet Modulversionen ───
        var topicDef = new JsonObject { ["format"] = "learny-topic", ["schema_version"] = 1, ["name"] = "MV Thema", ["modules"] = new JsonArray(new JsonObject { ["module_name"] = "MV-Modul" }) }.ToJsonString();
        var tid = S(await J(await o.PostAsync("/api/topics/import", Raw(topicDef))), "id");
        var draft = await J(await o.GetAsync($"/api/topics/{tid}"));
        check(S(draft, "modules.0.version") == "3" && S(draft, "modules.0.newer_available") == "false", "T1 Zuordnung hält den ausgewählten Modulstand fest (aktueller Stand = Version 3)");
        await o.PostAsync($"/api/topics/{tid}/versions", Body(new { }));
        var t1 = await J(await o.GetAsync($"/api/topics/{tid}?version=1"));
        var frozen1 = S(t1, "modules.0.module_id");
        check(S(t1, "modules.0.version") == "3" && await DefOf(o, frozen1) == "Noch neuer", "T2 Themenversion 1 enthält Modulversion 3");
        check((await o.PostAsync($"/api/topics/{tid}/modules/{modId}/adopt", Body(new { version = 1 }))).IsSuccessStatusCode && S(await J(await o.GetAsync($"/api/topics/{tid}")), "unpublished_changes") == "true", "T3 ausdrückliche Übernahme einer anderen Modulversion (1) macht den Entwurf zur unveröffentlichten Änderung");
        await o.PostAsync($"/api/topics/{tid}/modules/{modId}/adopt", Body(new { version = 3 }));
        check(S(await J(await o.GetAsync($"/api/topics/{tid}")), "unpublished_changes") == "false", "T4 Rückkehr zur veröffentlichten Modulversion: wieder keine Änderung");

        await Edit("Danach");
        var d2 = await J(await o.GetAsync($"/api/topics/{tid}"));
        check(S(d2, "unpublished_changes") == "false" && S(d2, "modules.0.version") == "3" && S(d2, "modules.0.module_has_unpublished_changes") == "true" && S(d2, "modules.0.newer_available") == "true",
            "T5 geändertes Modul: Hinweis „neuere Version“, aber keine stillschweigende Übernahme in den Themenentwurf");
        var pub = await o.PostAsync($"/api/topics/{tid}/versions", Body(new { }));
        check(pub.StatusCode == HttpStatusCode.Conflict, "T6 Veröffentlichen ohne ausdrückliche Übernahme erzeugt keine neue Themenversion (409)");
        await o.PostAsync($"/api/topics/{tid}/modules/{modId}/adopt", Body(new { }));
        var d3 = await J(await o.GetAsync($"/api/topics/{tid}"));
        check(S(d3, "modules.0.version") == "4" && S(d3, "unpublished_changes") == "true", "T7 Übernahme ohne Versionsangabe: aktueller Stand wird als Modulversion 4 festgelegt");
        await o.PostAsync($"/api/topics/{tid}/versions", Body(new { }));
        var t2 = await J(await o.GetAsync($"/api/topics/{tid}?version=2"));
        var t1Again = await J(await o.GetAsync($"/api/topics/{tid}?version=1"));
        check(S(t2, "modules.0.version") == "4" && await DefOf(o, S(t2, "modules.0.module_id")) == "Danach" && S(t1Again, "modules.0.version") == "3" && await DefOf(o, S(t1Again, "modules.0.module_id")) == "Noch neuer",
            "T8 Themenversion 2 enthält Modulversion 4; Themenversion 1 bleibt bei Modulversion 3 unverändert");

        // ─── Freigabe: persönliche Freigabe enthält die festgelegten Modulversionen ───
        await o.PostAsync($"/api/topics/{tid}/shares", Body(new { user_id = leaId }));
        var sharedV1 = await J(await lea.GetAsync($"/api/topics/{tid}?version=1"));
        var leaSeesOriginal = (await lea.GetAsync($"/api/cards/?module_id={modId}")).IsSuccessStatusCode && (await J(await lea.GetAsync($"/api/cards/?module_id={modId}")))!.AsArray().Count == 0;
        check(await DefOf(lea, S(sharedV1, "modules.0.module_id")) == "Noch neuer" && await DefOf(lea, frozen1) == "Noch neuer" && leaSeesOriginal && (await lea.GetAsync($"/api/cards/?module_id={snap1}")).IsSuccessStatusCode
              && (await J(await lea.GetAsync($"/api/cards/?module_id={snap1}")))!.AsArray().Count == 0,
            "F1 persönliche Freigabe: Empfänger lesen die festgelegten Modulversionen im Themenkontext; das private Ursprungsmodul und nicht verwendete Modulversionen bleiben geschützt");
        await Edit("Nach der Freigabe");
        check(await DefOf(lea, S(await J(await lea.GetAsync($"/api/topics/{tid}?version=2")), "modules.0.module_id")) == "Danach", "F2 spätere Modulbearbeitung ändert freigegebene Themenversionen nicht");
        await o.DeleteAsync($"/api/topics/{tid}/shares/{leaId}");
        check((await lea.GetAsync($"/api/topics/{tid}?version=1")).StatusCode == HttpStatusCode.NotFound, "F3 Widerruf entzieht den Zugriff");
    }
}
