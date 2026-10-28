using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using LearnCards.Web.Data;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services.Pdf;
using LearnCards.Web.Services.Pki;

namespace LearnCards.Web.Services.Certificates;

/// <summary>
/// Prüfung eines hochgeladenen Zertifikats-PDFs (ADR 0033). Die Befunde werden getrennt ausgewiesen: (1) PDF-Signatur, (2) Datei seit der Signierung unverändert, (3) CLR,
/// (4) Master-Badge, (5) Modul-Credentials, (6) Referenzen und Hashes, (7) Vertrauen in Aussteller/Unterzeichner, (8) Widerruf, (9) Versionen. Eine gültige CLR-Signatur
/// legitimiert kein verändertes PDF und umgekehrt. Das PDF wird nur im Speicher verarbeitet und nie gespeichert.
/// </summary>
public sealed class PdfVerificationService
{
    public const int MaxPdfBytes = 25 * 1024 * 1024;
    private readonly CertificateService _certs;
    private readonly PdfSigningProvider _signing;
    private readonly IDatabase _db;
    private readonly PkiVerifier _pki;

    public PdfVerificationService(CertificateService certs, PdfSigningProvider signing, IDatabase db, PkiVerifier pki) { _certs = certs; _signing = signing; _db = db; _pki = pki; }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    private static string S(JsonNode? n, string path)
    {
        var cur = n;
        foreach (var part in path.Split('.')) cur = cur is JsonArray a ? (int.TryParse(part, out var i) && i < a.Count ? a[i] : null) : (cur as JsonObject)?[part];
        return cur?.ToString() ?? "";
    }
    private static JsonNode? TryParse(byte[]? data) { try { return data is null ? null : JsonNode.Parse(data); } catch (System.Text.Json.JsonException) { return null; } }

    public async Task<JsonObject> VerifyAsync(byte[] pdf)
    {
        var insp = PdfInspector.Inspect(pdf);
        var report = new JsonObject();
        var problems = new JsonArray();
        foreach (var p in insp.Problems) problems.Add(p);

        // ─── 1/2: PDF-Signatur und Unversehrtheit ───
        var pdfNode = new JsonObject
        {
            ["is_pdf"] = insp.IsPdf, ["size"] = insp.Length, ["signature_present"] = insp.SignaturePresent, ["signature_count"] = insp.SignatureCount, ["sub_filter"] = insp.SubFilter,
            ["signature_valid"] = insp.Cms is { Parsed: true, SignatureValid: true }, ["document_unchanged"] = insp.ByteRangeCoversFile && insp.DigestMatches,
            ["covers_attachments"] = insp.ByteRangeCoversFile && insp.DigestMatches,   // die Anhänge liegen in der signierten Byte-Folge
            ["signature_error"] = insp.Cms?.Error, ["signature_algorithm"] = insp.Cms?.SignatureAlgorithm, ["signing_time"] = insp.Cms?.SigningTime ?? insp.ClaimedSigningTime,
        };
        var signerTrust = "none";
        PkiVerification? pkiV = null;
        if (insp.Cms?.Signer is { } signer)
        {
            var known = (await _db.QueryAsync("SELECT 1 FROM certificate_pdfs WHERE signer_thumbprint = @t LIMIT 1", new[] { ("t", (object?)signer.Thumbprint) })).Count > 0 || signer.Thumbprint == _signing.Thumbprint;
            bool chainTrusted = false; string chainStatus = "";
            try
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                foreach (var extra in insp.Cms.Certificates) if (extra.Thumbprint != signer.Thumbprint) chain.ChainPolicy.ExtraStore.Add(extra);
                chain.ChainPolicy.VerificationTime = insp.Cms.SigningTime ?? DateTime.UtcNow;
                var built = chain.Build(signer);
                chainTrusted = built && chain.ChainStatus.All(s => s.Status is X509ChainStatusFlags.NoError) && chain.ChainElements.Count > 1 && !(signer.Subject == signer.Issuer);
                chainStatus = string.Join(", ", chain.ChainStatus.Select(s => s.Status.ToString()));
            }
            catch (CryptographicException) { chainStatus = "Kette nicht prüfbar"; }
            try { pkiV = await _pki.VerifyAsync(signer, insp.Cms.Certificates, insp.Cms.SigningTime); } catch (CryptographicException) { pkiV = null; }
            signerTrust = pkiV is { IssuerConfirmed: true } ? "learny_pki" : known ? "installation" : chainTrusted ? "trusted_ca" : "untrusted";
            pdfNode["signer"] = new JsonObject
            {
                ["subject"] = signer.Subject, ["issuer"] = signer.Issuer, ["thumbprint"] = signer.Thumbprint, ["serial"] = signer.SerialNumber, ["not_before"] = signer.NotBefore.ToUniversalTime(), ["not_after"] = signer.NotAfter.ToUniversalTime(),
                ["self_signed"] = signer.Subject == signer.Issuer, ["key_algorithm"] = signer.GetRSAPublicKey() is not null ? "RSA" : "ECDSA",
                ["valid_at_signing"] = (insp.Cms.SigningTime ?? DateTime.UtcNow) >= signer.NotBefore.ToUniversalTime() && (insp.Cms.SigningTime ?? DateTime.UtcNow) <= signer.NotAfter.ToUniversalTime(),
                ["signed_by_this_installation"] = known, ["chain_trusted"] = chainTrusted, ["chain_status"] = chainStatus,
            };
        }
        pdfNode["signer_trust"] = signerTrust;   // learny_pki | installation | trusted_ca | untrusted | none
        if (pkiV is not null)
            pdfNode["pki"] = new JsonObject
            {
                ["cryptographically_valid"] = insp.Cms is { Parsed: true, SignatureValid: true }, ["chain_valid"] = pkiV.ChainValid, ["profile_ok"] = pkiV.ProfileOk, ["issuer_confirmed"] = pkiV.IssuerConfirmed,
                ["revocation"] = pkiV.Revocation, ["crl_fresh"] = pkiV.CrlFresh, ["revoked_at"] = pkiV.RevokedAt, ["revocation_reason"] = pkiV.RevocationReason,
                ["publicly_trusted"] = false, ["problems"] = new JsonArray(pkiV.Problems.Select(x => (JsonNode?)x).ToArray()),
            };
        report["pdf"] = pdfNode;

        // ─── Anhänge ───
        var files = insp.Attachments.GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.First());
        var vNode = files.TryGetValue("verification.json", out var vf) ? TryParse(vf.Data) as JsonObject : null;
        var fileList = new JsonArray(); var filesOk = vNode is not null;
        var expected = (vNode?["hashes"]?["embedded_files"]?.AsArray() ?? new JsonArray()).ToDictionary(e => S(e, "name"), e => S(e, "sha256"));
        foreach (var (name, a) in files)
        {
            var exp = expected.GetValueOrDefault(name);
            var ok = name == "verification.json" || (exp is not null && string.Equals(exp, a.Sha256, StringComparison.Ordinal));
            if (!ok) filesOk = false;
            fileList.Add(new JsonObject { ["name"] = name, ["size"] = a.Data.Length, ["sha256"] = a.Sha256, ["expected_sha256"] = name == "verification.json" ? null : exp, ["matches"] = ok });
        }
        foreach (var missing in expected.Keys.Where(k => !files.ContainsKey(k))) { filesOk = false; fileList.Add(new JsonObject { ["name"] = missing, ["sha256"] = null, ["expected_sha256"] = expected[missing], ["matches"] = false }); }
        report["attachments"] = new JsonObject { ["count"] = files.Count, ["verification_json_present"] = vNode is not null, ["hashes_match"] = filesOk, ["files"] = fileList };

        // ─── 3/4/5: Credentials ───
        var jwk = files.TryGetValue("public-key.jwk", out var kf) ? TryParse(kf.Data) : null;
        var kind = S(vNode, "kind");
        var refs = new JsonArray(); var refsOk = vNode is not null;
        void Ref(string what, bool ok, string detail = "") { if (!ok) refsOk = false; refs.Add(new JsonObject { ["check"] = what, ["ok"] = ok, ["detail"] = detail.Length > 0 ? detail : null }); }

        async Task<JsonObject?> CheckCredential(string file, string label, string? expectedJwsHash)
        {
            if (!files.TryGetValue(file, out var f)) return null;
            var text = Encoding.UTF8.GetString(f.Data);
            var node = TryParse(f.Data);
            var v = await _certs.VerifyAsync(text);
            var jws = S(node, "proof.jwt");
            if (jws.Length == 0 && files.TryGetValue(Path.ChangeExtension(file, ".jwt"), out var jf)) jws = Encoding.ASCII.GetString(jf.Data).Trim();   // neues Profil: die JWS liegt als .jwt-Datei bei
            var offline = jws.Length > 0 && jwk is not null ? BadgeSigner.VerifyWithJwk(jws, jwk) : null;
            var hashOk = jws.Length > 0 && (expectedJwsHash is null || string.Equals(BadgeSigner.HashHex(jws), expectedJwsHash, StringComparison.OrdinalIgnoreCase));
            return new JsonObject
            {
                ["present"] = true, ["label"] = label, ["id"] = S(node, "id"), ["valid"] = v.Valid, ["signature_valid"] = v.SignatureValid, ["signature_error"] = v.SignatureError, ["content_matches"] = v.ContentMatches,
                ["issuer_valid"] = v.IssuerValid, ["known"] = v.Known, ["revoked"] = v.Revoked, ["revoked_reason"] = v.RevokedReason, ["certificate_id"] = v.CertificateId, ["number"] = v.Number,
                ["jws_hash_matches"] = hashOk, ["offline_signature_valid"] = offline?.SignatureValid, ["offline_note"] = offline is null ? null : "Mit dem im PDF mitgelieferten Schlüssel geprüft – die Echtheit des Schlüssels bestätigt nur der Aussteller (jwks_url).",
                ["achievement_version"] = S(node, "credentialSubject.achievement.version"),
            };
        }

        var credentials = new JsonObject();
        var anyRevoked = false; var credsValid = true;
        if (kind == "topic")
        {
            var clr = await CheckCredential("clr-credential.json", "CLR", S(vNode, "hashes.clr_jws_sha256"));
            var master = await CheckCredential("master-badge.json", "Master-Badge", S(vNode, "hashes.master_badge_jws_sha256"));
            credentials["clr"] = clr ?? new JsonObject { ["present"] = false }; credentials["master_badge"] = master ?? new JsonObject { ["present"] = false };
            var mods = new JsonArray();
            var modBundle = files.TryGetValue("module-credentials.json", out var mf) ? TryParse(mf.Data) : null;
            var expectedMods = (vNode?["hashes"]?["referenced_credentials"]?.AsArray() ?? new JsonArray()).ToDictionary(r => S(r, "id"), r => S(r, "jws_sha256"));
            // Eingebettete Credentials: neues Profil = kompakte JWS (Zeichenkette), älteres Profil = Objekt mit Hülle
            var embeddedRaw = (TryParse(files.TryGetValue("clr-credential.json", out var cf) ? cf.Data : null)?["credentialSubject"]?["verifiableCredential"]?.AsArray() ?? new JsonArray()).ToList();
            var embeddedJwsById = new Dictionary<string, string>();
            var embeddedInClr = embeddedRaw.Select(e =>
            {
                if (e is JsonValue ev && ev.TryGetValue<string>(out var ej)) { var o = BadgeSigner.CredentialFromJws(ej); embeddedJwsById[S(o, "id")] = ej; return (JsonNode?)o; }
                return e;
            }).ToList();
            foreach (var c in modBundle?["credentials"]?.AsArray() ?? new JsonArray())
            {
                var text = c!.ToJsonString();
                var v = await _certs.VerifyAsync(text);
                var id = S(c, "id");
                var jws = S(c, "proof.jwt");
                if (jws.Length == 0 && embeddedJwsById.TryGetValue(id, out var ejws)) jws = ejws;
                mods.Add(new JsonObject
                {
                    ["id"] = id, ["name"] = S(c, "credentialSubject.achievement.name"), ["version"] = S(c, "credentialSubject.achievement.version"), ["valid"] = v.Valid, ["signature_valid"] = v.SignatureValid, ["content_matches"] = v.ContentMatches, ["known"] = v.Known, ["revoked"] = v.Revoked,
                    ["jws_hash_matches"] = expectedMods.TryGetValue(id, out var h) && string.Equals(h, jws.Length > 0 ? BadgeSigner.HashHex(jws) : v.BadgeHash, StringComparison.OrdinalIgnoreCase),   // ohne Hülle: Hash der bei Learny gespeicherten, signierten JWS (Treffer über die Credential-ID)
                    ["identical_to_clr_copy"] = embeddedInClr.Any(e => JsonNode.DeepEquals(e, c)),
                });
                if (v.Revoked) anyRevoked = true;
                if (!v.Valid) credsValid = false;
            }
            credentials["modules"] = mods;
            credsValid &= clr is not null && master is not null && S(clr, "valid") == "true" && S(master, "valid") == "true";
            anyRevoked |= S(clr, "revoked") == "true" || S(master, "revoked") == "true";

            // ─── 6: Referenzen und Hashes ───
            var clrNode = TryParse(files.TryGetValue("clr-credential.json", out var cf2) ? cf2.Data : null);
            var masterNode = TryParse(files.TryGetValue("master-badge.json", out var mf2) ? mf2.Data : null);
            var embeddedIds = embeddedInClr.Select(e => S(e, "id")).ToHashSet();
            Ref("Hash der CLR-Signatur entspricht verification.json", S(clr, "jws_hash_matches") == "true");
            Ref("Hash der Master-Badge-Signatur entspricht verification.json", S(master, "jws_hash_matches") == "true");
            Ref("Master-Badge ist im CLR eingebettet (identisch)", masterNode is not null && embeddedInClr.Any(e => JsonNode.DeepEquals(e, masterNode)));
            Ref("Alle Modul-Credentials sind im CLR eingebettet (identisch zu module-credentials.json)", mods.Count > 0 && mods.All(m => S(m, "identical_to_clr_copy") == "true") && embeddedInClr.Count == mods.Count + 1);
            Ref("Hashes der Modul-Credentials entsprechen verification.json", mods.All(m => S(m, "jws_hash_matches") == "true") && mods.Count == expectedMods.Count);
            var ids = (vNode?["credentials"]?["module_credential_ids"]?.AsArray() ?? new JsonArray()).Select(i => i!.GetValue<string>()).ToList();
            Ref("Kennungen in verification.json stimmen mit den Credentials überein", ids.Count == mods.Count && ids.All(i => embeddedIds.Contains(i)) && S(vNode, "credentials.master_badge_id") == S(masterNode, "id") && S(vNode, "credentials.clr_credential_id") == S(clrNode, "id"));
            var snapText = files.TryGetValue("competence-snapshot.json", out var sf) ? sf.Sha256 : "";
            Ref("Hash des Kompetenzsnapshots entspricht verification.json", S(vNode, "hashes.competence_snapshot_sha256").Length == 0 ? snapText.Length == 0 : snapText.Length > 0 && string.Equals(snapText, S(vNode, "hashes.competence_snapshot_sha256"), StringComparison.Ordinal));   // Testnachweise haben keinen Snapshot
            Ref("Aussteller von CLR und Badge stimmt überein", S(clrNode, "issuer.id") == S(masterNode, "issuer.id") && S(clrNode, "issuer.id") == S(vNode, "issuer.id"));

            // ─── 9: Versionen ───
            var versions = new[] { S(vNode, "topic.version"), S(masterNode, "credentialSubject.achievement.version"), S(clrNode, "credentialSubject.achievement.0.version") };
            var cert = Guid.TryParse(S(vNode, "certificate_id"), out _) ? await _certs.GetRawAsync(S(vNode, "certificate_id")) : null;
            var versionsOk = versions.Distinct().Count() == 1 && versions[0].Length > 0 && (cert is null || cert.ModuleVersion.ToString() == versions[0]);
            report["versions"] = new JsonObject { ["match"] = versionsOk, ["topic_version_verification_json"] = versions[0], ["topic_version_master_badge"] = versions[1], ["topic_version_clr"] = versions[2], ["topic_version_registered"] = cert?.ModuleVersion.ToString() };
        }
        else
        {
            var mod = await CheckCredential("module-credential.json", "Modulzertifikat", S(vNode, "hashes.badge_jws_sha256"));
            credentials["module_credential"] = mod ?? new JsonObject { ["present"] = false };
            credsValid = mod is not null && S(mod, "valid") == "true";
            anyRevoked = S(mod, "revoked") == "true";
            Ref("Hash der Signatur entspricht verification.json", S(mod, "jws_hash_matches") == "true");
            var snapHash = files.TryGetValue("competence-snapshot.json", out var sf) ? sf.Sha256 : "";
            var node = TryParse(files.TryGetValue("module-credential.json", out var mcf) ? mcf.Data : null);
            Ref("Hash des Kompetenzsnapshots entspricht verification.json", S(vNode, "hashes.competence_snapshot_sha256").Length == 0 ? snapHash.Length == 0 : snapHash == S(vNode, "hashes.competence_snapshot_sha256"));
            Ref("Kennung in verification.json stimmt mit dem Credential überein", S(vNode, "credentials.badge_id") == S(node, "id"));
            var cert = Guid.TryParse(S(vNode, "certificate_id"), out _) ? await _certs.GetRawAsync(S(vNode, "certificate_id")) : null;
            var ver = S(vNode, "module.version");
            report["versions"] = new JsonObject { ["match"] = ver == S(node, "credentialSubject.achievement.version") || (ver == "0" && S(node, "credentialSubject.achievement.version").Length == 0) && (cert is null || cert.ModuleVersion.ToString() == ver), ["module_version_verification_json"] = ver, ["module_version_registered"] = cert?.ModuleVersion.ToString() };
        }
        report["credentials"] = credentials;
        report["references"] = new JsonObject { ["all_ok"] = refsOk, ["checks"] = refs };

        // ─── 7/8: Aussteller und Widerruf ───
        var issuerOk = vNode is not null && (S(vNode, "issuer.id") == _certs.IssuerId || S(vNode, "issuer.id").EndsWith("/badges/issuer", StringComparison.Ordinal));
        report["issuer"] = new JsonObject
        {
            ["id"] = S(vNode, "issuer.id"), ["matches_this_installation"] = issuerOk, ["credentials_signed_by_this_installation"] = credsValid,
            ["jwks_url"] = S(vNode, "signature.jwks_url"), ["key_id"] = S(vNode, "signature.key_id"),
            ["note"] = credsValid ? "Die Credentials sind mit dem Schlüssel dieser Installation signiert." : "Die Credentials ließen sich nicht mit dem Schlüssel dieser Installation bestätigen.",
        };
        report["revocation"] = new JsonObject { ["checked_online"] = true, ["any_revoked"] = anyRevoked };

        var pdfOk = S(pdfNode, "signature_valid") == "true" && S(pdfNode, "document_unchanged") == "true";
        var signerRevoked = pkiV is { IssuerConfirmed: true, Revocation: "revoked" };
        var valid = pdfOk && filesOk && credsValid && refsOk && !anyRevoked && !signerRevoked && (S(report["versions"], "match") == "true");
        report["problems"] = problems;
        report["overall"] = new JsonObject
        {
            ["valid"] = valid,
            ["pdf_signature_ok"] = pdfOk, ["attachments_ok"] = filesOk, ["credentials_ok"] = credsValid, ["references_ok"] = refsOk, ["revoked"] = anyRevoked, ["signer_revoked"] = signerRevoked, ["versions_ok"] = S(report["versions"], "match") == "true",
            ["signer_trust"] = signerTrust,
            ["summary"] = valid ? (signerTrust == "learny_pki" ? "Gültig: PDF-Signatur, Dateiinhalt, Credentials, Hashes und Versionen stimmen überein; das PDF wurde mit einem Zertifikat der Learny-PKI signiert (keine öffentliche Anerkennung durch Adobe oder Betriebssysteme)."
                    : signerTrust == "installation" ? "Gültig: PDF-Signatur, Dateiinhalt, Credentials, Hashes und Versionen stimmen überein; das PDF wurde von dieser Installation signiert."
                    : "Technisch gültig: Signaturen, Hashes und Versionen stimmen überein, der PDF-Unterzeichner ist jedoch nicht als diese Installation bekannt – Vertrauen hängt vom Zertifikat des Unterzeichners ab.")
                : signerRevoked ? "Nicht gültig: Das Signaturzertifikat der Learny-PKI ist widerrufen."
                : anyRevoked ? "Nicht gültig: Ein Nachweis ist widerrufen."
                : !pdfOk ? "Nicht gültig: PDF-Signatur fehlt/ungültig oder die Datei wurde nach der Signierung verändert."
                : !filesOk ? "Nicht gültig: Ein eingebetteter Anhang stimmt nicht mit der Prüfsumme überein oder fehlt."
                : !credsValid ? "Nicht gültig: Ein eingebettetes Credential ist ungültig."
                : "Nicht gültig: Referenzen, Hashes oder Versionen stimmen nicht überein.",
        };
        return report;
    }
}
