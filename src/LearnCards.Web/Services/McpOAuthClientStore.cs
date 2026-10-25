using System.Text.Json;
using System.Text.Json.Serialization;
using LearnCards.Web.Data;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services;

/// <summary>Datensatz eines dynamisch registrierten öffentlichen OAuth-Clients des MCP-Servers (ADR 0025). Öffentliche Clients haben kein Secret.</summary>
public sealed class McpOAuthClientRecord
{
    [JsonPropertyName("client_id")] public string ClientId { get; set; } = "";
    [JsonPropertyName("client_name")] public string ClientName { get; set; } = "";
    [JsonPropertyName("redirect_uris")] public List<string> RedirectUris { get; set; } = new();
    [JsonPropertyName("token_endpoint_auth_method")] public string TokenEndpointAuthMethod { get; set; } = "none";
    [JsonPropertyName("scopes")] public List<string> Scopes { get; set; } = new();
    [JsonPropertyName("application_type")] public string ApplicationType { get; set; } = "web";
    [JsonPropertyName("issued_at")] public long IssuedAt { get; set; }
}

/// <summary>
/// Persistente Ablage der OAuth-Client-Registrierungen (damit sich Claude nach einem Neustart des MCP-Servers nicht neu registrieren muss).
/// Der Datensatz liegt nur verschlüsselt (AES-256-GCM, client_id als AAD) mit Zeilen-MAC in der Datenbank — Schlüssel aus der .env (ADR 0025).
/// Gespeichert werden nur öffentliche Clients (ohne Secret). Zugriff nur über Endpunkte mit Dienstschlüssel.
/// </summary>
public class McpOAuthClientStore
{
    public const int MaxClients = 1000;
    private const string Table = "mcp_oauth_clients";
    private static readonly System.Text.RegularExpressions.Regex IdPattern = new("^[A-Za-z0-9._-]{8,100}$");

    private readonly IDatabase _db;
    private readonly McpTokenProtector _prot;
    private readonly object _rateLock = new();
    private DateTime _rateWindow = DateTime.UtcNow;
    private int _rateCount;

    public McpOAuthClientStore(IDatabase db, McpTokenProtector protector)
    {
        _db = db;
        _prot = protector;
    }

    /// <summary>Speichert einen Client. Liefert einen Fehlertext (für die Antwort) oder null bei Erfolg; wiederholtes Speichern derselben client_id ist wirkungslos.</summary>
    public async Task<string?> RegisterAsync(McpOAuthClientRecord rec)
    {
        if (!IdPattern.IsMatch(rec.ClientId ?? "")) return "client_id ungültig";
        if (!string.Equals(rec.TokenEndpointAuthMethod, "none", StringComparison.Ordinal)) return "Nur öffentliche Clients (token_endpoint_auth_method=none) werden gespeichert";
        if (rec.RedirectUris.Count is < 1 or > 10 || rec.RedirectUris.Any(u => !IsSafeRedirect(u))) return "redirect_uris ungültig";
        if ((rec.ClientName ?? "").Length > 200 || rec.Scopes.Count > 20 || rec.Scopes.Any(s => s.Length > 100)) return "Felder zu lang";
        lock (_rateLock)
        {
            if ((DateTime.UtcNow - _rateWindow).TotalMinutes >= 1) { _rateWindow = DateTime.UtcNow; _rateCount = 0; }
            if (++_rateCount > 60) return "Zu viele Registrierungen";
        }

        // Aufräumen: nie für eine Anmeldung verwendete Clients verfallen nach 30 Tagen
        await _db.ExecuteAsync("DELETE FROM mcp_oauth_clients WHERE last_used_at IS NULL AND created_at < @cut",
            new (string, object?)[] { ("cut", DateTime.UtcNow.AddDays(-30)) });
        var count = await _db.QueryAsync("SELECT COUNT(*) AS n FROM mcp_oauth_clients");
        if (count.Count > 0 && int.TryParse(count[0]["n"], out var n) && n >= MaxClients) return "Maximale Anzahl registrierter Clients erreicht";

        var exists = await _db.QueryAsync("SELECT 1 FROM mcp_oauth_clients WHERE client_id = @id", new[] { ("id", (object?)rec.ClientId) });
        if (exists.Count > 0) return null;

        var now = DateTime.UtcNow;
        var keyId = _prot.ActiveKeyId;
        rec.IssuedAt = rec.IssuedAt == 0 ? new DateTimeOffset(now).ToUnixTimeSeconds() : rec.IssuedAt;
        var enc = _prot.Encrypt(JsonSerializer.Serialize(rec), rec.ClientId);
        await _db.ExecuteAsync("""
            INSERT INTO mcp_oauth_clients (client_id, key_id, record_enc, created_at, row_mac)
            VALUES (@id, @k, @enc, @now, @mac)
            """, new (string, object?)[]
        {
            ("id", rec.ClientId), ("k", keyId), ("enc", enc), ("now", now),
            ("mac", _prot.RowMac(keyId, Table, rec.ClientId, keyId, enc, McpTokenProtector.Ts(now))),
        });
        return null;
    }

    /// <summary>Lädt und entschlüsselt einen Client. Manipulierte oder fremde Datensätze (MAC/AAD) liefern null.</summary>
    public async Task<McpOAuthClientRecord?> GetAsync(string clientId)
    {
        if (!IdPattern.IsMatch(clientId ?? "")) return null;
        var rows = await _db.QueryAsync("SELECT client_id, key_id, record_enc, created_at, last_used_at, row_mac FROM mcp_oauth_clients WHERE client_id = @id",
            new[] { ("id", (object?)clientId) });
        if (rows.Count == 0) return null;
        var r = rows[0];
        var keyId = r["key_id"] ?? "";
        var enc = r["record_enc"] ?? "";
        if (!_prot.VerifyRowMac(keyId, r.GetValueOrDefault("row_mac"), Table, clientId, keyId, enc, McpTokenProtector.Ts(DbValue.ToDateTime(r["created_at"])))) return null;
        var json = _prot.Decrypt(keyId, enc, clientId);
        if (json is null) return null;
        McpOAuthClientRecord? rec;
        try { rec = JsonSerializer.Deserialize<McpOAuthClientRecord>(json); }
        catch (JsonException) { return null; }
        if (rec is null || !string.Equals(rec.ClientId, clientId, StringComparison.Ordinal)) return null;

        var last = r.GetValueOrDefault("last_used_at");
        if (string.IsNullOrEmpty(last) || DbValue.ToDateTime(last) < DateTime.UtcNow.AddDays(-1))
            await _db.ExecuteAsync("UPDATE mcp_oauth_clients SET last_used_at=@n WHERE client_id=@id", new (string, object?)[] { ("n", DateTime.UtcNow), ("id", clientId) });
        return rec;
    }

    /// <summary>https, Loopback-http oder ein eigenes App-Schema; nie javascript/data/file/vbscript, keine Fragmente.</summary>
    private static bool IsSafeRedirect(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > 500 || !Uri.TryCreate(uri, UriKind.Absolute, out var u) || !string.IsNullOrEmpty(u.Fragment)) return false;
        if (u.Scheme is "javascript" or "data" or "file" or "vbscript" or "blob" or "about") return false;
        if (u.Scheme == "http") return u.IsLoopback;
        return true;
    }
}
