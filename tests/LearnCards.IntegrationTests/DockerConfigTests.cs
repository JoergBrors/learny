using System.Text.RegularExpressions;

/// <summary>
/// Konsistenz der Docker-Hub-Veröffentlichung (ADR 0024): Compose-Datei, .env.example, Dokumentation und Code sprechen dieselben Variablennamen;
/// keine Geheimnisse in den Beispielen; Workflow nutzt nur gepinnte Actions und Secrets-Referenzen.
/// </summary>
public static class DockerConfigTests
{
    // Variablen, die nur die Compose-Datei selbst auswertet (nicht an die Anwendung gehen)
    private static readonly HashSet<string> ComposeOnly = new()
    {
        "LEARNY_VERSION", "COMPOSE_PROFILES", "LEARNY_BIND_ADDRESS", "LEARNY_WEB_PORT", "LEARNY_MCP_PORT", "LEARNY_HOST", "MCP_HOST",
    };

    // Vom Framework bzw. Image (nicht vom LearnCards-Code) gelesen
    private static readonly HashSet<string> FrameworkVars = new()
    {
        "ASPNETCORE_ENVIRONMENT", "ASPNETCORE_URLS", "ASPNETCORE_FORWARDEDHEADERS_ENABLED",
    };

    public static void Run(Action<bool, string> check, string repoRoot)
    {
        var dir = Path.Combine(repoRoot, "deploy", "docker");
        var compose = File.ReadAllText(Path.Combine(dir, "docker-compose.yml"));
        var envExample = File.ReadAllText(Path.Combine(dir, ".env.example"));
        var readme = File.ReadAllText(Path.Combine(dir, "README.md"));
        var workflow = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "publish-docker.yml"));

        var names = Regex.Matches(envExample, @"^([A-Z][A-Z0-9_]*)=(.*)$", RegexOptions.Multiline).Select(m => (Name: m.Groups[1].Value, Value: m.Groups[2].Value.Trim())).ToList();
        check(names.Count > 50, $"Docker: .env.example enthält die Konfiguration ({names.Count} Variablen)");

        var code = string.Join("\n", new[] { "LearnCards.Web", "LearnCards.McpServer" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(repoRoot, "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Select(File.ReadAllText));

        // COMPOSE_PROFILES wertet Docker Compose selbst aus (kein ${…} in der Datei nötig)
        var unusedInCompose = names.Where(n => n.Name != "COMPOSE_PROFILES" && !compose.Contains("${" + n.Name)).Select(n => n.Name).ToList();
        check(unusedInCompose.Count == 0, "Docker: jede Variable der .env.example wird in docker-compose.yml verwendet" + (unusedInCompose.Count > 0 ? " — " + string.Join(", ", unusedInCompose) : ""));
        check(names.All(n => readme.Contains("| `" + n.Name + "` |")), "Docker: jede Variable der .env.example steht in der Variablentabelle der README (" + string.Join(", ", names.Where(n => !readme.Contains("| `" + n.Name + "` |")).Select(n => n.Name)) + ")");

        // Container-Variablen des Web-Dienstes müssen im Code existieren (keine erfundenen Namen)
        var web = Section(compose, "  web:", "  mcp:");
        var webVars = Regex.Matches(web, @"^      ([A-Z][A-Z0-9_]*):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Where(v => !FrameworkVars.Contains(v)).ToList();
        var unknownWeb = webVars.Where(v => !code.Contains("\"" + v + "\"")).ToList();
        check(webVars.Count > 40 && unknownWeb.Count == 0, "Docker: alle Umgebungsvariablen des Web-Containers werden vom Code gelesen" + (unknownWeb.Count > 0 ? " — unbekannt: " + string.Join(", ", unknownWeb) : ""));

        var mcp = Section(compose, "  mcp:", "  proxy:");
        var mcpVars = Regex.Matches(mcp, @"^      (LEARNCARDS_[A-Z0-9_]*):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        var unknownMcp = mcpVars.Where(v => !code.Contains("\"" + v + "\"")).ToList();
        check(mcpVars.Count >= 6 && unknownMcp.Count == 0, "Docker: alle Umgebungsvariablen des MCP-Containers werden vom MCP-Server gelesen" + (unknownMcp.Count > 0 ? " — unbekannt: " + string.Join(", ", unknownMcp) : ""));

        var appNames = names.Select(n => n.Name).Where(n => !ComposeOnly.Contains(n) && !FrameworkVars.Contains(n) && n != "LEARNCARDS_WEB_PUBLIC_URL").ToList();
        var unknownEnv = appNames.Where(n => !code.Contains("\"" + n + "\"")).ToList();
        check(unknownEnv.Count == 0, "Docker: jede anwendungsrelevante Variable der .env.example existiert im Code" + (unknownEnv.Count > 0 ? " — unbekannt: " + string.Join(", ", unknownEnv) : ""));

        // Keine Geheimnisse in den Beispielen
        var secretNames = names.Where(n => Regex.IsMatch(n.Name, "PASSWORD|SECRET|_KEY|TOKEN|_PEM") && !n.Name.EndsWith("_DAYS")).ToList();
        var leaked = secretNames.Where(n => n.Value.Length > 0 && n.Value != "CHANGE_ME" && !n.Name.EndsWith("_KEY_ID") && !n.Name.EndsWith("ACCESS_KEY_ID")).ToList();
        check(leaked.Count == 0, "Docker: .env.example enthält keine Werte für Passwörter/Schlüssel (nur leer oder CHANGE_ME)" + (leaked.Count > 0 ? " — " + string.Join(", ", leaked.Select(l => l.Name)) : ""));
        check(!Regex.IsMatch(compose, @"(PASSWORD|SECRET|_KEY)\w*: (?!\$\{)\S+"), "Docker: docker-compose.yml enthält keine festen Geheimnisse (nur ${…}-Verweise)");

        // Sichere Standards
        check(compose.Contains("AUTH_MODE: ${AUTH_MODE:-oidc}") && names.First(n => n.Name == "AUTH_MODE").Value == "oidc", "Docker: AUTH_MODE ist standardmäßig oidc (kein stiller dev-Betrieb, ADR 0014)");
        check(!Regex.IsMatch(Section(compose, "  db:", "  web:"), @"^\s+ports:", RegexOptions.Multiline), "Docker: PostgreSQL veröffentlicht keinen Port");
        check(compose.Contains("${LEARNY_BIND_ADDRESS:-127.0.0.1}"), "Docker: Web/MCP sind standardmäßig nur an 127.0.0.1 gebunden");
        check(compose.Contains("web_data:/app/data") && compose.Contains("postgres_data:/var/lib/postgresql/data"), "Docker: Datenbank und /app/data (Schlüssel, Bilder) liegen auf benannten Volumes");
        check(compose.Contains("joergb23/learny-web:${LEARNY_VERSION:-latest}") && compose.Contains("joergb23/learny-mcp:${LEARNY_VERSION:-latest}") && !Regex.IsMatch(compose, @"^\s+build:", RegexOptions.Multiline), "Docker: Compose nutzt ausschließlich Docker-Hub-Images, kein lokaler Build");

        // Workflow
        var uses = Regex.Matches(workflow, @"uses:\s*(\S+)").Select(m => m.Groups[1].Value).ToList();
        check(uses.Count >= 8 && uses.All(u => Regex.IsMatch(u, @"^[\w./-]+@[0-9a-f]{40}$")), "Docker-Workflow: alle externen Actions sind auf Commit-SHAs gepinnt");
        check(workflow.Contains("secrets.DOCKERHUB_USERNAME") && workflow.Contains("secrets.DOCKERHUB_TOKEN") && !Regex.IsMatch(workflow, @"dckr_pat_\w+"), "Docker-Workflow: Anmeldung nur über Repository-Secrets");
        check(workflow.Contains("joergb23/learny-web") && workflow.Contains("joergb23/learny-mcp") && workflow.Contains("release:") && workflow.Contains("types: [published]"), "Docker-Workflow: Images joergb23/learny-web und -mcp, Auslöser Release");
        check(!Regex.IsMatch(workflow, @"git (tag|push)") && !workflow.Contains("deploy-iis"), "Docker-Workflow: erzeugt keine Git-Tags und löst kein IIS-Deployment aus");
        check(workflow.Contains("STABLE") && workflow.Contains("latest") && workflow.Contains("case \"$version\" in *-*) stable=false"), "Docker-Workflow: Prereleases überschreiben latest nicht");
        check(File.ReadAllText(Path.Combine(repoRoot, "src", "LearnCards.Web", "Dockerfile")).Contains("USER 1654") && File.ReadAllText(Path.Combine(repoRoot, "src", "LearnCards.McpServer", "Dockerfile")).Contains("USER 1654"), "Docker: beide Images laufen als unprivilegierter Benutzer");
    }

    private static string Section(string text, string start, string end)
    {
        var a = text.IndexOf(start, StringComparison.Ordinal);
        var b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        return a < 0 ? "" : text.Substring(a, (b < 0 ? text.Length : b) - a);
    }
}
