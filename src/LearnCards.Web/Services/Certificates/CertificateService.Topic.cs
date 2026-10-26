using System.Globalization;
using System.Text.Json.Nodes;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services.Certificates;

/// <summary>
/// Themenabschluss (ADR 0032): Ein verifizierter Themenabschluss erzeugt unter <b>einer</b> Zertifikatsidentität zwei signierte Credentials —
/// das Master-Badge (<c>OpenBadgeCredential</c>, Open Badges 3.0: „Erfolgreicher Themenabschluss“) und das CLR (<c>ClrCredential</c>, CLR Standard 2.0), das die Modulabschlüsse
/// (eigenständige OpenBadgeCredentials) und das Master-Badge als eingebettete Credentials bündelt. Das Master-Badge verweist auf die Modulnachweise nur als Evidence (keine Zirkel).
/// </summary>
public sealed partial class CertificateService
{
    public const string ClrContext = "https://purl.imsglobal.org/spec/clr/v2p0/context-2.0.1.json";
    public const string Ob3Context = "https://purl.imsglobal.org/spec/ob/v3p0/context-3.0.3.json";
    public const string Vc2Context = "https://www.w3.org/ns/credentials/v2";

    /// <summary>Eingabe der Ausstellung eines Themenabschlusses (alles bereits serverseitig verifiziert).</summary>
    public sealed record TopicCompletionInput(string UserId, string TopicId, string TopicName, int TopicVersion, DateTime CompletedAt, double ScorePercent, int PassPercent, string? ResultId,
        bool FinalExamRequired, IReadOnlyList<Certificate> ModuleCertificates);

    public async Task<Certificate?> FindTopicAsync(string userId, string topicId, int version)
    {
        var rows = await _db.QueryAsync("SELECT * FROM certificates WHERE user_id = @u AND module_id = @m AND module_version = @v AND kind = 'topic'",
            new (string, object?)[] { ("u", userId), ("m", topicId), ("v", version) });
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    /// <summary>
    /// Stellt den Themenabschluss aus (idempotent je Benutzer, Thema und Themenversion; der Abschlusszeitpunkt bleibt unverändert). <paramref name="certificateId"/> ist vorab bekannt,
    /// weil der Prüfungssnapshot darauf verweist. Fehler dürfen Prüfungsergebnisse nie gefährden — der Aufrufer fängt Ausnahmen.
    /// </summary>
    public async Task<Certificate?> IssueTopicAsync(TopicCompletionInput inp, string certificateId, ExamSnapshot snapshot)
    {
        var existing = await FindTopicAsync(inp.UserId, inp.TopicId, inp.TopicVersion);
        if (existing is not null) return existing;
        var learner = (await _users.GetAsync(inp.UserId))?.DisplayName ?? "Lernende/r";
        var settings = await _chain.GetAsync();
        var cert = new Certificate
        {
            Id = certificateId, Kind = "topic", Number = NewNumber(), UserId = inp.UserId, ModuleId = inp.TopicId, ModuleName = inp.TopicName, ModuleVersion = inp.TopicVersion, ModuleScope = "topic",
            ResultId = inp.ResultId, LearnerName = learner, ScorePercent = Math.Round(inp.ScorePercent, 1), PassPercent = inp.PassPercent, IssuedAt = DateTime.UtcNow, CompletedAt = inp.CompletedAt,
            SnapshotId = snapshot.Id, SnapshotHash = snapshot.Hash,
        };
        var master = BuildMasterBadge(cert, inp, snapshot);
        cert.BadgeJws = _signer.Sign(master, KeyId);
        cert.BadgeHash = BadgeSigner.HashHex(cert.BadgeJws);
        cert.LeafHash = MerkleTree.Hex(MerkleTree.Leaf(MerkleTree.FromHex(cert.BadgeHash)));
        var clr = BuildClr(cert, inp);
        cert.ClrJws = _signer.Sign(clr, KeyId);
        cert.ClrHash = BadgeSigner.HashHex(cert.ClrJws);
        if (settings.Enabled) { cert.ChainStatus = Domain.ChainStatus.Pending; cert.ChainNetwork = settings.Network; cert.ChainId = settings.ChainId; }
        try { await InsertAsync(cert); }
        catch (Exception)
        {
            var raced = await FindTopicAsync(inp.UserId, inp.TopicId, inp.TopicVersion);   // paralleler Abschluss: das vorhandene Zertifikat gilt
            if (raced is not null) return raced;
            throw;
        }
        await _users.WriteAuditAsync(inp.UserId, "certificate.issue", cert.Id, $"number={cert.Number};topic={inp.TopicId};version={inp.TopicVersion};kind=topic");
        return cert;
    }

    /// <summary>Protokolliert die Erzeugung des signierten PDFs (nur Hash, nie der Inhalt).</summary>
    public Task RecordPdfIssuedAsync(string userId, Certificate c, string pdfSha256) => _users.WriteAuditAsync(userId, "certificate.pdf", c.Id, $"number={c.Number};sha256={pdfSha256}");

    /// <summary>Angaben für „Auf LinkedIn hinzufügen“ (ADR 0034): reine Hilfe zum Eintragen durch die Person selbst — nichts wird veröffentlicht, es gibt keine LinkedIn-API-Anbindung.</summary>
    public sealed record LinkedInShareInfo(string Name, string Organization, string CertificateId, int IssueYear, int IssueMonth, string VerifyUrl, string AddUrl);

    public LinkedInShareInfo LinkedInShare(Certificate c)
    {
        var url = $"{BaseUrl}/verify/{c.Id}";
        var name = Truncate(c.IsTopic ? $"{c.ModuleName} – Themenabschluss" : $"{c.ModuleName} – Modulabschluss", 100);
        var issued = c.IssuedAt.ToUniversalTime();
        // „Add to Profile“-Startpunkt von LinkedIn; die Vorbelegung ist nicht garantiert (LinkedIn kann sie ignorieren) — daher zusätzlich die Werte zum Eintragen
        var add = "https://www.linkedin.com/profile/add?startTask=CERTIFICATION_NAME"
            + $"&name={Uri.EscapeDataString(name)}&organizationName={Uri.EscapeDataString("Learny")}&issueYear={issued.Year}&issueMonth={issued.Month}"
            + $"&certUrl={Uri.EscapeDataString(url)}&certId={Uri.EscapeDataString(c.Number)}";
        return new LinkedInShareInfo(name, "Learny (LearnCards)", c.Number, issued.Year, issued.Month, url, add);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private JsonObject IssuerProfile() => new() { ["id"] = IssuerId, ["type"] = new JsonArray("Profile"), ["name"] = "LearnCards", ["url"] = BaseUrl };

    private JsonObject TopicAchievement(Certificate c, TopicCompletionInput i)
    {
        var id = $"{BaseUrl}/badges/achievements/topics/{c.ModuleId}/{c.ModuleVersion}";
        var narrative = "Alle erforderlichen Module der angegebenen Themenversion erfolgreich bestanden." + (i.FinalExamRequired ? " Zusätzlich wurde die verpflichtende Abschlussprüfung bestanden." : "");
        return new JsonObject
        {
            ["id"] = id, ["type"] = new JsonArray("Achievement"), ["achievementType"] = "CertificateOfCompletion", ["name"] = "Erfolgreicher Themenabschluss",
            ["description"] = $"Das Thema „{c.ModuleName}“ (Version {c.ModuleVersion}) wurde erfolgreich abgeschlossen.", ["version"] = c.ModuleVersion.ToString(CultureInfo.InvariantCulture),
            ["criteria"] = new JsonObject { ["id"] = id + "/criteria", ["narrative"] = narrative },
        };
    }

    /// <summary>Master-Badge: <c>OpenBadgeCredential</c> „Erfolgreicher Themenabschluss“. Die Modulabschlüsse und der Prüfungssnapshot sind als <c>evidence</c> referenziert.</summary>
    public JsonObject BuildMasterBadge(Certificate c, TopicCompletionInput i, ExamSnapshot snapshot)
    {
        var evidence = new JsonArray(new JsonObject
        {
            ["id"] = $"{BaseUrl}/api/public/certificates/{c.Id}/evidence", ["type"] = new JsonArray("Evidence"), ["name"] = "Nachweissnapshot des Themenabschlusses",
            ["description"] = $"Unveränderlicher Nachweissnapshot (SHA-256 {snapshot.Hash}).", ["genre"] = "Exam",
            ["narrative"] = $"{i.ModuleCertificates.Count} Modulabschluss(e){(i.FinalExamRequired ? " und die Abschlussprüfung" : "")}; Snapshot-Hash: {snapshot.Hash}.",
        });
        foreach (var m in i.ModuleCertificates)
            evidence.Add(new JsonObject
            {
                ["id"] = $"{BaseUrl}/verify/{m.Id}", ["type"] = new JsonArray("Evidence"), ["name"] = $"Modulabschluss {m.ModuleName}{(m.ModuleVersion > 0 ? " (Version " + m.ModuleVersion + ")" : "")}",
                ["description"] = $"Eigenständiges Open Badge, Zertifikat {m.Number}.", ["genre"] = "Credential",
            });
        return new JsonObject
        {
            ["@context"] = new JsonArray(Vc2Context, Ob3Context), ["id"] = $"{BaseUrl}/verify/{c.Id}", ["type"] = new JsonArray("VerifiableCredential", "OpenBadgeCredential"),
            ["issuer"] = IssuerProfile(), ["validFrom"] = Iso(c.IssuedAt), ["name"] = $"{c.ModuleName} — Themenabschluss",
            ["credentialSubject"] = new JsonObject
            {
                ["type"] = new JsonArray("AchievementSubject"), ["identifier"] = new JsonArray(Identity(c.LearnerName, true)), ["activityEndDate"] = Iso(c.CompletedAt ?? c.IssuedAt),
                ["achievement"] = TopicAchievement(c, i),
                ["result"] = new JsonArray(new JsonObject
                {
                    ["type"] = new JsonArray("Result"), ["status"] = "Completed",
                    ["value"] = c.ScorePercent.ToString("0.#", CultureInfo.InvariantCulture) + " % — " + GermanGrade.Text(c.ScorePercent),
                }),
            },
            ["evidence"] = evidence,
            ["credentialStatus"] = new JsonObject { ["id"] = BaseUrl + "/api/public/revocation-list", ["type"] = "1EdTechRevocationList" },
        };
    }

    /// <summary>
    /// CLR 2.0 (<c>ClrCredential</c>): <c>credentialSubject</c> ist ein <c>ClrSubject</c> mit den Achievements (Thema und Module), den eingebetteten signierten Credentials
    /// (Modulabschlüsse und Master-Badge) und Assoziationen (Modul <c>isChildOf</c> Thema). Es gibt keine Zirkelverweise: Das CLR umschließt, die Badges verweisen nicht auf das CLR.
    /// </summary>
    public JsonObject BuildClr(Certificate c, TopicCompletionInput i)
    {
        var masterSigned = SignedBadgeJson(c);   // Master-Badge samt Signatur (proof.jwt)
        var topicAch = TopicAchievement(c, i);
        var achievements = new JsonArray(topicAch.DeepClone());
        var credentials = new JsonArray(masterSigned);
        var associations = new JsonArray();
        foreach (var m in i.ModuleCertificates)
        {
            var signed = SignedBadgeJson(m);
            credentials.Add(signed);
            var ach = (JsonObject)signed["credentialSubject"]!["achievement"]!.DeepClone();
            achievements.Add(ach);
            associations.Add(new JsonObject { ["type"] = "Association", ["associationType"] = "isChildOf", ["sourceId"] = ach["id"]!.GetValue<string>(), ["targetId"] = topicAch["id"]!.GetValue<string>() });
        }
        return new JsonObject
        {
            ["@context"] = new JsonArray(Vc2Context, ClrContext, Ob3Context), ["id"] = $"{BaseUrl}/verify/{c.Id}/clr", ["type"] = new JsonArray("VerifiableCredential", "ClrCredential"),
            ["issuer"] = IssuerProfile(), ["validFrom"] = Iso(c.IssuedAt), ["name"] = $"{c.ModuleName} — Comprehensive Learner Record",
            ["credentialSubject"] = new JsonObject
            {
                ["type"] = new JsonArray("ClrSubject"), ["identifier"] = new JsonArray(Identity(c.LearnerName, true)),
                ["achievement"] = achievements, ["verifiableCredential"] = credentials, ["association"] = associations,
            },
            ["credentialStatus"] = new JsonObject { ["id"] = BaseUrl + "/api/public/revocation-list", ["type"] = "1EdTechRevocationList" },
        };
    }

    /// <summary>Signiertes CLR als JSON-LD: ClrCredential plus eingebettete Signatur (<c>proof.jwt</c>, VC-JWT) — unverändert aus der gespeicherten Signatur.</summary>
    public JsonObject? SignedClrJson(Certificate c)
    {
        if (string.IsNullOrEmpty(c.ClrJws)) return null;
        var credential = (JsonObject)JsonNode.Parse(BadgeSigner.PayloadText(c.ClrJws))!;
        credential["proof"] = new JsonObject { ["type"] = "JwtProof2020", ["jwt"] = c.ClrJws };
        return credential;
    }
}
