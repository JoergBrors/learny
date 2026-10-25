using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

// Integrationstests für Mehrbenutzer-Isolation und Rollen (ADR 0011).
// Aufruf: dotnet run --project tests/LearnCards.IntegrationTests [-- <pfad-zur-LearnCards.Web.dll>]
// Optional per Umgebungsvariable TEST_DB_PROVIDER=postgres + POSTGRES_* gegen PostgreSQL testen.

var repoRoot = FindRepoRoot();
// Pfade der gebauten DLLs: Argument bzw. Umgebungsvariablen TEST_WEB_DLL / TEST_MCP_DLL, sonst bin/Debug
var webDll = args.Length > 0 ? args[0]
    : Environment.GetEnvironmentVariable("TEST_WEB_DLL") ?? Path.Combine(repoRoot, "src", "LearnCards.Web", "bin", "Debug", "net10.0", "LearnCards.Web.dll");
var mcpDll = Environment.GetEnvironmentVariable("TEST_MCP_DLL") ?? Path.Combine(repoRoot, "src", "LearnCards.McpServer", "bin", "Debug", "net10.0", "LearnCards.McpServer.dll");
if (!File.Exists(webDll) || !File.Exists(mcpDll))
{
    Console.Error.WriteLine($"DLL nicht gefunden: {(File.Exists(webDll) ? mcpDll : webDll)}\nZuerst 'dotnet build LearnCards.slnx' ausführen.");
    return 2;
}

var tmp = Path.Combine(Path.GetTempPath(), "lc-it-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmp);
var port = FreePort();
var baseUrl = $"http://127.0.0.1:{port}";
var mcpPort = FreePort();
var mcpUrl = $"http://127.0.0.1:{mcpPort}";
const string McpKey = "it-test-key";

var psi = new ProcessStartInfo("dotnet", $"\"{webDll}\"")
{
    WorkingDirectory = tmp,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
};
void Env(string k, string v) => psi.Environment[k] = v;
Env("ASPNETCORE_URLS", baseUrl);
Env("ASPNETCORE_ENVIRONMENT", "Production");
Env("AUTH_MODE", "dev");
Env("MCP_API_KEY", McpKey);
Env("MCP_PUBLIC_URL", mcpUrl);
Env("OPENAI_API_KEY", "");
Env("DATA_PROTECTION_DIR", Path.Combine(tmp, "keys"));
var provider = Environment.GetEnvironmentVariable("TEST_DB_PROVIDER") ?? "sqlite";
Env("DB_PROVIDER", provider);
if (provider == "sqlite") Env("SQLITE_PATH", Path.Combine(tmp, "it.db"));

using var server = Process.Start(psi)!;
Process? mcpServer = null;
var log = new System.Text.StringBuilder();
server.OutputDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine(e.Data); };
server.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine(e.Data); };
server.BeginOutputReadLine();
server.BeginErrorReadLine();

var failures = 0;
var checks = 0;
void Check(bool ok, string name)
{
    checks++;
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "  OK  " : " FAIL ")} {name}");
}

HttpClient NewClient(bool cookies = true)
{
    var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = cookies, CookieContainer = new CookieContainer() };
    return new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
}

async Task<HttpClient> LoginAsync(string? devUser)
{
    var c = NewClient();
    var r = await c.GetAsync("/auth/login" + (devUser is null ? "" : "?devUser=" + devUser));
    if (r.StatusCode != HttpStatusCode.Redirect) throw new Exception($"Login {devUser} fehlgeschlagen: {r.StatusCode}");
    return c;
}

static byte[] PngLike(int n, int seed)
{
    var b = new byte[n];
    new Random(seed).NextBytes(b);
    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
    return b;
}

static void StopProcess(Process? p)
{
    try { if (p is { HasExited: false }) { p.Kill(true); p.WaitForExit(5000); } } catch { }
}

var perfRows = new List<string>();

// Startet eine weitere Web-Instanz (SQLite, Dev-Auth) mit zusätzlichen Umgebungsvariablen — für Provider-Ende-zu-Ende-Läufe
async Task<(string Url, Process Proc)> StartWebAsync(string label, Dictionary<string, string> extraEnv)
{
    var url = $"http://127.0.0.1:{FreePort()}";
    var dir = Path.Combine(tmp, label);
    Directory.CreateDirectory(dir);
    var info = new ProcessStartInfo("dotnet", $"\"{webDll}\"") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
    info.Environment["ASPNETCORE_URLS"] = url;
    info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
    info.Environment["AUTH_MODE"] = "dev";
    info.Environment["MCP_API_KEY"] = McpKey;
    info.Environment["OPENAI_API_KEY"] = "";
    info.Environment["DB_PROVIDER"] = "sqlite";
    info.Environment["SQLITE_PATH"] = Path.Combine(dir, "e2e.db");
    info.Environment["DATA_PROTECTION_DIR"] = Path.Combine(dir, "keys");
    foreach (var (k, v) in extraEnv) info.Environment[k] = v;
    var proc = Process.Start(info)!;
    var output = new System.Text.StringBuilder();
    proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) output.AppendLine(e.Data); lock (log) log.AppendLine($"[{label}] {e.Data}"); } };
    proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) output.AppendLine(e.Data); lock (log) log.AppendLine($"[{label}] {e.Data}"); } };
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();
    using var probe = NewClient(false);
    for (var i = 0; i < 100 && !proc.HasExited; i++)
    {
        try { if ((await probe.GetAsync(url + "/health")).IsSuccessStatusCode) return (url, proc); } catch { await Task.Delay(250); }
    }
    StopProcess(proc);
    throw new Exception($"Web-Instanz '{label}' wurde nicht bereit:\n{output}");
}

// Provider-unabhängiges Szenario: dieselben Prüfungen für jeden Bildspeicher (ADR 0017/0018)
async Task ImageScenario(string label, string url, Func<int>? filesOnDisk = null)
{
    var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() };
    var c = new HttpClient(handler) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(60) };
    await c.GetAsync("/auth/login?devUser=e2e" + label.Replace("-", ""));
    var other = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(60) };
    await other.GetAsync("/auth/login?devUser=other" + label.Replace("-", ""));
    static StringContent J2(object o) => new(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");
    async Task<JsonNode?> Parse(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync());

    var d1 = PngLike(1_000, 1); var d2 = PngLike(60_000, 2); var d3 = PngLike(300_000, 3);
    object Entry(byte[] d, string alt) => new { mime_type = "image/png", data_base64 = Convert.ToBase64String(d), alt_text = alt, caption = "" };
    var sw = Stopwatch.StartNew();
    var create = await c.PostAsync("/api/cards/", J2(new { module = "E2E-" + label, category = "B", term = "T", question = "Q?", definition = "D", images = new[] { Entry(d1, "a"), Entry(d2, "b"), Entry(d3, "c") } }));
    var putMs = sw.ElapsedMilliseconds;
    Check(create.StatusCode == HttpStatusCode.Created, $"E2E[{label}]: Karte mit 3 Bildern (≈361 KB) wird gespeichert");
    var cardId = (await Parse(create))!["id"]!.GetValue<string>();
    var cardJson = (await Parse(await c.GetAsync($"/api/cards/{cardId}")))!;
    var imgs = cardJson["images"]!.AsArray();
    var expected = new[] { d1, d2, d3 };
    var allSame = imgs.Count == 3;
    for (var i = 0; allSame && i < 3; i++)
    {
        var res = await c.GetAsync("/api/images/" + imgs[i]!["image_id"]!.GetValue<string>());
        allSame = res.IsSuccessStatusCode && (await res.Content.ReadAsByteArrayAsync()).SequenceEqual(expected[i]) && res.Content.Headers.ContentType?.MediaType == "image/png";
    }
    Check(allSame, $"E2E[{label}]: alle Bilder werden byte-identisch über /api/images/{{id}} ausgeliefert");
    var cardText = cardJson.ToJsonString();
    Check(cardText.Contains("/api/images/") && !cardText.Contains("sig=") && !cardText.Contains("blob.core") && !cardText.Contains("X-Amz") && !cardText.Contains("sharepoint.com") && !cardText.Contains("downloadUrl"),
        $"E2E[{label}]: Kartenantwort enthält nur LearnCards-Bild-URLs (keine SAS-/Cloud-/Graph-URL)");
    var fullData = (await Parse(await c.GetAsync($"/api/cards/{cardId}?include_image_data=true")))!["images"]!.AsArray();
    Check(fullData.Count == 3 && Convert.FromBase64String(fullData[2]!["data_base64"]!.GetValue<string>()).SequenceEqual(d3), $"E2E[{label}]: include_image_data liefert dieselben Daten");
    var id3 = imgs[2]!["image_id"]!.GetValue<string>();
    var etag = (await c.GetAsync("/api/images/" + id3)).Headers.ETag?.Tag;
    using (var inm = new HttpRequestMessage(HttpMethod.Get, "/api/images/" + id3)) { inm.Headers.TryAddWithoutValidation("If-None-Match", etag); Check((await c.SendAsync(inm)).StatusCode == HttpStatusCode.NotModified, $"E2E[{label}]: ETag-Revalidierung → 304"); }
    var redirectProbe = await c.GetAsync("/api/images/" + id3);
    Check(redirectProbe.StatusCode == HttpStatusCode.OK && redirectProbe.Headers.Location is null, $"E2E[{label}]: der Proxy liefert Bytes (200), nie eine Weiterleitung auf Cloud-URLs");
    Check((await other.GetAsync("/api/images/" + id3)).StatusCode == HttpStatusCode.NotFound, $"E2E[{label}]: anderer Benutzer erhält 404");
    Check((await NewClient().GetAsync(url + "/api/images/" + id3)).StatusCode == HttpStatusCode.Unauthorized, $"E2E[{label}]: ohne Anmeldung 401");

    // Performance: erster Abruf (kalt) und Median weiterer Abrufe (warm: Cache bzw. direkter Zugriff)
    var cold = Stopwatch.StartNew();
    await (await c.GetAsync("/api/images/" + imgs[1]!["image_id"]!.GetValue<string>())).Content.ReadAsByteArrayAsync();
    var coldMs = cold.ElapsedMilliseconds;
    var times = new List<long>();
    for (var i = 0; i < 20; i++)
    {
        var t = Stopwatch.StartNew();
        await (await c.GetAsync("/api/images/" + id3)).Content.ReadAsByteArrayAsync();
        times.Add(t.ElapsedMilliseconds);
    }
    times.Sort();
    Console.WriteLine($"  PERF [{label}] Speichern 3 Bilder (361 KB): {putMs} ms · Abruf 60 KB (erster): {coldMs} ms · Abruf 300 KB Median/P95 (n=20): {times[10]} / {times[18]} ms");
    perfRows.Add($"{label}|{putMs}|{coldMs}|{times[10]}|{times[18]}");

    if (filesOnDisk is not null) Check(filesOnDisk() == 3, $"E2E[{label}]: genau 3 Objekte im Speicher");

    // Ersetzen: eines behalten, eines neu, zwei entfernen → Aufräumen im Speicher
    var removedId = imgs[0]!["image_id"]!.GetValue<string>();
    var keepId = imgs[1]!["image_id"]!.GetValue<string>();
    var replace = await c.PutAsync($"/api/cards/{cardId}", J2(new { module = "E2E-" + label, category = "B", term = "T", question = "Q?", definition = "D", images = new object[] { new { image_id = keepId }, Entry(PngLike(2_000, 9), "neu") } }));
    Check(replace.IsSuccessStatusCode, $"E2E[{label}]: Bilder ersetzen (behalten + neu)");
    Check((await c.GetAsync("/api/images/" + removedId)).StatusCode == HttpStatusCode.NotFound && (await c.GetAsync("/api/images/" + keepId)).IsSuccessStatusCode, $"E2E[{label}]: entferntes Bild ist weg, behaltenes bleibt lesbar");
    Check((await c.GetAsync("/api/images/" + id3)).StatusCode == HttpStatusCode.NotFound, $"E2E[{label}]: entferntes (zuvor gecachtes) Bild liefert nach Invalidierung 404");
    if (filesOnDisk is not null) Check(filesOnDisk() == 2, $"E2E[{label}]: entfernte Binärdaten wurden aus dem Speicher gelöscht (2 verbleiben)");
    await c.DeleteAsync($"/api/cards/{cardId}");
    Check((await c.GetAsync("/api/images/" + keepId)).StatusCode == HttpStatusCode.NotFound, $"E2E[{label}]: nach Löschen der Karte sind die Bilder nicht mehr abrufbar");
    if (filesOnDisk is not null) Check(filesOnDisk() == 0, $"E2E[{label}]: nach Löschen der Karte ist der Speicher leer");
}

try
{
    await ImageStorageUnitTests.RunAsync(Check, tmp);
    await ImageStorageRemoteUnitTests.RunAsync(Check);
    Console.WriteLine();

    // ─── Warten bis der Server antwortet ───────────────────────────────────
    using (var probe = NewClient(false))
    {
        var ready = false;
        for (var i = 0; i < 80 && !ready && !server.HasExited; i++)
        {
            try { ready = (await probe.GetAsync("/health")).IsSuccessStatusCode; } catch { await Task.Delay(250); }
        }
        if (!ready) throw new Exception("Server wurde nicht bereit.\n" + log);
    }

    Console.WriteLine($"Server läuft auf {baseUrl} (DB: {provider}, Verzeichnis {tmp})");

    // MCP-Server (HTTP, OAuth aktiv) gegen die Web-API
    var mcpConfig = Path.Combine(tmp, "mcpsettings.json");
    File.WriteAllText(mcpConfig, JsonSerializer.Serialize(new
    {
        serverName = "learncards-it",
        upstreamApi = new { baseUrl = baseUrl + "/api", apiKey = McpKey },
        transports = new { stdio = new { enabled = false }, http = new { enabled = true, urls = new[] { mcpUrl } } },
        oauth = new { enabled = true, issuer = mcpUrl, audience = "learncards-mcp", signingKey = "", tokenLifetimeMinutes = 60, clients = Array.Empty<object>() },
    }));
    var mcpPsi = new ProcessStartInfo("dotnet", $"\"{mcpDll}\"") { WorkingDirectory = tmp, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
    mcpPsi.Environment["LEARNCARDS_MCP_CONFIG"] = mcpConfig;
    mcpServer = Process.Start(mcpPsi)!;
    mcpServer.OutputDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine("[mcp] " + e.Data); };
    mcpServer.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine("[mcp] " + e.Data); };
    mcpServer.BeginOutputReadLine();
    mcpServer.BeginErrorReadLine();
    using (var probe = NewClient(false))
    {
        var ready = false;
        for (var i = 0; i < 80 && !ready && !mcpServer.HasExited; i++)
        {
            try { ready = (await probe.GetAsync(mcpUrl + "/health")).IsSuccessStatusCode; } catch { await Task.Delay(250); }
        }
        if (!ready) throw new Exception("MCP-Server wurde nicht bereit.\n" + log);
    }
    Console.WriteLine($"MCP-Server läuft auf {mcpUrl}\n");

    var admin = await LoginAsync(null);          // local-dev-user = Bootstrap-Administrator im Dev-Modus
    var alice = await LoginAsync("alice");
    var bob = await LoginAsync("bob");
    var anon = NewClient();
    var key = NewClient(false);
    key.DefaultRequestHeaders.Add("X-MCP-Key", McpKey);

    async Task<JsonNode?> J(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync());
    static StringContent Body(object o) => new(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

    Console.WriteLine("Authentifizierung");
    Check((await anon.GetAsync("/api/modules/")).StatusCode == HttpStatusCode.Unauthorized, "ohne Login: 401");

    var meAlice = (await J(await alice.GetAsync("/api/me")))!;
    var meBob = (await J(await bob.GetAsync("/api/me")))!;
    var meAdmin = (await J(await admin.GetAsync("/api/me")))!;
    var aliceId = meAlice["id"]!.GetValue<string>();
    var bobId = meBob["id"]!.GetValue<string>();
    var adminId = meAdmin["id"]!.GetValue<string>();
    Check(aliceId != bobId && bobId != adminId, "jeder Benutzer hat eine eigene interne ID");
    Check(Guid.TryParse(aliceId, out _), "interne Benutzer-ID ist eine GUID");
    Check(meAlice["roles"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(new[] { "user" }), "Alice hat nur Rolle 'user'");
    Check(meAdmin["roles"]!.AsArray().Any(x => x!.GetValue<string>() == "admin"), "Bootstrap-Admin hat Rolle 'admin'");

    Console.WriteLine("\nDatenisolation");
    var fixedCardId = Guid.NewGuid().ToString();
    var importRes = await alice.PostAsync("/api/import/cards", Body(new
    {
        cards = new object[]
        {
            new { id = fixedCardId, module = "Geheim", category = "A", term = "Alice-Term", question = "Q?", definition = "D" },
            new { module = "Geheim", category = "A", term = "Alice-Term-2", question = "Q2?", definition = "D2" },
        },
        overwrite_existing = false,
    }));
    Check(importRes.IsSuccessStatusCode && (await J(importRes))!["created"]!.GetValue<int>() == 2, "Alice importiert 2 Karten");

    var aliceModules = (await J(await alice.GetAsync("/api/modules/")))!.AsArray();
    var aliceModuleId = aliceModules.Single()!["id"]!.GetValue<string>();
    Check(aliceModules.Count == 1, "Alice sieht ihr Modul");

    Check((await J(await bob.GetAsync("/api/modules/")))!.AsArray().Count == 0, "Bob sieht keine Module von Alice");
    Check((await J(await bob.GetAsync("/api/cards/")))!.AsArray().Count == 0, "Bob sieht keine Karten von Alice (Liste)");
    Check((await J(await bob.GetAsync($"/api/cards/?module_id={aliceModuleId}")))!.AsArray().Count == 0, "Bob sieht keine Karten über fremde module_id");
    Check((await bob.GetAsync($"/api/cards/{fixedCardId}")).StatusCode == HttpStatusCode.NotFound, "Bob: GET fremde Karte → 404");

    var upd = await bob.PutAsync($"/api/cards/{fixedCardId}", Body(new { module = "Geheim", category = "A", term = "Gehackt", question = "Q", definition = "D" }));
    Check(upd.StatusCode == HttpStatusCode.NotFound, "Bob: PUT fremde Karte → 404");
    Check((await bob.PatchAsync($"/api/cards/{fixedCardId}/archive", null)).StatusCode == HttpStatusCode.NotFound, "Bob: archivieren fremder Karte → 404");
    Check((await bob.DeleteAsync($"/api/cards/{fixedCardId}")).StatusCode == HttpStatusCode.NotFound, "Bob: DELETE fremde Karte → 404");
    Check((await bob.DeleteAsync($"/api/modules/{aliceModuleId}")).StatusCode == HttpStatusCode.NotFound, "Bob: DELETE fremdes Modul → 404");
    Check((await bob.GetAsync($"/api/cards/{fixedCardId}/state")).StatusCode == HttpStatusCode.NotFound, "Bob: Kartenstatus fremder Karte → 404");
    Check((await bob.PutAsync($"/api/cards/{fixedCardId}/state", Body(new { is_checked = true, marked_review = false }))).StatusCode == HttpStatusCode.NotFound, "Bob: Status setzen auf fremder Karte → 404");
    Check((await bob.PostAsync($"/api/cards/{fixedCardId}/quiz", null)).StatusCode == HttpStatusCode.BadRequest, "Bob: Karten-Quiz fremder Karte abgewiesen");
    Check((await bob.PostAsync("/api/quiz/start", Body(new { module_id = aliceModuleId, category = "", card_id = fixedCardId, num_questions = 3 }))).StatusCode == HttpStatusCode.NotFound, "Bob: Quiz-Start auf fremde Karte → 404");
    Check((await bob.PostAsync("/api/quiz/submit", Body(new { module_id = aliceModuleId, category = "", card_id = "", answers = Array.Empty<object>() }))).StatusCode == HttpStatusCode.NotFound, "Bob: Quiz-Ergebnis auf fremdes Modul → 404");
    Check((await bob.PostAsync("/api/chat/", Body(new { card_id = fixedCardId, messages = Array.Empty<object>() }))).StatusCode == HttpStatusCode.NotFound, "Bob: Chat zu fremder Karte → 404");
    Check((await bob.GetAsync($"/api/chat/history?card_id={fixedCardId}")).IsSuccessStatusCode
          && (await J(await bob.GetAsync($"/api/chat/history?card_id={fixedCardId}")))!.AsArray().Count == 0, "Bob: Chat-Verlauf fremder Karte leer");
    Check((await bob.GetAsync("/api/cards/not-a-guid")).StatusCode == HttpStatusCode.NotFound, "manipulierte ID ('not-a-guid') → 404 statt 500");
    Check((await bob.GetAsync("/api/cards/?module_id=' OR 1=1 --")).IsSuccessStatusCode, "SQL-Injektionsversuch in module_id bleibt wirkungslos");

    var stillThere = (await J(await alice.GetAsync($"/api/cards/{fixedCardId}")))!;
    Check(stillThere["term"]!.GetValue<string>() == "Alice-Term", "Alices Karte ist unverändert");

    Console.WriteLine("\nGleichnamige Module / fremde Karten-IDs");
    var bobModule = await bob.PostAsync("/api/modules/", Body(new { name = "Geheim" }));
    Check(bobModule.StatusCode == HttpStatusCode.Created, "Bob darf ein Modul gleichen Namens anlegen (Name nur pro Eigentümer eindeutig)");
    var bobImport = await bob.PostAsync("/api/import/cards", Body(new
    {
        cards = new object[] { new { id = fixedCardId, module = "Geheim", category = "A", term = "Bob-Term", question = "Q?", definition = "D" } },
        overwrite_existing = true,
    }));
    var bobImportJson = (await J(bobImport))!;
    Check(bobImport.IsSuccessStatusCode && bobImportJson["created"]!.GetValue<int>() == 1 && bobImportJson["updated"]!.GetValue<int>() == 0,
        "Import mit fremder Karten-ID legt neue eigene Karte an (überschreibt nichts)");
    Check((await J(await alice.GetAsync($"/api/cards/{fixedCardId}")))!["term"]!.GetValue<string>() == "Alice-Term", "Alices Karte nach Bobs Import weiterhin unverändert");
    var bobCards = (await J(await bob.GetAsync("/api/cards/")))!.AsArray();
    Check(bobCards.Count == 1 && bobCards[0]!["id"]!.GetValue<string>() != fixedCardId, "Bobs Karte hat eine neue, eigene ID");

    Console.WriteLine("\nLernergebnisse");
    await alice.PutAsync($"/api/cards/{fixedCardId}/state", Body(new { is_checked = true, marked_review = false }));
    var start = await alice.PostAsync("/api/quiz/start", Body(new { module_id = aliceModuleId, category = "", card_id = "", num_questions = 2 }));
    Check(start.IsSuccessStatusCode, "Alice startet Quiz mit eigener markierter Karte");
    var submit = await alice.PostAsync("/api/quiz/submit", Body(new
    {
        module_id = aliceModuleId, category = "", card_id = fixedCardId,
        answers = new object[] { new { question = "Q?", user_answer = "D", topic = "A", source_card_id = fixedCardId, source_term = "Alice-Term" } },
    }));
    Check(submit.IsSuccessStatusCode, "Alice speichert ein Quiz-Ergebnis");
    Check((await J(await alice.GetAsync("/api/quiz/history")))!.AsArray().Count == 1, "Alice sieht ihr Ergebnis");
    Check((await J(await bob.GetAsync("/api/quiz/history")))!.AsArray().Count == 0, "Bob sieht Alices Ergebnis nicht");
    Check((await J(await bob.GetAsync($"/api/quiz/history?module_id={aliceModuleId}")))!.AsArray().Count == 0, "Bob sieht Alices Ergebnis auch nicht über module_id");
    var historyId = (await J(await alice.GetAsync("/api/quiz/history")))!.AsArray()[0]!["id"]!.GetValue<string>();
    Check((await bob.DeleteAsync($"/api/quiz/history/{historyId}")).StatusCode == HttpStatusCode.NotFound, "Bob kann Alices Ergebnis nicht löschen");

    Console.WriteLine("\nRollen und Administration");
    Check((await bob.GetAsync("/api/admin/users")).StatusCode == HttpStatusCode.Forbidden, "Bob: Admin-API → 403");
    Check((await bob.PutAsync($"/api/admin/users/{bobId}/roles", Body(new { roles = new[] { "admin" } }))).StatusCode == HttpStatusCode.Forbidden, "Bob kann sich nicht selbst zum Admin machen");
    Check((await bob.PutAsync("/api/me", Body(new { display_name = "Bobby", roles = new[] { "admin" } }))).StatusCode == HttpStatusCode.NoContent, "Bob ändert Anzeigename (Rollenfeld im Body wird ignoriert)");
    Check(!(await J(await bob.GetAsync("/api/me")))!["roles"]!.AsArray().Any(x => x!.GetValue<string>() == "admin"), "Bob ist weiterhin kein Admin");
    Check((await J(await bob.GetAsync("/api/me")))!["display_name"]!.GetValue<string>() == "Bobby", "Anzeigename wurde gespeichert");

    var users = (await J(await admin.GetAsync("/api/admin/users")))!.AsArray();
    Check(users.Count >= 3, "Admin sieht alle Benutzer");

    Check((await admin.PutAsync($"/api/admin/users/{aliceId}/roles", Body(new { roles = new[] { "auditor" } }))).StatusCode == HttpStatusCode.NoContent, "Admin vergibt Rolle 'auditor' an Alice");
    var aliceRoles = (await J(await alice.GetAsync("/api/me")))!["roles"]!.AsArray().Select(x => x!.GetValue<string>()).OrderBy(x => x).ToArray();
    Check(aliceRoles.SequenceEqual(new[] { "auditor", "user" }), "Alice hat jetzt 'auditor' + 'user'");
    Check((await alice.GetAsync("/api/admin/users")).StatusCode == HttpStatusCode.Forbidden, "Auditor hat keinen Zugriff auf Admin-API");
    Check((await alice.PutAsync($"/api/admin/users/{bobId}/roles", Body(new { roles = new[] { "auditor" } }))).StatusCode == HttpStatusCode.Forbidden, "Auditor kann keine Rollen vergeben");
    Check((await admin.PutAsync($"/api/admin/users/{adminId}/roles", Body(new { roles = Array.Empty<string>() }))).StatusCode == HttpStatusCode.BadRequest, "Letzter Admin kann sich die Rolle nicht entziehen");
    Check((await admin.PutAsync($"/api/admin/users/{bobId}/roles", Body(new { roles = new[] { "superuser" } }))).StatusCode == HttpStatusCode.BadRequest, "Unbekannte Rolle wird abgelehnt");
    Check((await J(await admin.GetAsync("/api/admin/audit")))!.AsArray().Count > 0, "Rollenänderungen sind im Audit-Log protokolliert");
    Check((await J(await admin.GetAsync("/api/cards/")))!.AsArray().Count == 0, "Admin sieht fremde private Karten nicht über die normale API");

    Console.WriteLine("\nRollen aus dem Identity Provider (Entra-ID-App-Rollen, ADR 0012)");
    async Task<string[]> RolesOf(HttpClient c) =>
        (await J(await c.GetAsync("/api/me")))!["roles"]!.AsArray().Select(x => x!.GetValue<string>()).OrderBy(x => x).ToArray();

    var carol = await LoginAsync("carol&devRoles=Admin.All");   // simuliert roles-Claim "Admin.All"
    Check((await RolesOf(carol)).SequenceEqual(new[] { "admin", "user" }), "Claim 'Admin.All' vergibt interne Rolle 'admin'");
    Check((await carol.GetAsync("/api/admin/users")).IsSuccessStatusCode, "IdP-Admin hat Zugriff auf die Administration");
    var carolPlain = await LoginAsync("carol&devRoles=Users");  // 'Users' ist keine verwaltete Rolle; Admin.All fehlt jetzt
    Check((await RolesOf(carolPlain)).SequenceEqual(new[] { "user" }), "Fehlt 'Admin.All' im nächsten Login, wird die IdP-Rolle entzogen");
    Check((await carolPlain.GetAsync("/api/admin/users")).StatusCode == HttpStatusCode.Forbidden, "Entzogene IdP-Admin-Rolle wirkt sofort");
    var carolUnknown = await LoginAsync("carol&devRoles=Irgendwas,Admin");  // nicht abgebildete Rollennamen
    Check((await RolesOf(carolUnknown)).SequenceEqual(new[] { "user" }), "Nicht abgebildete Rollenwerte werden ignoriert (kein 'Admin' ohne Mapping)");
    var carolId = (await J(await carolUnknown.GetAsync("/api/me")))!["id"]!.GetValue<string>();
    await admin.PutAsync($"/api/admin/users/{carolId}/roles", Body(new { roles = new[] { "auditor" } }));
    var carolAgain = await LoginAsync("carol&devRoles=Users");
    Check((await RolesOf(carolAgain)).SequenceEqual(new[] { "auditor", "user" }), "Manuell vergebene Rolle bleibt beim IdP-Abgleich erhalten");

    Console.WriteLine("\nMCP-Schlüssel (technischer Zugang)");
    Check((await key.GetAsync("/api/schema/import/cards")).IsSuccessStatusCode, "Schlüssel: Schema ist abrufbar");
    Check((await key.GetAsync("/api/modules/")).StatusCode == HttpStatusCode.Forbidden, "Dienstschlüssel trägt keinen Benutzer: kein Datenzugriff (403)");
    using (var spoof = new HttpRequestMessage(HttpMethod.Get, "/api/modules/"))
    {
        spoof.Headers.Add("X-MCP-Key", McpKey);
        spoof.Headers.Add("X-User-Id", aliceId);
        Check((await key.SendAsync(spoof)).StatusCode == HttpStatusCode.Forbidden, "Mitgesendete Benutzer-ID umgeht die Autorisierung nicht");
    }
    var wrongKey = NewClient(false);
    wrongKey.DefaultRequestHeaders.Add("X-MCP-Key", "falsch");
    Check((await wrongKey.GetAsync("/api/modules/")).StatusCode == HttpStatusCode.Unauthorized, "Falscher Schlüssel → 401");
    Check((await key.GetAsync("/api/admin/users")).StatusCode == HttpStatusCode.Forbidden, "Schlüssel hat keinen Zugriff auf die Administration");


    Console.WriteLine("\nMCP-Zugänge: persönliche Tokens (ADR 0013)");
    HttpClient TokenClient(string token)
    {
        var c = NewClient(false);
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return c;
    }
    var aliceTokenRes = await alice.PostAsync("/api/mcp/links", Body(new { name = "Test-Token", days = 30 }));
    Check(aliceTokenRes.StatusCode == HttpStatusCode.Created, "Alice erzeugt ein persönliches MCP-Token");
    var aliceTokenJson = (await J(aliceTokenRes))!;
    var aliceToken = aliceTokenJson["token"]!.GetValue<string>();
    var aliceLinkId = aliceTokenJson["link"]!["id"]!.GetValue<string>();
    Check(aliceToken.StartsWith("lcm_"), "Token hat das Präfix lcm_");
    var aTok = TokenClient(aliceToken);
    var bobToken = (await J(await bob.PostAsync("/api/mcp/links", Body(new { name = "Bob-Token", days = 30 }))))!["token"]!.GetValue<string>();
    var bTok = TokenClient(bobToken);

    Check((await J(await aTok.GetAsync("/api/mcp/whoami")))!["user_id"]!.GetValue<string>() == aliceId, "Token gehört Alice (whoami)");
    Check((await J(await aTok.GetAsync("/api/modules/")))!.AsArray().Single()!["id"]!.GetValue<string>() == aliceModuleId, "Alices Token sieht nur Alices Modul");
    Check((await J(await bTok.GetAsync("/api/modules/")))!.AsArray().Single()!["id"]!.GetValue<string>() != aliceModuleId, "Bobs Token sieht nur Bobs Modul");
    Check((await bTok.GetAsync($"/api/cards/{fixedCardId}")).StatusCode == HttpStatusCode.NotFound, "Bobs Token kann Alices Karte nicht lesen");
    Check((await bTok.PutAsync($"/api/cards/{fixedCardId}", Body(new { module = "Geheim", category = "A", term = "X", question = "Q", definition = "D" }))).StatusCode == HttpStatusCode.NotFound, "Bobs Token kann Alices Karte nicht ändern");
    var tokCard = await aTok.PostAsync("/api/cards/", Body(new { module = "Geheim", category = "A", term = "Per-Token", question = "Q?", definition = "D" }));
    Check(tokCard.StatusCode == HttpStatusCode.Created, "Alices Token legt eine Karte an");
    Check((await J(await bob.GetAsync("/api/cards/")))!.AsArray().All(c => c!["term"]!.GetValue<string>() != "Per-Token"), "Die per Token angelegte Karte erscheint nicht bei Bob");
    Check((await aTok.GetAsync("/api/me")).StatusCode == HttpStatusCode.Forbidden, "Token: Profil-API gesperrt");
    Check((await aTok.GetAsync("/api/admin/users")).StatusCode == HttpStatusCode.Forbidden, "Token: Admin-API gesperrt");
    Check((await aTok.PostAsync("/api/mcp/links", Body(new { name = "x" }))).StatusCode == HttpStatusCode.Forbidden, "Token kann keine weiteren Zugänge erzeugen");
    Check((await aTok.GetAsync("/api/quiz/history")).StatusCode == HttpStatusCode.Forbidden, "Token: Quiz-/Ergebnis-API gesperrt");
    Check((await TokenClient("lcm_garbage").GetAsync("/api/modules/")).StatusCode == HttpStatusCode.Unauthorized, "Ungültiges Token → 401");
    Check((await TokenClient("kein-lcm-token").GetAsync("/api/modules/")).StatusCode == HttpStatusCode.Unauthorized, "Token ohne Präfix → 401");
    Check((await J(await alice.GetAsync("/api/mcp/links/")))!.AsArray().Count == 1, "Alice sieht ihren Zugang in der Liste");
    Check((await J(await bob.GetAsync("/api/mcp/links/")))!.AsArray().All(l => l!["id"]!.GetValue<string>() != aliceLinkId), "Bob sieht Alices Zugang nicht");
    Check((await bob.DeleteAsync($"/api/mcp/links/{aliceLinkId}")).StatusCode == HttpStatusCode.NotFound, "Bob kann Alices Zugang nicht widerrufen");
    Check((await alice.DeleteAsync($"/api/mcp/links/{aliceLinkId}")).StatusCode == HttpStatusCode.NoContent, "Alice widerruft ihr Token");
    Check((await aTok.GetAsync("/api/modules/")).StatusCode == HttpStatusCode.Unauthorized, "Widerrufenes Token funktioniert nicht mehr (401)");

    Console.WriteLine("\nMCP-Zugänge: Zustimmung und Code-Tausch");
    var goodReturn = $"{mcpUrl}/oauth/callback";
    var evil = await alice.PostAsync("/api/mcp/link/authorize", Body(new { return_url = "https://evil.example/oauth/callback", state = "s1", client_name = "Evil", approve = true }));
    Check(evil.StatusCode == HttpStatusCode.BadRequest, "Fremde Rückleitungs-URL wird abgelehnt (kein offener Redirect)");
    var evil2 = await alice.PostAsync("/api/mcp/link/authorize", Body(new { return_url = goodReturn + "?x=1", state = "s1", client_name = "Evil", approve = true }));
    Check(evil2.StatusCode == HttpStatusCode.BadRequest, "Rückleitungs-URL mit Query wird abgelehnt");
    var denied = (await J(await alice.PostAsync("/api/mcp/link/authorize", Body(new { return_url = goodReturn, state = "s2", client_name = "T", approve = false }))))!["redirect_url"]!.GetValue<string>();
    Check(denied.Contains("error=access_denied") && !denied.Contains("code="), "Ablehnung liefert error=access_denied ohne Code");
    var granted = (await J(await alice.PostAsync("/api/mcp/link/authorize", Body(new { return_url = goodReturn, state = "s3", client_name = "Test", approve = true }))))!["redirect_url"]!.GetValue<string>();
    var grantedQuery = System.Web.HttpUtility.ParseQueryString(new Uri(granted).Query);
    var linkCode = grantedQuery["code"]!;
    Check(grantedQuery["state"] == "s3" && linkCode.Length > 20, "Zustimmung liefert Code und state");
    Check((await alice.PostAsync("/api/mcp/link/exchange", Body(new { code = linkCode }))).StatusCode == HttpStatusCode.Forbidden, "Code-Tausch per Browser-Sitzung ist verboten (nur Dienstschlüssel)");
    var wrong = NewClient(false); wrong.DefaultRequestHeaders.Add("X-MCP-Key", "falsch");
    Check((await wrong.PostAsync("/api/mcp/link/exchange", Body(new { code = linkCode }))).StatusCode == HttpStatusCode.Unauthorized, "Code-Tausch mit falschem Schlüssel → 401");
    var exchanged = await key.PostAsync("/api/mcp/link/exchange", Body(new { code = linkCode }));
    Check(exchanged.IsSuccessStatusCode, "MCP-Server (Dienstschlüssel) tauscht den Code");
    var exchangedToken = (await J(exchanged))!["access_token"]!.GetValue<string>();
    Check((await J(await TokenClient(exchangedToken).GetAsync("/api/mcp/whoami")))!["user_id"]!.GetValue<string>() == aliceId, "Das getauschte Token gehört dem zustimmenden Benutzer");
    Check((await key.PostAsync("/api/mcp/link/exchange", Body(new { code = linkCode }))).StatusCode == HttpStatusCode.BadRequest, "Code ist nur einmal verwendbar");
    Check((await key.PostAsync("/api/mcp/link/exchange", Body(new { code = "erfunden" }))).StatusCode == HttpStatusCode.BadRequest, "Erfundener Code wird abgewiesen");

    Console.WriteLine("\nMCP-Server: OAuth-Anmeldung pro Benutzer (Ende-zu-Ende)");
    var mcp = NewClient(false);
    var meta = await J(await mcp.GetAsync(mcpUrl + "/.well-known/oauth-authorization-server"));
    Check(meta!["code_challenge_methods_supported"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(new[] { "S256" })
          && meta["grant_types_supported"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(new[] { "authorization_code" }), "Metadaten: nur Authorization Code + S256 (kein client_credentials/plain)");
    Check((await mcp.PostAsync(mcpUrl + "/oauth/register", Body(new { client_name = "x", redirect_uris = new[] { "javascript:alert(1)" }, token_endpoint_auth_method = "none" }))).StatusCode == HttpStatusCode.BadRequest, "Registrierung: javascript:-Redirect abgelehnt");
    Check((await mcp.PostAsync(mcpUrl + "/oauth/register", Body(new { client_name = "x", redirect_uris = new[] { "http://example.com/cb" }, token_endpoint_auth_method = "none" }))).StatusCode == HttpStatusCode.BadRequest, "Registrierung: http-Redirect ausserhalb von Loopback abgelehnt");
    Check((await mcp.PostAsync(mcpUrl + "/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = "a", ["client_secret"] = "b" }))).StatusCode == HttpStatusCode.BadRequest, "client_credentials-Grant wird nicht mehr unterstützt");
    var noTok = await mcp.PostAsync(mcpUrl + "/mcp", Body(new { jsonrpc = "2.0", id = 1, method = "tools/list" }));
    Check(noTok.StatusCode == HttpStatusCode.Unauthorized && noTok.Headers.WwwAuthenticate.ToString().Contains("resource_metadata"), "/mcp ohne Token: 401 mit WWW-Authenticate");

    const string cbUri = "http://127.0.0.1:9/cb";
    var regJson = (await J(await mcp.PostAsync(mcpUrl + "/oauth/register", Body(new { client_name = "Inspector-Test", redirect_uris = new[] { cbUri }, token_endpoint_auth_method = "none" }))))!;
    var clientId = regJson["client_id"]!.GetValue<string>();

    // Führt den kompletten Ablauf für einen angemeldeten Learny-Benutzer aus und liefert (Token-Antwort, Authorize-Redirect-Parameter)
    async Task<(string? AccessToken, string Verifier, string Code, string PendingId)> OAuthFlow(HttpClient browserUser, bool completeCallback = true, bool sendCookie = true)
    {
        var verifier = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var authUrl = $"{mcpUrl}/oauth/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(cbUri)}&state=clientstate&code_challenge={challenge}&code_challenge_method=S256&scope=mcp&resource={Uri.EscapeDataString(mcpUrl + "/mcp")}";
        var authRes = await mcp.GetAsync(authUrl);
        if (authRes.StatusCode != HttpStatusCode.Redirect) throw new Exception("authorize: " + authRes.StatusCode + " " + await authRes.Content.ReadAsStringAsync());
        var location = authRes.Headers.Location!;
        var setCookie = authRes.Headers.GetValues("Set-Cookie").First();
        var cookiePair = setCookie.Split(';')[0];
        var q = System.Web.HttpUtility.ParseQueryString(location.Query);
        var pendingId = q["state"]!;
        if (!location.ToString().StartsWith(baseUrl + "/mcp/connect")) throw new Exception("authorize leitet nicht zu Learny: " + location);

        // Der Benutzer stimmt im Browser bei Learny zu (die Seite /mcp/connect ruft dieselbe Logik)
        var consent = await browserUser.PostAsync("/api/mcp/link/authorize", Body(new { return_url = q["return"], state = pendingId, client_name = q["client"], approve = true }));
        var redirectUrl = (await J(consent))!["redirect_url"]!.GetValue<string>();
        if (!completeCallback) return (null, verifier, "", pendingId);

        using var cb = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
        if (sendCookie) cb.Headers.Add("Cookie", cookiePair);
        var cbRes = await mcp.SendAsync(cb);
        if (cbRes.StatusCode != HttpStatusCode.Redirect) return (null, verifier, "", pendingId);
        var cbQuery = System.Web.HttpUtility.ParseQueryString(cbRes.Headers.Location!.Query);
        if (cbQuery["state"] != "clientstate") throw new Exception("state des Clients ging verloren");
        var code = cbQuery["code"]!;

        var tokenRes = await mcp.PostAsync(mcpUrl + "/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = cbUri, ["code_verifier"] = verifier,
        }));
        var access = tokenRes.IsSuccessStatusCode ? (await J(tokenRes))!["access_token"]!.GetValue<string>() : null;
        return (access, verifier, code, pendingId);
    }

    async Task<string> McpCall(string token, string tool, object? arguments = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, mcpUrl + "/mcp")
        {
            Content = Body(new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = tool, arguments = arguments ?? new { } } }),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var res = await mcp.SendAsync(req);
        if (!res.IsSuccessStatusCode) return "HTTP " + (int)res.StatusCode;
        return (await J(res))!["result"]!["content"]![0]!["text"]!.GetValue<string>();
    }

    var (loginCsrf, _, _, _) = await OAuthFlow(alice, sendCookie: false);
    Check(loginCsrf is null, "Callback ohne das zur Autorisierung gehörende Browser-Cookie wird abgewiesen (Schutz vor Login-CSRF)");

    var (aliceOAuth, aliceVerifier, aliceAuthCode, _) = await OAuthFlow(alice);
    Check(aliceOAuth is not null, "Alice: OAuth-Ablauf liefert ein Access Token");
    var (bobOAuth, _, _, _) = await OAuthFlow(bob);
    Check(bobOAuth is not null, "Bob: OAuth-Ablauf liefert ein Access Token");

    var reuse = await mcp.PostAsync(mcpUrl + "/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = aliceAuthCode, ["redirect_uri"] = cbUri, ["code_verifier"] = aliceVerifier,
    }));
    Check(reuse.StatusCode == HttpStatusCode.BadRequest, "Autorisierungscode ist nur einmal verwendbar");

    // falscher PKCE-Verifier
    {
        var verifier = "x" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var authRes = await mcp.GetAsync($"{mcpUrl}/oauth/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(cbUri)}&state=cs&code_challenge={challenge}&code_challenge_method=S256");
        var q = System.Web.HttpUtility.ParseQueryString(authRes.Headers.Location!.Query);
        var cookiePair = authRes.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var url = (await J(await alice.PostAsync("/api/mcp/link/authorize", Body(new { return_url = q["return"], state = q["state"], client_name = "x", approve = true }))))!["redirect_url"]!.GetValue<string>();
        using var cb = new HttpRequestMessage(HttpMethod.Get, url); cb.Headers.Add("Cookie", cookiePair);
        var code = System.Web.HttpUtility.ParseQueryString((await mcp.SendAsync(cb)).Headers.Location!.Query)["code"]!;
        var bad = await mcp.PostAsync(mcpUrl + "/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = cbUri, ["code_verifier"] = "falscher-verifier-" + Guid.NewGuid().ToString("N"),
        }));
        Check(bad.StatusCode == HttpStatusCode.BadRequest, "Falscher PKCE-Verifier wird abgewiesen");
    }

    var whoAlice = await McpCall(aliceOAuth!, "whoami");
    var whoBob = await McpCall(bobOAuth!, "whoami");
    Check(whoAlice.Contains(aliceId) && !whoAlice.Contains(bobId), "MCP whoami (Alice): handelt als Alice");
    Check(whoBob.Contains(bobId) && !whoBob.Contains(aliceId), "MCP whoami (Bob): handelt als Bob");

    var aliceModulesViaMcp = await McpCall(aliceOAuth!, "list_modules");
    var bobModulesViaMcp = await McpCall(bobOAuth!, "list_modules");
    Check(aliceModulesViaMcp.Contains(aliceModuleId) && !bobModulesViaMcp.Contains(aliceModuleId), "MCP list_modules: jeder sieht nur die eigenen Module");
    var bobForeignCards = await McpCall(bobOAuth!, "list_cards", new { module_id = aliceModuleId });
    Check(!bobForeignCards.Contains("Alice-Term") && !bobForeignCards.Contains(fixedCardId), "MCP list_cards: Bob sieht Alices Karten nicht");
    var aliceCardsViaMcp = await McpCall(aliceOAuth!, "list_cards", new { module_id = aliceModuleId });
    Check(aliceCardsViaMcp.Contains("Alice-Term"), "MCP list_cards: Alice sieht ihre Karten");

    var added = await McpCall(bobOAuth!, "add_card", new { module = "Bob-MCP", category = "C", term = "Bob-MCP-Karte", question = "Q?", definition = "D" });
    Check(added.Contains("Card created"), "MCP add_card (Bob) legt eine Karte an");
    Check((await J(await bob.GetAsync("/api/cards/")))!.AsArray().Any(c => c!["term"]!.GetValue<string>() == "Bob-MCP-Karte"), "Die per MCP angelegte Karte erscheint in Bobs Bereich");
    Check((await J(await alice.GetAsync("/api/cards/")))!.AsArray().All(c => c!["term"]!.GetValue<string>() != "Bob-MCP-Karte"), "… und nicht in Alices Bereich");
    Check((await McpCall(aliceOAuth!, "archive_card", new { card_id = (await J(await bob.GetAsync("/api/cards/")))!.AsArray().First(c => c!["term"]!.GetValue<string>() == "Bob-MCP-Karte")!["id"]!.GetValue<string>() })).Contains("404"), "MCP archive_card: Alice kann Bobs Karte nicht archivieren (404)");

    // Widerruf: oauth-Zugang von Alice in der Profilverwaltung
    var oauthLinks = (await J(await alice.GetAsync("/api/mcp/links/")))!.AsArray().Where(l => l!["kind"]!.GetValue<string>() == "oauth" && l["active"]!.GetValue<bool>()).ToList();
    Check(oauthLinks.Count >= 1, "Alice sieht ihre OAuth-Verknüpfungen im Profil");
    foreach (var l in oauthLinks) await alice.DeleteAsync($"/api/mcp/links/{l!["id"]!.GetValue<string>()}");
    var afterRevoke = await mcp.SendAsync(new HttpRequestMessage(HttpMethod.Post, mcpUrl + "/mcp")
    {
        Content = Body(new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
        Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", aliceOAuth!) },
    });
    Check(afterRevoke.StatusCode == HttpStatusCode.Unauthorized, "Nach Widerruf der Verknüpfung: /mcp liefert 401");
    Check((await McpCall(bobOAuth!, "whoami")).Contains(bobId), "Bobs Verknüpfung bleibt vom Widerruf unberührt");

    Console.WriteLine("\nFreigaben, Veröffentlichung und globale Ergebnisse (ADR 0015)");
    var ann = await LoginAsync("ann");     // Benutzer A (Eigentümerin)
    var ben = await LoginAsync("ben");     // Benutzer B
    var cora = await LoginAsync("cora");   // Auditor
    string IdOf(JsonNode? me) => me!["id"]!.GetValue<string>();
    var annId = IdOf(await J(await ann.GetAsync("/api/me")));
    var benId = IdOf(await J(await ben.GetAsync("/api/me")));
    var coraId = IdOf(await J(await cora.GetAsync("/api/me")));
    await admin.PutAsync($"/api/admin/users/{coraId}/roles", Body(new { roles = new[] { "auditor" } }));

    var quizItem = new { type = "single_choice", question = "Welche Antwort stimmt?", options = new[] { "A", "B", "C", "D" }, correct_index = 1, explanation = "B ist richtig." };
    await ann.PostAsync("/api/import/cards", Body(new
    {
        cards = new object[]
        {
            new { module = "Teilen", category = "K", term = "Teil-1", question = "Frage 1?", definition = "Definition 1", quiz = new[] { quizItem } },
            new { module = "Teilen", category = "K", term = "Teil-2", question = "Frage 2?", definition = "Definition 2" },
        },
    }));
    var shareModuleId = (await J(await ann.GetAsync("/api/modules/")))!.AsArray().Single(m => m!["name"]!.GetValue<string>() == "Teilen")!["id"]!.GetValue<string>();
    var shareCards = (await J(await ann.GetAsync($"/api/cards/?module_id={shareModuleId}")))!.AsArray();
    var shareCardId = shareCards.First(c => c!["term"]!.GetValue<string>() == "Teil-1")!["id"]!.GetValue<string>();

    // Ablauf 1: privat bleibt privat
    Check((await J(await ben.GetAsync($"/api/cards/?module_id={shareModuleId}")))!.AsArray().Count == 0, "A1: B sieht das private Modul von A nicht");
    Check((await ben.GetAsync($"/api/cards/{shareCardId}")).StatusCode == HttpStatusCode.NotFound, "A1: B kann eine Karte von A nicht lesen");
    Check((await J(await ben.GetAsync("/api/modules/?scope=shared")))!.AsArray().Count == 0, "A1: B hat noch keine Freigaben");
    Check((await ben.PostAsync($"/api/sharing/modules/{shareModuleId}/shares", Body(new { recipient_user_id = coraId }))).StatusCode == HttpStatusCode.NotFound, "A1: B kann ein fremdes Modul nicht freigeben");
    Check((await ben.GetAsync($"/api/sharing/modules/{shareModuleId}/shares")).StatusCode == HttpStatusCode.NotFound, "A1: B sieht die Freigabeliste von A nicht");

    // Ablauf 2: persönliche Freigabe
    Check((await ann.PostAsync($"/api/sharing/modules/{shareModuleId}/shares", Body(new { recipient_user_id = annId }))).StatusCode == HttpStatusCode.BadRequest, "A2: Freigabe an sich selbst abgelehnt");
    Check((await ann.PostAsync($"/api/sharing/modules/{shareModuleId}/shares", Body(new { recipient_user_id = Guid.NewGuid().ToString() }))).StatusCode == HttpStatusCode.NotFound, "A2: Freigabe an unbekannten Benutzer abgelehnt");
    Check((await ann.PostAsync($"/api/sharing/modules/{shareModuleId}/shares", Body(new { recipient_user_id = "kein-guid" }))).StatusCode == HttpStatusCode.NotFound, "A2: manipulierte Empfänger-ID abgelehnt");
    Check((await J(await ann.GetAsync("/api/sharing/users?q=be")))!.AsArray().Count == 0, "A2: Benutzersuche verlangt mindestens 3 Zeichen");
    Check((await J(await ann.GetAsync("/api/sharing/users?q=ben")))!.AsArray().Any(u => u!["id"]!.GetValue<string>() == benId), "A2: Benutzersuche findet B");
    Check((await ann.PostAsync($"/api/sharing/modules/{shareModuleId}/shares", Body(new { recipient_user_id = benId }))).StatusCode == HttpStatusCode.NoContent, "A2: A gibt das Modul für B frei");
    var sharedList = (await J(await ben.GetAsync("/api/modules/?scope=shared")))!.AsArray();
    Check(sharedList.Count == 1 && sharedList[0]!["context"]!.GetValue<string>() == "shared" && sharedList[0]!["owner_name"]!.GetValue<string>() == "ann", "A2: B sieht das Modul unter „Mit mir geteilt“ (Kontext shared, Herausgeber ann)");
    Check((await J(await ben.GetAsync($"/api/cards/?module_id={shareModuleId}")))!.AsArray().Count == 2, "A2: B kann die Karten lesen");
    Check((await ben.GetAsync($"/api/cards/{shareCardId}")).IsSuccessStatusCode, "A2: B kann eine freigegebene Karte lesen");
    var editBody = Body(new { module = "Teilen", category = "K", term = "Gehackt", question = "q", definition = "d" });
    Check((await ben.PutAsync($"/api/cards/{shareCardId}", editBody)).StatusCode == HttpStatusCode.NotFound, "A2: B kann das Original nicht ändern");
    Check((await ben.PatchAsync($"/api/cards/{shareCardId}/archive", null)).StatusCode == HttpStatusCode.NotFound, "A2: B kann das Original nicht archivieren");
    Check((await ben.DeleteAsync($"/api/cards/{shareCardId}")).StatusCode == HttpStatusCode.NotFound, "A2: B kann eine Karte nicht löschen");
    Check((await ben.DeleteAsync($"/api/modules/{shareModuleId}")).StatusCode == HttpStatusCode.NotFound, "A2: B kann das Modul nicht löschen");
    Check((await J(await ben.GetAsync("/api/modules/")))!.AsArray().All(m => m!["id"]!.GetValue<string>() != shareModuleId), "A2: freigegebene Module erscheinen nicht unter „Meine Module“");
    var importOverShared = await ben.PostAsync("/api/import/cards", Body(new { cards = new object[] { new { id = shareCardId, module = "Teilen", category = "K", term = "Überschrieben?", question = "q", definition = "d" } }, overwrite_existing = true }));
    Check((await J(await ann.GetAsync($"/api/cards/{shareCardId}")))!["term"]!.GetValue<string>() == "Teil-1", "A2: Import mit der ID einer freigegebenen Karte überschreibt das Original nicht");

    // Ablauf 3: B lernt, Ergebnis gehört B
    Check((await ben.PutAsync($"/api/cards/{shareCardId}/state", Body(new { is_checked = true, marked_review = false }))).StatusCode == HttpStatusCode.NoContent, "A3: B markiert eine freigegebene Karte als gelernt");
    Check((await ann.GetAsync($"/api/cards/{shareCardId}/state")).IsSuccessStatusCode
          && !(await J(await ann.GetAsync($"/api/cards/{shareCardId}/state")))!["is_checked"]!.GetValue<bool>(), "A3: Bs Lernstatus ist für A nicht sichtbar");
    Check((await ben.PostAsync($"/api/cards/{shareCardId}/quiz", null)).IsSuccessStatusCode, "A3: B kann das statische Karten-Quiz der freigegebenen Karte nutzen");
    var benShareQuiz = await ben.PostAsync("/api/quiz/submit", Body(new
    {
        module_id = shareModuleId, category = "", card_id = shareCardId,
        answers = new object[] { new { question = "Frage 1?", user_answer = "Definition 1", topic = "K", source_card_id = shareCardId, source_term = "Teil-1" } },
    }));
    Check(benShareQuiz.IsSuccessStatusCode, "A3: B speichert ein Ergebnis auf dem freigegebenen Modul");
    var benHist = (await J(await ben.GetAsync($"/api/quiz/history?module_id={shareModuleId}")))!.AsArray();
    Check(benHist.Count == 1 && benHist[0]!["context"]!.GetValue<string>() == "shared", "A3: Ergebnis gehört B und hat den Kontext „shared“");
    Check((await J(await ann.GetAsync($"/api/quiz/history?module_id={shareModuleId}")))!.AsArray().Count == 0, "A3: A kann Bs Ergebnis nicht lesen");
    Check((await ann.GetAsync("/api/global/results")).StatusCode == HttpStatusCode.Forbidden, "A3: A hat keinen Zugriff auf Ergebnisübersichten");

    // Ablauf 4: Einreichung
    Check((await ben.PostAsync($"/api/sharing/modules/{shareModuleId}/submissions", null)).StatusCode == HttpStatusCode.NotFound, "A4: B kann das Modul von A nicht einreichen");
    var submitRes = await ann.PostAsync($"/api/sharing/modules/{shareModuleId}/submissions", null);
    Check(submitRes.StatusCode == HttpStatusCode.Created, "A4: A reicht das Modul zur Veröffentlichung ein");
    var submission = (await J(submitRes))!;
    var submissionId = submission["id"]!.GetValue<string>();
    Check(submission["status"]!.GetValue<string>() == "pending" && submission["card_count"]!.GetValue<int>() == 2 && submission["version"]!.GetValue<int>() == 1, "A4: Einreichung offen (Version 1, 2 Karten)");
    Check((await ann.PostAsync($"/api/sharing/modules/{shareModuleId}/submissions", null)).StatusCode == HttpStatusCode.BadRequest, "A4: doppelte Einreichung abgelehnt");
    Check((await J(await ben.GetAsync("/api/modules/?scope=global")))!.AsArray().Count == 0, "A4: eingereichte (nicht veröffentlichte) Module sind für Benutzer nicht sichtbar");
    Check((await ann.GetAsync("/api/admin/submissions")).StatusCode == HttpStatusCode.Forbidden, "A4: normale Benutzer sehen die Einreichungen nicht");
    Check((await ann.PostAsync($"/api/admin/submissions/{submissionId}/approve", null)).StatusCode == HttpStatusCode.Forbidden, "A4: A kann ihre Einreichung nicht selbst veröffentlichen");
    Check((await cora.PostAsync($"/api/admin/submissions/{submissionId}/approve", null)).StatusCode == HttpStatusCode.Forbidden, "A4: der Auditor kann nicht veröffentlichen");
    var pendingList = (await J(await admin.GetAsync("/api/admin/submissions?status=pending")))!.AsArray();
    Check(pendingList.Any(s => s!["id"]!.GetValue<string>() == submissionId && s["submitter"]!.GetValue<string>() == "ann"), "A4: Administrator sieht den Vorschlag mit Einreicher");
    var preview = (await J(await admin.GetAsync($"/api/admin/submissions/{submissionId}/preview")))!;
    Check(preview["cards"]!.AsArray().Count == 2 && preview["module"]!["id"]!.GetValue<string>() != shareModuleId, "A4: Vorschau zeigt die eingefrorene Kopie (eigene IDs), nicht das private Original");
    Check((await admin.GetAsync($"/api/cards/?module_id={shareModuleId}")).IsSuccessStatusCode
          && (await J(await admin.GetAsync($"/api/cards/?module_id={shareModuleId}")))!.AsArray().Count == 0, "A4: Administratorrolle gibt keinen Zugriff auf das private Original");

    // Ablauf 5: Veröffentlichung
    Check((await admin.PostAsync($"/api/admin/submissions/{submissionId}/approve", null)).StatusCode == HttpStatusCode.NoContent, "A5: Administrator veröffentlicht das Modul");
    Check((await admin.PostAsync($"/api/admin/submissions/{submissionId}/approve", null)).StatusCode == HttpStatusCode.BadRequest, "A5: eine entschiedene Einreichung kann nicht erneut entschieden werden");
    var globalForBen = (await J(await ben.GetAsync("/api/modules/?scope=global")))!.AsArray();
    var globalForAnn = (await J(await ann.GetAsync("/api/modules/?scope=global")))!.AsArray();
    Check(globalForBen.Count == 1 && globalForAnn.Count == 1 && globalForBen[0]!["context"]!.GetValue<string>() == "global"
          && globalForBen[0]!["owner_name"]!.GetValue<string>() == "ann" && globalForBen[0]!["global_version"]!.GetValue<int>() == 1, "A5: beide Benutzer sehen die globale Version 1 (Herkunft ann)");
    var globalModuleId = globalForBen[0]!["id"]!.GetValue<string>();
    var publicationId = globalForBen[0]!["publication_id"]!.GetValue<string>();
    Check(globalModuleId != shareModuleId, "A5: globale Identität ≠ ID des privaten Originals");
    var globalCards = (await J(await ben.GetAsync($"/api/cards/?module_id={globalModuleId}")))!.AsArray();
    Check(globalCards.Count == 2 && globalCards.Any(c => c!["quiz"]!.AsArray().Count == 1), "A5: globale Karten inkl. statischem Quiz lesbar");
    var globalCardId = globalCards.First(c => c!["term"]!.GetValue<string>() == "Teil-1")!["id"]!.GetValue<string>();
    Check((await ann.PutAsync($"/api/cards/{globalCardId}", editBody)).StatusCode == HttpStatusCode.NotFound, "A5: auch die Urheberin kann die globale Karte nicht ändern");
    Check((await ann.DeleteAsync($"/api/modules/{globalModuleId}")).StatusCode == HttpStatusCode.NotFound, "A5: globale Module sind nicht durch Benutzer löschbar");
    Check((await ben.PostAsync($"/api/sharing/modules/{globalModuleId}/shares", Body(new { recipient_user_id = coraId }))).StatusCode == HttpStatusCode.NotFound, "A5: globale Module lassen sich nicht weiter freigeben");
    Check((await J(await ann.GetAsync($"/api/cards/?module_id={shareModuleId}")))!.AsArray().Count == 2
          && (await J(await ann.GetAsync($"/api/cards/{shareCardId}")))!["term"]!.GetValue<string>() == "Teil-1", "A5: das private Original bleibt unverändert");

    // Ablauf 6: Lernen im globalen Kontext
    Check((await ben.PutAsync($"/api/cards/{globalCardId}/state", Body(new { is_checked = true, marked_review = false }))).StatusCode == HttpStatusCode.NoContent, "A6: B markiert eine globale Karte");
    var benGlobalQuiz = await ben.PostAsync("/api/quiz/submit", Body(new
    {
        module_id = globalModuleId, category = "", card_id = globalCardId,
        answers = new object[] { new { question = "Frage 1?", user_answer = "Definition 1", topic = "K", source_card_id = globalCardId, source_term = "Teil-1" } },
    }));
    Check(benGlobalQuiz.IsSuccessStatusCode, "A6: B speichert ein Ergebnis im globalen Kontext");
    var benGlobalHist = (await J(await ben.GetAsync($"/api/quiz/history?module_id={globalModuleId}")))!.AsArray();
    Check(benGlobalHist.Count == 1 && benGlobalHist[0]!["context"]!.GetValue<string>() == "global", "A6: B sieht sein eigenes globales Ergebnis (Kontext global)");
    Check((await J(await ann.GetAsync($"/api/quiz/history?module_id={globalModuleId}")))!.AsArray().Count == 0, "A6: A sieht Bs globales Ergebnis nicht");
    Check((await ben.GetAsync("/api/global/results")).StatusCode == HttpStatusCode.Forbidden, "A6: normale Benutzer sehen keine globalen Ergebnisübersichten");
    foreach (var (who, client) in new[] { ("Administrator", admin), ("Auditor", cora) })
    {
        var summary = (await J(await client.GetAsync($"/api/global/results?publication_id={publicationId}")))!.AsArray();
        Check(summary.Count == 1 && summary[0]!["attempts"]!.GetValue<int>() == 1 && summary[0]["learners"]!.GetValue<int>() == 1 && summary[0]["version"]!.GetValue<int>() == 1, $"A6: {who} sieht die aggregierten globalen Ergebnisse");
        var rows = (await J(await client.GetAsync($"/api/global/results/{globalModuleId}")))!.AsArray();
        Check(rows.Count == 1 && rows[0]!["learner"]!.GetValue<string>() == "ben" && rows[0].AsObject().All(kv => kv.Key is not ("answers" or "feedback")), $"A6: {who} sieht Lernenden und Punkte, aber keine Antworten/Feedbacktexte");
    }
    Check((await ben.GetAsync($"/api/global/results/{globalModuleId}")).StatusCode == HttpStatusCode.Forbidden, "A6: Benutzer B kann Ergebnisdetails nicht lesen");
    Check((await admin.GetAsync($"/api/global/results/{shareModuleId}")).StatusCode == HttpStatusCode.NotFound, "A6: Ergebnisdetails gibt es nur für globale Module (kein Zugriff auf private Ergebnisse)");
    Check((await admin.GetAsync($"/api/quiz/history?module_id={shareModuleId}")).IsSuccessStatusCode
          && (await J(await admin.GetAsync($"/api/quiz/history?module_id={shareModuleId}")))!.AsArray().Count == 0, "A6: persönliche Ergebnisse auf dem freigegebenen Modul bleiben auch für Administratoren verborgen");

    // Ablauf 7: Auditor darf nur lesen
    Check((await cora.GetAsync("/api/global/modules")).IsSuccessStatusCode, "A7: Auditor sieht die globalen Veröffentlichungen");
    Check((await cora.PostAsync($"/api/admin/publications/{publicationId}/deactivate", null)).StatusCode == HttpStatusCode.Forbidden, "A7: Auditor kann Veröffentlichungen nicht deaktivieren");
    Check((await cora.PutAsync($"/api/cards/{globalCardId}", editBody)).StatusCode == HttpStatusCode.NotFound, "A7: Auditor kann globale Karten nicht ändern");
    Check((await cora.GetAsync("/api/admin/users")).StatusCode == HttpStatusCode.Forbidden, "A7: Auditor hat keinen Zugriff auf die Benutzerverwaltung");
    Check((await cora.GetAsync("/api/admin/audit")).StatusCode == HttpStatusCode.Forbidden, "A7: Auditor liest nicht das gesamte Protokoll");
    var auditorLog = (await J(await cora.GetAsync("/api/global/audit")))!.AsArray();
    Check(auditorLog.Count > 0 && auditorLog.All(e => e!["action"]!.GetValue<string>().StartsWith("publication.") || e["action"]!.GetValue<string>().StartsWith("submission.")), "A7: Auditor liest Veröffentlichungsprotokolle (nur publication.*/submission.*)");

    // Ablauf 8: Widerruf
    Check((await ann.DeleteAsync($"/api/sharing/modules/{shareModuleId}/shares/{benId}")).StatusCode == HttpStatusCode.NoContent, "A8: A widerruft die Freigabe");
    Check((await ann.DeleteAsync($"/api/sharing/modules/{shareModuleId}/shares/{benId}")).StatusCode == HttpStatusCode.NotFound, "A8: erneuter Widerruf → 404");
    Check((await J(await ben.GetAsync($"/api/cards/?module_id={shareModuleId}")))!.AsArray().Count == 0
          && (await ben.GetAsync($"/api/cards/{shareCardId}")).StatusCode == HttpStatusCode.NotFound
          && (await J(await ben.GetAsync("/api/modules/?scope=shared")))!.AsArray().Count == 0, "A8: B verliert den Zugriff auf das freigegebene Modul");
    Check((await ben.PutAsync($"/api/cards/{shareCardId}/state", Body(new { is_checked = false }))).StatusCode == HttpStatusCode.NotFound, "A8: B kann nach Widerruf keinen Status mehr schreiben");
    Check((await J(await ben.GetAsync($"/api/quiz/history?module_id={shareModuleId}")))!.AsArray().Count == 1, "A8: Bs früheres Ergebnis bleibt nach dem Widerruf erhalten");
    Check((await ann.DeleteAsync($"/api/cards/{shareCardId}")).StatusCode == HttpStatusCode.Conflict, "A8: Löschen einer Karte mit Ergebnissen anderer Benutzer wird verhindert (409)");
    Check((await ann.DeleteAsync($"/api/modules/{shareModuleId}")).StatusCode == HttpStatusCode.Conflict, "A8: Löschen eines Moduls mit Ergebnissen anderer Benutzer wird verhindert (409)");
    Check((await ann.PostAsync($"/api/sharing/modules/{shareModuleId}/shares", Body(new { recipient_user_id = benId }))).StatusCode == HttpStatusCode.NoContent
          && (await J(await ben.GetAsync("/api/modules/?scope=shared")))!.AsArray().Count == 1, "A8: erneute Freigabe stellt den Zugriff wieder her");
    await ann.DeleteAsync($"/api/sharing/modules/{shareModuleId}/shares/{benId}");

    // Ablauf 9: Änderung am Original → globale Version bleibt stabil bis zur bewussten Aktualisierung
    await ann.PutAsync($"/api/cards/{shareCardId}", Body(new { module = "Teilen", category = "K", term = "Teil-1 NEU", question = "Frage 1 NEU?", definition = "Definition 1 NEU" }));
    Check((await J(await ben.GetAsync($"/api/cards/{globalCardId}")))!["term"]!.GetValue<string>() == "Teil-1", "A9: die globale Version bleibt trotz Änderung am Original unverändert");
    var updateSubmission = (await J(await ann.PostAsync($"/api/sharing/modules/{shareModuleId}/submissions", null)))!;
    Check(updateSubmission["kind"]!.GetValue<string>() == "update" && updateSubmission["version"]!.GetValue<int>() == 2 && updateSubmission["publication_id"]!.GetValue<string>() == publicationId, "A9: bewusste Aktualisierung = neue Version derselben Veröffentlichung");
    Check((await J(await ben.GetAsync($"/api/cards/?module_id={globalModuleId}")))!.AsArray().Any(c => c!["term"]!.GetValue<string>() == "Teil-1"), "A9: bis zur Freigabe bleibt Version 1 aktiv");
    var rejected = await admin.PostAsync($"/api/admin/submissions/{updateSubmission["id"]!.GetValue<string>()}/reject", Body(new { reason = "Bitte überarbeiten" }));
    Check(rejected.StatusCode == HttpStatusCode.NoContent, "A9: Administrator lehnt die Aktualisierung ab");
    Check((await J(await ben.GetAsync("/api/modules/?scope=global")))!.AsArray().Single()!["global_version"]!.GetValue<int>() == 1
          && (await J(await ann.GetAsync($"/api/cards/{shareCardId}")))!["term"]!.GetValue<string>() == "Teil-1 NEU", "A9: nach Ablehnung bleiben Version 1 und das private Original unverändert");
    var update2 = (await J(await ann.PostAsync($"/api/sharing/modules/{shareModuleId}/submissions", null)))!;
    Check((await admin.PostAsync($"/api/admin/submissions/{update2["id"]!.GetValue<string>()}/approve", null)).StatusCode == HttpStatusCode.NoContent, "A9: Administrator veröffentlicht Version 2");
    var globalV2 = (await J(await ben.GetAsync("/api/modules/?scope=global")))!.AsArray();
    Check(globalV2.Count == 1 && globalV2[0]!["global_version"]!.GetValue<int>() == 2 && globalV2[0]["id"]!.GetValue<string>() != globalModuleId, "A9: Benutzer sehen nur noch Version 2 (neue Modul-ID)");
    Check((await ben.GetAsync($"/api/cards/{globalCardId}")).StatusCode == HttpStatusCode.NotFound, "A9: Version 1 ist nicht mehr lernbar");
    Check((await J(await ben.GetAsync($"/api/quiz/history?module_id={globalModuleId}")))!.AsArray().Count == 1, "A9: Bs Ergebnis bleibt Version 1 zugeordnet");
    var versions = (await J(await admin.GetAsync($"/api/global/results?publication_id={publicationId}")))!.AsArray();
    Check(versions.Count == 2 && versions.Single(v => v!["version"]!.GetValue<int>() == 1)!["attempts"]!.GetValue<int>() == 1
          && versions.Single(v => v!["version"]!.GetValue<int>() == 1)!["status"]!.GetValue<string>() == "superseded"
          && versions.Single(v => v!["version"]!.GetValue<int>() == 2)!["attempts"]!.GetValue<int>() == 0, "A9: Ergebnisse bleiben ihrer Version zugeordnet (v1 superseded, v2 ohne Ergebnisse)");
    Check((await admin.PostAsync($"/api/admin/publications/{publicationId}/deactivate", null)).StatusCode == HttpStatusCode.NoContent
          && (await J(await ben.GetAsync("/api/modules/?scope=global")))!.AsArray().Count == 0, "A9: Deaktivierung nimmt die Veröffentlichung zurück");
    Check((await J(await ben.GetAsync($"/api/quiz/history?module_id={globalModuleId}")))!.AsArray().Count == 1, "A9: Ergebnisse bleiben nach der Deaktivierung erhalten");
    Check((await admin.PostAsync($"/api/admin/publications/{publicationId}/activate", null)).StatusCode == HttpStatusCode.NoContent
          && (await J(await ben.GetAsync("/api/modules/?scope=global")))!.AsArray().Count == 1, "A9: Reaktivierung stellt die Veröffentlichung wieder her");

    // Ablauf 10: MCP
    var (benMcp, _, _, _) = await OAuthFlow(ben);
    Check(benMcp is not null, "A10: B meldet seinen MCP-Client per OAuth an");
    await ann.PostAsync($"/api/sharing/modules/{shareModuleId}/shares", Body(new { recipient_user_id = benId }));
    var mcpAll = await McpCall(benMcp!, "list_modules", new { scope = "all" });
    Check(mcpAll.Contains("[global") && mcpAll.Contains("[shared") && mcpAll.Contains("version 2") && mcpAll.Contains("from: ann"), "A10: MCP list_modules(all) zeigt freigegebene und globale Module mit Herkunft");
    var mcpDefault = await McpCall(benMcp!, "list_modules");
    Check(!mcpDefault.Contains("[global") && !mcpDefault.Contains("[shared"), "A10: list_modules ohne scope bleibt kompatibel (nur eigene Module)");
    var globalNow = (await J(await ben.GetAsync("/api/modules/?scope=global")))!.AsArray().Single()!;
    Check((await McpCall(benMcp!, "list_cards", new { module_id = globalNow["id"]!.GetValue<string>() })).Contains("Teil-1 NEU"), "A10: MCP list_cards liest ein globales Modul");
    Check((await McpCall(benMcp!, "list_cards", new { module_id = shareModuleId })).Contains("Teil-1 NEU"), "A10: MCP list_cards liest ein für B freigegebenes Modul");
    Check((await McpCall(benMcp!, "archive_card", new { card_id = shareCardId })).Contains("404"), "A10: MCP kann freigegebene Karten nicht ändern");
    var benTokenHttp = TokenClient(benMcp!);
    Check((await benTokenHttp.GetAsync("/api/sharing/users?q=ann")).StatusCode == HttpStatusCode.Forbidden, "A10: MCP-Token darf keine Freigaben verwalten");
    Check((await benTokenHttp.PostAsync($"/api/sharing/modules/{shareModuleId}/submissions", null)).StatusCode == HttpStatusCode.Forbidden, "A10: MCP-Token darf nichts einreichen");
    var adminMcp = (await OAuthFlow(admin)).AccessToken;
    var adminTokenHttp = TokenClient(adminMcp!);
    Check((await adminTokenHttp.PostAsync($"/api/admin/publications/{publicationId}/deactivate", null)).StatusCode == HttpStatusCode.Forbidden
          && (await adminTokenHttp.GetAsync("/api/admin/submissions")).StatusCode == HttpStatusCode.Forbidden
          && (await adminTokenHttp.GetAsync("/api/global/results")).StatusCode == HttpStatusCode.Forbidden, "A10: auch das MCP-Token eines Administrators hat keine Veröffentlichungs- und Ergebnisrechte");
    Check((await J(await adminTokenHttp.GetAsync($"/api/cards/?module_id={shareModuleId}")))!.AsArray().Count == 0, "A10: Administrator-MCP sieht fremde private Module nicht");

    // Ablauf 11: manipulierte IDs
    Check((await admin.PostAsync($"/api/admin/submissions/{Guid.NewGuid()}/approve", null)).StatusCode == HttpStatusCode.NotFound, "A11: unbekannte Einreichung → 404");
    Check((await admin.PostAsync("/api/admin/submissions/kein-guid/approve", null)).StatusCode == HttpStatusCode.NotFound, "A11: ungültige Einreichungs-ID → 404");
    Check((await admin.GetAsync("/api/admin/submissions/kein-guid/preview")).StatusCode == HttpStatusCode.NotFound, "A11: ungültige Vorschau-ID → 404");
    Check((await admin.PostAsync("/api/admin/publications/kein-guid/deactivate", null)).StatusCode == HttpStatusCode.NotFound, "A11: ungültige Veröffentlichungs-ID → 404");
    Check((await admin.GetAsync("/api/global/results/kein-guid")).StatusCode == HttpStatusCode.NotFound, "A11: ungültige Modul-ID bei Ergebnissen → 404");
    Check((await ben.GetAsync($"/api/modules/?scope=alles")).StatusCode == HttpStatusCode.BadRequest, "A11: ungültiger scope → 400");
    Check((await ben.GetAsync($"/api/cards/?module_id=' OR 1=1 --")).IsSuccessStatusCode, "A11: SQL-Fragment in module_id wirkungslos");
    Check((await ben.DeleteAsync($"/api/sharing/modules/{shareModuleId}/shares/{annId}")).StatusCode == HttpStatusCode.NotFound, "A11: Empfänger können fremde Freigaben nicht widerrufen");
    Check((await ben.PostAsync("/api/sharing/modules/" + Guid.NewGuid() + "/shares", Body(new { recipient_user_id = coraId }))).StatusCode == HttpStatusCode.NotFound, "A11: Freigabe auf unbekanntes Modul → 404");

    Console.WriteLine("\nBilder in Lernkarten (ADR 0016)");
    var img = await LoginAsync("imgu");
    const string Png = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAQCAIAAAD4YuoOAAAAI0lEQVR42mPg94mhKWIAYq3ls2mERi0YtWDUglELRpIFNEUAOleXE3NVou0AAAAASUVORK5CYII=";
    object ImgObj(string mime = "image/png", string? data = null, string alt = "Testbild", string cap = "Unterschrift") =>
        new { mime_type = mime, data_base64 = data ?? Png, alt_text = alt, caption = cap };
    object ImgCard(string term, object? images, string module = "Bilder") => images is null
        ? new { module, category = "B", term, question = "Q?", definition = "D" }
        : (object)new { module, category = "B", term, question = "Q?", definition = "D", images };
    async Task<HttpResponseMessage> PostCard(object card) => await img.PostAsync("/api/cards/", Body(card));
    async Task<JsonNode?> FullCard(string id) => await J(await img.GetAsync($"/api/cards/{id}"));

    var withImg = await PostCard(ImgCard("Mit-Bild", new[] { ImgObj() }));
    Check(withImg.StatusCode == HttpStatusCode.Created, "B1: Karte mit gültigem Base64-PNG wird importiert");
    var withImgId = (await J(withImg))!["id"]!.GetValue<string>();
    var readBack = (await FullCard(withImgId))!["images"]!.AsArray();
    Check(readBack.Count == 1 && readBack[0]!["mime_type"]!.GetValue<string>() == "image/png" && readBack[0]["size_bytes"]!.GetValue<int>() == 92
          && readBack[0]["alt_text"]!.GetValue<string>() == "Testbild" && readBack[0]["caption"]!.GetValue<string>() == "Unterschrift"
          && readBack[0]["data_base64"] is null && readBack[0]["url"]!.GetValue<string>() == "/api/images/" + readBack[0]["image_id"]!.GetValue<string>(), "B2: Einzelabruf liefert Bild-Metadaten und URL (ohne Base64)");
    var readFull = (await J(await img.GetAsync($"/api/cards/{withImgId}?include_image_data=true")))!["images"]!.AsArray();
    Check(readFull.Count == 1 && readFull[0]!["data_base64"]!.GetValue<string>() == Png, "B2: mit include_image_data=true wird das Bild unverändert als Base64 ausgelesen");
    var listed = (await J(await img.GetAsync("/api/cards/")))!.AsArray().Single(c => c!["id"]!.GetValue<string>() == withImgId)!["images"]!.AsArray();
    Check(listed.Count == 1 && listed[0]!["data_base64"] is null && listed[0]["size_bytes"]!.GetValue<int>() == 92, "B3: Listen liefern nur Bild-Metadaten (size_bytes), keine Base64-Daten");

    var multi = await PostCard(ImgCard("Mehrere", new[] { ImgObj(alt: "a"), ImgObj(alt: "b"), ImgObj(alt: "c") }));
    Check(multi.StatusCode == HttpStatusCode.Created && (await FullCard((await J(multi))!["id"]!.GetValue<string>()))!["images"]!.AsArray().Count == 3, "B4: Karte mit mehreren Bildern wird gespeichert");
    Check((await PostCard(ImgCard("Ohne", null))).StatusCode == HttpStatusCode.Created, "B5: fehlendes images-Feld verursacht keinen Fehler");
    var empty = await PostCard(ImgCard("Leer", Array.Empty<object>()));
    Check(empty.StatusCode == HttpStatusCode.Created && (await FullCard((await J(empty))!["id"]!.GetValue<string>()))!["images"]!.AsArray().Count == 0, "B5: leeres images[] = keine Bilder");

    async Task<string> Reject(string name, object images)
    {
        var res = await PostCard(ImgCard("Fehler-" + name, images));
        var text = await res.Content.ReadAsStringAsync();
        Check(res.StatusCode == HttpStatusCode.BadRequest && text.Contains("Bild"), $"B6: {name} wird mit verständlichem Fehler abgelehnt (400)");
        return text;
    }
    await Reject("zu viele Bilder (6)", Enumerable.Range(0, 6).Select(_ => ImgObj()).ToArray());
    await Reject("ungültiges Base64", new[] { ImgObj(data: "das-ist-kein-base64!!") });
    await Reject("data:-Präfix", new[] { ImgObj(data: "data:image/png;base64," + Png) });
    await Reject("nicht erlaubter MIME-Typ", new[] { ImgObj(mime: "image/gif") });
    await Reject("MIME-Typ passt nicht zur Signatur", new[] { ImgObj(mime: "image/jpeg") });
    await Reject("kein Bild (Text als Base64)", new[] { ImgObj(data: Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Hallo Welt, ich bin kein Bild"))) });
    await Reject("fehlende Bilddaten", new[] { ImgObj(data: "") });
    await Reject("zu langer alt_text", new[] { ImgObj(alt: new string('x', 301)) });
    var big = new byte[2 * 1024 * 1024 + 1];
    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(big, 0);
    var tooBig = await Reject("zu großes Bild (2 MiB + 1 Byte)", new[] { ImgObj(data: Convert.ToBase64String(big)) });
    Check(tooBig.Contains("zu groß"), "B6: Größenfehler nennt den Grund");
    var atLimit = new byte[2 * 1024 * 1024];
    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(atLimit, 0);
    Check((await PostCard(ImgCard("Grenze", new[] { ImgObj(data: Convert.ToBase64String(atLimit)) }))).StatusCode == HttpStatusCode.Created, "B6: genau 2 MiB sind erlaubt");
    var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0 };
    var webp = new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' };
    Check((await PostCard(ImgCard("Typen", new[] { ImgObj("image/jpeg", Convert.ToBase64String(jpeg)), ImgObj("image/webp", Convert.ToBase64String(webp)) }))).StatusCode == HttpStatusCode.Created, "B6: JPEG- und WebP-Signaturen werden akzeptiert");
    Check((await PostCard(ImgCard("Umbruch", new[] { ImgObj(data: Png[..40] + "\r\n" + Png[40..]) }))).StatusCode == HttpStatusCode.Created, "B6: Zeilenumbrüche im Base64 werden toleriert und entfernt");

    // Update-Semantik (Import mit overwrite_existing)
    var keepId = (await J(await PostCard(ImgCard("Behalten", new[] { ImgObj() }))))!["id"]!.GetValue<string>();
    var overwriteWithout = await img.PostAsync("/api/import/cards", Body(new { cards = new object[] { new { id = keepId, module = "Bilder", category = "B", term = "Behalten", question = "Q2?", definition = "D2" } }, overwrite_existing = true }));
    var kept = (await FullCard(keepId))!;
    Check(overwriteWithout.IsSuccessStatusCode && kept["question"]!.GetValue<string>() == "Q2?" && kept["images"]!.AsArray().Count == 1, "B7: Überschreiben ohne images-Feld behält vorhandene Bilder");
    await img.PostAsync("/api/import/cards", Body(new { cards = new object[] { new { id = keepId, module = "Bilder", category = "B", term = "Behalten", question = "Q2?", definition = "D2", images = Array.Empty<object>() } }, overwrite_existing = true }));
    Check((await FullCard(keepId))!["images"]!.AsArray().Count == 0, "B7: Überschreiben mit images=[] entfernt die Bilder");
    var putKeep = (await J(await PostCard(ImgCard("Put", new[] { ImgObj() }))))!["id"]!.GetValue<string>();
    await img.PutAsync($"/api/cards/{putKeep}", Body(new { module = "Bilder", category = "B", term = "Put2", question = "Q?", definition = "D" }));
    Check((await FullCard(putKeep))!["images"]!.AsArray().Count == 1, "B7: PUT ohne images-Feld behält die Bilder");
    Check((await img.PutAsync($"/api/cards/{putKeep}", Body(new { module = "Bilder", category = "B", term = "Put3", question = "Q?", definition = "D", images = new[] { ImgObj(mime: "image/gif") } }))).StatusCode == HttpStatusCode.BadRequest, "B7: PUT mit ungültigem Bild → 400");

    // Stapelimport: gute Karten werden importiert, fehlerhafte mit Grund gemeldet
    var batch = await img.PostAsync("/api/import/cards", Body(new { cards = new object[]
    {
        ImgCard("Batch-gut", new[] { ImgObj() }, "Stapel"),
        ImgCard("Batch-schlecht", new[] { ImgObj(mime: "image/gif") }, "Stapel"),
    } }));
    var batchJson = (await J(batch))!;
    Check(batchJson["created"]!.GetValue<int>() == 1 && batchJson["skipped"]!.GetValue<int>() == 1
          && batchJson["errors"]!.AsArray().Any(e => e!.GetValue<string>().Contains("Batch-schlecht") && e.GetValue<string>().Contains("image/gif")), "B8: Stapelimport meldet abgelehnte Karte mit Grund und importiert die übrigen");

    // Schema
    var schema = (await J(await img.GetAsync("/api/schema/import/cards")))!;
    var imagesSchema = schema["card_schema"]!["properties"]!["images"]!;
    Check(imagesSchema["maxItems"]!.GetValue<int>() == 5 && imagesSchema["items"]!["properties"]!["mime_type"]!["enum"]!.AsArray().Count == 3
          && imagesSchema["items"]!["properties"]!["data_base64"] is not null && imagesSchema["items"]!["properties"]!["alt_text"] is not null
          && imagesSchema["items"]!["properties"]!["caption"] is not null && imagesSchema["description"]!.GetValue<string>().Contains("2 MiB"), "B9: Live-Schema beschreibt images[] samt Unterfeldern und Limits");
    Check(schema["grounding_rules"]!.AsArray().Any(r => r!.GetValue<string>().Contains("Never invent")), "B9: Schema enthält die Regel „Base64 nie erfinden“");
    Check(schema["example"]!["cards"]![0]!["images"]![0]!["data_base64"] is not null
          && (await img.PostAsync("/api/import/cards", Body(schema["example"]!.DeepClone()))).IsSuccessStatusCode, "B9: das Beispiel aus dem Schema ist selbst importierbar");

    // Demo-Dateien bleiben importierbar
    foreach (var demo in new[] { "demo.json", "kvno_folien_lernkarten.json" })
    {
        var demoPath = Path.Combine(repoRoot, "demoodule", demo);
        if (!File.Exists(demoPath)) continue;
        var demoCards = JsonNode.Parse(File.ReadAllText(demoPath));
        var demoRes = await (await LoginAsync("demouser_" + demo.Replace(".json", "").Replace("_", ""))).PostAsync("/api/import/cards", Body(demoCards is JsonArray ? new { cards = demoCards } : demoCards!));
        var dj = await J(demoRes);
        Check(demoRes.IsSuccessStatusCode && dj!["created"]!.GetValue<int>() > 0 && dj["skipped"]!.GetValue<int>() == 0, $"B10: demoodule/{demo} bleibt importierbar (ohne Fehler)");
    }

    // Freigabe/Global: Bilder werden mitgegeben, Bilddaten nur für Berechtigte
    var imgUser2 = await LoginAsync("imgu2");
    var imgUser2Id = IdOf(await J(await imgUser2.GetAsync("/api/me")));
    var imgModuleId = (await J(await img.GetAsync("/api/modules/")))!.AsArray().Single(m => m!["name"]!.GetValue<string>() == "Bilder")!["id"]!.GetValue<string>();
    Check((await imgUser2.GetAsync($"/api/cards/{withImgId}")).StatusCode == HttpStatusCode.NotFound, "B11: fremde Bilder sind ohne Freigabe nicht lesbar");
    await img.PostAsync($"/api/sharing/modules/{imgModuleId}/shares", Body(new { recipient_user_id = imgUser2Id }));
    var sharedImg = (await J(await imgUser2.GetAsync($"/api/cards/{withImgId}")))!["images"]!.AsArray();
    Check(sharedImg.Count == 1 && sharedImg[0]!["url"] is not null, "B11: freigegebene Karte liefert dem Empfänger die Bild-Metadaten");
    Check((await J(await imgUser2.GetAsync($"/api/cards/{withImgId}?include_image_data=true")))!["images"]![0]!["data_base64"]!.GetValue<string>() == Png, "B11: … und die Bilddaten");
    var imgSubmission = (await J(await img.PostAsync($"/api/sharing/modules/{imgModuleId}/submissions", null)))!["id"]!.GetValue<string>();
    await admin.PostAsync($"/api/admin/submissions/{imgSubmission}/approve", null);
    var globalImg = (await J(await imgUser2.GetAsync("/api/modules/?scope=global")))!.AsArray().Single(m => m!["name"]!.GetValue<string>() == "Bilder")!["id"]!.GetValue<string>();
    var globalImgCards = (await J(await imgUser2.GetAsync($"/api/cards/?module_id={globalImg}")))!.AsArray();
    var globalWithImg = globalImgCards.First(c => c!["term"]!.GetValue<string>() == "Mit-Bild")!["id"]!.GetValue<string>();
    Check((await J(await imgUser2.GetAsync($"/api/cards/{globalWithImg}")))!["images"]!.AsArray().Count == 1, "B11: die globale Kopie enthält die Bilder");

    // ─── Bildspeicher (ADR 0017): Endpunkt, Berechtigung, Metadaten ───
    Console.WriteLine("\nBildspeicher: Endpunkt und Berechtigung (ADR 0017)");
    var pngBytes = Convert.FromBase64String(Png);
    var imgMeta = (await FullCard(withImgId))!["images"]![0]!;
    var imageId = imgMeta["image_id"]!.GetValue<string>();
    var imgRes = await img.GetAsync("/api/images/" + imageId);
    var imgData = await imgRes.Content.ReadAsByteArrayAsync();
    Check(imgRes.IsSuccessStatusCode && imgData.SequenceEqual(pngBytes) && imgRes.Content.Headers.ContentType?.MediaType == "image/png", "C1: GET /api/images/{id} liefert exakt die importierten Bytes mit Content-Type");
    var etag = imgRes.Headers.ETag?.Tag;
    Check(etag == "\"" + imgMeta["checksum"]!.GetValue<string>() + "\"" && imgRes.Headers.CacheControl?.Private == true
          && imgRes.Headers.GetValues("X-Content-Type-Options").Contains("nosniff"), "C2: Antwort trägt ETag (Prüfsumme), Cache-Control private und nosniff");
    using (var inm = new HttpRequestMessage(HttpMethod.Get, "/api/images/" + imageId)) { inm.Headers.TryAddWithoutValidation("If-None-Match", etag); Check((await img.SendAsync(inm)).StatusCode == HttpStatusCode.NotModified, "C3: If-None-Match mit aktuellem ETag → 304"); }
    Check((await NewClient().GetAsync("/api/images/" + imageId)).StatusCode == HttpStatusCode.Unauthorized, "C4: ohne Anmeldung → 401");
    Check((await ben.GetAsync("/api/images/" + imageId)).StatusCode == HttpStatusCode.NotFound, "C4: fremder Benutzer → 404 (kein Hinweis auf Existenz)");
    Check((await img.GetAsync("/api/images/kein-guid")).StatusCode == HttpStatusCode.NotFound && (await img.GetAsync("/api/images/" + Guid.NewGuid())).StatusCode == HttpStatusCode.NotFound, "C4: ungültige/unbekannte Bild-ID → 404");
    Check((await imgUser2.GetAsync("/api/images/" + imageId)).IsSuccessStatusCode, "C5: freigegebene Karte: Empfänger darf das Bild laden");
    var imgToken = (await J(await img.PostAsync("/api/mcp/links", Body(new { name = "img", days = 1 }))))!["token"]!.GetValue<string>();
    Check((await TokenClient(imgToken).GetAsync("/api/images/" + imageId)).IsSuccessStatusCode && (await TokenClient(bobToken).GetAsync("/api/images/" + imageId)).StatusCode == HttpStatusCode.NotFound,
        "C5: MCP-Token liest nur Bilder des eigenen Benutzers");
    var globalImgMeta = (await J(await imgUser2.GetAsync($"/api/cards/{globalWithImg}")))!["images"]![0]!;
    Check(globalImgMeta["image_id"]!.GetValue<string>() != imageId && (await ben.GetAsync("/api/images/" + globalImgMeta["image_id"]!.GetValue<string>())).IsSuccessStatusCode, "C6: globale Kopie hat eigene Bild-ID und ist für alle Benutzer ladbar");
    await img.DeleteAsync($"/api/sharing/modules/{imgModuleId}/shares/{imgUser2Id}");
    Check((await imgUser2.GetAsync("/api/images/" + imageId)).StatusCode == HttpStatusCode.NotFound, "C7: nach Widerruf der Freigabe ist das Bild nicht mehr ladbar");
    var metaJson = imgMeta.ToJsonString();
    Check(!metaJson.Contains("storage_key") && !metaJson.Contains("\"provider\""), "C8: Speicher-Interna (provider, storage_key) werden nicht ausgeliefert");

    // image_id-Semantik beim Aktualisieren
    var rtId = (await J(await PostCard(ImgCard("Roundtrip", new[] { ImgObj(alt: "alt0") }))))!["id"]!.GetValue<string>();
    var rtImg = (await FullCard(rtId))!["images"]![0]!;
    await img.PutAsync($"/api/cards/{rtId}", Body(new { module = "Bilder", category = "B", term = "Roundtrip", question = "Q?", definition = "D", images = new[] { new { image_id = rtImg["image_id"]!.GetValue<string>(), alt_text = "neu", caption = "neue Unterschrift" } } }));
    var rtAfter = (await FullCard(rtId))!["images"]![0]!;
    Check(rtAfter["image_id"]!.GetValue<string>() == rtImg["image_id"]!.GetValue<string>() && rtAfter["alt_text"]!.GetValue<string>() == "neu" && rtAfter["checksum"]!.GetValue<string>() == rtImg["checksum"]!.GetValue<string>(),
        "C9: Zurückschreiben mit image_id behält das Bild (nur Texte ändern sich)");
    Check((await img.PutAsync($"/api/cards/{rtId}", Body(new { module = "Bilder", category = "B", term = "Roundtrip", question = "Q?", definition = "D", images = new[] { new { image_id = Guid.NewGuid().ToString() } } }))).StatusCode == HttpStatusCode.BadRequest, "C9: image_id einer fremden/unbekannten Karte → 400");
    Check((await img.PutAsync($"/api/cards/{rtId}", Body(new { module = "Bilder", category = "B", term = "Roundtrip", question = "Q?", definition = "D", images = new[] { new { image_id = imageId } } }))).StatusCode == HttpStatusCode.BadRequest, "C9: image_id einer anderen Karte kann nicht übernommen werden (kein Fremdzugriff über IDs)");
    await img.PutAsync($"/api/cards/{rtId}", Body(new { module = "Bilder", category = "B", term = "Roundtrip", question = "Q?", definition = "D", images = new[] { ImgObj(alt: "gleiches Bild") } }));
    Check((await FullCard(rtId))!["images"]![0]!["image_id"]!.GetValue<string>() == rtImg["image_id"]!.GetValue<string>(), "C10: identisches Bild wird nicht doppelt gespeichert (gleiche Prüfsumme → gleiche Bild-ID)");
    await img.PutAsync($"/api/cards/{rtId}", Body(new { module = "Bilder", category = "B", term = "Roundtrip", question = "Q?", definition = "D", images = Array.Empty<object>() }));
    Check((await img.GetAsync("/api/images/" + rtImg["image_id"]!.GetValue<string>())).StatusCode == HttpStatusCode.NotFound, "C11: entferntes Bild ist nicht mehr abrufbar");
    var delCard = (await J(await PostCard(ImgCard("Löschen", new[] { ImgObj() }))))!["id"]!.GetValue<string>();
    var delImg = (await FullCard(delCard))!["images"]![0]!["image_id"]!.GetValue<string>();
    await img.DeleteAsync($"/api/cards/{delCard}");
    Check((await img.GetAsync("/api/images/" + delImg)).StatusCode == HttpStatusCode.NotFound, "C11: Bilder gelöschter Karten sind nicht mehr abrufbar");

    // MCP
    var (imgMcp, _, _, _) = await OAuthFlow(img);
    var mcpSchema = await McpCall(imgMcp!, "get_import_schema");
    Check(mcpSchema.Contains("\"images\"") && mcpSchema.Contains("data_base64") && mcpSchema.Contains("image/webp"), "B12: MCP get_import_schema liefert das images-Schema");
    var mcpAdd = await McpCall(imgMcp!, "add_card", new { module = "Bilder", category = "B", term = "MCP-Bild", question = "Q?", definition = "D", images = new[] { ImgObj() } });
    Check(mcpAdd.Contains("Card created"), "B12: MCP add_card akzeptiert Bilder");
    var mcpImgCard = (await J(await img.GetAsync("/api/cards/")))!.AsArray().First(c => c!["term"]!.GetValue<string>() == "MCP-Bild")!["id"]!.GetValue<string>();
    Check((await FullCard(mcpImgCard))!["images"]!.AsArray().Count == 1, "B12: das per MCP angelegte Bild ist gespeichert");
    var mcpImport = await McpCall(imgMcp!, "import_cards", new { cards = new object[] { ImgCard("MCP-Import", new[] { ImgObj() }), ImgCard("MCP-Fehler", new[] { ImgObj(mime: "image/gif") }) } });
    Check(mcpImport.Contains("1 created") && mcpImport.Contains("1 skipped") && mcpImport.Contains("MCP-Fehler") && mcpImport.Contains("image/gif"), "B12: MCP import_cards meldet fehlerhafte Bilder mit Grund");
    var mcpBad = await McpCall(imgMcp!, "add_card", new { module = "Bilder", category = "B", term = "MCP-schlecht", question = "Q?", definition = "D", images = new[] { ImgObj(data: "kaputt!!") } });
    Check(mcpBad.Contains("400") && mcpBad.Contains("Base64"), "B12: MCP add_card meldet ungültiges Base64 verständlich");
    Check((await McpCall(imgMcp!, "list_cards", new { module_id = imgModuleId })).Contains("Mit-Bild"), "B12: MCP list_cards funktioniert mit Bildkarten weiterhin");

    Console.WriteLine("\nPrüfbericht: persönliche Daten, Gleichzeitigkeit, Sitzungen");
    // Frische Benutzer, damit frühere Abschnitte (Sperre/Rollen) nicht hineinspielen
    var dora = await LoginAsync("dora");
    var emil = await LoginAsync("emil");
    var doraId = (await J(await dora.GetAsync("/api/me")))!["id"]!.GetValue<string>();
    var emilId = (await J(await emil.GetAsync("/api/me")))!["id"]!.GetValue<string>();
    await dora.PostAsync("/api/import/cards", Body(new { cards = new object[] { new { module = "Dora", category = "A", term = "D1", question = "Q?", definition = "D" } } }));
    await emil.PostAsync("/api/import/cards", Body(new { cards = new object[] { new { module = "Emil", category = "A", term = "E1", question = "Q?", definition = "D" } } }));
    var doraCard = (await J(await dora.GetAsync("/api/cards/")))!.AsArray().Single()!["id"]!.GetValue<string>();
    var emilCard = (await J(await emil.GetAsync("/api/cards/")))!.AsArray().Single()!["id"]!.GetValue<string>();

    // Frage 2: Theme/Präferenzen getrennt
    await dora.PutAsync("/api/preferences/theme", Body(new { theme = "dark" }));
    await emil.PutAsync("/api/preferences/theme", Body(new { theme = "light" }));
    Check((await J(await dora.GetAsync("/api/preferences/")))!["theme"]!.GetValue<string>() == "dark"
          && (await J(await emil.GetAsync("/api/preferences/")))!["theme"]!.GetValue<string>() == "light", "Theme ist pro Benutzer getrennt");

    // Frage 3: Lernstatus getrennt
    await dora.PutAsync($"/api/cards/{doraCard}/state", Body(new { is_checked = true, marked_review = true }));
    var emilState = (await J(await emil.GetAsync($"/api/cards/{emilCard}/state")))!;
    Check(!emilState["is_checked"]!.GetValue<bool>() && !emilState["marked_review"]!.GetValue<bool>(), "Doras Lernstatus verändert Emils Lernstatus nicht");
    Check((await J(await dora.GetAsync($"/api/cards/{doraCard}/state")))!["is_checked"]!.GetValue<bool>(), "Doras Lernstatus bleibt erhalten");

    // Frage 4: Chat-Verlauf (wird nur von der Blazor-Oberfläche geschrieben) — Lesepfad über die API
    Check((await J(await emil.GetAsync($"/api/chat/history?card_id={doraCard}")))!.AsArray().Count == 0, "Chat-Verlauf einer fremden Karte ist für Emil leer");

    // Frage 7: Gleichzeitigkeit — parallele Schreibzugriffe zweier Benutzer vermischen sich nicht
    var parallelTheme = Enumerable.Range(0, 40).Select(i => i % 2 == 0
        ? dora.PutAsync("/api/preferences/theme", Body(new { theme = "dark" }))
        : emil.PutAsync("/api/preferences/theme", Body(new { theme = "light" })));
    var themeResults = await Task.WhenAll(parallelTheme);
    Check(themeResults.All(r => r.StatusCode == HttpStatusCode.NoContent), "40 parallele Theme-Speicherungen zweier Benutzer: alle erfolgreich");
    Check((await J(await dora.GetAsync("/api/preferences/")))!["theme"]!.GetValue<string>() == "dark"
          && (await J(await emil.GetAsync("/api/preferences/")))!["theme"]!.GetValue<string>() == "light", "… ohne Vermischung der Themes");
    var parallelState = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => i % 2 == 0
        ? dora.PutAsync($"/api/cards/{doraCard}/state", Body(new { is_checked = true, marked_review = false }))
        : emil.PutAsync($"/api/cards/{emilCard}/state", Body(new { is_checked = true, marked_review = false }))));
    Check(parallelState.All(r => r.StatusCode == HttpStatusCode.NoContent), "30 parallele Status-Schreibzugriffe zweier Benutzer: alle erfolgreich");
    // Gleicher Benutzer, gleicher Schlüssel (Upsert = Lesen dann Schreiben): Wettlauf zwischen SELECT und INSERT möglich
    var sameKey = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ =>
        dora.PutAsync($"/api/cards/{doraCard}/state", Body(new { is_checked = false, marked_review = true }))));
    var sameKeyFailures = sameKey.Count(r => r.StatusCode != HttpStatusCode.NoContent);
    Console.WriteLine($"  INFO 25 parallele Upserts desselben Benutzers/derselben Karte: {sameKeyFailures} Fehler (Wettlauf SELECT→INSERT)");

    // Frage 6: Benutzerwechsel im selben Browser und Logout
    var shared = NewClient();
    await shared.GetAsync("/auth/login?devUser=dora");
    Check((await J(await shared.GetAsync("/api/me")))!["id"]!.GetValue<string>() == doraId, "Sitzung als Dora");
    await shared.GetAsync("/auth/login?devUser=emil");   // Benutzerwechsel ohne Logout
    Check((await J(await shared.GetAsync("/api/me")))!["id"]!.GetValue<string>() == emilId
          && (await J(await shared.GetAsync("/api/cards/")))!.AsArray().All(c => c!["term"]!.GetValue<string>() == "E1"), "Nach Benutzerwechsel im selben Browser sieht die Sitzung nur noch Emils Daten");

    var jarHandler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() };
    var withJar = new HttpClient(jarHandler) { BaseAddress = new Uri(baseUrl) };
    await withJar.GetAsync("/auth/login?devUser=dora");
    var stolenCookie = string.Join("; ", jarHandler.CookieContainer.GetCookies(new Uri(baseUrl)).Select(c => $"{c.Name}={c.Value}"));
    await withJar.GetAsync("/auth/logout");
    Check((await withJar.GetAsync("/api/me")).StatusCode == HttpStatusCode.Unauthorized, "Nach Logout ist die Browser-Sitzung abgemeldet (401)");
    using (var replay = new HttpRequestMessage(HttpMethod.Get, "/api/me"))
    {
        replay.Headers.Add("Cookie", stolenCookie);
        var replayStatus = (await NewClient(false).SendAsync(replay)).StatusCode;
        Console.WriteLine($"  INFO Replay des Cookies nach Logout: HTTP {(int)replayStatus} (200 = Cookie bleibt bis zum Ablauf gültig)");
    }

    // Frage 8: alle Endpunkte mit dem Dienstschlüssel
    Console.WriteLine("  INFO Endpunkte mit X-MCP-Key (Dienstschlüssel):");
    var keyMatrix = new (HttpMethod M, string Path, object? Body)[]
    {
        (HttpMethod.Get, "/api/modules/", null), (HttpMethod.Post, "/api/modules/", new { name = "x" }), (HttpMethod.Delete, $"/api/modules/{aliceModuleId}", null),
        (HttpMethod.Get, "/api/cards/", null), (HttpMethod.Get, $"/api/cards/{doraCard}", null), (HttpMethod.Put, $"/api/cards/{doraCard}", new { module = "m", category = "c", term = "t", question = "q", definition = "d" }),
        (HttpMethod.Patch, $"/api/cards/{doraCard}/archive", null), (HttpMethod.Delete, $"/api/cards/{doraCard}", null),
        (HttpMethod.Post, "/api/import/cards", new { cards = Array.Empty<object>() }), (HttpMethod.Post, "/api/cards/" + doraCard + "/quiz", null),
        (HttpMethod.Post, "/api/quiz/start", new { module_id = aliceModuleId }), (HttpMethod.Post, "/api/quiz/submit", new { module_id = aliceModuleId }),
        (HttpMethod.Get, "/api/quiz/history", null), (HttpMethod.Get, "/api/preferences/", null), (HttpMethod.Put, "/api/preferences/theme", new { theme = "dark" }),
        (HttpMethod.Get, $"/api/cards/{doraCard}/state", null), (HttpMethod.Put, $"/api/cards/{doraCard}/state", new { is_checked = true }),
        (HttpMethod.Get, $"/api/chat/history?card_id={doraCard}", null), (HttpMethod.Post, "/api/chat/", new { card_id = doraCard, messages = Array.Empty<object>() }),
        (HttpMethod.Get, "/api/me", null), (HttpMethod.Put, "/api/me", new { display_name = "x" }), (HttpMethod.Get, "/api/mcp/links/", null), (HttpMethod.Get, "/api/mcp/whoami", null),
        (HttpMethod.Get, "/api/admin/users", null), (HttpMethod.Get, "/api/admin/audit", null), (HttpMethod.Get, "/api/admin/unowned-modules", null),
    };
    var keyLeaks = new List<string>();
    foreach (var (m, path, body) in keyMatrix)
    {
        using var req = new HttpRequestMessage(m, path) { Content = body is null ? null : Body(body) };
        var status = (await key.SendAsync(req)).StatusCode;
        if (status != HttpStatusCode.Forbidden) keyLeaks.Add($"{m} {path} → {(int)status}");
    }
    Check(keyLeaks.Count == 0, $"Dienstschlüssel: alle {keyMatrix.Length} benutzerbezogenen Endpunkte liefern 403" + (keyLeaks.Count > 0 ? " — Ausnahmen: " + string.Join("; ", keyLeaks) : ""));

    // Frage 5: manipulierte Anfragen — Benutzerkennung in Headern/Query/Body wird ignoriert
    foreach (var (hname, hval) in new[] { ("X-User-Id", doraId), ("X-Forwarded-User", doraId), ("sub", doraId), ("uid", doraId) })
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/cards/");
        req.Headers.TryAddWithoutValidation(hname, hval);
        var cardsSeen = (await J(await emil.SendAsync(req)))!.AsArray().Select(c => c!["term"]!.GetValue<string>()).ToArray();
        if (!cardsSeen.SequenceEqual(new[] { "E1" })) { Check(false, $"Header '{hname}' beeinflusst die Benutzerzuordnung"); }
    }
    Check((await J(await emil.GetAsync($"/api/cards/?user_sub={doraId}&owner_user_id={doraId}")))!.AsArray().Select(c => c!["term"]!.GetValue<string>()).SequenceEqual(new[] { "E1" }), "Query-Parameter user_sub/owner_user_id werden ignoriert");
    var bodyOwner = await emil.PostAsync("/api/cards/", Body(new { module = "Emil", category = "A", term = "E2", question = "Q?", definition = "D", owner_user_id = doraId, user_sub = doraId }));
    Check((await J(await dora.GetAsync("/api/cards/")))!.AsArray().All(c => c!["term"]!.GetValue<string>() != "E2"), "Besitzer-Felder im Request-Body schieben keine Karte in fremde Bestände");

    Console.WriteLine("\nGesperrte Benutzer");
    Check((await admin.PutAsync($"/api/admin/users/{bobId}/status", Body(new { active = false }))).StatusCode == HttpStatusCode.NoContent, "Admin sperrt Bob");
    Check((await bob.GetAsync("/api/modules/")).StatusCode == HttpStatusCode.Unauthorized, "Gesperrter Benutzer: bestehende Sitzung wird abgewiesen");
    Check((await bTok.GetAsync("/api/modules/")).StatusCode == HttpStatusCode.Unauthorized, "Gesperrter Benutzer: MCP-Token funktioniert nicht mehr");
    var bobMcpAfterBlock = await mcp.SendAsync(new HttpRequestMessage(HttpMethod.Post, mcpUrl + "/mcp")
    {
        Content = Body(new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
        Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bobOAuth!) },
    });
    Check(bobMcpAfterBlock.StatusCode == HttpStatusCode.Unauthorized, "Gesperrter Benutzer: OAuth-Zugang am MCP-Server wird abgewiesen (401)");
    var bobAgain = NewClient();
    Check((await bobAgain.GetAsync("/auth/login?devUser=bob")).Headers.Location?.ToString().Contains("login?error") == true, "Gesperrter Benutzer kann sich nicht neu anmelden");
    Check((await admin.PutAsync($"/api/admin/users/{adminId}/status", Body(new { active = false }))).StatusCode == HttpStatusCode.BadRequest, "Admin kann sich nicht selbst sperren");

    // ─── Freigabe: Version zum Ersetzen wählen, globale Version löschen ───
    Console.WriteLine("\nVeröffentlichung: Ersetzen wählen und Löschen");
    async Task<(string SubmissionId, string ModuleId)> SubmitModuleAsync(HttpClient who, string moduleName)
    {
        var created = await who.PostAsync("/api/cards/", Body(new { module = moduleName, category = "B", term = "T", question = "Q?", definition = "D" }));
        var cardModule = (await J(await who.GetAsync($"/api/cards/{(await J(created))!["id"]!.GetValue<string>()}")))!["module_id"]!.GetValue<string>();
        var sub = await J(await who.PostAsync($"/api/sharing/modules/{cardModule}/submissions", null));
        return (sub!["id"]!.GetValue<string>(), cardModule);
    }
    var (subR1, _) = await SubmitModuleAsync(ben, "Ersetzen-1");
    var approveBogus = await admin.PostAsync($"/api/admin/submissions/{subR1}/approve", Body(new { replace_publication_id = Guid.NewGuid().ToString() }));
    Check(approveBogus.StatusCode == HttpStatusCode.NotFound, "Ersetzen: unbekannte Zielveröffentlichung → 404");
    Check((await J(await admin.GetAsync("/api/admin/submissions?status=pending")))!.AsArray().Any(x => x!["id"]!.GetValue<string>() == subR1), "Ersetzen: die Einreichung bleibt nach falschem Ziel offen");
    var activeBefore = (await J(await ben.GetAsync("/api/modules/?scope=global")))!.AsArray();
    var targetPub = activeBefore[0]!["publication_id"]!.GetValue<string>();
    var targetModuleId = activeBefore[0]!["id"]!.GetValue<string>();
    Check((await admin.PostAsync($"/api/admin/submissions/{subR1}/approve", Body(new { replace_publication_id = targetPub }))).StatusCode == HttpStatusCode.NoContent, "Ersetzen: Einreichung ersetzt die gewählte Veröffentlichung");
    var afterReplace = (await J(await ben.GetAsync("/api/modules/?scope=global")))!.AsArray();
    var replaced = afterReplace.FirstOrDefault(x => x!["name"]!.GetValue<string>() == "Ersetzen-1");
    Check(afterReplace.Count == activeBefore.Count && replaced is not null && replaced["global_version"]!.GetValue<int>() >= 2 && replaced["publication_id"]!.GetValue<string>() == targetPub && afterReplace.All(x => x!["id"]!.GetValue<string>() != targetModuleId), "Ersetzen: neue Version trägt die Veröffentlichungs-ID des ersetzten Moduls, die alte ist nicht mehr aktiv");
    var (subR2, _) = await SubmitModuleAsync(ben, "Ersetzen-2");
    Check((await admin.PostAsync($"/api/admin/submissions/{subR2}/approve", Body(new { replace_publication_id = "new" }))).StatusCode == HttpStatusCode.NoContent
          && (await J(await ben.GetAsync("/api/modules/?scope=global")))!.AsArray().Count == activeBefore.Count + 1, "Ersetzen: „als eigenständig neu“ lässt die bestehende Veröffentlichung aktiv");
    var oldVersionId = targetModuleId;
    Check((await ben.DeleteAsync($"/api/admin/global-modules/{oldVersionId}")).StatusCode == HttpStatusCode.Forbidden, "Löschen: normale Benutzer dürfen keine globale Version löschen");
    Check((await admin.DeleteAsync($"/api/admin/global-modules/{Guid.NewGuid()}")).StatusCode == HttpStatusCode.NotFound, "Löschen: unbekannte Version → 404");
    Check((await admin.DeleteAsync($"/api/admin/global-modules/{oldVersionId}")).StatusCode == HttpStatusCode.NoContent, "Löschen: Administrator löscht eine ersetzte Version");
    var globalNowDel = (await J(await ben.GetAsync("/api/modules/?scope=global")))!.AsArray();
    var delTarget = globalNowDel.First(x => x!["name"]!.GetValue<string>() == "Ersetzen-2")!["id"]!.GetValue<string>();
    Check((await admin.DeleteAsync($"/api/admin/global-modules/{delTarget}")).StatusCode == HttpStatusCode.NoContent
          && (await J(await ben.GetAsync("/api/modules/?scope=global")))!.AsArray().All(x => x!["name"]!.GetValue<string>() != "Ersetzen-2"), "Löschen: die aktive globale Version verschwindet für alle Benutzer");
    Check((await J(await admin.GetAsync("/api/admin/audit?limit=100")))!.ToJsonString().Contains("publication.delete"), "Löschen: wird im Audit-Log festgehalten");

    // ─── Bildspeicher-Provider Ende-zu-Ende (ADR 0017/0018) ───
    Console.WriteLine("\nBildspeicher-Provider Ende-zu-Ende");
    var dbRun = await StartWebAsync("e2e-db", new() { ["IMAGE_STORAGE_PROVIDER"] = "database" });
    try { await ImageScenario("database", dbRun.Url); }
    finally { StopProcess(dbRun.Proc); }
    var localStore = Path.Combine(tmp, "e2e-local-store");
    var localRun = await StartWebAsync("e2e-local", new() { ["IMAGE_STORAGE_PROVIDER"] = "local", ["IMAGE_LOCAL_PATH"] = localStore });
    try
    {
        await ImageScenario("local", localRun.Url, filesOnDisk: () => Directory.Exists(localStore) ? Directory.GetFiles(localStore, "*.img", SearchOption.AllDirectories).Length : 0);
    }
    finally { StopProcess(localRun.Proc); }

    // Azure Blob gegen Azurite (Entwicklungsumgebung: 127.0.0.1:10000) — Lesen ausschließlich über kurzlebige Lese-SAS
    var azuriteUp = false;
    try { using var tcp = new System.Net.Sockets.TcpClient(); await Task.WhenAny(tcp.ConnectAsync("127.0.0.1", 10000), Task.Delay(1500)); azuriteUp = tcp.Connected; } catch { }
    if (azuriteUp)
    {
        var azRun = await StartWebAsync("e2e-azure", new()
        {
            ["IMAGE_STORAGE_PROVIDER"] = "azureblob", ["IMAGE_AZUREBLOB_ACCOUNT"] = "devstoreaccount1", ["IMAGE_AZUREBLOB_ENDPOINT"] = "http://127.0.0.1:10000/devstoreaccount1",
            ["IMAGE_AZUREBLOB_AUTH"] = "sharedkey", ["IMAGE_AZUREBLOB_ACCOUNT_KEY"] = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
            ["IMAGE_AZUREBLOB_ALLOW_HTTP"] = "true", ["IMAGE_AZUREBLOB_ALLOW_PRIVATE_NETWORK"] = "true", ["IMAGE_AZUREBLOB_CONTAINER"] = "learncards-test-" + Guid.NewGuid().ToString("N")[..8],
            ["IMAGE_CACHE_ENABLED"] = "true",
        });
        try
        {
            await ImageScenario("azurite", azRun.Url);
            string logText; lock (log) logText = log.ToString();
            Check(!logText.Contains("sig=") && !logText.Contains("Eby8vdM02xNOc") && !logText.Contains("skoid"), "E2E[azurite]: weder SAS-Signatur noch Kontoschlüssel erscheinen im Serverlog");
            Check(logText.Contains("provider=azureblob") && logText.Contains("cache=hit") && logText.Contains("cache=miss"), "E2E[azurite]: Abrufe werden mit Provider und Cache hit/miss protokolliert");
        }
        finally { StopProcess(azRun.Proc); }
    }
    else Console.WriteLine("  (Azurite auf 127.0.0.1:10000 nicht erreichbar — Azure-Ende-zu-Ende übersprungen)");

    // SharePoint gegen Graph-Mock: Cache-Treffer dürfen keinen Graph-Aufruf auslösen
    await using (var mockGraph = new MockGraph())
    {
        var sp = mockGraph.Settings();
        var spRun = await StartWebAsync("e2e-sharepoint", new()
        {
            ["IMAGE_STORAGE_PROVIDER"] = "sharepoint", ["IMAGE_SHAREPOINT_TENANT_ID"] = sp.TenantId, ["IMAGE_SHAREPOINT_CLIENT_ID"] = sp.ClientId, ["IMAGE_SHAREPOINT_CLIENT_SECRET"] = sp.ClientSecret,
            ["IMAGE_SHAREPOINT_SITE_ID"] = sp.SiteId, ["IMAGE_SHAREPOINT_DRIVE_ID"] = sp.DriveId, ["IMAGE_SHAREPOINT_GRAPH_BASE_URL"] = sp.GraphBaseUrl, ["IMAGE_SHAREPOINT_AUTHORITY"] = sp.Authority,
            ["IMAGE_SHAREPOINT_ALLOW_HTTP"] = "true", ["IMAGE_SHAREPOINT_ALLOW_PRIVATE_NETWORK"] = "true",
        });
        try
        {
            await ImageScenario("sharepoint", spRun.Url, filesOnDisk: () => mockGraph.FileCount);
            // Cache-Verhalten: erster Abruf lädt, danach keine Graph-Aufrufe mehr
            var hc = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(spRun.Url) };
            await hc.GetAsync("/auth/login?devUser=spcache");
            var cr = await hc.PostAsync("/api/cards/", Body(new { module = "SPC", category = "B", term = "T", question = "Q?", definition = "D", images = new[] { new { mime_type = "image/png", data_base64 = Convert.ToBase64String(PngLike(40_000, 77)), alt_text = "x", caption = "" } } }));
            var cj = JsonNode.Parse(await cr.Content.ReadAsStringAsync())!;
            var iid = JsonNode.Parse(await (await hc.GetAsync($"/api/cards/{cj["id"]!.GetValue<string>()}")).Content.ReadAsStringAsync())!["images"]![0]!["image_id"]!.GetValue<string>();
            await (await hc.GetAsync("/api/images/" + iid)).Content.ReadAsByteArrayAsync();   // füllt den Cache
            var metaBefore = mockGraph.MetaCalls; var dlBefore = mockGraph.Downloads; var tokBefore = mockGraph.TokenCalls;
            var bodies = await Task.WhenAll(Enumerable.Range(0, 15).Select(async _ => await (await hc.GetAsync("/api/images/" + iid)).Content.ReadAsByteArrayAsync()));
            Check(bodies.All(b => b.Length == 40_000) && mockGraph.MetaCalls == metaBefore && mockGraph.Downloads == dlBefore && mockGraph.TokenCalls == tokBefore,
                "E2E[sharepoint]: 15 parallele Abrufe aus dem Cache lösen keinen Graph- oder Token-Aufruf aus");
            Check(mockGraph.AuthorizedGraphCallsOnly, "E2E[sharepoint]: alle Graph-Aufrufe nutzten das App-only-Token");
            Check(Directory.Exists(Path.Combine(tmp, "e2e-sharepoint", "data", "image-cache")), "E2E[sharepoint]: persistenter Datei-Cache wird angelegt");
        }
        finally { StopProcess(spRun.Proc); }
    }

    // Bild-Proxy abgeschaltet → 503 mit stabilem Code
    var offRun = await StartWebAsync("e2e-proxyoff", new() { ["IMAGE_PROXY_ENABLED"] = "false" });
    try
    {
        var oc = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(offRun.Url) };
        await oc.GetAsync("/auth/login?devUser=proxyoff");
        var off = await oc.GetAsync("/api/images/" + Guid.NewGuid().ToString("N"));
        Check(off.StatusCode == HttpStatusCode.ServiceUnavailable && (await off.Content.ReadAsStringAsync()).Contains("proxy_disabled"), "IMAGE_PROXY_ENABLED=false: /api/images/{id} antwortet 503 (proxy_disabled)");
    }
    finally { StopProcess(offRun.Proc); }

    // Migration database → local (ausdrücklich gestartet, wiederaufnehmbar, Quelle bleibt erhalten)
    var migStore = Path.Combine(tmp, "e2e-mig-store");
    var migRun = await StartWebAsync("e2e-migrate", new() { ["IMAGE_STORAGE_PROVIDER"] = "database", ["IMAGE_LOCAL_PATH"] = migStore, ["ADMIN_SUBJECTS"] = "dev-migadmin" });
    try
    {
        var mc = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(migRun.Url) };
        await mc.GetAsync("/auth/login?devUser=migadmin");
        var imgsIn = new[] { PngLike(3_000, 21), PngLike(4_000, 22), PngLike(5_000, 23) };
        var mcr = await mc.PostAsync("/api/cards/", Body(new { module = "MIG", category = "B", term = "T", question = "Q?", definition = "D",
            images = imgsIn.Select(d => new { mime_type = "image/png", data_base64 = Convert.ToBase64String(d), alt_text = "m", caption = "" }).ToArray() }));
        var mcid = JsonNode.Parse(await mcr.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
        var before = JsonNode.Parse(await (await mc.GetAsync("/api/admin/images/storage")).Content.ReadAsStringAsync())!;
        Check(before["primary"]!.GetValue<string>() == "database" && before["image_counts"]!["database"]!.GetValue<int>() >= 3, "Migration: Status zeigt Primärprovider und Bildanzahl je Provider");
        var dry = JsonNode.Parse(await (await mc.PostAsync("/api/admin/images/migrate", Body(new { source = "database", target = "local", limit = 100, dry_run = true }))).Content.ReadAsStringAsync())!;
        Check(dry["migrated"]!.GetValue<int>() == 3 && (Directory.Exists(migStore) ? Directory.GetFiles(migStore, "*.img", SearchOption.AllDirectories).Length : 0) == 0, "Migration: Trockenlauf prüft die Quelle, schreibt aber nichts");
        var part1 = JsonNode.Parse(await (await mc.PostAsync("/api/admin/images/migrate", Body(new { source = "database", target = "local", limit = 2 }))).Content.ReadAsStringAsync())!;
        Check(part1["migrated"]!.GetValue<int>() == 2 && part1["remaining"]!.GetValue<int>() == 1 && part1["next_cursor"] is not null, "Migration: Teilabschnitt (limit=2) liefert Rest und Cursor");
        var part2 = JsonNode.Parse(await (await mc.PostAsync("/api/admin/images/migrate", Body(new { source = "database", target = "local", limit = 2, cursor = part1["next_cursor"]!.GetValue<string>() }))).Content.ReadAsStringAsync())!;
        Check(part2["migrated"]!.GetValue<int>() == 1 && part2["remaining"]!.GetValue<int>() == 0 && part2["failed"]!.GetValue<int>() == 0, "Migration: Fortsetzung mit Cursor schließt ab, keine Fehler");
        var after = JsonNode.Parse(await (await mc.GetAsync("/api/admin/images/storage")).Content.ReadAsStringAsync())!;
        Check(after["image_counts"]!["local"]!.GetValue<int>() >= 3 && Directory.GetFiles(migStore, "*.img", SearchOption.AllDirectories).Length == 3, "Migration: Metadaten zeigen auf 'local', 3 Dateien im Ziel");
        var cardAfter = JsonNode.Parse(await (await mc.GetAsync($"/api/cards/{mcid}")).Content.ReadAsStringAsync())!["images"]!.AsArray();
        var same = true;
        for (var i = 0; i < 3; i++) same &= (await (await mc.GetAsync("/api/images/" + cardAfter[i]!["image_id"]!.GetValue<string>())).Content.ReadAsByteArrayAsync()).SequenceEqual(imgsIn[i]);
        Check(same, "Migration: Bilder sind nach der Migration byte-identisch abrufbar (jetzt aus dem Ziel)");
        var again = JsonNode.Parse(await (await mc.PostAsync("/api/admin/images/migrate", Body(new { source = "database", target = "local" }))).Content.ReadAsStringAsync())!;
        Check(again["processed"]!.GetValue<int>() == 0, "Migration: erneuter Lauf ist idempotent (nichts mehr zu tun)");
        Check((await mc.PostAsync("/api/admin/images/migrate", Body(new { source = "database", target = "s3" }))).StatusCode == HttpStatusCode.BadRequest, "Migration: nicht konfigurierter Zielprovider wird abgewiesen (400)");
        var audit = await (await mc.GetAsync("/api/admin/audit?limit=50")).Content.ReadAsStringAsync();
        Check(audit.Contains("images.migrate"), "Migration: Lauf wird im Audit-Log festgehalten");
        var other = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(migRun.Url) };
        await other.GetAsync("/auth/login?devUser=migplain");
        Check((await other.PostAsync("/api/admin/images/migrate", Body(new { source = "database", target = "local" }))).StatusCode == HttpStatusCode.Forbidden, "Migration: nur Administratoren dürfen sie starten (403)");
    }
    finally { StopProcess(migRun.Proc); }

    if (perfRows.Count > 0)
    {
        Console.WriteLine("\nPerformance je Provider (ms; Messung auf dieser Maschine, lokale Dienste)");
        foreach (var row in perfRows) Console.WriteLine("  PERFROW " + row);
    }
}
catch (Exception ex)
{
    failures++;
    Console.WriteLine("\nTESTLAUF ABGEBROCHEN: " + ex);
}
finally
{
    try { if (mcpServer is { HasExited: false }) { mcpServer.Kill(true); mcpServer.WaitForExit(5000); } } catch { }
    try { if (!server.HasExited) { server.Kill(true); server.WaitForExit(5000); } } catch { }
    try { if (failures == 0) Directory.Delete(tmp, true); else Console.WriteLine($"\nServer-Log:\n{log}"); } catch { }
}

Console.WriteLine($"\n{checks - failures}/{checks} Prüfungen bestanden" + (failures == 0 ? "" : $", {failures} FEHLGESCHLAGEN"));
return failures == 0 ? 0 : 1;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LearnCards.slnx"))) dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

static int FreePort()
{
    var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    l.Start();
    var p = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}
