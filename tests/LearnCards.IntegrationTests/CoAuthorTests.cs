using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LearnCards.Web.Domain;
using LearnCards.Web.Services;

/// <summary>
/// Co-Author-Modus (ADR 0052). Teil 1 im Prozess auf SQLite (Berechtigungsmodell, Modul-/Kartenkontext, Konflikte, Entzug); Teil 2 über die REST-API des laufenden Servers.
/// PostgreSQL ist hiermit nicht belegt (gleiche SQL-Anweisungen wie bei den übrigen Themen, siehe docs/database-schema.md).
/// </summary>
public static class CoAuthorTests
{
    private static async Task<T?> Try<T>(Func<Task<T>> f) where T : class { try { return await f(); } catch (Exception e) when (e is KeyNotFoundException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { return null; } }
    private static async Task<bool> Denied(Func<Task> f) { try { await f(); return false; } catch (Exception e) when (e is KeyNotFoundException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or CardRepository.CardConflictException) { return true; } }

    public static async Task RunAsync(Action<bool, string> check, string tmp)
    {
        Console.WriteLine("\nCo-Author-Modus (ADR 0052), im Prozess auf SQLite");
        var e = await InvitationUnitTests.Env.CreateAsync(Path.Combine(tmp, "coauthor"));
        var owner = await e.AddWorkforceUserAsync("Olga Owner", "olga@example.org");
        var c1 = await e.AddWorkforceUserAsync("Carl Co", "carl@example.org");
        var c2 = await e.AddWorkforceUserAsync("Cora Co", "cora@example.org");
        var outsider = await e.AddWorkforceUserAsync("Otto Outsider", "otto@example.org");
        var ext = await e.AddWorkforceUserAsync("Extern Eva", "eva@example.org");
        await e.Raw.ExecuteAsync("UPDATE user_identities SET kind='external' WHERE user_id=@u", new (string, object?)[] { ("u", ext) });   // eingeschränktes Lernprofil (ADR 0049)

        // A: Eigentümer erstellt ein Thema mit eigenem privaten Modul und einer Karte
        var topic = await e.Topics.CreateAsync(owner, "Gemeinsames Thema", "Beschreibung");
        var mod = await e.Repo.CreateModuleAsync(new ModuleCreateRequest { Name = "Co-Modul" }, owner);
        var card = await e.Repo.CreateCardAsync(new CardJson { Module = "Co-Modul", Category = "K", Term = "T1", Question = "Frage?", Definition = "D" }, owner);
        await e.Topics.AddModuleAsync(owner, topic.Id, mod.Id);
        check(topic.OwnerUserId == owner && await e.Topics.GetTopicRoleAsync(owner, topic.Id) == TopicRole.Owner, "A Eigentümer erstellt ein Thema und behält die Rolle Eigentümer");

        // C: ohne Co-Author-Eintrag kein Zugriff; B: Eigentümer fügt hinzu
        check(await e.Topics.GetTopicRoleAsync(c1, topic.Id) == TopicRole.None && await Denied(() => e.Topics.GetDetailAsync(c1, topic.Id)), "C vorher: kein Co-Author sieht den Entwurf nicht");
        var added = await e.Topics.AddCoAuthorAsync(owner, topic.Id, c1);
        check(added.UserId == c1 && await e.Topics.GetTopicRoleAsync(c1, topic.Id) == TopicRole.CoAuthor, "B Eigentümer fügt einen Co-Author hinzu (Rolle nur für dieses Thema)");
        var idem = await e.Topics.AddCoAuthorAsync(owner, topic.Id, c1);
        check(idem.UserId == c1 && (await e.Topics.GetCollaborationAsync(owner, topic.Id)).CoAuthors.Count == 1, "B Hinzufügen ist idempotent (genau ein Eintrag je Thema und Benutzer)");
        var detail = await e.Topics.GetDetailAsync(c1, topic.Id);
        check(detail.IsCoAuthor && !detail.IsOwner && detail.CanEdit && detail.IsDraft && detail.Modules.Count == 1 && detail.Section == TopicSections.CoAuthored, "C Co-Author sieht das Thema (Entwurf mit Modulen), gekennzeichnet als Co-Author");
        check((await e.Topics.ListOverviewAsync(c1)).CoAuthored.Any(t => t.Topic.Id == topic.Id && t.Relation == TopicRelations.CoAuthor)
              && !(await e.Topics.ListOverviewAsync(c1)).Own.Any(), "C Themenübersicht: Thema erscheint unter „Gemeinsam bearbeitet“, nicht unter „Meine Themen“");

        // D: Themenentwurf bearbeiten; N: Konflikt bei gleichzeitiger Änderung
        var rev = detail.Topic.DraftRevision;
        var upd = await e.Topics.UpdateAsync(c1, topic.Id, "Gemeinsames Thema v2", "neu", rev);
        check(upd.Name == "Gemeinsames Thema v2" && upd.OwnerUserId == owner, "D Co-Author bearbeitet den Themenentwurf (Eigentümer bleibt unverändert)");
        check(await Denied(() => e.Topics.UpdateAsync(owner, topic.Id, "Überschrieben", "x", rev)), "N Gleichzeitige Änderung: veraltete Entwurfsrevision wird abgelehnt (Konflikt), nichts überschrieben");
        check((await e.Topics.GetDetailAsync(owner, topic.Id)).Topic.Name == "Gemeinsames Thema v2", "N Der Name aus der zuerst gespeicherten Änderung bleibt erhalten");
        var cfg = await e.Topics.GetExamConfigAsync(c1, topic.Id);
        var cfgSaved = await Try(() => e.Topics.SaveExamConfigAsync(c1, topic.Id, cfg));
        check(cfgSaved is not null, "D Co-Author bearbeitet die Prüfungsstruktur des Themas");
        check(await Try(() => e.Topics.CreateQuestionAsync(c1, topic.Id, new QuizQuestionInput { Type = "single_choice", Question = "Welche?", Options = new() { "a", "b" }, CorrectIndexes = new() { 0 }, Explanation = "a" })) is not null, "D Co-Author legt eine Themenfrage an");
        var modOrder = await Try(async () => { await e.Topics.ReorderModulesAsync(c1, topic.Id, new[] { mod.Id }); return new object(); });
        check(modOrder is not null, "D Co-Author ordnet Module des Themas");

        // E / R: Karten im Themenkontext; private Module außerhalb des Themas bleiben geschützt
        var editOwner = await e.Topics.ResolveModuleEditOwnerAsync(c1, mod.Id);
        check(editOwner == owner, "E Bearbeitungskontext: das Modul liegt im Entwurf des Themas → Dienste arbeiten unter dem Moduleigentümer");
        var edited = await e.Repo.UpdateCardAsync(card.Id, new CardJson { Module = "Co-Modul", Category = "K", Term = "T1", Question = "Frage?", Definition = "Von Co-Author geändert" }, editOwner, (await e.Repo.GetCardAsync(card.Id, owner))!.UpdatedAt);
        check(edited is not null && (await e.Repo.GetCardAsync(card.Id, owner))!.Definition == "Von Co-Author geändert", "E Co-Author bearbeitet die zugeordnete Lernkarte");
        var staleStamp = (await e.Repo.GetCardAsync(card.Id, owner))!.UpdatedAt.AddSeconds(-5);   // Stand, den der Eigentümer vor der Änderung des Co-Autors geladen hatte
        var conflict = await Denied(() => e.Repo.UpdateCardAsync(card.Id, new CardJson { Module = "Co-Modul", Category = "K", Term = "T1", Question = "Frage?", Definition = "Überschrieben" }, owner, staleStamp));
        check(conflict, "N Gleichzeitige Kartenänderung: veralteter Stand wird abgelehnt, keine stillschweigende Überschreibung");
        var privateOther = await e.AddPrivateModuleAsync(owner, "Privat, nicht im Thema");
        check(await e.Topics.ResolveModuleEditOwnerAsync(c1, privateOther) == c1 && await e.Repo.GetModuleAsync(privateOther, await e.Topics.ResolveModuleEditOwnerAsync(c1, privateOther)) is null,
            "R Privates Modul des Eigentümers außerhalb des Themas bleibt für den Co-Author unzugänglich");
        var topic2 = await e.Topics.CreateAsync(owner, "Anderes Thema", "");
        var mod2 = await e.Repo.CreateModuleAsync(new ModuleCreateRequest { Name = "Nur in Thema B" }, owner);
        await e.Topics.AddModuleAsync(owner, topic2.Id, mod2.Id);
        check(await e.Topics.GetTopicRoleAsync(c1, topic2.Id) == TopicRole.None && await Denied(() => e.Topics.UpdateAsync(c1, topic2.Id, "x", "y")) && await e.Topics.ResolveModuleEditOwnerAsync(c1, mod2.Id) == c1,
            "K Co-Author von Thema A kann Thema B und dessen Module nicht bearbeiten");
        await e.Topics.AddModuleAsync(owner, topic2.Id, mod.Id);   // dasselbe Modul zusätzlich in Thema B (kein Co-Author-Recht dort)
        check(await e.Topics.ResolveModuleEditOwnerAsync(c1, mod.Id) == c1, "R Modul, das auch in einem Thema ohne Bearbeitungsrecht liegt, wird nicht gemeinsam bearbeitet (keine Auswirkung auf andere Themen)");
        await e.Topics.RemoveModuleAsync(owner, topic2.Id, mod.Id);
        check(await e.Topics.ResolveModuleEditOwnerAsync(c1, mod.Id) == owner, "R Nach Entfernen aus dem anderen Thema ist die gemeinsame Bearbeitung wieder möglich");

        // Grenzen der Co-Autoren: Veröffentlichen, Freigeben, Archivieren, Duplizieren, Module hinzufügen, JSON
        check(await Denied(() => e.Topics.PublishAsync(c1, topic.Id)) && await Denied(() => e.Topics.GrantShareAsync(c1, topic.Id, outsider))
              && await Denied(() => e.Topics.SetArchivedAsync(c1, topic.Id, true)) && await Denied(() => e.Topics.DuplicateAsync(c1, topic.Id))
              && await Denied(() => e.Topics.AddModuleAsync(c1, topic.Id, mod.Id)) && await Denied(() => e.Topics.UpdateFromDefinitionAsync(c1, topic.Id, "{}")),
            "S Co-Author darf nicht veröffentlichen, freigeben, archivieren, duplizieren, Module aus der Bibliothek hinzufügen oder per JSON ersetzen");
        check(await Denied(() => e.Topics.SubmitGlobalAsync(c1, topic.Id, 1)), "S Co-Author reicht nichts global ein");
        check(await Denied(() => e.Learners.AssignTopicAsync(c1, outsider, topic.Id, 1, mandatory: false)), "L Co-Author kann keine Themenzuweisung vergeben (keine Admin-/Themen-Admin-Rolle)");
        check((await e.Users.GetAsync(c1))!.Roles.SequenceEqual(new[] { Roles.User }), "L Co-Author erhält keine globale Rolle");

        // F, G, H: Co-Authoren verwalten
        check(await Denied(() => e.Topics.AddCoAuthorAsync(c1, topic.Id, c1)), "F Ein Co-Author kann sich nicht selbst Berechtigungen erteilen");
        var c2Added = await e.Topics.AddCoAuthorAsync(c1, topic.Id, c2);
        check(c2Added.UserId == c2 && c2Added.GrantedByName == "Carl Co", "F Co-Author fügt einen weiteren Co-Author hinzu (Vergabe protokolliert)");
        var d2 = await e.Topics.GetDetailAsync(c2, topic.Id);
        var upd2 = await Try(() => e.Topics.UpdateAsync(c2, topic.Id, "Von Cora", "", d2.Topic.DraftRevision));
        check(upd2 is not null && d2.IsCoAuthor, "G Der zweite Co-Author kann ebenfalls bearbeiten");
        check(await Denied(() => e.Topics.RemoveCoAuthorAsync(c1, topic.Id, owner)), "J Co-Author kann den Eigentümer nicht entfernen");
        check(await Denied(() => e.Topics.AddCoAuthorAsync(c1, topic.Id, owner)), "J Der Eigentümer kann nicht (erneut) als Co-Author eingetragen werden");
        check((await e.Topics.GetCollaborationAsync(c1, topic.Id)).OwnerUserId == owner, "J Eigentümer bleibt unverändert");
        check(await Denied(() => e.Topics.AddCoAuthorAsync(owner, topic.Id, ext)), "Q Eingeschränkte Lernprofile können keine Co-Autoren werden");
        check(await Denied(() => e.Topics.AddCoAuthorAsync(owner, topic.Id, Guid.NewGuid().ToString())), "Co-Autoren müssen existierende Benutzer sein");
        await e.Raw.ExecuteAsync("INSERT INTO topic_coauthors (topic_id, user_id, granted_by, created_at) VALUES (@t,@u,@g,@n)", new (string, object?)[] { ("t", topic.Id), ("u", ext), ("g", owner), ("n", DateTime.UtcNow) });
        check(await e.Topics.GetTopicRoleAsync(ext, topic.Id) == TopicRole.None && await Denied(() => e.Topics.GetDetailAsync(ext, topic.Id)) && (await e.Topics.ListOverviewAsync(ext)).CoAuthored.Count == 0,
            "Q Auch ein (manipulierter) Eintrag verleiht einem eingeschränkten Lernprofil keine Autorenrechte");
        await e.Raw.ExecuteAsync("DELETE FROM topic_coauthors WHERE user_id=@u", new (string, object?)[] { ("u", ext) });
        var dup = false; try { await e.Raw.ExecuteAsync("INSERT INTO topic_coauthors (topic_id, user_id, granted_by, created_at) VALUES (@t,@u,@g,@n)", new (string, object?)[] { ("t", topic.Id), ("u", c1), ("g", owner), ("n", DateTime.UtcNow) }); } catch (Exception) { dup = true; }
        check(dup, "Datenmodell: ein Benutzer ist je Thema nur einmal eingetragen (Primärschlüssel)");

        // M: veröffentlichte Version bleibt unverändert
        var v1 = await e.Topics.PublishAsync(owner, topic.Id);
        var frozenBefore = (await e.Topics.GetDetailAsync(owner, topic.Id, 1)).Modules.Select(m => m.ModuleId + "|" + m.Name).ToList();
        await e.Repo.UpdateCardAsync(card.Id, new CardJson { Module = "Co-Modul", Category = "K", Term = "T1", Question = "Frage?", Definition = "Nach Veröffentlichung geändert" }, await e.Topics.ResolveModuleEditOwnerAsync(c1, mod.Id), (await e.Repo.GetCardAsync(card.Id, owner))!.UpdatedAt);
        await e.Topics.UpdateAsync(c1, topic.Id, "Neuer Entwurfsname", "", (await e.Topics.GetDetailAsync(c1, topic.Id)).Topic.DraftRevision);
        var frozenAfter = (await e.Topics.GetDetailAsync(c1, topic.Id, 1)).Modules.Select(m => m.ModuleId + "|" + m.Name).ToList();
        var v1Detail = await e.Topics.GetDetailAsync(c1, topic.Id, 1);
        check(v1.VersionNumber == 1 && v1Detail.Version == 1 && frozenBefore.SequenceEqual(frozenAfter) && v1Detail.Topic.Name == "Gemeinsames Thema v2" == false, "M Veröffentlichte Version 1 bleibt trotz Änderungen am Entwurf unverändert");
        var frozenModuleId = v1Detail.Modules[0].ModuleId;
        check(await Denied(async () => { var fm = await e.Topics.ResolveModuleEditOwnerAsync(c1, frozenModuleId); if (await e.Repo.GetModuleAsync(frozenModuleId, fm) is null) throw new KeyNotFoundException(); }),
            "M Eingefrorene Themenmodule (veröffentlichte Version) sind auch für Co-Autoren nicht bearbeitbar");

        // H, I, S: Entzug
        check(await e.Topics.RemoveCoAuthorAsync(c1, topic.Id, c2), "H Co-Author entfernt einen anderen Co-Author");
        check(await e.Topics.GetTopicRoleAsync(c2, topic.Id) == TopicRole.None && await Denied(() => e.Topics.GetDetailAsync(c2, topic.Id)) && await Denied(() => e.Topics.UpdateAsync(c2, topic.Id, "x", "y")), "I Entfernter Benutzer verliert sofort den Zugriff");
        check(await e.Topics.ResolveModuleEditOwnerAsync(c2, mod.Id) == c2 && await Denied(() => e.Topics.AddCoAuthorAsync(c2, topic.Id, outsider)), "S Entzug verhindert weitere Schreibzugriffe (Module, Co-Autoren)");
        check((await e.Repo.GetCardAsync(card.Id, owner)) is not null && (await e.Topics.GetDetailAsync(owner, topic.Id)).Modules.Count == 1, "Entzug löscht keine Lerninhalte");
        check(await e.Topics.LeaveCoAuthorAsync(c1, topic.Id) && await e.Topics.GetTopicRoleAsync(c1, topic.Id) == TopicRole.None, "Eigene Co-Author-Berechtigung beenden");
        check(await Denied(() => e.Topics.LeaveCoAuthorAsync(owner, topic.Id)), "Der Eigentümer kann nicht austreten");

        // O: bestehende Themen ohne Co-Autoren unverändert
        var plain = await e.Topics.CreateAsync(owner, "Ohne Co-Autoren", "");
        check((await e.Topics.GetCollaborationAsync(owner, plain.Id)).CoAuthors.Count == 0 && (await e.Topics.ListOverviewAsync(owner)).Own.Count >= 3 && (await e.Topics.ListOverviewAsync(outsider)).CoAuthored.Count == 0,
            "O Bestehende Themen funktionieren ohne Co-Autoren unverändert");
        check(await Denied(() => e.Topics.GetCollaborationAsync(outsider, plain.Id)) && await Denied(() => e.Topics.AddCoAuthorAsync(outsider, plain.Id, c1)), "K Fremde können weder Zusammenarbeit lesen noch Co-Autoren hinzufügen");
        var users = await e.Topics.SearchCoAuthorCandidatesAsync(owner, plain.Id, "carl@");
        check(users.Any(u => u.Id == c1) && !(await e.Topics.SearchCoAuthorCandidatesAsync(owner, plain.Id, "eva@")).Any(), "Kandidatensuche: eingeschränkte Lernprofile werden nicht angeboten");
        check(await Denied(() => e.Topics.SearchCoAuthorCandidatesAsync(outsider, plain.Id, "carl@")), "Kandidatensuche nur für Eigentümer und Co-Autoren des Themas");
        var audit = await e.Raw.QueryAsync("SELECT action FROM audit_log WHERE action LIKE 'topic.coauthor.%'");
        check(audit.Select(r => r["action"]).Distinct().Count() >= 3, "Hinzufügen, Entfernen und Austreten werden protokolliert");
    }

    // ─── REST (laufender Server) ─────────────────────────────────────────────

    private static StringContent Body(object? o) => new(JsonSerializer.Serialize(o ?? new { }), Encoding.UTF8, "application/json");
    private static async Task<JsonNode?> J(HttpResponseMessage r) { var t = await r.Content.ReadAsStringAsync(); try { return t.Length == 0 ? null : JsonNode.Parse(t); } catch (JsonException) { return null; } }
    private static string S(JsonNode? n, string path) { var cur = n; foreach (var p in path.Split('.')) cur = cur is JsonArray a && int.TryParse(p, out var i) ? (i < a.Count ? a[i] : null) : cur?[p]; return cur?.ToString() ?? ""; }

    public static async Task RunHttpAsync(Func<string?, Task<HttpClient>> login, Func<HttpClient> anonymous, Action<bool, string> check)
    {
        Console.WriteLine("\nCo-Author-Modus (ADR 0052), REST");
        var o = await login("ca-owner"); var c = await login("ca-co"); var x = await login("ca-outsider");
        var cId = S(await J(await c.GetAsync("/api/me")), "id"); var xId = S(await J(await x.GetAsync("/api/me")), "id");
        var t = S(await J(await o.PostAsync("/api/topics/", Body(new { name = "CA REST", description = "" }))), "id");
        check(t.Length > 0, "A REST: Eigentümer erstellt ein Thema");
        check((await c.GetAsync($"/api/topics/{t}")).StatusCode == HttpStatusCode.NotFound && (await c.GetAsync($"/api/topics/{t}/coauthors")).StatusCode == HttpStatusCode.NotFound, "P Ohne Berechtigung: Thema und Zusammenarbeit sind für Fremde „nicht gefunden“");
        check((await c.PostAsync($"/api/topics/{t}/coauthors", Body(new { user_id = cId }))).StatusCode == HttpStatusCode.NotFound, "P Ein Fremder kann sich nicht selbst als Co-Author eintragen");
        var add = await o.PostAsync($"/api/topics/{t}/coauthors", Body(new { user_id = cId }));
        check(add.StatusCode == HttpStatusCode.Created, "B REST: Eigentümer fügt einen Co-Author hinzu");
        var collab = await J(await c.GetAsync($"/api/topics/{t}/coauthors"));
        check(S(collab, "role") == "co_author" && S(collab, "co_authors.0.user_id") == cId, "C REST: Co-Author sieht die Zusammenarbeit");
        var detail = await J(await c.GetAsync($"/api/topics/{t}"));
        check(S(detail, "is_co_author") == "true" && S(detail, "is_owner") == "false", "C REST: Thema für den Co-Author lesbar, als Co-Author gekennzeichnet");
        check((await c.PutAsync($"/api/topics/{t}", Body(new { name = "CA REST 2", description = "x" }))).IsSuccessStatusCode, "D REST: Co-Author bearbeitet den Entwurf");
        var rev = S(await J(await c.GetAsync($"/api/topics/{t}")), "topic.draft_revision");
        var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/topics/{t}") { Content = Body(new { name = "Alt", description = "" }) };
        stale.Headers.Add("X-Topic-Draft-Revision", "1");
        var staleRes = await o.SendAsync(stale);
        check(staleRes.StatusCode == HttpStatusCode.Conflict && S(await J(await o.GetAsync($"/api/topics/{t}")), "topic.name") == "CA REST 2", "N REST: veraltete Entwurfsrevision → 409, Änderung wird nicht gespeichert");
        check((await c.PostAsync($"/api/topics/{t}/versions", Body(new { }))).StatusCode == HttpStatusCode.NotFound, "S REST: Co-Author kann nicht veröffentlichen");
        check((await c.PostAsync($"/api/topics/{t}/shares", Body(new { user_id = xId }))).StatusCode == HttpStatusCode.NotFound && (await c.PostAsync($"/api/topics/{t}/archive", Body(new { archived = true }))).StatusCode == HttpStatusCode.NotFound,
            "S REST: Co-Author kann weder freigeben noch archivieren");
        check((await c.GetAsync("/api/admin/users")).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, "L REST: Co-Author hat keine Administratorrechte");
        var add2 = await c.PostAsync($"/api/topics/{t}/coauthors", Body(new { user_id = xId }));
        check(add2.StatusCode == HttpStatusCode.Created, "F REST: Co-Author fügt einen weiteren Co-Author hinzu");
        check((await x.PutAsync($"/api/topics/{t}", Body(new { name = "CA REST 3", description = "" }))).IsSuccessStatusCode, "G REST: zweiter Co-Author bearbeitet ebenfalls");
        var ownerId = S(await J(await o.GetAsync("/api/me")), "id");
        check((await c.DeleteAsync($"/api/topics/{t}/coauthors/{ownerId}")).StatusCode == HttpStatusCode.BadRequest, "J REST: der Eigentümer kann nicht entfernt werden");
        // Karten-REST bleibt eigentümergebunden (gemeinsame Kartenbearbeitung nur in der Oberfläche; MCP teilt diese Routen, ADR 0007)
        var mod = S(await J(await o.PostAsync("/api/modules/", Body(new { name = "CA-Mod" }))), "id");
        var cardRes = await o.PostAsync("/api/cards/", Body(new { module = "CA-Mod", category = "K", term = "T", question = "Q?", definition = "D" }));
        var cardId = S(await J(cardRes), "id");
        await o.PostAsync($"/api/topics/{t}/modules", Body(new { module_id = mod }));
        check((await c.PutAsync($"/api/cards/{cardId}", Body(new { module = "CA-Mod", category = "K", term = "T", question = "Q?", definition = "Hack" }))).StatusCode == HttpStatusCode.NotFound
              && (await c.GetAsync($"/api/modules/{mod}/export")).StatusCode == HttpStatusCode.NotFound, "P REST: Karten- und Modulrouten bleiben eigentümergebunden (kein Umgehen über die API)");
        check((await c.PostAsync($"/api/topics/{t}/modules", Body(new { module_id = mod }))).StatusCode == HttpStatusCode.NotFound, "P REST: Co-Author kann keine Module aus der Bibliothek des Eigentümers hinzufügen");
        // Entzug
        check((await o.DeleteAsync($"/api/topics/{t}/coauthors/{xId}")).StatusCode == HttpStatusCode.NoContent, "H REST: Eigentümer entfernt einen Co-Author");
        check((await x.GetAsync($"/api/topics/{t}")).StatusCode == HttpStatusCode.NotFound && (await x.PutAsync($"/api/topics/{t}", Body(new { name = "Nach Entzug", description = "" }))).StatusCode == HttpStatusCode.NotFound, "I/S REST: entfernter Co-Author verliert sofort jeden Zugriff");
        check((await c.PostAsync($"/api/topics/{t}/coauthors/leave", Body(new { }))).StatusCode == HttpStatusCode.NoContent && (await c.GetAsync($"/api/topics/{t}")).StatusCode == HttpStatusCode.NotFound, "REST: eigene Mitarbeit beenden");
        // MCP-Token: keine Co-Author-Verwaltung (TokenMayReadTopics) — Dienstschlüssel ohne Benutzer ebenso nicht
        var anon = anonymous();
        check((await anon.GetAsync($"/api/topics/{t}/coauthors")).StatusCode == HttpStatusCode.Unauthorized, "P REST: ohne Anmeldung keine Zusammenarbeit lesbar");

        // MCP-Token (tatsächlich erzeugt): jede Co-Author-Route ist gesperrt — unabhängig von der Themenrolle (Eigentümer und Co-Author)
        await o.PostAsync($"/api/topics/{t}/coauthors", Body(new { user_id = cId }));
        foreach (var (who, role, client) in new[] { ("Eigentümer", "Owner", o), ("Co-Author", "CoAuthor", c) })
        {
            var tok = S(await J(await client.PostAsync("/api/mcp/links", Body(new { name = "CA-Token-" + role, days = 1 }))), "token");
            var tc = new HttpClient { BaseAddress = client.BaseAddress }; tc.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tok);
            check(tok.StartsWith("lcm_") && (await tc.GetAsync("/api/mcp/whoami")).IsSuccessStatusCode, $"MCP {who}: gültiges Token vorhanden");
            var codes = new[]
            {
                (await tc.GetAsync($"/api/topics/{t}/coauthors")).StatusCode, (await tc.GetAsync($"/api/topics/{t}/coauthors/candidates?q=abc")).StatusCode,
                (await tc.PostAsync($"/api/topics/{t}/coauthors", Body(new { user_id = xId }))).StatusCode, (await tc.DeleteAsync($"/api/topics/{t}/coauthors/{xId}")).StatusCode,
                (await tc.PostAsync($"/api/topics/{t}/coauthors/leave", Body(new { }))).StatusCode,
            };
            check(codes.All(sc => sc == HttpStatusCode.Forbidden), $"MCP {who}: GET, Kandidatensuche, POST, DELETE und leave auf /coauthors → 403 ({string.Join(",", codes.Select(x2 => (int)x2))})");
        }
        check(S(await J(await o.GetAsync($"/api/topics/{t}/coauthors")), "co_authors.0.user_id") == cId, "MCP: die gesperrten Aufrufe haben nichts verändert (Co-Author weiterhin eingetragen)");
    }
}
