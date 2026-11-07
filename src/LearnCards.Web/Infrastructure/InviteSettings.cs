using System.Security.Cryptography;

namespace LearnCards.Web.Infrastructure;

/// <summary>
/// Konfiguration der einladungspflichtigen Registrierung (ADR 0049). Der Token-Schlüssel liegt nur in der Geheimnisverwaltung (.env/Umgebung), nie in der Datenbank:
/// Einladungstoken werden ausschließlich als HMAC-SHA256 gespeichert. Ist External ID aktiv, ist der Schlüssel Pflicht (fail-closed, ADR 0026).
/// </summary>
public sealed class InviteSettings
{
    public const int MinKeyBytes = 32;
    public byte[] TokenKey { get; init; } = Array.Empty<byte>();
    /// <summary>Öffentliche Basisadresse der Anwendung für Einladungslinks (PUBLIC_BASE_URL); leer = Einladungen können keine Links bilden.</summary>
    public string LinkBaseUrl { get; init; } = "";
    /// <summary>Standardgültigkeit in Stunden (1–720).</summary>
    public int ValidityHours { get; init; } = 72;

    public bool Enabled => TokenKey.Length >= MinKeyBytes && LinkBaseUrl.Length > 0;

    public static InviteSettings Resolve(Func<string, string> env, bool required, bool isDevelopment, string dataDir, string publicBaseUrl)
    {
        var hours = int.TryParse(env("INVITE_VALIDITY_HOURS"), out var h) ? h : 72;
        if (hours is < 1 or > 720) throw new InvalidOperationException("INVITE_VALIDITY_HOURS muss zwischen 1 und 720 liegen.");

        var link = (publicBaseUrl ?? "").Trim().TrimEnd('/');
        if (link.Length == 0 && isDevelopment) link = "http://localhost:5000";
        if (link.Length > 0)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var u) || u.Query.Length > 0 || !(u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)))
                throw new InvalidOperationException("PUBLIC_BASE_URL muss für Einladungslinks eine HTTPS-Adresse sein (HTTP nur auf Loopback).");
        }

        byte[] key = Array.Empty<byte>();
        var configured = env("INVITE_TOKEN_KEY");
        if (!string.IsNullOrWhiteSpace(configured)) key = Decode(configured, "INVITE_TOKEN_KEY");
        else if (isDevelopment && required) key = DevKey(dataDir);
        else if (required)
            throw new InvalidOperationException("INVITE_TOKEN_KEY fehlt (Pflicht, solange EXTID_ENABLED gesetzt ist). Der Schlüssel schützt die Einladungslinks (HMAC); erzeugen: openssl rand -base64 32 — nur in der .env bzw. Geheimnisverwaltung ablegen (ADR 0049).");
        if (required && link.Length == 0)
            throw new InvalidOperationException("PUBLIC_BASE_URL fehlt (Pflicht, solange EXTID_ENABLED gesetzt ist): Einladungslinks brauchen die öffentliche Adresse.");
        return new InviteSettings { TokenKey = key, LinkBaseUrl = link, ValidityHours = hours };
    }

    private static byte[] DevKey(string dataDir)
    {
        var path = Path.Combine(dataDir, "invite-token.key");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(dataDir);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, Convert.ToBase64String(RandomNumberGenerator.GetBytes(MinKeyBytes)));
            try { File.Move(tmp, path, overwrite: false); }
            catch (IOException) { /* paralleler Start war schneller */ }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
        }
        return Decode(File.ReadAllText(path).Trim(), "data/invite-token.key");
    }

    private static byte[] Decode(string value, string name)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value.Trim()); }
        catch (FormatException) { throw new InvalidOperationException($"{name} ist kein gültiges Base64."); }
        if (bytes.Length < MinKeyBytes) throw new InvalidOperationException($"{name} ist zu kurz (mindestens {MinKeyBytes} Byte, Base64 kodiert).");
        return bytes;
    }
}
