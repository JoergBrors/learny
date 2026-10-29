using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using LearnCards.Web.Domain;
using LearnCards.Web.Services.Pdf;

namespace LearnCards.Web.Services.Certificates;

/// <summary>
/// Signiertes Integritätsmanifest für Zertifikats-PDFs (ADR 0039): <c>integrity-manifest.json</c> (exakte Bytes maßgeblich) listet alle Nutzdateien mit Größe, Medientyp und SHA-256 der
/// Originalbytes; <c>integrity-manifest.jws</c> ist die ES256-JWS über genau diese Bytes. Das Manifest enthält weder sich selbst noch JWS, OTS-Datei oder Blockchain-Status.
/// </summary>
public static class IntegrityManifest
{
    public const string ManifestName = "integrity-manifest.json", JwsName = "integrity-manifest.jws", OtsName = "integrity-manifest.jws.ots";
    public const string JwsTyp = "learny-integrity-manifest+jws", JwsCty = "application/json";
    public static readonly string[] ArtifactNames = { ManifestName, JwsName, OtsName };

    public static string[] RequiredFiles(string kind) => kind == "topic"
        ? new[] { "clr-credential.json", "clr-credential.jwt", "master-badge.json", "master-badge.jwt", "module-credentials.json", "module-competence-snapshots.json", "public-key.jwk", "verification.json" }
        : new[] { "module-credential.json", "module-credential.jwt", "public-key.jwk", "verification.json" };

    public static readonly string[] OptionalFiles = { "competence-snapshot.json" };

    private static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Erzeugt die Manifestbytes (UTF-8 ohne BOM, LF). <paramref name="files"/> enthält nur Nutzdateien, nie die Manifest-Artefakte.</summary>
    public static byte[] Build(Certificate cert, string credentialUrn, string issuerId, DateTime issuedAtUtc, JsonNode publicJwk, string kidUrl, string jwksUrl, IEnumerable<PdfEmbeddedFile> files)
    {
        var list = files.Where(f => !ArtifactNames.Contains(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        if (list.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != list.Count) throw new InvalidOperationException("Doppelte Dateinamen im Paket.");
        var kind = cert.IsTopic ? "topic" : "module";
        var manifest = new JsonObject
        {
            ["format"] = "learny-integrity-manifest", ["manifest_version"] = 1,
            ["credential_id"] = cert.Id, ["certificate_number"] = cert.Number, ["certificate_kind"] = kind, ["credential_urn"] = credentialUrn,
            ["issuer_id"] = issuerId, ["issued_at"] = issuedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["hash_algorithm"] = "SHA-256",
            ["issuer_key"] = new JsonObject { ["kid"] = kidUrl, ["key_id"] = publicJwk["kid"]?.DeepClone(), ["jwk_thumbprint"] = BadgeSigner.JwkThumbprint(publicJwk), ["jwks_url"] = jwksUrl },
            ["rules"] = new JsonObject
            {
                ["required_files"] = new JsonArray(RequiredFiles(kind).Select(n => (JsonNode)n).ToArray()),
                ["allowed_optional_files"] = new JsonArray(OptionalFiles.Select(n => (JsonNode)n).ToArray()),
                ["additional_files_allowed"] = false,
                ["excluded_from_manifest"] = new JsonArray(ArtifactNames.Select(n => (JsonNode)n).ToArray()),
            },
            ["files"] = new JsonArray(list.Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["size"] = f.Data.Length, ["media_type"] = f.MimeType, ["sha256"] = Sha256Hex(f.Data) }).ToArray()),
        };
        return new UTF8Encoding(false).GetBytes(manifest.ToJsonString(Indented).Replace("\r\n", "\n") + "\n");
    }

    /// <summary>
    /// Prüft Manifest, Manifest-JWS und Dateien getrennt (ein erfolgreicher Teil überdeckt nie einen Fehler eines anderen). <paramref name="embeddedJwk"/> ist der mitgelieferte Schlüssel,
    /// <paramref name="installationJwk"/> der Schlüssel dieser Installation (null = unbekannt, z. B. Offline-Prüfung).
    /// </summary>
    public static JsonObject Verify(IReadOnlyDictionary<string, byte[]> files, JsonNode? embeddedJwk, JsonNode? installationJwk)
    {
        var r = new JsonObject();
        files.TryGetValue(ManifestName, out var manifestBytes); files.TryGetValue(JwsName, out var jwsBytes);
        var present = manifestBytes is not null && jwsBytes is not null;
        r["present"] = manifestBytes is not null || jwsBytes is not null; r["complete"] = present;
        if (!present)
        {
            r["status"] = manifestBytes is null && jwsBytes is null ? "absent" : "invalid";
            r["problems"] = new JsonArray(manifestBytes is null && jwsBytes is null ? "Kein Integritätsmanifest (älteres Paket)." : "Manifest und Manifest-JWS sind nur unvollständig vorhanden.");
            return r;
        }
        var problems = new JsonArray();
        void P(string s) => problems.Add(s);
        JsonNode? m = null;
        try { m = JsonNode.Parse(manifestBytes!); } catch (System.Text.Json.JsonException) { P("Manifest ist kein gültiges JSON."); }
        r["manifest_parsed"] = m is JsonObject && m["format"]?.GetValue<string>() == "learny-integrity-manifest";
        if (r["manifest_parsed"]!.GetValue<bool>() is false) P("Manifestformat unbekannt.");

        // Manifest-JWS: Signatur, Typ, Nutzlast = Manifestbytes, Schlüssel
        var jws = Encoding.ASCII.GetString(jwsBytes!).Trim();
        var jv = BadgeSigner.VerifyBytesWithJwk(jws, embeddedJwk);
        var payloadMatches = jv.PayloadBytes is not null && manifestBytes!.AsSpan().SequenceEqual(jv.PayloadBytes);
        r["jws"] = new JsonObject { ["signature_valid"] = jv.SignatureValid, ["error"] = jv.Error, ["typ_ok"] = jv.Typ == JwsTyp, ["payload_matches_manifest"] = payloadMatches, ["kid"] = jv.Kid, ["sha256"] = Sha256Hex(jwsBytes!) };
        if (!jv.SignatureValid) P("Manifest-JWS: " + (jv.Error ?? "Signatur ungültig."));
        if (jv.Typ != JwsTyp) P("Manifest-JWS hat einen unerwarteten Typ.");
        if (jv.SignatureValid && !payloadMatches) P("Die Nutzlast der Manifest-JWS entspricht nicht den Bytes von integrity-manifest.json.");

        // Aussteller-Schlüssel: Thumbprint im Manifest ↔ eingebettetes JWK; Vertrauensstufe
        var thumbEmbedded = embeddedJwk is null ? null : TryThumb(embeddedJwk);
        var thumbManifest = m?["issuer_key"]?["jwk_thumbprint"]?.GetValue<string>();
        var thumbInstall = installationJwk is null ? null : TryThumb(installationJwk);
        var keyMatchesManifest = thumbEmbedded is not null && thumbEmbedded == thumbManifest;
        if (!keyMatchesManifest) P("Das eingebettete öffentliche JWK entspricht nicht dem im Manifest genannten Schlüssel.");
        r["issuer_key"] = new JsonObject
        {
            ["thumbprint_embedded"] = thumbEmbedded, ["thumbprint_manifest"] = thumbManifest, ["matches_manifest"] = keyMatchesManifest,
            // mathematisch gültig ≠ Aussteller bestätigt: Bestätigung nur, wenn der Schlüssel dem dieser Installation entspricht (oder einem vom Prüfer gesicherten Anker)
            ["trust"] = !jv.SignatureValid || !keyMatchesManifest ? "invalid" : thumbInstall is not null && thumbInstall == thumbEmbedded ? "installation" : "unconfirmed",
        };

        // Dateiinventar
        var listed = (m?["files"] as JsonArray ?? new JsonArray()).Select(f => (Name: f?["name"]?.GetValue<string>() ?? "", Size: f?["size"]?.GetValue<long>() ?? -1, Sha: f?["sha256"]?.GetValue<string>() ?? "")).ToList();
        var protectedNames = listed.Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
        var rows = new JsonArray(); var allOk = true;
        foreach (var l in listed)
        {
            var found = files.TryGetValue(l.Name, out var data);
            var ok = found && Sha256Hex(data!) == l.Sha && data!.Length == l.Size;
            if (!ok) { allOk = false; P(found ? $"{l.Name}: Hash oder Größe weicht vom Manifest ab." : $"{l.Name}: laut Manifest vorhanden, fehlt im PDF."); }
            rows.Add(new JsonObject { ["name"] = l.Name, ["size"] = l.Size, ["sha256"] = l.Sha, ["found"] = found, ["matches"] = ok });
        }
        var extra = files.Keys.Where(n => !protectedNames.Contains(n) && !ArtifactNames.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        foreach (var e in extra) P($"{e}: zusätzlicher Anhang, nicht vom Manifest geschützt.");
        var kind = m?["certificate_kind"]?.GetValue<string>() == "topic" ? "topic" : "module";
        var missingRequired = RequiredFiles(kind).Where(n => !protectedNames.Contains(n)).ToList();
        foreach (var n in missingRequired) P($"{n}: Pflichtdatei fehlt im Manifest.");
        r["files"] = new JsonObject { ["listed"] = rows, ["hashes_match"] = allOk, ["unlisted_attachments"] = new JsonArray(extra.Select(e => (JsonNode)e).ToArray()), ["missing_required"] = new JsonArray(missingRequired.Select(e => (JsonNode)e).ToArray()) };

        var ok2 = r["manifest_parsed"]!.GetValue<bool>() && jv.SignatureValid && payloadMatches && jv.Typ == JwsTyp && keyMatchesManifest && allOk && extra.Count == 0 && missingRequired.Count == 0;
        r["manifest_credential_id"] = m?["credential_id"]?.DeepClone();
        r["status"] = ok2 ? "valid" : "invalid"; r["problems"] = problems;
        return r;
    }

    private static string? TryThumb(JsonNode jwk) { try { return BadgeSigner.JwkThumbprint(jwk); } catch (Exception e) when (e is NullReferenceException or InvalidOperationException) { return null; } }
}
