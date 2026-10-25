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
/// Schutz bei Datenbankzugriff (ADR 0025): Tokens und Codes liegen nur als HMAC mit einem Schlüssel aus der .env in der Datenbank,
/// jede Zeile trägt einen MAC (Manipulation/Einschleusen wird erkannt). Altbestand (reines SHA-256) wird befristet beim ersten Gebrauch umgestellt.
/// </summary>
public class McpLinkService
{
    public const string TokenPrefix = "lcm_";
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    private readonly IDatabase _db;
    private readonly AppConfig _cfg;
    private readonly UserService _users;
    private readonly McpTokenProtector _prot;

    public McpLinkService(IDatabase db, AppConfig cfg, UserService users, McpTokenProtector protector)
    {
        _db = db;
        _cfg = cfg;
        _users = users;
        _prot = protector;
    }

    private const string LinksTable = "mcp_links";
    private const string CodesTable = "mcp_link_codes";

    private string LinkMac(string? keyId, string id, string userId, string kind, string tokenHash, DateTime created, DateTime expires, DateTime? revoked) =>
        _prot.RowMac(keyId, LinksTable, id, userId, kind, tokenHash, McpTokenProtector.Ts(created), McpTokenProtector.Ts(expires), McpTokenProtector.Ts(revoked), keyId);

    // ─── Erstellen / Prüfen / Widerrufen ────────────────────────────────────

    public async Task<(string Token, McpLink Link)> CreateLinkAsync(string userId, string name, string kind, TimeSpan lifetime)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) name = kind == "oauth" ? "MCP-Client" : "Persönliches Token";
        if (name.Length > 80) name = name[..80];

        var token = TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid().ToString();
        var link = new McpLink
        {
            // Wiedererkennung ohne Token-Teilwissen: Kurzkennung aus der Zeilen-ID (ADR 0025)
            Id = id, UserId = userId, Name = name, Kind = kind,
            TokenHint = "#" + id[..4], CreatedAt = now, ExpiresAt = now.Add(lifetime),
        };
        var keyId = _prot.ActiveKeyId;
        var tokenHash = _prot.HashToken(token);
        await _db.ExecuteAsync("""
            INSERT INTO mcp_links (id, user_id, name, kind, token_hash, token_hint, created_at, expires_at, hash_alg, key_id, row_mac)
            VALUES (@id, @user_id, @name, @kind, @token_hash, @token_hint, @created_at, @expires_at, 'hmac1', @key_id, @row_mac)
            """, new (string, object?)[]
        {
            ("id", id), ("user_id", userId), ("name", name), ("kind", kind), ("token_hash", tokenHash),
            ("token_hint", link.TokenHint), ("created_at", now), ("expires_at", link.ExpiresAt), ("key_id", keyId),
            ("row_mac", LinkMac(keyId, id, userId, kind, tokenHash, now, link.ExpiresAt, null)),
        });
        await _users.WriteAuditAsync(userId, "mcp.link.create", link.Id, $"{kind}:{name}");
        return (token, link);
    }

    /// <summary>
    /// Liefert Benutzer und Zugang, wenn das Token gültig, nicht widerrufen/abgelaufen und der Benutzer aktiv ist.
    /// Vor allem anderen wird der Zeilen-MAC geprüft: eine in der Datenbank eingeschleuste oder veränderte Zeile ergibt nie einen Zugang.
    /// </summary>
    public async Task<(AppUser User, McpLink Link)?> ValidateAsync(string? token)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal) || token.Length > 200) return null;
        var candidates = _prot.TokenCandidates(token);
        var legacyHash = McpTokenProtector.LegacyHash(token);
        var hashes = candidates.Select(c => c.Hash).Append(legacyHash).ToList();
        var args = hashes.Select((h, i) => ("h" + i, (object?)h)).ToList();
        var rows = await _db.QueryAsync($"""
            SELECT id, user_id, name, kind, token_hint, created_at, expires_at, last_used_at, revoked_at, token_hash, hash_alg, key_id, row_mac
            FROM mcp_links WHERE token_hash IN ({string.Join(", ", args.Select(a => "@" + a.Item1))})
            """, args);
        if (rows.Count == 0) return null;

        var row = rows[0];
        var link = Map(row);
        var storedHash = row["token_hash"] ?? "";
        var alg = row.GetValueOrDefault("hash_alg") ?? "sha256";
        var keyId = row.GetValueOrDefault("key_id") ?? "";
        var legacy = alg == "sha256";
        if (legacy)
        {
            // Übergangsregel (ADR 0025): Altbestand nur befristet und nur mit passendem reinem SHA-256
            if (DateTime.UtcNow > _prot.LegacyUntil || !string.Equals(storedHash, legacyHash, StringComparison.Ordinal)) return null;
        }
        else
        {
            var hashMatchesKey = candidates.Any(c => c.KeyId == keyId && string.Equals(c.Hash, storedHash, StringComparison.Ordinal));
            if (!hashMatchesKey || !_prot.VerifyRowMac(keyId, row.GetValueOrDefault("row_mac"), LinksTable,
                    link.Id, link.UserId, link.Kind, storedHash, McpTokenProtector.Ts(link.CreatedAt), McpTokenProtector.Ts(link.ExpiresAt), McpTokenProtector.Ts(link.RevokedAt), keyId))
            {
                await AuditIntegrityAsync(link.UserId, link.Id);
                return null;
            }
        }

        if (!link.IsActive) return null;
        var user = await _users.GetAsync(link.UserId);
        if (user is null || !user.IsActive) return null;

        // Umstellung Altbestand → HMAC bzw. Schlüsselrotation: das Klartext-Token liegt jetzt vor
        if (legacy || keyId != _prot.ActiveKeyId)
        {
            var newKey = _prot.ActiveKeyId;
            var newHash = _prot.HashToken(token);
            await _db.ExecuteAsync("""
                UPDATE mcp_links SET token_hash=@nh, hash_alg='hmac1', key_id=@k, row_mac=@m, token_hint=@hint WHERE id=@id AND token_hash=@oh
                """, new (string, object?)[]
            {
                ("nh", newHash), ("k", newKey), ("m", LinkMac(newKey, link.Id, link.UserId, link.Kind, newHash, link.CreatedAt, link.ExpiresAt, link.RevokedAt)),
                ("hint", "#" + link.Id[..4]), ("id", link.Id), ("oh", storedHash),
            });
        }

        // last_used_at höchstens einmal pro Minute schreiben (nicht Teil des MAC)
        if (link.LastUsedAt is null || link.LastUsedAt < DateTime.UtcNow.AddMinutes(-1))
        {
            await _db.ExecuteAsync("UPDATE mcp_links SET last_used_at=@n WHERE id=@id",
                new (string, object?)[] { ("n", DateTime.UtcNow), ("id", link.Id) });
            link.LastUsedAt = DateTime.UtcNow;
        }
        return (user, link);
    }

    private async Task AuditIntegrityAsync(string userId, string linkId)
    {
        try { await _users.WriteAuditAsync(userId, "mcp.token.integrity_failure", linkId, "Zeilen-MAC ungültig"); }
        catch (Exception) { /* Audit darf die Ablehnung nie verhindern */ }
    }

    public async Task<List<McpLink>> ListAsync(string userId)
    {
        var rows = await _db.QueryAsync("""
            SELECT id, user_id, name, kind, token_hint, created_at, expires_at, last_used_at, revoked_at
            FROM mcp_links WHERE user_id = @u ORDER BY created_at DESC
            """, new[] { ("u", (object?)userId) });
        return rows.Select(Map).ToList();
    }

    /// <summary>Widerruft einen eigenen Zugang (Zeilen-MAC wird neu berechnet). Fremde IDs wirken wie "nicht vorhanden".</summary>
    public async Task<bool> RevokeAsync(string userId, string linkId)
    {
        if (!UserService.IsGuid(linkId)) return false;
        var rows = await _db.QueryAsync("""
            SELECT id, user_id, kind, token_hash, created_at, expires_at, hash_alg, key_id FROM mcp_links WHERE id=@id AND user_id=@u AND revoked_at IS NULL
            """, new (string, object?)[] { ("id", linkId), ("u", userId) });
        if (rows.Count == 0) return false;
        var r = rows[0];
        var now = DateTime.UtcNow;
        var keyId = r.GetValueOrDefault("key_id") ?? "";
        var mac = (r.GetValueOrDefault("hash_alg") ?? "sha256") == "hmac1" && _prot.KnowsKey(keyId)
            ? LinkMac(keyId, linkId, userId, r["kind"] ?? "", r["token_hash"] ?? "", DbValue.ToDateTime(r["created_at"]), DbValue.ToDateTime(r["expires_at"]), now)
            : "";
        var n = await _db.ExecuteAsync("UPDATE mcp_links SET revoked_at=@n, row_mac=@m WHERE id=@id AND user_id=@u AND revoked_at IS NULL",
            new (string, object?)[] { ("n", now), ("m", mac), ("id", linkId), ("u", userId) });
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
            var codeHash = _prot.HashCode(code);
            var cleanName = (clientName ?? "").Trim();
            var expires = now.Add(CodeLifetime);
            var keyId = _prot.ActiveKeyId;
            await _db.ExecuteAsync("""
                INSERT INTO mcp_link_codes (code_hash, user_id, client_name, created_at, expires_at, key_id, row_mac)
                VALUES (@h, @u, @c, @now, @exp, @k, @m)
                """, new (string, object?)[]
            {
                ("h", codeHash), ("u", userId), ("c", cleanName), ("now", now), ("exp", expires), ("k", keyId),
                ("m", _prot.RowMac(keyId, CodesTable, codeHash, userId, cleanName, McpTokenProtector.Ts(now), McpTokenProtector.Ts(expires), keyId)),
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

        // Atomar entwerten: nur wer die Zeile tatsächlich von "unbenutzt" auf "benutzt" setzt, darf fortfahren (aktiver Schlüssel zuerst, dann Vorgänger)
        string? hash = null;
        foreach (var candidate in _prot.CodeCandidates(code))
        {
            var claimed = await _db.ExecuteAsync("UPDATE mcp_link_codes SET used_at=@n WHERE code_hash=@h AND used_at IS NULL AND expires_at > @n",
                new (string, object?)[] { ("n", DateTime.UtcNow), ("h", candidate.Hash) });
            if (claimed > 0) { hash = candidate.Hash; break; }
        }
        if (hash is null) return null;

        var rows = await _db.QueryAsync("SELECT user_id, client_name, created_at, expires_at, key_id, row_mac FROM mcp_link_codes WHERE code_hash=@h", new[] { ("h", (object?)hash) });
        if (rows.Count == 0) return null;
        var r = rows[0];
        var keyId = r.GetValueOrDefault("key_id") ?? "";
        if (!_prot.VerifyRowMac(keyId, r.GetValueOrDefault("row_mac"), CodesTable, hash, r["user_id"] ?? "", r.GetValueOrDefault("client_name") ?? "",
                McpTokenProtector.Ts(DbValue.ToDateTime(r["created_at"])), McpTokenProtector.Ts(DbValue.ToDateTime(r["expires_at"])), keyId))
        {
            await AuditIntegrityAsync(r["user_id"] ?? "", "code");
            return null;
        }
        var user = await _users.GetAsync(r["user_id"]);
        if (user is null || !user.IsActive) return null;

        return await CreateLinkAsync(user.Id, r.GetValueOrDefault("client_name") ?? "", "oauth", TimeSpan.FromDays(_cfg.McpTokenDays));
    }

    // ─── Hilfen ─────────────────────────────────────────────────────────────

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
