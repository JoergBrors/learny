using System.Text.Json;

namespace LearnCards.McpServer;

public sealed class McpSettings
{
    public string ServerName { get; set; } = "learncards";
    public string ServerVersion { get; set; } = "2.1.0";
    public UpstreamApiSettings UpstreamApi { get; set; } = new();
    public McpTransportSettings Transports { get; set; } = new();
    public ProprietaryOAuthSettings OAuth { get; set; } = new();

    public static McpSettings Load()
    {
        var path = ResolveConfigPath();
        var settings = new McpSettings();
        if (path is not null)
        {
            using var stream = File.OpenRead(path);
            settings = JsonSerializer.Deserialize<McpSettings>(stream, JsonOptions) ?? new McpSettings();
        }

        settings.ApplyEnvironmentOverrides();
        settings.Normalize();
        return settings;
    }

    private static string? ResolveConfigPath()
    {
        var configured = Environment.GetEnvironmentVariable("LEARNCARDS_MCP_CONFIG");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var candidates = new List<string>();
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 5 && current is not null; i++, current = current.Parent)
            candidates.Add(Path.Combine(current.FullName, "mcpsettings.json"));

        var exeDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(exeDir, "mcpsettings.json"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private void ApplyEnvironmentOverrides()
    {
        if (Environment.GetEnvironmentVariable("LEARNCARDS_API_URL") is { Length: > 0 } apiBase)
            UpstreamApi.BaseUrl = apiBase;

        var apiKey = Environment.GetEnvironmentVariable("LEARNCARDS_MCP_API_KEY")
                     ?? Environment.GetEnvironmentVariable("MCP_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey))
            UpstreamApi.ApiKey = apiKey;

        if (Environment.GetEnvironmentVariable("LEARNCARDS_MCP_HTTP_URLS") is { Length: > 0 } urls)
        {
            Transports.Http.Enabled = true;
            Transports.Http.Urls = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        if (Environment.GetEnvironmentVariable("LEARNCARDS_MCP_STDIO") is { Length: > 0 } stdioRaw
            && bool.TryParse(stdioRaw, out var stdioEnabled))
            Transports.Stdio.Enabled = stdioEnabled;

        if (Environment.GetEnvironmentVariable("LEARNCARDS_MCP_HTTP") is { Length: > 0 } httpRaw
            && bool.TryParse(httpRaw, out var httpEnabled))
            Transports.Http.Enabled = httpEnabled;
    }

    private void Normalize()
    {
        UpstreamApi.BaseUrl = string.IsNullOrWhiteSpace(UpstreamApi.BaseUrl)
            ? "http://localhost:5000/api"
            : UpstreamApi.BaseUrl.TrimEnd('/');

        UpstreamApi.ApiKey ??= "";
        Transports.Http.Urls = Transports.Http.Urls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

public sealed class UpstreamApiSettings
{
    public string BaseUrl { get; set; } = "http://localhost:5000/api";
    public string ApiKey { get; set; } = "";
}

public sealed class McpTransportSettings
{
    public StdioTransportSettings Stdio { get; set; } = new();
    public HttpTransportSettings Http { get; set; } = new();
}

public sealed class StdioTransportSettings
{
    public bool Enabled { get; set; } = true;
}

public sealed class HttpTransportSettings
{
    public bool Enabled { get; set; }
    public List<string> Urls { get; set; } = new() { "http://127.0.0.1:8787" };
}

public sealed class ProprietaryOAuthSettings
{
    public bool Enabled { get; set; }
    public string Issuer { get; set; } = "learncards-mcp";
    public string Audience { get; set; } = "learncards-mcp";
    public string SigningKey { get; set; } = "";
    public int TokenLifetimeMinutes { get; set; } = 60;
    public List<OAuthClientSettings> Clients { get; set; } = new();
}

public sealed class OAuthClientSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public List<string> Scopes { get; set; } = new() { "mcp" };
}
