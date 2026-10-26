using System.Net;
using System.Text.Json.Nodes;

/// <summary>ADR 0034: Nachweisübersicht „Meine Abschlüsse und Nachweise“, öffentliche Prüfseite mit freigegebenen Kompetenzen, LinkedIn-Angaben, Prüfsiegel (SVG/PNG, Hell/Dunkel), Favicon/Manifest.</summary>
public static class AchievementTests
{
    private static string S(JsonNode? n, string path)
    {
        var cur = n;
        foreach (var part in path.Split('.')) cur = cur is JsonArray a ? (int.TryParse(part, out var i) && i < a.Count ? a[i] : null) : (cur as JsonObject)?[part];
        return cur?.ToString() ?? "";
    }

    public static async Task RunAsync(HttpClient lea, HttpClient other, Func<HttpClient> newClient, string topicCertId, string moduleCertId, Action<bool, string> check)
    {
        Console.WriteLine("\nNachweisübersicht, öffentliche Prüfseite, LinkedIn und Prüfsiegel (ADR 0034)");
        var anon = newClient();
        // Prüfsiegel und Icons
        var assets = new[] { "learny-verified-mark.svg", "learny-verified-mark-dark.svg", "learny-verified.svg", "learny-verified-dark.svg", "learny-verified-mark-64.png", "learny-verified-mark-192.png", "learny-verified-mark-512.png", "favicon-32.png", "apple-touch-icon.png", "learny-verified.png", "learny-verified-dark.png", "learny-verified-share.png" };
        // Statische Dateien: Das Testsystem läuft als Produktionsumgebung aus dem Build-Ordner (ohne ausgeliefertes wwwroot), daher werden die Dateien selbst und die
        // Endpunkt-Tabelle des Builds (Routen und Content-Type) geprüft; die Einbindung in die Seiten prüfen die HTML-Antworten unten.
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LearnCards.slnx"))) root = root.Parent;
        var wwwroot = Path.Combine(root!.FullName, "src", "LearnCards.Web", "wwwroot");
        var webDll = Environment.GetEnvironmentVariable("TEST_WEB_DLL") ?? Path.Combine(root.FullName, "src", "LearnCards.Web", "bin", "Debug", "net10.0", "LearnCards.Web.dll");
        var endpoints = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(webDll, null) + ".staticwebassets.endpoints.json"))!["Endpoints"]!.AsArray();
        string TypeOf(string route) => endpoints.Where(e => S(e, "Route") == route).SelectMany(e => e!["ResponseHeaders"]!.AsArray()).Where(h => S(h, "Name") == "Content-Type").Select(h => S(h, "Value")).FirstOrDefault() ?? "";
        var ok = true;
        foreach (var a in assets)
        {
            var file = Path.Combine(wwwroot, "brand", a);
            if (!File.Exists(file) || new FileInfo(file).Length < 200) { ok = false; continue; }
            var head = File.ReadAllBytes(file);
            if (a.EndsWith(".png") && !(head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)) ok = false;
            if (a.EndsWith(".svg") && !System.Text.Encoding.UTF8.GetString(head).Contains("<svg")) ok = false;
            if (TypeOf("brand/" + a) != (a.EndsWith(".svg") ? "image/svg+xml" : "image/png")) ok = false;
        }
        var svg = File.ReadAllText(Path.Combine(wwwroot, "brand", "learny-verified.svg"));
        var svgDark = svg.Contains("prefers-color-scheme: dark") && svg.Contains("Learny Verified") && svg.Contains("Digitally Signed Credential");
        var manifest = File.ReadAllText(Path.Combine(wwwroot, "manifest.webmanifest")); var manifestType = TypeOf("manifest.webmanifest");
        var home = await (await lea.GetAsync("/")).Content.ReadAsStringAsync();
        check(ok && svgDark && manifestType == "application/manifest+json" && manifest.Contains("learny-verified-mark-512.png") && home.Contains("/brand/learny-verified-mark.svg") && home.Contains("rel=\"icon\"") && home.Contains("apple-touch-icon") && home.Contains("/manifest.webmanifest") && home.Contains("seal-link"),
            "L1 Prüfsiegel „Learny Verified“ als SVG (Hell/Dunkel) und PNG, Favicon, Apple-Touch-Icon, Manifest und Seitenkopf sind eingebunden" + $" [assets={ok} svg={svgDark} manifest={manifest.Contains("learny-verified-mark-512.png")} mark={home.Contains("/brand/learny-verified-mark.svg")} icon={home.Contains("rel=\"icon\"")} apple={home.Contains("apple-touch-icon")} man={home.Contains("/manifest.webmanifest")} seal={home.Contains("seal-link")}]");

        // Nachweisübersicht
        var page = await (await lea.GetAsync("/achievements")).Content.ReadAsStringAsync();
        check(page.Contains("Meine Abschlüsse und Nachweise") && page.Contains("TC Thema") && page.Contains("Themenabschluss") && page.Contains("Master-Badge") && page.Contains($"/api/certificates/{topicCertId}/certificate.pdf") && page.Contains($"/api/certificates/{topicCertId}/clr")
              && page.Contains($"/api/certificates/{moduleCertId}/certificate.pdf") && page.Contains("Auf LinkedIn hinzufügen") && page.Contains("Zertifikat prüfen") && page.Contains("Learny Verified") && page.Contains("Grundlagen anwenden"),
            "L2 „Meine Abschlüsse und Nachweise“ zeigt Modul- und Themenabschlüsse mit Versionen, Nummern, Kompetenzen und den Aktionen PDF, CLR, Open Badge, Prüfen und LinkedIn");
        var otherPage = await (await other.GetAsync("/achievements")).Content.ReadAsStringAsync();
        check(!otherPage.Contains(topicCertId) && !otherPage.Contains("TC Thema") && otherPage.Contains("Noch keine Abschlüsse"), "L3 jeder Benutzer sieht nur seine eigenen Abschlüsse");
        var anonPage = await anon.GetAsync("/achievements");
        check(anonPage.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || !(await anonPage.Content.ReadAsStringAsync()).Contains("TC Thema"), "L4 ohne Anmeldung gibt es keine Nachweisübersicht");

        // Öffentliche Prüfseite: freigegebene Kompetenzen, keine Personendaten
        var verify = await (await anon.GetAsync($"/verify/{topicCertId}")).Content.ReadAsStringAsync();
        var verifyMod = await (await anon.GetAsync($"/verify/{moduleCertId}")).Content.ReadAsStringAsync();
        check(verify.Contains("Nachweise und Kompetenzen") && verify.Contains("TC-A") && verify.Contains("Grundlagen anwenden") && verify.Contains("Themenabschluss") && !verify.Contains("tcr-lea", StringComparison.OrdinalIgnoreCase)
              && verifyMod.Contains("Nachweise und Kompetenzen") && !verifyMod.Contains("tcr-lea", StringComparison.OrdinalIgnoreCase) && verify.Contains("Learny Verified"),
            "L5 die Verifikationsadresse zeigt den passenden Abschluss mit den freigegebenen Kompetenzen und Modulen — ohne Lernendenname");

        // LinkedIn
        var li = JsonNode.Parse(await (await lea.GetAsync($"/api/certificates/{topicCertId}/linkedin")).Content.ReadAsStringAsync());
        var addUrl = S(li, "add_url");
        check(S(li, "automatic_publication") == "false" && addUrl.StartsWith("https://www.linkedin.com/profile/add?startTask=CERTIFICATION_NAME") && addUrl.Contains("name=TC%20Thema") && addUrl.Contains(Uri.EscapeDataString(S(li, "certificate_url")))
              && S(li, "certificate_url").EndsWith("/verify/" + topicCertId) && S(li, "organization").Contains("Learny") && S(li, "certificate_id").StartsWith("LC-") && S(li, "issue_year").Length == 4,
            "L6 „Auf LinkedIn hinzufügen“: Name, Aussteller Learny, Zertifikats-ID, Ausstellungsdatum und Verifikationsadresse des passenden Abschlusses; keine automatische Veröffentlichung");
        check((await other.GetAsync($"/api/certificates/{topicCertId}/linkedin")).StatusCode == HttpStatusCode.NotFound && (await anon.GetAsync($"/api/certificates/{topicCertId}/linkedin")).StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Redirect,
            "L7 LinkedIn-Angaben nur für den Eigentümer");
        var pageLi = await (await lea.GetAsync("/achievements")).Content.ReadAsStringAsync();
        check(!pageLi.Contains("linkedin.com/profile/add") || pageLi.Contains("rel=\"noopener noreferrer\""), "L8 externe LinkedIn-Adresse nur mit noopener/noreferrer und nur nach ausdrücklicher Aktion");
    }
}
