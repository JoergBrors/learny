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
        var pem = ReadPem(_cfg);
        if (pem is not null)
        {
            _key = ImportKey(pem);
            _keyId = ComputeKeyId(_key);
            if (_cfg.BadgeKeyPem.Length == 0) CheckOrWriteMarker(_cfg.BadgeKeyFile, _keyId);
        }
        else
        {
            // Kein Schlüssel vorhanden. Nur eine NEUE Installation darf einen Schlüssel erzeugen. Hat diese Installation bereits einen Schlüssel erzeugt
            // (Marker neben der Schlüsseldatei), ist ein fehlender Schlüssel ein Verlust: abbrechen statt still zu rotieren, sonst würden alle Badges ungültig.
            var markerFile = MarkerPath(_cfg.BadgeKeyFile);
            if (File.Exists(markerFile))
                throw new InvalidOperationException($"Der Badge-Signaturschlüssel fehlt ({Path.GetFileName(_cfg.BadgeKeyFile)}), obwohl diese Installation bereits initialisiert wurde (Marker vorhanden). " +
                    "Es wird bewusst KEIN neuer Schlüssel erzeugt, damit ausgestellte Badges gültig bleiben. Schlüssel aus der Sicherung wiederherstellen oder BADGE_SIGNING_KEY_PEM/BADGE_SIGNING_KEY_FILE setzen (docs/iis-deployment.md).");
            _key = CreateKeyFileAtomically(_cfg.BadgeKeyFile, out var created);
            GeneratedKeyFile = created;
            _keyId = ComputeKeyId(_key);
            CheckOrWriteMarker(_cfg.BadgeKeyFile, _keyId);
        }
        // Betriebsprüfung: Der Schlüssel muss tatsächlich signieren und verifizieren können (Start bricht sonst ab — keine stille Deaktivierung).
        var probe = Encoding.ASCII.GetBytes("learncards-selftest");
        if (!_key.VerifyData(probe, _key.SignData(probe, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidOperationException("Der Badge-Signaturschlüssel wurde geladen, besteht aber die Signatur-Selbstprüfung nicht.");
    }

    private static string ComputeKeyId(ECDsa key)
    {
        var p = key.ExportParameters(false);
        return Convert.ToHexString(SHA256.HashData(Concat(p.Q.X!, p.Q.Y!)))[..16].ToLowerInvariant();
    }

    /// <summary>Marker „Installation initialisiert“ neben der Schlüsseldatei: enthält nur Schlüssel-ID und Zeitpunkt (kein Geheimnis).</summary>
    public static string MarkerPath(string keyFile) => keyFile + ".initialized";

    private static void CheckOrWriteMarker(string keyFile, string keyId)
    {
        var marker = MarkerPath(keyFile);
        try
        {
            if (File.Exists(marker))
            {
                var recorded = JsonNode.Parse(File.ReadAllText(marker))?["key_id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(recorded) && recorded != keyId)
                    throw new InvalidOperationException($"Der Badge-Signaturschlüssel ({keyId}) passt nicht zur Initialisierung dieser Installation ({recorded}). Ein anderer Schlüssel würde ausgestellte Badges ungültig machen. " +
                        "Richtigen Schlüssel wiederherstellen; nur bei bewusster Rotation den Marker entfernen (docs/iis-deployment.md).");
                return;
            }
            var json = new JsonObject { ["key_id"] = keyId, ["created_at"] = DateTime.UtcNow.ToString("o") }.ToJsonString();
            using var fs = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.Write(Encoding.UTF8.GetBytes(json));
        }
        catch (IOException) { /* paralleler Start hat den Marker bereits angelegt */ }
        catch (UnauthorizedAccessException) { /* Marker ist optional lesbar/schreibbar; fehlende Schreibrechte verhindern den Start nicht */ }
    }

    /// <summary>
    /// Erzeugt den Schlüssel in einer temporären Datei im selben Verzeichnis und verschiebt sie ohne Überschreiben an den Zielort (atomar, parallele Starts sicher).
    /// Existiert das Ziel inzwischen, gewinnt der bereits vorhandene Schlüssel. Danach wird der Schlüssel aus der Datei neu geladen.
    /// </summary>
    private static ECDsa CreateKeyFileAtomically(string path, out bool created)
    {
        created = false;
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, ".badge-key-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var gen = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                fs.Write(Encoding.ASCII.GetBytes(gen.ExportPkcs8PrivateKeyPem()));
            if (OperatingSystem.IsWindows()) RestrictAcl(tmp);
            else File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            try { File.Move(tmp, full, overwrite: false); created = true; }
            catch (IOException) when (File.Exists(full)) { /* anderer Start war schneller */ }
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        return ImportKey(File.ReadAllText(full));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictAcl(string path)
    {
        try
        {
            var sec = new System.Security.AccessControl.FileSecurity();
            sec.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { System.Security.Principal.WindowsIdentity.GetCurrent().User!, new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null), new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null) })
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(sec);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException or IOException) { /* ACL-Härtung ist Zusatz; das Deployment prüft die Rechte erneut */ }
    }

    private static string? ReadPem(BlockchainSettings cfg)
    {
        try
        {
            if (cfg.BadgeKeyPem.Length > 0) return cfg.BadgeKeyPem.Contains("-----BEGIN") ? cfg.BadgeKeyPem : File.ReadAllText(cfg.BadgeKeyPem);
            return File.Exists(cfg.BadgeKeyFile) ? File.ReadAllText(cfg.BadgeKeyFile) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nur Art des Fehlers nennen — nie Schlüsselinhalt.
            throw new InvalidOperationException($"Die Schlüsseldatei für die Badge-Signierung ist nicht lesbar ({ex.GetType().Name}). Prüfe Pfad und Leserechte des Anwendungspools (BADGE_SIGNING_KEY_FILE / BADGE_SIGNING_KEY_PEM).", ex);
        }
    }

    /// <summary>
    /// Importiert den privaten P-256-Schlüssel. Unter Windows führt <c>ImportFromPem</c>/<c>ImportPkcs8PrivateKey</c> über den CNG-Schlüsseldienst (NCrypt), der ohne
    /// geladenes Benutzerprofil (IIS-Anwendungspool mit „Load User Profile = false“) mit „Das System kann die angegebene Datei nicht finden“ fehlschlagen kann.
    /// Deshalb wird der Schlüssel zuerst selbst aus PKCS#8/SEC1 in <see cref="ECParameters"/> zerlegt und flüchtig im Prozess importiert; erst danach der Standardweg.
    /// </summary>
    public static ECDsa ImportKey(string pem)
    {
        Exception? first = null;
        try { if (TryParseParameters(pem, out var prm)) return ECDsa.Create(prm); }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or System.Formats.Asn1.AsnContentException) { first = ex; }
        try
        {
            var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            k.ImportFromPem(pem);
            return k;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException(
                "Der Badge-Signaturschlüssel konnte nicht geladen werden (" + (first ?? ex).GetType().Name + "). Erwartet wird ein ECDSA-P-256-Schlüssel als PKCS#8-PEM. " +
                "Unter IIS zusätzlich: Anwendungspool → Erweiterte Einstellungen → „Benutzerprofil laden“ = True und Leserecht auf die Schlüsseldatei (docs/iis-deployment.md).", ex);
        }
    }

    private static readonly string P256Oid = "1.2.840.10045.3.1.7";

    private static bool TryParseParameters(string pem, out ECParameters prm)
    {
        prm = default;
        if (!PemEncoding.TryFind(pem, out var f)) return false;
        var label = pem[f.Label];
        var der = Convert.FromBase64String(pem[f.Base64Data].ToString());
        try
        {
            var r = new System.Formats.Asn1.AsnReader(der, System.Formats.Asn1.AsnEncodingRules.BER);
            var seq = r.ReadSequence();
            if (label == "PRIVATE KEY")
            {
                seq.ReadInteger();
                var alg = seq.ReadSequence();
                alg.ReadObjectIdentifier();
                if (alg.ReadObjectIdentifier() != P256Oid) return false;
                var ecKey = seq.ReadOctetString();
                var inner = new System.Formats.Asn1.AsnReader(ecKey, System.Formats.Asn1.AsnEncodingRules.BER).ReadSequence();
                return ReadEcPrivateKey(inner, out prm);
            }
            return label == "EC PRIVATE KEY" && ReadEcPrivateKey(seq, out prm);
        }
        finally { Array.Clear(der); }
    }

    private static bool ReadEcPrivateKey(System.Formats.Asn1.AsnReader seq, out ECParameters prm)
    {
        prm = default;
        seq.ReadInteger();
        var d = seq.ReadOctetString();
        byte[]? pub = null;
        while (seq.HasData)
        {
            var tag = seq.PeekTag();
            if (tag.TagClass == System.Formats.Asn1.TagClass.ContextSpecific && tag.TagValue == 1)
            {
                var c = seq.ReadSequence(tag);
                pub = c.ReadBitString(out _);
            }
            else seq.ReadEncodedValue();
        }
        if (d.Length is 0 or > 32 || pub is not { Length: 65 } || pub[0] != 4) return false; // ohne öffentlichen Anteil: Standardweg
        var dd = new byte[32]; d.CopyTo(dd, 32 - d.Length);
        prm = new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = dd, Q = new ECPoint { X = pub[1..33], Y = pub[33..65] } };
        return true;
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
            if (kid is null || !(kid.EndsWith("#" + _keyId, StringComparison.Ordinal) || kid.EndsWith("/badges/keys/" + _keyId, StringComparison.Ordinal))) return new(false, "Die Schlüssel-ID gehört nicht zu diesem Aussteller.", payload, kid);
            return new(true, null, payload, kid);
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException or ArgumentException)
        {
            return new(false, "Die Signatur konnte nicht gelesen werden.", null, null);
        }
    }

    /// <summary>
    /// Prüft eine kompakte JWS (ES256) mit einem mitgelieferten öffentlichen Schlüssel (JWK, z. B. aus einem PDF) — Offline-Prüfung der Unversehrtheit. Das beweist
    /// <b>nicht</b>, dass der Schlüssel dem Aussteller gehört: Das Ergebnis darf nur als „Inhalt zur Signatur passend“ gelten; die Echtheit des Schlüssels bestätigt allein
    /// der Aussteller (<c>/.well-known/jwks.json</c>).
    /// </summary>
    public static JwsVerification VerifyWithJwk(string jws, JsonNode? jwk)
    {
        try
        {
            if (jwk?["kty"]?.GetValue<string>() != "EC" || jwk["crv"]?.GetValue<string>() != "P-256") return new(false, "Kein ES256-Schlüssel (EC P-256).", null, null);
            var parts = jws.Trim().Split('.');
            if (parts.Length != 3) return new(false, "Keine kompakte JWS (3 Teile erwartet).", null, null);
            var header = JsonNode.Parse(FromB64(parts[0]));
            var kid = header?["kid"]?.GetValue<string>();
            if (header?["alg"]?.GetValue<string>() != "ES256") return new(false, "Nur ES256 wird akzeptiert.", null, kid);
            var payload = JsonNode.Parse(FromB64(parts[1]));
            var sig = FromB64Bytes(parts[2]);
            if (sig.Length != 64) return new(false, "Signaturlänge ungültig.", payload, kid);
            using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = FromB64Bytes(jwk["x"]!.GetValue<string>()), Y = FromB64Bytes(jwk["y"]!.GetValue<string>()) } });
            return key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                ? new(true, null, payload, kid) : new(false, "Die Signatur stimmt nicht mit dem Inhalt überein.", payload, kid);
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException or ArgumentException or NullReferenceException or InvalidOperationException)
        {
            return new(false, "Die Signatur oder der Schlüssel konnte nicht gelesen werden.", null, null);
        }
    }

    /// <summary>Nutzlast (Teil 2) einer kompakten JWS als JSON-Text — ohne Signaturprüfung (nur für bereits geprüfte, gespeicherte Badges).</summary>
    public static string PayloadText(string jws) => FromB64(jws.Split('.')[1]);

    /// <summary>JWT-Registered-Claims, die das Profil neben dem Credential in die Nutzlast schreibt (OB 3.0 §8.2.4.1) und beim Rekonstruieren des Credentials wieder entfernt.</summary>
    private static readonly string[] RegisteredClaims = { "iss", "jti", "nbf", "sub" };

    /// <summary>
    /// Signiert ein Credential im Profil „VC-JWT nach Open Badges 3.0 / CLR 2.0 §JSON Web Token Proof Format“ (ADR 0037): JOSE-Header nur <c>alg</c>, <c>kid</c>, <c>typ: JWT</c> (keine weiteren Header),
    /// Nutzlast = <c>iss</c> (issuer.id), <c>jti</c> (id), <c>nbf</c> (validFrom), <c>sub</c> (credentialSubject.id) und das Credential im Claim <c>vc</c>. ES256 bleibt der Algorithmus (die Spezifikation fordert RS256 als Minimum;
    /// siehe docs/credential-validation.md). Das ältere Profil (<see cref="Sign"/>, <c>typ: vc+jwt</c>) bleibt für bereits ausgestellte Credentials prüfbar.
    /// </summary>
    public string SignCredential(JsonObject credential, string kidUrl)
    {
        var validFrom = DateTimeOffset.Parse(credential["validFrom"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
        // Nutzlast wie in den 1EdTech-Referenzbeispielen (CLR 2.0 simple.jwt): Registered Claims plus das Credential im Claim "vc".
        // Der CLR-Validator liest das Credential ausschließlich aus "vc" (er setzt JWT_CREDENTIAL_ALLOW_WHOLE_PAYLOAD nicht).
        var payload = new JsonObject
        {
            ["iss"] = credential["issuer"]?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Credential ohne issuer.id."),
            ["jti"] = credential["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Credential ohne id."),
            ["nbf"] = validFrom.ToUnixTimeSeconds(),
            ["sub"] = credential["credentialSubject"]?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Credential ohne credentialSubject.id."),
            ["vc"] = credential.DeepClone(),
        };
        var header = new JsonObject { ["alg"] = "ES256", ["kid"] = kidUrl, ["typ"] = "JWT" };
        var signingInput = B64(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + B64(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var sig = _key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + B64(sig);
    }

    /// <summary>Das Credential aus der Nutzlast (ohne die vom Profil ergänzten Registered Claims) — ohne Signaturprüfung; nur für gespeicherte, bereits geprüfte Credentials.</summary>
    public static JsonObject CredentialFromJws(string jws)
    {
        var obj = (JsonObject)JsonNode.Parse(PayloadText(jws))!;
        if (obj["vc"] is JsonObject vc) return (JsonObject)vc.DeepClone();   // Profil ADR 0038: Credential im Claim "vc"
        if (!IsLegacyProfile(jws)) foreach (var c in RegisteredClaims) obj.Remove(c);
        return obj;
    }

    /// <summary>Wahr bei dem älteren Profil (<c>typ: vc+jwt</c>, <c>cty: vc</c>, Hülle <c>JwtProof2020</c>), das nicht dem OB-3.0-JWT-Profil entspricht und nur noch für Altbestand gilt.</summary>
    public static bool IsLegacyProfile(string jws)
    {
        try { return JsonNode.Parse(FromB64(jws.Split('.')[0]))?["typ"]?.GetValue<string>() == "vc+jwt"; }
        catch (Exception e) when (e is FormatException or JsonException or IndexOutOfRangeException) { return true; }
    }

    /// <summary>Algorithmus, Schlüssel-ID und Profil aus dem JOSE-Header (nur Anzeige im Register; keine Prüfung).</summary>
    public static (string? Alg, string? Kid, string Profile) HeaderInfo(string jws)
    {
        try
        {
            var h = JsonNode.Parse(FromB64(jws.Split('.')[0]));
            return (h?["alg"]?.GetValue<string>(), h?["kid"]?.GetValue<string>(), h?["typ"]?.GetValue<string>() == "vc+jwt" ? "legacy-vc+jwt" : "ob3-jwt");
        }
        catch (Exception e) when (e is FormatException or JsonException or IndexOutOfRangeException) { return (null, null, "unknown"); }
    }

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
