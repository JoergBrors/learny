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

try
{
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
