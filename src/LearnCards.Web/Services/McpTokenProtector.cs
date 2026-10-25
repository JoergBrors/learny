using System.Security.Cryptography;
using System.Text;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services;

/// <summary>
/// Schutz von MCP-Tokens, Einmal-Codes und OAuth-Clients gegen Zugriff auf die Datenbank (ADR 0025):
/// Schlüssel-Hash (HMAC-SHA-256) statt reinem Hash, Zeilen-MAC gegen Manipulation, AES-256-GCM für Client-Datensätze.
/// Der Hauptschlüssel stammt aus der .env; daraus werden per HKDF getrennte Teilschlüssel abgeleitet. Nichts davon liegt in der Datenbank.
/// </summary>
public sealed class McpTokenProtector
{
    private sealed record KeySet(string KeyId, byte[] Hash, byte[] Mac, byte[] Enc);

    private readonly List<KeySet> _keys;   // [0] = aktiv

    public McpTokenProtector(AppConfig cfg)
    {
        if (cfg.McpTokenKey is null || cfg.McpTokenKey.Length < McpTokenKeys.MinBytes)
            throw new InvalidOperationException("MCP_TOKEN_KEY fehlt oder ist zu kurz (ADR 0025).");
        _keys = new() { Derive(cfg.McpTokenKey) };
        foreach (var previous in cfg.McpTokenKeysPrevious) _keys.Add(Derive(previous));
        LegacyUntil = cfg.McpLegacyTokenUntil;
    }

    /// <summary>Bis zu diesem Zeitpunkt werden Altbestands-Tokens (reines SHA-256) beim ersten Gebrauch umgestellt, danach abgelehnt.</summary>
    public DateTime LegacyUntil { get; }

    public string ActiveKeyId => _keys[0].KeyId;

    private static KeySet Derive(byte[] master)
    {
        static byte[] Key(byte[] ikm, string info) => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt: null, info: Encoding.UTF8.GetBytes(info));
        var kid = Convert.ToHexString(SHA256.HashData(Concat(Encoding.UTF8.GetBytes("learny/mcp/key-id/v1"), master)))[..8].ToLowerInvariant();
        return new KeySet(kid, Key(master, "learny/mcp/token-hash/v1"), Key(master, "learny/mcp/row-mac/v1"), Key(master, "learny/mcp/client-enc/v1"));
    }

    // ─── Hashes (für Datenbanksuche) ────────────────────────────────────────

    /// <summary>HMAC des Tokens mit dem aktiven Schlüssel (Hex). Ohne den Schlüssel lässt sich kein passender Eintrag berechnen.</summary>
    public string HashToken(string token) => Hmac(_keys[0].Hash, "lcm-token-v1", token);
    public string HashCode(string code) => Hmac(_keys[0].Hash, "lcm-code-v1", code);

    /// <summary>Kandidaten-Hashes für die Suche: aktiver Schlüssel zuerst, dann Vorgängerschlüssel (Rotation).</summary>
    public IReadOnlyList<(string KeyId, string Hash)> TokenCandidates(string token) => _keys.Select(k => (k.KeyId, Hmac(k.Hash, "lcm-token-v1", token))).ToList();
    public IReadOnlyList<(string KeyId, string Hash)> CodeCandidates(string code) => _keys.Select(k => (k.KeyId, Hmac(k.Hash, "lcm-code-v1", code))).ToList();

    /// <summary>Reiner SHA-256 des Altbestands (nur für die befristete Umstellung).</summary>
    public static string LegacyHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Hmac(byte[] key, string purpose, string value) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(purpose + "\0" + value))).ToLowerInvariant();

    // ─── Zeilen-MAC ─────────────────────────────────────────────────────────

    /// <summary>MAC über die kanonisch kodierten Felder (längenpräfixiert, damit Feldgrenzen eindeutig sind).</summary>
    public string RowMac(string? keyId, string table, params string?[] fields) => Convert.ToHexString(HMACSHA256.HashData(Find(keyId).Mac, Canonical(table, fields))).ToLowerInvariant();

    /// <summary>Konstantzeit-Vergleich des gespeicherten MAC. Unbekannte Schlüssel-ID oder leerer MAC → false.</summary>
    public bool VerifyRowMac(string? keyId, string? expectedHex, string table, params string?[] fields)
    {
        if (string.IsNullOrEmpty(expectedHex) || !_keys.Any(k => k.KeyId == keyId)) return false;
        byte[] expected;
        try { expected = Convert.FromHexString(expectedHex); } catch (FormatException) { return false; }
        var actual = HMACSHA256.HashData(Find(keyId).Mac, Canonical(table, fields));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public bool KnowsKey(string? keyId) => _keys.Any(k => k.KeyId == keyId);

    /// <summary>Zeitpunkt für den MAC: ganze Sekunden (UTC) — stabil gegenüber der Zeitgenauigkeit der Datenbank.</summary>
    public static string Ts(DateTime? value) => value is null ? "" : new DateTimeOffset(DateTime.SpecifyKind(value.Value, value.Value.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : value.Value.Kind)).ToUnixTimeSeconds().ToString();

    private static byte[] Canonical(string table, string?[] fields)
    {
        using var ms = new MemoryStream();
        void Write(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            ms.Write(BitConverter.GetBytes(bytes.Length));
            ms.Write(bytes);
        }
        Write("learny/mcp/row/v1");
        Write(table);
        foreach (var f in fields) Write(f ?? "");
        return ms.ToArray();
    }

    // ─── Verschlüsselung (OAuth-Clients) ────────────────────────────────────

    /// <summary>AES-256-GCM; <paramref name="aad"/> (z. B. die client_id) bindet den Datensatz an seinen Schlüsselwert. Ergebnis: "v1.&lt;nonce&gt;.&lt;ciphertext+tag&gt;" (Base64url).</summary>
    public string Encrypt(string plaintext, string aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(_keys[0].Enc, 16);
        gcm.Encrypt(nonce, data, cipher, tag, Encoding.UTF8.GetBytes(aad));
        return "v1." + B64(nonce) + "." + B64(Concat(cipher, tag));
    }

    /// <summary>Entschlüsselt mit dem Schlüssel <paramref name="keyId"/>; bei Manipulation, falschem AAD oder fremdem Schlüssel → null.</summary>
    public string? Decrypt(string? keyId, string? protectedValue, string aad)
    {
        if (string.IsNullOrEmpty(protectedValue) || !_keys.Any(k => k.KeyId == keyId)) return null;
        var parts = protectedValue.Split('.');
        if (parts.Length != 3 || parts[0] != "v1") return null;
        try
        {
            var nonce = FromB64(parts[1]);
            var blob = FromB64(parts[2]);
            if (nonce.Length != 12 || blob.Length < 16) return null;
            var cipher = blob[..^16];
            var tag = blob[^16..];
            var plain = new byte[cipher.Length];
            using var gcm = new AesGcm(Find(keyId).Enc, 16);
            gcm.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(aad));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { return null; }
    }

    // ─── Hilfen ─────────────────────────────────────────────────────────────

    private KeySet Find(string? keyId) => _keys.FirstOrDefault(k => k.KeyId == keyId) ?? _keys[0];
    private static byte[] Concat(byte[] a, byte[] b) { var r = new byte[a.Length + b.Length]; a.CopyTo(r, 0); b.CopyTo(r, a.Length); return r; }
    private static string B64(byte[] d) => Convert.ToBase64String(d).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromB64(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4)); }
}
