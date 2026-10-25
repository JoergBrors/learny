using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services.Certificates;

/// <summary>Ergebnis der Prüfung einer kompakten JWS.</summary>
public sealed record JwsVerification(bool SignatureValid, string? Error, JsonNode? Payload, string? Kid);

/// <summary>
/// Signiert und prüft Open-Badge-Nachweise als VC-JWT (kompakte JWS, ES256 = ECDSA P-256/SHA-256) — mit den Bordmitteln von .NET, ohne Fremdpaket (ADR 0021).
/// Der private Schlüssel stammt aus <c>BADGE_SIGNING_KEY_PEM</c> (PKCS#8, Secret-Verwaltung/Key Vault) oder <c>BADGE_SIGNING_KEY_FILE</c>; nur in Entwicklung wird
/// eine Schlüsseldatei im Datenverzeichnis erzeugt (nie in der Datenbank, nie in Logs). Der öffentliche Schlüssel wird als JWK veröffentlicht.
/// </summary>
public sealed class BadgeSigner : IDisposable
{
    private readonly ECDsa _key;
    private readonly string _keyId;
    private readonly BlockchainSettings _cfg;

    public BadgeSigner(AppConfig config)
    {
        _cfg = config.Blockchain;
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (_cfg.BadgeKeyPem.Length > 0) _key.ImportFromPem(_cfg.BadgeKeyPem.Contains("-----BEGIN") ? _cfg.BadgeKeyPem : File.ReadAllText(_cfg.BadgeKeyPem));
        else if (File.Exists(_cfg.BadgeKeyFile)) _key.ImportFromPem(File.ReadAllText(_cfg.BadgeKeyFile));
        else
        {
            // Kein Schlüssel konfiguriert: einmalig erzeugen und im (persistenten) Datenverzeichnis ablegen — nicht im Repository, nicht in der Datenbank.
            // Für Produktion BADGE_SIGNING_KEY_PEM aus einer Secret-Verwaltung setzen (der Start meldet eine Warnung).
            GeneratedKeyFile = true;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cfg.BadgeKeyFile))!);
            File.WriteAllText(_cfg.BadgeKeyFile, _key.ExportPkcs8PrivateKeyPem());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_cfg.BadgeKeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var p = _key.ExportParameters(false);
        _keyId = Convert.ToHexString(SHA256.HashData(Concat(p.Q.X!, p.Q.Y!)))[..16].ToLowerInvariant();
    }

    /// <summary>Wahr, wenn der Schlüssel beim Start in eine Datei im Datenverzeichnis erzeugt wurde (kein BADGE_SIGNING_KEY_PEM gesetzt).</summary>
    public bool GeneratedKeyFile { get; private set; }

    public string KeyId => _keyId;

    /// <summary>Öffentlicher Schlüssel als JWK (kein privater Anteil).</summary>
    public JsonObject PublicJwk()
    {
        var p = _key.ExportParameters(false);
        return new JsonObject { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = B64(p.Q.X!), ["y"] = B64(p.Q.Y!), ["use"] = "sig", ["alg"] = "ES256", ["kid"] = _keyId };
    }

    public string Sign(JsonNode payload, string kidUrl)
    {
        var header = new JsonObject { ["alg"] = "ES256", ["typ"] = "vc+jwt", ["cty"] = "vc", ["kid"] = kidUrl };
        var signingInput = B64(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + B64(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var sig = _key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + B64(sig);
    }

    /// <summary>
    /// Prüft eine kompakte JWS gegen den Aussteller-Schlüssel. Nur ES256 wird akzeptiert (kein „alg: none“, keine Algorithmen-Verwechslung);
    /// die Schlüssel-ID muss zum Aussteller passen.
    /// </summary>
    public JwsVerification Verify(string jws)
    {
        try
        {
            var parts = jws.Trim().Split('.');
            if (parts.Length != 3) return new(false, "Keine kompakte JWS (3 Teile erwartet).", null, null);
            var header = JsonNode.Parse(FromB64(parts[0]));
            var alg = header?["alg"]?.GetValue<string>();
            var kid = header?["kid"]?.GetValue<string>();
            if (alg != "ES256") return new(false, $"Algorithmus '{alg}' wird nicht akzeptiert (nur ES256).", null, kid);
            var payload = JsonNode.Parse(FromB64(parts[1]));
            var sig = FromB64Bytes(parts[2]);
            if (sig.Length != 64) return new(false, "Signaturlänge ungültig.", payload, kid);
            var ok = _key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!ok) return new(false, "Die Signatur stimmt nicht mit dem Inhalt überein (manipuliert oder fremder Aussteller).", payload, kid);
            if (kid is null || !kid.EndsWith("#" + _keyId, StringComparison.Ordinal)) return new(false, "Die Schlüssel-ID gehört nicht zu diesem Aussteller.", payload, kid);
            return new(true, null, payload, kid);
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException or ArgumentException)
        {
            return new(false, "Die Signatur konnte nicht gelesen werden.", null, null);
        }
    }

    /// <summary>Nutzlast (Teil 2) einer kompakten JWS als JSON-Text — ohne Signaturprüfung (nur für bereits geprüfte, gespeicherte Badges).</summary>
    public static string PayloadText(string jws) => FromB64(jws.Split('.')[1]);

    public static string HashHex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromB64Bytes(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        t = t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=');
        return Convert.FromBase64String(t);
    }
    private static string FromB64(string s) => Encoding.UTF8.GetString(FromB64Bytes(s));
    private static byte[] Concat(byte[] a, byte[] b) => a.Concat(b).ToArray();

    public void Dispose() => _key.Dispose();
}
