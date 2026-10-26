using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography.X509Certificates;

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
Env("MCP_TOKEN_KEY", "aW50ZWdyYXRpb24tdGVzdC1rZXktMDEyMzQ1Njc4OSE=");   // Pflicht (ADR 0025); nur Testwert
Env("MCP_PUBLIC_URL", mcpUrl);
Env("OPENAI_API_KEY", "");
using (var pdfCert = PdfTests.NewRsaCert("CN=Learny Test PDF Signer, O=LearnCards Tests"))   // eigenes PDF-Signaturzertifikat über die Umgebung (ADR 0033)
{
    Env("PDF_SIGNING_CERT_PFX_BASE64", Convert.ToBase64String(pdfCert.Export(X509ContentType.Pkcs12, "pdf-test")));
    Env("PDF_SIGNING_CERT_PASSWORD", "pdf-test");
}
Env("DATA_PROTECTION_DIR", Path.Combine(tmp, "keys"));
const string PkiKeyPassword = "it-pki-key-password-0123", PkiRootPassphrase = "it-pki-root-passphrase-0123";
var pkiKeyDir = Path.Combine(tmp, "pki");
Env("PKI_PUBLIC_URL", baseUrl);   // Learny-PKI (ADR 0035): wird beim ersten Start automatisch eingerichtet
Env("PKI_KEY_DIR", pkiKeyDir); Env("PKI_KEY_PASSWORD", PkiKeyPassword); Env("PKI_ROOT_PASSPHRASE", PkiRootPassphrase);
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

static string SchemaTopicExample(JsonNode topicSchema)
{
    var ex = topicSchema["example"]!.DeepClone();
    ex["name"] = "Schema-Beispiel";
    ex["modules"] = new JsonArray(ex["modules"]![0]!.DeepClone());   // nur das importierte Beispielmodul „Microsoft Entra ID“
    return ex.ToJsonString();
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
    info.Environment["MCP_TOKEN_KEY"] = "aW50ZWdyYXRpb24tdGVzdC1rZXktMDEyMzQ1Njc4OSE=";
    info.Environment["OPENAI_API_KEY"] = "";
    info.Environment["DB_PROVIDER"] = "sqlite";
    info.Environment["SQLITE_PATH"] = Path.Combine(dir, "e2e.db");
    info.Environment["DATA_PROTECTION_DIR"] = Path.Combine(dir, "keys");
    foreach (var (k, v) in extraEnv) info.Environment[k] = v.Replace("{URL}", url);
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
    RichTextUnitTests.Run(Check);
    await BlockchainUnitTests.RunAsync(Check, tmp);
    PdfTests.Unit(Check);   // PDF, CMS-Signatur, Umbruch (ADR 0033)
    await TransferIntegrityUnitTests.RunAsync(Check, tmp);   // Fehlerinjektion, Referenzen, Altbestand (ADR 0028)
    WorkflowStructureTests.Run(Check, repoRoot);   // IIS-Workflow: MCP-Neustart (ADR 0029)
    await McpTokenUnitTests.RunAsync(Check, tmp);   // Schutz der MCP-Tokens bei Datenbankzugriff (ADR 0025)
    AuthModeTests.Run(Check);   // Betriebsmodus fail closed (ADR 0026)
    DockerConfigTests.Run(Check, repoRoot);   // Docker-Hub-Veröffentlichung (ADR 0024)
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
    if (Environment.GetEnvironmentVariable("TEST_ONLY") == "topics")   // Entwicklungsabkürzung: nur Themen-/Modulversions-Tests
    {
        await TopicE2ETests.RunAsync(LoginAsync, () => NewClient(false), admin, mcpUrl, Check);
        await TopicTransferTests.RunAsync(LoginAsync, () => NewClient(false), admin, mcpUrl, Check);
        await TransferHardeningTests.RunAsync(LoginAsync, () => NewClient(false), mcpUrl, McpKey, PngLike, Check);
        await TopicViewTests.RunAsync(LoginAsync, Check);
        await ModuleVersionTests.RunAsync(LoginAsync, Check);
        await CompetencyTests.RunAsync(LoginAsync, () => NewClient(false), Check);
        await TopicCredentialTests.RunAsync(LoginAsync, () => NewClient(false), admin, Check);
        await TopicGovernanceTests.RunAsync(LoginAsync, () => NewClient(false), admin, mcpUrl, Check);
        Console.WriteLine($"{checks - failures}/{checks} Prüfungen bestanden" + (failures == 0 ? "" : $", {failures} FEHLGESCHLAGEN"));
        return failures == 0 ? 0 : 1;
    }
    if (Environment.GetEnvironmentVariable("TEST_ONLY") == "pki")   // Entwicklungsabkürzung: nur Learny-PKI
    {
    await PkiTests.RunAsync(LoginAsync, () => NewClient(false), admin, baseUrl, pkiKeyDir, PkiKeyPassword, PkiRootPassphrase, StartWebAsync, StopProcess, () => { lock (log) return log.ToString(); }, Check);   // Learny-PKI (ADR 0035)
        Console.WriteLine($"{checks - failures}/{checks} Prüfungen bestanden" + (failures == 0 ? "" : $", {failures} FEHLGESCHLAGEN"));
        return failures == 0 ? 0 : 1;
    }
    if (Environment.GetEnvironmentVariable("TEST_ONLY") == "topiccreds")
    {
        await TopicCredentialTests.RunAsync(LoginAsync, () => NewClient(false), admin, Check);
        Console.WriteLine($"{checks - failures}/{checks} Prüfungen bestanden" + (failures == 0 ? "" : $", {failures} FEHLGESCHLAGEN"));
        return failures == 0 ? 0 : 1;
    }
    if (Environment.GetEnvironmentVariable("TEST_ONLY") == "competencies")
    {
        await CompetencyTests.RunAsync(LoginAsync, () => NewClient(false), Check);
        Console.WriteLine($"{checks - failures}/{checks} Prüfungen bestanden" + (failures == 0 ? "" : $", {failures} FEHLGESCHLAGEN"));
        return failures == 0 ? 0 : 1;
    }
    if (Environment.GetEnvironmentVariable("TEST_ONLY") == "cardtools")   // Entwicklungsabkürzung: nur MCP-Kartenwerkzeuge (get_card/update_card/export_cards)
    {
        await CardToolsTests.RunAsync(LoginAsync, () => NewClient(false), admin, mcpUrl, McpKey, PngLike, Check);
        await TopicViewTests.RunAsync(LoginAsync, Check);
        await ModuleVersionTests.RunAsync(LoginAsync, Check);
        await CompetencyTests.RunAsync(LoginAsync, () => NewClient(false), Check);
        Console.WriteLine($"{checks - failures}/{checks} Prüfungen bestanden" + (failures == 0 ? "" : $", {failures} FEHLGESCHLAGEN"));
        return failures == 0 ? 0 : 1;
    }
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
    // Claude-Registrierung: Discovery muss in sich stimmig sein — jede veröffentlichte URL liegt auf dem öffentlichen Host (Fehlerbild: Issuer mit falschem Host → „Registrierung fehlgeschlagen“)
    Check(new[] { "issuer", "authorization_endpoint", "token_endpoint", "registration_endpoint" }.All(k => meta[k]!.GetValue<string>().StartsWith(mcpUrl, StringComparison.Ordinal)), "Metadaten: issuer, authorization-, token- und registration_endpoint liegen auf demselben öffentlichen Host");
    var prm = await J(await mcp.GetAsync(mcpUrl + "/.well-known/oauth-protected-resource/mcp"));
    Check(prm!["resource"]!.GetValue<string>() == mcpUrl + "/mcp" && prm["authorization_servers"]![0]!.GetValue<string>() == mcpUrl, "Protected-Resource-Metadaten: resource = <öffentliche URL>/mcp, authorization_servers = Issuer");
    Check((await mcp.GetAsync(mcpUrl + "/.well-known/oauth-protected-resource")).StatusCode == HttpStatusCode.OK, "Protected-Resource-Metadaten auch ohne Pfad erreichbar");
    var dcr = await mcp.PostAsync(meta["registration_endpoint"]!.GetValue<string>(), Body(new
    {
        client_name = "Claude", redirect_uris = new[] { "https://claude.ai/api/mcp/auth_callback" }, grant_types = new[] { "authorization_code", "refresh_token" },
        response_types = new[] { "code" }, token_endpoint_auth_method = "none", scope = "mcp",
    }));
    var dcrJson = await J(dcr);
    Check(dcr.StatusCode == HttpStatusCode.Created && (dcr.Content.Headers.ContentType?.MediaType ?? "") == "application/json"
          && dcrJson!["client_id"]!.GetValue<string>().Length > 8 && dcrJson["token_endpoint_auth_method"]!.GetValue<string>() == "none"
          && dcrJson["redirect_uris"]![0]!.GetValue<string>() == "https://claude.ai/api/mcp/auth_callback" && (dcrJson["client_secret"]?.GetValue<string>() ?? "") == "",
        "DCR über den veröffentlichten registration_endpoint: öffentlicher Client (https-Redirect, auth_method none) → 201 JSON mit client_id ohne Secret");
    Check((await mcp.PostAsync(meta["registration_endpoint"]!.GetValue<string>(), Body(new { client_name = "x", token_endpoint_auth_method = "none" }))).StatusCode == HttpStatusCode.BadRequest
          && (await mcp.PostAsync(meta["registration_endpoint"]!.GetValue<string>(), new StringContent("{kaputt", System.Text.Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.BadRequest, "DCR: fehlende Redirect-URIs und fehlerhaftes JSON → 400");
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

    // Neustart des MCP-Servers (z. B. Deployment): dynamisch registrierte öffentliche Clients bleiben erhalten (verschlüsselt in Learny, ADR 0025)
    {
        mcpServer!.Kill(true); mcpServer.WaitForExit(5000);
        mcpServer = Process.Start(mcpPsi)!;
        mcpServer.OutputDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine("[mcp2] " + e.Data); };
        mcpServer.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine("[mcp2] " + e.Data); };
        mcpServer.BeginOutputReadLine(); mcpServer.BeginErrorReadLine();
        var back = false;
        for (var i = 0; i < 80 && !back && !mcpServer.HasExited; i++)
        {
            try { back = (await mcp.GetAsync(mcpUrl + "/health")).IsSuccessStatusCode; } catch { await Task.Delay(250); }
        }
        Check(back, "MCP-Server startet nach dem Neustart wieder");
        var cv = "n" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var cc = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(cv))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var afterRestart = await mcp.GetAsync($"{mcpUrl}/oauth/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(cbUri)}&state=s1&code_challenge={cc}&code_challenge_method=S256&scope=mcp");
        Check(afterRestart.StatusCode == HttpStatusCode.Redirect && afterRestart.Headers.Location!.ToString().StartsWith(baseUrl + "/mcp/connect"),
            "OAuth-Client bleibt nach einem Neustart des MCP-Servers bekannt (Autorisierung ohne erneute Registrierung)");
        var unknownClient = await mcp.GetAsync($"{mcpUrl}/oauth/authorize?response_type=code&client_id=learncards-{Guid.NewGuid():N}&redirect_uri={Uri.EscapeDataString(cbUri)}&state=s2&code_challenge={cc}&code_challenge_method=S256&scope=mcp");
        Check(unknownClient.StatusCode == HttpStatusCode.BadRequest, "Unbekannte client_id wird weiterhin abgewiesen (kein Nachladen ins Leere)");
        // Dienstschlüssel-Endpunkte: Benutzer/Anonyme dürfen Clients weder lesen noch schreiben
        Check((await alice.GetAsync("/api/mcp/oauth/clients/" + clientId)).StatusCode == HttpStatusCode.Forbidden
              && (await anon.GetAsync("/api/mcp/oauth/clients/" + clientId)).StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
              && (await alice.PostAsync("/api/mcp/oauth/clients", Body(new { client_id = "learncards-" + Guid.NewGuid().ToString("N"), redirect_uris = new[] { "https://claude.ai/cb" }, token_endpoint_auth_method = "none" }))).StatusCode == HttpStatusCode.Forbidden,
            "OAuth-Client-Endpunkte nur mit Dienstschlüssel (Benutzer und Anonyme: 403/401)");
        var stored = await J(await key.GetAsync("/api/mcp/oauth/clients/" + clientId));
        Check(stored!["client_id"]!.GetValue<string>() == clientId && stored["token_endpoint_auth_method"]!.GetValue<string>() == "none" && stored["client_secret"] is null, "Gespeicherter Client enthält kein Secret und liefert die Registrierungsdaten");
    }

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
    await TopicE2ETests.RunAsync(LoginAsync, () => NewClient(false), admin, mcpUrl, Check);   // Themen (ADR 0027)

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

    // ─── Einheitliches Quizsystem (ADR 0019): Konfiguration, Pool, Sitzungen, Bewertung, Rechte ───
    Console.WriteLine("\nQuiz-Engine (ADR 0019)");
    async Task<(string ModuleId, string[] CardIds)> MakeQuizModule(HttpClient who, string name)
    {
        var defs = new object[]
        {
            new { module = name, category = "K", term = "SC-Karte", question = "Frage SC?", definition = "Definition der SC-Karte",
                  quiz = new object[] { new { type = "single_choice", question = "Welche Antwort stimmt?", options = new[] { "A", "B", "C", "D" }, correct_index = 1, explanation = "B stimmt." } } },
            new { module = name, category = "K", term = "MR-Karte", question = "Frage MR?", definition = "Definition der MR-Karte",
                  quiz = new object[]
                  {
                      new { type = "multiple_response", question = "Welche sind Primzahlen?", options = new[] { "2", "3", "4", "6" }, correct_indexes = new[] { 0, 1 }, explanation = "2 und 3." },
                      new { type = "free_text", question = "Erkläre den Begriff MR.", options = Array.Empty<string>(), explanation = "Muster: MR ist ein Auswahlformat mit mehreren richtigen Antworten." },
                  } },
            new { module = name, category = "K", term = "Ohne-Quiz", question = "Frage ohne Quiz?", definition = "Definition ohne Quiz", quiz = Array.Empty<object>() },
        };
        var ids = new List<string>();
        foreach (var d in defs)
        {
            var created = await who.PostAsync("/api/cards/", Body(d));
            ids.Add((await J(created))!["id"]!.GetValue<string>());
            await who.PutAsync($"/api/cards/{ids[^1]}/state", Body(new { is_checked = true, marked_review = false }));
        }
        var module = (await J(await who.GetAsync($"/api/cards/{ids[0]}")))!["module_id"]!.GetValue<string>();
        return (module, ids.ToArray());
    }
    async Task<(HttpStatusCode Status, JsonNode? Body)> QStart(HttpClient who, string moduleId, string? cardId = null, int n = 5)
    {
        var r = cardId is null
            ? await who.PostAsync("/api/quiz/start", Body(new { module_id = moduleId, category = "", card_id = "", num_questions = n }))
            : await who.PostAsync($"/api/cards/{cardId}/quiz", null);
        return (r.StatusCode, await J(r));
    }
    // Antworten: richtige Auswahl (nach Fragetext), Freitext mit Stichworten der Musterlösung
    object[] QAnswers(JsonNode start, bool correct, Func<string, int[]>? pick = null) => start["questions"]!.AsArray().Select(q =>
    {
        var text = q!["question"]!.GetValue<string>();
        var type = q["type"]!.GetValue<string>();
        int[] sel = type == "free_text" ? Array.Empty<int>()
            : pick is not null ? pick(text)
            : !correct ? new[] { 3 }
            : text.StartsWith("Welche Antwort") ? new[] { 1 } : new[] { 0, 1 };
        return (object)new { question = text, question_id = q["question_id"]!.GetValue<string>(), source_card_id = q["source_card_id"]!.GetValue<string>(), user_answer = type == "free_text" && correct ? "Auswahlformat mehreren richtigen Antworten" : "", selected_indexes = sel };
    }).ToArray();
    async Task<(HttpStatusCode Status, JsonNode? Body)> QSubmit(HttpClient who, string moduleId, JsonNode start, object[] answers, string cardId = "")
    {
        var r = await who.PostAsync("/api/quiz/submit", Body(new { module_id = moduleId, category = "", card_id = cardId, session_id = start["session_id"]!.GetValue<string>(), answers }));
        return (r.StatusCode, await J(r));
    }

    var (qm, qCards) = await MakeQuizModule(ann, "QZ-Engine");
    var modulePageHtml = await (await ann.GetAsync($"/module/{qm}")).Content.ReadAsStringAsync();
    Check(modulePageHtml.Contains("Quiz-Einstellungen") && modulePageHtml.Contains("Quiz starten"), "Quiz-UI: Modulseite rendert den Einstieg zu den Quiz-Einstellungen (Serverrendering)");
    var cfg0 = (await J(await ann.GetAsync($"/api/modules/{qm}/quiz-config")))!;
    Check(cfg0["can_edit"]!.GetValue<bool>() && cfg0["config"]!["enabled"]!.GetValue<bool>() && cfg0["config"]!["pass_percent"]!.GetValue<int>() == 60
          && cfg0["config"]!["origin"]!.GetValue<string>() == "mixed" && cfg0["config"]!["strategy"]!.GetValue<string>() == "on_demand", "Quiz: Standardkonfiguration ist kompatibel (aktiv, 60 %, gemischt, bei Bedarf)");
    var pool0 = (await J(await ann.GetAsync($"/api/modules/{qm}/quiz-questions")))!.AsArray();
    Check(pool0.Count == 3 && pool0.All(q => q!["source"]!.GetValue<string>() == "card_json" && q["origin"]!.GetValue<string>() == "manual" && q["active"]!.GetValue<bool>()),
        "Quiz: quiz[] der Karten erscheint als Projektion im Fragenpool (3 Fragen, manuell, aktiv)");
    Check(pool0.Any(q => q!["type"]!.GetValue<string>() == "multiple_response" && q["correct_indexes"]!.AsArray().Count == 2) && pool0.Any(q => q!["type"]!.GetValue<string>() == "free_text"), "Quiz: Multiple Response und Freitext aus quiz[] werden übernommen");

    // Import-Verträge: alte quiz[]-Struktur gültig, ungültige Erweiterungen abgelehnt
    var badMr = await ann.PostAsync("/api/cards/", Body(new { module = "QZ-Engine", category = "K", term = "Bad", question = "Q?", definition = "D", quiz = new object[] { new { type = "multiple_response", question = "x?", options = new[] { "a", "b", "c" }, correct_indexes = new[] { 5 }, explanation = "e" } } }));
    var badType = await ann.PostAsync("/api/cards/", Body(new { module = "QZ-Engine", category = "K", term = "Bad2", question = "Q?", definition = "D", quiz = new object[] { new { type = "essay", question = "x?", explanation = "e" } } }));
    Check(badMr.StatusCode == HttpStatusCode.BadRequest && badType.StatusCode == HttpStatusCode.BadRequest, "Quiz: ungültige quiz[]-Einträge (falscher Index, unbekannter Typ) werden abgelehnt");
    var schemaText = (await (await ann.GetAsync("/api/schema/import/cards")).Content.ReadAsStringAsync());
    Check(schemaText.Contains("correct_index") && schemaText.Contains("multiple_response") && schemaText.Contains("single_choice"), "Quiz: Importschema nennt weiterhin correct_index und zusätzlich multiple_response");

    // Kartenquiz: Sitzung ohne Lösungen, serverseitige Bewertung
    var cardStart = await QStart(ann, qm, qCards[0]);
    var cardStartText = cardStart.Body!.ToJsonString();
    Check(cardStart.Status == HttpStatusCode.OK && cardStart.Body["questions"]!.AsArray().Count == 1 && cardStart.Body["questions"]![0]!["type"]!.GetValue<string>() == "single_choice"
          && !cardStartText.Contains("correct_index") && !cardStartText.Contains("B stimmt"), "Quiz: Kartenquiz startet über die Engine; die Antwort enthält keine Lösung");
    var cardOk = await QSubmit(ann, qm, cardStart.Body, QAnswers(cardStart.Body, true), qCards[0]);
    Check(cardOk.Status == HttpStatusCode.OK && cardOk.Body!["percentage"]!.GetValue<double>() == 100 && cardOk.Body["passed"]!.GetValue<bool>() && cardOk.Body["graded_answers"]![0]!["correct"]!.GetValue<bool>(),
        "Quiz: richtige Single-Choice-Antwort → 100 %, bestanden (serverseitig bewertet)");
    Check((await QSubmit(ann, qm, cardStart.Body, QAnswers(cardStart.Body, true), qCards[0])).Status == HttpStatusCode.Conflict, "Quiz: eine Sitzung kann nur einmal abgegeben werden (409)");
    Check((await QSubmit(ben, qm, cardStart.Body, QAnswers(cardStart.Body, true), qCards[0])).Status == HttpStatusCode.NotFound, "Quiz: fremder Benutzer kann die Sitzung nicht abgeben (404)");
    var cardBad = await QStart(ann, qm, qCards[0]);
    var cardBadRes = await QSubmit(ann, qm, cardBad.Body!, QAnswers(cardBad.Body!, false), qCards[0]);
    Check(cardBadRes.Body!["percentage"]!.GetValue<double>() == 0 && !cardBadRes.Body["passed"]!.GetValue<bool>() && cardBadRes.Body["graded_answers"]![0]!["correct_indexes"]![0]!.GetValue<int>() == 1, "Quiz: falsche Antwort → 0 %, nicht bestanden; richtige Antwort wird angezeigt");

    // Multiple Response: nur die exakte Menge zählt
    var mrStart = await QStart(ann, qm, qCards[1]);
    Check(mrStart.Body!["questions"]!.AsArray().Select(q => q!["type"]!.GetValue<string>()).OrderBy(x => x).SequenceEqual(new[] { "free_text", "multiple_response" }), "Quiz: Kartenquiz einer Karte mit zwei Fragen liefert Multiple Response und Freitext");
    var mrPartial = await QSubmit(ann, qm, mrStart.Body, QAnswers(mrStart.Body, true, t => new[] { 0 }), qCards[1]);
    var mrPartialGraded = mrPartial.Body!["graded_answers"]!.AsArray().Single(g => g!["type"]!.GetValue<string>() == "multiple_response")!;
    Check(!mrPartialGraded["correct"]!.GetValue<bool>() && mrPartialGraded["score"]!.GetValue<double>() == 0, "Quiz: Multiple Response mit nur einer von zwei richtigen Optionen → falsch");
    var mrStart2 = await QStart(ann, qm, qCards[1]);
    var mrFull = await QSubmit(ann, qm, mrStart2.Body!, QAnswers(mrStart2.Body!, true), qCards[1]);
    Check(mrFull.Body!["graded_answers"]!.AsArray().Single(g => g!["type"]!.GetValue<string>() == "multiple_response")!["correct"]!.GetValue<bool>(), "Quiz: Multiple Response mit allen richtigen Optionen → richtig");

    // Modulquiz: gleiche Engine, Fragen aus dem Pool + Offline-Ableitung aus Karten (ohne KI)
    var modStart = await QStart(ann, qm);
    var modTypes = modStart.Body!["questions"]!.AsArray().Select(q => q!["type"]!.GetValue<string>()).ToList();
    Check(modStart.Status == HttpStatusCode.OK && modStart.Body["questions"]!.AsArray().Count == 4 && modTypes.Contains("single_choice") && modTypes.Contains("multiple_response") && modTypes.Count(t => t == "free_text") == 2,
        "Quiz: Modulquiz mischt gespeicherte Fragen (3) und eine aus der Karte abgeleitete Freitextfrage (Offline-Fallback)");
    Check(modStart.Body["notices"]!.AsArray().Count > 0, "Quiz: fehlende KI wird als Hinweis gemeldet (kein stiller Wechsel)");
    var modRes = await QSubmit(ann, qm, modStart.Body, QAnswers(modStart.Body, true));
    Check(modRes.Status == HttpStatusCode.OK && modRes.Body!["graded_answers"]!.AsArray().Count == 4 && modRes.Body["score"]!.GetValue<double>() >= 20 && modRes.Body["max_score"]!.GetValue<double>() == 40
          && modRes.Body["pass_percent"]!.GetValue<int>() == 60, "Quiz: Modulquiz bewertet gemischte Fragetypen mit derselben Logik (Auswahl exakt, Freitext lokal)");
    var hist = (await J(await ann.GetAsync($"/api/quiz/history?module_id={qm}")))!.AsArray();
    Check(hist.Count >= 4 && hist.Any(h => !string.IsNullOrEmpty(h!["session_id"]?.GetValue<string>()) && h["passed"] is not null && h["pass_percent"] is not null), "Quiz: Ergebnisse speichern Sitzung, Bestanden-Status und Bestehensgrenze (für Auftrag 3)");

    // Rückwärtskompatibilität: alter Ablauf ohne Sitzung (Freitext) funktioniert weiter
    var legacy = await ann.PostAsync("/api/quiz/submit", Body(new { module_id = qm, category = "", card_id = "", answers = new[] { new { question = "Frage SC?", user_answer = "Definition der SC-Karte", source_card_id = qCards[0] } } }));
    var legacyBody = await J(legacy);
    Check(legacy.IsSuccessStatusCode && legacyBody!["graded_answers"]!.AsArray().Count == 1 && legacyBody["passed"] is not null, "Quiz: Abgabe ohne Sitzung (älterer Client) wird weiter als Freitext bewertet");

    // Konfiguration: ungültige Kombinationen werden verhindert
    async Task<HttpResponseMessage> PutCfg(HttpClient who, string moduleId, object cfg) => await who.PutAsync($"/api/modules/{moduleId}/quiz-config", Body(cfg));
    var baseCfg = new Func<Dictionary<string, object>>(() => new()
    {
        ["enabled"] = true, ["allow_multiple_choice"] = true, ["origin"] = "mixed", ["strategy"] = "on_demand", ["single_choice"] = true, ["multiple_response"] = true, ["free_text"] = true,
        ["question_count"] = 5, ["shuffle_questions"] = true, ["shuffle_answers"] = false, ["scope"] = "both", ["pass_percent"] = 60, ["allow_retry"] = true, ["show_correct_answers"] = true,
    });
    var c1 = baseCfg(); c1["allow_multiple_choice"] = false;
    var r1 = await PutCfg(ann, qm, c1);
    Check(r1.StatusCode == HttpStatusCode.BadRequest && (await r1.Content.ReadAsStringAsync()).Contains("Multiple Choice"), "Quiz-Konfiguration: Multiple Choice aus + Auswahlformate an → 400 mit Begründung");
    var c2 = baseCfg(); c2["single_choice"] = false; c2["multiple_response"] = false; c2["free_text"] = false;
    Check((await PutCfg(ann, qm, c2)).StatusCode == HttpStatusCode.BadRequest, "Quiz-Konfiguration: aktives Quiz ohne erlaubtes Frageformat → 400");
    var c3 = baseCfg(); c3["origin"] = "fixed";
    Check((await PutCfg(ann, qm, c3)).StatusCode == HttpStatusCode.BadRequest, "Quiz-Konfiguration: nur feste Fragen + Generierung → 400");
    var c4 = baseCfg(); c4["pass_percent"] = 0; var c5 = baseCfg(); c5["question_count"] = 99; var c6 = baseCfg(); c6["scope"] = "x";
    Check((await PutCfg(ann, qm, c4)).StatusCode == HttpStatusCode.BadRequest && (await PutCfg(ann, qm, c5)).StatusCode == HttpStatusCode.BadRequest && (await PutCfg(ann, qm, c6)).StatusCode == HttpStatusCode.BadRequest, "Quiz-Konfiguration: Bestehensgrenze, Umfang und Bereich werden geprüft");

    // Multiple Choice aus → nur Freitext
    var cFt = baseCfg(); cFt["allow_multiple_choice"] = false; cFt["single_choice"] = false; cFt["multiple_response"] = false;
    Check((await PutCfg(ann, qm, cFt)).IsSuccessStatusCode, "Quiz-Konfiguration: Multiple Choice deaktivieren (Auswahlformate aus) wird gespeichert");
    var ftStart = await QStart(ann, qm);
    Check(ftStart.Status == HttpStatusCode.OK && ftStart.Body!["questions"]!.AsArray().All(q => q!["type"]!.GetValue<string>() == "free_text" && q["options"]!.AsArray().Count == 0), "Quiz: ohne Multiple Choice erscheinen nur Freitextfragen");

    // Nur Auswahl: ohne passende Fragen kein stiller Wechsel auf Freitext
    var cChoice = baseCfg(); cChoice["free_text"] = false; cChoice["question_count"] = 10;
    await PutCfg(ann, qm, cChoice);
    var choiceStart = await QStart(ann, qm);
    Check(choiceStart.Body!["questions"]!.AsArray().All(q => q!["type"]!.GetValue<string>() != "free_text") && choiceStart.Body["questions"]!.AsArray().Count == 2, "Quiz: nur Auswahlformate → nur gespeicherte Auswahlfragen, keine abgeleiteten Freitextfragen");
    // Modul ohne Auswahlfragen, nur Auswahl erlaubt → klare Fehlermeldung statt stillem Wechsel
    var onlyFree = await ann.PostAsync("/api/cards/", Body(new { module = "QZ-NurKarten", category = "K", term = "K1", question = "Q1?", definition = "D1" }));
    var onlyFreeModule = (await J(await ann.GetAsync($"/api/cards/{(await J(onlyFree))!["id"]!.GetValue<string>()}")))!["module_id"]!.GetValue<string>();
    var onlyFreeCard = (await J(onlyFree))!["id"]!.GetValue<string>();
    await ann.PutAsync($"/api/cards/{onlyFreeCard}/state", Body(new { is_checked = true, marked_review = false }));
    await PutCfg(ann, onlyFreeModule, cChoice);
    var noChoice = await QStart(ann, onlyFreeModule);
    Check(noChoice.Status == HttpStatusCode.NotFound && noChoice.Body!["detail"]!.GetValue<string>().Contains("Frageformate"), "Quiz: nur Auswahl erlaubt, aber keine Auswahlfragen → Fehlermeldung statt Wechsel auf Freitext");

    // Umfang, Bereich, Bestehensgrenze, Anzeige der Lösungen
    var cMisc = baseCfg(); cMisc["question_count"] = 2; cMisc["pass_percent"] = 100; cMisc["show_correct_answers"] = false;
    Check((await PutCfg(ann, qm, cMisc)).IsSuccessStatusCode, "Quiz-Konfiguration: Umfang 2, Bestehensgrenze 100 %, Lösungen verbergen");
    var miscStart = await QStart(ann, qm, null, 10);
    Check(miscStart.Body!["questions"]!.AsArray().Count == 2 && !miscStart.Body["show_correct_answers"]!.GetValue<bool>(), "Quiz: konfigurierter Umfang begrenzt die Fragenzahl (auch bei größerer Anfrage)");
    var miscRes = await QSubmit(ann, qm, miscStart.Body, QAnswers(miscStart.Body, false));
    var miscText = miscRes.Body!.ToJsonString();
    Check(!miscRes.Body["passed"]!.GetValue<bool>() && miscRes.Body["graded_answers"]!.AsArray().All(g => g!["correct_indexes"]!.AsArray().Count == 0 && string.IsNullOrEmpty(g["explanation"]!.GetValue<string>())),
        "Quiz: ‚Richtige Antworten anzeigen‘ = nein → Ergebnis enthält weder richtige Optionen noch Erklärungen");
    var cScopeCard = baseCfg(); cScopeCard["scope"] = "card"; await PutCfg(ann, qm, cScopeCard);
    Check((await QStart(ann, qm)).Status == HttpStatusCode.Forbidden && (await QStart(ann, qm, qCards[0])).Status == HttpStatusCode.OK, "Quiz-Bereich ‚nur kartenbezogen‘: Modulquiz gesperrt (403), Kartenquiz erlaubt");
    var cScopeMod = baseCfg(); cScopeMod["scope"] = "module"; await PutCfg(ann, qm, cScopeMod);
    Check((await QStart(ann, qm, qCards[0])).Status == HttpStatusCode.Forbidden && (await QStart(ann, qm)).Status == HttpStatusCode.OK, "Quiz-Bereich ‚nur modulweit‘: Kartenquiz gesperrt (403), Modulquiz erlaubt");
    var cOff = baseCfg(); cOff["enabled"] = false; await PutCfg(ann, qm, cOff);
    Check((await QStart(ann, qm)).Status == HttpStatusCode.Forbidden, "Quiz deaktiviert: Start wird abgewiesen (403)");

    // Deaktivieren einzelner Fragen (Ergebnisse bleiben erhalten)
    await PutCfg(ann, qm, baseCfg());
    var histBefore = (await J(await ann.GetAsync($"/api/quiz/history?module_id={qm}")))!.AsArray().Count;
    var mrQuestionId = (await J(await ann.GetAsync($"/api/modules/{qm}/quiz-questions?origin=manual")))!.AsArray().Single(q => q!["type"]!.GetValue<string>() == "multiple_response")!["id"]!.GetValue<string>();
    Check((await ann.PutAsync($"/api/modules/{qm}/quiz-questions/{mrQuestionId}/active", Body(new { active = false }))).StatusCode == HttpStatusCode.NoContent, "Quiz-Pool: Frage deaktivieren");
    var noMr = await QStart(ann, qm);
    Check(noMr.Body!["questions"]!.AsArray().All(q => q!["type"]!.GetValue<string>() != "multiple_response")
          && (await J(await ann.GetAsync($"/api/modules/{qm}/quiz-questions?active=false")))!.AsArray().Count == 1
          && (await J(await ann.GetAsync($"/api/quiz/history?module_id={qm}")))!.AsArray().Count == histBefore, "Quiz-Pool: deaktivierte Frage wird nicht gestellt, Ergebnisse bleiben erhalten, Filter nach Aktivstatus");
    await ann.PutAsync($"/api/modules/{qm}/quiz-questions/{mrQuestionId}/active", Body(new { active = true }));
    Check((await J(await ann.GetAsync($"/api/modules/{qm}/quiz-questions?card_id={qCards[1]}")))!.AsArray().Count == 2, "Quiz-Pool: Filter nach Karte");

    // Quelle der Wahrheit: quiz[] der Karte bestimmt die card_json-Fragen
    var qUpd = await ann.PutAsync($"/api/cards/{qCards[0]}", Body(new { module = "QZ-Engine", category = "K", term = "SC-Karte", question = "Frage SC?", definition = "Definition der SC-Karte", quiz = Array.Empty<object>() }));
    var poolAfterUpd = (await J(await ann.GetAsync($"/api/modules/{qm}/quiz-questions")))!.AsArray();
    Check(qUpd.IsSuccessStatusCode && poolAfterUpd.Count == 2 && poolAfterUpd.All(q => q!["card_id"]!.GetValue<string>() == qCards[1]), "Quiz-Pool: entfällt eine Frage aus quiz[] der Karte, verschwindet sie aus dem Pool (eine Quelle der Wahrheit)");
    var sameAgain = await ann.PutAsync($"/api/cards/{qCards[1]}", Body(new { module = "QZ-Engine", category = "K", term = "MR-Karte", question = "Frage MR?", definition = "Definition der MR-Karte",
        quiz = new object[] { new { type = "multiple_response", question = "Welche sind Primzahlen?", options = new[] { "2", "3", "4", "6" }, correct_indexes = new[] { 0, 1 }, explanation = "2 und 3." } } }));
    Check(sameAgain.IsSuccessStatusCode && (await J(await ann.GetAsync($"/api/modules/{qm}/quiz-questions")))!.AsArray().Count == 1, "Quiz-Pool: unveränderte Fragen bleiben, entfallene werden entfernt, keine Duplikate");

    // Rechte: fremde Benutzer, Freigabeempfänger
    Check((await ben.GetAsync($"/api/modules/{qm}/quiz-config")).StatusCode == HttpStatusCode.NotFound
          && (await PutCfg(ben, qm, baseCfg())).StatusCode == HttpStatusCode.NotFound
          && (await ben.GetAsync($"/api/modules/{qm}/quiz-questions")).StatusCode == HttpStatusCode.NotFound, "Quiz-Rechte: fremder Benutzer sieht weder Konfiguration noch Pool (404)");
    await ann.PostAsync($"/api/sharing/modules/{qm}/shares", Body(new { recipient_user_id = benId }));
    var benCfg = (await J(await ben.GetAsync($"/api/modules/{qm}/quiz-config")))!;
    var poolSizeBefore = (await J(await ann.GetAsync($"/api/modules/{qm}/quiz-questions")))!.AsArray().Count;
    Check(!benCfg["can_edit"]!.GetValue<bool>() && (await PutCfg(ben, qm, baseCfg())).StatusCode == HttpStatusCode.Forbidden
          && (await ben.GetAsync($"/api/modules/{qm}/quiz-questions")).StatusCode == HttpStatusCode.Forbidden
          && (await ben.PutAsync($"/api/modules/{qm}/quiz-questions/{mrQuestionId}/active", Body(new { active = false }))).StatusCode == HttpStatusCode.Forbidden,
        "Quiz-Rechte: Freigabeempfänger darf Konfiguration lesen, aber weder Konfiguration noch Fragenpool ändern oder lesen (403)");
    var benCardStart = await QStart(ben, qm, qCards[1]);
    var benSubmit = benCardStart.Status == HttpStatusCode.OK ? await QSubmit(ben, qm, benCardStart.Body!, QAnswers(benCardStart.Body!, true), qCards[1]) : (benCardStart.Status, (JsonNode?)null);
    Check(benCardStart.Status == HttpStatusCode.OK && benSubmit.Status == HttpStatusCode.OK
          && (await J(await ann.GetAsync($"/api/modules/{qm}/quiz-questions")))!.AsArray().Count == poolSizeBefore
          , "Quiz-Rechte: Freigabeempfänger kann Quizze durchführen; der Pool bleibt unverändert");

    // Globale Version: Konfiguration und Fragen werden eingefroren
    var cGlobal = baseCfg(); cGlobal["pass_percent"] = 80; cGlobal["question_count"] = 3;
    await PutCfg(ann, qm, cGlobal);
    var gSub = (await J(await ann.PostAsync($"/api/sharing/modules/{qm}/submissions", null)))!["id"]!.GetValue<string>();
    Check((await admin.PostAsync($"/api/admin/submissions/{gSub}/approve", null)).StatusCode == HttpStatusCode.NoContent, "Quiz-Global: Modul mit Quizkonfiguration wird veröffentlicht");
    var globalQm = (await J(await ben.GetAsync("/api/modules/?scope=global")))!.AsArray().First(m => m!["name"]!.GetValue<string>() == "QZ-Engine")!["id"]!.GetValue<string>();
    await PutCfg(ann, qm, baseCfg());   // Original ändert sich danach
    var gCfg = (await J(await ben.GetAsync($"/api/modules/{globalQm}/quiz-config")))!;
    Check(gCfg["config"]!["pass_percent"]!.GetValue<int>() == 80 && gCfg["config"]!["question_count"]!.GetValue<int>() == 3 && !gCfg["can_edit"]!.GetValue<bool>() && gCfg["module_version"]!.GetValue<int>() >= 1,
        "Quiz-Global: die eingefrorene Version behält die Konfiguration; spätere Änderungen am Original wirken nicht");
    Check((await PutCfg(ben, globalQm, baseCfg())).StatusCode == HttpStatusCode.Forbidden && (await PutCfg(admin, globalQm, baseCfg())).StatusCode == HttpStatusCode.Forbidden, "Quiz-Global: Lernende und selbst Administratoren können die globale Konfiguration nicht ändern (403)");
    var qGlobalCards = (await J(await ben.GetAsync($"/api/cards/?module_id={globalQm}")))!.AsArray();
    var gCardId = qGlobalCards.First(c => c!["term"]!.GetValue<string>() == "MR-Karte")!["id"]!.GetValue<string>();
    var gStart = await QStart(ben, globalQm, gCardId);
    var gRes = gStart.Status == HttpStatusCode.OK ? await QSubmit(ben, globalQm, gStart.Body!, QAnswers(gStart.Body!, true), gCardId) : (gStart.Status, (JsonNode?)null);
    var gHist = (await J(await ben.GetAsync($"/api/quiz/history?module_id={globalQm}")))!.AsArray();
    Check(gStart.Status == HttpStatusCode.OK && gRes.Status == HttpStatusCode.OK && gHist.Any(h => h!["module_version"] is not null && h["context"]!.GetValue<string>() == "global" && h["pass_percent"]!.GetValue<int>() == 80),
        "Quiz-Global: Quiz auf der globalen Version läuft mit deren Konfiguration; das Ergebnis speichert Modulversion und Bestehensgrenze");

    // Wiederholungen
    var (retryModule, retryCards) = await MakeQuizModule(ann, "QZ-Retry");
    var cRetry = baseCfg(); cRetry["allow_retry"] = false; await PutCfg(ann, retryModule, cRetry);
    var rs1 = await QStart(ann, retryModule);
    await QSubmit(ann, retryModule, rs1.Body!, QAnswers(rs1.Body!, true));
    Check((await QStart(ann, retryModule)).Status == HttpStatusCode.Conflict, "Quiz: Wiederholung nicht erlaubt → zweiter Start wird abgewiesen (409)");

    // ─── Fragen-Schnittstellen für den Editor (Auftrag 2): anlegen, ändern, zuordnen, Rechte ───
    Console.WriteLine("\nQuiz: Fragenpool-Schnittstellen");
    {
        var (em, eCards) = await MakeQuizModule(ann, "QZ-Editor");
        string QPath(string m) => $"/api/modules/{m}/quiz-questions";
        var newQ = new { card_id = eCards[2], type = "single_choice", question = "Editor-Frage?", options = new[] { "w", "x", "y", "z" }, correct_indexes = new[] { 2 }, explanation = "y" };
        var created = await ann.PostAsync(QPath(em), Body(newQ));
        var createdJson = await J(created);
        Check(created.StatusCode == HttpStatusCode.Created && createdJson!["source"]!.GetValue<string>() == "editor" && createdJson["origin"]!.GetValue<string>() == "manual" && createdJson["card_id"]!.GetValue<string>() == eCards[2] && createdJson["active"]!.GetValue<bool>(),
            "Editor-API: Frage anlegen (source=editor, manuell, aktiv, Karte zugeordnet)");
        var editorId = createdJson!["id"]!.GetValue<string>();
        Check((await ann.PostAsync(QPath(em), Body(newQ))).StatusCode == HttpStatusCode.BadRequest, "Editor-API: gleiche Frage wird nicht doppelt angelegt");
        var invalid = new[]
        {
            new { card_id = (string?)null, type = "single_choice", question = "x?", options = new[] { "a", "b" }, correct_indexes = new[] { 5 }, explanation = "e" },
            new { card_id = (string?)null, type = "multiple_response", question = "x?", options = new[] { "a", "b", "c" }, correct_indexes = Array.Empty<int>(), explanation = "e" },
            new { card_id = (string?)null, type = "free_text", question = "x?", options = new[] { "a" }, correct_indexes = Array.Empty<int>(), explanation = "e" },
            new { card_id = (string?)null, type = "essay", question = "x?", options = Array.Empty<string>(), correct_indexes = Array.Empty<int>(), explanation = "e" },
            new { card_id = (string?)null, type = "free_text", question = "", options = Array.Empty<string>(), correct_indexes = Array.Empty<int>(), explanation = "e" },
        };
        var allBad = true;
        foreach (var bad in invalid) allBad &= (await ann.PostAsync(QPath(em), Body(bad))).StatusCode == HttpStatusCode.BadRequest;
        Check(allBad, "Editor-API: ungültige Fragen (Index außerhalb, keine richtige Option, Optionen bei Freitext, unbekannter Typ, leer) → 400");
        var (otherModule, otherCards) = await MakeQuizModule(ann, "QZ-Editor-Fremd");
        Check((await ann.PostAsync(QPath(em), Body(new { card_id = otherCards[0], type = "free_text", question = "Fremde Karte?", options = Array.Empty<string>(), correct_indexes = Array.Empty<int>(), explanation = "e" }))).StatusCode == HttpStatusCode.BadRequest,
            "Editor-API: Karte eines anderen Moduls kann nicht zugeordnet werden");
        var updated = await ann.PutAsync($"{QPath(em)}/{editorId}", Body(new { card_id = eCards[1], type = "multiple_response", question = "Editor-Frage geändert?", options = new[] { "a", "b", "c", "d" }, correct_indexes = new[] { 0, 3 }, explanation = "a und d" }));
        var updatedJson = await J(updated);
        Check(updated.IsSuccessStatusCode && updatedJson!["type"]!.GetValue<string>() == "multiple_response" && updatedJson["card_id"]!.GetValue<string>() == eCards[1] && updatedJson["source"]!.GetValue<string>() == "editor",
            "Editor-API: Frage ändern (Typ, Optionen, Karten-Zuordnung); Quelle bleibt editor");
        var cardJsonQ = (await J(await ann.GetAsync($"{QPath(em)}?card_id={eCards[0]}")))!.AsArray().First()!["id"]!.GetValue<string>();
        var cardJsonUpdate = await ann.PutAsync($"{QPath(em)}/{cardJsonQ}", Body(new { card_id = eCards[0], type = "single_choice", question = "Umgeschrieben?", options = new[] { "a", "b", "c", "d" }, correct_indexes = new[] { 0 }, explanation = "x" }));
        Check(cardJsonUpdate.StatusCode == HttpStatusCode.BadRequest && (await cardJsonUpdate.Content.ReadAsStringAsync()).Contains("Karte"), "Editor-API: Fragen aus quiz[] der Karte lassen sich nur über die Karte ändern (eine Quelle der Wahrheit)");

        // Editor-Fragen überleben das Speichern der Karte; card_json-Fragen folgen quiz[]
        var cardSave = await ann.PutAsync($"/api/cards/{eCards[1]}", Body(new { module = "QZ-Editor", category = "K", term = "MR-Karte", question = "Frage MR?", definition = "Definition der MR-Karte",
            quiz = new object[] { new { type = "free_text", question = "Neu im quiz[]", explanation = "Muster" } } }));
        var poolAfter = (await J(await ann.GetAsync(QPath(em))))!.AsArray();
        Check(cardSave.IsSuccessStatusCode && poolAfter.Any(q => q!["id"]!.GetValue<string>() == editorId) && poolAfter.Count(q => q!["source"]!.GetValue<string>() == "card_json" && q["card_id"]!.GetValue<string>() == eCards[1]) == 1
              && poolAfter.Any(q => q!["question"]!.GetValue<string>() == "Neu im quiz[]"), "Quelle der Wahrheit: Speichern der Karte ändert nur card_json-Fragen, die Editor-Frage bleibt unverändert");
        var edStart = await QStart(ann, em, eCards[1]);
        Check(edStart.Body!["questions"]!.AsArray().Any(q => q!["question"]!.GetValue<string>() == "Editor-Frage geändert?"), "Editor-Frage erscheint im Kartenquiz der zugeordneten Karte");

        // Rechte: Empfänger und fremde Benutzer
        await ann.PostAsync($"/api/sharing/modules/{em}/shares", Body(new { recipient_user_id = benId }));
        Check((await ben.PostAsync(QPath(em), Body(newQ))).StatusCode == HttpStatusCode.Forbidden
              && (await ben.PutAsync($"{QPath(em)}/{editorId}", Body(newQ))).StatusCode == HttpStatusCode.Forbidden, "Editor-API: Freigabeempfänger darf Fragen weder anlegen noch ändern (403)");
        var cora2 = await LoginAsync("quizstranger");
        Check((await cora2.PostAsync(QPath(em), Body(newQ))).StatusCode == HttpStatusCode.NotFound, "Editor-API: fremder Benutzer erhält 404");
        Check((await ann.PostAsync(QPath(Guid.NewGuid().ToString()), Body(newQ))).StatusCode == HttpStatusCode.NotFound && (await ann.PutAsync($"{QPath(em)}/{Guid.NewGuid()}", Body(newQ))).StatusCode == HttpStatusCode.NotFound, "Editor-API: unbekanntes Modul bzw. unbekannte Frage → 404");
    }

    // ─── Beispiele, Templates, Live-Schema, MCP und Hilfeseite bleiben synchron ───
    Console.WriteLine("\nBeispiele, Templates, Schema, MCP, Hilfe");
    {
        var tplRoot = FindRepoRoot();
        var templateDir = Path.Combine(tplRoot, "src", "LearnCards.Web", "wwwroot", "templates");
        var files = Directory.GetFiles(templateDir, "*.json").Concat(Directory.GetFiles(Path.Combine(tplRoot, "demoodule"), "*.json"))
            .Where(f => !File.ReadAllText(f).Contains("\"learny-topic\"") && !File.ReadAllText(f).Contains("\"learny-module\"")).OrderBy(x => x).ToList();   // Modul-/Themenvorlage: eigene Formate (ADR 0028), separat geprüft
        var schemaDoc = (await J(await ann.GetAsync("/api/schema/import/cards")))!;
        {
            // ADR 0028: Modul- und Themenvorlage sowie die Schema-Beispiele sind gültig und importierbar (Modul zuerst, dann das Thema darauf)
            var tpl = await LoginAsync("tpluser");
            var moduleTpl = File.ReadAllText(Path.Combine(templateDir, "module-template.json"));
            var topicTpl = File.ReadAllText(Path.Combine(templateDir, "topic-template.json"));
            var modImp = await tpl.PostAsync("/api/modules/import", new StringContent(moduleTpl, System.Text.Encoding.UTF8, "application/json"));
            var topImp = await tpl.PostAsync("/api/topics/import", new StringContent(topicTpl, System.Text.Encoding.UTF8, "application/json"));
            Check(modImp.StatusCode == HttpStatusCode.Created && topImp.StatusCode == HttpStatusCode.Created, "Vorlagen: module-template.json und topic-template.json sind gültig und importierbar");
            var modSchema = (await J(await tpl.GetAsync("/api/schema/import/module")))!;
            var topSchema = (await J(await tpl.GetAsync("/api/schema/import/topic")))!;
            Check((await tpl.PostAsync("/api/modules/import", new StringContent(modSchema["example"]!.ToJsonString(), System.Text.Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.Created
                  && (await tpl.PostAsync("/api/topics/import", new StringContent(SchemaTopicExample(topSchema), System.Text.Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.Created,
                "Live-Schemas: die Beispiele für Modul-JSON und Themen-JSON sind importierbar");
            var moduleProps = modSchema["module_schema"]!["properties"]!.AsObject().Select(kv => kv.Key).ToHashSet();
            var topicProps = topSchema["topic_schema"]!["properties"]!.AsObject().Select(kv => kv.Key).ToHashSet();
            Check(JsonNode.Parse(moduleTpl)!.AsObject().All(kv => moduleProps.Contains(kv.Key)) && JsonNode.Parse(topicTpl)!.AsObject().All(kv => topicProps.Contains(kv.Key)), "Vorlagen: verwenden nur Felder des Live-Schemas");
        }
        var cardProps = schemaDoc["card_schema"]!["properties"]!.AsObject().Select(kv => kv.Key).ToHashSet();
        var quizSchema = schemaDoc["card_schema"]!["properties"]!["quiz"]!["items"]!;
        var quizProps = quizSchema["properties"]!.AsObject().Select(kv => kv.Key).ToHashSet();
        var schemaTypes = quizSchema["properties"]!["type"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        var exampleQuiz = schemaDoc["example"]!["cards"]![0]!["quiz"]!.AsArray();
        Check(schemaTypes.SequenceEqual(new[] { "single_choice", "multiple_response", "free_text" }) && quizProps.IsSupersetOf(new[] { "type", "question", "options", "correct_index", "correct_indexes", "explanation" })
              && schemaDoc["quiz_rules"]!.AsArray().Count >= 5 && exampleQuiz.Select(q => q!["type"]!.GetValue<string>()).OrderBy(x => x).SequenceEqual(new[] { "free_text", "multiple_response", "single_choice" }),
            "Live-Schema: beschreibt single_choice, multiple_response und free_text (Felder, Regeln, Beispiel mit allen Typen)");
        Check(quizSchema["required"]!.AsArray().Select(x => x!.GetValue<string>()).OrderBy(x => x).SequenceEqual(new[] { "explanation", "question", "type" }), "Live-Schema: Pflichtfelder von quiz[]-Einträgen (type, question, explanation); der Rest ist typabhängig optional");

        var seenTypes = new HashSet<string>();
        var checkedNames = new List<string>();
        var idx = 0;
        foreach (var f in files.Concat(new[] { "<live-schema-example>" }))
        {
            var isExample = f == "<live-schema-example>";
            var name = isExample ? "Live-Schema-Beispiel" : Path.GetRelativePath(tplRoot, f).Replace('\\', '/');
            var doc = isExample ? schemaDoc["example"]!.DeepClone() : JsonNode.Parse(File.ReadAllText(f))!;
            var cards = doc["cards"]!.AsArray();
            var problems = new List<string>();
            foreach (var c in cards)
            {
                foreach (var fk in c!.AsObject().Select(kv => kv.Key)) if (!cardProps.Contains(fk)) problems.Add("unbekanntes Feld " + fk);
                foreach (var q in c["quiz"]?.AsArray() ?? new JsonArray())
                {
                    foreach (var qk in q!.AsObject().Select(kv => kv.Key)) if (!quizProps.Contains(qk)) problems.Add("unbekanntes quiz-Feld " + qk);
                    var type = q["type"]?.GetValue<string>() ?? "single_choice";
                    seenTypes.Add(type);
                    var opts = q["options"]?.AsArray().Count ?? 0;
                    if (!schemaTypes.Contains(type)) problems.Add("Typ " + type);
                    else if (type == "single_choice" && !(opts == 4 && q["correct_index"] is { } ci && ci.GetValue<int>() is >= 0 and < 4 && q["correct_indexes"] is null)) problems.Add("single_choice ungültig: " + q["question"]);
                    else if (type == "multiple_response" && !(opts is >= 3 and <= 6 && q["correct_indexes"] is { } cis && cis.AsArray().Count > 0 && cis.AsArray().Select(x => x!.GetValue<int>()).Distinct().Count() == cis.AsArray().Count && cis.AsArray().All(x => x!.GetValue<int>() >= 0 && x.GetValue<int>() < opts) && q["correct_index"] is null)) problems.Add("multiple_response ungültig: " + q["question"]);
                    else if (type == "free_text" && !(opts == 0 && q["correct_index"] is null && q["correct_indexes"] is null)) problems.Add("free_text ungültig: " + q["question"]);
                    if (string.IsNullOrWhiteSpace(q["explanation"]?.GetValue<string>()) || string.IsNullOrWhiteSpace(q["question"]?.GetValue<string>())) problems.Add("question/explanation fehlt");
                }
                foreach (var tplImg in c["images"]?.AsArray() ?? new JsonArray())
                {
                    var bytes = Convert.FromBase64String(tplImg!["data_base64"]!.GetValue<string>());
                    if (bytes.Length > 2 * 1024 * 1024 || (c["images"]!.AsArray().Count > 5) || !new[] { "image/png", "image/jpeg", "image/webp" }.Contains(tplImg["mime_type"]!.GetValue<string>())) problems.Add("Bildlimit verletzt");
                }
            }
            Check(problems.Count == 0, $"Beispiel {name}: nur Schema-Felder und gültige Quiz-/Bildstruktur" + (problems.Count > 0 ? " — " + string.Join("; ", problems.Take(3)) : ""));

            var importer = await LoginAsync("tplimp" + idx++);
            var res = await importer.PostAsync("/api/import/cards", Body(new { cards = JsonNode.Parse(cards.ToJsonString()), overwrite_existing = false }));
            var resJson = await J(res);
            var stored = (await J(await importer.GetAsync("/api/cards/")))!.AsArray();
            var quizSrc = cards.Sum(c => c!["quiz"]?.AsArray().Count ?? 0);
            var quizStored = stored.Sum(c => c!["quiz"]!.AsArray().Count);
            Check(res.IsSuccessStatusCode && resJson!["errors"]!.AsArray().Count == 0 && resJson["created"]!.GetValue<int>() == cards.Count && stored.Count == cards.Count && quizStored == quizSrc,
                $"Beispiel {name}: Import über /api/import/cards erfolgreich ({cards.Count} Karten, {quizSrc} Quizfragen unverändert gespeichert)");
            checkedNames.Add(name);
        }
        Check(seenTypes.IsSupersetOf(new[] { "single_choice", "multiple_response", "free_text" }), "Beispiele: alle drei Fragetypen kommen in ausgelieferten Beispielen vor");
        Console.WriteLine("  INFO geprüfte Beispiele/Templates: " + string.Join(", ", checkedNames));

        // Altformat ohne quiz-Feld bzw. nur single_choice ohne type
        var oldImp = await LoginAsync("tplold");
        var oldRes = await oldImp.PostAsync("/api/import/cards", Body(new { cards = new object[]
        {
            new { module = "Alt", category = "K", term = "Ohne Quiz", question = "Q?", definition = "D" },
            new { module = "Alt", category = "K", term = "Altes quiz", question = "Q?", definition = "D", quiz = new object[] { new { question = "Frage?", options = new[] { "a", "b", "c", "d" }, correct_index = 3, explanation = "e" } } },
        }, overwrite_existing = false }));
        var oldJson = await J(oldRes);
        Check(oldRes.IsSuccessStatusCode && oldJson!["created"]!.GetValue<int>() == 2 && oldJson["errors"]!.AsArray().Count == 0, "Altformat: Karte ohne quiz und quiz-Eintrag ohne type/correct_indexes bleiben importierbar (type = single_choice)");

        // MCP: Tool-Beschreibungen und Eingabeschema stimmen mit dem Live-Schema überein
        using var listReq = new HttpRequestMessage(HttpMethod.Post, mcpUrl + "/mcp") { Content = Body(new { jsonrpc = "2.0", id = 3, method = "tools/list" }) };
        listReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", benMcp!);
        var tools = (await J(await mcp.SendAsync(listReq)))!["result"]!["tools"]!.AsArray();
        var addCard = tools.First(t => t!["name"]!.GetValue<string>() == "add_card")!;
        var importCards = tools.First(t => t!["name"]!.GetValue<string>() == "import_cards")!;
        var mcpQuiz = addCard["inputSchema"]!["properties"]!["quiz"]!["items"]!;
        var mcpTypes = mcpQuiz["properties"]!["type"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        var mcpQuizProps = mcpQuiz["properties"]!.AsObject().Select(kv => kv.Key).ToHashSet();
        var addProps = addCard["inputSchema"]!["properties"]!.AsObject().Select(kv => kv.Key).ToHashSet();
        Check(mcpTypes.SequenceEqual(schemaTypes) && mcpQuizProps.SetEquals(quizProps) && addProps.IsSubsetOf(cardProps.Append("module").ToHashSet()),
            "MCP: add_card-Eingabeschema (quiz-Typen und -Felder) stimmt mit dem Live-Importschema überein; keine erfundenen Felder");
        Check(new[] { addCard, importCards }.All(t => { var d = t["description"]!.GetValue<string>(); return d.Contains("multiple_response") && d.Contains("free_text") && d.Contains("correct_indexes") && d.Contains("get_import_schema"); }),
            "MCP: Beschreibungen von add_card und import_cards nennen alle Quiz-Fragetypen und verweisen auf get_import_schema");
        var mcpSchemaText = await McpCall(benMcp!, "get_import_schema");
        Check(mcpSchemaText.Contains("multiple_response") && mcpSchemaText.Contains("quiz_rules"), "MCP: get_import_schema liefert das aktuelle Live-Schema inkl. Quizregeln");
        var mcpAddText = await McpCall(benMcp!, "add_card", new { module = "MCP-Quiz", category = "K", term = "MR", question = "Q?", definition = "D",
            quiz = new object[] { new { type = "multiple_response", question = "Welche?", options = new[] { "a", "b", "c" }, correct_indexes = new[] { 0, 2 }, explanation = "a und c" }, new { type = "free_text", question = "Frei?", explanation = "Muster" } } });
        Check(mcpAddText.StartsWith("Card created"), "MCP: add_card nimmt multiple_response und free_text im quiz[] an");
    }

    // ─── Karten-Editor (ADR 0020): Rich-Text, Duplizieren, Sortieren, Archiv, Konflikte, Bilder, Rechte ───
    Console.WriteLine("\nKarten-Editor (ADR 0020)");
    {
        var (em2, ec) = await MakeQuizModule(ann, "ED-Karten");
        async Task<HttpResponseMessage> PutCardFull(HttpClient who, string id, object body, string? ifUpdatedAt = null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/cards/{id}") { Content = Body(body) };
            if (ifUpdatedAt is not null) req.Headers.TryAddWithoutValidation("X-Card-Updated-At", ifUpdatedAt);
            return await who.SendAsync(req);
        }

        // 1) Rich-Text: Grenzen und Speicherung
        Check((await ann.PostAsync("/api/cards/", Body(new { module = "ED-Karten", category = "K", term = "Lang", question = "Q?", definition = new string('x', 20001) }))).StatusCode == HttpStatusCode.BadRequest
              && (await ann.PostAsync("/api/cards/", Body(new { module = "ED-Karten", category = "K", term = "Lang", question = "Q?", definition = "a\u0001b" }))).StatusCode == HttpStatusCode.BadRequest,
            "Rich-Text: zu lange Texte (> 20 000 Zeichen) und Steuerzeichen werden beim Anlegen abgelehnt (400)");
        Check((await PutCardFull(ann, ec[0], new { module = "ED-Karten", category = "K", term = "SC-Karte", question = "Q?", definition = new string('x', 20001) })).StatusCode == HttpStatusCode.BadRequest, "Rich-Text: auch beim Aktualisieren wird validiert (400)");
        var markup = "## Titel\n**fett** und [Link](https://example.org)\n\n- eins\n- zwei";
        var withMarkup = await ann.PostAsync("/api/cards/", Body(new { module = "ED-Karten", category = "K", term = "Markup", question = "Q?", definition = markup, key_fact = "<script>alert(1)</script>" }));
        var markupCard = await J(withMarkup);
        Check(withMarkup.StatusCode == HttpStatusCode.Created && markupCard!["definition"]!.GetValue<string>() == markup && markupCard["key_fact"]!.GetValue<string>() == "<script>alert(1)</script>",
            "Rich-Text: das Markup wird unverändert gespeichert und ausgeliefert (kein HTML-Umbau im Speicher; Ausgabe wird beim Rendern gesichert)");

        // Ausgabe in der Lernansicht (serverseitig gerendert): Angriffe erscheinen nur als Text, Formatierung als erlaubte Elemente
        var xssRes = await ann.PostAsync("/api/cards/", Body(new { module = "ED-XSS", category = "K", term = "XSS", question = "Q?",
            definition = "<script>window.__xss=1</script> <img src=x onerror=alert(1)>\n\n[Klick](javascript:alert(2)) und [ok](https://example.org/a?b=1&c=2) mit **fett**",
            how_it_works = "- <b onclick=alert(3)>x</b>", key_fact = "++unter++ <svg onload=alert(4)>" }));
        var xssModule = (await J(await ann.GetAsync($"/api/cards/{(await J(xssRes))!["id"]!.GetValue<string>()}")))!["module_id"]!.GetValue<string>();
        var xssPage = await (await ann.GetAsync($"/module/{xssModule}")).Content.ReadAsStringAsync();
        Check(xssPage.Contains("&lt;script&gt;window.__xss=1&lt;/script&gt;") && !xssPage.Contains("<script>window.__xss") && !xssPage.Contains("<img src=x") && !xssPage.Contains("<svg onload") && !xssPage.Contains("<b onclick")
              && !xssPage.Contains("href=\"javascript:") && xssPage.Contains("<strong>fett</strong>") && xssPage.Contains("href=\"https://example.org/a?b=1&amp;c=2\"") && xssPage.Contains("<u>unter</u>"),
            "Rich-Text-Sicherheit: die Lernansicht gibt Skripte, Bilder, SVG und Event-Handler nur als Text aus; erlaubte Formatierung und https-Links bleiben");

        // 2) Bilder: behalten, Metadaten ändern, sortieren, entfernen, Limits
        var imgA = Convert.ToBase64String(PngLike(2_000, 41)); var imgB = Convert.ToBase64String(PngLike(3_000, 42));
        object QuizOf(int i) => i == 1 ? new object[] { new { type = "multiple_response", question = "Welche sind Primzahlen?", options = new[] { "2", "3", "4", "6" }, correct_indexes = new[] { 0, 1 }, explanation = "2 und 3." },
                                                       new { type = "free_text", question = "Erkläre den Begriff MR.", explanation = "Muster: MR ist ein Auswahlformat mit mehreren richtigen Antworten." } } : Array.Empty<object>();
        var put2 = await PutCardFull(ann, ec[1], new { module = "ED-Karten", category = "K", term = "MR-Karte", question = "Frage MR?", definition = "Definition der MR-Karte", quiz = QuizOf(1),
            images = new object[] { new { mime_type = "image/png", data_base64 = imgA, alt_text = "A", caption = "a" }, new { mime_type = "image/png", data_base64 = imgB, alt_text = "B", caption = "b" } } });
        var imgs0 = (await J(put2))!["images"]!.AsArray();
        Check(put2.IsSuccessStatusCode && imgs0.Count == 2, "Editor-Bilder: zwei Bilder gespeichert");
        var idA = imgs0[0]!["image_id"]!.GetValue<string>(); var idB = imgs0[1]!["image_id"]!.GetValue<string>();
        var putKeepImages = await PutCardFull(ann, ec[1], new { module = "ED-Karten", category = "K", term = "MR-Karte", question = "Frage MR?", definition = "Definition der MR-Karte (geändert)", quiz = QuizOf(1) });
        Check((await J(putKeepImages))!["images"]!.AsArray().Count == 2, "Editor-Bilder: fehlendes images-Feld behält die vorhandenen Bilder (nichts geht beim Speichern verloren)");
        var putMeta = await PutCardFull(ann, ec[1], new { module = "ED-Karten", category = "K", term = "MR-Karte", question = "Frage MR?", definition = "Definition der MR-Karte", quiz = QuizOf(1),
            images = new object[] { new { image_id = idB, alt_text = "B neu", caption = "b neu" }, new { image_id = idA, alt_text = "A neu", caption = "" } } });
        var imgs1 = (await J(putMeta))!["images"]!.AsArray();
        Check(imgs1.Count == 2 && imgs1[0]!["image_id"]!.GetValue<string>() == idB && imgs1[0]!["alt_text"]!.GetValue<string>() == "B neu" && imgs1[0]["caption"]!.GetValue<string>() == "b neu" && imgs1[1]!["alt_text"]!.GetValue<string>() == "A neu",
            "Editor-Bilder: Alternativtext, Unterschrift und Reihenfolge lassen sich über image_id ändern (Bilddaten bleiben)");
        var bytesB = await (await ann.GetAsync("/api/images/" + idB)).Content.ReadAsByteArrayAsync();
        Check(bytesB.SequenceEqual(PngLike(3_000, 42)), "Editor-Bilder: Auslieferung über /api/images/{id} ist byte-identisch");
        Check((await PutCardFull(ann, ec[1], new { module = "ED-Karten", category = "K", term = "MR-Karte", question = "Frage MR?", definition = "D", quiz = QuizOf(1),
                images = new object[] { new { image_id = (await J(await ann.GetAsync($"/api/cards/{ec[0]}")))!["images"]?.AsArray().FirstOrDefault()?["image_id"]?.GetValue<string>() ?? Guid.NewGuid().ToString() } } })).StatusCode == HttpStatusCode.BadRequest, "Editor-Bilder: eine image_id einer anderen Karte wird abgewiesen (400)");
        var six = Enumerable.Range(0, 6).Select(i => (object)new { mime_type = "image/png", data_base64 = Convert.ToBase64String(PngLike(500, 60 + i)), alt_text = "", caption = "" }).ToArray();
        Check((await PutCardFull(ann, ec[1], new { module = "ED-Karten", category = "K", term = "MR-Karte", question = "Frage MR?", definition = "D", quiz = QuizOf(1), images = six })).StatusCode == HttpStatusCode.BadRequest, "Editor-Bilder: mehr als 5 Bilder werden abgewiesen (400)");

        // 3) Fragenpool: Editor-Frage zur Karte, dann Duplizieren
        var poolQ = await ann.PostAsync($"/api/modules/{em2}/quiz-questions", Body(new { card_id = ec[1], type = "single_choice", question = "Editor-Frage zur MR-Karte?", options = new[] { "a", "b", "c", "d" }, correct_indexes = new[] { 3 }, explanation = "d" }));
        Check(poolQ.StatusCode == HttpStatusCode.Created, "Editor: Fragenpool-Frage der Karte zugeordnet");
        var dup = await ann.PostAsync($"/api/cards/{ec[1]}/duplicate", null);
        var dupJson = await J(dup);
        var dupId = dupJson!["id"]!.GetValue<string>();
        var dupPool = (await J(await ann.GetAsync($"/api/modules/{em2}/quiz-questions?card_id={dupId}")))!.AsArray();
        var srcPool = (await J(await ann.GetAsync($"/api/modules/{em2}/quiz-questions?card_id={ec[1]}")))!.AsArray();
        Check(dup.StatusCode == HttpStatusCode.Created && dupJson["term"]!.GetValue<string>() == "MR-Karte (Kopie)" && !dupJson["archived"]!.GetValue<bool>() && dupJson["quiz"]!.AsArray().Count == 2,
            "Duplizieren: Kopie mit Titelzusatz „(Kopie)“, aktiv, mit quiz[]");
        var dupImgs = dupJson["images"]!.AsArray();
        Check(dupImgs.Count == 2 && dupImgs.All(i => i!["image_id"]!.GetValue<string>() != idA && i["image_id"]!.GetValue<string>() != idB)
              && (await (await ann.GetAsync("/api/images/" + dupImgs[0]!["image_id"]!.GetValue<string>())).Content.ReadAsByteArrayAsync()).SequenceEqual(bytesB), "Duplizieren: Bilder werden mit neuen IDs und gleichen Daten kopiert");
        Check(dupPool.Count == srcPool.Count && srcPool.Count == 3 && dupPool.Count(p => p!["source"]!.GetValue<string>() == "card_json") == 2 && dupPool.Count(p => p!["source"]!.GetValue<string>() == "editor") == 1,
            "Duplizieren: Fragenpool — card_json-Fragen genau einmal (aus dem quiz[] der Kopie), Editor-Frage kopiert, keine Duplikate");
        Check((await ben.PostAsync($"/api/cards/{ec[1]}/duplicate", null)).StatusCode == HttpStatusCode.NotFound, "Duplizieren: fremde Karten lassen sich nicht duplizieren (404)");

        // 4) Sortieren
        var idsK = (await J(await ann.GetAsync($"/api/cards/?module_id={em2}")))!.AsArray().Select(c => c!["id"]!.GetValue<string>()).ToList();
        var reversed = Enumerable.Reverse(idsK).ToList();
        Check((await ann.PutAsync($"/api/modules/{em2}/cards/order", Body(new { card_ids = reversed }))).StatusCode == HttpStatusCode.NoContent
              && (await J(await ann.GetAsync($"/api/cards/?module_id={em2}")))!.AsArray().Select(c => c!["id"]!.GetValue<string>()).SequenceEqual(reversed), "Sortieren: die neue Reihenfolge wird gespeichert und angezeigt");
        Check((await ann.PutAsync($"/api/modules/{em2}/cards/order", Body(new { card_ids = idsK.Append(qCards[0]).ToList() }))).StatusCode == HttpStatusCode.BadRequest
              && (await ann.PutAsync($"/api/modules/{em2}/cards/order", Body(new { card_ids = new[] { idsK[0], idsK[0] } }))).StatusCode == HttpStatusCode.BadRequest, "Sortieren: fremde oder doppelte Karten-IDs werden abgewiesen (400)");
        Check((await ben.PutAsync($"/api/modules/{em2}/cards/order", Body(new { card_ids = idsK }))).StatusCode == HttpStatusCode.NotFound
              && (await ann.PutAsync($"/api/modules/{Guid.NewGuid()}/cards/order", Body(new { card_ids = idsK }))).StatusCode == HttpStatusCode.NotFound, "Sortieren: fremdes bzw. unbekanntes Modul → 404");

        // 5) Archivieren / Wiederherstellen
        Check((await ann.PatchAsync($"/api/cards/{ec[2]}/archive", null)).IsSuccessStatusCode
              && !(await J(await ann.GetAsync($"/api/cards/?module_id={em2}")))!.AsArray().Any(c => c!["id"]!.GetValue<string>() == ec[2])
              && (await J(await ann.GetAsync("/api/cards/?archived=true")))!.AsArray().Any(c => c!["id"]!.GetValue<string>() == ec[2]), "Archivieren: Karte verschwindet aus der aktiven Liste und erscheint im Archiv");
        Check((await ben.PatchAsync($"/api/cards/{ec[2]}/restore", null)).StatusCode == HttpStatusCode.NotFound && (await ann.PatchAsync($"/api/cards/{ec[2]}/restore", null)).IsSuccessStatusCode
              && (await J(await ann.GetAsync($"/api/cards/?module_id={em2}")))!.AsArray().Any(c => c!["id"]!.GetValue<string>() == ec[2]), "Wiederherstellen: nur der Eigentümer; Karte ist wieder aktiv");

        // 6) Gleichzeitige Bearbeitung
        var cur = (await J(await ann.GetAsync($"/api/cards/{ec[0]}")))!;
        var u1 = cur["updated_at"]!.GetValue<string>();
        object Body0(string def) => new { module = "ED-Karten", category = "K", term = "SC-Karte", question = "Frage SC?", definition = def };
        var ok1 = await PutCardFull(ann, ec[0], Body0("Stand 2"), u1);
        var u2 = (await J(ok1))!["updated_at"]!.GetValue<string>();
        var stale = await PutCardFull(ann, ec[0], Body0("Veraltet"), u1);
        var staleJson = await J(stale);
        Check(ok1.IsSuccessStatusCode && stale.StatusCode == HttpStatusCode.Conflict && staleJson!["code"]!.GetValue<string>() == "card_conflict"
              && (await J(await ann.GetAsync($"/api/cards/{ec[0]}")))!["definition"]!.GetValue<string>() == "Stand 2", "Konflikt: ein veralteter Bearbeitungsstand wird erkannt (409) und überschreibt nichts");
        Check((await PutCardFull(ann, ec[0], Body0("Stand 3"), u2)).IsSuccessStatusCode && (await PutCardFull(ann, ec[0], Body0("Stand 4"))).IsSuccessStatusCode, "Konflikt: aktueller Stand wird akzeptiert; ohne Kopfzeile gilt weiter das bisherige Verhalten (Import/REST kompatibel)");

        // 7) Schreibschutz und Besitz
        Check((await PutCardFull(ben, qCards[0], new { module = "QZ-Engine", category = "K", term = "x", question = "Q?", definition = "x" })).StatusCode == HttpStatusCode.NotFound
              && (await ben.PatchAsync($"/api/cards/{qCards[0]}/archive", null)).StatusCode == HttpStatusCode.NotFound
              && (await ben.PostAsync($"/api/cards/{qCards[0]}/duplicate", null)).StatusCode == HttpStatusCode.NotFound
              && (await ben.PutAsync($"/api/modules/{qm}/cards/order", Body(new { card_ids = qCards }))).StatusCode == HttpStatusCode.NotFound,
            "Editor-Rechte: Freigabeempfänger können Karten weder ändern noch archivieren, duplizieren oder sortieren (404)");
        Check((await PutCardFull(admin, gCardId, new { module = "QZ-Engine", category = "K", term = "x", question = "Q?", definition = "x" })).StatusCode == HttpStatusCode.NotFound
              && (await ben.PostAsync($"/api/cards/{gCardId}/duplicate", null)).StatusCode == HttpStatusCode.NotFound
              && (await admin.PutAsync($"/api/modules/{globalQm}/cards/order", Body(new { card_ids = new[] { gCardId } }))).StatusCode == HttpStatusCode.NotFound,
            "Editor-Rechte: globale Modulversionen sind für alle (auch Administratoren) unveränderlich (404)");
        var stranger = await LoginAsync("editorstranger");
        string Text(string html) => html;
        var okPage = Text(await (await ann.GetAsync($"/module/{em2}/edit")).Content.ReadAsStringAsync());
        var shareEdit = Text(await (await ben.GetAsync($"/module/{qm}/edit")).Content.ReadAsStringAsync());
        var strangerEdit = Text(await (await stranger.GetAsync($"/module/{em2}/edit")).Content.ReadAsStringAsync());
        var adminEdit = Text(await (await admin.GetAsync($"/module/{em2}/edit")).Content.ReadAsStringAsync());
        var globalEdit = Text(await (await ben.GetAsync($"/module/{globalQm}/edit")).Content.ReadAsStringAsync());
        Check(okPage.Contains("ED-Karten") && !okPage.Contains("darfst es nicht bearbeiten") && shareEdit.Contains("darfst es nicht bearbeiten") && strangerEdit.Contains("darfst es nicht bearbeiten")
              && adminEdit.Contains("darfst es nicht bearbeiten") && globalEdit.Contains("darfst es nicht bearbeiten"),
            "Editor-Seite: nur der Eigentümer öffnet den Editor; Freigabeempfänger, Fremde, Administratoren (fremde private Module) und globale Module erhalten die Sperrmeldung");
        Check((await NewClient(false).GetAsync($"/module/{em2}/edit")).StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.Unauthorized, "Editor-Seite: ohne Anmeldung wird zur Anmeldung umgeleitet");

        // 8) Vorlage mit Formatierung, Schema, MCP
        var fullTpl = JsonNode.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "LearnCards.Web", "wwwroot", "templates", "full-card-template.json")))!["cards"]![0]!;
        var tplHtml = LearnCards.Web.Domain.RichText.ToHtml(fullTpl["definition"]!.GetValue<string>());
        Check(tplHtml.Contains("<strong>") && tplHtml.Contains("<em>") && tplHtml.Contains("<u>") && tplHtml.Contains("<ul>") && tplHtml.Contains("<ol>") && tplHtml.Contains("<a href=\"https://learn.microsoft.com/\""),
            "Vorlage: full-card-template.json zeigt die Formatierung (fett, kursiv, unterstrichen, Listen, Link) und rendert sicher");
        var schema2 = (await J(await ann.GetAsync("/api/schema/import/cards")))!;
        Check(schema2["rich_text_rules"]!.AsArray().Count >= 6 && schema2["card_schema"]!["properties"]!["definition"]!["description"]!.GetValue<string>().Contains("rich_text_rules"),
            "Live-Schema: rich_text_rules beschreibt die Textsyntax; die Textfelder verweisen darauf");
        using var listReq2 = new HttpRequestMessage(HttpMethod.Post, mcpUrl + "/mcp") { Content = Body(new { jsonrpc = "2.0", id = 4, method = "tools/list" }) };
        listReq2.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", benMcp!);
        var tools2 = (await J(await mcp.SendAsync(listReq2)))!["result"]!["tools"]!.AsArray();
        Check(new[] { "add_card", "import_cards" }.All(n => { var d = tools2.First(t => t!["name"]!.GetValue<string>() == n)!["description"]!.GetValue<string>(); return d.Contains("**bold**") && d.Contains("[text](https://") && d.Contains("never send HTML"); }),
            "MCP: add_card und import_cards beschreiben die Textformatierung (und dass kein HTML gesendet wird)");
    }

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

    // ─── Quiz mit KI (OpenAI-Mock): Generierung, Speicherung, Wiederverwendung, Mischung, Offline-Fallback (ADR 0019) ───
    Console.WriteLine("\nQuiz-Engine mit KI-Mock");
    await using var mockAi = new MockOpenAi();
    var aiRun = await StartWebAsync("e2e-quizai", new() { ["OPENAI_API_KEY"] = "mock-key", ["OPENAI_BASE_URL"] = mockAi.BaseUrl });
    try
    {
        HttpClient AiUser(string name) => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(aiRun.Url), Timeout = TimeSpan.FromSeconds(60) };
        var aiOwner = AiUser("x"); await aiOwner.GetAsync("/auth/login?devUser=aiowner");
        var aiRecv = AiUser("y"); await aiRecv.GetAsync("/auth/login?devUser=airecv");
        var aiRecvId = IdOf(await J(await aiRecv.GetAsync("/api/me")));
        var aiCards = new List<string>();
        for (var i = 1; i <= 3; i++)
        {
            var cr = await aiOwner.PostAsync("/api/cards/", Body(new { module = "AI-Quiz", category = "K", term = $"T{i}", question = $"Q{i}?", definition = $"D{i} Definition mit Inhalt", quiz = Array.Empty<object>() }));
            aiCards.Add((await J(cr))!["id"]!.GetValue<string>());
            await aiOwner.PutAsync($"/api/cards/{aiCards[^1]}/state", Body(new { is_checked = true, marked_review = false }));
        }
        var aiModule = (await J(await aiOwner.GetAsync($"/api/cards/{aiCards[0]}")))!["module_id"]!.GetValue<string>();
        var aiCfg = baseCfg(); aiCfg["question_count"] = 4; aiCfg["shuffle_questions"] = false;
        await PutCfg(aiOwner, aiModule, aiCfg);
        Check((await J(await aiOwner.GetAsync($"/api/modules/{aiModule}/quiz-config")))!["ai_available"]!.GetValue<bool>(), "Quiz-KI: Konfiguration meldet KI als verfügbar");

        async Task<JsonArray> AiPool(string? origin = null) => (await J(await aiOwner.GetAsync($"/api/modules/{aiModule}/quiz-questions" + (origin is null ? "" : "?origin=" + origin))))!.AsArray();
        object[] AiAnswers(JsonNode start, JsonArray pool) => start["questions"]!.AsArray().Select(q =>
        {
            var id = q!["question_id"]!.GetValue<string>();
            var correct = pool.FirstOrDefault(p => p!["id"]!.GetValue<string>() == id)?["correct_indexes"]?.AsArray().Select(x => x!.GetValue<int>()).ToArray() ?? new[] { 0 };
            var free = q["type"]!.GetValue<string>() == "free_text";
            return (object)new { question = q["question"]!.GetValue<string>(), question_id = id, source_card_id = q["source_card_id"]!.GetValue<string>(), user_answer = free ? "meine Antwort" : "", selected_indexes = free ? Array.Empty<int>() : correct };
        }).ToArray();

        // 1) erster Start: nichts gespeichert → generieren, validieren, speichern
        var ai1 = await QStart(aiOwner, aiModule);
        var aiPool1 = await AiPool("ai");
        var aiTypes = aiPool1.Select(p => p!["type"]!.GetValue<string>()).ToHashSet();
        Check(ai1.Status == HttpStatusCode.OK && ai1.Body!["questions"]!.AsArray().Count == 4 && mockAi.GenerateCalls == 1, "Quiz-KI: ohne gespeicherte Fragen wird einmal generiert (4 Fragen)");
        Check(aiPool1.Count == 4 && aiTypes.Contains("single_choice") && aiTypes.Contains("multiple_response") && aiTypes.Contains("free_text") && aiPool1.All(p => p!["source"]!.GetValue<string>() == "ai" && p["origin"]!.GetValue<string>() == "ai"),
            "Quiz-KI: gültige KI-Fragen werden im Pool gespeichert (Herkunft ai); ungültige (unbekannter Typ, fremde Karte, 3 Optionen, Index außerhalb) werden verworfen");
        Check(!ai1.Body.ToJsonString().Contains("Musterlösung") && !ai1.Body.ToJsonString().Contains("b ist richtig"), "Quiz-KI: auch KI-Fragen verraten ihre Lösung nicht vor der Abgabe");

        // 2) Bewertung: Auswahl lokal, Freitext durch die KI
        var aiRes = await QSubmit(aiOwner, aiModule, ai1.Body!, AiAnswers(ai1.Body!, aiPool1));
        var choiceGraded = aiRes.Body!["graded_answers"]!.AsArray().Where(g => g!["type"]!.GetValue<string>() != "free_text").ToList();
        var freeGraded = aiRes.Body["graded_answers"]!.AsArray().Where(g => g!["type"]!.GetValue<string>() == "free_text").ToList();
        Check(aiRes.Status == HttpStatusCode.OK && choiceGraded.All(g => g!["score"]!.GetValue<double>() == 10) && freeGraded.All(g => g!["score"]!.GetValue<double>() == 8 && g["feedback"]!.GetValue<string>() == "mock-bewertung")
              && mockAi.GradeCalls == 1 && aiRes.Body["feedback"]!.GetValue<string>() == "mock-gesamt", "Quiz-KI: Auswahlfragen exakt lokal bewertet, Freitext per KI (ein Aufruf für alle Freitextfragen)");

        // 3) zweiter Start: gespeicherte Fragen werden wiederverwendet, keine Neugenerierung
        var ai2 = await QStart(aiOwner, aiModule);
        Check(ai2.Body!["questions"]!.AsArray().Count == 4 && mockAi.GenerateCalls == 1 && (await AiPool("ai")).Count == 4, "Quiz-KI: gespeicherte KI-Fragen werden wiederverwendet — keine unnötige Neugenerierung");

        // 4) Mischung: gespeicherte und neue Fragen
        var cMix = baseCfg(); cMix["question_count"] = 4; cMix["shuffle_questions"] = false; cMix["strategy"] = "mix"; await PutCfg(aiOwner, aiModule, cMix);
        var ai3 = await QStart(aiOwner, aiModule);
        var poolAfterMix = await AiPool("ai");
        var mixIds = ai3.Body!["questions"]!.AsArray().Select(q => q!["question_id"]!.GetValue<string>()).ToHashSet();
        var storedIdsBefore = aiPool1.Select(p => p!["id"]!.GetValue<string>()).ToHashSet();
        Check(ai3.Body["questions"]!.AsArray().Count == 4 && mockAi.GenerateCalls == 2 && poolAfterMix.Count == 6 && mixIds.Count(storedIdsBefore.Contains) == 2,
            "Quiz-KI: Strategie ‚mischen‘ → 2 gespeicherte + 2 neue Fragen; die neuen werden gespeichert (Pool 6)");

        // 5) keine Generierung
        var cNone = baseCfg(); cNone["strategy"] = "none"; cNone["question_count"] = 4; await PutCfg(aiOwner, aiModule, cNone);
        var ai4 = await QStart(aiOwner, aiModule);
        Check(ai4.Body!["questions"]!.AsArray().Count == 4 && mockAi.GenerateCalls == 2, "Quiz-KI: Strategie ‚keine neuen Fragen‘ → nur Gespeichertes, kein KI-Aufruf");

        // 6) gespeicherte bevorzugen: nichts nachgenerieren, solange es Gespeichertes gibt
        var cPref = baseCfg(); cPref["strategy"] = "prefer_stored"; cPref["question_count"] = 20; await PutCfg(aiOwner, aiModule, cPref);
        var ai5 = await QStart(aiOwner, aiModule, null, 20);
        Check(ai5.Body!["questions"]!.AsArray().Count == 6 && mockAi.GenerateCalls == 2, "Quiz-KI: ‚Gespeicherte bevorzugen‘ → alle 6 gespeicherten Fragen, nichts nachgeneriert");

        // 7) nur feste Fragen: KI-Fragen werden nicht gestellt, nichts generiert, Ableitung aus den Karten bleibt möglich
        var cFixed = baseCfg(); cFixed["origin"] = "fixed"; cFixed["strategy"] = "none"; cFixed["question_count"] = 10; await PutCfg(aiOwner, aiModule, cFixed);
        var ai6 = await QStart(aiOwner, aiModule, null, 10);
        Check(ai6.Body!["questions"]!.AsArray().Count == 3 && ai6.Body["questions"]!.AsArray().All(q => q!["type"]!.GetValue<string>() == "free_text" && q["question"]!.GetValue<string>().StartsWith('Q')) && mockAi.GenerateCalls == 2,
            "Quiz-KI: ‚nur feste Fragen‘ → keine KI-Fragen, kein KI-Aufruf; Fragen werden aus den Karten abgeleitet");

        // 8) nur KI-Fragen
        var cAiOnly = baseCfg(); cAiOnly["origin"] = "ai"; cAiOnly["strategy"] = "none"; cAiOnly["question_count"] = 10; await PutCfg(aiOwner, aiModule, cAiOnly);
        var ai7 = await QStart(aiOwner, aiModule, null, 10);
        Check(ai7.Body!["questions"]!.AsArray().Count == 6 && mockAi.GenerateCalls == 2, "Quiz-KI: ‚nur KI-Fragen‘ → die gespeicherten KI-Fragen");

        // 9) Lernende verändern den Pool nie: Generierung bleibt flüchtig
        var cLearner = baseCfg(); cLearner["question_count"] = 20; await PutCfg(aiOwner, aiModule, cLearner);
        await aiOwner.PostAsync($"/api/sharing/modules/{aiModule}/shares", Body(new { recipient_user_id = aiRecvId }));
        foreach (var cid in aiCards) await aiRecv.PutAsync($"/api/cards/{cid}/state", Body(new { is_checked = true, marked_review = false }));
        var poolBeforeLearner = (await AiPool()).Count;
        var genBeforeLearner = mockAi.GenerateCalls;
        var learner = await QStart(aiRecv, aiModule, null, 20);
        Check(learner.Status == HttpStatusCode.OK && mockAi.GenerateCalls == genBeforeLearner + 1 && learner.Body!["questions"]!.AsArray().Count == 20 && (await AiPool()).Count == poolBeforeLearner,
            "Quiz-KI: Lernende in freigegebenen Modulen erhalten generierte Fragen, der Pool des Eigentümers bleibt unverändert");
        var learnerRes = await QSubmit(aiRecv, aiModule, learner.Body!, AiAnswers(learner.Body!, new JsonArray()));
        Check(learnerRes.Status == HttpStatusCode.OK && learnerRes.Body!["graded_answers"]!.AsArray().Count == 20, "Quiz-KI: flüchtige Fragen werden ebenfalls serverseitig bewertet");

        // 10) KI nicht erreichbar → Fallback ohne Fehler
        mockAi.Mode = "fail";
        var failStart = await QStart(aiOwner, aiModule, null, 20);
        Check(failStart.Status == HttpStatusCode.OK && failStart.Body!["questions"]!.AsArray().Count >= 6 && failStart.Body["notices"]!.AsArray().Any(n => n!.GetValue<string>().Contains("fehlgeschlagen")),
            "Quiz-KI: KI-Fehler bei der Generierung → Hinweis und Weiterarbeit mit vorhandenen Fragen (Offline-Fallback)");
        var failRes = await QSubmit(aiOwner, aiModule, failStart.Body, AiAnswers(failStart.Body, await AiPool()));
        Check(failRes.Status == HttpStatusCode.OK && failRes.Body!["feedback"]!.GetValue<string>().Contains("Heuristik"), "Quiz-KI: KI-Fehler bei der Bewertung → lokale Heuristik statt Abbruch");
        mockAi.Mode = "ok";
    }
    finally { StopProcess(aiRun.Proc); }

    // Altbestand: quiz[] vorhandener Karten wird beim Start in den Fragenpool übernommen (idempotent, quiz_json bleibt erhalten)
    {
        var legRun = await StartWebAsync("e2e-quizlegacy", new());
        string legCard, legModule;
        try
        {
            var lc = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(legRun.Url) };
            await lc.GetAsync("/auth/login?devUser=legacyq");
            var made = await lc.PostAsync("/api/cards/", Body(new { module = "QZ-Altbestand", category = "K", term = "Alt", question = "Q?", definition = "D",
                quiz = new object[] { new { type = "single_choice", question = "Altfrage?", options = new[] { "A", "B", "C", "D" }, correct_index = 2, explanation = "C" } } }));
            legCard = (await J(made))!["id"]!.GetValue<string>();
            legModule = (await J(await lc.GetAsync($"/api/cards/{legCard}")))!["module_id"]!.GetValue<string>();
        }
        finally { StopProcess(legRun.Proc); }
        await Task.Delay(500);
        await using (var ldb = new LearnCards.Web.Data.SqliteDatabase(Path.Combine(tmp, "e2e-quizlegacy", "e2e.db")))
        {
            await ldb.InitAsync();
            await ldb.ExecuteAsync("DELETE FROM quiz_questions", null);          // Zustand vor dem Update simulieren: Pool leer, Karte nicht übernommen
            await ldb.ExecuteAsync("UPDATE cards SET quiz_synced = 0", null);
        }
        for (var round = 1; round <= 2; round++)
        {
            var again = await StartWebAsync("e2e-quizlegacy", new());
            try
            {
                var lc2 = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(again.Url) };
                await lc2.GetAsync("/auth/login?devUser=legacyq");
                var poolLeg = (await J(await lc2.GetAsync($"/api/modules/{legModule}/quiz-questions")))!.AsArray();
                var cardLeg = (await J(await lc2.GetAsync($"/api/cards/{legCard}")))!;
                Check(poolLeg.Count == 1 && poolLeg[0]!["correct_indexes"]![0]!.GetValue<int>() == 2 && cardLeg["quiz"]!.AsArray().Count == 1,
                    round == 1 ? "Quiz-Migration: vorhandenes quiz[] wird beim Start in den Pool übernommen, die Karte behält ihr quiz[]" : "Quiz-Migration: ein weiterer Start ist idempotent (keine Duplikate)");
            }
            finally { StopProcess(again.Proc); }
            await Task.Delay(300);
        }
    }

    // ─── Quizsitzungen: Ablauf (4 h) und Bereinigung (24 h); Ergebnisse bleiben ───
    Console.WriteLine("\nQuiz: Sitzungsablauf und Bereinigung");
    {
        var sessRun = await StartWebAsync("e2e-quizsess", new());
        HttpClient SessClient(string url) => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(60) };
        string sModule, sCard, sExpired, sActive, sOld;
        int resultsBefore;
        try
        {
            var sc = SessClient(sessRun.Url);
            await sc.GetAsync("/auth/login?devUser=sessuser");
            (sModule, var sCards) = await MakeQuizModule(sc, "QZ-Sitzung");
            sCard = sCards[0];
            var done = await QStart(sc, sModule, sCard);
            await QSubmit(sc, sModule, done.Body!, QAnswers(done.Body!, true), sCard);   // ein gespeichertes Ergebnis
            resultsBefore = (await J(await sc.GetAsync($"/api/quiz/history?module_id={sModule}")))!.AsArray().Count;
            sExpired = (await QStart(sc, sModule, sCard)).Body!["session_id"]!.GetValue<string>();
            sActive = (await QStart(sc, sModule, sCard)).Body!["session_id"]!.GetValue<string>();
            sOld = (await QStart(sc, sModule, sCard)).Body!["session_id"]!.GetValue<string>();
        }
        finally { StopProcess(sessRun.Proc); }
        await Task.Delay(500);
        await using (var sdb = new LearnCards.Web.Data.SqliteDatabase(Path.Combine(tmp, "e2e-quizsess", "e2e.db")))
        {
            await sdb.InitAsync();
            await sdb.ExecuteAsync("UPDATE quiz_sessions SET created_at = @c WHERE id = @id", new (string, object?)[] { ("c", DateTime.UtcNow.AddHours(-5)), ("id", sExpired) });   // abgelaufen (> 4 h), noch aufbewahrt
            await sdb.ExecuteAsync("UPDATE quiz_sessions SET created_at = @c WHERE id = @id", new (string, object?)[] { ("c", DateTime.UtcNow.AddDays(-3)), ("id", sOld) });      // älter als 24 h → wird beim Start entfernt
        }
        var sessRun2 = await StartWebAsync("e2e-quizsess", new());
        try
        {
            var sc2 = SessClient(sessRun2.Url);
            await sc2.GetAsync("/auth/login?devUser=sessuser");
            async Task<HttpResponseMessage> SubmitById(string sid) => await sc2.PostAsync("/api/quiz/submit", Body(new { module_id = sModule, category = "", card_id = sCard, session_id = sid,
                answers = new object[] { new { question = "Welche Antwort stimmt?", question_id = "", source_card_id = sCard, user_answer = "", selected_indexes = new[] { 1 } } } }));
            Check((await SubmitById(sExpired)).StatusCode == HttpStatusCode.Gone, "Quizsitzung: abgelaufene Sitzung (> 4 h) wird abgelehnt (410)");
            Check((await SubmitById(sOld)).StatusCode == HttpStatusCode.NotFound, "Quizsitzung: Sitzungen älter als 24 h wurden beim Start bereinigt");
            Check((await J(await sc2.GetAsync($"/api/quiz/history?module_id={sModule}")))!.AsArray().Count == resultsBefore, "Quizsitzung: gespeicherte Ergebnisse bleiben bei Ablauf und Bereinigung erhalten");
            var activeRes = await SubmitById(sActive);
            Check(activeRes.IsSuccessStatusCode, "Quizsitzung: gültige Sitzung funktioniert nach dem Neustart weiter");
            Check((await J(await sc2.GetAsync($"/api/quiz/history?module_id={sModule}")))!.AsArray().Count == resultsBefore + 1, "Quizsitzung: Ergebnis der aktiven Sitzung wird gespeichert");
        }
        finally { StopProcess(sessRun2.Proc); }
    }

    // ─── Hilfeseite ↔ Templates (echte Dateien, Download über die Anwendung) ───
    Console.WriteLine("\nHilfeseite und Template-Downloads");
    {
        var contentRoot = Path.Combine(FindRepoRoot(), "src", "LearnCards.Web");
        var statRun = await StartWebAsync("e2e-static", new() { ["ASPNETCORE_CONTENTROOT"] = contentRoot });
        try
        {
            var hc = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(statRun.Url), Timeout = TimeSpan.FromSeconds(60) };
            await hc.GetAsync("/auth/login?devUser=helpreader");
            var help = await (await hc.GetAsync("/help")).Content.ReadAsStringAsync();
            var linked = System.Text.RegularExpressions.Regex.Matches(help, @"/templates/([\w\-]+\.json)").Select(m => m.Groups[1].Value).ToHashSet();
            var onDisk = Directory.GetFiles(Path.Combine(contentRoot, "wwwroot", "templates"), "*.json").Select(Path.GetFileName).ToHashSet();
            Check(linked.Count > 0 && linked.SetEquals(onDisk!), $"Hilfeseite: verlinkt genau die ausgelieferten Templates ({string.Join(", ", linked.OrderBy(x => x))})");
            var allServed = true;
            foreach (var name in linked)
            {
                var res = await hc.GetAsync("/templates/" + name);
                allServed &= res.IsSuccessStatusCode && JsonNode.DeepEquals(JsonNode.Parse(await res.Content.ReadAsStringAsync()), JsonNode.Parse(File.ReadAllText(Path.Combine(contentRoot, "wwwroot", "templates", name))));
            }
            Check(allServed, "Hilfeseite: alle Template-Downloads funktionieren und liefern den Dateiinhalt");
            var helpTerms = new[] { "single_choice", "multiple_response", "free_text", "correct_indexes", "Quiz-Einstellungen", "Bestehensgrenze", "/api/schema/import/cards", "KI-Fragen", "Lernkarten visuell bearbeiten", "Karten bearbeiten", "Alternativtext", "Fragenpool", "++unterstrichen++", "Konflikt", "ungespeicherte Änderungen" };
            var missing = helpTerms.Where(t => !help.Contains(t)).ToList();
            Check(missing.Count == 0, "Hilfeseite: erklärt Fragetypen, Quiz-Einstellungen, KI-Fragen und verweist auf das Live-Importschema" + (missing.Count > 0 ? " — fehlt: " + string.Join(", ", missing) : ""));
            Check(!help.Contains("WYSIWYG"), "Hilfeseite: dokumentiert keine nicht vorhandenen Funktionen (WYSIWYG)");
            var certTerms = new[] { "Learny Verified", "Modulversionen", "Lernziele", "Kompetenzen", "signierte PDF", "/verify", "/achievements", "LinkedIn", "Neuen Stand übernehmen", "CLR" };
            var certMissing = certTerms.Where(t => !help.Contains(t)).ToList();
            Check(certMissing.Count == 0, "Hilfeseite: erklärt Modulversionen, Kompetenzen, Zertifikate, signierte PDFs, Prüfung und LinkedIn" + (certMissing.Count > 0 ? " — fehlt: " + string.Join(", ", certMissing) : ""));
        }
        finally { StopProcess(statRun.Proc); }
    }

    // zuletzt: veröffentlicht ein globales Modul, das die früheren Prüfungen (genau ein globales Modul) nicht stören darf
    await TopicTransferTests.RunAsync(LoginAsync, () => NewClient(false), admin, mcpUrl, Check);   // Themenmodule, JSON, Klonen, MCP (ADR 0028)
    await TransferHardeningTests.RunAsync(LoginAsync, () => NewClient(false), mcpUrl, McpKey, PngLike, Check);   // Referenzen parallel, große Dateien, MCP-Matrix (ADR 0028)
    await TopicViewTests.RunAsync(LoginAsync, Check);   // Themenseite: Entwurf nur bei Abweichung
    await CompetencyTests.RunAsync(LoginAsync, () => NewClient(false), Check);   // Kompetenzen, Prüfungssnapshot, Badge (ADR 0031)
    await TopicCredentialTests.RunAsync(LoginAsync, () => NewClient(false), admin, Check);   // Themenabschluss: Master-Badge + CLR (ADR 0032)
    await ModuleVersionTests.RunAsync(LoginAsync, Check);   // Modulversionen, feste Modulversionen im Thema (ADR 0030)
    await CardToolsTests.RunAsync(LoginAsync, () => NewClient(false), admin, mcpUrl, McpKey, PngLike, Check);   // get_card, update_card, export_cards
    await TopicGovernanceTests.RunAsync(LoginAsync, () => NewClient(false), admin, mcpUrl, Check);   // Themenfreigaben, Pflicht, Themen-Admin, Lernstatus (ADR 0029)
    await PkiTests.RunAsync(LoginAsync, () => NewClient(false), admin, baseUrl, pkiKeyDir, PkiKeyPassword, PkiRootPassphrase, StartWebAsync, StopProcess, () => { lock (log) return log.ToString(); }, Check);   // Learny-PKI (ADR 0035)

    // Migration modules.reference_id: Altbestand ohne Spalte wird beim Start ergänzt (idempotent), Daten bleiben erhalten, Referenzen sind danach stabil
    Console.WriteLine("\nMigration modules.reference_id (ADR 0028)");
    {
        string refModule = "", firstRef = "";
        var first = await StartWebAsync("e2e-refmig", new());
        try
        {
            var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(first.Url) };
            await c.GetAsync("/auth/login?devUser=refmig");
            var imp = await c.PostAsync("/api/modules/import", new StringContent("{\"format\":\"learny-module\",\"schema_version\":1,\"reference_id\":\"" + Guid.NewGuid() + "\",\"name\":\"RM\",\"cards\":[{\"category\":\"K\",\"term\":\"T\",\"question\":\"Q?\",\"definition\":\"D\"}]}", System.Text.Encoding.UTF8, "application/json"));
            refModule = (await J(imp))!["id"]!.GetValue<string>();
            firstRef = (await J(await c.GetAsync($"/api/modules/{refModule}/export")))!["reference_id"]!.GetValue<string>();
        }
        finally { StopProcess(first.Proc); }
        await Task.Delay(500);
        await using (var mdb = new LearnCards.Web.Data.SqliteDatabase(Path.Combine(tmp, "e2e-refmig", "e2e.db")))
        {
            await mdb.InitAsync();
            await mdb.ExecuteAsync("DROP INDEX IF EXISTS ux_modules_owner_reference", null);
            await mdb.ExecuteAsync("ALTER TABLE modules DROP COLUMN reference_id", null);   // Zustand vor ADR 0028
            foreach (var col in new[] { "topic_version", "mandatory", "due_at" }) await mdb.ExecuteAsync($"ALTER TABLE topic_assignments DROP COLUMN {col}", null);   // Zustand vor ADR 0029
        }
        var seen = new List<string>();
        for (var round = 1; round <= 2; round++)
        {
            var again = await StartWebAsync("e2e-refmig", new() { ["ADMIN_SUBJECTS"] = "dev-refmig" });
            try
            {
                var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(again.Url) };
                await c.GetAsync("/auth/login?devUser=refmig");
                var mods = (await J(await c.GetAsync("/api/modules/")))!.AsArray();
                var cards = (await J(await c.GetAsync($"/api/cards/?module_id={refModule}")))!.AsArray();
                var rf1 = (await J(await c.GetAsync($"/api/modules/{refModule}/export")))!["reference_id"]!.GetValue<string>();
                var rf2 = (await J(await c.GetAsync($"/api/modules/{refModule}/export")))!["reference_id"]!.GetValue<string>();
                // ADR 0029: Altbestand ohne die neuen Spalten von topic_assignments — der Start ergänzt sie, Zuweisungen funktionieren (Version, Pflicht, Fälligkeit)
                if (round == 1)
                {
                    var def = new JsonObject { ["format"] = "learny-topic", ["schema_version"] = 1, ["name"] = "RM-Thema", ["modules"] = new JsonArray(new JsonObject { ["module_name"] = "RM" }) };
                    var topicRes = await c.PostAsync("/api/topics/import", new StringContent(def.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
                    var topicIdRm = (await J(topicRes))!["id"]!.GetValue<string>();
                    await c.PostAsync($"/api/topics/{topicIdRm}/versions", Body(new { }));
                    var myId = (await J(await c.GetAsync("/api/me")))!["id"]!.GetValue<string>();
                    var asg = await c.PostAsync("/api/topic-admin/assignments", Body(new { topic_id = topicIdRm, user_id = myId, mandatory = true, due_at = DateTime.UtcNow.AddDays(5) }));
                    Check(asg.StatusCode == HttpStatusCode.Created, "Migration (ADR 0029): nach dem Ergänzen der Spalten (topic_version, mandatory, due_at) lassen sich Themen zuweisen");
                }
                var mandatoryNow = (await J(await c.GetAsync("/api/topics/")))!["mandatory"]!.AsArray().Count;
                Check(mandatoryNow == 1, round == 1 ? "Migration (ADR 0029): die Pflichtzuweisung erscheint" : "Migration (ADR 0029): ein weiterer Start ist idempotent, die Pflichtzuweisung bleibt erhalten");
                seen.Add(rf1);
                Check(mods.Count == 1 && cards.Count == 1 && Guid.TryParse(rf1, out _) && rf1 == rf2, round == 1 ? "Migration: fehlende Spalte wird beim Start ergänzt, Module und Karten bleiben erhalten, erster Export vergibt eine GUID" : "Migration: ein weiterer Start ist idempotent, die Referenz bleibt stabil");
            }
            finally { StopProcess(again.Proc); }
            await Task.Delay(300);
        }
        Check(seen.Distinct().Count() == 1 && seen[0] != firstRef, "Migration: die Referenz überlebt Neustarts (und ist nach dem Zurücksetzen der Spalte neu vergeben)");
        await using (var mdb2 = new LearnCards.Web.Data.SqliteDatabase(Path.Combine(tmp, "e2e-refmig", "e2e.db")))
        {
            await mdb2.InitAsync();
            var dupFailed = false;
            await mdb2.ExecuteAsync("INSERT INTO modules (id, name, description, icon, color, owner_user_id, created_at, updated_at, reference_id) SELECT @id, 'RM-Dup', '', 'x', '#000', owner_user_id, created_at, updated_at, @r FROM modules WHERE id = @m", new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("r", Guid.NewGuid().ToString()), ("m", refModule) });
            try { await mdb2.ExecuteAsync("INSERT INTO modules (id, name, description, icon, color, owner_user_id, created_at, updated_at, reference_id) SELECT @id, 'RM-Dup2', '', 'x', '#000', owner_user_id, created_at, updated_at, @r FROM modules WHERE id = @m", new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("r", seen[0]), ("m", refModule) }); } catch (Exception) { dupFailed = true; }
            Check(dupFailed, "Migration: der Unique-Index (Eigentümer, Referenz) besteht nach der Migration");
        }
    }

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
