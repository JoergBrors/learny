using System.Security.Cryptography;

namespace LearnCards.Web.Infrastructure;

/// <summary>
/// Hauptschlüssel für MCP-Tokens, Einmal-Codes und OAuth-Clients (ADR 0025). Der Schlüssel liegt nur in der Geheimnisverwaltung der Anwendung (.env/Umgebung),
/// nie in der Datenbank oder in deren Sicherungen. Pflicht: Ohne gültigen Schlüssel startet die Anwendung außerhalb von Development nicht.
/// </summary>
public static class McpTokenKeys
{
    public const int MinBytes = 32;

    /// <summary>Liest <c>MCP_TOKEN_KEY</c> (Base64, mindestens 32 Byte). In Development wird ohne Angabe einmalig <c>data/mcp-token.key</c> erzeugt.</summary>
    public static byte[] Resolve(string? configured, bool isDevelopment, string dataDir)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Decode(configured, "MCP_TOKEN_KEY");
        if (!isDevelopment)
            throw new InvalidOperationException("MCP_TOKEN_KEY fehlt (Pflicht). Der Schlüssel schützt MCP-Tokens und OAuth-Clients in der Datenbank. Erzeugen: openssl rand -base64 32 — und nur in der .env bzw. Geheimnisverwaltung ablegen, nie in der Datenbank oder im Repository (ADR 0025).");

        // Development: persistenter, lokaler Schlüssel (atomar erzeugt, nie überschrieben)
        var path = Path.Combine(dataDir, "mcp-token.key");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(dataDir);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, Convert.ToBase64String(RandomNumberGenerator.GetBytes(MinBytes)));
            try { File.Move(tmp, path, overwrite: false); }
            catch (IOException) { /* paralleler Start war schneller */ }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
        }
        return Decode(File.ReadAllText(path).Trim(), "data/mcp-token.key");
    }

    /// <summary>Vorgängerschlüssel (kommagetrennt) — nur zum Prüfen/Entschlüsseln nach einer Rotation.</summary>
    public static List<byte[]> ResolvePrevious(string? configured) =>
        (configured ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => Decode(v, "MCP_TOKEN_KEY_PREVIOUS")).ToList();

    private static byte[] Decode(string value, string name)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value.Trim()); }
        catch (FormatException) { throw new InvalidOperationException($"{name} ist kein gültiges Base64."); }
        if (bytes.Length < MinBytes) throw new InvalidOperationException($"{name} ist zu kurz (mindestens {MinBytes} Byte, Base64 kodiert).");
        return bytes;
    }
}
