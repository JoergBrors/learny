using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LearnCards.Web.Domain;
using LearnCards.Web.Services.Pdf;
using LearnCards.Web.Services.Pki;

/// <summary>
/// ADR 0035: eigene Learny-PKI — Ersteinrichtung beim ersten Start, X.509-Profile, Ausstellung, Widerruf, CRL, Sicherheit, Neustart/Schlüsselverlust und PDF-Signatur mit
/// Prüfung der Learny-Kette. Die unabhängige Validierung mit OpenSSL und pyHanko liegt in tools/pki/validate_pki.py (docs/testing.md).
/// </summary>
public static class PkiTests
{
    private static StringContent Body(object? o) => new(JsonSerializer.Serialize(o ?? new { }), Encoding.UTF8, "application/json");
    private static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");
    private static async Task<JsonNode?> J(HttpResponseMessage r) { var t = await r.Content.ReadAsStringAsync(); try { return t.Length == 0 ? null : JsonNode.Parse(t); } catch (JsonException) { return null; } }
    private static string S(JsonNode? n, string path)
    {
        var cur = n;
        foreach (var part in path.Split('.')) cur = cur is JsonArray a && int.TryParse(part, out var i) ? (i < a.Count ? a[i] : null) : cur?[part];
        return cur?.ToString() ?? "";
    }
    private static X509Certificate2 Parse(byte[] der) => X509CertificateLoader.LoadCertificate(der);
    private static T? Ext<T>(X509Certificate2 c) where T : X509Extension => c.Extensions.OfType<T>().FirstOrDefault();

    public static async Task RunAsync(Func<string?, Task<HttpClient>> login, Func<HttpClient> newClient, HttpClient admin, string baseUrl, string keyDir, string keyPassword, string rootPassphrase,
        Func<string, Dictionary<string, string>, Task<(string Url, Process Proc)>> startWeb, Action<Process?> stop, Func<string> getLog, Action<bool, string> check)
    {
        Console.WriteLine("\nLearny-PKI: Ersteinrichtung, X.509, Ausstellung, Widerruf, CRL, Sicherheit (ADR 0035)");
        var anon = newClient();

        // ─── A: Ersteinrichtung beim ersten Start ───
        var st = await J(await admin.GetAsync("/api/pki/status"));
        check(S(st, "state") == "operational" && S(st, "root.type") == "root_ca" && S(st, "issuing.type") == "issuing_ca" && S(st, "signing.type") == "document_signing" && S(st, "publicly_trusted") == "false",
            $"A1 beim ersten Start wurde die PKI automatisch eingerichtet und ist betriebsbereit (Zustand {S(st, "state")})");
        var certs = (await J(await admin.GetAsync("/api/pki/certificates")))!.AsArray();
        check(certs.Count(c => S(c, "type") == "root_ca") == 1 && certs.Count(c => S(c, "type") == "issuing_ca") == 1 && certs.Count(c => S(c, "type") == "document_signing" && S(c, "status") == "active") == 1,
            "A2 genau eine Root CA, eine Issuing CA und ein aktives Signaturzertifikat im Register");
        var again = await admin.PostAsync("/api/pki/initialize", Body(new { public_url = baseUrl }));
        check(again.StatusCode == HttpStatusCode.BadRequest && (await again.Content.ReadAsStringAsync()).Contains("bereits"), "A3 eine zweite Initialisierung wird abgewiesen (keine zweite Root CA)");
        check(S(st, "notice").Contains("keine automatische öffentliche Anerkennung") && S(st, "root_key_on_server") == "true" && (st!["warnings"]!.AsArray().Any(w => w!.ToString().Contains("Root-Schlüssel"))), "A4 Status nennt die fehlende öffentliche Anerkennung und warnt, solange der Root-Schlüssel auf dem Server liegt");

        // Schlüsselspeicher: verschlüsselte PKCS#12-Dateien, kein Klartext, nie in der Datenbank
        var p12s = Directory.EnumerateFiles(keyDir, "*.p12", SearchOption.AllDirectories).ToList();
        var openOk = true; var wrongRejected = true;
        foreach (var f in p12s)
        {
            var bytes = File.ReadAllBytes(f);
            var pass = f.Contains("root-offline") ? rootPassphrase : keyPassword;
            try { using var c = PkiKeyStore.LoadBytes(bytes, pass); openOk &= c.HasPrivateKey; } catch (CryptographicException) { openOk = false; }
            try { using var c = PkiKeyStore.LoadBytes(bytes, "falsches-passwort-123"); wrongRejected = false; } catch (CryptographicException) { }
        }
        check(p12s.Count == 3 && openOk && wrongRejected && Directory.EnumerateFiles(keyDir, "*", SearchOption.AllDirectories).All(f => f.EndsWith(".p12") || f.EndsWith(".txt") || f.EndsWith(".pass") || f.EndsWith(".tmp")),
            $"A5 Schlüssel liegen nur als passwortverschlüsselte PKCS#12-Dateien vor ({p12s.Count} Dateien, falsches Passwort wird abgewiesen, kein PEM) [{string.Join(' ', Directory.EnumerateFiles(keyDir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(keyDir, f)))}; open={openOk} wrong={wrongRejected}]");
        var certListText = await (await admin.GetAsync("/api/pki/certificates")).Content.ReadAsStringAsync();
        check(!certListText.Contains("PRIVATE KEY") && !certListText.Contains(keyPassword) && !getLog().Contains(keyPassword) && !getLog().Contains(rootPassphrase), "A6 weder API noch Protokoll enthalten private Schlüssel oder Passwörter");

        // ─── B: X.509-Profile ───
        var rootDer = await (await anon.GetAsync("/pki/certificates/root")).Content.ReadAsByteArrayAsync();
        var issDer = await (await anon.GetAsync("/pki/certificates/issuing")).Content.ReadAsByteArrayAsync();
        var signDer = await (await anon.GetAsync(S(st, "signing.download"))).Content.ReadAsByteArrayAsync();
        using var root = Parse(rootDer); using var iss = Parse(issDer); using var sign = Parse(signDer);
        var rootBc = Ext<X509BasicConstraintsExtension>(root)!; var issBc = Ext<X509BasicConstraintsExtension>(iss)!; var signBc = Ext<X509BasicConstraintsExtension>(sign);
        check(root.Subject == root.Issuer && rootBc is { CertificateAuthority: true, HasPathLengthConstraint: true, PathLengthConstraint: 1, Critical: true } && Ext<X509KeyUsageExtension>(root)!.KeyUsages == (X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign)
              && Ext<X509KeyUsageExtension>(root)!.Critical && Ext<X509SubjectKeyIdentifierExtension>(root) is not null && root.GetRSAPublicKey()!.KeySize == 4096 && root.SignatureAlgorithm.Value == "1.2.840.113549.1.1.11",
            "B1 Root CA: selbstsigniert, CA:TRUE pathLen 1 (kritisch), keyCertSign+cRLSign (kritisch), SKI, RSA-4096/SHA-256");
        var rootSki = Ext<X509SubjectKeyIdentifierExtension>(root)!.SubjectKeyIdentifier;
        var issAki = Ext<X509AuthorityKeyIdentifierExtension>(iss);
        check(issBc is { CertificateAuthority: true, PathLengthConstraint: 0, Critical: true } && Ext<X509KeyUsageExtension>(iss)!.KeyUsages == (X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign) && issAki is not null && Convert.ToHexString(issAki.KeyIdentifier!.Value.Span) == rootSki
              && Encoding.ASCII.GetString(Ext<X509Extension>(iss, "2.5.29.31")!.RawData).Contains("/pki/crl/") && iss.Issuer == root.Subject,
            "B2 Issuing CA: CA:TRUE pathLen 0, keyCertSign+cRLSign, AKI = SKI der Root, CRL-Verteilungspunkt, von der Root ausgestellt");
        var signAki = Ext<X509AuthorityKeyIdentifierExtension>(sign);
        var eku = Ext<X509EnhancedKeyUsageExtension>(sign)!;
        var aia = Ext<X509AuthorityInformationAccessExtension>(sign);
        check(signBc is { CertificateAuthority: false } && Ext<X509KeyUsageExtension>(sign)!.KeyUsages == (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation) && eku.EnhancedKeyUsages.Count == 1 && eku.EnhancedKeyUsages[0].Value == "1.3.6.1.5.5.7.3.36"
              && signAki is not null && Convert.ToHexString(signAki.KeyIdentifier!.Value.Span) == Ext<X509SubjectKeyIdentifierExtension>(iss)!.SubjectKeyIdentifier && Encoding.ASCII.GetString(Ext<X509Extension>(sign, "2.5.29.31")!.RawData).Contains("/pki/crl/")
              && aia is not null && !aia.EnumerateOcspUris().Any() && aia.EnumerateCAIssuersUris().Any() && (sign.NotAfter - sign.NotBefore).TotalDays <= 731 && sign.SerialNumberBytes.Length >= 16,
            "B3 Signaturzertifikat: CA:FALSE, digitalSignature+nonRepudiation, nur EKU id-kp-documentSigning (RFC 9336), AKI, CRL-DP, keine OCSP-Adresse, ≤ 2 Jahre, ≥ 128-Bit-Seriennummer");
        var problems = PkiVerifier.CheckProfile(root, PkiRole.Root).Concat(PkiVerifier.CheckProfile(iss, PkiRole.Issuing)).Concat(PkiVerifier.CheckProfile(sign, PkiRole.Signing)).ToList();
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.Add(root); chain.ChainPolicy.ExtraStore.Add(iss); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        check(problems.Count == 0 && chain.Build(sign) && chain.ChainElements.Count == 3, "B4 Profilprüfung ohne Befund; Kette Signatur → Issuing → Root wird gegen die Root als Vertrauensanker gebaut");
        var pemChain = await (await anon.GetAsync("/pki/chain.pem")).Content.ReadAsStringAsync();
        check(pemChain.Split("BEGIN CERTIFICATE").Length == 3 && !pemChain.Contains("PRIVATE"), "B5 /pki/chain.pem liefert Issuing und Root als PEM (öffentlich, ohne Anmeldung)");

        // ─── C: Ausstellung ───
        var oldSigning = S(st, "signing.id");
        var countBefore = (await J(await admin.GetAsync("/api/pki/certificates")))!.AsArray().Count;
        var csr = await admin.PostAsync("/api/pki/certificates", Body(new { csr = "-----BEGIN CERTIFICATE REQUEST-----", ca = true, subject = "CN=Evil Root" }));
        var csr2 = await admin.PostAsync("/api/pki/certificates/csr", Body(new { csr = "x" }));
        check(!csr.IsSuccessStatusCode && !csr2.IsSuccessStatusCode && (await J(await admin.GetAsync("/api/pki/certificates")))!.AsArray().Count == countBefore, "C1 es gibt keinen Endpunkt für beliebige CSR-Inhalte oder zusätzliche Roots (Anfragen scheitern, das Register bleibt unverändert)");
        var renew = await admin.PostAsync("/api/pki/signing/renew", Body(new { }));
        var renewed = await J(renew);
        var certsAfter = (await J(await admin.GetAsync("/api/pki/certificates")))!.AsArray();
        check(renew.IsSuccessStatusCode && S(renewed, "id") != oldSigning && S(renewed, "serial_number") != S(st, "signing.serial_number") && certsAfter.Single(c => S(c, "id") == oldSigning)["status"]!.ToString() == "superseded"
              && certsAfter.Count(c => S(c, "type") == "document_signing" && S(c, "status") == "active") == 1 && certsAfter.Select(c => S(c, "serial_number")).Distinct().Count() == certsAfter.Count,
            "C2 Erneuerung: neues Zertifikat mit neuer Seriennummer, Vorgänger „superseded“, genau ein aktives, Seriennummern eindeutig");
        var detail = await J(await admin.GetAsync($"/api/pki/certificates/{oldSigning}"));
        check(detail!["events"]!.AsArray().Any(e => S(e, "event") == "superseded"), "C3 Statushistorie wird getrennt geführt (Ereignisse: ausgestellt, ersetzt)");

        // ─── D/E: Widerruf und CRL ───
        var issuingId = S(st, "issuing.id"); var rootId = S(st, "root.id");
        async Task<(CrlInfo Info, byte[] Der)> Crl(string caId, X509Certificate2 ca) { var der = await (await anon.GetAsync($"/pki/crl/{caId}.crl")).Content.ReadAsByteArrayAsync(); return (PkiCrypto.ParseAndVerifyCrl(der, ca), der); }
        var crl0 = await Crl(issuingId, iss);
        var crlResp = await anon.GetAsync($"/pki/crl/{issuingId}.crl");
        check(crlResp.Content.Headers.ContentType?.MediaType == "application/pkix-crl" && crl0.Info.IsValidFor(DateTime.UtcNow) && crl0.Info.Version == 2 && crl0.Info.Number >= 1 && crl0.Info.SignatureAlgorithm == "1.2.840.113549.1.1.11",
            "E1 öffentliche CRL: application/pkix-crl, X.509 v2, Signatur/Aussteller/AKI geprüft, aktuell, cRLNumber vorhanden");
        var rootCrl = await Crl(rootId, root);
        check(rootCrl.Info.IsValidFor(DateTime.UtcNow) && rootCrl.Info.Entries.Count == 0, "E2 Root-CRL beim Einrichten ausgestellt und gültig");

        var curSigning = S(renewed, "id"); var curSerial = S(renewed, "serial_number");
        check((await admin.PostAsync($"/api/pki/certificates/{curSigning}/revoke", Body(new { reason = "keyCompromise", confirm_serial = "00" }))).StatusCode == HttpStatusCode.BadRequest
              && (await admin.PostAsync($"/api/pki/certificates/{curSigning}/revoke", Body(new { reason = "bogus", confirm_serial = curSerial }))).StatusCode == HttpStatusCode.BadRequest
              && (await admin.PostAsync($"/api/pki/certificates/{curSigning}/revoke", Body(new { reason = "7", confirm_serial = curSerial }))).StatusCode == HttpStatusCode.BadRequest
              && (await admin.PostAsync($"/api/pki/certificates/{rootId}/revoke", Body(new { reason = "keyCompromise", confirm_serial = S(st, "root.serial_number") }))).StatusCode == HttpStatusCode.BadRequest,
            "D1 Widerruf braucht Bestätigung (Seriennummer) und einen gültigen RFC-5280-Grund; die Root CA wird nicht widerrufen");
        var revoke = await admin.PostAsync($"/api/pki/certificates/{curSigning}/revoke", Body(new { reason = "keyCompromise", confirm_serial = curSerial }));
        var revs = (await J(await admin.GetAsync("/api/pki/revocations")))!.AsArray();
        var crl1 = await Crl(issuingId, iss);
        var serialNorm = curSerial.TrimStart('0');
        check(revoke.StatusCode == HttpStatusCode.NoContent && revs.Any(r => S(r, "certificate_id") == curSigning && S(r, "reason_code") == "1" && S(r, "active") == "true")
              && crl1.Info.Number == crl0.Info.Number + 1 && crl1.Info.IsValidFor(DateTime.UtcNow) && crl1.Info.Entries.Any(e => e.SerialHex.TrimStart('0') == serialNorm && e.Reason == 1),
            "D2 Widerruf: Register-Eintrag, Zertifikatsstatus, sofort neue CRL (Nummer +1) mit Eintrag und Grund keyCompromise");
        check((await admin.PostAsync($"/api/pki/certificates/{curSigning}/revoke", Body(new { reason = "keyCompromise", confirm_serial = curSerial }))).StatusCode == HttpStatusCode.BadRequest, "D3 doppelter Widerruf wird abgewiesen (ein aktiver Eintrag je Zertifikat)");
        var tampered = (byte[])crl1.Der.Clone(); tampered[tampered.Length / 2] ^= 0x01;
        check(!PkiCrypto.ParseAndVerifyCrl(tampered, iss).SignatureValid && !PkiCrypto.ParseAndVerifyCrl(crl1.Der, root).SignatureValid && !new PkiCrl { ThisUpdate = DateTime.UtcNow.AddDays(-9), NextUpdate = DateTime.UtcNow.AddDays(-2) }.IsFresh(DateTime.UtcNow),
            "E3 manipulierte CRL und CRL gegen falschen Aussteller sind ungültig; eine abgelaufene CRL gilt nicht als aktuell");
        var st2 = await J(await admin.GetAsync("/api/pki/status"));
        check(S(st2, "state") == "maintenance_required", $"D4 ohne gültiges Signaturzertifikat ist die PKI nicht betriebsbereit (Zustand {S(st2, "state")})");
        var renew2 = await J(await admin.PostAsync("/api/pki/signing/renew", Body(new { })));
        check(S(await J(await admin.GetAsync("/api/pki/status")), "state") == "operational" && S(renew2, "status") == "active", "D5 nach Erneuerung ist die PKI wieder betriebsbereit");

        var holdId = S(renew2, "id"); var holdSerial = S(renew2, "serial_number");
        var hold = await admin.PostAsync($"/api/pki/certificates/{holdId}/revoke", Body(new { reason = "certificateHold", confirm_serial = holdSerial }));
        var crlHold = await Crl(issuingId, iss);
        var release = await admin.PostAsync($"/api/pki/certificates/{holdId}/release-hold", Body(new { }));
        var crlRel = await Crl(issuingId, iss);
        var holdRows = (await J(await admin.GetAsync("/api/pki/revocations")))!.AsArray().Where(r => S(r, "certificate_id") == holdId).ToList();
        check(hold.StatusCode == HttpStatusCode.NoContent && crlHold.Info.Entries.Any(e => e.SerialHex.TrimStart('0') == holdSerial.TrimStart('0') && e.Reason == 6) && release.StatusCode == HttpStatusCode.NoContent
              && !crlRel.Info.Entries.Any(e => e.SerialHex.TrimStart('0') == holdSerial.TrimStart('0')) && holdRows.Count == 1 && S(holdRows[0], "active") == "false" && crlRel.Info.Number > crlHold.Info.Number,
            "D6 certificateHold erscheint in der CRL und kann aufgehoben werden (Eintrag bleibt als Historie, fehlt in der neuen CRL)");
        check((await admin.PostAsync($"/api/pki/certificates/{curSigning}/release-hold", Body(new { }))).StatusCode == HttpStatusCode.BadRequest, "D7 endgültige Widerrufe (keyCompromise) lassen sich nicht aufheben");
        var allCrls = (await J(await admin.GetAsync("/api/pki/crls")))!.AsArray().Where(c => S(c, "issuer_ca_id") == issuingId).Select(c => long.Parse(S(c, "crl_number"))).OrderBy(x => x).ToList();
        check(allCrls.Count >= 4 && allCrls.Zip(allCrls.Skip(1), (a, b) => b == a + 1).All(x => x), $"E4 CRL-Nummern sind lückenlos monoton steigend ({string.Join(",", allCrls)})");
        var rootIssue = await admin.PostAsync("/api/pki/crl/issue", Body(new { }));
        check(rootIssue.IsSuccessStatusCode && (await Crl(issuingId, iss)).Info.Number == allCrls.Max() + 1, "E5 manuell ausgestellte CRL erhöht die Nummer");
        check((await anon.GetAsync($"/pki/crl/{Guid.NewGuid()}.crl")).StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.NotFound && (await anon.GetAsync("/pki/crl/..%2f..%2fetc.crl")).StatusCode == HttpStatusCode.NotFound && (await anon.GetAsync("/pki/certificates/nicht-vorhanden")).StatusCode == HttpStatusCode.NotFound,
            "E6 unbekannte CA-IDs und Pfadtricks liefern keine Daten");

        // ─── G: Sicherheit und Rollen ───
        var user = await login("pki-user"); var aud = await login("pki-auditor"); var ta = await login("pki-themenadmin"); var adm2 = await login("pki-admin2");
        async Task<string> Uid(HttpClient c) => S(await J(await c.GetAsync("/api/me")), "id");
        var audId = await Uid(aud); var taId = await Uid(ta); var adm2Id = await Uid(adm2);
        await admin.PutAsync($"/api/admin/users/{audId}/roles", Body(new { roles = new[] { "auditor" } }));
        await admin.PutAsync($"/api/admin/users/{taId}/roles", Body(new { roles = new[] { "themen_admin" } }));
        await admin.PutAsync($"/api/admin/users/{adm2Id}/roles", Body(new { roles = new[] { "admin" } }));
        check((await user.GetAsync("/api/pki/status")).StatusCode == HttpStatusCode.Forbidden && (await ta.GetAsync("/api/pki/status")).StatusCode == HttpStatusCode.Forbidden
              && (await user.PostAsync("/api/pki/signing/renew", Body(new { }))).StatusCode == HttpStatusCode.Forbidden && (await ta.PostAsync("/api/pki/crl/issue", Body(new { }))).StatusCode == HttpStatusCode.Forbidden,
            "G1 normale Benutzer und Themen-Admins haben keinen Zugriff auf die PKI-Verwaltung");
        check((await aud.GetAsync("/api/pki/status")).IsSuccessStatusCode && (await aud.GetAsync("/api/pki/revocations")).IsSuccessStatusCode && (await aud.PostAsync("/api/pki/signing/renew", Body(new { }))).StatusCode == HttpStatusCode.Forbidden
              && (await aud.PostAsync($"/api/pki/certificates/{holdId}/revoke", Body(new { reason = "keyCompromise", confirm_serial = holdSerial }))).StatusCode == HttpStatusCode.Forbidden,
            "G2 Auditoren dürfen die PKI nur lesen (Ausstellen/Widerrufen: 403)");
        var noCookie = newClient();
        var svcKey = newClient(); svcKey.DefaultRequestHeaders.Add("X-MCP-Key", "it-test-key");
        var bearer = newClient(); bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "lcm_ungueltig");
        check((await noCookie.GetAsync("/api/pki/status")).StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Redirect && (await svcKey.GetAsync("/api/pki/status")).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
              && (await bearer.GetAsync("/api/pki/status")).StatusCode == HttpStatusCode.Unauthorized && (await svcKey.PostAsync("/api/pki/signing/renew", Body(new { }))).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            "G3 weder anonyme Aufrufe noch der MCP-Dienstschlüssel noch MCP-Tokens erreichen die PKI-Verwaltung");
        check((await adm2.GetAsync("/api/pki/status")).IsSuccessStatusCode, "G4 ein zweiter Administrator darf die PKI verwalten");
        await admin.PutAsync($"/api/admin/users/{adm2Id}/roles", Body(new { roles = Array.Empty<string>() }));
        check((await adm2.PostAsync("/api/pki/signing/renew", Body(new { }))).StatusCode == HttpStatusCode.Forbidden, "G5 entzogene Administratorrolle wirkt sofort (Rolle wird serverseitig gegen die Datenbank geprüft)");
        var audit = (await J(await admin.GetAsync("/api/admin/audit?limit=300")))!.AsArray().Select(a => S(a, "action")).ToList();
        check(new[] { "pki.certificate.renew", "pki.certificate.revoke", "pki.crl.issue" }.All(audit.Contains), "G6 Auditereignisse für Erneuerung, Widerruf und CRL sind vorhanden");
        check(!(await admin.GetStringAsync("/api/admin/audit?limit=300")).Contains("PRIVATE") && !(await admin.GetStringAsync("/api/admin/audit?limit=300")).Contains(keyPassword), "G7 das Audit-Protokoll enthält keine Geheimnisse");

        await PdfFlowAsync(login, newClient, startWeb, stop, getLog, check);
    }

    private static T? Ext<T>(X509Certificate2 c, string oid) where T : X509Extension => c.Extensions.OfType<T>().FirstOrDefault(e => e.Oid?.Value == oid);

    // ─── F/H: eigene Instanz — PDF-Signatur mit der Learny-Kette, Widerruf, Neustart, Schlüsselverlust ───
    private static async Task PdfFlowAsync(Func<string?, Task<HttpClient>> login, Func<HttpClient> newClient,
        Func<string, Dictionary<string, string>, Task<(string Url, Process Proc)>> startWeb, Action<Process?> stop, Func<string> getLog, Action<bool, string> check)
    {
        var keyPass = "pki-instanz-passwort-9876"; var rootPass = "pki-instanz-root-phrase-5432";
        var tmpKeys = Path.Combine(Path.GetTempPath(), "lc-pki-" + Guid.NewGuid().ToString("N")[..8]);
        var env = new Dictionary<string, string> { ["PKI_PUBLIC_URL"] = "{URL}", ["PKI_KEY_DIR"] = tmpKeys, ["PKI_KEY_PASSWORD"] = keyPass, ["PKI_ROOT_PASSPHRASE"] = rootPass, ["PDF_SIGNING_CERT_FILE"] = "", ["PDF_SIGNING_CERT_PFX_BASE64"] = "" };
        var (url, proc) = await startWeb("pki-e2e", env);
        Process? proc2 = null;
        try
        {
            HttpClient Client(string user) { var h = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }; var c = new HttpClient(h) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(60) }; c.GetAsync("/auth/login" + (user.Length == 0 ? "" : "?devUser=" + user)).GetAwaiter().GetResult(); return c; }
            var adm = Client(""); var lea = Client("pki-lea"); var owner = Client("pki-owner");
            var leaId = S(await J(await lea.GetAsync("/api/me")), "id");
            var st = await J(await adm.GetAsync("/api/pki/status"));
            check(S(st, "state") == "operational" && S(st, "public_url") == url, $"F0 eigene Instanz ohne PDF_SIGNING_*: PKI beim ersten Start eingerichtet, Basis-URL aus der Konfiguration ({S(st, "state")})");
            var rootSerial = S(st, "root.serial_number"); var filesBefore = Directory.EnumerateFiles(tmpKeys, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(tmpKeys, f)).OrderBy(x => x).ToList();

            // Modulzertifikat über eine Themenprüfung
            string Mod(string name) => new JsonObject
            {
                ["format"] = "learny-module", ["schema_version"] = 1, ["name"] = name,
                ["cards"] = new JsonArray(Enumerable.Range(1, 3).Select(i => (JsonNode)new JsonObject
                {
                    ["category"] = "K", ["term"] = $"{name}-{i}", ["question"] = $"Frage {name} {i}?", ["definition"] = "D",
                    ["quiz"] = new JsonArray(new JsonObject { ["type"] = "single_choice", ["question"] = $"Quiz {name} {i}?", ["options"] = new JsonArray("falsch1", "richtig", "falsch2", "falsch3"), ["correct_index"] = 1, ["explanation"] = "w" }),
                }).ToArray()),
            }.ToJsonString();
            await owner.PostAsync("/api/modules/import", Raw(Mod("PKI-A")));
            var topicDef = new JsonObject { ["format"] = "learny-topic", ["schema_version"] = 1, ["name"] = "PKI Thema", ["description"] = "x", ["modules"] = new JsonArray(new JsonObject { ["module_name"] = "PKI-A", ["exam"] = new JsonObject { ["enabled"] = true, ["question_count"] = 2, ["pass_percent"] = 60 } }) };
            var tid = S(await J(await owner.PostAsync("/api/topics/import", Raw(topicDef.ToJsonString()))), "id");
            await owner.PostAsync($"/api/topics/{tid}/versions", Body(new { }));
            await owner.PostAsync($"/api/topics/{tid}/shares", Body(new { user_id = leaId }));
            var frozen = (await J(await lea.GetAsync($"/api/topics/{tid}?version=1")))!["modules"]!.AsArray().Single();
            async Task<string?> PassAndGetCertAsync()
            {
                var start = await J(await lea.PostAsync($"/api/topics/{tid}/exams/module", Body(new { module_id = S(frozen, "module_id") })));
                var answers = start!["questions"]!.AsArray().Select(q => new { question_id = S(q, "question_id"), selected_indexes = new[] { Math.Max(0, q!["options"]!.AsArray().Select(v => v!.GetValue<string>()).ToList().IndexOf("richtig")) } }).ToList();
                await lea.PostAsync("/api/quiz/submit", Body(new { module_id = S(start, "module_id"), session_id = S(start, "session_id"), answers }));
                return (await J(await lea.GetAsync("/api/certificates/")))!.AsArray().FirstOrDefault(c => S(c, "kind") == "module") is { } c ? S(c, "id") : null;
            }
            var certId = await PassAndGetCertAsync();
            var pdfRes = await lea.GetAsync($"/api/certificates/{certId}/certificate.pdf");
            var pdf = await pdfRes.Content.ReadAsByteArrayAsync();
            var insp = PdfInspector.Inspect(pdf);
            check(pdfRes.IsSuccessStatusCode && insp.Cms is { Parsed: true, SignatureValid: true } && insp.Cms.Certificates.Count == 3 && insp.Cms.Signer!.Issuer.Contains("Learny Issuing CA"),
                $"F1 das PDF ist mit dem Learny-Signaturzertifikat signiert; die Kette (Signatur, Issuing, Root) ist eingebettet ({insp.Cms?.Certificates.Count} Zertifikate)");
            if (Environment.GetEnvironmentVariable("TEST_DUMP_DIR") is { Length: > 0 } dump)   // nur für die unabhängige Prüfung mit OpenSSL/pyHanko (tools/pki/validate_pki.py)
            {
                Directory.CreateDirectory(dump);
                File.WriteAllBytes(Path.Combine(dump, "pki-signed.pdf"), pdf);
                File.WriteAllBytes(Path.Combine(dump, "pki-root.cer"), await newClientBytes(url, "/pki/certificates/root"));
                File.WriteAllBytes(Path.Combine(dump, "pki-issuing.cer"), await newClientBytes(url, "/pki/certificates/issuing"));
                var ids = (await J(await adm.GetAsync("/api/pki/status")))!;
                File.WriteAllBytes(Path.Combine(dump, "pki-issuing.crl"), await newClientBytes(url, $"/pki/crl/{S(ids, "issuing.id")}.crl"));
                File.WriteAllBytes(Path.Combine(dump, "pki-root.crl"), await newClientBytes(url, $"/pki/crl/{S(ids, "root.id")}.crl"));
                File.WriteAllBytes(Path.Combine(dump, "pki-signing.cer"), await newClientBytes(url, S(ids, "signing.download")));
            }

            async Task<JsonNode?> Upload(byte[] data)
            {
                using var form = new MultipartFormDataContent(); var fc = new ByteArrayContent(data); fc.Headers.ContentType = new MediaTypeHeaderValue("application/pdf"); form.Add(fc, "file", "zertifikat.pdf");
                return await J(await newClient().PostAsync(url + "/api/public/verify/pdf", form));
            }
            var v1 = await Upload(pdf);
            check(S(v1, "overall.valid") == "true" && S(v1, "overall.signer_trust") == "learny_pki" && S(v1, "pdf.pki.cryptographically_valid") == "true" && S(v1, "pdf.pki.chain_valid") == "true" && S(v1, "pdf.pki.issuer_confirmed") == "true"
                  && S(v1, "pdf.pki.revocation") == "good" && S(v1, "pdf.pki.crl_fresh") == "true" && S(v1, "pdf.pki.publicly_trusted") == "false" && S(v1, "overall.summary").Contains("keine öffentliche Anerkennung"),
                "F2 Prüfung: kryptografisch gültig, Aussteller durch die Learny-PKI bestätigt, Widerrufsprüfung ok — öffentlich vertrauenswürdig: nein");
            var f3 = (byte[])pdf.Clone(); f3[pdf.Length / 3] ^= 0x55;
            check(S(await Upload(f3), "overall.valid") == "false", "F3 manipuliertes PDF bleibt ungültig");

            // eingebettetes Root-Zertifikat ist kein Vertrauensanker: Kette mit fremder Root, gleicher Name
            using var fakeRootKey = RSA.Create(3072);
            var fakeReq = new CertificateRequest("CN=Learny Root CA, O=Learny", fakeRootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            fakeReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
            fakeReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            using var fakeRoot = fakeReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            using var sigKey = RSA.Create(2048);
            var sigReq = new CertificateRequest("CN=Learny Document Signing, O=Learny", sigKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            sigReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            sigReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            sigReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.36") }, false));
            using var forged = sigReq.Create(fakeRoot, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), PkiCrypto.NewSerial());
            using var forgedWithKey = forged.CopyWithPrivateKey(sigKey);
            var doc = new PdfBuilder { Title = "Gefälscht" }; doc.AddPage().Text(50, 780, "Gefälscht", 12);
            var forgedPdf = doc.Build(new PdfSignOptions { Certificate = forgedWithKey, Chain = new List<X509Certificate2> { fakeRoot }, Name = "x", Reason = "x" });
            var vf = await Upload(forgedPdf);
            check(S(vf, "overall.signer_trust") != "learny_pki" && S(vf, "pdf.pki.issuer_confirmed") != "true", "F4 ein eingebettetes Fantasie-Root wird nicht zum Vertrauensanker (Anker kommt nur aus dem Register)");

            // Widerruf des Signaturzertifikats → Prüfung zeigt Widerruf
            var signing = st!["signing"]!;
            var rv = await adm.PostAsync($"/api/pki/certificates/{S(signing, "id")}/revoke", Body(new { reason = "keyCompromise", confirm_serial = S(signing, "serial_number") }));
            var v2 = await Upload(pdf);
            check(rv.StatusCode == HttpStatusCode.NoContent && S(v2, "pdf.pki.revocation") == "revoked" && S(v2, "overall.valid") == "false" && S(v2, "overall.signer_revoked") == "true" && S(v2, "overall.summary").Contains("widerrufen"),
                "F5 nach dem Widerruf des Signaturzertifikats zeigt die Prüfung des alten PDFs „widerrufen“ (CRL) und ist nicht mehr gültig");
            var blocked = await lea.GetAsync($"/api/certificates/{certId}/certificate.pdf");
            check(blocked.IsSuccessStatusCode, "F6 bereits gespeicherte PDFs bleiben abrufbar (unveränderlich)");
            var renewed = await adm.PostAsync("/api/pki/signing/renew", Body(new { }));
            check(renewed.IsSuccessStatusCode && S(await J(await adm.GetAsync("/api/pki/status")), "state") == "operational", "F7 nach Erneuerung ist die PKI wieder betriebsbereit");

            var audit2 = (await J(await adm.GetAsync("/api/admin/audit?limit=300")))!.AsArray().Select(a => S(a, "action")).ToList();
            check(new[] { "pki.initialize", "pki.certificate.issue", "pki.certificate.renew", "pki.certificate.revoke", "pki.crl.issue" }.All(audit2.Contains), "G6b Auditereignisse für Initialisierung, Ausstellung, Erneuerung, Widerruf und CRL (frische Instanz)");

            // ─── H: Neustart, Persistenz, Schlüsselverlust ───
            stop(proc);
            (var url2, proc2) = await startWeb("pki-e2e", env);
            var adm2 = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(url2) };
            await adm2.GetAsync("/auth/login");
            var stR = await J(await adm2.GetAsync("/api/pki/status"));
            var certsR = (await J(await adm2.GetAsync("/api/pki/certificates")))!.AsArray();
            var filesAfter = Directory.EnumerateFiles(tmpKeys, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(tmpKeys, f)).Where(f => !f.StartsWith("signing-")).OrderBy(x => x).ToList();
            check(S(stR, "state") == "operational" && S(stR, "root.serial_number") == rootSerial && certsR.Count(c => S(c, "type") == "root_ca") == 1 && filesAfter.SequenceEqual(filesBefore.Where(f => !f.StartsWith("signing-")).OrderBy(x => x).ToList()),
                "H1 Neustart: dieselbe Root CA, keine neue PKI, keine neuen CA-Schlüsseldateien, Migrationen wiederholbar");
            check((await adm2.PostAsync("/api/pki/initialize", Body(new { public_url = url2 }))).StatusCode == HttpStatusCode.BadRequest, "H2 auch nach dem Neustart kann die PKI nicht erneut initialisiert werden");

            // Schlüsselverlust: Signaturschlüssel entfernen → gesperrt, nie neu erzeugen
            var activeKeyFile = Directory.EnumerateFiles(tmpKeys, "signing-*.p12").OrderByDescending(File.GetLastWriteTimeUtc).First();
            var backup = File.ReadAllBytes(activeKeyFile); File.Delete(activeKeyFile);
            await Task.Delay(200);
            // Zustand wird bei jeder Anfrage frisch berechnet; Signatur-Cache läuft nach spätestens 30 s ab — daher Status prüfen und PDF-Neuausstellung über eine zweite Instanz vermeiden
            var stK = await J(await adm2.GetAsync("/api/pki/status"));
            check(S(stK, "state") == "key_unavailable" && S(stK, "message").Contains("nie ein neuer Root-Schlüssel"), $"H3 fehlende Schlüsseldatei: Zustand key_unavailable, Signierung gesperrt ({S(stK, "state")})");
            File.WriteAllBytes(activeKeyFile, backup);
            check(S(await J(await adm2.GetAsync("/api/pki/status")), "state") == "operational", "H4 nach Wiederherstellen der Sicherung ist die PKI wieder betriebsbereit");
            var junk = File.ReadAllBytes(Path.Combine(tmpKeys, "issuing-ca.p12")); File.WriteAllBytes(Path.Combine(tmpKeys, "issuing-ca.p12"), junk.Take(junk.Length / 2).ToArray());
            var stJ = await J(await adm2.GetAsync("/api/pki/status"));
            File.WriteAllBytes(Path.Combine(tmpKeys, "issuing-ca.p12"), junk);
            check(S(stJ, "state") == "key_unavailable" && S(await J(await adm2.GetAsync("/api/pki/status")), "root.serial_number") == rootSerial, "H5 beschädigte Schlüsseldatei: gesperrt, die Root CA bleibt unverändert (keine stille Neuerzeugung)");

            // Widerruf der Issuing CA (Root-Schlüssel nötig): falscher Upload wird abgewiesen, der Server-Schlüssel genügt, danach gesperrt
            var stNow = await J(await adm2.GetAsync("/api/pki/status"));
            var issId = S(stNow, "issuing.id"); var issSerial = S(stNow, "issuing.serial_number");
            using var wrongPfx = new RSACertificateHelper().Create();
            var wrongUp = await adm2.PostAsync($"/api/pki/certificates/{issId}/revoke", Body(new { reason = "cACompromise", confirm_serial = issSerial, root_pfx_base64 = Convert.ToBase64String(wrongPfx.Export(X509ContentType.Pkcs12, "x")), root_passphrase = "x" }));
            check(wrongUp.StatusCode == HttpStatusCode.BadRequest && S(await J(await adm2.GetAsync("/api/pki/status")), "state") == "operational", "H6 Widerruf der Issuing CA mit falschem Root-Schlüssel wird abgewiesen, nichts ändert sich");
            var rm = await adm2.PostAsync("/api/pki/root-key/remove", Body(new { confirmation = "falsch" }));
            check(rm.StatusCode == HttpStatusCode.BadRequest && File.Exists(Path.Combine(tmpKeys, "root-offline", "root-ca.p12")), "H7 das Entfernen des Root-Schlüssels braucht die ausdrückliche Bestätigung");
            var revIss = await adm2.PostAsync($"/api/pki/certificates/{issId}/revoke", Body(new { reason = "cACompromise", confirm_serial = issSerial }));
            var stL = await J(await adm2.GetAsync("/api/pki/status"));
            var rootCrlEntries = PkiCrypto.ParseAndVerifyCrl(await newClientBytes(url2, $"/pki/crl/{S(stL, "root.id")}.crl"), Parse(await newClientBytes(url2, "/pki/certificates/root")));
            check(revIss.StatusCode == HttpStatusCode.NoContent && S(stL, "state") == "locked" && rootCrlEntries.Entries.Count == 1 && rootCrlEntries.SignatureValid,
                "H8 Widerruf der Issuing CA: Root-CRL mit Eintrag, PKI gesperrt (keine Ausstellung, keine Signatur mehr)");
            var lockedRenew = await adm2.PostAsync("/api/pki/signing/renew", Body(new { }));
            check(lockedRenew.StatusCode == HttpStatusCode.BadRequest, "H9 bei widerrufener Issuing CA werden keine Zertifikate mehr ausgestellt");
            var rmOk = await adm2.PostAsync("/api/pki/root-key/remove", Body(new { confirmation = "ROOT-SCHLUESSEL ENTFERNEN" }));
            check(rmOk.StatusCode == HttpStatusCode.NoContent && !Directory.Exists(Path.Combine(tmpKeys, "root-offline")), "H10 der Root-Schlüssel lässt sich nach Bestätigung vom Server entfernen");
            var log = getLog();
            check(!log.Contains(keyPass) && !log.Contains(rootPass), "H11 Protokoll der Instanzen enthält keine Passwörter");
        }
        finally { stop(proc); stop(proc2); try { Directory.Delete(tmpKeys, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static async Task<byte[]> newClientBytes(string url, string path)
    {
        using var c = new HttpClient { BaseAddress = new Uri(url) };
        return await c.GetByteArrayAsync(path);
    }

    private sealed class RSACertificateHelper
    {
        public X509Certificate2 Create()
        {
            using var k = RSA.Create(2048);
            var r = new CertificateRequest("CN=Fremd", k, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var c = r.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pkcs12, "x"), "x", X509KeyStorageFlags.Exportable);
        }
    }
}
