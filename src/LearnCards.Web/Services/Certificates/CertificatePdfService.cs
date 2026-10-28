using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services.Pdf;

namespace LearnCards.Web.Services.Certificates;

public sealed record StoredCertificatePdf(byte[] Data, string Sha256, string FileName, bool Created);

/// <summary>
/// Signierte Zertifikats-PDFs (ADR 0033) für Modulabschlüsse und Themenabschlüsse mit einheitlicher Architektur: Das PDF ist die menschenlesbare Darstellung der unveränderlichen
/// Credentials und Snapshots (keine abweichende Wahrheitsquelle), enthält die signierten Credentials als eingebettete Dateien (<c>/EmbeddedFiles</c>, <c>/AF</c>) und wird zuletzt
/// als Ganzes CMS-signiert (<c>ETSI.CAdES.detached</c>) — erst nach Fertigstellung aller Inhalte, Anhänge und Metadaten. Erzeugte PDFs werden einmalig gespeichert und unverändert ausgeliefert.
/// </summary>
public sealed class CertificatePdfService
{
    private readonly IDatabase _db;
    private readonly CertificateService _certs;
    private readonly CompetencyService _competencies;
    private readonly PdfSigningProvider _signing;

    public CertificatePdfService(IDatabase db, CertificateService certs, CompetencyService competencies, PdfSigningProvider signing)
    {
        _db = db; _certs = certs; _competencies = competencies; _signing = signing;
    }

    private static string Day(DateTime t) => t.ToUniversalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    private static string Pct(double v) => v.ToString("0.#", CultureInfo.InvariantCulture) + " %";

    // ─── Abruf und Ablage ───────────────────────────────────────────────────

    public async Task<StoredCertificatePdf> GetOrCreateAsync(string userId, string certificateId, CancellationToken ct = default)
    {
        var cert = await _certs.GetForUserAsync(userId, certificateId) ?? throw new KeyNotFoundException("Zertifikat nicht gefunden.");
        var name = $"Zertifikat-{cert.Number}.pdf";
        var rows = await _db.QueryAsync("SELECT pdf_base64, pdf_sha256 FROM certificate_pdfs WHERE certificate_id = @id", new[] { ("id", (object?)cert.Id) });
        if (rows.Count > 0) return new(Convert.FromBase64String(rows[0]["pdf_base64"] ?? ""), rows[0]["pdf_sha256"] ?? "", name, false);

        var options = await _signing.GetOptionsAsync();   // wirft PdfSigningUnavailableException, wenn kein Signaturzertifikat konfiguriert ist (fail-closed)
        var pdf = await BuildAsync(cert, options, ct);
        var sha = Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant();
        try
        {
            await _db.ExecuteAsync("""
                INSERT INTO certificate_pdfs (certificate_id, pdf_base64, pdf_sha256, size_bytes, signed, signer_thumbprint, signer_subject, signed_at, created_at)
                VALUES (@id, @b, @h, @n, 1, @tp, @sub, @t, @t)
                """, new (string, object?)[] { ("id", cert.Id), ("b", Convert.ToBase64String(pdf)), ("h", sha), ("n", pdf.Length), ("tp", options.Certificate.Thumbprint), ("sub", options.Certificate.Subject), ("t", options.SigningTime) });
        }
        catch (Exception)
        {
            var raced = await _db.QueryAsync("SELECT pdf_base64, pdf_sha256 FROM certificate_pdfs WHERE certificate_id = @id", new[] { ("id", (object?)cert.Id) });   // parallele Erzeugung: das gespeicherte gilt
            if (raced.Count > 0) return new(Convert.FromBase64String(raced[0]["pdf_base64"] ?? ""), raced[0]["pdf_sha256"] ?? "", name, false);
            throw;
        }
        await _certs.RecordPdfIssuedAsync(userId, cert, sha);
        return new(pdf, sha, name, true);
    }

    // ─── Aufbau ─────────────────────────────────────────────────────────────

    private sealed record ModuleCredentialInfo(Certificate Cert, JsonObject Signed, JsonNode? Snapshot);

    public async Task<byte[]> BuildAsync(Certificate cert, PdfSignOptions options, CancellationToken ct = default)
    {
        var verifyUrl = $"{_certs.BaseUrl}/verify/{cert.Id}";
        var doc = new PdfBuilder
        {
            Title = $"{(cert.IsTest ? "TESTNACHWEIS – " : "")}{(cert.IsTopic ? "Themenabschluss" : "Modulzertifikat")} {cert.ModuleName} – {cert.Number}", Author = "LearnCards (Learny Verified)",
            Subject = cert.IsTopic ? "Comprehensive Learner Record (CLR 2.0) und Open Badge 3.0" : "Open Badge 3.0", Keywords = $"Open Badges 3.0; {(cert.IsTopic ? "CLR 2.0; " : "")}Learny Verified; {cert.Number}",
            CreationDate = options.SigningTime,
        };
        JsonNode? snapshot = null; string? snapshotText = null;
        if (cert.SnapshotId is { } sid && await _competencies.GetAsync(sid) is { } snap) { snapshotText = snap.Json; snapshot = JsonNode.Parse(snap.Json); }

        var qr = QrCode.EncodeText(verifyUrl, QrCode.Ecc.Medium);
        void Chrome(PdfPage p)
        {
            p.Rect(0, PdfPage.Height - 8, PdfPage.Width, 8, PdfColor.Teal);
            p.Text(PdfFlow.Left, PdfPage.Height - 30, "LearnCards", 9, true, PdfColor.Teal);
            p.TextRight(PdfFlow.Right, PdfPage.Height - 30, (cert.IsTest ? "TESTNACHWEIS – " : "") + (cert.IsTopic ? "Themenabschluss – Comprehensive Learner Record" : "Modulzertifikat"), 9, cert.IsTest, cert.IsTest ? PdfColor.Teal : PdfColor.Grey);
        }
        var flow = new PdfFlow(doc, Chrome);
        var files = new List<PdfEmbeddedFile>();
        var moduleInfos = new List<ModuleCredentialInfo>();
        if (cert.IsTopic) await ComposeTopicAsync(doc, flow, cert, snapshot, qr, verifyUrl, moduleInfos, files, options);
        else ComposeModule(doc, flow, cert, snapshot, qr, verifyUrl, files);
        // Der Snapshot wird byte-genau eingebettet: sein SHA-256 entspricht dem im Credential und am Zertifikat genannten Hash
        if (snapshotText is not null) files.Add(new PdfEmbeddedFile("competence-snapshot.json", Encoding.UTF8.GetBytes(snapshotText), "application/json", "Supplement", cert.IsTopic ? "Unveränderlicher Nachweissnapshot des Themenabschlusses" : "Unveränderlicher Prüfungs- und Kompetenznachweis (Snapshot)"));

        // Anhänge: verification.json zuletzt (listet die Hashes aller übrigen Dateien) und öffentlicher Schlüssel
        files.Add(new PdfEmbeddedFile("public-key.jwk", Encoding.UTF8.GetBytes(_certs.Signer.PublicJwk().ToJsonString(new() { WriteIndented = true })), "application/jwk+json", "Supplement", "Öffentlicher Schlüssel (ES256) zur Prüfung der Credential-Signaturen – Echtheit nur über die Aussteller-Adresse bestätigt"));
        files.Add(BuildVerificationFile(cert, files, moduleInfos));
        foreach (var f in files) doc.Files.Add(f);

        // Fuß: Seitenzahl und Kennung auf jeder Seite (erst jetzt bekannt)
        for (var i = 0; i < doc.Pages.Count; i++)
        {
            var p = doc.Pages[i];
            p.Line(PdfFlow.Left, 46, PdfFlow.Right, 46, 0.4, PdfColor.LightGrey);
            p.Text(PdfFlow.Left, 32, $"Zertifikat {cert.Number} · Prüfung: {verifyUrl}", 7.5, false, PdfColor.Grey);
            p.TextRight(PdfFlow.Right, 32, $"Seite {i + 1} von {doc.Pages.Count}", 7.5, false, PdfColor.Grey);
            p.Link(PdfFlow.Left, 28, 360, 12, verifyUrl);
        }
        return doc.Build(options);
    }

    // ─── Modul-PDF ──────────────────────────────────────────────────────────

    private void ComposeModule(PdfBuilder doc, PdfFlow flow, Certificate c, JsonNode? snapshot, QrCode qr, string verifyUrl, List<PdfEmbeddedFile> files)
    {
        var page = flow.Page;
        PdfFlow.DrawSeal(page, PdfFlow.Right - 190, PdfPage.Height - 48, c.IsRevoked);
        flow.Space(8);
        flow.Paragraph("Zertifikat", 26, true, PdfColor.Teal, 0, 6);
        flow.Paragraph("Hiermit wird bestätigt, dass", 10.5, false, PdfColor.Grey);
        flow.Paragraph(c.LearnerName, 20, true, null, 0, 6);
        flow.Paragraph("das Lernmodul", 10.5, false, PdfColor.Grey);
        flow.Paragraph(c.ModuleName + (c.ModuleVersion > 0 ? $" (Version {c.ModuleVersion})" : ""), 16, true, null, 0, 4);
        flow.Paragraph("erfolgreich abgeschlossen hat.", 10.5, false, PdfColor.Grey, 0, 8);
        flow.Paragraph($"Prüfungsergebnis: {Pct(c.ScorePercent)} – {GermanGrade.Text(c.ScorePercent)} · bestanden (Bestehensgrenze {c.PassPercent} %)", 11, true, PdfColor.Green, 0, 8);

        flow.Heading("Angaben zum Nachweis");
        flow.KeyValue("Zertifikatsnummer", c.Number);
        flow.KeyValue("Ausgestellt am", Day(c.IssuedAt));
        flow.KeyValue("Abschlussdatum", Day(c.CompletedAt ?? c.IssuedAt));
        flow.KeyValue("Modul", c.ModuleName + (c.ModuleVersion > 0 ? $" (Version {c.ModuleVersion})" : ""));
        flow.KeyValue("Aussteller", $"LearnCards – {_certs.IssuerId}");
        flow.KeyValue("Status", c.IsRevoked ? $"WIDERRUFEN am {Day(c.RevokedAt ?? c.IssuedAt)}: {c.RevokedReason}" : "Bei Erstellung dieses Dokuments gültig. Den aktuellen Status (Widerruf) zeigt die Online-Prüfung.");

        ComposeCompetencies(flow, snapshot, includeHeading: true);

        flow.Heading("Bewertungskriterien und Nachweisreferenzen");
        flow.Paragraph($"Das serverseitig bewertete Modulquiz wurde mit mindestens {c.PassPercent} % bestanden. Kompetenzen werden nur bescheinigt, wenn ausreichend viele ihrer Fragen geprüft und die Bestehensgrenze der Kompetenz erreicht wurde.", 9.5);
        if (snapshot is not null)
        {
            flow.KeyValue("Prüfungsnachweis (Snapshot)", $"{_certs.BaseUrl}/api/public/certificates/{c.Id}/evidence");
            flow.KeyValue("Snapshot-Hash (SHA-256)", c.SnapshotHash);
            flow.KeyValue("Version der Kriterien", S(snapshot, "exam.criteria_version"));
            flow.KeyValue("Modulversion / Fragen", $"{S(snapshot, "module.version")} / {S(snapshot, "exam.questions")} Fragen");
        }
        else flow.Paragraph("Dieses Zertifikat wurde vor Einführung der Kompetenznachweise ausgestellt und enthält keine Kompetenzangaben.", 9.5, false, PdfColor.Grey);

        ComposeVerificationBlock(flow, c, qr, verifyUrl, new[] { ("Open Badge (Modulzertifikat)", $"{_certs.BaseUrl}/verify/{c.Id}") });

        var signed = _certs.SignedBadgeJson(c);
        files.Add(new PdfEmbeddedFile("module-credential.json", Encoding.UTF8.GetBytes(signed.ToJsonString(new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })), "application/ld+json", "Data", "Signiertes Modulzertifikat (Open Badge 3.0, JSON-LD mit eingebettetem VC-JWT)"));
        files.Add(new PdfEmbeddedFile("module-credential.jwt", Encoding.ASCII.GetBytes(c.BadgeJws), "application/jwt", "Data", "Kompakte JWS (VC-JWT, ES256) des Modulzertifikats"));
    }

    private static string S(JsonNode? n, string path)
    {
        var cur = n;
        foreach (var part in path.Split('.')) cur = cur is JsonArray a ? (int.TryParse(part, out var i) && i < a.Count ? a[i] : null) : (cur as JsonObject)?[part];
        return cur?.ToString() ?? "";
    }

    /// <summary>Nachgewiesene Kompetenzen (Tabelle); nicht oder nicht ausreichend geprüfte stehen getrennt und eindeutig als solche gekennzeichnet.</summary>
    private static void ComposeCompetencies(PdfFlow flow, JsonNode? snapshot, bool includeHeading)
    {
        var comps = snapshot?["competencies"]?.AsArray();
        if (comps is null || comps.Count == 0) return;
        var objectives = (snapshot?["objectives"]?.AsArray() ?? new JsonArray()).ToDictionary(o => S(o, "id"), o => S(o, "text"));
        var shown = comps.Where(k => S(k, "status") == CompetencyStatus.Demonstrated).ToList();
        if (includeHeading) flow.Heading("Nachgewiesene Kompetenzen");
        if (shown.Count == 0) flow.Paragraph("In diesem Prüfungsversuch wurde keine Kompetenz nachgewiesen.", 9.5, false, PdfColor.Grey);
        else flow.Table(new[] { "Kompetenz", "Lernziel", "Ergebnis", "Bewertungskriterium", "Methode" }, new[] { 120.0, 120, 55, 130, 70 },
            shown.Select(k => new[] { S(k, "name"), objectives.GetValueOrDefault(S(k, "objective_id"), "–"), $"{S(k, "percent")} % ({S(k, "questions_asked")} Fragen)", $"ab {S(k, "pass_percent")} % bei mind. {S(k, "min_questions")} Fragen", "Modulquiz (serverseitig bewertet)" }));
        var others = comps.Where(k => S(k, "status") != CompetencyStatus.Demonstrated).ToList();
        if (others.Count > 0)
        {
            flow.Paragraph("Nicht Bestandteil dieses Nachweises (nicht nachgewiesen bzw. nicht ausreichend geprüft):", 9, true, PdfColor.Grey);
            foreach (var k in others)
                flow.Paragraph($"• {S(k, "name")} – {(S(k, "status") == CompetencyStatus.NotDemonstrated ? $"nicht nachgewiesen ({S(k, "percent")} % bei Grenze {S(k, "pass_percent")} %)" : $"nicht ausreichend geprüft ({S(k, "questions_asked")} von mind. {S(k, "min_questions")} Fragen)")}", 8.5, false, PdfColor.Grey, 8, 1);
        }
    }

    private void ComposeVerificationBlock(PdfFlow flow, Certificate c, QrCode qr, string verifyUrl, IEnumerable<(string Label, string Id)> credentialIds)
    {
        flow.Heading("Verifikation");
        const double qrSize = 96;
        flow.Ensure(qrSize + 10);
        var top = flow.Y;
        flow.Page.Qr(PdfFlow.Right - qrSize, top - qrSize, qrSize, qr);
        flow.Page.Link(PdfFlow.Right - qrSize, top - qrSize, qrSize, qrSize, verifyUrl);
        var lines = new List<string>
        {
            "Öffentliche Prüfung (QR-Code oder Adresse):", verifyUrl, "",
            "Dieses Dokument ist digital signiert (CMS/CAdES). Die Credentials liegen als Dateien im PDF (Büroklammer-Ansicht bzw. Anhänge) und sind einzeln mit dem Aussteller-Schlüssel geprüft.",
            $"Signatur der Credentials: ES256 (VC-JWT) · Schlüssel-ID {_certs.Signer.KeyId} · {_certs.BaseUrl}/.well-known/jwks.json",
        };
        var y = top;
        foreach (var l in lines)
            foreach (var w in PdfText.Wrap(l, 8.5, flow.Width - qrSize - 12)) { flow.Page.Text(PdfFlow.Left, y - 8.5, w, 8.5, l == verifyUrl, l == verifyUrl ? PdfColor.Teal : null); y -= 11.5; }
        flow.Y = Math.Min(y, top - qrSize) - 6;
        foreach (var (label, id) in credentialIds) flow.KeyValue(label, id, 150, 8.5);
        flow.Paragraph("Widerruf: Die Online-Prüfung zeigt den aktuellen Widerrufsstatus; eine Kopie dieses PDFs ersetzt sie nicht. Das Siegel „Learny Verified“ bestätigt die Prüfung durch Learny und ist keine Zertifizierung durch 1EdTech oder LinkedIn.", 8, false, PdfColor.Grey);
    }

    // ─── Themen-PDF ─────────────────────────────────────────────────────────

    private async Task ComposeTopicAsync(PdfBuilder doc, PdfFlow flow, Certificate c, JsonNode? snapshot, QrCode qr, string verifyUrl, List<ModuleCredentialInfo> moduleInfos, List<PdfEmbeddedFile> files, PdfSignOptions options)
    {
        var clr = _certs.SignedClrJson(c) ?? throw new InvalidOperationException("Zum Themenabschluss fehlt das CLR.");
        var master = _certs.SignedBadgeJson(c);
        foreach (var vc in clr["credentialSubject"]!["verifiableCredential"]!.AsArray())
        {
            // Neues Profil: eingebettete Credentials sind kompakte JWS (das JWT ist der Proof); ältere sind Objekte mit Hülle
            var vcObj = vc is JsonValue jv && jv.TryGetValue<string>(out var jws) ? BadgeSigner.CredentialFromJws(jws) : vc!.AsObject();
            var id = vcObj["id"]!.GetValue<string>();
            if (id.EndsWith("/verify/" + c.Id, StringComparison.Ordinal)) continue;   // Master-Badge
            var mc = await _certs.GetRawAsync(id[(id.LastIndexOf('/') + 1)..]);
            if (mc is null) continue;
            JsonNode? ms = null;
            if (mc.SnapshotId is { } sid && await _competencies.GetAsync(sid) is { } s) ms = JsonNode.Parse(s.Json);
            moduleInfos.Add(new ModuleCredentialInfo(mc, (JsonObject)vcObj.DeepClone(), ms));
        }
        var done = S(snapshot, "completed_at");
        var moduleLines = (snapshot?["modules"]?.AsArray() ?? new JsonArray());
        var fin = snapshot?["final_exam"];

        // Seite 1 – Gesamtabschluss
        PdfFlow.DrawSeal(flow.Page, PdfFlow.Right - 190, PdfPage.Height - 48, c.IsRevoked);
        flow.Space(8);
        flow.Paragraph("Themenabschluss", 26, true, PdfColor.Teal, 0, 6);
        flow.Paragraph("Hiermit wird bestätigt, dass", 10.5, false, PdfColor.Grey);
        flow.Paragraph(c.LearnerName, 20, true, null, 0, 6);
        flow.Paragraph("das Thema", 10.5, false, PdfColor.Grey);
        flow.Paragraph($"{c.ModuleName} (Version {c.ModuleVersion})", 16, true, null, 0, 4);
        flow.Paragraph("erfolgreich abgeschlossen hat.", 10.5, false, PdfColor.Grey, 0, 8);
        flow.Heading("Gesamtabschluss");
        flow.KeyValue("Zertifikatsnummer", c.Number);
        flow.KeyValue("Themenversion", c.ModuleVersion.ToString(CultureInfo.InvariantCulture));
        flow.KeyValue("Abschlussdatum", Day(c.CompletedAt ?? c.IssuedAt));
        flow.KeyValue("Ausgestellt am", Day(c.IssuedAt));
        flow.KeyValue("Gesamtergebnis", $"{Pct(c.ScorePercent)} – {GermanGrade.Text(c.ScorePercent)}");
        flow.KeyValue("Master-Abschluss-Badge", $"„Erfolgreicher Themenabschluss“ · {_certs.BaseUrl}/verify/{c.Id}");
        flow.KeyValue("Status", c.IsRevoked ? $"WIDERRUFEN am {Day(c.RevokedAt ?? c.IssuedAt)}: {c.RevokedReason}" : "Bei Erstellung dieses Dokuments gültig. Den aktuellen Status zeigt die Online-Prüfung.");
        flow.Heading("Abschlusskriterien");
        flow.Paragraph(S(master, "credentialSubject.achievement.criteria.narrative"), 9.5);
        flow.KeyValue("Erforderliche Module", $"{moduleLines.Count} (davon {moduleLines.Count(l => S(l, "passed") == "true")} mit bestandener Modulprüfung)");
        flow.KeyValue("Abschlussprüfung", fin is null ? "nicht vorgesehen" : $"bestanden ({S(fin, "percent")} %, Bestehensgrenze {S(fin, "pass_percent")} %)");
        flow.Space(6);
        const double qrSize = 96;
        flow.Ensure(qrSize + 30);
        flow.Page.Qr(PdfFlow.Right - qrSize, flow.Y - qrSize, qrSize, qr);
        flow.Page.Link(PdfFlow.Right - qrSize, flow.Y - qrSize, qrSize, qrSize, verifyUrl);
        flow.Page.Text(PdfFlow.Left, flow.Y - 10, "Öffentliche Prüfung (QR-Code oder Adresse):", 9, true);
        flow.Page.Text(PdfFlow.Left, flow.Y - 24, verifyUrl, 9, false, PdfColor.Teal);
        flow.Y -= qrSize + 8;

        // Seite 2 – Modulübersicht
        flow.NewPage();
        flow.Heading("Modulübersicht");
        flow.Paragraph("Alle erforderlichen Module dieser Themenversion mit der jeweils festgelegten Modulversion. Jeder Modulabschluss ist ein eigenständiges, signiertes Open Badge im CLR.", 9.5);
        flow.Table(new[] { "Modul", "Version", "Status", "Ergebnis", "Abschluss", "Nachweis" }, new[] { 125.0, 42, 62, 55, 58, 153 },
            moduleLines.Select(l => new[]
            {
                S(l, "name"), S(l, "version") is { Length: > 0 } v && v != "0" ? v : "–", S(l, "passed") == "true" ? "bestanden" : "gelernt (ohne Prüfung)",
                S(l, "percent") is { Length: > 0 } p ? $"{p} %" : "–", S(l, "completed_at") is { Length: > 0 } d ? Day(DateTime.Parse(d, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)) : "–",
                S(l, "certificate_number") is { Length: > 0 } n ? $"Zertifikat {n}" : "kein Einzelnachweis",
            }));

        // Weitere Seiten – Kompetenznachweise je Modul
        flow.NewPage();
        flow.Heading("Kompetenznachweise");
        foreach (var info in moduleInfos)
        {
            flow.Ensure(60);
            flow.Paragraph($"{info.Cert.ModuleName}  ·  Version {(info.Cert.ModuleVersion > 0 ? info.Cert.ModuleVersion.ToString(CultureInfo.InvariantCulture) : "–")}  ·  Zertifikat {info.Cert.Number}", 11, true, null, 0, 2);
            var objectives = info.Snapshot?["objectives"]?.AsArray();
            if (objectives is { Count: > 0 }) flow.Paragraph("Lernziele: " + string.Join("; ", objectives.Select(o => S(o, "text"))), 9, false, PdfColor.Grey, 0, 2);
            flow.Paragraph($"Bewertungsmethode: serverseitig bewertetes Modulquiz · Ergebnis {Pct(info.Cert.ScorePercent)} (Grenze {info.Cert.PassPercent} %) · Prüfungs-/Snapshot-Version {S(info.Snapshot, "snapshot_id")}", 8.5, false, PdfColor.Grey, 0, 2);
            if (info.Snapshot is not null) ComposeCompetencies(flow, info.Snapshot, includeHeading: false);
            else flow.Paragraph("Keine Kompetenzangaben (Zertifikat vor Einführung der Kompetenznachweise ausgestellt).", 8.5, false, PdfColor.Grey);
            flow.KeyValue("Nachweisreferenz", $"{_certs.BaseUrl}/verify/{info.Cert.Id}  ·  Snapshot-SHA-256 {info.Cert.SnapshotHash}", 110, 8);
            flow.Space(6);
        }
        if (moduleInfos.Count == 0) flow.Paragraph("In dieser Themenversion gab es keine Module mit Einzelnachweis.", 9.5, false, PdfColor.Grey);

        // Abschlussprüfung
        if (fin is not null)
        {
            flow.Heading("Themenabschlussprüfung");
            flow.KeyValue("Prüfungsbezeichnung", S(fin, "name"));
            flow.KeyValue("Prüfungsversion", $"Themenversion {S(fin, "version")}");
            flow.KeyValue("Bestehensgrenze", $"{S(fin, "pass_percent")} %");
            flow.KeyValue("Erreichtes Ergebnis", $"{S(fin, "percent")} %");
            flow.KeyValue("Abschlussstatus", "bestanden");
            flow.KeyValue("Verifizierter Prüfungszeitpunkt", Day(DateTime.Parse(S(fin, "completed_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)));
        }

        // Letzte Seite – Verifikation
        flow.NewPage();
        flow.Heading("Verifikation");
        flow.KeyValue("CLR-Credential-ID", $"{_certs.BaseUrl}/verify/{c.Id}/clr");
        flow.KeyValue("Master-Badge-ID", $"{_certs.BaseUrl}/verify/{c.Id}");
        flow.KeyValue("Aussteller", $"LearnCards – {_certs.IssuerId}");
        flow.KeyValue("Signaturalgorithmus", "ES256 (ECDSA P-256 / SHA-256), VC-JWT; PDF: CMS/CAdES (SHA-256)");
        flow.KeyValue("Schlüssel-ID", _certs.KeyId);
        flow.KeyValue("Schlüsselverzeichnis", $"{_certs.BaseUrl}/.well-known/jwks.json");
        flow.KeyValue("Verifikationsadresse", verifyUrl);
        flow.Space(2);
        flow.Paragraph("Referenzierte Credentials", 9.5, true);
        foreach (var info in moduleInfos) flow.Paragraph($"• Modulabschluss {info.Cert.ModuleName}: {_certs.BaseUrl}/verify/{info.Cert.Id}", 8, false, null, 4, 1);
        flow.Space(8);
        flow.Ensure(104);
        flow.Page.Qr(PdfFlow.Left, flow.Y - 96, 96, qr);
        flow.Page.Link(PdfFlow.Left, flow.Y - 96, 96, 96, verifyUrl);
        flow.Page.Text(PdfFlow.Left + 108, flow.Y - 30, "Öffentliche Prüfung (QR-Code oder Adresse):", 9, true);
        flow.Page.Text(PdfFlow.Left + 108, flow.Y - 44, verifyUrl, 9, false, PdfColor.Teal);
        flow.Y -= 108;
        flow.Heading("Kryptografische Prüfung");
        flow.Paragraph("1. PDF-Signatur: Das gesamte Dokument einschließlich aller eingebetteten Dateien ist digital signiert (CMS/CAdES). Jede Änderung nach der Signierung macht die Signatur ungültig.", 9);
        flow.Paragraph("2. Eingebettete Credentials: clr-credential.json, master-badge.json und module-credentials.json enthalten die signierten Original-Credentials (die zugehörigen VC-JWTs liegen als .jwt-Dateien bei). Sie lassen sich unabhängig aus dem PDF extrahieren und prüfen.", 9);
        flow.Paragraph("3. Hashes: verification.json nennt SHA-256-Prüfsummen aller eingebetteten Dateien und der referenzierten Credentials.", 9);
        flow.Paragraph("4. Widerruf: Der aktuelle Widerrufsstatus ist nur online prüfbar (Verifikationsadresse oder Upload dieses PDFs unter /verify). Offline lassen sich Signaturen und Hashes prüfen, soweit der Schlüssel des Ausstellers bekannt ist.", 9);
        flow.Paragraph("Das Siegel „Learny Verified“ bestätigt die von Learny durchgeführte Prüfung. Es handelt sich nicht um eine Zertifizierung durch 1EdTech oder LinkedIn; Open Badges 3.0 und CLR 2.0 werden gemäß den offenen Spezifikationen verwendet.", 8.5, false, PdfColor.Grey);

        var pretty = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        byte[] J(JsonNode n) => Encoding.UTF8.GetBytes(n.ToJsonString(pretty));
        files.Add(new PdfEmbeddedFile("clr-credential.json", J(clr), "application/ld+json", "Data", "Signiertes Comprehensive Learner Record (CLR 2.0, JSON-LD mit eingebettetem VC-JWT)"));
        files.Add(new PdfEmbeddedFile("clr-credential.jwt", Encoding.ASCII.GetBytes(c.ClrJws ?? ""), "application/jwt", "Data", "Kompakte JWS (VC-JWT, ES256) des CLR"));
        files.Add(new PdfEmbeddedFile("master-badge.json", J(master), "application/ld+json", "Data", "Signiertes Master-Abschluss-Badge (Open Badge 3.0)"));
        files.Add(new PdfEmbeddedFile("master-badge.jwt", Encoding.ASCII.GetBytes(c.BadgeJws), "application/jwt", "Data", "Kompakte JWS (VC-JWT, ES256) des Master-Badges"));
        files.Add(new PdfEmbeddedFile("module-credentials.json", J(new JsonObject { ["format"] = "learny-module-credentials", ["credentials"] = new JsonArray(moduleInfos.Select(m => (JsonNode)m.Signed.DeepClone()).ToArray()) }), "application/ld+json", "Data", "Signierte Modulabschlüsse (Open Badges 3.0), identisch mit den im CLR eingebetteten"));
        var snapDoc = new JsonObject
        {
            ["format"] = "learny-module-competence-snapshots",
            ["modules"] = new JsonArray(moduleInfos.Where(m => m.Snapshot is not null).Select(m => (JsonNode)new JsonObject { ["certificate_id"] = m.Cert.Id, ["snapshot"] = m.Snapshot!.DeepClone() }).ToArray()),
        };
        files.Add(new PdfEmbeddedFile("module-competence-snapshots.json", J(snapDoc), "application/json", "Supplement", "Unveränderliche Prüfungs- und Kompetenznachweise der Module (Snapshots)"));
    }

    // ─── verification.json ──────────────────────────────────────────────────

    private PdfEmbeddedFile BuildVerificationFile(Certificate c, List<PdfEmbeddedFile> others, List<ModuleCredentialInfo> moduleInfos)
    {
        var baseUrl = _certs.BaseUrl;
        var v = new JsonObject
        {
            ["format"] = "learny-verification", ["schema_version"] = 1, ["certificate_id"] = c.Id, ["certificate_number"] = c.Number, ["kind"] = c.Kind,
            ["issuer"] = new JsonObject { ["id"] = _certs.IssuerId, ["name"] = "LearnCards", ["url"] = baseUrl },
            [c.IsTopic ? "topic" : "module"] = new JsonObject { ["name"] = c.ModuleName, ["version"] = c.ModuleVersion },
            ["credentials"] = new JsonObject
            {
                ["clr_credential_id"] = c.IsTopic ? $"{baseUrl}/verify/{c.Id}/clr" : null,
                ["master_badge_id"] = c.IsTopic ? $"{baseUrl}/verify/{c.Id}" : null,
                ["badge_id"] = c.IsTopic ? null : $"{baseUrl}/verify/{c.Id}",
                ["module_credential_ids"] = new JsonArray(moduleInfos.Select(m => (JsonNode)$"{baseUrl}/verify/{m.Cert.Id}").ToArray()),
            },
            ["signature"] = new JsonObject
            {
                ["algorithm"] = "ES256", ["key_id"] = _certs.Signer.KeyId, ["kid"] = _certs.KeyId, ["proof_format"] = "VC-JWT nach OB 3.0 §8.2 (compact JWS, typ JWT, ES256); die zugehörige JWS liegt als .jwt-Datei bei (ältere Pakete tragen sie zusätzlich in proof.jwt)",
                ["jwks_url"] = baseUrl + "/.well-known/jwks.json", ["public_key_file"] = "public-key.jwk",
            },
            ["standards"] = new JsonArray("1EdTech Open Badges 3.0 (JSON-LD context 3.0.3)", "1EdTech CLR Standard 2.0 (JSON-LD context 2.0.1)", "W3C Verifiable Credentials Data Model 2.0"),
            ["conformance_note"] = "Strukturen nach den offenen Spezifikationen; keine 1EdTech-Zertifizierung, keine LinkedIn-Zertifizierung.",
            ["verification_url"] = $"{baseUrl}/verify/{c.Id}", ["verify_api"] = baseUrl + "/api/public/verify", ["status_api"] = $"{baseUrl}/api/public/certificates/{c.Id}/status",
            ["revocation_list"] = baseUrl + "/api/public/revocation-list",
            ["hashes"] = new JsonObject
            {
                ["clr_jws_sha256"] = c.IsTopic ? c.ClrHash : null, ["master_badge_jws_sha256"] = c.IsTopic ? c.BadgeHash : null, ["badge_jws_sha256"] = c.IsTopic ? null : c.BadgeHash,
                ["competence_snapshot_sha256"] = c.SnapshotHash.Length > 0 ? c.SnapshotHash : null,
                ["referenced_credentials"] = new JsonArray(moduleInfos.Select(m => (JsonNode)new JsonObject { ["id"] = $"{baseUrl}/verify/{m.Cert.Id}", ["jws_sha256"] = m.Cert.BadgeHash, ["snapshot_sha256"] = m.Cert.SnapshotHash.Length > 0 ? m.Cert.SnapshotHash : null }).ToArray()),
                ["embedded_files"] = new JsonArray(others.Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["size"] = f.Data.Length, ["media_type"] = f.MimeType, ["sha256"] = Convert.ToHexString(SHA256.HashData(f.Data)).ToLowerInvariant() }).ToArray()),
            },
        };
        return new PdfEmbeddedFile("verification.json", Encoding.UTF8.GetBytes(v.ToJsonString(new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })), "application/json", "Supplement",
            "Verifikationsdaten: Kennungen, Algorithmen, Schlüssel-ID, Standards und SHA-256-Prüfsummen der übrigen eingebetteten Dateien");
    }
}
