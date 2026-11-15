using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;
using LearnCards.Web.Services.AvatarGallery;
using LearnCards.Web.Services.ImageStorage;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Persönliche Avatar-Galerie, Austauschformat, AES-ZIP und Importsicherheit (ADR 0057). Läuft gegen die per TEST_DB_PROVIDER gewählte Datenbank (SQLite oder PostgreSQL).</summary>
public static class AvatarGalleryUnitTests
{
    private static byte[] Webp(int size, byte fill)
    {
        var b = new byte[size]; Array.Fill(b, fill);
        Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0); BitConverter.GetBytes((uint)(size - 8)).CopyTo(b, 4); Encoding.ASCII.GetBytes("WEBP").CopyTo(b, 8);
        for (var i = 12; i < size; i++) b[i] = (byte)(fill + i * 7);   // nicht trivial komprimierbar
        return b;
    }
    /// <summary>Stub des Bildanbieters für Stimmungsbilder: liefert je Stimmung/Aufruf ein anderes WebP; Fehler pro Stimmung einstellbar.</summary>
    private sealed class MoodStub : HttpMessageHandler
    {
        public int Calls; public readonly List<string> Moods = new(); public readonly Dictionary<string, int> Fail = new();   // Stimmung → HTTP-Status
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var body = request.Content is null ? "" : Encoding.Latin1.GetString(await request.Content.ReadAsByteArrayAsync(ct));
            var m = System.Text.RegularExpressions.Regex.Match(body, @"Expression key: (\w+)"); var mood = m.Success ? m.Groups[1].Value : "base";
            lock (Moods) Moods.Add(mood);
            if (Fail.TryGetValue(mood, out var status)) return new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent("{\"error\":{\"code\":\"x\"}}", Encoding.UTF8, "application/json") };
            var idx = AvatarMoods.All.ToList().FindIndex(x => x.Id == mood);
            var img = Webp(3000 + Math.Max(idx, 0) * 70, (byte)(100 + Math.Max(idx, 0) * 20 + Calls));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(img) } } }), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class MoodFactory : IHttpClientFactory { private readonly HttpMessageHandler _h; public MoodFactory(HttpMessageHandler h) { _h = h; } public HttpClient CreateClient(string name) => new(_h, false); }
    private static AvatarImageService MakeImages(MoodStub stub, int dailyLimit = 50)
    {
        var c = new AppConfig { OpenAiApiKey = "test-key", OpenAiBaseUrl = "http://stub.invalid/v1" }; c.AvatarImages.DailyLimitPerUser = dailyLimit;
        return new AvatarImageService(c, new MoodFactory(stub), NullLogger<AvatarImageService>.Instance);
    }

    private static async Task<string?> Fails(Func<Task> act) { try { await act(); return null; } catch (Exception ex) when (ex is ArchiveException or ArgumentException or KeyNotFoundException or InvalidOperationException or AvatarImageException) { return ex.Message; } }
    private static string? FailsSync(Action act) { try { act(); return null; } catch (ArchiveException ex) { return ex.Message; } }
    private static byte[] Zip(IEnumerable<(string Name, byte[] Data)> files, Action<ZipArchive>? tweak = null)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) { foreach (var (n, d) in files) { using var s = z.CreateEntry(n, CompressionLevel.Optimal).Open(); s.Write(d); } tweak?.Invoke(z); }
        return ms.ToArray();
    }
    private static byte[] ManifestZip(object manifest, params (string Name, byte[] Data)[] extra)
    {
        var all = new List<ZipFileData> { new(GalleryManifest.FileName, JsonSerializer.SerializeToUtf8Bytes(manifest)) };
        all.AddRange(extra.Select(e => new ZipFileData(e.Name, e.Data)));
        return SecureZip.Build(all, null);
    }
    private static int FindCentral(byte[] zip, int nth = 0) { var c = 0; for (var i = 0; i < zip.Length - 4; i++) if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) == 0x02014b50 && c++ == nth) return i; return -1; }

    /// <param name="external">Optional bereits initialisierte Datenbank (z. B. PostgreSQL aus PostgresReadinessTests); dann entfallen ZIP-/7-Zip-Prüfungen (sie sind datenbankunabhängig).</param>
    public static async Task RunAsync(Action<bool, string> check, string tmp, IDatabase? external = null)
    {
        var files = new List<ZipFileData>();
        var pw = "Korrekt-Pferd-Batterie-9";
        var img1 = Webp(4000, 1);
        string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        object Man(int version = 1, string format = "learny-avatar-gallery", object[]? avatars = null) => new { format, version, exported_at = DateTime.UtcNow.ToString("o"), unknown_optional = "egal", groups = new[] { new { id = "g1", name = "G" } },
            avatars = avatars ?? new object[] { new { id = "a1", group = "g1", type = "image", name = "Eins", file = "avatars/avatar-001.webp", mime = "image/webp", sha256 = Sha(img1), bytes = img1.Length, future_field = 7 } } };
        if (external is null) { RunContainerTests(check, out files); }
        // ─── Dienst gegen die Testdatenbank ────────────────────────────
        Directory.CreateDirectory(tmp);
        var cfg = new AppConfig();
        IDatabase db = external ?? new SqliteDatabase(Path.Combine(tmp, "gallery-" + Guid.NewGuid().ToString("N")[..6] + ".db"));
        await using var _owned = external is null ? db : null;
        await db.InitAsync();
        var repo = new CardRepository(db, new CardImageService(db, new ImageStorageRegistry(cfg, db), new NullImageCache(), cfg, NullLogger<CardImageService>.Instance), new QuizPoolService(db));
        var moodStub = new MoodStub(); var images = MakeImages(moodStub);
        var gal = new AvatarGalleryService(db, repo, images);
        var alice = "alice-" + Guid.NewGuid().ToString("N")[..6]; var bob = "bob-" + Guid.NewGuid().ToString("N")[..6];

        var batch = Enumerable.Range(1, 4).Select(i => new NewGalleryEntry("image", "Variante " + i, "", i % 2 == 0 ? "classic" : "realistic", Webp(3000 + i * 100, (byte)i), null, GroupKey: "s1")).ToList();
        var added = await gal.AddAsync(alice, batch);
        var luna = await gal.AddAsync(alice, new[] { new NewGalleryEntry("svg", "Luna", "", "svg", null, "luna") });
        var custom = Avatars.Custom("#F5D0B5", "#9CA3AF", "bald", "#7C3AED", "glasses", "square", "full")!;
        await gal.AddAsync(alice, new[] { new NewGalleryEntry("svg", "Opa", "freundlich", "svg", null, custom.Id, true) });
        var list = await gal.ListAsync(alice);
        check(list.Count == 6 && added.Select(a => a.GroupId).Distinct().Count() == 1 && list.Count(e => e.GroupId == added[0].GroupId) == 4, "Galerie: mehrere Varianten werden dauerhaft gespeichert und als eine Gruppe geführt");
        check(list.Select(e => e.Id).Distinct().Count() == 6 && list.All(e => AvatarGalleryService.IsValidId(e.Id)), "Galerie: interne Kennungen sind eindeutig und wohlgeformt");
        // Neustart-Simulation: neuer Dienst auf derselben Datenbank
        var gal2 = new AvatarGalleryService(db, repo, images);
        check((await gal2.ListAsync(alice)).Count == 6 && (await gal2.GetImageAsync(alice, added[2].Id))!.Value.Bytes.SequenceEqual(batch[2].Bytes!), "Galerie: Einträge und Bilddaten bleiben nach Neustart erhalten (nicht nur im Speicher)");
        // Isolation
        check((await gal.ListAsync(bob)).Count == 0 && await gal.GetImageAsync(bob, added[0].Id) is null && await gal.GetAsync(bob, added[0].Id) is null, "Isolation: Benutzer B sieht weder Liste noch Bild von A (auch nicht über erratene Kennung)");
        check(await Fails(() => gal.ExportAsync(bob, new[] { added[0].Id }, null)) is not null && await Fails(() => gal.RenameAsync(bob, added[0].Id, "x")) is not null && await Fails(() => gal.DeleteAsync(bob, new[] { added[0].Id })) is not null && await Fails(() => gal.ActivateAsync(bob, added[0].Id)) is not null, "Isolation: Export, Umbenennen, Löschen und Aktivieren fremder Einträge schlagen fehl");
        check((await gal.GetImageAsync(alice, "../../etc/passwd")) is null && !AvatarGalleryService.IsValidId("0123456789abcdef0123456789abcde'") , "Isolation: ungültige Kennungen werden nie in Abfragen verwendet");
        // Bearbeiten, Aktivieren, Löschen
        await gal.RenameAsync(alice, added[0].Id, "  Umbenannt \n ");
        await gal.SetFavoriteAsync(alice, added[1].Id, true);
        var l2 = await gal.ListAsync(alice);
        check(l2.First(e => e.Id == added[0].Id).Name == "Umbenannt" && l2.First(e => e.Id == added[1].Id).Favorite, "Galerie: Umbenennen (bereinigt) und Favorit");
        var presetBefore = (await repo.GetAvatarAsync(alice)).Preset;
        var act = await gal.ActivateAsync(alice, added[2].Id);
        var activeNow = await gal.ActivePresetAsync(alice);
        var lAct = await gal.ListAsync(alice);
        check(presetBefore == "" && act == Avatars.GalleryId(added[2].Id) && activeNow == act && lAct.Count(e => AvatarGalleryService.IsActive(e, activeNow)) == 1 && AvatarGalleryService.IsActive(lAct.First(e => e.Id == added[2].Id), activeNow)
              && (await gal.GetActiveImageAsync(alice))!.Value.Bytes.SequenceEqual(batch[2].Bytes!) && await gal.GetActiveImageAsync(bob) is null, "Galerie: Aktivieren setzt genau einen Profilavatar; das aktive Bild ist nur für den Besitzer lesbar");
        check((await gal.ListAsync(alice)).Count == 6, "Galerie: Aktivieren überschreibt oder löscht keinen anderen Eintrag");
        check((await Fails(() => gal.DeleteAsync(alice, new[] { added[2].Id })) ?? "").Contains("aktive"), "Lebenszyklus: der aktive Avatar wird nicht gelöscht");
        check((await Fails(() => gal.DeleteGroupAsync(alice, added[0].GroupId)) ?? "").Contains("aktive") && (await gal.ListAsync(alice)).Count == 6, "Lebenszyklus: Gruppenlöschung mit aktivem Mitglied schlägt vollständig fehl (alles oder nichts)");
        await gal.ActivateAsync(alice, luna[0].Id);
        check((await repo.GetAvatarAsync(alice)).Preset == "luna" && AvatarGalleryService.IsActive((await gal.ListAsync(alice)).First(e => e.Id == luna[0].Id), "luna"), "Galerie: Baukasten-Avatar (SVG-Kennung) aktivieren, alte Kennungen bleiben gültig");
        check(await gal.DeleteAsync(alice, new[] { added[3].Id }) == 1 && (await gal.ListAsync(alice)).Count == 5, "Lebenszyklus: einzelnen Eintrag löschen");
        check(await gal.DeleteGroupAsync(alice, added[0].GroupId) == 3 && (await gal.ListAsync(alice)).Count == 2, "Lebenszyklus: ganze Gruppe löschen");

        // Export/Import-Roundtrip aller Varianten
        var batch2 = Enumerable.Range(1, 3).Select(i => new NewGalleryEntry("image", "R" + i, "Beschr " + i, "cartoon3d", Webp(3500 + i, (byte)(40 + i)), null, i == 2, GroupKey: "r1")).ToList();
        await gal.AddAsync(alice, batch2);
        var allBefore = await gal.ListAsync(alice);
        var zipAll = await gal.ExportAsync(alice, null, null);
        var parsedAll = gal.PreviewImport(zipAll, null);
        check(parsedAll.Avatars.Count == allBefore.Count && parsedAll.Avatars.Count(a => a.Kind == "image") == allBefore.Count(e => e.IsImage) && parsedAll.Avatars.Count(a => a.Kind == "svg") == allBefore.Count(e => !e.IsImage), "Export: die gesamte Galerie (alle Varianten, Bild und SVG) ist im Archiv");
        var mjson = Encoding.UTF8.GetString(SecureZip.Read(zipAll, null).First(f => f.Name == "manifest.json").Data);
        check(mjson.Contains("\"format\": \"learny-avatar-gallery\"") && mjson.Contains("\"version\": 1") && !mjson.Contains(alice) && !mjson.Contains("data_b64") && !mjson.ToLowerInvariant().Contains("password") && !mjson.Contains("user_sub"), "Export: Manifest versioniert, ohne Benutzerkennung, Passwort oder interne Felder");
        check(SecureZip.Read(zipAll, null).Where(f => f.Name.StartsWith("avatars/")).All(f => System.Text.RegularExpressions.Regex.IsMatch(f.Name, @"^avatars/avatar-\d{3}(-[a-z]+)?\.(webp|png|jpg)$")), "Export: Dateinamen entstehen nur aus einer Zählung (keine Benutzereingaben im Pfad)");
        var sel = allBefore.Where(e => e.IsImage).Take(2).Select(e => e.Id).ToList();
        check(gal.PreviewImport(await gal.ExportAsync(alice, sel, null), null).Avatars.Count == 2, "Export: Auswahl exportiert nur die gewählten Einträge");
        check(await Fails(() => gal.ExportAsync(alice, new[] { "0123456789abcdef0123456789abcdef" }, null)) is not null, "Export: unbekannte Kennung → Fehler");
        // Reimport in anderes Konto: alles wiederhergestellt, neue interne IDs, Profilavatar unberührt
        var carol = "carol-" + Guid.NewGuid().ToString("N")[..6];
        var res = await gal.CommitImportAsync(carol, parsedAll, parsedAll.Avatars.Select(a => a.ArchiveId).ToList(), ImportDuplicates.Skip);
        var carolList = await gal.ListAsync(carol);
        check(res.Imported == allBefore.Count && carolList.Count == allBefore.Count && carolList.Select(e => e.Id).Intersect(allBefore.Select(e => e.Id)).Count() == 0, "Import: alle Einträge wiederhergestellt, mit neuen internen Kennungen (fremde Archiv-IDs werden nie übernommen)");
        check(carolList.Where(e => e.IsImage).Select(e => e.Sha256).OrderBy(x => x).SequenceEqual(allBefore.Where(e => e.IsImage).Select(e => e.Sha256).OrderBy(x => x)) && carolList.Where(e => !e.IsImage).Select(e => e.SvgId).OrderBy(x => x).SequenceEqual(allBefore.Where(e => !e.IsImage).Select(e => e.SvgId).OrderBy(x => x)), "Import: Bilder (Prüfsummen) und SVG-Parameter sind identisch");
        check(carolList.Select(e => e.GroupId).Distinct().Count() == allBefore.Select(e => e.GroupId).Distinct().Count() && carolList.Count(e => e.Favorite) == allBefore.Count(e => e.Favorite) && carolList.Any(e => e.Description == "Beschr 1"), "Import: Gruppen, Favoriten und Beschreibungen bleiben erhalten");
        check((await repo.GetAvatarAsync(carol)).Preset == "", "Import: ändert den Profilavatar nicht");
        // Duplikate
        var dup = await gal.CommitImportAsync(carol, parsedAll, parsedAll.Avatars.Select(a => a.ArchiveId).ToList(), ImportDuplicates.Skip);
        check(dup.Imported == 0 && dup.SkippedDuplicates == allBefore.Count && (await gal.ListAsync(carol)).Count == allBefore.Count, "Import: Duplikate werden überspringbar (nichts doppelt, nichts überschrieben)");
        var dup2 = await gal.CommitImportAsync(carol, parsedAll, new[] { parsedAll.Avatars.First(a => a.Kind == "image").ArchiveId }, ImportDuplicates.ImportAsNew);
        check(dup2.Imported == 1 && (await gal.ListAsync(carol)).Count == allBefore.Count + 1, "Import: Duplikate lassen sich als neue Einträge importieren");
        check(await Fails(() => gal.CommitImportAsync(carol, parsedAll, Array.Empty<string>(), ImportDuplicates.Skip)) is not null, "Import: ohne Auswahl wird nichts importiert");

        // Grenzen
        var many = Enumerable.Range(0, GalleryLimits.MaxEntriesPerUser).Select(i => new NewGalleryEntry("svg", "S" + i, "", "svg", null, Avatars.Custom("#F5D0B5", "#" + (0x100000 + i).ToString("X6"), "short", "#7C3AED", "none")!.Id)).ToList();
        var dave = "dave-" + Guid.NewGuid().ToString("N")[..6];
        await gal.AddAsync(dave, many.Take(GalleryLimits.MaxEntriesPerUser - 1).ToList());
        check(await Fails(() => gal.AddAsync(dave, many.Skip(GalleryLimits.MaxEntriesPerUser - 2).Take(2).ToList())) is not null && (await gal.ListAsync(dave)).Count == GalleryLimits.MaxEntriesPerUser - 1, "Grenzen: Überschreiten der Eintragszahl wird abgelehnt, nichts wird teilweise gespeichert");
        check(await Fails(() => gal.AddAsync(dave, new[] { new NewGalleryEntry("image", "x", "", "", Encoding.UTF8.GetBytes("<svg onload=x>" + new string(' ', 400)), null) })) is not null && await Fails(() => gal.AddAsync(dave, new[] { new NewGalleryEntry("svg", "x", "", "", null, "custom:red|x") })) is not null && await Fails(() => gal.AddAsync(dave, new[] { new NewGalleryEntry("svg", "x", "", "", null, "gal:" + new string('a', 32)) })) is not null, "Grenzen: ungültige Bilder/SVG-Kennungen werden nie gespeichert (auch keine Galerie-Kennung als SVG)");

        // Passwortschutz: Export und Reimport
        var zipEnc = await gal.ExportAsync(alice, null, pw);
        check(await Fails(() => gal.ExportAsync(alice, null, "kurz")) is not null && AvatarGalleryService.PasswordHint("abc123") is not null && AvatarGalleryService.PasswordHint(pw) is null, "Passwortschutz: zu kurze Passwörter werden abgelehnt, schwache mit Hinweis versehen");
        check(gal.PreviewImport(zipEnc, pw).Avatars.Count == allBefore.Count, "Passwortschutz: verschlüsselter Export lässt sich mit dem Passwort wieder importieren");
        var eve = "eve-" + Guid.NewGuid().ToString("N")[..6];
        var wrongPw = await Fails(() => { gal.PreviewImport(zipEnc, "falsches-passwort-1"); return Task.CompletedTask; });
        check(wrongPw is not null && (await gal.ListAsync(eve)).Count == 0, "Passwortschutz: falsches Passwort → Fehler, keine Daten importiert");
        var cut = zipEnc[..(zipEnc.Length - 3000)];
        check(await Fails(() => { gal.PreviewImport(cut, pw); return Task.CompletedTask; }) is not null, "Passwortschutz: beschädigtes Archiv wird abgelehnt");
        check(!zipEnc.AsSpan().IndexOf(Encoding.UTF8.GetBytes("learny-avatar-gallery")).Equals(0) && zipEnc.AsSpan().IndexOf(Encoding.UTF8.GetBytes("learny-avatar-gallery")) < 0 && zipEnc.AsSpan().IndexOf(Encoding.UTF8.GetBytes(pw)) < 0, "Passwortschutz: weder Manifest noch Passwort stehen im Klartext im Archiv");

        // Migration des älteren Profilbilds
        var frank = "frank-" + Guid.NewGuid().ToString("N")[..6];
        var legacyBytes = Webp(6000, 77);
        var legacyId = await repo.SaveAvatarImageAsync(frank, legacyBytes, "gpt-image-2.5-flare", "classic", 1_500_000);
        var mig = await gal.ListAsync(frank); var mig2 = await gal.ListAsync(frank);
        check(mig.Count == 1 && mig2.Count == 1 && mig[0].LegacyVersion == Avatars.ImageVersionOf(legacyId) && mig[0].Style == "classic" && AvatarGalleryService.IsActive(mig[0], legacyId), "Migration: älteres Profilbild wird einmalig (idempotent) in die Galerie übernommen und als aktiv erkannt");
        check((await repo.GetAvatarImageAsync(frank))!.Value.Bytes.SequenceEqual(legacyBytes) && (await gal.GetActiveImageAsync(frank))!.Value.Bytes.SequenceEqual(legacyBytes) && (await repo.GetAvatarAsync(frank)).Preset == legacyId, "Migration: Altbestand (avatar_images, img:-Kennung) bleibt unverändert gültig und wird weiter angezeigt");
        check((await Fails(() => gal.DeleteAsync(frank, new[] { mig[0].Id })) ?? "").Contains("aktive"), "Migration: das aktive migrierte Bild wird nicht gelöscht");


        // ─── Stimmungsvarianten (ADR 0058) ─────────────────────────────
        var prompts = AvatarMoods.All.Where(m => m.Id != "neutral").Select(m => AvatarImageService.BuildMoodPrompt("realistic", m.Id)).ToList();
        check(prompts.Distinct().Count() == 4 && prompts.All(x => x.Contains("Keep exactly the same character") && x.Contains("ONLY the facial expression")) && AvatarImageService.BuildMoodPrompt("classic", "happy").Contains("comic-book"), "Stimmungen: Aufträge nennen Identität bewahren und nur Mimik ändern; je Stimmung ein eigener Auftrag");
        var greta = "greta-" + Guid.NewGuid().ToString("N")[..6]; var hans = "hans-" + Guid.NewGuid().ToString("N")[..6];
        var baseImg = Webp(4200, 55);
        var main = (await gal.AddAsync(greta, new[] { new NewGalleryEntry("image", "Greta", "", "realistic", baseImg, null) }))[0];
        check(main.FigureId.Length == 32 && main.Mood == "neutral" && (await gal.MissingMoodsAsync(greta, main.Id)).Count == 4, "Stimmungen: ein neuer KI-Avatar ist das neutrale Hauptbild einer Figur; vier Stimmungen fehlen (ohne KI-Aufruf)");
        var c0 = moodStub.Calls;
        var run = await gal.CreateMoodsAsync(greta, main.Id, new[] { "happy", "motivating", "success", "encouraging" });
        var fig = await gal.FigureEntriesAsync(greta, main.Id);
        check(run.Created == 4 && run.Failed == 0 && moodStub.Calls == c0 + 4 && fig.Count == 5 && fig.Select(f => f.EffectiveMood).SequenceEqual(AvatarMoods.All.Select(m => m.Id)), "Stimmungen: alle vier fehlenden Stimmungen werden erzeugt und der Figur in der richtigen Reihenfolge zugeordnet");
        check(fig.Select(f => f.Sha256).Distinct().Count() == 5 && fig.Select(f => f.FigureId).Distinct().Count() == 1 && fig.Select(f => f.GroupId).Distinct().Count() == 1 && fig.All(f => f.Style == "realistic"), "Stimmungen: fünf unterschiedliche Bilddaten, gleiche Figur, gleiche Gruppe, gleicher Stil");
        check(moodStub.Moods.Skip(moodStub.Moods.Count - 4).SequenceEqual(new[] { "happy", "motivating", "success", "encouraging" }), "Stimmungen: je Stimmung genau ein Bildauftrag mit dem passenden Ausdruck");
        check(fig.Where(f => f.EffectiveMood != "neutral").All(f => f.Name.StartsWith("Greta · ")) && (await gal.GetImageAsync(greta, fig[0].Id))!.Value.Bytes.SequenceEqual(baseImg), "Stimmungen: Benennung „Name · Stimmung“; das Hauptbild bleibt unverändert");
        // Neustart + Dashboard-Wechsel ohne KI-Aufruf
        await gal.ActivateAsync(greta, main.Id);
        var galR = new AvatarGalleryService(db, repo, images); var cBefore = moodStub.Calls;
        var moodsNow = await galR.ActiveMoodsAsync(greta);
        var shas = new List<string>();
        foreach (var m in AvatarMoods.All) shas.Add(Convert.ToHexString(SHA256.HashData((await galR.GetActiveImageAsync(greta, m.Id))!.Value.Bytes)).ToLowerInvariant());
        check(moodsNow.Count == 5 && shas.Distinct().Count() == 5 && shas.SequenceEqual(fig.Select(f => f.Sha256)) && moodStub.Calls == cBefore, "Dashboard: nach Neustart liefert jede Stimmung ihr eigenes gespeichertes Bild; der Wechsel löst keinen KI-Aufruf aus");
        check((await galR.GetActiveImageAsync(greta))!.Value.Bytes.SequenceEqual(baseImg) && await galR.GetActiveImageAsync(hans, "happy") is null, "Dashboard: ohne Stimmung die Hauptvariante; andere Benutzer erhalten kein Bild");
        // fehlende Variante: Ersatz = Hauptvariante, nie das Bild einer anderen Stimmung
        var ivo = "ivo-" + Guid.NewGuid().ToString("N")[..6];
        var ivoMain = (await gal.AddAsync(ivo, new[] { new NewGalleryEntry("image", "Ivo", "", "classic", Webp(4300, 9), null) }))[0];
        await gal.ActivateAsync(ivo, ivoMain.Id);
        var sub = (await gal.GetActiveImageAsync(ivo, "success"))!.Value.Bytes;
        var ivoBase = (await gal.GetImageAsync(ivo, ivoMain.Id))!.Value.Bytes; var ivoMoods = await gal.ActiveMoodsAsync(ivo);
        check(Convert.ToBase64String(sub) == Convert.ToBase64String(ivoBase) && ivoMoods.SetEquals(new[] { "neutral" }), "Dashboard: fehlt eine Stimmung, wird die Hauptvariante als (zu kennzeichnender) Ersatz geliefert; die verfügbaren Stimmungen sind abfragbar");
        // vorhandene nie überschreiben
        var c1 = moodStub.Calls; var again = await gal.CreateMoodsAsync(greta, main.Id, new[] { "happy", "success" });
        check(again.Items.Count == 0 && moodStub.Calls == c1, "Stimmungen: bereits vorhandene Varianten werden nicht überschrieben und kosten keinen Aufruf");
        // nachträglich für Altbestand (älteres Profilbild)
        var jana = "jana-" + Guid.NewGuid().ToString("N")[..6];
        var legacyId2 = await repo.SaveAvatarImageAsync(jana, Webp(5200, 33), "gpt-image-2.5-flare", "cartoon3d", 1_500_000);
        var legacyEntry = (await gal.ListAsync(jana))[0];
        check(legacyEntry.FigureId.Length == 0 && legacyEntry.EffectiveMood == "neutral" && (await gal.MissingMoodsAsync(jana, legacyEntry.Id)).Count == 4, "Altbestand: ein bisheriges Einzelbild gilt als Neutral-Variante, vier Stimmungen fehlen (kein automatischer KI-Aufruf)");
        var lr = await gal.CreateMoodsAsync(jana, legacyEntry.Id, new[] { "happy", "motivating", "success", "encouraging" });
        var legacyFig = await gal.FigureEntriesAsync(jana, legacyEntry.Id);
        check(lr.Created == 4 && legacyFig.Count == 5 && legacyFig.All(f => f.Style == "cartoon3d") && (await repo.GetAvatarAsync(jana)).Preset == legacyId2 && (await gal.ActiveMoodsAsync(jana)).Count == 5, "Altbestand: Stimmungen lassen sich nachträglich ergänzen; Profilkennung (img:) bleibt gültig und die Stimmungen sind abrufbar");
        var legacyOld = (await repo.GetAvatarImageAsync(jana))!.Value.Bytes; var legacyNeutral = (await gal.GetImageAsync(jana, legacyFig.First(f => f.EffectiveMood == "neutral").Id))!.Value.Bytes;
        check(Convert.ToBase64String(legacyOld) == Convert.ToBase64String(legacyNeutral), "Altbestand: das ursprüngliche Bild bleibt als Hauptvariante bytegleich erhalten");
        // Teilfehler und gezieltes Nachholen
        var kim = "kim-" + Guid.NewGuid().ToString("N")[..6];
        var kimMain = (await gal.AddAsync(kim, new[] { new NewGalleryEntry("image", "Kim", "", "realistic", Webp(4400, 21), null) }))[0];
        moodStub.Fail["success"] = 500;
        var part = await gal.CreateMoodsAsync(kim, kimMain.Id, new[] { "happy", "motivating", "success", "encouraging" });
        check(part.Created == 3 && part.Failed == 1 && part.Items.Single(i => !i.Ok).Mood == "success" && (await gal.FigureEntriesAsync(kim, kimMain.Id)).Count == 4, "Teilfehler: erfolgreiche Bilder bleiben gespeichert, die fehlgeschlagene Stimmung wird benannt");
        moodStub.Fail.Clear(); var cNach = moodStub.Calls;
        var nach = await gal.MissingMoodsAsync(kim, kimMain.Id);
        var retry = await gal.CreateMoodsAsync(kim, kimMain.Id, new[] { "happy", "motivating", "success", "encouraging" });
        check(nach.SequenceEqual(new[] { "success" }) && retry.Created == 1 && moodStub.Calls == cNach + 1 && (await gal.FigureEntriesAsync(kim, kimMain.Id)).Count == 5, "Teilfehler: nur die fehlende Stimmung wird erneut erzeugt (ein Aufruf)");
        // Serie stoppt bei Auslastung/Limit
        var lou = "lou-" + Guid.NewGuid().ToString("N")[..6];
        var louMain = (await gal.AddAsync(lou, new[] { new NewGalleryEntry("image", "Lou", "", "realistic", Webp(4500, 31), null) }))[0];
        moodStub.Fail["happy"] = 429; var cStop = moodStub.Calls;
        var stop = await gal.CreateMoodsAsync(lou, louMain.Id, new[] { "happy", "motivating", "success", "encouraging" });
        check(stop.Items.Count == 1 && !stop.Items[0].Ok && moodStub.Calls == cStop + 1, "Kosten: bei Auslastung/Kontingentfehler stoppt die Serie nach dem ersten Aufruf (keine weiteren kostenpflichtigen Versuche)");
        moodStub.Fail.Clear();
        var stub2 = new MoodStub(); var gal3 = new AvatarGalleryService(db, repo, MakeImages(stub2, dailyLimit: 2));
        var mia = "mia-" + Guid.NewGuid().ToString("N")[..6];
        var miaMain = (await gal3.AddAsync(mia, new[] { new NewGalleryEntry("image", "Mia", "", "realistic", Webp(4600, 41), null) }))[0];
        var lim = await gal3.CreateMoodsAsync(mia, miaMain.Id, new[] { "happy", "motivating", "success", "encouraging" });
        check(lim.Created == 2 && lim.Failed == 1 && lim.Items.Last().Error!.Contains("Tageslimit") && stub2.Calls == 2 && (await gal3.FigureEntriesAsync(mia, miaMain.Id)).Count == 3, "Kosten: das Tageslimit begrenzt die Serie; Teilgruppen bleiben gültig, der Rest bleibt fehlend");
        // Neu erstellen: Ersatz erst bei Erfolg
        var happyEntry = fig.First(f => f.EffectiveMood == "happy"); var oldBytes = (await gal.GetImageAsync(greta, happyEntry.Id))!.Value.Bytes;
        moodStub.Fail["happy"] = 500;
        var regFail = await Fails(() => gal.RegenerateMoodAsync(greta, happyEntry.Id));
        check(regFail is not null && (await gal.GetImageAsync(greta, happyEntry.Id))!.Value.Bytes.SequenceEqual(oldBytes), "Neu erstellen: bei Fehler bleibt das vorhandene Bild unverändert");
        moodStub.Fail.Clear();
        await gal.RegenerateMoodAsync(greta, happyEntry.Id);
        var newBytes = (await gal.GetImageAsync(greta, happyEntry.Id))!.Value.Bytes;
        check(!newBytes.SequenceEqual(oldBytes) && (await gal.FigureEntriesAsync(greta, main.Id)).Count == 5 && (await gal.GetAsync(greta, happyEntry.Id))!.Sha256 == Convert.ToHexString(SHA256.HashData(newBytes)).ToLowerInvariant(), "Neu erstellen: nach Erfolg ersetzt das neue Bild genau diese Stimmung (Prüfsumme aktualisiert), die anderen bleiben");
        check(await Fails(() => gal.RegenerateMoodAsync(greta, main.Id)) is not null, "Neu erstellen: das neutrale Hauptbild wird nicht über diesen Weg neu erzeugt");
        // Isolation und SVG
        check(await Fails(() => gal.CreateMoodsAsync(hans, main.Id, new[] { "happy" })) is not null && await Fails(() => gal.RegenerateMoodAsync(hans, happyEntry.Id)) is not null && await Fails(() => gal.FigureEntriesAsync(hans, main.Id)) is not null && await Fails(() => gal.MissingMoodsAsync(hans, main.Id)) is not null, "Isolation: fremde Figuren lassen sich weder ergänzen, neu erzeugen noch lesen");
        var svgE = (await gal.AddAsync(hans, new[] { new NewGalleryEntry("svg", "Luna", "", "svg", null, "luna") }))[0];
        await gal.ActivateAsync(hans, svgE.Id);
        check((await Fails(() => gal.CreateMoodsAsync(hans, svgE.Id, new[] { "happy" })) ?? "").Contains("KI-Bild") && (await gal.ActiveMoodsAsync(hans)).Count == 0 && await gal.GetActiveImageAsync(hans, "happy") is null && (await repo.GetAvatarAsync(hans)).Preset == "luna", "SVG-Avatare: keine Stimmungsbilder, bestehende Stimmungslogik (Darstellungsparameter) und Kennung unverändert");
        // Export der Figur mit allen Stimmungen, Reimport, AES
        var figIds = fig.Select(f => f.Id).ToList();
        var zipFig = await gal.ExportAsync(greta, figIds, null);
        var entries = SecureZip.Read(zipFig, null);
        var manifestFig = Encoding.UTF8.GetString(entries.First(e => e.Name == "manifest.json").Data);
        var avatarFiles = entries.Where(e => e.Name.StartsWith("avatars/")).Select(e => e.Name).OrderBy(x => x).ToList();
        check(avatarFiles.Count == 5 && new[] { "neutral", "freundlich", "motivierend", "erfolgreich", "ermutigend" }.All(sl => avatarFiles.Any(f => f.Contains("-" + sl + "."))), "Export: die Figur enthält fünf Bilddateien mit Stimmung im Dateinamen (freundlich, motivierend, erfolgreich, ermutigend, neutral)");
        check(manifestFig.Contains("\"figure\": \"f001\"") && new[] { "neutral", "happy", "motivating", "success", "encouraging" }.All(m => manifestFig.Contains("\"mood\": \"" + m + "\"")) && !manifestFig.Contains(greta), "Export: Manifest ordnet jedem Bild Figur und Stimmung zu (ohne Benutzerkennung)");
        var nora = "nora-" + Guid.NewGuid().ToString("N")[..6]; var cImp = moodStub.Calls;
        var parsedFig = gal.PreviewImport(zipFig, null);
        await gal.CommitImportAsync(nora, parsedFig, parsedFig.Avatars.Select(a => a.ArchiveId).ToList(), ImportDuplicates.Skip);
        var noraList = await gal.ListAsync(nora);
        check(noraList.Count == 5 && noraList.Select(e => e.FigureId).Distinct().Count() == 1 && noraList.Select(e => e.FigureId).First() != fig[0].FigureId && noraList.Select(e => e.EffectiveMood).OrderBy(x => x).SequenceEqual(AvatarMoods.All.Select(m => m.Id).OrderBy(x => x)), "Reimport: Figur und fünf Stimmungen sind wiederhergestellt (neue interne Kennungen)");
        var currentFig = await gal.FigureEntriesAsync(greta, main.Id);
        var nShas = noraList.OrderBy(e => AvatarMoods.All.ToList().FindIndex(m => m.Id == e.EffectiveMood)).Select(e => e.Sha256).ToList();
        check(nShas.SequenceEqual(currentFig.Select(f => f.Sha256)) && moodStub.Calls == cImp, "Reimport: Bilder (Prüfsummen) je Stimmung unverändert, keine KI-Aufrufe");
        var zipFigEnc = await gal.ExportAsync(greta, figIds, "Korrekt-Pferd-Batterie-9");
        var olga = "olga-" + Guid.NewGuid().ToString("N")[..6];
        var parsedEnc = gal.PreviewImport(zipFigEnc, "Korrekt-Pferd-Batterie-9");
        await gal.CommitImportAsync(olga, parsedEnc, parsedEnc.Avatars.Select(a => a.ArchiveId).ToList(), ImportDuplicates.Skip);
        var olgaList = await gal.ListAsync(olga);
        check(SecureZip.Read(zipFigEnc, "Korrekt-Pferd-Batterie-9").Count(e => e.Name.StartsWith("avatars/")) == 5 && olgaList.Count == 5 && olgaList.Select(e => e.FigureId).Distinct().Count() == 1 && olgaList.Select(e => e.EffectiveMood).Distinct().Count() == 5, "AES-256: der passwortgeschützte Export enthält alle fünf Stimmungen; Reimport stellt Figur und Stimmungen wieder her");
        // Gesamtexport ohne Auswahl enthält alle Figuren vollständig
        var gesamt = gal.PreviewImport(await gal.ExportAsync(greta, null, null), null);
        check(gesamt.Avatars.Count == 5 && gesamt.Avatars.Select(a => a.FigureKey).Distinct().Count() == 1 && gesamt.Avatars.All(a => a.Mood.Length > 0), "Export: die gesamte Galerie enthält die Figur vollständig mit allen Stimmungen");
        // Rückwärtskompatibilität: Archiv ohne figure/mood (Format 1 vor ADR 0058) und mit unbekannter Stimmung
        var oldImg1 = Webp(3100, 11); var oldImg2 = Webp(3200, 12);
        string Sh(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        var oldZip = ManifestZip(new { format = "learny-avatar-gallery", version = 1, exported_at = DateTime.UtcNow.ToString("o"), groups = new[] { new { id = "g1", name = "Alt" } },
            avatars = new object[] { new { id = "a1", group = "g1", type = "image", name = "Alt 1", file = "avatars/avatar-001.webp", mime = "image/webp", sha256 = Sh(oldImg1) }, new { id = "a2", group = "g1", type = "image", name = "Alt 2", mood = "grumpy", file = "avatars/avatar-002.webp", mime = "image/webp", sha256 = Sh(oldImg2) } } },
            ("avatars/avatar-001.webp", oldImg1), ("avatars/avatar-002.webp", oldImg2));
        var pOld = gal.PreviewImport(oldZip, null);
        var pia = "pia-" + Guid.NewGuid().ToString("N")[..6];
        await gal.CommitImportAsync(pia, pOld, pOld.Avatars.Select(a => a.ArchiveId).ToList(), ImportDuplicates.Skip);
        var piaList = await gal.ListAsync(pia);
        check(pOld.Avatars.Count == 2 && pOld.Avatars.All(a => a.Mood == "" && a.FigureKey is null) && piaList.Count == 2 && piaList.Select(e => e.FigureId).Distinct().Count() == 2 && piaList.All(e => e.EffectiveMood == "neutral"), "Rückwärtskompatibilität: ältere Archive ohne Figur/Stimmung (und unbekannte Stimmungen) bleiben importierbar; jedes Bild ist eine eigene Figur (Neutral)");
        // Gruppenlöschung/Aktivschutz gelten weiter für Figuren
        check((await Fails(() => gal.DeleteAsync(greta, new[] { main.Id })) ?? "").Contains("aktive"), "Lebenszyklus: das aktive Hauptbild einer Figur wird nicht gelöscht");

        // Kontolöschung (Dienstmethode für ein künftiges Löschkonzept): nur eigene Daten
        await gal.DeleteAllForUserAsync(alice);
        check((await gal.ListAsync(alice)).Count == 0 && (await gal.ListAsync(carol)).Count == allBefore.Count + 1 && (await gal.ListAsync(frank)).Count == 1, "Lebenszyklus: Löschen aller Galeriedaten eines Benutzers entfernt nur dessen Daten");
        check((await gal.ListAsync(frank)).Count == 1 && (await gal.GetActiveImageAsync(frank)) is not null, "Lebenszyklus: Daten anderer Benutzer bleiben unberührt");

        // ─── Interoperabilität mit unabhängigem ZIP-Programm (7-Zip), falls vorhanden ───
        if (external is not null) return;
        var sevenZ = new[] { @"C:\Program Files\7-Zip\7z.exe", "/usr/bin/7z", "/usr/bin/7zz", "/usr/local/bin/7z" }.FirstOrDefault(File.Exists);
        if (sevenZ is null) { Console.WriteLine("  HINWEIS 7-Zip nicht gefunden: Interoperabilitätsprüfung übersprungen (siehe docs/testing.md)"); return; }
        var work = Path.Combine(tmp, "interop-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(work);
        var exportDir = Path.Combine(work, "ex"); var plainPath = Path.Combine(work, "plain.zip"); var encPath = Path.Combine(work, "enc.zip");
        await File.WriteAllBytesAsync(plainPath, SecureZip.Build(files, null)); await File.WriteAllBytesAsync(encPath, SecureZip.Build(files, pw));
        (int Code, string Out) Run(params string[] args)
        {
            var psi = new ProcessStartInfo(sevenZ) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(60000); return (p.ExitCode, o);
        }
        check(Run("t", plainPath).Code == 0, "Interop 7-Zip: unverschlüsseltes Archiv besteht den Integritätstest");
        var enc7 = Run("t", encPath, "-p" + pw);
        var info7 = Run("l", "-slt", encPath, "-p" + pw);
        check(enc7.Code == 0 && info7.Out.Contains("AES-256") && info7.Out.Contains("Encrypted = +"), "Interop 7-Zip: 7-Zip erkennt WinZip-AES-256 (Encrypted, Methode AES-256) und der Integritätstest mit Passwort besteht");
        check(Run("t", encPath, "-pfalsch-falsch-1").Code != 0, "Interop 7-Zip: falsches Passwort wird von 7-Zip abgelehnt");
        var x = Run("x", encPath, "-o" + exportDir, "-p" + pw, "-y");
        check(x.Code == 0 && File.ReadAllBytes(Path.Combine(exportDir, "avatars", "avatar-001.webp")).SequenceEqual(files[1].Data) && File.ReadAllBytes(Path.Combine(exportDir, "manifest.json")).SequenceEqual(files[0].Data), "Interop 7-Zip: entschlüsselte Dateien sind bytegleich mit den Originalen");
        var realExport = Path.Combine(work, "real.zip"); await File.WriteAllBytesAsync(realExport, zipEnc);
        var realDir = Path.Combine(work, "real"); var rx = Run("x", realExport, "-o" + realDir, "-p" + pw, "-y");
        var firstImg = Directory.GetFiles(Path.Combine(realDir, "avatars")).First();
        check(rx.Code == 0 && File.Exists(Path.Combine(realDir, "manifest.json")) && AvatarPhoto.DetectMime(File.ReadAllBytes(firstImg)) == "image/webp", "Interop 7-Zip: ein echter Galerie-Export lässt sich entschlüsseln; die Bilddateien sind eigenständig als WebP lesbar");
        // Umgekehrt: von 7-Zip erzeugtes AES-256-Archiv in Learny lesen
        var srcDir = Path.Combine(work, "src"); Directory.CreateDirectory(Path.Combine(srcDir, "avatars"));
        var man7 = JsonSerializer.SerializeToUtf8Bytes(Man());
        await File.WriteAllBytesAsync(Path.Combine(srcDir, "manifest.json"), man7); await File.WriteAllBytesAsync(Path.Combine(srcDir, "avatars", "avatar-001.webp"), img1);
        var zip7 = Path.Combine(work, "by7z.zip");
        var mk = Run("a", "-tzip", "-mem=AES256", "-p" + pw, zip7, Path.Combine(srcDir, "manifest.json"), Path.Combine(srcDir, "avatars"));
        // 7-Zip legt Dateien relativ zum angegebenen Pfad ab; für den Test genügt ein Archiv mit manifest.json + avatars/ im Stammverzeichnis
        var mk2 = Run("a", "-tzip", "-mem=AES256", "-p" + pw, zip7, "manifest.json", "avatars");
        var psi2 = new ProcessStartInfo(sevenZ) { WorkingDirectory = srcDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        File.Delete(zip7); foreach (var a in new[] { "a", "-tzip", "-mem=AES256", "-p" + pw, zip7, "manifest.json", "avatars" }) psi2.ArgumentList.Add(a);
        using (var p2 = Process.Start(psi2)!) { p2.StandardOutput.ReadToEnd(); p2.StandardError.ReadToEnd(); p2.WaitForExit(60000); }
        var parsed7 = File.Exists(zip7) ? FailsSync(() => AvatarArchive.Parse(File.ReadAllBytes(zip7), pw)) : "kein Archiv";
        check(File.Exists(zip7) && parsed7 is null && AvatarArchive.Parse(File.ReadAllBytes(zip7), pw).Avatars.Count == 1, "Interop 7-Zip: ein von 7-Zip erzeugtes AES-256-Archiv wird von Learny gelesen und geprüft");
        try { Directory.Delete(work, true); } catch (IOException) { /* Aufräumen ist optional */ }
    }

    private static void RunContainerTests(Action<bool, string> check, out List<ZipFileData> files)
    {
        var img1 = Webp(4000, 1); string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        object Man(int version = 1, string format = "learny-avatar-gallery", object[]? avatars = null) => new { format, version, exported_at = DateTime.UtcNow.ToString("o"), unknown_optional = "egal", groups = new[] { new { id = "g1", name = "G" } },
            avatars = avatars ?? new object[] { new { id = "a1", group = "g1", type = "image", name = "Eins", file = "avatars/avatar-001.webp", mime = "image/webp", sha256 = Sha(img1), bytes = img1.Length, future_field = 7 } } };
        // ─── ZIP-Container ─────────────────────────────────────────────
        var big = Encoding.UTF8.GetBytes("{\"a\":\"" + new string('x', 3000) + "\"}");
        files = new List<ZipFileData> { new("manifest.json", big), new("avatars/avatar-001.webp", Webp(5000, 3)), new("avatars/avatar-002.webp", Webp(900, 9)) };
        var plain = SecureZip.Build(files, null);
        var back = SecureZip.Read(plain, null);
        check(back.Count == 3 && back.Zip(files).All(p => p.First.Name == p.Second.Name && p.First.Data.SequenceEqual(p.Second.Data)), "ZIP: eigenes Schreiben/Lesen ohne Passwort ist verlustfrei");
        using (var za = new ZipArchive(new MemoryStream(plain), ZipArchiveMode.Read))
        {
            var ok = za.Entries.Count == 3 && files.All(f => { using var s = za.GetEntry(f.Name)!.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray().SequenceEqual(f.Data); });
            check(ok, "ZIP: ein unabhängiger Leser (System.IO.Compression) öffnet das erzeugte ZIP und findet identische Inhalte");
        }
        var foreign = Zip(files.Select(f => (f.Name, f.Data)));
        check(SecureZip.Read(foreign, null).Count == 3, "ZIP: fremd erzeugte ZIPs (System.IO.Compression) werden gelesen");

        // AES
        var enc = SecureZip.Build(files, "Korrekt-Pferd-Batterie-9");
        check(!enc.AsSpan().IndexOf(files[1].Data.AsSpan(20, 64)).Equals(-1) == false, "AES: Bildbytes stehen nicht im Klartext im Archiv");
        check(SecureZip.Read(enc, "Korrekt-Pferd-Batterie-9").Zip(files).All(p => p.First.Data.SequenceEqual(p.Second.Data)), "AES: Entschlüsseln mit richtigem Passwort ergibt die Originaldaten");
        var wrong = FailsSync(() => SecureZip.Read(enc, "falsch-falsch-1"));
        var missing = FailsSync(() => SecureZip.Read(enc, null));
        check(wrong is not null && wrong.Contains("Passwort") && wrong.Contains("nichts importiert") && missing is not null && missing.Contains("passwortgeschützt"), "AES: falsches/fehlendes Passwort → verständliche Meldung, keine Daten");
        var tampered = (byte[])enc.Clone(); tampered[200] ^= 0x01;
        var tam = FailsSync(() => SecureZip.Read(tampered, "Korrekt-Pferd-Batterie-9"));
        check(tam is not null, "AES: verändertes Archiv wird abgelehnt (HMAC), keine Teildaten");
        var enc2 = SecureZip.Build(files, "Korrekt-Pferd-Batterie-9");
        check(!enc.SequenceEqual(enc2), "AES: jeder Export nutzt ein neues Salz (zwei Exporte unterscheiden sich)");
        // Header: Methode 99, Flag verschlüsselt, AE-2, AES-256
        var c0 = FindCentral(enc);
        check(BinaryPrimitives.ReadUInt16LittleEndian(enc.AsSpan(c0 + 10)) == 99 && (BinaryPrimitives.ReadUInt16LittleEndian(enc.AsSpan(c0 + 8)) & 1) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(enc.AsSpan(c0 + 16)) == 0, "AES: WinZip-AE-2-Kennzeichnung (Methode 99, verschlüsselt, keine CRC)");
        // Klassische ZipCrypto abgelehnt
        var legacy = SecureZip.Build(files, null); var lc = FindCentral(legacy);
        BinaryPrimitives.WriteUInt16LittleEndian(legacy.AsSpan(lc + 8), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(legacy.AsSpan(lc + 8)) | 1));
        check((FailsSync(() => SecureZip.Read(legacy, "x")) ?? "").Contains("unsicher"), "ZIP: klassische ZIP-Verschlüsselung (ZipCrypto) wird nicht unterstützt");

        // Pfadmanipulation / Bomben / Beschädigung
        foreach (var bad in new[] { "../evil.webp", "/abs.webp", "a\\b.webp", "C:/x.webp", "a/../../b", "a//b", "./x" })
            check(FailsSync(() => SecureZip.Read(Zip(new[] { (bad, new byte[300]) }), null)) is not null, "ZIP: unzulässiger Pfad abgelehnt (" + bad + ")");
        check(FailsSync(() => SecureZip.Read(Zip(new[] { ("a.bin", new byte[300]), ("A.BIN", new byte[300]) }), null)) is not null, "ZIP: doppelte Namen (auch Groß-/Kleinschreibung) abgelehnt");
        var sym = SecureZip.Build(new[] { new ZipFileData("link", new byte[300]) }, null); var sc = FindCentral(sym);
        BinaryPrimitives.WriteUInt16LittleEndian(sym.AsSpan(sc + 4), (ushort)((3 << 8) | 63)); BinaryPrimitives.WriteUInt32LittleEndian(sym.AsSpan(sc + 38), 0xA1FFu << 16);
        check((FailsSync(() => SecureZip.Read(sym, null)) ?? "").Contains("Verknüpfungen"), "ZIP: symbolische Verknüpfungen werden abgelehnt");
        var bomb = Zip(new[] { ("manifest.json", new byte[1_400_000]) });
        check((FailsSync(() => SecureZip.Read(bomb, null)) ?? "").Contains("komprimiert"), "ZIP-Bombe: extrem hohes Kompressionsverhältnis wird abgelehnt");
        var lie = SecureZip.Build(new[] { new ZipFileData("manifest.json", big) }, null); var lcn = FindCentral(lie);
        BinaryPrimitives.WriteUInt32LittleEndian(lie.AsSpan(lcn + 24), 100);
        check(FailsSync(() => SecureZip.Read(lie, null)) is not null, "ZIP-Bombe: falsche (zu kleine) Größenangabe wird abgelehnt, nie mehr als angegeben entpackt");
        check(FailsSync(() => SecureZip.Read(Zip(Enumerable.Range(0, 130).Select(i => ($"f{i}.bin", new byte[300]))), null)) is not null, "ZIP: zu viele Dateien werden abgelehnt");
        check(FailsSync(() => SecureZip.Read(new byte[GalleryLimits.MaxArchiveBytes + 1], null)) is not null && FailsSync(() => SecureZip.Read(new byte[100], null)) is not null && FailsSync(() => SecureZip.Read(Encoding.UTF8.GetBytes("kein zip kein zip kein zip kein zip"), null)) is not null, "ZIP: zu große, zu kleine und keine ZIP-Dateien werden abgelehnt");
        check(FailsSync(() => SecureZip.Read(plain[..(plain.Length / 2)], null)) is not null && FailsSync(() => SecureZip.Read(plain[..(plain.Length - 5)], null)) is not null, "ZIP: abgeschnittene Archive werden abgelehnt");
        var flip = (byte[])plain.Clone(); flip[100] ^= 0xFF;
        check(FailsSync(() => SecureZip.Read(flip, null)) is not null, "ZIP: veränderte Inhalte werden über die Prüfsumme erkannt");

        // ─── Manifest ──────────────────────────────────────────────────
        var img2 = Webp(5000, 2);
        var okZip = ManifestZip(Man(), ("avatars/avatar-001.webp", img1));
        var parsedOk = AvatarArchive.Parse(okZip, null);
        check(parsedOk.Avatars.Count == 1 && parsedOk.Avatars[0].Sha256 == Sha(img1), "Manifest: gültiges Archiv wird gelesen, unbekannte optionale Felder werden toleriert");
        var v2 = FailsSync(() => AvatarArchive.Parse(ManifestZip(Man(version: 2), ("avatars/avatar-001.webp", img1)), null));
        check(v2 is not null && v2.Contains("Formatversion 2"), "Manifest: unbekannte Pflichtversion wird verständlich abgelehnt");
        check(FailsSync(() => AvatarArchive.Parse(ManifestZip(Man(format: "anderes"), ("avatars/avatar-001.webp", img1)), null)) is not null, "Manifest: falsches Format wird abgelehnt");
        check(FailsSync(() => AvatarArchive.Parse(Zip(new[] { ("x.txt", new byte[300]) }), null)) is not null, "Manifest: Archiv ohne manifest.json wird abgelehnt");
        check(FailsSync(() => AvatarArchive.Parse(Zip(new[] { ("manifest.json", Encoding.UTF8.GetBytes("{kein json" + new string(' ', 300))) }), null)) is not null, "Manifest: kaputtes JSON wird abgelehnt");
        object[] one(object a) => new[] { a };
        check((FailsSync(() => AvatarArchive.Parse(ManifestZip(Man(avatars: one(new { id = "a1", group = "g1", type = "image", name = "x", file = "avatars/avatar-001.webp", mime = "image/webp", sha256 = new string('0', 64) })), ("avatars/avatar-001.webp", img1)), null)) ?? "").Contains("Prüfsumme"), "Manifest: falsche Prüfsumme wird erkannt");
        check((FailsSync(() => AvatarArchive.Parse(ManifestZip(Man(), ("avatars/avatar-009.webp", img1)), null)) ?? "").Contains("fehlt"), "Manifest: fehlende Bilddatei wird erkannt");
        foreach (var f in new[] { "http://evil.example/x.png", "../x.webp", "/etc/passwd", "avatars/../../x", "file:///x" })
            check(FailsSync(() => AvatarArchive.Parse(ManifestZip(Man(avatars: one(new { id = "a1", group = "g1", type = "image", name = "x", file = f, mime = "image/webp", sha256 = Sha(img1) })), ("avatars/avatar-001.webp", img1)), null)) is not null, "Manifest: unzulässiger Dateiverweis abgelehnt (" + f + ")");
        check(FailsSync(() => AvatarArchive.Parse(ManifestZip(Man(avatars: one(new { id = "a1", group = "g1", type = "image", name = "x", file = "avatars/avatar-001.webp", mime = "image/png", sha256 = Sha(img1) })), ("avatars/avatar-001.webp", img1)), null)) is not null, "Manifest: Bildtyp ≠ Inhalt wird abgelehnt");
        var svgFile = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>" + new string(' ', 300));
        check(FailsSync(() => AvatarArchive.Parse(ManifestZip(Man(avatars: one(new { id = "a1", group = "g1", type = "image", name = "x", file = "avatars/avatar-001.webp", sha256 = Sha(svgFile) })), ("avatars/avatar-001.webp", svgFile)), null)) is not null, "Import: hochgeladenes SVG/HTML als „Bild“ wird abgelehnt (nur echte WebP/PNG/JPEG)");
        check(FailsSync(() => AvatarArchive.Parse(ManifestZip(Man(avatars: one(new { id = "a1", group = "g1", type = "svg", name = "x", svg = new { skin = "red", hair = "#000000", hair_style = "short", outfit = "#7C3AED", accessory = "none" } }))), null)) is not null, "Import: SVG-Parameter außerhalb des Baukastens werden abgelehnt (kein freies SVG)");
        check(FailsSync(() => AvatarArchive.Parse(ManifestZip(Man(avatars: one(new { id = "a1", group = "g1", type = "svg", name = "x", svg = new { skin = "#F5D0B5", hair = "#000000", hair_style = "short", outfit = "#7C3AED\" onload=\"x", accessory = "none" } }))), null)) is not null, "Import: Markup in SVG-Parametern wird abgelehnt");
        check(FailsSync(() => AvatarArchive.Parse(ManifestZip(Man(avatars: Enumerable.Range(0, 51).Select(i => (object)new { id = "a" + i, group = "g1", type = "svg", name = "x", svg = new { skin = "#F5D0B5", hair = "#000000", hair_style = "short", outfit = "#7C3AED", accessory = "none" } }).ToArray())), null)) is not null, "Import: zu viele Avatare werden abgelehnt");

    }

}
