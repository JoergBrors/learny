namespace LearnCards.Web.Infrastructure;

public enum DbProvider { Sqlite, Postgres }

public class PostgresSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string User { get; set; } = "learncards";
    public string Password { get; set; } = "";
    public string Database { get; set; } = "learncards";
    /// <summary>disable | prefer | require</summary>
    public string SslMode { get; set; } = "prefer";
}

/// <summary>
/// Zentrale, aus Umgebungsvariablen (bzw. .env) geladene Konfiguration.
/// Schlüssel sind kompatibel zur ursprünglichen Python-Lösung.
/// </summary>
public class AppConfig
{
    public string AppName => "LearnCards";
    public string AppDomain { get; set; } = "localhost";
    public bool Debug { get; set; }

    // Datenbank
    public DbProvider DbProvider { get; set; } = DbProvider.Sqlite;
    public string SqlitePath { get; set; } = "data/learncards.db";
    public PostgresSettings Postgres { get; set; } = new();

    // Authentifizierung
    /// <summary>"oidc" | "dev" — dev = lokaler Login ohne Entra ID</summary>
    public string AuthMode { get; set; } = "dev";
    public string OidcIssuer { get; set; } = "";
    public string OidcClientId { get; set; } = "";
    public string OidcClientSecret { get; set; } = "";
    public string OidcAudience { get; set; } = "";
    public string OidcScope { get; set; } = "openid profile email";

    // OpenAI
    public string OpenAiApiKey { get; set; } = "";
    public string OpenAiModel { get; set; } = "gpt-4o-mini";
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public bool OpenAiConfigured =>
        !string.IsNullOrWhiteSpace(OpenAiApiKey) && OpenAiApiKey != "sk-..." && !OpenAiApiKey.StartsWith("replace", StringComparison.OrdinalIgnoreCase);

    // Bildspeicher (ADR 0017)
    public ImageStorageSettings ImageStorage { get; set; } = new();

    // Blockchain-Verankerung und Badge-Signierung (ADR 0021/0022)
    public BlockchainSettings Blockchain { get; set; } = new();

    // PDF-Signatur der Zertifikate (ADR 0033)
    public PdfSigningSettings PdfSigning { get; set; } = new();

    // Learny-PKI (ADR 0035)
    public PkiSettings Pki { get; set; } = new();

    // MCP
    public string McpApiKey { get; set; } = "";
    /// <summary>
    /// Öffentliche Basis-URLs des MCP-Servers (kommagetrennt, z. B. https://mcp.example.com). Nur Rückleitungen auf
    /// {Basis}/oauth/callback sind für die MCP-Zustimmung erlaubt (Schutz vor offenen Weiterleitungen). ADR 0013.
    /// Leer im Dev-Modus: http://127.0.0.1:8787 und http://localhost:8787.
    /// </summary>
    public List<string> McpPublicUrls { get; set; } = new();
    /// <summary>Gültigkeit benutzergebundener MCP-Tokens aus der OAuth-Anmeldung in Tagen (Standard 30).</summary>
    public int McpTokenDays { get; set; } = 30;
    /// <summary>Hauptschlüssel für MCP-Tokens, Codes und OAuth-Clients (MCP_TOKEN_KEY, Pflicht; ADR 0025). Nie in der Datenbank.</summary>
    public byte[]? McpTokenKey { get; set; }
    /// <summary>Vorgängerschlüssel nach einer Rotation (MCP_TOKEN_KEY_PREVIOUS) — nur Prüfen/Entschlüsseln.</summary>
    public List<byte[]> McpTokenKeysPrevious { get; set; } = new();
    /// <summary>Ende der Übergangsfrist für Altbestands-Tokens mit reinem SHA-256 (MCP_LEGACY_TOKEN_UNTIL, ISO-Datum; Standard 2027-01-31).</summary>
    public DateTime McpLegacyTokenUntil { get; set; } = new DateTime(2027, 1, 31, 23, 59, 59, DateTimeKind.Utc);

    // Benutzer / Rollen
    /// <summary>
    /// Zuordnung von Entra-ID-App-Rollen (Claim "roles", Spalte "Value" der App role) zu internen Rollen
    /// (OIDC_ROLE_MAP, z. B. "Admin.All=admin,Auditor=auditor"). Standard: "Admin.All=admin". "user" hat jeder Benutzer ohnehin.
    /// </summary>
    public Dictionary<string, string> RoleMap { get; set; } = new(StringComparer.OrdinalIgnoreCase) { ["Admin.All"] = "admin" };

    /// <summary>IdP-Subjects (kommagetrennt), die beim Login die Rolle "admin" erhalten (Bootstrap).</summary>
    public HashSet<string> AdminSubjects { get; set; } = new(StringComparer.Ordinal);

    public static AppConfig Load()
    {
        string E(string key, string fallback = "") =>
            Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

        var cfg = new AppConfig
        {
            AppDomain = E("APP_DOMAIN", "localhost"),
            Debug = E("DEBUG", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            SqlitePath = E("SQLITE_PATH", "data/learncards.db"),
            OidcIssuer = E("OIDC_ISSUER").TrimEnd('/') is { Length: > 0 } iss ? iss : "",
            OidcClientId = E("OIDC_CLIENT_ID"),
            OidcClientSecret = E("OIDC_CLIENT_SECRET"),
            OidcAudience = E("OIDC_AUDIENCE"),
            OidcScope = E("OIDC_WEB_SCOPE", "openid profile email"),
            OpenAiApiKey = E("OPENAI_API_KEY"),
            OpenAiModel = E("OPENAI_MODEL", "gpt-4o-mini"),
            OpenAiBaseUrl = E("OPENAI_BASE_URL", "https://api.openai.com/v1").TrimEnd('/'),
            McpApiKey = E("MCP_API_KEY"),
            McpPublicUrls = E("MCP_PUBLIC_URL").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(u => u.TrimEnd('/')).ToList(),
            McpTokenDays = int.TryParse(E("MCP_TOKEN_DAYS"), out var mtd) ? Math.Clamp(mtd, 1, 365) : 30,
            AdminSubjects = E("ADMIN_SUBJECTS").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal),
        };

        if (E("OIDC_ROLE_MAP") is { Length: > 0 } roleMap)
        {
            cfg.RoleMap = new(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in roleMap.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var external = pair[..eq].Trim();
                var internalRole = pair[(eq + 1)..].Trim().ToLowerInvariant();
                // "user" besitzt jeder; nur bekannte, privilegierte interne Rollen sind abbildbar
                if (external.Length > 0 && internalRole is "admin" or "auditor" or "themen_admin") cfg.RoleMap[external] = internalRole;
            }
        }

        // ── Bildspeicher: Konfiguration laden und beim Start vollständig prüfen (ADR 0017) ──
        cfg.ImageStorage = ImageStorageSettings.Load();
        cfg.ImageStorage.EnsureValid();

        // ── Blockchain (ADR 0022): Konfiguration beim Start prüfen; Mainnet nie implizit ──
        cfg.Blockchain = BlockchainSettings.Load(Path.GetFullPath("data"));
        cfg.Blockchain.EnsureValid();

        // ── PDF-Signatur (ADR 0033): eigenes Signaturzertifikat aus der .env; Entwicklungszertifikat nur in Development ──
        var envName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        cfg.Pki = PkiSettings.Load(Path.GetFullPath("data"));
        if (cfg.Pki.Validate() is { Count: > 0 } pkiErrors) throw new InvalidOperationException("Ungültige PKI-Konfiguration: " + string.Join(" ", pkiErrors));
        cfg.PdfSigning = PdfSigningSettings.Load(Path.GetFullPath("data"), envName.Equals("Development", StringComparison.OrdinalIgnoreCase));

        // ── Datenbank-Provider bestimmen ────────────────────────────────
        var provider = E("DB_PROVIDER", "auto").ToLowerInvariant();
        var databaseUrl = E("DATABASE_URL");
        var pgHost = E("POSTGRES_HOST");

        if (!string.IsNullOrEmpty(databaseUrl) && databaseUrl.Contains("postgres"))
            ParseDatabaseUrl(databaseUrl, cfg.Postgres);
        if (!string.IsNullOrEmpty(pgHost)) cfg.Postgres.Host = pgHost;
        if (int.TryParse(E("POSTGRES_PORT"), out var pgPort)) cfg.Postgres.Port = pgPort;
        if (E("POSTGRES_USER") is { Length: > 0 } pu) cfg.Postgres.User = pu;
        if (E("POSTGRES_PASSWORD") is { Length: > 0 } pw) cfg.Postgres.Password = pw;
        if (E("POSTGRES_DB") is { Length: > 0 } pdb) cfg.Postgres.Database = pdb;
        if (E("POSTGRES_SSLMODE") is { Length: > 0 } ssl) cfg.Postgres.SslMode = ssl.ToLowerInvariant();

        cfg.DbProvider = provider switch
        {
            "sqlite" => DbProvider.Sqlite,
            "postgres" or "postgresql" => DbProvider.Postgres,
            // auto: Postgres nur, wenn explizit ein Host oder eine DATABASE_URL angegeben wurde
            _ => (!string.IsNullOrEmpty(pgHost) || databaseUrl.Contains("postgres"))
                    ? DbProvider.Postgres : DbProvider.Sqlite,
        };

        // ── Auth-Modus bestimmen ────────────────────────────────────────
        // Fail closed (ADR 0014/0026): Außerhalb von "Development" wird der Dev-Login nie stillschweigend aktiv. "auto", leere oder unbekannte Werte
        // (z. B. Tippfehler) führen dann zu OIDC oder zum Startabbruch; "dev" gilt nur, wenn AUTH_MODE=dev ausdrücklich gesetzt ist.
        var authMode = E("AUTH_MODE", "auto").Trim().ToLowerInvariant();
        var isDevelopment = (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production")
            .Equals("Development", StringComparison.OrdinalIgnoreCase);
        var oidcComplete = cfg.OidcIssuer.Length > 0 && cfg.OidcClientId.Length > 0
                           && !cfg.OidcClientId.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase);
        cfg.AuthMode = authMode switch
        {
            "oidc" => "oidc",
            "dev" or "none" or "local" => "dev",
            _ => oidcComplete ? "oidc" : "dev",
        };
        if (!isDevelopment)
        {
            var explicitDev = authMode is "dev" or "none" or "local";
            if (!explicitDev && authMode is not ("oidc" or "auto"))
                throw new InvalidOperationException($"AUTH_MODE hat den ungültigen Wert '{authMode}'. Erlaubt: oidc | dev (nur zum lokalen Ausprobieren). Ein Tippfehler darf nicht zum Dev-Login führen.");
            if (!explicitDev && !oidcComplete)
                throw new InvalidOperationException("Die Anmeldung ist nicht konfiguriert: OIDC_ISSUER und OIDC_CLIENT_ID (und OIDC_CLIENT_SECRET) fehlen. Der Dev-Login ohne Passwort wird außerhalb von Development nie " +
                    "stillschweigend aktiviert. Ursache oft: die .env wurde nicht gelesen (ENV_FILE prüfen). Für lokale Tests ausdrücklich AUTH_MODE=dev setzen.");
            if (authMode == "oidc" && (!oidcComplete || cfg.OidcClientSecret.Length == 0))
                throw new InvalidOperationException("AUTH_MODE=oidc, aber OIDC_ISSUER, OIDC_CLIENT_ID oder OIDC_CLIENT_SECRET fehlt.");
        }
        if (cfg.McpPublicUrls.Count == 0 && cfg.AuthMode == "dev")
            cfg.McpPublicUrls = new() { "http://127.0.0.1:8787", "http://localhost:8787" };

        // MCP-Schlüssel (ADR 0025): Pflicht außerhalb von Development; Fehler nennen nie den Schlüsselwert
        cfg.McpTokenKey = McpTokenKeys.Resolve(E("MCP_TOKEN_KEY"), isDevelopment, Path.GetFullPath("data"));
        cfg.McpTokenKeysPrevious = McpTokenKeys.ResolvePrevious(E("MCP_TOKEN_KEY_PREVIOUS"));
        if (E("MCP_LEGACY_TOKEN_UNTIL") is { Length: > 0 } until)
        {
            if (!DateTime.TryParse(until, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var untilDate))
                throw new InvalidOperationException("MCP_LEGACY_TOKEN_UNTIL ist kein gültiges Datum (z. B. 2027-01-31).");
            cfg.McpLegacyTokenUntil = untilDate;
        }
        return cfg;
    }

    /// <summary>Parst postgres://user:pass@host:port/db?sslmode=require (auch postgresql+asyncpg://).</summary>
    internal static void ParseDatabaseUrl(string url, PostgresSettings pg)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return;
        var rest = url[(schemeEnd + 3)..];

        var qIdx = rest.IndexOf('?');
        string? query = null;
        if (qIdx >= 0) { query = rest[(qIdx + 1)..]; rest = rest[..qIdx]; }

        var at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            var userInfo = rest[..at];
            rest = rest[(at + 1)..];
            var colon = userInfo.IndexOf(':');
            if (colon >= 0)
            {
                pg.User = Uri.UnescapeDataString(userInfo[..colon]);
                pg.Password = Uri.UnescapeDataString(userInfo[(colon + 1)..]);
            }
            else pg.User = Uri.UnescapeDataString(userInfo);
        }

        var slash = rest.IndexOf('/');
        if (slash >= 0)
        {
            pg.Database = Uri.UnescapeDataString(rest[(slash + 1)..]);
            rest = rest[..slash];
        }

        var portColon = rest.LastIndexOf(':');
        if (portColon >= 0 && int.TryParse(rest[(portColon + 1)..], out var port))
        {
            pg.Port = port;
            rest = rest[..portColon];
        }
        if (rest.Length > 0) pg.Host = rest;

        if (query is not null)
            foreach (var kv in query.Split('&'))
            {
                var eq = kv.IndexOf('=');
                if (eq > 0 && kv[..eq].Equals("sslmode", StringComparison.OrdinalIgnoreCase))
                    pg.SslMode = kv[(eq + 1)..].ToLowerInvariant();
            }
    }
}
