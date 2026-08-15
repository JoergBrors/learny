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

    // MCP
    public string McpApiKey { get; set; } = "";

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
        };

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
        var authMode = E("AUTH_MODE", "auto").ToLowerInvariant();
        cfg.AuthMode = authMode switch
        {
            "oidc" => "oidc",
            "dev" or "none" or "local" => "dev",
            _ => (cfg.OidcIssuer.Length > 0 && cfg.OidcClientId.Length > 0
                  && !cfg.OidcClientId.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase))
                    ? "oidc" : "dev",
        };
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
