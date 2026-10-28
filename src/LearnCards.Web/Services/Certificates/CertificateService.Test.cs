using System.Globalization;
using System.Text.Json.Nodes;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services.Certificates;

/// <summary>
/// Administrative Testausstellung (ADR 0037): Administratoren erzeugen Open Badges, Master-Badge und CLR zum Prüfen der Signaturkette (z. B. mit dem 1EdTech-Validator).
/// Es gibt <b>keinen</b> Umweg um die regulären Prüfungsbedingungen: Testnachweise entstehen nie aus Quizergebnissen, haben weder Ergebnis noch Kompetenz noch Prüfungssnapshot,
/// tragen eine eigene Nachweisidentität (<c>TEST-…</c>), stehen nicht in den Listen/Statistiken regulärer Abschlüsse und werden nicht verankert. Signiert wird mit denselben Diensten wie bei regulären Nachweisen.
/// </summary>
public sealed partial class CertificateService
{
    public const string TestMarker = "TEST";

    private static void MarkAsTest(JsonObject credential)
    {
        credential["name"] = $"{TestMarker} – " + (credential["name"]?.GetValue<string>() ?? "Nachweis");
        var subject = (JsonObject)credential["credentialSubject"]!;
        subject.Remove("result");   // kein Ergebnis, keine Note
        subject.Remove("activityEndDate");
        void Mark(JsonNode? a)
        {
            if (a is not JsonObject ach) return;
            if (ach["name"]?.GetValue<string>() is { } n && !n.StartsWith(TestMarker, StringComparison.Ordinal)) ach["name"] = $"{TestMarker} – {n}";
            ach["description"] = "TESTNACHWEIS: administrativ ausgestellt zur technischen Prüfung. Kein Lernmodul wurde abgeschlossen, keine Prüfung abgelegt.";
            ach["criteria"] = new JsonObject { ["id"] = (ach["id"]?.GetValue<string>() ?? "urn:learny:test") + "/criteria", ["narrative"] = "TESTNACHWEIS – keine Prüfung, kein Kompetenznachweis, keine Aussage über Lernleistungen." };
            ach.Remove("alignment"); ach.Remove("resultDescription");
        }
        if (subject["achievement"] is JsonArray list) foreach (var a in list) Mark(a); else Mark(subject["achievement"]);
        credential["evidence"] = new JsonArray(new JsonObject
        {
            ["type"] = new JsonArray("Evidence"), ["name"] = "TESTNACHWEIS", ["genre"] = "Test",
            ["narrative"] = "Dieser Nachweis wurde von einem Administrator zu Testzwecken ausgestellt. Er belegt keine Lernleistung.",
        });
    }

    private Certificate NewTestCertificate(AppUser actor, string kind, string name, int version, string? parentId) => new()
    {
        Kind = kind, Number = NewNumber("TEST"), UserId = actor.Id, ModuleId = Guid.NewGuid().ToString(), ModuleName = name, ModuleVersion = version, ModuleScope = kind == "topic" ? "topic" : "test",
        LearnerName = $"{TestMarker}-Subjekt", ScorePercent = 0, PassPercent = 0, IssuedAt = DateTime.UtcNow, IsTest = true, IssuedBy = actor.Id, IssuedByName = actor.DisplayName, IssueSource = "admin", ParentId = parentId,
    };

    private JsonObject BuildTestModuleBadge(Certificate c)
    {
        var achievementId = $"{BaseUrl}/badges/achievements/{c.ModuleId}/{c.ModuleVersion}";
        var credential = new JsonObject
        {
            ["@context"] = new JsonArray(Vc2Context, Ob3Context, RevocationListContext()), ["id"] = $"{BaseUrl}/verify/{c.Id}", ["type"] = new JsonArray("VerifiableCredential", "OpenBadgeCredential"),
            ["issuer"] = IssuerProfile(), ["validFrom"] = Iso(c.IssuedAt), ["name"] = c.ModuleName,
            ["credentialSubject"] = new JsonObject
            {
                ["id"] = SubjectId(c.UserId), ["type"] = new JsonArray("AchievementSubject"), ["identifier"] = new JsonArray(Identity(c.LearnerName, true)),
                ["achievement"] = new JsonObject { ["id"] = achievementId, ["type"] = new JsonArray("Achievement"), ["achievementType"] = "Course", ["name"] = c.ModuleName, ["version"] = c.ModuleVersion.ToString(CultureInfo.InvariantCulture) },
            },
            ["credentialStatus"] = new JsonObject { ["id"] = BaseUrl + "/api/public/revocation-list", ["type"] = "1EdTechRevocationList" },
        };
        MarkAsTest(credential);
        return credential;
    }

    private void SignInto(Certificate c, JsonObject master, JsonObject? clr)
    {
        c.BadgeJws = _signer.SignCredential(master, JwtKeyId);
        c.BadgeHash = BadgeSigner.HashHex(c.BadgeJws);
        c.LeafHash = MerkleTree.Hex(MerkleTree.Leaf(MerkleTree.FromHex(c.BadgeHash)));
        if (clr is not null) { c.ClrJws = _signer.SignCredential(clr, JwtKeyId); c.ClrHash = BadgeSigner.HashHex(c.ClrJws); }
    }

    /// <summary>
    /// Stellt einen Testnachweis aus (nur Administratoren, im Dienst gegen die Datenbankrolle geprüft). <paramref name="type"/>: <c>badge</c> (Open Badge 3.0 – Modul) oder <c>topic</c>
    /// (Test-Themenabschluss: Master-Badge + CLR mit zwei Test-Modul-Badges). Der Aussteller stammt ausschließlich aus dem authentifizierten Kontext.
    /// Gibt den Hauptnachweis zurück; bei einem Fehler werden die gerade angelegten Testzeilen wieder entfernt.
    /// </summary>
    public async Task<Certificate> IssueTestAsync(string actorId, string type, string correlationId)
    {
        var actor = await _users.RequireRoleAsync(actorId, Roles.Admin);
        if (type is not ("badge" or "topic")) throw new ArgumentException("Unbekannter Testtyp (badge oder topic).");
        Certificate primary;
        var created = new List<Certificate>();
        try
        {
            if (type == "badge")
            {
                primary = NewTestCertificate(actor, "module", "Beispielmodul (Testnachweis)", 1, null);
                SignInto(primary, BuildTestModuleBadge(primary), null);
                await InsertAsync(primary); created.Add(primary);
            }
            else
            {
                primary = NewTestCertificate(actor, "topic", "Beispielthema (Testnachweis)", 1, null);
                var modules = new List<Certificate>();
                foreach (var name in new[] { "Beispielmodul A (Testnachweis)", "Beispielmodul B (Testnachweis)" })
                {
                    var m = NewTestCertificate(actor, "module", name, 1, primary.Id);
                    SignInto(m, BuildTestModuleBadge(m), null);
                    modules.Add(m);
                }
                var inp = new TopicCompletionInput(actor.Id, primary.ModuleId, primary.ModuleName, 1, primary.IssuedAt, 0, 0, null, false, modules);
                var master = BuildMasterBadge(primary, inp, new ExamSnapshot { Hash = "TEST" });
                MarkAsTest(master);
                SignInto(primary, master, null);   // zuerst das Master-Badge: das CLR bettet die bereits signierten Credentials ein
                var clr = BuildClr(primary, inp);
                MarkAsTest(clr);
                primary.ClrJws = _signer.SignCredential(clr, JwtKeyId);
                primary.ClrHash = BadgeSigner.HashHex(primary.ClrJws);
                foreach (var m in modules) { await InsertAsync(m); created.Add(m); }
                await InsertAsync(primary); created.Add(primary);
            }
        }
        catch
        {
            foreach (var c in created) await _db.ExecuteAsync("DELETE FROM certificates WHERE id = @id AND is_test = 1", new[] { ("id", (object?)c.Id) });   // nur die eigenen, gerade angelegten Testzeilen
            await _users.WriteAuditAsync(actor.Id, "credential.test_issue", "", $"correlation={correlationId};type={type};result=failed");
            throw;
        }
        await _users.WriteAuditAsync(actor.Id, "credential.test_issue", primary.Id, $"correlation={correlationId};type={type};number={primary.Number};result=ok;children={created.Count - 1}");
        return primary;
    }
}
