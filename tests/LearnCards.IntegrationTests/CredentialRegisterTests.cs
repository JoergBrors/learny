using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LearnCards.Web.Services.Certificates;

/// <summary>
/// ADR 0037/0038: JWT-Profil der Credentials (unabhängig vom Learny-Verifier geprüft), administrative Testausstellung, Ausstellungsregister, gezielter und idempotenter Widerruf,
/// Berechtigungen. Läuft gegen den echten Server (Dev-Modus, Administrator = local-dev-user).
/// </summary>
public static class CredentialRegisterTests
{
    private static string S(JsonNode? n, string path)
    {
        var cur = n;
        foreach (var part in path.Split('.')) cur = cur is JsonArray a ? (int.TryParse(part, out var i) && i < a.Count ? a[i] : null) : (cur as JsonObject)?[part];
        return cur?.ToString() ?? "";
    }

    private static async Task<JsonNode?> J(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync());

    private static byte[] B64(string s) { var b = s.Replace('-', '+').Replace('_', '/'); b += new string('=', (4 - b.Length % 4) % 4); return Convert.FromBase64String(b); }

    /// <summary>Prüft ein VC-JWT gegen die Vorgaben des Abschnitts „JSON Web Token Proof Format“ von Open Badges 3.0 (§8.2.3/8.2.4.1) — ohne Learny-Verifier. Leere Liste = konform.</summary>
    public static List<string> JwtProfileProblems(string jws, JsonNode jwks)
    {
        var problems = new List<string>();
        var p = jws.Split('.');
        if (p.Length != 3) return new() { "keine kompakte JWS" };
        var header = JsonNode.Parse(B64(p[0]))!.AsObject(); var outer = JsonNode.Parse(B64(p[1]))!.AsObject();
        // Credential im Claim "vc" (1EdTech-Referenzbeispiele; der CLR-Validator liest nur dort); Altbestand/Fehlprofil: oberste Ebene
        var payload = outer["vc"] as JsonObject ?? outer;
        if (outer["vc"] is null) problems.Add("Claim vc fehlt (Credential nicht im JWT-Claim vc)");
        if (S(header, "alg") != "ES256" && S(header, "alg") != "RS256") problems.Add("alg weder RS256 noch ES256");
        if (header.ContainsKey("typ") && S(header, "typ") != "JWT") problems.Add($"typ muss \"JWT\" sein, ist \"{S(header, "typ")}\"");
        if (!header.ContainsKey("kid") && !header.ContainsKey("jwk")) problems.Add("kid oder jwk fehlt");
        foreach (var k in header.Select(kv => kv.Key).Where(k => k is not ("alg" or "typ" or "kid" or "jwk"))) problems.Add($"zusätzlicher JOSE-Header \"{k}\" nicht erlaubt");
        if (S(outer, "iss") != S(payload, "issuer.id") || S(outer, "iss").Length == 0) problems.Add("iss fehlt oder ≠ issuer.id");
        if (S(outer, "jti") != S(payload, "id") || S(outer, "jti").Length == 0) problems.Add("jti fehlt oder ≠ id");
        if (S(outer, "sub") != S(payload, "credentialSubject.id") || S(outer, "sub").Length == 0) problems.Add("sub fehlt oder ≠ credentialSubject.id");
        if (!outer.ContainsKey("nbf") || DateTimeOffset.FromUnixTimeSeconds(outer["nbf"]!.GetValue<long>()).UtcDateTime != DateTime.Parse(S(payload, "validFrom"), null, System.Globalization.DateTimeStyles.AdjustToUniversal)) problems.Add("nbf fehlt oder ≠ validFrom");
        // Signatur unabhängig mit dem veröffentlichten JWK prüfen (ES256, IEEE-P1363)
        var kid = S(header, "kid"); var frag = kid.Contains('#') ? kid[(kid.IndexOf('#') + 1)..] : kid[(kid.LastIndexOf('/') + 1)..];
        var key = jwks["keys"]!.AsArray().FirstOrDefault(k => S(k, "kid") == frag);
        if (key is null) problems.Add("kid ist über das JWKS nicht auflösbar");
        else if (S(header, "alg") == "ES256")
        {
            using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = B64(S(key, "x")), Y = B64(S(key, "y")) } });
            if (!ec.VerifyData(Encoding.ASCII.GetBytes(p[0] + "." + p[1]), B64(p[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) problems.Add("ES256-Signatur ungültig");
        }
        return problems;
    }


    /// <summary>Lokale Strukturprüfung nach dem offiziellen CLR-2.0-JSON-Schema (clr_v2p0_clrcredential_schema.json): Pflichtfelder von ClrCredential, ClrSubject und VerifiableCredential, <c>verifiableCredential</c> nur als Objekte, <c>proof</c> nur als Proof-Objekt(e) mit <c>type</c> (nie der erfundene Typ JwtProof2020).</summary>
    public static List<string> ClrJsonSchemaProblems(JsonNode clr)
    {
        var problems = new List<string>();
        foreach (var k in new[] { "@context", "type", "id", "name", "credentialSubject", "issuer", "validFrom" }) if (clr[k] is null) problems.Add($"ClrCredential: {k} fehlt");
        var subject = clr["credentialSubject"] as JsonObject;
        if (subject is null) { problems.Add("credentialSubject kein Objekt"); return problems; }
        foreach (var k in new[] { "type", "verifiableCredential" }) if (subject[k] is null) problems.Add($"ClrSubject: {k} fehlt");
        if (subject["id"] is null && subject["identifier"] is null) problems.Add("ClrSubject: id oder identifier fehlt");
        var vcs = subject["verifiableCredential"] is JsonArray a ? a.ToList() : new List<JsonNode?> { subject["verifiableCredential"] };
        if (vcs.Count == 0) problems.Add("verifiableCredential: mindestens 1 Eintrag");
        foreach (var vc in vcs)
        {
            if (vc is not JsonObject o) { problems.Add("verifiableCredential-Eintrag ist kein Objekt (kompakte JWS nur im JWT-Payload erlaubt)"); continue; }
            foreach (var k in new[] { "@context", "type", "issuer", "validFrom", "credentialSubject" }) if (o[k] is null) problems.Add($"VerifiableCredential: {k} fehlt");
        }
        foreach (var holder in new[] { clr }.Concat(vcs.Where(v => v is JsonObject)))
        {
            var proof = holder!["proof"];
            if (proof is null) continue;
            foreach (var pr in proof is JsonArray pa ? pa.ToList() : new List<JsonNode?> { proof })
                if (pr is not JsonObject po || po["type"] is null || S(po, "type") == "JwtProof2020") problems.Add("proof ohne gültigen Proof-Typ");
        }
        return problems;
    }

    public static async Task RunAsync(Func<string?, Task<HttpClient>> login, Func<HttpClient> newClient, HttpClient admin, Action<bool, string> check)
    {
        Console.WriteLine("\nAusstellungsregister, Testausstellung, Widerruf, JWT-Profil (ADR 0037/0038)");
        // Die Missbrauchsbremse der öffentlichen Endpunkte zählt je Client-Adresse. Dieser Testlauf nutzt eine eigene Adresse (X-Forwarded-For wird von der Testinstanz ausgewertet):
        // so bleibt die produktive Bremse unverändert, und es ist keine Wartezeit nötig.
        var testIp = $"10.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}";
        HttpClient Pub() { var c = newClient(); c.DefaultRequestHeaders.Add("X-Forwarded-For", testIp); return c; }
        var user = await login("reg-user");
        var adminId = S(await J(await admin.GetAsync("/api/me")), "id");
        var adminName = S(await J(await admin.GetAsync("/api/me")), "display_name");
        var jwks = (await J(await Pub().GetAsync("/.well-known/jwks.json")))!;

        // Ursachenanalyse (Spezifikation, nicht Validator): das frühere Profil verletzt mehrere Vorgaben — der Prüfer erkennt sie
        {
            var legacyHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"ES256\",\"typ\":\"vc+jwt\",\"cty\":\"vc\",\"kid\":\"https://x/badges/issuer#k\"}")).TrimEnd('=');
            var legacyPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"id\":\"https://x/verify/1\",\"issuer\":{\"id\":\"https://x/badges/issuer\"},\"validFrom\":\"2026-01-01T00:00:00Z\",\"credentialSubject\":{}}")).TrimEnd('=');
            var problems = JwtProfileProblems($"{legacyHeader}.{legacyPayload}.AAAA", jwks);
            check(problems.Any(x => x.Contains("typ")) && problems.Any(x => x.Contains("cty")) && problems.Any(x => x.StartsWith("iss")) && problems.Any(x => x.StartsWith("jti")) && problems.Any(x => x.StartsWith("sub")) && problems.Any(x => x.StartsWith("nbf")),
                "JWT-Profil (Ursache): das ältere Profil (typ vc+jwt, cty, keine iss/jti/nbf/sub) verletzt OB 3.0 §8.2.3/8.2.4.1 — der unabhängige Prüfer meldet jeden Verstoß");
            check(BadgeSigner.IsLegacyProfile($"{legacyHeader}.{legacyPayload}.AAAA") && !BadgeSigner.CredentialFromJws($"{legacyHeader}.{legacyPayload}.AAAA").ContainsKey("proof"), "JWT-Profil: das ältere Profil wird als Altbestand erkannt und bleibt lesbar");
        }

        // Berechtigungen
        check((await user.PostAsJsonAsync("/api/admin/credentials/test", new { type = "badge" })).StatusCode == HttpStatusCode.Forbidden, "Register: normale Benutzer können keine Testnachweise ausstellen (403)");
        check((await user.GetAsync("/api/admin/credentials")).StatusCode == HttpStatusCode.Forbidden && (await user.GetAsync($"/api/admin/credentials/{Guid.NewGuid()}")).StatusCode == HttpStatusCode.Forbidden, "Register: Suche und Details nur für Administratoren (403)");
        check((await user.PostAsJsonAsync($"/api/admin/credentials/{Guid.NewGuid()}/revoke", new { reason = "x", confirm_number = "x" })).StatusCode == HttpStatusCode.Forbidden, "Register: Widerruf nur für Administratoren (403)");
        check((await Pub().GetAsync("/api/admin/credentials")).StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Forbidden, "Register: ohne Anmeldung nicht erreichbar");

        // Testausstellung: Badge
        var before = DateTime.UtcNow.AddSeconds(-2);
        var badgeRes = await admin.PostAsJsonAsync("/api/admin/credentials/test", new { type = "badge" });
        var badge = await J(badgeRes);
        var badgeId = S(badge, "id");
        check(badgeRes.IsSuccessStatusCode && S(badge, "number").StartsWith("TEST-") && S(badge, "is_test") == "true", "Testausstellung: Open Badge 3.0 (Modul) mit eigener TEST-Nachweisidentität");
        check((await admin.PostAsJsonAsync("/api/admin/credentials/test", new { type = "gibtsnicht" })).StatusCode == HttpStatusCode.BadRequest, "Testausstellung: unbekannter Typ abgewiesen");

        // JWT-Profil des neuen Credentials, unabhängig geprüft
        var badgeJws = (await admin.GetStringAsync($"/api/certificates/{badgeId}/badge.jwt")).Trim();
        var badgeJson = (await J(await admin.GetAsync($"/api/certificates/{badgeId}/badge")))!;
        check(JwtProfileProblems(badgeJws, jwks).Count == 0, "JWT-Profil: neues Credential konform (typ JWT, nur alg/kid/typ, iss/jti/nbf/sub, ES256 unabhängig gültig, kid über JWKS auflösbar)" + string.Join("; ", JwtProfileProblems(badgeJws, jwks)));
        {
            // Open Badges 3.0 — Validator-Verhalten (Quelltext ExternalProofProbe): die kid wird per HTTP abgerufen und die Antwort direkt als EINZELNES JWK gelesen (kty/x/y), nicht als JWKS oder Ausstellerprofil
            var kidUrl = S(JsonNode.Parse(Encoding.UTF8.GetString(B64(badgeJws.Split('.')[0]))), "kid");
            var kidRes = await Pub().GetAsync(new Uri(kidUrl).AbsolutePath);
            var jwk = JsonNode.Parse(await kidRes.Content.ReadAsStringAsync())!;
            check(kidRes.IsSuccessStatusCode && S(jwk, "kty") == "EC" && S(jwk, "crv") == "P-256" && S(jwk, "x").Length > 0 && S(jwk, "y").Length > 0 && jwk["keys"] is null, "kid: die Schlüssel-URL liefert genau ein JWK (EC P-256), keine JWKS-Liste und kein Ausstellerprofil");
            check(jwks["keys"]!.AsArray().Any(k => S(k, "x") == S(jwk, "x") && S(k, "y") == S(jwk, "y") && S(k, "kid") == S(jwk, "kid")), "kid: das JWK der kid-URL entspricht dem Eintrag im JWKS (eindeutige Zuordnung)");
            var jwksText = await Pub().GetStringAsync("/.well-known/jwks.json"); var issuerText = await Pub().GetStringAsync("/badges/issuer");
            check(!new[] { "\"d\"", "\"p\"", "\"q\"", "\"dp\"", "\"dq\"", "\"qi\"" }.Any(k => jwksText.Contains(k + ":") || issuerText.Contains(k + ":") || jwk.ToJsonString().Contains(k + ":")), "Sicherheit: weder JWKS noch Ausstellerprofil noch kid-JWK enthalten private Schlüsselbestandteile");
            check((await Pub().GetAsync("/badges/keys/gibt-es-nicht")).StatusCode != HttpStatusCode.OK, "kid: unbekannte Schlüssel-ID liefert keinen Schlüssel");
            // Manipulation: Header, Nutzlast und Signatur
            var parts = badgeJws.Split('.');
            string Mut(string b64) { var b = B64(b64); b[^1] ^= 1; return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
            string Swap(string b64, string from, string to) => Convert.ToBase64String(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(B64(b64)).Replace(from, to))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var tampered = new[] { parts[0] + "." + parts[1] + "." + Mut(parts[2]), Swap(parts[0], "ES256", "RS256") + "." + parts[1] + "." + parts[2], parts[0] + "." + Swap(parts[1], "TEST", "FAKE") + "." + parts[2] };
            var allRejected = true;
            foreach (var t in tampered) allRejected &= S(await J(await Pub().PostAsync("/api/public/verify", new StringContent(t))), "signature_valid") != "true";
            check(allRejected, "Manipulation: veränderte Signatur, veränderter Header (Algorithmus) und veränderte Nutzlast werden abgelehnt");
            // JSON-Download ist als unsignierte Datendarstellung gekennzeichnet, das signierte JWT wird benannt
            var jsonRes = await admin.GetAsync($"/api/certificates/{badgeId}/badge");
            check(jsonRes.Headers.TryGetValues("X-Credential-Representation", out var rep) && rep.First() == "unsigned-json" && jsonRes.Headers.GetValues("X-Credential-Signed-Form").First() == $"/api/certificates/{badgeId}/badge.jwt",
                "JSON-Download: als unsignierte Datendarstellung gekennzeichnet (Verweis auf das signierte JWT)");
            // Fingerprint stabil
            var fp1 = BadgeSigner.HashHex((await admin.GetStringAsync($"/api/certificates/{badgeId}/badge.jwt")).Trim()); var fp2 = BadgeSigner.HashHex(badgeJws);
            check(fp1 == fp2, "Fingerprint: wiederholte Downloads liefern dieselbe JWS (stabiler Fingerprint)");
            // Strukturprüfung nach Open Badges 3.0 für Modul- und Master-Badge
            var masterOb = BadgeSigner.CredentialFromJws((await admin.GetStringAsync($"/api/certificates/{(S(await J(await admin.PostAsJsonAsync("/api/admin/credentials/test", new { type = "topic" })), "id"))}/badge.jwt")).Trim());
            var errModule = TopicCredentialTests.CheckVc(BadgeSigner.CredentialFromJws(badgeJws), "module"); var errMaster = TopicCredentialTests.CheckVc(masterOb, "master");
            check(errModule.Count == 0 && errMaster.Count == 0, "Open-Badges-3.0-Struktur: Modul-Badge und Master-Badge erfüllen die Pflichtfelder" + string.Join("; ", errModule.Concat(errMaster)));
            check(S(masterOb, "credentialSubject.achievement.name").StartsWith("TEST") && masterOb["credentialSubject"]!["result"] is null && masterOb["credentialSubject"]!["achievement"]!["alignment"] is null, "Master-Test-Badge: als TEST gekennzeichnet, ohne Ergebnis und ohne Kompetenzen");
            // Prüfstatus in der Detailansicht: lokale Prüfung ≠ externe Prüfung, nicht erreichbarer Schlüssel ≠ ungültige Signatur
            var vv = (await J(await admin.GetAsync($"/api/admin/credentials/{badgeId}")))!["validation"]!;
            check(S(vv, "signature").StartsWith("Gültig") && S(vv, "key_access").Contains("nicht erreichbar") && S(vv, "key_access").Contains("kein Signaturfehler") && S(vv, "external").StartsWith("Nicht geprüft") && S(vv, "format").Contains("typ JWT"),
                "Admin-Prüfstatus: Signatur gültig, lokaler Schlüssel als „nicht erreichbar“ (kein Signaturfehler), externe Prüfung „nicht geprüft“");
        }
        check(badgeJson["proof"] is null && S(badgeJson, "credentialSubject.id").StartsWith("urn:uuid:") && !badgeJson.ToJsonString().Contains("JwtProof2020"), "JWT-Profil: JSON ohne erfundenen Proof-Typ (JwtProof2020), credentialSubject.id vorhanden");
        check(S(badgeJson, "name").StartsWith("TEST") && S(badgeJson, "credentialSubject.achievement.name").StartsWith("TEST") && badgeJson["credentialSubject"]!["result"] is null && badgeJson["credentialSubject"]!["achievement"]!["alignment"] is null,
            "Testnachweis: sichtbar mit TEST gekennzeichnet, ohne Ergebnis und ohne Kompetenzen");
        var verJws = await J(await Pub().PostAsync("/api/public/verify", new StringContent(badgeJws)));
        var verJson = await J(await Pub().PostAsync("/api/public/verify", new StringContent(badgeJson.ToJsonString())));
        check(S(verJws, "signature_valid") == "true" && S(verJws, "known") == "true" && S(verJson, "valid") == "true" && S(verJson, "content_matches") == "true", "Prüfung: JWT und proof-loses JSON werden gegen die gespeicherte Signatur geprüft");
        badgeJson["name"] = "Manipuliert";
        check(S(await J(await Pub().PostAsync("/api/public/verify", new StringContent(badgeJson.ToJsonString()))), "valid") == "false", "Prüfung: manipuliertes proof-loses JSON wird erkannt");

        // Test-Themenabschluss: Master-Badge + CLR + Modul-Badges
        var topic = await J(await admin.PostAsJsonAsync("/api/admin/credentials/test", new { type = "topic" }));
        var topicId = S(topic, "id");
        var clrJws = (await admin.GetStringAsync($"/api/certificates/{topicId}/clr.jwt")).Trim();
        var masterJws = (await admin.GetStringAsync($"/api/certificates/{topicId}/badge.jwt")).Trim();
        var clr = (await J(await admin.GetAsync($"/api/certificates/{topicId}/clr")))!;
        var embedded = clr["credentialSubject"]!["verifiableCredential"]!.AsArray();   // JSON-Darstellung: Credential-Objekte (CLR-2.0-JSON-Schema)
        var clrPayload = JsonNode.Parse(Encoding.UTF8.GetString(B64(clrJws.Split('.')[1])))!;
        var embeddedJws = clrPayload["vc"]!["credentialSubject"]!["verifiableCredential"]!.AsArray();   // JWT-Darstellung: kompakte JWS (ClrSubjectJwtPayload)
        check(JwtProfileProblems(clrJws, jwks).Count == 0 && JwtProfileProblems(masterJws, jwks).Count == 0, "Test-CLR/-Master-Badge: JWT-Profil konform" + string.Join("; ", JwtProfileProblems(clrJws, jwks)));
        var schemaProblems = ClrJsonSchemaProblems(clr);
        check(schemaProblems.Count == 0, "CLR-JSON entspricht dem CLR-2.0-JSON-Schema (Pflichtfelder, verifiableCredential = Objekte, kein erfundener Proof)" + string.Join("; ", schemaProblems));
        {
            // Reproduktion des gemeldeten Fehlers: JSON mit kompakten JWS-Strings unter verifiableCredential verletzt das CLR-JSON-Schema (nur das JWT-Payload-Schema erlaubt Strings)
            var stringStyle = JsonNode.Parse(clr.ToJsonString())!; stringStyle["credentialSubject"]!["verifiableCredential"] = JsonNode.Parse(embeddedJws.ToJsonString());
            check(ClrJsonSchemaProblems(stringStyle).Count(x => x.Contains("kein Objekt")) == 3, "CLR-JSON (Ursache): drei JWS-Strings statt Objekte werden vom Schema-Prüfer als Verstoß erkannt — das frühere Format");
        }
        check(embedded.Count == 3 && embedded.All(e => e is JsonObject) && S(clr, "proof") == "" && S(clr, "credentialSubject.id").StartsWith("urn:uuid:"), "CLR-JSON: drei eingebettete Credential-Objekte (Master-Badge und zwei Modul-Badges), keine Proof-Hülle");
        check(embeddedJws.Count == 3 && embeddedJws.All(e => e is JsonValue) && embeddedJws.All(e => JwtProfileProblems(e!.GetValue<string>(), jwks).Count == 0) && clrPayload["credentialSubject"] is null,
            "CLR-JWT: Credential im Claim vc, drei eingebettete kompakte JWS (ClrSubjectJwtPayload), jede unabhängig signaturgültig");
        check(Enumerable.Range(0, 3).All(i => JsonNode.DeepEquals(embedded[i], BadgeSigner.CredentialFromJws(embeddedJws[i]!.GetValue<string>()))), "CLR: JSON-Objekte und eingebettete JWS haben dieselbe Identität und denselben Inhalt (nichts erfunden)");
        var embeddedObjs = embeddedJws.Select(e => BadgeSigner.CredentialFromJws(e!.GetValue<string>())).ToList();
        check(embeddedObjs.Select(e => S(e, "credentialSubject.id")).Distinct().Count() == 1, "Test-CLR: alle Credentials haben dasselbe Subjekt");
        {
            // CLR-JSON ohne Proof wird gegen die gespeicherte JWS geprüft; ein verändertes eingebettetes Objekt wird erkannt
            var vOk = await J(await Pub().PostAsync("/api/public/verify", new StringContent(clr.ToJsonString())));
            var forged = JsonNode.Parse(clr.ToJsonString())!; forged["credentialSubject"]!["verifiableCredential"]![1]!["name"] = "Manipuliert";
            var vBad = await J(await Pub().PostAsync("/api/public/verify", new StringContent(forged.ToJsonString())));
            var vJwt = await J(await Pub().PostAsync("/api/public/verify", new StringContent(clrJws)));
            check(S(vOk, "valid") == "true" && S(vOk, "content_matches") == "true" && S(vJwt, "valid") == "true" && S(vBad, "valid") == "false" && S(vBad, "content_matches") == "false", "CLR: Prüfung von JWT und JSON; manipuliertes eingebettetes Credential wird erkannt");
        }

        var pdfModuleId = "";
        // Signiertes PDF zum Testnachweis (bestehende PDF-Signatur und -Prüfung bleiben unverändert wirksam)
        {
            async Task<JsonNode?> PdfVerifyAsync(byte[] data)
            {
                using var form = new MultipartFormDataContent(); var fc = new ByteArrayContent(data); fc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf"); form.Add(fc, "file", "t.pdf");
                var body = await (await Pub().PostAsync("/api/public/verify/pdf", form)).Content.ReadAsStringAsync();
                return body.TrimStart().StartsWith("{") ? JsonNode.Parse(body) : null;
            }
            var pdfT = await admin.GetAsync($"/api/certificates/{topicId}/certificate.pdf");
            var pdfBytes = await pdfT.Content.ReadAsByteArrayAsync();
            var vp = pdfT.IsSuccessStatusCode ? await PdfVerifyAsync(pdfBytes) : null;
            check(pdfT.IsSuccessStatusCode && pdfBytes.Length > 1000 && Encoding.ASCII.GetString(pdfBytes, 0, 5) == "%PDF-" && S(vp, "pdf.signature_valid") == "true" && S(vp, "attachments.hashes_match") == "true" && S(vp, "credentials.clr.valid") == "true" && S(vp, "overall.valid") == "true",
                "Test-PDF: Test-Themenabschluss als signiertes PDF; PDF-Signatur, Anhänge, CLR und Modulnachweise werden von der bestehenden Prüfung bestätigt" + (vp is null ? " (kein Prüfergebnis)" : " " + S(vp, "overall.summary") + " " + string.Join("|", (vp["references"]?["checks"]?.AsArray() ?? new JsonArray()).Where(c => S(c, "ok") != "true").Select(c => S(c, "check"))) + " versions=" + S(vp, "versions")));
            pdfModuleId = S(await J(await admin.PostAsJsonAsync("/api/admin/credentials/test", new { type = "badge" })), "id");
            var pdfM = await admin.GetAsync($"/api/certificates/{pdfModuleId}/certificate.pdf");
            check(pdfM.IsSuccessStatusCode && Encoding.ASCII.GetString(await pdfM.Content.ReadAsByteArrayAsync(), 0, 5) == "%PDF-", "Test-PDF: Test-Modulnachweis als signiertes PDF erzeugt");
        }

        // JSON und JWT haben dieselbe Identität und dieselben fachlichen Inhalte
        {
            var fresh = (await J(await admin.GetAsync($"/api/certificates/{badgeId}/badge")))!;
            var pl = JsonNode.Parse(Encoding.UTF8.GetString(B64(badgeJws.Split('.')[1])))!;
            check(S(fresh, "id") == S(pl, "jti") && S(fresh, "issuer.id") == S(pl, "iss") && S(fresh, "credentialSubject.id") == S(pl, "sub") && S(pl, "vc.id") == S(fresh, "id") && JsonNode.DeepEquals(fresh, BadgeSigner.CredentialFromJws(badgeJws)),
                "JSON ↔ JWT: gleiche Credential-Identität (id = jti, issuer = iss, Subjekt = sub) und identischer fachlicher Inhalt");
            check(fresh["@context"]!.AsArray().Any(c => c is JsonValue && c.ToString() == "https://www.w3.org/ns/credentials/v2") && S(fresh, "type").Contains("OpenBadgeCredential") && S(fresh, "credentialStatus.type") == "1EdTechRevocationList" && S(fresh, "validFrom").EndsWith("Z"),
                "Credential: VC-2.0-Kontext, Typ OpenBadgeCredential, Widerrufsstatus 1EdTechRevocationList, validFrom in UTC");
        }

        // Fremde Benutzer erhalten keine Nachweise per ID-Manipulation
        check((await user.GetAsync($"/api/certificates/{badgeId}/badge")).StatusCode == HttpStatusCode.NotFound && (await user.GetAsync($"/api/certificates/{badgeId}/badge.jwt")).StatusCode == HttpStatusCode.NotFound
              && (await user.GetAsync($"/api/certificates/{badgeId}/certificate.pdf")).StatusCode == HttpStatusCode.NotFound, "Sicherheit: fremde Benutzer erhalten Testnachweise weder als JSON, JWT noch PDF (404)");

        // Gleichzeitige Ausstellung (mehrere Administratoren/Anfragen): jeder Nachweis hat eine eigene Identität
        var parallel = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => admin.PostAsJsonAsync("/api/admin/credentials/test", new { type = "badge" })));
        var parallelIds = (await Task.WhenAll(parallel.Select(async r => S(await J(r), "id")))).ToList();
        check(parallel.All(r => r.IsSuccessStatusCode) && parallelIds.Distinct().Count() == 6, "Parallel: sechs gleichzeitige Testausstellungen erzeugen sechs verschiedene Nachweise");

        // Gleichzeitiger Widerruf: genau ein Ereignis
        {
            var target = parallelIds[0];
            var num = S(await J(await admin.GetAsync($"/api/admin/credentials/{target}")), "row.number");
            var rs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => admin.PostAsJsonAsync($"/api/admin/credentials/{target}/revoke", new { reason = "Parallel", confirm_number = num })));
            var changed = 0; foreach (var r in rs) if (S(await J(r), "changed") == "true") changed++;
            var det = (await J(await admin.GetAsync($"/api/admin/credentials/{target}")))!;
            check(changed == 1 && det["audit"]!.AsArray().Count(a => S(a, "action") == "credential.revoke") == 1 && S(det, "row.status") == "revoked", "Parallel: sechs gleichzeitige Widerrufe erzeugen genau ein Widerrufsereignis");
            check(S(await J(await admin.GetAsync($"/api/admin/credentials/{parallelIds[1]}")), "row.status") == "active", "Parallel: andere Nachweise bleiben unberührt");
        }

        // Aussteller und Zeitpunkt bleiben nach dem Widerruf unverändert
        // (siehe unten: Vergleich Detail vor/nach Widerruf)

        // Suchparameter: begrenzt, parametrisiert
        check((await admin.GetAsync(Qs("q=" + Uri.EscapeDataString("' OR 1=1 --")))).IsSuccessStatusCode && await Count("q=" + Uri.EscapeDataString("' OR 1=1 --")) == 0, "Sicherheit: SQL-Zeichen in der Suche sind wirkungslos (parametrisiert)");
        check((await admin.GetAsync(Qs("q=" + new string('a', 5000) + "&page_size=100000&page=-4"))).IsSuccessStatusCode, "Sicherheit: überlange Suche, absurde Seitengröße und negative Seite werden begrenzt");

        // Missbrauchsbremse bleibt wirksam (eigene Adresse): nach 30 Anfragen je Minute 429
        {
            var limited = 0; using var rl = newClient(); rl.DefaultRequestHeaders.Add("X-Forwarded-For", $"10.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
            for (var i = 0; i < 34; i++) if ((await rl.PostAsync("/api/public/verify", new StringContent("x"))).StatusCode != HttpStatusCode.OK) limited++;   // die Statusseite macht aus 429 eine Fehlerantwort
            check(limited >= 1, "Missbrauchsbremse: öffentliche Prüfung wird je Adresse weiterhin begrenzt (nach 30 Anfragen je Minute abgewiesen)");
        }

        // X.509-Thumbprint ≠ Credential-Fingerprint: dasselbe Signaturzertifikat signiert mehrere PDFs; ein Credential-Widerruf lässt es und die übrigen Nachweise unberührt
        {
            var tp = S(await J(await admin.GetAsync($"/api/admin/credentials/{topicId}")), "row.thumbprint");
            var byTp = (await J(await admin.GetAsync("/api/admin/credentials?thumbprint=" + Uri.EscapeDataString(tp) + "&page_size=50")))!;
            var tpIds = byTp["items"]!.AsArray().Select(i => S(i, "id")).ToList();
            check(tp.Length >= 40 && tpIds.Contains(topicId) && tpIds.Contains(pdfModuleId) && tpIds.Count >= 2, "X.509: derselbe Thumbprint findet mehrere Nachweise (Signaturzertifikat ≠ Credential-Identität)");
            check(await Count("fingerprint=" + S(await J(await admin.GetAsync($"/api/admin/credentials/{pdfModuleId}")), "row.fingerprint")) == 1, "Credential-Fingerprint identifiziert genau einen Nachweis");
            var numPdf = S(await J(await admin.GetAsync($"/api/admin/credentials/{pdfModuleId}")), "row.number");
            await admin.PostAsJsonAsync($"/api/admin/credentials/{pdfModuleId}/revoke", new { reason = "Thumbprint-Test", confirm_number = numPdf });
            var still = (await J(await admin.GetAsync("/api/admin/credentials?thumbprint=" + Uri.EscapeDataString(tp) + "&status=active&page_size=50")))!["items"]!.AsArray().Select(i => S(i, "id")).ToList();
            check(still.Contains(topicId) && !still.Contains(pdfModuleId) && S(await J(await admin.GetAsync($"/api/admin/credentials/{topicId}")), "row.thumbprint") == tp, "X.509: der Widerruf eines Nachweises lässt das gemeinsame Signaturzertifikat und die übrigen PDFs unberührt");
        }

        // Register: Suche und Detail
        string Qs(string q) => "/api/admin/credentials?" + q;
        var list = (await J(await admin.GetAsync(Qs("test=test&page_size=100"))))!;
        var items = list["items"]!.AsArray();
        var mine = items.FirstOrDefault(i => S(i, "id") == badgeId);
        check(mine is not null && S(mine, "is_test") == "true" && S(mine, "issuer_id") == adminId && S(mine, "issuer_name") == adminName && S(mine, "source") == "admin" && S(mine, "recipient") == "TEST-Subjekt",
            "Register: Aussteller (Administrator aus dem authentifizierten Kontext), Quelle admin und Testsubjekt korrekt");
        check(S(mine, "issued_at").EndsWith("Z") && DateTime.Parse(S(mine, "issued_at"), null, System.Globalization.DateTimeStyles.AdjustToUniversal) >= before, "Register: Ausstellungszeitpunkt in UTC");
        check(items.Count >= 4 && items.Select(i => DateTime.Parse(S(i, "issued_at"))).SequenceEqual(items.Select(i => DateTime.Parse(S(i, "issued_at"))).OrderByDescending(d => d)), "Register: neueste zuerst");
        var fp = S(mine, "fingerprint");
        check(fp.Length == 64 && BadgeSignerHash(badgeJws) == fp, "Fingerprint: SHA-256 der signierten Original-JWS");
        async Task<int> Count(string q) => (int)(await J(await admin.GetAsync(Qs(q))))!["total"]!.GetValue<int>();
        check(await Count($"id={badgeId}") == 1 && await Count($"fingerprint={fp}") == 1 && await Count($"fingerprint={fp[..12]}") == 1 && await Count($"q={S(mine, "number")}") == 1, "Suche: Credential-ID, Fingerprint (voll und Präfix) und Nummer finden genau den Nachweis");
        check(await Count("fingerprint=abc") == 0 && await Count("thumbprint=0000000000000000000000000000000000000000") == 0, "Suche: zu kurzer Fingerprint und unbekannter Thumbprint liefern nichts (keine Massentreffer)");
        check(await Count($"issuer={Uri.EscapeDataString(adminName)}&test=test") >= 4 && await Count("recipient=TEST-Subjekt") >= 4 && await Count("module=Beispielthema") >= 1, "Suche: Aussteller, Empfänger, Modul-/Themenname");
        check(await Count("kind=clr&test=test") >= 1 && await Count("kind=badge&test=test") >= 3 && await Count("status=active&test=test") >= 4 && await Count("test=regular&id=" + badgeId) == 0, "Filter: Typ, Status, Test/Regulär");
        check(await Count($"from={DateTime.UtcNow:yyyy-MM-dd}&to={DateTime.UtcNow:yyyy-MM-dd}&test=test") >= 4 && await Count("from=2000-01-01&to=2000-01-02") == 0, "Filter: Zeitraum");
        check((await J(await admin.GetAsync(Qs("test=test&page_size=2&page=1"))))!["items"]!.AsArray().Count == 2 && (await J(await admin.GetAsync(Qs("sort=asc&test=test&page_size=1"))))!["items"]![0]!["issued_at"] is not null, "Register: Pagination und Sortierung");
        check(!(await admin.GetStringAsync(Qs("test=test&page_size=100"))).Contains("eyJ"), "Register: Listen enthalten keine Credentials (JWS)");

        // Regulär: Testnachweise stehen nicht in den Listen regulärer Abschlüsse
        var own = await admin.GetStringAsync("/api/certificates");
        check(!own.Contains(badgeId) && !own.Contains(topicId), "Testnachweise erscheinen nicht in „Meine Abschlüsse“ (keine Statistik, keine reguläre Leistung)");

        var detail = (await J(await admin.GetAsync($"/api/admin/credentials/{topicId}")))!;
        check(S(detail, "children").Length > 0 && detail["children"]!.AsArray().Count == 2 && S(detail, "signature_valid") == "true" && S(detail, "clr_signature_valid") == "true" && S(detail, "alg") == "ES256" && S(detail, "profile") == "ob3-jwt"
              && detail["audit"]!.AsArray().Any(a => S(a, "action") == "credential.test_issue" && S(a, "actor_id") == adminId), "Detail: Verknüpfungen, Signaturstatus, Algorithmus, Profil, Auditverlauf mit Akteur");
        var childId = S(detail, "children.0.id");
        check(S((await J(await admin.GetAsync($"/api/admin/credentials/{childId}"))), "parent.id") == topicId, "Detail: Modulnachweis verweist auf den übergeordneten Nachweis");

        // Widerruf
        var number = S(mine, "number");
        check((await admin.PostAsJsonAsync($"/api/admin/credentials/{badgeId}/revoke", new { reason = "Test", confirm_number = "falsch" })).StatusCode == HttpStatusCode.BadRequest
              && (await admin.PostAsJsonAsync($"/api/admin/credentials/{badgeId}/revoke", new { reason = "", confirm_number = number })).StatusCode == HttpStatusCode.BadRequest, "Widerruf: ohne passende Bestätigung und ohne Grund abgewiesen");
        var pre = (await J(await admin.GetAsync($"/api/admin/credentials/{badgeId}")))!;
        var r1 = await J(await admin.PostAsJsonAsync($"/api/admin/credentials/{badgeId}/revoke", new { reason = "Testwiderruf", confirm_number = number }));
        var r2 = await J(await admin.PostAsJsonAsync($"/api/admin/credentials/{badgeId}/revoke", new { reason = "Testwiderruf", confirm_number = number }));
        check(S(r1, "changed") == "true" && S(r2, "changed") == "false" && S(r2, "status") == "revoked", "Widerruf: einzelner Nachweis; erneuter Widerruf ist idempotent");
        var after = (await J(await admin.GetAsync($"/api/admin/credentials/{badgeId}")))!;
        check(after["audit"]!.AsArray().Count(a => S(a, "action") == "credential.revoke") == 1 && S(after, "revoked_by_name") == adminName && S(after, "revoked_reason") == "Testwiderruf", "Audit: genau ein Widerrufsereignis mit Akteur und Grund");
        check(S(pre, "row.issuer_id") == S(after, "row.issuer_id") && S(pre, "row.issued_at") == S(after, "row.issued_at") && S(pre, "row.fingerprint") == S(after, "row.fingerprint") && S(pre, "row.issuer_name") == S(after, "row.issuer_name"), "Unveränderlich: Aussteller, UTC-Zeitpunkt und Fingerprint bleiben nach dem Widerruf gleich");
        check(!string.Join(" ", after["audit"]!.AsArray().Select(a => S(a, "detail"))).Contains("eyJ"), "Audit: enthält keine JWS");
        var status = await J(await Pub().GetAsync($"/api/public/certificates/{badgeId}/status"));
        var verRevoked = await J(await Pub().PostAsync("/api/public/verify", new StringContent(badgeJws)));
        check(S(status, "status") == "revoked" && S(verRevoked, "revoked") == "true" && S(verRevoked, "valid") == "false" && S(verRevoked, "signature_valid") == "true", "Öffentliche Prüfung meldet widerrufen; die gültige Signatur ist kein Gültigkeitsbeweis");
        check((await admin.GetStringAsync($"/api/certificates/{badgeId}/badge.jwt")).Trim() == badgeJws, "Widerruf verändert die signierten Originalbytes nicht");
        check((await Pub().GetStringAsync("/api/public/revocation-list")).Contains(badgeId), "Sperrliste enthält den widerrufenen Nachweis");

        // Teilwiderruf: Themenabschluss widerrufen → Modulnachweise bleiben gültig (ADR 0032)
        var topicNumber = S(detail, "row.number");
        await admin.PostAsJsonAsync($"/api/admin/credentials/{topicId}/revoke", new { reason = "Testwiderruf Thema", confirm_number = topicNumber });
        var topicAfter = (await J(await admin.GetAsync($"/api/admin/credentials/{topicId}")))!;
        check(S(topicAfter, "row.status") == "revoked" && topicAfter["children"]!.AsArray().All(c => S(c, "status") == "active"), "Teilwiderruf: Themenabschluss (Master-Badge + CLR) widerrufen, Modulnachweise unverändert aktiv (kein kaskadierender Widerruf)");
        check(S(await J(await Pub().GetAsync($"/api/public/certificates/{childId}/status")), "status") == "active", "Teilwiderruf: öffentlicher Status des Modulnachweises bleibt aktiv");
        check((await admin.PostAsJsonAsync($"/api/admin/credentials/{Guid.NewGuid()}/revoke", new { reason = "x", confirm_number = "x" })).StatusCode == HttpStatusCode.NotFound, "Widerruf: unbekannte ID → 404");
    }

    private static string BadgeSignerHash(string jws) => BadgeSigner.HashHex(jws);
}
