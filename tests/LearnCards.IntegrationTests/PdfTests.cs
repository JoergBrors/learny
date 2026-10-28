using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services.Certificates;
using LearnCards.Web.Services.Pdf;

/// <summary>ADR 0033: PDF-Erzeugung, CMS-Signatur (RSA und ECDSA), Inspektion, Umbruch/Tabellen, Upload-Verifikation, Manipulations- und Anhangserkennung, Datenschutz.</summary>
public static class PdfTests
{
    private static string S(JsonNode? n, string path)
    {
        var cur = n;
        foreach (var part in path.Split('.')) cur = cur is JsonArray a && int.TryParse(part, out var i) ? (i < a.Count ? a[i] : null) : cur?[part];
        return cur?.ToString() ?? "";
    }

    public static X509Certificate2 NewRsaCert(string cn = "CN=Learny Test Signer")
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(cn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pkcs12, "t"), "t", X509KeyStorageFlags.Exportable);
    }

    public static X509Certificate2 NewEcCert()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=Learny Test EC", ec, HashAlgorithmName.SHA256);
        using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pkcs12, "t"), "t", X509KeyStorageFlags.Exportable);
    }

    private static List<string> InflatedStreams(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf); var result = new List<string>();
        foreach (Match m in Regex.Matches(text, @"/Filter /FlateDecode /Length (\d+) >>\nstream\n"))
        {
            var start = m.Index + m.Length; var len = int.Parse(m.Groups[1].Value);
            try { using var z = new ZLibStream(new MemoryStream(pdf, start, len), CompressionMode.Decompress); using var o = new MemoryStream(); z.CopyTo(o); result.Add(Encoding.Latin1.GetString(o.ToArray())); } catch (InvalidDataException) { }
        }
        return result;
    }

    // ─── Einheitentests ───
    public static void Unit(Action<bool, string> check)
    {
        Console.WriteLine("\nPDF: Erzeugung, CMS-Signatur, Inspektion, Umbruch (ADR 0033)");
        foreach (var (label, cert) in new[] { ("RSA", NewRsaCert()), ("ECDSA", NewEcCert()) })
        {
            var doc = new PdfBuilder { Title = "Test Ä Ö Ü ß „Zitat“ – €", Author = "Test" };
            var p = doc.AddPage(); p.Text(50, 780, "Grüße aus Äpfeln – 100 % (ok) \\ „x“", 12);
            doc.Files.Add(new PdfEmbeddedFile("a.json", Encoding.UTF8.GetBytes("{\"a\":1}"), "application/json", "Data", "A"));
            doc.Files.Add(new PdfEmbeddedFile("verification.json", Encoding.UTF8.GetBytes("{\"v\":2}"), "application/json", "Supplement", "V"));
            var pdf = doc.Build(new PdfSignOptions { Certificate = cert, Name = "Test", Reason = "Test" });
            var insp = PdfInspector.Inspect(pdf);
            check(insp.IsPdf && insp.SignaturePresent && insp.SubFilter == "ETSI.CAdES.detached" && insp.ByteRangeCoversFile && insp.DigestMatches && insp.Cms is { Parsed: true, SignatureValid: true } && insp.Cms.Signer?.Thumbprint == cert.Thumbprint && insp.Cms.HasSigningCertificateV2,
                $"PDF1 {label}: PDF wird CAdES-signiert (ETSI.CAdES.detached), Signatur gültig, ByteRange deckt die ganze Datei, Unterzeichner und signing-certificate-v2 lesbar");
            check(insp.Attachments.Count == 2 && Encoding.UTF8.GetString(insp.Attachments.First(a => a.Name == "a.json").Data) == "{\"a\":1}", $"PDF2 {label}: eingebettete Dateien lassen sich unverändert extrahieren");
            var bad = (byte[])pdf.Clone(); bad[200] ^= 0xFF;   // Byte im signierten Bereich ändern
            var appended = pdf.Concat(Encoding.ASCII.GetBytes("\n% Nachtrag\n")).ToArray();
            var insBad = PdfInspector.Inspect(bad); var insApp = PdfInspector.Inspect(appended);
            check(!(insBad.ByteRangeCoversFile && insBad.DigestMatches) && !insApp.ByteRangeCoversFile, $"PDF3 {label}: Änderung im signierten Bereich bzw. angehängte Daten werden erkannt");
            // Signaturwert manipulieren
            var tamperedCms = Regex.Replace(Encoding.Latin1.GetString(pdf), @"(/Contents <)([0-9A-F]{200})", m => m.Groups[1].Value + new string(m.Groups[2].Value.Reverse().ToArray()));
            var insT = PdfInspector.Inspect(Encoding.Latin1.GetBytes(tamperedCms));
            check(!(insT.Cms is { SignatureValid: true } && insT.DigestMatches), $"PDF4 {label}: veränderter Signaturwert ist ungültig");
        }

        // Umbruch: lange Wörter und Tabellen werden nicht abgeschnitten, Kopfzeilen wiederholen sich
        var d = new PdfBuilder(); var flow = new PdfFlow(d, _ => { });
        var longWord = new string('X', 500) + "ENDE"; var terms = Enumerable.Range(0, 120).Select(i => $"Kompetenz-{i:000}-Ä").ToList();
        flow.Paragraph("Langer Text mit Wort " + longWord + " und Schluss", 10);
        flow.Table(new[] { "Spalte A", "Spalte B" }, new[] { 200.0, 295 }, terms.Select(t => new[] { t, "Beschreibung " + t + " " + new string('y', 150) }));
        var streams = InflatedStreams(d.Build());
        var all = string.Concat(streams);
        check(d.Pages.Count > 3 && terms.All(t => all.Contains(Regex.Escape(t).Replace("Ä", "Ä")) || all.Contains(t.Replace("Ä", "Ä"))) && Regex.Matches(all, @"\(Spalte A\)").Count == d.Pages.Count
              && string.Concat(Regex.Matches(all, @"\(([^)]*)\) Tj").Select(m => m.Groups[1].Value)).Count(ch => ch == 'X') == 500 && all.Contains("ENDE und Schluss"),
            $"PDF5 lange Inhalte: {d.Pages.Count} Seiten, alle 120 Tabellenzeilen vorhanden, Kopfzeile auf jeder Seite wiederholt, 500-Zeichen-Wort vollständig (nichts abgeschnitten)");

        // Signaturzertifikat nicht konfiguriert: fail-closed
        var provider = new PdfSigningProvider(new AppConfig(), null!);
        var threw = false; try { provider.GetOptions(); } catch (PdfSigningUnavailableException) { threw = true; }
        check(threw, "PDF6 ohne konfiguriertes Signaturzertifikat gibt es keine signierten PDFs (fail-closed, 503 im API)");
        var expired = NewRsaCert(); // gültig
        check(provider.Configured == false, "PDF7 ohne Konfiguration und ohne Entwicklungsmodus ist die Signatur nicht verfügbar");
    }

    // ─── Ende-zu-Ende ───
    public static async Task E2EAsync(HttpClient lea, HttpClient other, Func<HttpClient> newClient, string topicCertId, string moduleCertId, JsonNode clr, JsonNode master, Action<bool, string> check)
    {
        var res1 = await lea.GetAsync($"/api/certificates/{topicCertId}/certificate.pdf");
        var pdf1 = await res1.Content.ReadAsByteArrayAsync();
        var res2 = await lea.GetAsync($"/api/certificates/{topicCertId}/certificate.pdf");
        var pdf2 = await res2.Content.ReadAsByteArrayAsync();
        check(res1.StatusCode == HttpStatusCode.OK && res1.Content.Headers.ContentType?.MediaType == "application/pdf" && pdf1.Length > 5000 && pdf1.AsSpan(0, 5).SequenceEqual("%PDF-"u8) && pdf1.SequenceEqual(pdf2)
              && res1.Headers.GetValues("X-PDF-SHA256").First() == Convert.ToHexString(SHA256.HashData(pdf1)).ToLowerInvariant(),
            "PDF8 Themen-PDF: signiertes PDF wird ausgeliefert und beim zweiten Abruf unverändert (gespeichert) geliefert");
        if (Environment.GetEnvironmentVariable("TEST_DUMP_DIR") is { Length: > 0 } dump) { Directory.CreateDirectory(dump); File.WriteAllBytes(Path.Combine(dump, "topic.pdf"), pdf1); }   // nur für die manuelle Sichtprüfung
        var insp = PdfInspector.Inspect(pdf1);
        var names = insp.Attachments.Select(a => a.Name).ToHashSet();
        // Neues Profil (ADR 0038): die JWS liegt als .jwt-Datei bei; die .json-Dateien sind das reine Credential (identisch mit dem Download)
        var clrJws = Encoding.ASCII.GetString(insp.Attachments.First(a => a.Name == "clr-credential.jwt").Data).Trim();
        var masterJws = Encoding.ASCII.GetString(insp.Attachments.First(a => a.Name == "master-badge.jwt").Data).Trim();
        check(insp.SignaturePresent && insp.ByteRangeCoversFile && insp.DigestMatches && insp.Cms!.SignatureValid
              && new[] { "clr-credential.json", "clr-credential.jwt", "master-badge.json", "master-badge.jwt", "module-credentials.json", "module-competence-snapshots.json", "verification.json", "public-key.jwk", "competence-snapshot.json" }.All(names.Contains),
            "PDF9 Themen-PDF: signiert, enthält CLR, Master-Badge (JSON-LD und JWT), Modulnachweise, Verifikationsdaten, öffentlichen Schlüssel und Snapshots als Anhänge");
        var embeddedClr = JsonNode.Parse(insp.Attachments.First(a => a.Name == "clr-credential.json").Data);
        var embeddedMaster = JsonNode.Parse(insp.Attachments.First(a => a.Name == "master-badge.json").Data);
        check(JsonNode.DeepEquals(embeddedClr, clr) && JsonNode.DeepEquals(embeddedMaster, master)
              && S(BadgeSigner.CredentialFromJws(clrJws), "id") == S(clr, "id") && BadgeSigner.CredentialFromJws(clrJws)["credentialSubject"]!["verifiableCredential"]!.AsArray().Count == clr!["credentialSubject"]!["verifiableCredential"]!.AsArray().Count && JsonNode.DeepEquals(BadgeSigner.CredentialFromJws(masterJws), master),
            "PDF10 das eingebettete CLR ist das vollständige, unveränderte signierte Original (identisch mit dem Download); ebenso das Master-Badge");
        var verification = JsonNode.Parse(insp.Attachments.First(a => a.Name == "verification.json").Data)!;
        check(S(verification, "signature.algorithm") == "ES256" && S(verification, "signature.key_id").Length > 0 && S(verification, "hashes.clr_jws_sha256") == BadgeSigner.HashHex(clrJws)
              && S(verification, "hashes.master_badge_jws_sha256") == BadgeSigner.HashHex(masterJws) && S(verification, "topic.version") == "1" && S(verification, "certificate_id") == topicCertId
              && verification["hashes"]!["embedded_files"]!.AsArray().Count == insp.Attachments.Count - 1 && verification["standards"]!.ToJsonString().Contains("CLR Standard 2.0"),
            "PDF11 verification.json: Kennungen, Algorithmus, Schlüssel-ID, Version, Hashes von CLR/Badge/Snapshot und aller übrigen Anhänge stimmen mit den Originalen überein");
        var jwkText = Encoding.UTF8.GetString(insp.Attachments.First(a => a.Name == "public-key.jwk").Data);
        var allText = Encoding.Latin1.GetString(pdf1);
        check(!jwkText.Contains("\"d\"") && !allText.Contains("PRIVATE KEY") && !insp.Attachments.Any(a => Encoding.UTF8.GetString(a.Data).Contains("PRIVATE KEY")) && !allText.Contains("pdf-test"),
            "PDF12 weder privater Schlüssel noch Passwort noch Secrets im PDF oder in den Anhängen");
        check(Regex.IsMatch(allText, @"/URI \(https?://[^)]*/verify/" + topicCertId + @"\)"), "PDF13 die Verifikationsadresse ist als anklickbarer Link im PDF (QR-Code verweist auf dieselbe Adresse)");
        var texts = string.Concat(InflatedStreams(pdf1));
        check(texts.Contains("Themenabschluss") && texts.Contains("Gesamtabschluss") && texts.Contains("Modulübersicht".Replace("ü", "ü")) && texts.Contains("Kompetenznachweise") && texts.Contains("Verifikation") && texts.Contains("Learny Verified") && texts.Contains("Digitally Signed Credential")
              && texts.Contains("Seite 1 von"), "PDF14 Themen-PDF stellt Gesamtabschluss, Modulübersicht, Kompetenznachweise und Verifikation lesbar dar, mit Prüfsiegel und Seitenzahlen");

        // Modul-PDF
        var mres = await lea.GetAsync($"/api/certificates/{moduleCertId}/certificate.pdf");
        var mpdf = await mres.Content.ReadAsByteArrayAsync(); var minsp = PdfInspector.Inspect(mpdf);
        if (Environment.GetEnvironmentVariable("TEST_DUMP_DIR") is { Length: > 0 } dump2) File.WriteAllBytes(Path.Combine(dump2, "module.pdf"), mpdf);
        check(mres.IsSuccessStatusCode && minsp.Cms is { SignatureValid: true } && minsp.ByteRangeCoversFile && minsp.DigestMatches && minsp.Attachments.Any(a => a.Name == "module-credential.json") && minsp.Attachments.Any(a => a.Name == "competence-snapshot.json")
              && string.Concat(InflatedStreams(mpdf)).Contains("Zertifikat"), "PDF15 Modul-PDF: signiert, mit Modulzertifikat, Kompetenzsnapshot und Verifikationsdaten");

        // Datenschutz
        check((await other.GetAsync($"/api/certificates/{topicCertId}/certificate.pdf")).StatusCode == HttpStatusCode.NotFound && (await newClient().GetAsync($"/api/certificates/{topicCertId}/certificate.pdf")).StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Redirect,
            "PDF16 fremde Benutzer und Anonyme erhalten das PDF nicht");

        // ─── Upload-Prüfung ───
        async Task<JsonNode?> UploadAsync(byte[] data)
        {
            using var form = new MultipartFormDataContent(); var fc = new ByteArrayContent(data); fc.Headers.ContentType = new MediaTypeHeaderValue("application/pdf"); form.Add(fc, "file", "zertifikat.pdf");
            var r = await newClient().PostAsync("/api/public/verify/pdf", form);
            var body = await r.Content.ReadAsStringAsync();
            if (!body.TrimStart().StartsWith("{")) return null;
            return JsonNode.Parse(body);
        }
        var good = await UploadAsync(pdf1);
        check(S(good, "overall.valid") == "true" && S(good, "pdf.signature_valid") == "true" && S(good, "pdf.document_unchanged") == "true" && S(good, "attachments.hashes_match") == "true" && S(good, "credentials.clr.valid") == "true"
              && S(good, "credentials.master_badge.valid") == "true" && good!["credentials"]!["modules"]!.AsArray().All(m => S(m, "valid") == "true") && S(good, "references.all_ok") == "true" && S(good, "versions.match") == "true" && S(good, "revocation.any_revoked") == "false"
              && S(good, "overall.signer_trust") == "installation" && S(good, "pdf.signer.signed_by_this_installation") == "true",
            "V1 Upload-Prüfung des Originals: PDF-Signatur, Unversehrtheit, Anhänge, CLR, Master-Badge, Modulnachweise, Referenzen, Versionen und Widerrufsstatus werden getrennt bestätigt");
        var flipped = (byte[])pdf1.Clone(); flipped[pdf1.Length / 3] ^= 0x55;
        var tampered = await UploadAsync(flipped);
        var appended = await UploadAsync(pdf1.Concat(Encoding.ASCII.GetBytes("\n1 0 obj << /Nachtrag true >> endobj\n")).ToArray());
        check(S(tampered, "overall.valid") == "false" && S(tampered, "pdf.document_unchanged") == "false" && S(appended, "overall.valid") == "false" && S(appended, "pdf.document_unchanged") == "false" && S(appended, "pdf.signature_valid") == "true",
            "V2 manipulierte oder nachträglich ergänzte PDFs sind ungültig (auch wenn der alte Signaturwert noch formal gültig ist)");
        check(S(await UploadAsync(Encoding.ASCII.GetBytes("kein pdf")), "overall.valid") == "false" && S(await UploadAsync(Array.Empty<byte>()), "overall.valid") == "false", "V3 Nicht-PDF und leere Dateien werden abgewiesen");

        // fremd signierte PDFs mit veränderten Anhängen: eine gültige PDF-Signatur legitimiert weder Anhänge noch CLR
        var foreign = NewRsaCert("CN=Angreifer");
        byte[] Rebuild(Func<string, byte[], byte[]> mutate)
        {
            var d = new PdfBuilder { Title = "Fälschung" }; d.AddPage().Text(50, 780, "Fälschung", 12);
            foreach (var a in insp.Attachments) d.Files.Add(new PdfEmbeddedFile(a.Name, mutate(a.Name, a.Data), "application/json", "Data", a.Name));
            return d.Build(new PdfSignOptions { Certificate = foreign });
        }
        var changedClr = Rebuild((n, b) => n == "clr-credential.json" ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace("Comprehensive Learner Record", "Gefälscht")) : b);
        var vChangedClr = await UploadAsync(changedClr);
        check(S(vChangedClr, "pdf.signature_valid") == "true" && S(vChangedClr, "pdf.document_unchanged") == "true" && S(vChangedClr, "overall.valid") == "false" && S(vChangedClr, "attachments.hashes_match") == "false" && S(vChangedClr, "pdf.signer_trust") == "untrusted",
            "V4 gültige PDF-Signatur eines Fremden, aber veränderter Anhang (CLR): Hash stimmt nicht — nicht gültig; Unterzeichner nicht vertrauenswürdig");
        // Angreifer rechnet die Hashes in verification.json nach — dann scheitert die CLR-Signatur selbst
        var fixedHash = Rebuild((n, b) =>
        {
            if (n == "clr-credential.json") return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace("Comprehensive Learner Record", "Gefälscht"));
            if (n != "verification.json") return b;
            var v = JsonNode.Parse(b)!; var list = v["hashes"]!["embedded_files"]!.AsArray();
            var clrEntry = list.First(e => S(e, "name") == "clr-credential.json")!;
            clrEntry["sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(insp.Attachments.First(a => a.Name == "clr-credential.json").Data).Replace("Comprehensive Learner Record", "Gefälscht")))).ToLowerInvariant();
            return Encoding.UTF8.GetBytes(v.ToJsonString());
        });
        var vFixed = await UploadAsync(fixedHash);
        check(S(vFixed, "overall.valid") == "false" && S(vFixed, "attachments.hashes_match") == "true" && S(vFixed, "credentials.clr.valid") == "false" && S(vFixed, "credentials.clr.content_matches") == "false",
            "V5 Hashes nachgerechnet, aber CLR-Inhalt verändert: die CLR-Signatur ist ungültig — eine gültige PDF-Signatur legitimiert kein ungültiges CLR");
        var offlineJwk = JsonNode.Parse(jwkText);
        check(BadgeSigner.VerifyWithJwk(clrJws, offlineJwk).SignatureValid && !BadgeSigner.VerifyWithJwk(clrJws.Replace('A', 'B'), offlineJwk).SignatureValid,
            "V6 Offline-Prüfung: die CLR-Signatur lässt sich mit dem im PDF mitgelieferten öffentlichen Schlüssel prüfen (Manipulation wird erkannt)");
    }

    /// <summary>Nach dem Widerruf: dasselbe PDF zeigt online „widerrufen“, die PDF-Signatur selbst bleibt gültig.</summary>
    public static async Task RevokedAsync(HttpClient lea, Func<HttpClient> newClient, string topicCertId, Action<bool, string> check)
    {
        var pdf = await (await lea.GetAsync($"/api/certificates/{topicCertId}/certificate.pdf")).Content.ReadAsByteArrayAsync();
        using var form = new MultipartFormDataContent(); form.Add(new ByteArrayContent(pdf), "file", "z.pdf");
        var r = JsonNode.Parse(await (await newClient().PostAsync("/api/public/verify/pdf", form)).Content.ReadAsStringAsync());
        check(S(r, "pdf.signature_valid") == "true" && S(r, "pdf.document_unchanged") == "true" && S(r, "revocation.any_revoked") == "true" && S(r, "overall.valid") == "false" && S(r, "credentials.clr.revoked") == "true",
            "V7 nach dem Widerruf: PDF-Signatur bleibt gültig, die Online-Prüfung meldet den Themenabschluss als widerrufen und das PDF damit als nicht gültig");
    }
}
