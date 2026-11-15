using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Services;

/// <summary>
/// Modulbezogene Co-Author-Berechtigungen und Vererbung aus Themen (ADR 0053) sowie Regression des sofortigen Berechtigungsentzugs (ADR 0052/0053).
/// <see cref="CoreAsync"/> läuft identisch auf SQLite (im Prozess) und auf PostgreSQL (<c>PostgresReadinessTests</c>); <see cref="RunHttpAsync"/> prüft die REST-Schicht inkl. MCP-Token-Sperre.
/// </summary>
public static class ModuleCoAuthorTests
{
    private static async Task<bool> Denied(Func<Task> f)
    {
        try { await f(); return false; }
        catch (Exception e) when (e is KeyNotFoundException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or CardRepository.CardConflictException) { return true; }
    }

    /// <summary>true = es wurde nichts geschrieben (Ausnahme der Berechtigungsprüfung oder Ergebnis ohne Wirkung); ein erfolgreicher Schreibzugriff ergibt false.</summary>
    private static async Task<bool> NotWritten(Func<Task<bool>> tryWrite)
    {
        try { return !await tryWrite(); }
        catch (Exception e) when (e is KeyNotFoundException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or CardRepository.CardConflictException) { return true; }
    }

    private static CardJson Json(string module, string term, string definition = "D") => new() { Module = module, Category = "K", Term = term, Question = "Frage?", Definition = definition };

    public static async Task RunAsync(Action<bool, string> check, string tmp)
    {
        Console.WriteLine("\nModul-Co-Autoren und Rechtevererbung (ADR 0053), im Prozess auf SQLite");
        var e = await InvitationUnitTests.Env.CreateAsync(Path.Combine(tmp, "modcoauthor"));
        var comp = new CompetencyService(e.Db, e.Repo, e.Users);
        var ext = await e.AddWorkforceUserAsync("Extern Eva", "eva-m@example.org");
        await e.Raw.ExecuteAsync("UPDATE user_identities SET kind='external' WHERE user_id=@u", new (string, object?)[] { ("u", ext) });   // eingeschränktes Lernprofil (ADR 0049)
        var n = 0;
        await CoreAsync("SQLite", e.Db, e.Topics, e.Repo, comp, async name => await e.AddWorkforceUserAsync(name, $"{name.ToLowerInvariant().Replace(' ', '.')}-{++n}@example.org"), ext, check);
    }

    public static async Task CoreAsync(string db_, IDatabase db, TopicService topics, CardRepository repo, CompetencyService comp, Func<string, Task<string>> newUser, string? restricted, Action<bool, string> check)
    {
        var p = db_ + " ";
        var owner = await newUser("Mia Owner"); var co = await newUser("Carl Co"); var third = await newUser("Dora Dritte"); var outsider = await newUser("Otto Out");
        async Task<string> Mod(string name) => (await repo.CreateModuleAsync(new ModuleCreateRequest { Name = name }, owner)).Id;
        async Task<Card> NewCard(string module, string name, string term) => await repo.CreateCardAsync(Json(name, term), owner);
        // Module: M1 nur in T1, M2 in T1 und T2, M3 ohne Thema, M4 nur in T2 (nie freigegeben)
        var (n1, n2, n3, n4) = ("Mod-A-" + Guid.NewGuid().ToString("N")[..6], "Mod-B-" + Guid.NewGuid().ToString("N")[..6], "Mod-C-" + Guid.NewGuid().ToString("N")[..6], "Mod-D-" + Guid.NewGuid().ToString("N")[..6]);
        var m1 = await Mod(n1); var m2 = await Mod(n2); var m3 = await Mod(n3); var m4 = await Mod(n4);
        var c1 = await NewCard(m1, n1, "Karte 1"); var c2 = await NewCard(m2, n2, "Karte 2"); var c3 = await NewCard(m3, n3, "Karte 3"); var c3b = await NewCard(m3, n3, "Karte 3b"); await NewCard(m4, n4, "Karte 4");
        var t1 = await topics.CreateAsync(owner, "T1 " + n1, ""); var t2 = await topics.CreateAsync(owner, "T2 " + n1, "");
        await topics.AddModuleAsync(owner, t1.Id, m1); await topics.AddModuleAsync(owner, t1.Id, m2); await topics.AddModuleAsync(owner, t2.Id, m2); await topics.AddModuleAsync(owner, t2.Id, m4);
        async Task<ModuleAccessKind> Kind(string u, string m) => (await topics.GetModuleEditAccessAsync(u, m)).Kind;
        async Task<string> Term(string cardId) => (await repo.GetCardAsync(cardId, owner))?.Term ?? "";

        check(await Kind(co, m1) == ModuleAccessKind.None && await Kind(co, m3) == ModuleAccessKind.None && await Kind(owner, m1) == ModuleAccessKind.Owner, p + "M1 Vorher: ohne Berechtigung kein Zugriff, der Eigentümer behält Owner");

        // Themenfreigabe vererbt automatisch auf die Module des Entwurfs — aber nicht auf Module in fremden Themen oder die übrige Bibliothek
        await topics.AddCoAuthorAsync(owner, t1.Id, co);
        check(await Kind(co, m1) == ModuleAccessKind.ViaTopic && (await topics.GetModuleEditAccessAsync(co, m1)).ViaTopics.Single().TopicId == t1.Id, p + "M2 Themen-Co-Author: Modul des Entwurfs ist ohne weitere Freigabe bearbeitbar (abgeleitet)");
        check(await Kind(co, m2) == ModuleAccessKind.None, p + "M3 Mehrfachzuordnung: Modul liegt auch in Thema 2 ohne Recht → keine gemeinsame Bearbeitung");
        check(await Kind(co, m3) == ModuleAccessKind.None && await Kind(co, m4) == ModuleAccessKind.None, p + "M4 Keine Freigabe der übrigen Bibliothek (Modul ohne Thema, Modul eines fremden Themas)");
        check((await db.QueryAsync("SELECT COUNT(*) AS n FROM module_coauthors"))[0]["n"] == "0", p + "M5 Themenfreigabe erzeugt keine dauerhafte Modulfreigabe (nichts gespeichert)");
        check(await topics.ResolveModuleEditOwnerAsync(co, m1) == owner && await topics.ResolveModuleEditOwnerAsync(co, m2) == co, p + "M6 Bearbeitungskontext: Eigentümer nur bei gültigem Zugriff");

        // nachträglich hinzugefügtes Modul wird automatisch einbezogen
        var m5 = await Mod("Mod-E-" + Guid.NewGuid().ToString("N")[..6]);
        check(await Kind(co, m5) == ModuleAccessKind.None, p + "M7 Neues Modul ist vor der Zuordnung nicht zugänglich");
        await topics.AddModuleAsync(owner, t1.Id, m5);
        check(await Kind(co, m5) == ModuleAccessKind.ViaTopic, p + "M8 Nachträglich zum Thema hinzugefügtes Modul ist automatisch bearbeitbar");
        await topics.RemoveModuleAsync(owner, t1.Id, m5);
        check(await Kind(co, m5) == ModuleAccessKind.None, p + "M9 Modul aus dem Thema entfernt → abgeleitetes Recht entfällt sofort");

        // abgeleitetes Recht verwaltet keine direkten Co-Autoren
        check(await Denied(() => topics.AddModuleCoAuthorAsync(co, m1, third)) && await Denied(() => topics.SearchModuleCoAuthorCandidatesAsync(co, m1, "dora")) && await Denied(() => topics.LeaveModuleCoAuthorAsync(co, m1)), p + "M10 Abgeleitetes Recht darf keine direkten Modul-Co-Autoren verwalten oder verlassen");
        var view = await topics.GetModuleCollaborationAsync(co, m1);
        check(!view.CanManage && view.ViewerKind == ModuleAccessKind.ViaTopic && view.DirectCoAuthors.Count == 0 && view.DerivedAccess.Count == 0 && view.ViewerViaTopics.Count == 1, p + "M11 Abgeleiteter Zugriff ist in der Ansicht als solcher erkennbar und zeigt keine fremden Berechtigungen");
        var ownerView = await topics.GetModuleCollaborationAsync(owner, m1);
        check(ownerView.CanManage && ownerView.DerivedAccess.Any(d => d.UserId == co && d.TopicId == t1.Id) && ownerView.DirectCoAuthors.Count == 0, p + "M12 Eigentümer sieht abgeleitete Zugriffe getrennt von direkten Freigaben");

        // zweites berechtigtes Thema: Modul in zwei Themen, Co-Author an beiden → bearbeitbar; Entzug an einem lässt es wieder entfallen
        await topics.AddCoAuthorAsync(owner, t2.Id, co);
        check(await Kind(co, m2) == ModuleAccessKind.ViaTopic && (await topics.GetModuleEditAccessAsync(co, m2)).ViaTopics.Count == 2 && await Kind(co, m4) == ModuleAccessKind.ViaTopic, p + "M13 Co-Author an beiden Themen: Modul mit Mehrfachzuordnung ist bearbeitbar");
        await topics.RemoveCoAuthorAsync(owner, t2.Id, co);
        check(await Kind(co, m2) == ModuleAccessKind.None && await Kind(co, m4) == ModuleAccessKind.None && await Kind(co, m1) == ModuleAccessKind.ViaTopic, p + "M14 Entzug an Thema 2: Module von Thema 2 entfallen sofort, Thema 1 bleibt gültig");

        // Entzug des Themen-Co-Authors → alle abgeleiteten Rechte weg; Verlassen ebenso
        await topics.RemoveCoAuthorAsync(owner, t1.Id, co);
        check(await Kind(co, m1) == ModuleAccessKind.None, p + "M15 Themen-Co-Author entfernt → abgeleitetes Modulrecht entfällt sofort");
        await topics.AddCoAuthorAsync(owner, t1.Id, co); await topics.LeaveCoAuthorAsync(co, t1.Id);
        check(await Kind(co, m1) == ModuleAccessKind.None, p + "M16 Themen-Co-Author verlässt das Thema → Modulrecht entfällt");
        await topics.AddCoAuthorAsync(owner, t1.Id, co);
        await topics.SetArchivedAsync(owner, t1.Id, true);
        check(await Kind(co, m1) == ModuleAccessKind.None, p + "M17 Archiviertes Thema vererbt nichts (nur aktive Themenentwürfe)");
        await topics.SetArchivedAsync(owner, t1.Id, false);
        check(await Kind(co, m1) == ModuleAccessKind.ViaTopic, p + "M18 Nach Reaktivierung gilt das abgeleitete Recht wieder");

        // direkte Modulfreigabe: unabhängig von Themen
        check(await Denied(() => topics.AddModuleCoAuthorAsync(outsider, m3, third)) && await Denied(() => topics.GetModuleCollaborationAsync(outsider, m3)), p + "M19 Fremde ohne Recht: Modulzusammenarbeit „nicht gefunden“");
        var added = await topics.AddModuleCoAuthorAsync(owner, m3, co);
        await topics.AddModuleCoAuthorAsync(owner, m3, co);
        check(added.UserId == co && await Kind(co, m3) == ModuleAccessKind.Direct && (await db.QueryAsync("SELECT COUNT(*) AS n FROM module_coauthors WHERE module_id=@m", new (string, object?)[] { ("m", m3) }))[0]["n"] == "1", p + "M20 Direkte Modulfreigabe ohne Thema, idempotent (genau ein Eintrag je Modul und Benutzer)");
        check(await Denied(() => topics.AddModuleCoAuthorAsync(owner, m3, owner)) && await Denied(() => topics.AddModuleCoAuthorAsync(co, m3, co)) && await Denied(() => topics.RemoveModuleCoAuthorAsync(co, m3, owner)), p + "M21 Eigentümer weder hinzufüg- noch entfernbar, keine Selbsterteilung");
        // direkter Co-Author darf alle Schreibpfade nutzen (über den vom Dienst gelieferten Kontext) und weitere direkte Co-Autoren verwalten
        var ctx = await topics.ResolveModuleEditOwnerAsync(co, m3);
        var upd = await repo.UpdateCardAsync(c3.Id, Json(n3, "Karte 3 (Co)"), ctx, (await repo.GetCardAsync(c3.Id, owner))!.UpdatedAt);
        var dup = await repo.DuplicateCardAsync(c3.Id, ctx);
        var arch = await repo.SetArchivedAsync(c3b.Id, true, ctx);
        var created = await repo.CreateCardAsync(Json(n3, "Neue Co-Karte"), ctx);
        var model = new ModuleCompetencyModel { Objectives = { new LearningObjective { Id = "lz1", Text = "Ziel" } }, Competencies = { new Competency { Id = "k1", ObjectiveId = "lz1", Name = "Komp" } } };
        await comp.SaveModelAsync(ctx, m3, model);
        check(ctx == owner && upd is not null && dup is not null && arch is not null && created is not null && (await comp.GetModelRawAsync(m3)).Competencies.Count == 1, p + "M22 Direkter Co-Author: Karten ändern/anlegen/duplizieren/archivieren und Kompetenzen bearbeiten");
        check((await repo.GetModuleAsync(m3, owner))!.OwnerUserId == owner && (await repo.GetCardAsync(c3.Id, owner))!.Term == "Karte 3 (Co)", p + "M23 Der Moduleigentümer bleibt unverändert, die Änderung ist beim Eigentümer sichtbar");
        var d3 = await topics.AddModuleCoAuthorAsync(co, m3, third);
        check(d3.UserId == third && await Kind(third, m3) == ModuleAccessKind.Direct && await topics.RemoveModuleCoAuthorAsync(co, m3, third) && await Kind(third, m3) == ModuleAccessKind.None, p + "M24 Direkter Co-Author verwaltet weitere direkte Co-Autoren (hinzufügen/entfernen)");

        // keine Lernrechte, Veröffentlichung, Freigabe, Zuweisung
        check(await Denied(() => topics.AddModuleAsync(co, t1.Id, m3)) && await Denied(() => topics.PublishAsync(co, t1.Id)), p + "M25 Co-Autoren erhalten weder Modul-Hinzufügen aus der Bibliothek noch Veröffentlichung");
        check((await repo.ListSharedWithMeAsync(co)).All(x => x.Id != m3) && (await db.QueryAsync("SELECT COUNT(*) AS n FROM module_shares WHERE module_id=@m", new (string, object?)[] { ("m", m3) }))[0]["n"] == "0", p + "M26 Direkte Modulfreigabe ist keine Lernfreigabe (module_shares unberührt)");

        // direkte Freigabe bleibt nach Themenentzug bestehen; abgeleitete Quelle entfällt unabhängig
        await topics.AddModuleCoAuthorAsync(owner, m1, co);
        var both = await topics.GetModuleEditAccessAsync(co, m1);
        check(both.Kind == ModuleAccessKind.Direct && both.IsDirect && both.ViaTopics.Count == 1, p + "M27 Direkt und abgeleitet nebeneinander: beide Quellen erkennbar");
        await topics.RemoveCoAuthorAsync(owner, t1.Id, co);
        var left = await topics.GetModuleEditAccessAsync(co, m1);
        check(left.Kind == ModuleAccessKind.Direct && left.ViaTopics.Count == 0, p + "M28 Direkte Modulfreigabe bleibt nach Themenentzug bestehen");
        await topics.RemoveModuleCoAuthorAsync(owner, m1, co);
        check(await Kind(co, m1) == ModuleAccessKind.None, p + "M29 Direkter Entzug ohne Themenrecht → kein Zugriff");
        await topics.AddCoAuthorAsync(owner, t1.Id, co);   // für die Übersicht
        await topics.AddModuleCoAuthorAsync(owner, m2, co);   // direkte Freigabe gilt auch bei Mehrfachzuordnung (ausdrückliche Entscheidung des Eigentümers)
        check(await Kind(co, m2) == ModuleAccessKind.Direct, p + "M30 Direkt freigegebenes Modul bleibt trotz Mehrfachzuordnung bearbeitbar (kein abgeleitetes Recht nötig)");
        var overview = await topics.ListCoEditedModulesAsync(co);
        var byId = overview.ToDictionary(x => x.Module.Id);
        check(byId.Count == 3 && byId.ContainsKey(m1) && byId.ContainsKey(m2) && byId.ContainsKey(m3) && !byId.ContainsKey(m4) && byId[m3].Direct && byId[m2].Direct && !byId[m1].Direct && byId[m1].ViaTopics.Count == 1, p + "M31 Übersicht „Gemeinsam bearbeitet“: nur zugängliche Module mit Zugriffsart, keine fremde Bibliothek");
        await topics.RemoveModuleCoAuthorAsync(owner, m2, co);

        // Verlassen
        await topics.LeaveModuleCoAuthorAsync(co, m3);
        check(await Kind(co, m3) == ModuleAccessKind.None && await Denied(() => topics.LeaveModuleCoAuthorAsync(co, m3)), p + "M32 Direkter Co-Author beendet die eigene Mitarbeit");

        // eingeschränkte Lernprofile
        if (restricted is not null)
        {
            check(await Denied(() => topics.AddModuleCoAuthorAsync(owner, m3, restricted)) && !(await topics.SearchModuleCoAuthorCandidatesAsync(owner, m3, "extern")).Any(u => u.Id == restricted), p + "M33 Eingeschränkte Lernprofile können keine Modul-Co-Autoren sein");
            await db.ExecuteAsync("INSERT INTO module_coauthors (module_id, user_id, granted_by, created_at) VALUES (@m,@u,@g,@n)", new (string, object?)[] { ("m", m3), ("u", restricted), ("g", owner), ("n", DateTime.UtcNow) });
            check(await Kind(restricted, m3) == ModuleAccessKind.None && (await topics.ListCoEditedModulesAsync(restricted)).Count == 0, p + "M34 Selbst ein vorhandener Eintrag gewährt einem eingeschränkten Profil nie Zugriff");
            await db.ExecuteAsync("DELETE FROM module_coauthors WHERE user_id=@u", new (string, object?)[] { ("u", restricted) });
        }

        // ─── Regression: sofortiger Entzug, unabhängig vom Razor-Editor (ADR 0052/0053) ───
        foreach (var kind in new[] { "direkt", "abgeleitet" })
        {
            var module = kind == "direkt" ? m3 : m1;
            var cardId = kind == "direkt" ? c3.Id : c1.Id; var modName = kind == "direkt" ? n3 : n1;
            if (kind == "direkt") await topics.AddModuleCoAuthorAsync(owner, m3, co); else if (await topics.GetTopicRoleAsync(co, t1.Id) == TopicRole.None) await topics.AddCoAuthorAsync(owner, t1.Id, co);
            var staleOwnerContext = await topics.ResolveModuleEditOwnerAsync(co, module);   // beim Öffnen des Editors ermittelter Kontext
            check(staleOwnerContext == owner && await repo.UpdateCardAsync(cardId, Json(modName, "Vor Entzug " + kind), staleOwnerContext, (await repo.GetCardAsync(cardId, owner))!.UpdatedAt) is not null, p + $"E1 ({kind}) vor dem Entzug: Bearbeitung mit dem Kontext möglich");
            var cards = (await repo.ListCardsAsync(owner, module, null, archived: false)).Select(c => c.Id).ToList();
            var order = cards.AsEnumerable().Reverse().ToList();
            var termBefore = await Term(cardId); var countBefore = cards.Count; var modelBefore = (await comp.GetModelRawAsync(module)).Competencies.Count;
            if (kind == "direkt") await topics.RemoveModuleCoAuthorAsync(owner, m3, co); else await topics.RemoveCoAuthorAsync(owner, t1.Id, co);

            // 1) jede Auflösung nach dem Entzug liefert den Handelnden selbst — gecachte Rechte gibt es nicht
            check(await topics.ResolveModuleEditOwnerAsync(co, module) == co && !await topics.CanEditModuleAsync(co, module), p + $"E2 ({kind}) nach dem Entzug liefert die zentrale Entscheidung sofort „kein Zugriff“");
            // 2) sämtliche Schreibpfade mit der Identität des Handelnden werden abgewiesen (Dienstebene, unabhängig von der Oberfläche)
            var cardBefore = await repo.GetCardAsync(cardId, owner);
            var results = new[]
            {
                await NotWritten(async () => await repo.UpdateCardAsync(cardId, Json(modName, "Nach Entzug"), co, cardBefore!.UpdatedAt) is not null),
                await NotWritten(async () => await repo.CreateCardAsync(Json(modName, "Neue Karte nach Entzug"), co) is { } created2 && created2.ModuleId == module),
                await NotWritten(async () => await repo.DuplicateCardAsync(cardId, co) is not null),
                await NotWritten(async () => await repo.SetArchivedAsync(cardId, true, co) is not null),
                await NotWritten(async () => await repo.ReorderCardsAsync(module, order, co) > 0),
                await NotWritten(async () => { await comp.SaveModelAsync(co, module, new ModuleCompetencyModel { Objectives = { new LearningObjective { Id = "x1", Text = "Hack" } } }); return true; }),
            };
            check(results.All(r => r), p + $"E3 ({kind}) nach dem Entzug werden Karte ändern/anlegen/duplizieren/archivieren/verschieben und Kompetenzen abgewiesen ({string.Join("", results.Select(r => r ? "✓" : "✗"))})");
            var after = await repo.GetCardAsync(cardId, owner);
            check(await Term(cardId) == termBefore && !after!.Archived && (await repo.ListCardsAsync(owner, module, null, archived: false)).Count == countBefore
                  && (await comp.GetModelRawAsync(module)).Competencies.Count == modelBefore && (await comp.GetModelRawAsync(module)).Objectives.All(o => o.Id != "x1"), p + $"E4 ({kind}) Datenbank unverändert: Karte, Kartenzahl, Archivstatus und Kompetenzen");
            check((await repo.GetModuleAsync(module, owner))!.OwnerUserId == owner && await repo.UpdateCardAsync(cardId, Json(modName, "Eigentümer " + kind), owner, after!.UpdatedAt) is not null, p + $"E5 ({kind}) der Eigentümer kann weiterhin bearbeiten");
            // 3) erneute Gewährung → Bearbeitung wieder möglich
            if (kind == "direkt") await topics.AddModuleCoAuthorAsync(owner, m3, co); else await topics.AddCoAuthorAsync(owner, t1.Id, co);
            var fresh = await topics.ResolveModuleEditOwnerAsync(co, module);
            check(fresh == owner && await repo.UpdateCardAsync(cardId, Json(modName, "Wieder erlaubt " + kind), fresh, (await repo.GetCardAsync(cardId, owner))!.UpdatedAt) is not null, p + $"E6 ({kind}) nach erneuter Gewährung ist die Bearbeitung wieder möglich");
            // 4) gleichzeitige Zugriffe: parallele Schreibschleifen, während der Entzug erfolgt — nach dem Entzug darf keine Schleife mehr schreiben
            var stop = false; var writesAfterRevoke = 0; var revoked = false;
            async Task Writer(int i)
            {
                while (!stop)
                {
                    var wasRevoked = Volatile.Read(ref revoked);   // Entzug vollständig abgeschlossen, bevor die Auflösung beginnt
                    var ctx2 = await topics.ResolveModuleEditOwnerAsync(co, module);
                    if (ctx2 != co)
                    {
                        try { await repo.CreateCardAsync(Json(modName, $"Race {i} {Guid.NewGuid():N}"), ctx2); } catch { }
                        if (wasRevoked) Interlocked.Increment(ref writesAfterRevoke);
                    }
                    await Task.Delay(1);
                }
            }
            var writers = Enumerable.Range(0, 4).Select(i => Task.Run(() => Writer(i))).ToList();
            await Task.Delay(150);
            if (kind == "direkt") await topics.RemoveModuleCoAuthorAsync(owner, m3, co); else await topics.RemoveCoAuthorAsync(owner, t1.Id, co);
            Volatile.Write(ref revoked, true);
            await Task.Delay(250); stop = true; await Task.WhenAll(writers);
            check(writesAfterRevoke == 0, p + $"E7 ({kind}) parallele Bearbeitung während des Entzugs: nach abgeschlossenem Entzug erfolgt kein weiterer Schreibzugriff");
            // Aufräumen für die nächste Runde: die in der Schleife angelegten Karten bleiben beim Eigentümer (keine Datenlöschung nötig)
        }

        // Moduleigentümer unverändert, Löschen des Moduls entfernt direkte Berechtigungen
        await topics.AddModuleCoAuthorAsync(owner, m4, co);
        var delModule = await Mod("Mod-Del-" + Guid.NewGuid().ToString("N")[..6]);
        await topics.AddModuleCoAuthorAsync(owner, delModule, co);
        await repo.DeleteModuleAsync(delModule, owner);
        check((await db.QueryAsync("SELECT COUNT(*) AS n FROM module_coauthors WHERE module_id=@m", new (string, object?)[] { ("m", delModule) }))[0]["n"] == "0", p + "M35 Beim Löschen des Moduls entfallen seine direkten Berechtigungen (ON DELETE CASCADE)");
        check((await db.QueryAsync("SELECT COUNT(*) AS n FROM modules WHERE owner_user_id=@o AND id IN (@a,@b,@c,@d)", new (string, object?)[] { ("o", owner), ("a", m1), ("b", m2), ("c", m3), ("d", m4) }))[0]["n"] == "4", p + "M36 Alle Module gehören weiterhin dem ursprünglichen Eigentümer");
        await VersionsCoreAsync(p, db, topics, repo, comp, newUser, restricted, check);
    }

    /// <summary>ADR 0059: Co-Autoren verwalten und veröffentlichen Modulversionen (Eigentümer, direkt, abgeleitet); getrennt von Lernfreigabe, globaler Freigabe und Themenveröffentlichung.</summary>
    private static async Task VersionsCoreAsync(string p, IDatabase db, TopicService topics, CardRepository repo, CompetencyService comp, Func<string, Task<string>> newUser, string? restricted, Action<bool, string> check)
    {
        var owner = await newUser("Vera Owner"); var direct = await newUser("Dirk Direkt"); var via = await newUser("Viola Via"); var stranger = await newUser("Sven Fremd");
        var sfx = Guid.NewGuid().ToString("N")[..6];
        string mod1 = (await repo.CreateModuleAsync(new ModuleCreateRequest { Name = "VM1-" + sfx }, owner)).Id, mod2 = (await repo.CreateModuleAsync(new ModuleCreateRequest { Name = "VM2-" + sfx }, owner)).Id;
        var card1 = await repo.CreateCardAsync(Json("VM1-" + sfx, "Karte 1", "alt"), owner);
        var card2 = await repo.CreateCardAsync(Json("VM2-" + sfx, "Karte 2"), owner);
        var tA = await topics.CreateAsync(owner, "VT-A " + sfx, ""); var tB = await topics.CreateAsync(owner, "VT-B " + sfx, "");
        await topics.AddModuleAsync(owner, tA.Id, mod1);                       // Thema A bindet Modul 1 (festgelegte Version 1)
        await topics.AddModuleAsync(owner, tA.Id, mod2); await topics.AddModuleAsync(owner, tB.Id, mod2);   // Modul 2 in zwei Themen (Mehrfachzuordnung)
        async Task<string> Pin(string topic, string module) => (await db.QueryAsync("SELECT module_version FROM topic_modules WHERE topic_id=@t AND module_id=@m", new (string, object?)[] { ("t", topic), ("m", module) }))[0]["module_version"] ?? "";
        async Task<int> Count(string module) => (await topics.GetModuleVersionOverviewAsync(owner, module)).Versions.Count;
        async Task Edit(Card c, string module, string def, string actor) => await repo.UpdateCardAsync(c.Id, Json(module, c.Term, def), actor, (await repo.GetCardAsync(c.Id, owner))!.UpdatedAt);

        // A Eigentümer
        var ov0 = await topics.GetModuleVersionOverviewAsync(owner, mod1);
        check(ov0.LatestVersion == 1 && !ov0.HasUnpublishedChanges, p + "V1 Ausgangslage: Version 1 (beim Festlegen im Thema erzeugt), Entwurf unverändert");
        await Edit(card1, "VM1-" + sfx, "owner", owner);
        var vo = await topics.PublishModuleVersionAsync(owner, mod1, "Eigentümer", null);
        check(vo.VersionNumber == 2, p + "V2 (A) Eigentümer veröffentlicht Version 2");

        // B direkter Co-Autor
        await topics.AddModuleCoAuthorAsync(owner, mod1, direct);
        var ctx = await topics.ResolveModuleEditOwnerAsync(direct, mod1);
        await Edit(card1, "VM1-" + sfx, "direkt", ctx);
        await repo.CreateCardAsync(Json("VM1-" + sfx, "Neue Karte"), ctx);
        var ovd = await topics.GetModuleVersionOverviewAsync(direct, mod1);
        check(ovd.HasUnpublishedChanges && ovd.LatestVersion == 2 && ovd.Versions.Count == 2, p + "V3 (B/K) Direkter Co-Autor sieht Historie und unveröffentlichte Änderungen");
        var diff = await topics.CompareDraftWithVersionAsync(direct, mod1, 2);
        check(diff.Added.Contains("Neue Karte") && diff.Changed.Contains("Karte 1"), p + "V4 (K) Vergleich Entwurf ↔ Version zeigt neue und geänderte Karten");
        var v3 = await topics.PublishModuleVersionAsync(direct, mod1, "Co", ovd.DraftHash);
        var row = (await db.QueryAsync("SELECT published_by FROM module_versions WHERE module_id=@m AND version_number=3", new (string, object?)[] { ("m", mod1) }))[0];
        check(v3.VersionNumber == 3 && row["published_by"] == direct && (await repo.GetModuleAsync(mod1, owner))!.OwnerUserId == owner, p + "V5 (B) Co-Autor veröffentlicht Version 3; Handelnder in published_by, Eigentümer unverändert");
        var (vv1, cards1) = await topics.GetModuleVersionCardsAsync(direct, mod1, 1); var (_, cards3) = await topics.GetModuleVersionCardsAsync(direct, mod1, 3);
        check(cards1.Count == 1 && cards1[0].Definition == "alt" && cards3.Count == 2 && cards3.Any(c => c.Term == "Neue Karte") && vv1.VersionNumber == 1, p + "V6 (G/K) Version 1 bleibt unverändert, Version 3 enthält die neue Karte, beide für den Co-Autor lesbar");
        check((await db.QueryAsync("SELECT COUNT(*) AS n FROM audit_log WHERE actor_user_id=@u AND action='module.version.publish'", new (string, object?)[] { ("u", direct) }))[0]["n"] == "1", p + "V7 Audit nennt den tatsächlich Handelnden");
        check(await Denied(() => topics.PublishModuleVersionAsync(direct, mod1, null, null)), p + "V8 ohne Änderung keine Version");

        // I Themen unverändert
        check(await Pin(tA.Id, mod1) == "1" && (await db.QueryAsync("SELECT 1 FROM topic_versions WHERE topic_id=@t", new (string, object?)[] { ("t", tA.Id) })).Count == 0, p + "V9 (I) Thema bleibt auf Version 1, keine Themenversion entstanden");
        check((await db.QueryAsync("SELECT COUNT(*) AS n FROM module_shares WHERE module_id=@m", new (string, object?)[] { ("m", mod1) }))[0]["n"] == "0", p + "V10 (J) keine Lernfreigabe erzeugt");

        // H parallele Veröffentlichung
        await Edit(card1, "VM1-" + sfx, "parallel", owner);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i => { try { await topics.PublishModuleVersionAsync(i % 2 == 0 ? direct : owner, mod1, null, null); return 1; } catch (InvalidOperationException) { return 0; } }));
        var nums = (await db.QueryAsync("SELECT version_number FROM module_versions WHERE module_id=@m", new (string, object?)[] { ("m", mod1) })).Select(r => r["version_number"]).ToList();
        check(results.Sum() == 1 && nums.Count == 4 && nums.Distinct().Count() == 4, p + "V11 (H) vier parallele Veröffentlichungen erzeugen genau eine neue, eindeutige Version");

        // Konflikt: veralteter Stand
        var stale = (await topics.GetModuleVersionOverviewAsync(direct, mod1)).DraftHash;
        await Edit(card1, "VM1-" + sfx, "anderer", owner);
        check(await Denied(() => topics.PublishModuleVersionAsync(direct, mod1, null, stale)) && await Count(mod1) == 4, p + "V12 Veralteter Editorstand (Fingerabdruck) wird nicht veröffentlicht");

        // C/D abgeleitet, Mehrfachzuordnung
        await topics.AddCoAuthorAsync(owner, tA.Id, via);
        check((await topics.PublishModuleVersionAsync(via, mod1, "Via", null)).VersionNumber == 5, p + "V13 (C) Themen-Co-Autor veröffentlicht Version bei gültiger abgeleiteter Berechtigung");
        await Edit(card2, "VM2-" + sfx, "geändert", owner);
        check(await Denied(() => topics.PublishModuleVersionAsync(via, mod2, null, null)) && await Denied(() => topics.GetModuleVersionOverviewAsync(via, mod2)), p + "V14 (D) Mehrfachzuordnung ohne Recht am zweiten Thema: weder lesen noch veröffentlichen");

        // J: Co-Autoren lösen weder Themenveröffentlichung noch Übernahme aus
        check(await Denied(() => topics.PublishAsync(via, tA.Id)) && await Denied(() => topics.PublishAsync(direct, tA.Id)), p + "V15 (J) Co-Autoren veröffentlichen keine Themenversion (Eigentümerbindung unverändert)");
        check(await Pin(tA.Id, mod1) == "1", p + "V16 (I) Pin des Themas bleibt nach allen Veröffentlichungen auf Version 1");
        var adopted = await topics.AdoptModuleVersionAsync(owner, tA.Id, mod1);
        check(adopted == 5 && await Pin(tA.Id, mod1) == "5", p + "V17 Übernahme nur durch den Eigentümer und ausdrücklich");

        // Automatische Modulversion durch Themen-Co-Autor (Übernahme ohne Versionsangabe): Handelnder statt Eigentümer
        await Edit(card1, "VM1-" + sfx, "auto", owner);
        var autoBefore = await Count(mod1);
        await topics.AddCoAuthorAsync(owner, tA.Id, via);
        var autoPin = await topics.AdoptModuleVersionAsync(via, tA.Id, mod1);
        var autoRow = (await db.QueryAsync("SELECT published_by, note FROM module_versions WHERE module_id=@m AND version_number=@n", new (string, object?)[] { ("m", mod1), ("n", autoPin) }))[0];
        check(await Count(mod1) == autoBefore + 1 && autoRow["published_by"] == via && (await repo.GetModuleAsync(mod1, owner))!.OwnerUserId == owner, p + "V21 Automatische Modulversion bei Übernahme durch den Themen-Co-Autor nennt den Handelnden in published_by, Eigentümer unverändert");
        check((await db.QueryAsync("SELECT COUNT(*) AS n FROM audit_log WHERE actor_user_id=@u AND action='module.version.publish' AND target=@m AND detail=@d", new (string, object?)[] { ("u", via), ("m", mod1), ("d", $"version={autoPin}") }))[0]["n"] == "1", p + "V22 Audit der automatischen Version nennt den Handelnden");
        check(await Denied(() => topics.PublishAsync(via, tA.Id)), p + "V23 Übernahme erlaubt dem Co-Autor weiterhin keine Themenveröffentlichung");
        await topics.AdoptModuleVersionAsync(via, tA.Id, mod1);
        check(await Count(mod1) == autoBefore + 1, p + "V24 Wiederholte Übernahme ohne Änderung erzeugt keine weitere Version");

        // Inhaltsfingerabdruck: Lernziele und Kompetenzdefinitionen (ADR 0031)
        async Task<string> Hash() => (await topics.GetModuleVersionOverviewAsync(owner, mod1)).DraftHash;
        var h0 = await Hash();
        var model = new ModuleCompetencyModel { Objectives = { new LearningObjective { Id = "lz1", Text = "Ziel A" } }, Competencies = { new Competency { Id = "k1", ObjectiveId = "lz1", Name = "Komp A" } } };
        await comp.SaveModelAsync(owner, mod1, model); var h1 = await Hash();
        await comp.SaveModelAsync(owner, mod1, new ModuleCompetencyModel { Objectives = { new LearningObjective { Id = "lz1", Text = "Ziel A" } }, Competencies = { new Competency { Id = "k1", ObjectiveId = "lz1", Name = "Komp A" } } });
        check(h1 != h0 && await Hash() == h1, p + "V25 Neue Lernziele/Kompetenzen ändern den Fingerabdruck; erneutes Speichern identischer Inhalte nicht");
        await comp.SaveModelAsync(owner, mod1, new ModuleCompetencyModel { Objectives = { new LearningObjective { Id = "lz1", Text = "Ziel B" } }, Competencies = { new Competency { Id = "k1", ObjectiveId = "lz1", Name = "Komp A" } } });
        var h2 = await Hash();
        await comp.SaveModelAsync(owner, mod1, new ModuleCompetencyModel { Objectives = { new LearningObjective { Id = "lz1", Text = "Ziel B" } }, Competencies = { new Competency { Id = "k1", ObjectiveId = "lz1", Name = "Komp B" } } });
        check(h2 != h1 && await Hash() != h2, p + "V26 Änderung eines Lernziel-Textes und eines Kompetenznamens ändert jeweils den Fingerabdruck");
        var cn = (await db.QueryAsync("SELECT 1 FROM module_versions WHERE module_id=@m", new (string, object?)[] { ("m", mod1) })).Count;
        var vc = await topics.PublishModuleVersionAsync(owner, mod1, "Kompetenzen", null);
        var snapModel = await comp.GetModelRawAsync(vc.SnapshotModuleId);
        check(vc.VersionNumber == cn + 1 && snapModel.Competencies.Single().Name == "Komp B" && snapModel.Objectives.Single().Text == "Ziel B", p + "V27 Kompetenzänderung ist allein ein Versionsgrund; die Version enthält Lernziele und Kompetenzen");
        await Edit(card1, "VM1-" + sfx, "x2", owner);
        await repo.UpdateCardAsync(card1.Id, Json("VM1-" + sfx, "Karte 1", "x2"), owner, (await repo.GetCardAsync(card1.Id, owner))!.UpdatedAt);
        check(await Denied(() => topics.PublishModuleVersionAsync(owner, mod1, null, vc.ContentHash)), p + "V28 Veralteter Fingerabdruck der Vorversion wird nicht als aktueller Stand akzeptiert");
        check((await comp.GetModelRawAsync(mod1)).Objectives.Count == 1 && (await db.QueryAsync("SELECT 1 FROM module_versions WHERE module_id=@m AND version_number=1", new (string, object?)[] { ("m", mod1) })).Count == 1, p + "V29 Frühere Versionen bleiben unverändert vorhanden");

        // F Fremder, E Entzug, L eingeschränktes Profil
        check(await Denied(() => topics.PublishModuleVersionAsync(stranger, mod1, null, null)) && await Denied(() => topics.GetModuleVersionOverviewAsync(stranger, mod1)) && await Denied(() => topics.GetModuleVersionCardsAsync(stranger, mod1, 1)) && await Denied(() => topics.CompareDraftWithVersionAsync(stranger, mod1, 1)), p + "V18 (F) Fremder Benutzer kann weder lesen noch veröffentlichen");
        await Edit(card1, "VM1-" + sfx, "nach-entzug", owner);
        await topics.RemoveModuleCoAuthorAsync(owner, mod1, direct); await topics.RemoveCoAuthorAsync(owner, tA.Id, via);
        var before = await Count(mod1);
        check(await Denied(() => topics.PublishModuleVersionAsync(direct, mod1, null, null)) && await Denied(() => topics.PublishModuleVersionAsync(via, mod1, null, null)) && await Count(mod1) == before, p + "V19 (E) Entzogene Co-Autoren können nicht mehr veröffentlichen, keine unvollständige Version");
        if (restricted is not null)
        {
            await db.ExecuteAsync("INSERT INTO module_coauthors (module_id, user_id, granted_by, created_at) VALUES (@m,@u,@a,@t)", new (string, object?)[] { ("m", mod1), ("u", restricted), ("a", owner), ("t", DateTime.UtcNow) });
            check(await Denied(() => topics.PublishModuleVersionAsync(restricted, mod1, null, null)) && await Denied(() => topics.GetModuleVersionOverviewAsync(restricted, mod1)), p + "V20 (L) Eingeschränktes Lernprofil erhält trotz Co-Autor-Eintrag kein Recht");
        }
    }

    // ─── REST ────────────────────────────────────────────────────────────────

    private static JsonNode? Parse(string s) { try { return JsonNode.Parse(s); } catch { return null; } }
    private static async Task<JsonNode?> J(HttpResponseMessage r) => Parse(await r.Content.ReadAsStringAsync());
    private static string S(JsonNode? n, string path) { var cur = n; foreach (var p in path.Split('.')) cur = cur is JsonArray a && int.TryParse(p, out var i) ? (i < a.Count ? a[i] : null) : cur?[p]; return cur?.ToString() ?? ""; }
    private static StringContent Body(object o) => new(System.Text.Json.JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

    public static async Task RunHttpAsync(Func<string?, Task<HttpClient>> login, Func<HttpClient> anonymous, Action<bool, string> check)
    {
        Console.WriteLine("\nModul-Co-Autoren (ADR 0053), REST");
        var o = await login("mca-owner"); var c = await login("mca-co"); var d = await login("mca-third"); var x = await login("mca-outsider");
        var cId = S(await J(await c.GetAsync("/api/me")), "id"); var dId = S(await J(await d.GetAsync("/api/me")), "id");
        var mod = S(await J(await o.PostAsync("/api/modules/", Body(new { name = "MCA-Mod" }))), "id");
        var cardId = S(await J(await o.PostAsync("/api/cards/", Body(new { module = "MCA-Mod", category = "K", term = "T", question = "Q?", definition = "D" }))), "id");
        check((await c.GetAsync($"/api/modules/{mod}/coauthors")).StatusCode == HttpStatusCode.NotFound && (await x.PostAsync($"/api/modules/{mod}/coauthors", Body(new { user_id = cId }))).StatusCode == HttpStatusCode.NotFound, "A REST: Fremde sehen und verwalten keine Modulzusammenarbeit (404)");
        var add = await o.PostAsync($"/api/modules/{mod}/coauthors", Body(new { user_id = cId }));
        check(add.StatusCode == HttpStatusCode.Created, "B REST: Eigentümer fügt einen direkten Modul-Co-Author hinzu");
        var view = await J(await c.GetAsync($"/api/modules/{mod}/coauthors"));
        check(S(view, "access") == "direct" && S(view, "can_manage") == "true" && S(view, "direct_co_authors.0.user_id") == cId && S(view, "owner.display_name").Length > 0, "C REST: direkter Co-Author sieht die Zusammenarbeit und ist als „direct“ erkennbar");
        check((await c.PostAsync($"/api/modules/{mod}/coauthors", Body(new { user_id = dId }))).StatusCode == HttpStatusCode.Created && (await c.GetAsync($"/api/modules/{mod}/coauthors/candidates?q=mca")).IsSuccessStatusCode, "D REST: direkter Co-Author verwaltet weitere Co-Autoren und sucht Kandidaten");
        check((await c.DeleteAsync($"/api/modules/{mod}/coauthors/{S(await J(await o.GetAsync($"/api/modules/{mod}/coauthors")), "owner.user_id")}")).StatusCode == HttpStatusCode.BadRequest, "E REST: der Eigentümer kann nicht entfernt werden");
        check((await c.DeleteAsync($"/api/modules/{mod}/coauthors/{dId}")).StatusCode == HttpStatusCode.NoContent && (await d.GetAsync($"/api/modules/{mod}/coauthors")).StatusCode == HttpStatusCode.NotFound, "F REST: Entfernen wirkt sofort");
        check((await c.PutAsync($"/api/cards/{cardId}", Body(new { module = "MCA-Mod", category = "K", term = "T", question = "Q?", definition = "Hack" }))).StatusCode == HttpStatusCode.NotFound && (await c.GetAsync($"/api/modules/{mod}/export")).StatusCode == HttpStatusCode.NotFound, "G REST: Karten- und Modul-REST bleiben eigentümergebunden (auch für direkte Co-Autoren)");

        // MCP-Token: alle fünf Verwaltungsoperationen gesperrt — für Eigentümer und direkten Co-Author
        foreach (var (who, client) in new[] { ("Eigentümer", o), ("Co-Author", c) })
        {
            var tok = S(await J(await client.PostAsync("/api/mcp/links", Body(new { name = "MCA-Token-" + who, days = 1 }))), "token");
            var tc = new HttpClient { BaseAddress = client.BaseAddress }; tc.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tok);
            var codes = new[]
            {
                (await tc.GetAsync($"/api/modules/{mod}/coauthors")).StatusCode, (await tc.GetAsync($"/api/modules/{mod}/coauthors/candidates?q=abc")).StatusCode,
                (await tc.PostAsync($"/api/modules/{mod}/coauthors", Body(new { user_id = dId }))).StatusCode, (await tc.DeleteAsync($"/api/modules/{mod}/coauthors/{dId}")).StatusCode,
                (await tc.PostAsync($"/api/modules/{mod}/coauthors/leave", Body(new { }))).StatusCode,
            };
            check(tok.StartsWith("lcm_") && codes.All(sc => sc == HttpStatusCode.Forbidden), $"H MCP {who}: GET, Kandidaten, POST, DELETE und leave auf /modules/{{id}}/coauthors → 403 ({string.Join(",", codes.Select(sc => (int)sc))})");
        }
        check((await anonymous().GetAsync($"/api/modules/{mod}/coauthors")).StatusCode == HttpStatusCode.Unauthorized, "I REST: ohne Anmeldung keine Zusammenarbeit lesbar");

        // abgeleiteter Zugriff: ein Themen-Co-Author sieht das Modul als „via_topic“ und verwaltet keine direkten Co-Autoren
        var topic = S(await J(await o.PostAsync("/api/topics/", Body(new { name = "MCA-Thema", description = "" }))), "id");
        await o.PostAsync($"/api/topics/{topic}/modules", Body(new { module_id = mod }));
        await o.PostAsync($"/api/topics/{topic}/coauthors", Body(new { user_id = dId }));
        var dv = await J(await d.GetAsync($"/api/modules/{mod}/coauthors"));
        check(S(dv, "access") == "via_topic" && S(dv, "can_manage") == "false" && S(dv, "via_topics.0.topic_id") == topic, "J REST: Themen-Co-Author hat abgeleiteten Zugriff (via_topic), nicht verwaltend");
        check((await d.PostAsync($"/api/modules/{mod}/coauthors", Body(new { user_id = cId }))).StatusCode == HttpStatusCode.NotFound && (await d.PostAsync($"/api/modules/{mod}/coauthors/leave", Body(new { }))).StatusCode == HttpStatusCode.NotFound, "K REST: abgeleiteter Zugriff darf direkte Co-Autoren weder verwalten noch „verlassen“");
        await o.DeleteAsync($"/api/topics/{topic}/coauthors/{dId}");
        check((await d.GetAsync($"/api/modules/{mod}/coauthors")).StatusCode == HttpStatusCode.NotFound, "L REST: Themenentzug entzieht den abgeleiteten Modulzugriff sofort");
        check((await c.PostAsync($"/api/modules/{mod}/coauthors/leave", Body(new { }))).StatusCode == HttpStatusCode.NoContent && (await c.GetAsync($"/api/modules/{mod}/coauthors")).StatusCode == HttpStatusCode.NotFound, "M REST: direkter Co-Author beendet die eigene Mitarbeit");
    }
}
