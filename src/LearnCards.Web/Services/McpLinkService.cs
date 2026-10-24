using System.Security.Cryptography;
using System.Text;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services;

/// <summary>Ein benutzergebundener MCP-Zugang (Metadaten; das Token selbst wird nie gespeichert, nur sein Hash).</summary>
public class McpLink
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"oauth" (über die MCP-Anmeldung) oder "token" (persönliches Token, z. B. für stdio)</summary>
    public string Kind { get; set; } = "";
    public string TokenHint { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTime.UtcNow;
}

/// <summary>
/// Benutzergebundene MCP-Zugänge (ADR 0013): persönliche Tokens und OAuth-Verknüpfungen.
/// Ein Token identifiziert genau einen Learny-Benutzer; die API handelt dann ausschließlich in dessen Kontext.
/// Tokens werden nur als SHA-256-Hash gespeichert (zufällige 256 Bit, daher kein Salt nötig) und sind widerrufbar.
/// </summary>
public class McpLinkService
{
    public const string TokenPrefix = "lcm_";
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    private readonly IDatabase _db;
    private readonly AppConfig _cfg;
    private readonly UserService _users;

    public McpLinkService(IDatabase db, AppConfig cfg, UserService users)
    {
        _db = db;
        _cfg = cfg;
        _users = users;
    }

    // ─── Erstellen / Prüfen / Widerrufen ────────────────────────────────────

    public async Task<(string Token, McpLink Link)> CreateLinkAsync(string userId, string name, string kind, TimeSpan lifetime)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) name = kind == "oauth" ? "MCP-Client" : "Persönliches Token";
        if (name.Length > 80) name = name[..80];

        var token = TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        var link = new McpLink
        {
            Id = Guid.NewGuid().ToString(), UserId = userId, Name = name, Kind = kind,
            TokenHint = "…" + token[^4..], CreatedAt = now, ExpiresAt = now.Add(lifetime),
        };
        await _db.ExecuteAsync("""
            INSERT INTO mcp_links (id, user_id, name, kind, token_hash, token_hint, created_at, expires_at)
            VALUES (@id, @user_id, @name, @kind, @token_hash, @token_hint, @created_at, @expires_at)
            """, new (string, object?)[]
        {
            ("id", link.Id), ("user_id", userId), ("name", name), ("kind", kind), ("token_hash", Hash(token)),
            ("token_hint", link.TokenHint), ("created_at", now), ("expires_at", link.ExpiresAt),
        });
        await _users.WriteAuditAsync(userId, "mcp.link.create", link.Id, $"{kind}:{name}");
        return (token, link);
    }

    /// <summary>Liefert Benutzer und Zugang, wenn das Token gültig, nicht widerrufen/abgelaufen und der Benutzer aktiv ist.</summary>
    public async Task<(AppUser User, McpLink Link)?> ValidateAsync(string? token)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal) || token.Length > 200) return null;
        var rows = await _db.QueryAsync("""
            SELECT id, user_id, name, kind, token_hint, created_at, expires_at, last_used_at, revoked_at
            FROM mcp_links WHERE token_hash = @h
            """, new[] { ("h", (object?)Hash(token)) });
        if (rows.Count == 0) return null;

        var link = Map(rows[0]);
        if (!link.IsActive) return null;
        var user = await _users.GetAsync(link.UserId);
        if (user is null || !user.IsActive) return null;

        // last_used_at höchstens einmal pro Minute schreiben
        if (link.LastUsedAt is null || link.LastUsedAt < DateTime.UtcNow.AddMinutes(-1))
        {
            await _db.ExecuteAsync("UPDATE mcp_links SET last_used_at=@n WHERE id=@id",
                new (string, object?)[] { ("n", DateTime.UtcNow), ("id", link.Id) });
            link.LastUsedAt = DateTime.UtcNow;
        }
        return (user, link);
    }

    public async Task<List<McpLink>> ListAsync(string userId)
    {
        var rows = await _db.QueryAsync("""
            SELECT id, user_id, name, kind, token_hint, created_at, expires_at, last_used_at, revoked_at
            FROM mcp_links WHERE user_id = @u ORDER BY created_at DESC
            """, new[] { ("u", (object?)userId) });
        return rows.Select(Map).ToList();
    }

    /// <summary>Widerruft einen eigenen Zugang. Fremde IDs wirken wie "nicht vorhanden".</summary>
    public async Task<bool> RevokeAsync(string userId, string linkId)
    {
        if (!UserService.IsGuid(linkId)) return false;
        var n = await _db.ExecuteAsync("UPDATE mcp_links SET revoked_at=@n WHERE id=@id AND user_id=@u AND revoked_at IS NULL",
            new (string, object?)[] { ("n", DateTime.UtcNow), ("id", linkId), ("u", userId) });
        if (n > 0) await _users.WriteAuditAsync(userId, "mcp.link.revoke", linkId, "");
        return n > 0;
    }

    // ─── OAuth-Zustimmung (Browser des angemeldeten Benutzers) ──────────────

    /// <summary>Nur Rückleitungen auf {MCP_PUBLIC_URL}/oauth/callback sind erlaubt (kein offener Redirect).</summary>
    public bool IsAllowedReturnUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return _cfg.McpPublicUrls.Any(b => string.Equals(b.TrimEnd('/') + "/oauth/callback", normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verarbeitet die Entscheidung des angemeldeten Benutzers: Bei Zustimmung wird ein einmaliger Code erzeugt (nur als Hash gespeichert,
    /// 5 Minuten gültig) und die Rückleitungs-URL zum MCP-Server (mit code + state) geliefert, sonst mit error=access_denied.
    /// </summary>
    public async Task<string> CompleteConsentAsync(string userId, string returnUrl, string state, string clientName, bool approve)
    {
        if (!IsAllowedReturnUrl(returnUrl)) throw new ArgumentException("Die Rückleitungs-URL ist nicht erlaubt (MCP_PUBLIC_URL).");
        if (string.IsNullOrWhiteSpace(state) || state.Length > 200) throw new ArgumentException("state fehlt oder ist zu lang.");

        var builder = new UriBuilder(returnUrl);
        var query = new List<string> { "state=" + Uri.EscapeDataString(state) };
        if (approve)
        {
            var code = Base64Url(RandomNumberGenerator.GetBytes(32));
            var now = DateTime.UtcNow;
            await _db.ExecuteAsync("""
                INSERT INTO mcp_link_codes (code_hash, user_id, client_name, created_at, expires_at)
                VALUES (@h, @u, @c, @now, @exp)
                """, new (string, object?)[]
            {
                ("h", Hash(code)), ("u", userId), ("c", (clientName ?? "").Trim()), ("now", now), ("exp", now.Add(CodeLifetime)),
            });
            query.Insert(0, "code=" + Uri.EscapeDataString(code));
        }
        else
        {
            query.Insert(0, "error=access_denied");
        }
        builder.Query = string.Join("&", query);
        return builder.Uri.ToString();
    }

    /// <summary>
    /// Tauscht einen Übergabecode (nur der MCP-Server mit dem Dienstschlüssel darf das) gegen ein benutzergebundenes Token.
    /// Der Code ist einmalig und kurzlebig; ein zweiter Versuch schlägt fehl.
    /// </summary>
    public async Task<(string Token, McpLink Link)?> ExchangeCodeAsync(string code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 200) return null;
        var hash = Hash(code);

        // Atomar entwerten: nur wer die Zeile tatsächlich von "unbenutzt" auf "benutzt" setzt, darf fortfahren
        var claimed = await _db.ExecuteAsync("UPDATE mcp_link_codes SET used_at=@n WHERE code_hash=@h AND used_at IS NULL AND expires_at > @n",
            new (string, object?)[] { ("n", DateTime.UtcNow), ("h", hash) });
        if (claimed == 0) return null;

        var rows = await _db.QueryAsync("SELECT user_id, client_name FROM mcp_link_codes WHERE code_hash=@h", new[] { ("h", (object?)hash) });
        if (rows.Count == 0) return null;
        var user = await _users.GetAsync(rows[0]["user_id"]);
        if (user is null || !user.IsActive) return null;

        return await CreateLinkAsync(user.Id, rows[0].GetValueOrDefault("client_name") ?? "", "oauth", TimeSpan.FromDays(_cfg.McpTokenDays));
    }

    // ─── Hilfen ─────────────────────────────────────────────────────────────

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static McpLink Map(Dictionary<string, string?> r) => new()
    {
        Id = r["id"] ?? "",
        UserId = r["user_id"] ?? "",
        Name = r.GetValueOrDefault("name") ?? "",
        Kind = r.GetValueOrDefault("kind") ?? "",
        TokenHint = r.GetValueOrDefault("token_hint") ?? "",
        CreatedAt = DbValue.ToDateTime(r.GetValueOrDefault("created_at")),
        ExpiresAt = DbValue.ToDateTime(r.GetValueOrDefault("expires_at")),
        LastUsedAt = r.GetValueOrDefault("last_used_at") is { Length: > 0 } l ? DbValue.ToDateTime(l) : null,
        RevokedAt = r.GetValueOrDefault("revoked_at") is { Length: > 0 } v ? DbValue.ToDateTime(v) : null,
    };
}
