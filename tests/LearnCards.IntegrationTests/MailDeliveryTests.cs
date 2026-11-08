using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;
using LearnCards.Web.Services.Mail;

/// <summary>
/// ADR 0044/0045/0049 (Phase C), im Prozess auf SQLite mit simuliertem Graph: Mailtransport (Absender, Tenant, Empfängerregel, Fehlerklassen), Versandzustand getrennt vom
/// Einladungsstatus (Bestätigung, Doppelversand, parallele Versuche, mehrere Instanzen, Fehler, „unklar“, Neuausstellung während des Versands), Geheimnisfreiheit von Datenbank und Audit,
/// OTP-Extension (Endpunkt-Authentifizierung, Schema, Empfängerregel, Ratenbegrenzung, keine Codes in Ausgaben) und Verwaltung der Lernrechte (Rechteentzug, Deaktivierung, Bestandsschutz).
/// Es wird nie echt gesendet. PostgreSQL ist hiermit <b>nicht</b> belegt (siehe PostgresReadinessTests).
/// </summary>
public static class MailDeliveryTests
{
    private const string WorkforceMailTenant = "db88f9b6-c89a-41a3-a21d-b61458e234ff", ExternalTenant = "5fce2b3c-8e95-49b5-9cfd-fc223b7e8f63";

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public Func<HttpRequestMessage, int> DelayFor = _ => 0;
        public readonly List<(string Method, string Url, string Body)> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Calls) Calls.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            var d = DelayFor(request);
            if (d > 0) await Task.Delay(d, ct);
            return Respond(request);
        }
    }

    private sealed class FakeMailer : IInvitationMailer
    {
        public Func<InvitationMail, Task<MailSendResult>> Handler = _ => Task.FromResult(new MailSendResult(MailOutcome.Accepted, "accepted"));
        public int Calls; public string? LastLink;
        public async Task<MailSendResult> SendAsync(InvitationMail mail, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls); LastLink = mail.Link;
            return await Handler(mail);
        }
    }

    private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Token() => Json(new { access_token = "t", expires_in = 3600 });

    private static ExchangeMailOptions MailOpt(bool enabled = true, string tenant = WorkforceMailTenant, string sender = "learny@brors.link", params string[] allowed) => new()
    {
        Enabled = enabled, TenantId = tenant, ForbiddenTenantIds = new[] { ExternalTenant }, ClientId = "mail-app", ClientSecret = "mail-secret",
        SenderMailbox = sender, AllowedRecipients = allowed.Length == 0 ? new[] { "*" } : allowed,
    };

    public static async Task RunAsync(Action<bool, string> check, string tmp)
    {
        await TransportAsync(check);
        await TemplatesAsync(check);
        await DeliveryAsync(check, Path.Combine(tmp, "mail-delivery"));
        await OtpAsync(check, Path.Combine(tmp, "mail-otp"));
        await AdministrationAsync(check, Path.Combine(tmp, "mail-admin"));
        await CertificateAsync(check, Path.Combine(tmp, "mail-cert"));
        await RecipientsAsync(check, Path.Combine(tmp, "mail-recipients"));
        await B2cRestrictionsAsync(check, Path.Combine(tmp, "mail-b2c"));
        await FavoritesAsync(check, Path.Combine(tmp, "mail-fav"));
        await LandingAsync(check, Path.Combine(tmp, "mail-landing"));
        await UiAsync(check, Path.Combine(tmp, "mail-ui"));
    }

    // ─── Mailtransport (Exchange Online / Graph) ───────────────────────

    private static async Task TransportAsync(Action<bool, string> check)
    {
        Console.WriteLine("Mailtransport (simulierter Graph)");
        var graph = new StubHandler();
        int status = 202; TimeSpan? retry = null; bool throwTimeout = false;
        graph.Respond = r =>
        {
            if (r.RequestUri!.Host == "login.microsoftonline.com") return Token();
            if (throwTimeout) throw new TaskCanceledException();
            var res = new HttpResponseMessage((HttpStatusCode)status);
            if (retry is { } ra) res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(ra);
            return res;
        };
        ExchangeOnlineMailService Svc(ExchangeMailOptions o) => new(new HttpClient(graph), o);
        var mail = new InvitationMail("neu@kunde.test", "https://learny.test/invite#abc", DateTime.UtcNow.AddDays(1), "Erika");

        var ok = await Svc(MailOpt()).SendAsync(mail);
        var send = graph.Calls.Single(c => c.Url.Contains("/sendMail"));
        using var body = JsonDocument.Parse(send.Body);
        var msg = body.RootElement.GetProperty("message");
        var html = msg.GetProperty("body").GetProperty("content").GetString()!;
        check(ok.Outcome == MailOutcome.Accepted && ok.Code == "accepted", "Mail: HTTP 202 von Graph → angenommen (submitted), nicht „zugestellt“");
        check(send.Url == "https://graph.microsoft.com/v1.0/users/learny%40brors.link/sendMail", "Mail: Absender ausschließlich learny@brors.link (Graph-Pfad des Postfachs)");
        check(graph.Calls[0].Url.Contains(WorkforceMailTenant) && !graph.Calls[0].Url.Contains(ExternalTenant), "Mail: Token der Mail-App stammt vom Workforce-Tenant, nie vom External-ID-Tenant");
        check(msg.GetProperty("subject").GetString() == "Einladung zu Learny" && msg.GetProperty("body").GetProperty("contentType").GetString() == "HTML"
              && msg.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString() == "neu@kunde.test" && body.RootElement.GetProperty("saveToSentItems").GetBoolean() == false,
            "Mail: Betreff, Empfänger, ein HTML-Body (Graph kennt je Nachricht nur einen Body-Typ), nicht in „Gesendete Elemente“");
        check(html.Contains("https://learny.test/invite#abc") && !html.Contains("<img", StringComparison.OrdinalIgnoreCase) && !html.Contains("<script", StringComparison.OrdinalIgnoreCase)
              && !html.Contains("http://") && !html.Contains("<link", StringComparison.OrdinalIgnoreCase), "Mail: persönlicher Link, keine Bilder, Skripte, Tracking-Pixel oder externen Ressourcen");

        graph.Calls.Clear(); status = 403;
        var denied = await Svc(MailOpt(sender: "fremd@brors.link")).SendAsync(mail);
        check(denied.Outcome == MailOutcome.Rejected && denied.Code == "permission_denied" && graph.Calls.Single(c => c.Url.Contains("/sendMail")).Url.Contains("fremd%40brors.link"),
            "Mail: Postfach außerhalb des Exchange-RBAC-Scopes → 403 permission_denied (unberechtigter Absender wird nicht umgangen)");
        status = 404; check((await Svc(MailOpt()).SendAsync(mail)).Code == "mailbox_not_found", "Mail: unbekanntes Postfach → mailbox_not_found");
        status = 429; retry = TimeSpan.FromMinutes(5);
        var thr = await Svc(MailOpt()).SendAsync(mail);
        check(thr.Outcome == MailOutcome.Rejected && thr.Code == "throttled" && thr.RetryAfter == TimeSpan.FromMinutes(5), "Mail: 429 → eindeutig nicht gesendet, Wartezeit übernommen");
        status = 503; retry = null;
        check((await Svc(MailOpt()).SendAsync(mail)) is { Outcome: MailOutcome.Ambiguous, Code: "server_503" }, "Mail: 5xx → Ergebnis unklar (nicht „fehlgeschlagen“)");
        status = 202; throwTimeout = true;
        check((await Svc(MailOpt()).SendAsync(mail)) is { Outcome: MailOutcome.Ambiguous, Code: "timeout" }, "Mail: Zeitüberschreitung nach dem Senden → unklar (die Mail kann angenommen sein)");
        throwTimeout = false;

        graph.Calls.Clear();
        check((await Svc(MailOpt(enabled: false)).SendAsync(mail)).Code == "mail_disabled" && graph.Calls.Count == 0, "Mail: ohne Aktivierung wird nichts gesendet und nichts abgerufen (fail-closed)");
        check((await Svc(MailOpt(tenant: ExternalTenant)).SendAsync(mail)).Code == "wrong_tenant" && graph.Calls.Count == 0, "Mail: Mail-App im External-ID-Tenant wird abgelehnt (Verwechslungsschutz)");
        check((await Svc(MailOpt(sender: "kein-postfach")).SendAsync(mail)).Code == "mail_not_configured", "Mail: ungültiges Absenderpostfach → nicht konfiguriert");
        check((await Svc(MailOpt(allowed: new[] { "@intern.test" })).SendAsync(mail)).Code == "recipient_not_allowed" && graph.Calls.Count == 0, "Mail: Empfänger außerhalb von MAIL_ALLOWED_RECIPIENTS wird abgelehnt");
        check((await Svc(MailOpt(allowed: new[] { "@kunde.test" })).SendAsync(mail)).Outcome == MailOutcome.Accepted, "Mail: @domain-Eintrag erlaubt die Domäne");
        check((await Svc(MailOpt()).SendAsync(mail with { Link = "https://x\r\nBcc: a@b.c" })).Code == "invalid_message", "Mail: Zeilenumbrüche im Link (Header-Injection) werden abgelehnt");

        // Konfiguration (fail-closed)
        static InvalidOperationException? Throws(Func<string, string> env, bool dev = false) { try { ExchangeMailSettings.Load(env, ExternalTenant, dev); return null; } catch (InvalidOperationException e) { return e; } }
        Func<string, string> Env(params (string, string)[] kv) { var d = kv.ToDictionary(x => x.Item1, x => x.Item2); return k => d.GetValueOrDefault(k, ""); }
        var good = new[] { ("MAIL_ENABLED", "1"), ("MAIL_TENANT_ID", WorkforceMailTenant), ("MAIL_CLIENT_ID", Guid.NewGuid().ToString()), ("MAIL_SENDER", "learny@brors.link"), ("MAIL_CLIENT_SECRET", "s") };
        check(!ExchangeMailSettings.Load(Env(), ExternalTenant, false).Enabled, "Mail-Konfiguration: Standard ist aus");
        check(Throws(Env(good)) is not null, "Mail-Konfiguration: Client-Secret außerhalb von Development nicht zulässig (Zertifikat verlangt)");
        check(Throws(Env(good), true) is null, "Mail-Konfiguration: Client-Secret in Development zulässig");
        check(Throws(Env(good.Select(x => x.Item1 == "MAIL_TENANT_ID" ? (x.Item1, ExternalTenant) : x).ToArray()), true) is { } e1 && !e1.Message.Contains("mail-secret"), "Mail-Konfiguration: Tenant der Mail-App = External-ID-Tenant → Startabbruch (ohne Geheimnis im Text)");
        check(Throws(Env(good.Where(x => x.Item1 != "MAIL_SENDER").ToArray()), true) is not null, "Mail-Konfiguration: fehlender Absender → Startabbruch");
        check(Throws(Env(good.Append(("MAIL_ALLOWED_RECIPIENTS", "kaputt")).ToArray()), true) is not null, "Mail-Konfiguration: ungültiger Empfängereintrag → Startabbruch");
        var noCert = good.Where(x => x.Item1 != "MAIL_CLIENT_SECRET").ToArray();
        check(Throws(Env(noCert)) is null && ExchangeMailSettings.Load(Env(noCert), ExternalTenant, false).UsesPlatformCertificate, "Mail-Konfiguration: ohne Datei und Secret wird das Zertifikat auf der Plattform erzeugt (auch außerhalb von Development)");
        check(Throws(Env(noCert.Append(("MAIL_CLIENT_CERT_PATH", "x.pfx")).Append(("MAIL_CLIENT_SECRET", "s")).ToArray()), true) is not null, "Mail-Konfiguration: Datei und Secret gleichzeitig → Startabbruch");
        var mailOn = ExchangeMailSettings.Load(Env(good), ExternalTenant, true);
        var extOn = ExternalIdSettings.Load(k => new Dictionary<string, string> { ["EXTID_ENABLED"] = "1", ["EXTID_AUTHORITY"] = "https://x.ciamlogin.com/t/v2.0", ["EXTID_TENANT_ID"] = ExternalTenant, ["EXTID_CLIENT_ID"] = "c", ["EXTID_CLIENT_SECRET"] = "s" }.GetValueOrDefault(k, ""), "", "", false);
        check(Throws2(() => OtpExtensionSettings.Load(k => k == "EXTID_OTP_ENABLED" ? "1" : "", new ExternalIdSettings(), new ExchangeMailSettings())), "OTP-Konfiguration: ohne External ID → Startabbruch");
        check(Throws2(() => OtpExtensionSettings.Load(k => k == "EXTID_OTP_ENABLED" ? "1" : "", extOn, new ExchangeMailSettings())), "OTP-Konfiguration: ohne Mailversand → Startabbruch");
        check(Throws2(() => OtpExtensionSettings.Load(k => k == "EXTID_OTP_ENABLED" ? "1" : "", extOn, mailOn)), "OTP-Konfiguration: ohne Audience → Startabbruch");
        check(OtpExtensionSettings.Load(k => k switch { "EXTID_OTP_ENABLED" => "1", "EXTID_OTP_AUDIENCES" => "api-app", _ => "" }, extOn, mailOn).Enabled && !OtpExtensionSettings.Load(k => "", extOn, mailOn).Enabled, "OTP-Konfiguration: Standard aus, aktiv nur mit Audience, External ID und Mailversand");
        static bool Throws2(Action a) { try { a(); return false; } catch (InvalidOperationException) { return true; } }
        await Task.CompletedTask;
    }

    private static async Task TemplatesAsync(Action<bool, string> check)
    {
        Console.WriteLine("Mailvorlagen");
        var inv = InvitationMailTemplate.BuildHtml(new InvitationMail("a@b.test", "https://learny.test/invite#x\"><script>alert(1)</script>", DateTime.UtcNow.AddDays(1), "<b>Name</b>"));
        check(!inv.Contains("<script>") && !inv.Contains("<b>Name</b>") && inv.Contains("&lt;b&gt;Name&lt;/b&gt;"), "Einladungsvorlage: Link und Name werden kodiert (keine Markup-Einschleusung)");
        var train = new TrainingMail("a@b.test", "Datenschutz <i>2026</i>", true, DateTime.UtcNow.AddDays(10), "https://learny.test", false, "Max");
        var h1 = TrainingMailTemplate.BuildHtml(train); var h2 = TrainingMailTemplate.BuildHtml(train with { Reminder = true });
        check(TrainingMailTemplate.Subject(train) == "Neue Schulung in Learny" && TrainingMailTemplate.Subject(train with { Reminder = true }) == "Erinnerung: Pflichtschulung in Learny"
              && h1.Contains("Pflichtschulung zugewiesen") && h2.Contains("noch nicht abgeschlossen") && !h1.Contains("<i>") && !h1.Contains("<img") && !h1.Contains("invite"),
            "Schulungsvorlagen: zugewiesene Schulung und Pflichtschulungserinnerung, kodiert, ohne Bilder und ohne Einladungslink");
        var otp = OtpMailTemplate.BuildHtml("48151623");
        check(otp.Split("48151623").Length == 2 && !otp.Contains("href=") && !otp.Contains("<img") && OtpMailTemplate.IsValidCode("48151623") && !OtpMailTemplate.IsValidCode("12<b>"), "OTP-Vorlage: Code genau einmal, keine Links oder Bilder, nur alphanumerische Codes");
        await Task.CompletedTask;
    }

    // ─── Versandzustand ────────────────────────────────────────────────

    private static async Task DeliveryAsync(Action<bool, string> check, string dir)
    {
        Console.WriteLine("Einladungsversand: Zustand getrennt vom Einladungsstatus");
        var env = await InvitationUnitTests.Env.CreateAsync(dir);
        var mailer = new FakeMailer();
        var d = new InvitationDeliveryService(env.Db, env.Users, env.Invites, mailer, env.Events, () => env.Now);
        var mod = await env.AddGlobalModuleAsync("Modul A");
        async Task<string> Status(string id) => (await env.Raw.QueryAsync("SELECT status FROM invitation_deliveries WHERE invitation_id=@i", new (string, object?)[] { ("i", id) }))[0]["status"]!;
        async Task<string> InvStatus(string id) => (await env.Raw.QueryAsync("SELECT status FROM invitations WHERE invitation_id=@i", new (string, object?)[] { ("i", id) }))[0]["status"]!;
        async Task<List<string>> EventTypes(string id) => (await env.Raw.QueryAsync("SELECT event_type FROM invitation_events WHERE invitation_id=@i ORDER BY at", new (string, object?)[] { ("i", id) })).Select(r => r["event_type"]!).ToList();

        var c1 = await env.Invites.CreateAsync(env.Admin, "eins@kunde.test", LearnerProfiles.B2cLearner, new[] { mod });
        check(await Status(c1.InvitationId) == "pending" && mailer.Calls == 0, "Versand: das bloße Erstellen einer Einladung sendet nichts (Versandstatus pending)");
        check((await d.GetAsync(env.Admin, c1.InvitationId))?.Status == "pending" && mailer.Calls == 0, "Versand: Lesen des Zustands löst nie einen Versand aus");

        foreach (var (who, name) in new[] { (env.Auditor, "Auditor"), (env.TopicAdmin, "Themen-Admin") })
        {
            var denied = false; try { await d.SendAsync(who, c1.InvitationId, c1.Token, true); } catch (UnauthorizedAccessException) { denied = true; }
            check(denied && mailer.Calls == 0, $"Versand: {name} darf nicht senden (Rolle wird gegen die Datenbank geprüft)");
        }
        var anon = false; try { await d.SendAsync(null, c1.InvitationId, c1.Token, true); } catch (UnauthorizedAccessException) { anon = true; }
        check(anon, "Versand: ohne Akteur nicht möglich");

        check((await d.SendAsync(env.Admin, c1.InvitationId, c1.Token, confirmed: false)).Decision == SendDecision.NotConfirmed && mailer.Calls == 0, "Versand: ohne ausdrückliche Bestätigung wird nichts gesendet");
        check((await d.SendAsync(env.Admin, c1.InvitationId, "falscher-token-0123456789abcdefghij", true)).Decision == SendDecision.NotSendable && mailer.Calls == 0, "Versand: ein nicht aktueller oder falscher Token wird nicht versendet");

        var r1 = await d.SendAsync(env.Admin, c1.InvitationId, c1.Token, true);
        check(r1.Decision == SendDecision.Sent && mailer.Calls == 1 && mailer.LastLink == c1.Link && await Status(c1.InvitationId) == "submitted" && await InvStatus(c1.InvitationId) == "pending",
            "Versand: bestätigter Versand → submitted; der Einladungsstatus bleibt pending (getrennte Zustände)");
        var ev1 = await EventTypes(c1.InvitationId);
        check(ev1.Contains("invitation.send_requested") && ev1.Contains("invitation.submitted") && !ev1.Contains("invitation.send_failed"), "Versand: Ereignisse „angefordert“ und „angenommen“ (nicht „zugestellt“)");
        var again = await d.SendAsync(env.Admin, c1.InvitationId, c1.Token, true);
        check(again.Decision == SendDecision.RequiresRotation && mailer.Calls == 1, "Versand: bereits angenommen → kein zweiter Versand, nur nach ausdrücklicher Neuausstellung");

        // Geheimnisfreiheit
        var leak = new List<string>();
        foreach (var t in new[] { "invitations", "invitation_deliveries", "invitation_events", "invitation_meta", "invitation_module_grants", "invitation_topic_grants", "audit_log" })
            foreach (var row in await env.Raw.QueryAsync($"SELECT * FROM {t}"))
                foreach (var v in row.Values) if (v is not null && (v.Contains(c1.Token) || v.Contains(c1.Link))) leak.Add(t);
        check(leak.Count == 0, "Versand: weder Token noch Link stehen in Einladungs-, Versand-, Ereignis- oder Audit-Tabellen" + (leak.Count > 0 ? " (gefunden: " + string.Join(",", leak) + ")" : ""));
        var audit = (await env.Raw.QueryAsync("SELECT detail, target FROM audit_log WHERE action='invitation.send'")).Single();
        check(audit["detail"] == "status=submitted code=accepted" && audit["target"] == c1.InvitationId && !audit["detail"]!.Contains("@"), "Versand: Audit enthält nur Status und Code (keine Adresse, kein Link)");

        // Neuausstellung ist ausdrücklich und setzt den Versandzustand zurück
        var rot = await env.Invites.RotateAsync(env.Admin, c1.InvitationId);
        check(rot is not null && rot.Token != c1.Token && await Status(c1.InvitationId) == "pending" && (await d.SendAsync(env.Admin, c1.InvitationId, c1.Token, true)).Decision == SendDecision.NotSendable,
            "Neuausstellung: neuer Link, Versandzustand pending, der alte Link ist weder gültig noch versendbar");
        check((await d.SendAsync(env.Admin, c1.InvitationId, rot!.Token, true)).Decision == SendDecision.Sent && mailer.Calls == 2 && mailer.LastLink == rot.Link, "Neuausstellung: der neue Link kann nach Bestätigung versendet werden");

        // Eindeutiger Fehler: Einladung wird nicht verbraucht, Wiederholung erlaubt
        var c2 = await env.Invites.CreateAsync(env.Admin, "zwei@kunde.test", LearnerProfiles.B2bLearner);
        mailer.Handler = _ => Task.FromResult(new MailSendResult(MailOutcome.Rejected, "permission_denied"));
        var f = await d.SendAsync(env.Admin, c2.InvitationId, c2.Token, true);
        check(f.Decision == SendDecision.Failed && f.Code == "permission_denied" && await Status(c2.InvitationId) == "failed" && await InvStatus(c2.InvitationId) == "pending",
            "Versandfehler: eindeutig nicht gesendet → failed, die Einladung bleibt offen und nutzbar");
        check((await EventTypes(c2.InvitationId)).Contains("invitation.send_failed") && (await env.Raw.QueryAsync("SELECT category FROM invitation_events WHERE event_type='invitation.send_failed' AND invitation_id=@i", new (string, object?)[] { ("i", c2.InvitationId) }))[0]["category"] == "permission_denied",
            "Versandfehler: Ereignis mit kategorisiertem Fehlercode");
        check(await env.Invites.IsRedeemableAsync(c2.Token), "Versandfehler: der Link bleibt einlösbar (Versand und Einladung sind getrennt)");
        mailer.Handler = _ => Task.FromResult(new MailSendResult(MailOutcome.Accepted, "accepted"));
        check((await d.SendAsync(env.Admin, c2.InvitationId, c2.Token, true)).Decision == SendDecision.Sent && await Status(c2.InvitationId) == "submitted", "Versandfehler: nach einem eindeutigen Fehler ist eine kontrollierte Wiederholung möglich");

        // Unklares Ergebnis → unknown, keine stille Wiederholung
        var c3 = await env.Invites.CreateAsync(env.Admin, "drei@kunde.test", LearnerProfiles.B2cLearner);
        var callsBefore = mailer.Calls;
        mailer.Handler = _ => Task.FromResult(new MailSendResult(MailOutcome.Ambiguous, "timeout"));
        var u = await d.SendAsync(env.Admin, c3.InvitationId, c3.Token, true);
        check(u.Decision == SendDecision.Unknown && await Status(c3.InvitationId) == "unknown" && await InvStatus(c3.InvitationId) == "pending", "Zeitüberschreitung: Status unknown (Mail kann angenommen sein), Einladung bleibt offen");
        check((await EventTypes(c3.InvitationId)).Contains("invitation.send_failed") && (await env.Raw.QueryAsync("SELECT category FROM invitation_events WHERE event_type='invitation.send_failed' AND invitation_id=@i", new (string, object?)[] { ("i", c3.InvitationId) }))[0]["category"] == "unknown:timeout",
            "Zeitüberschreitung: Ereignis kennzeichnet „unklar“ (nicht „fehlgeschlagen“)");
        env.Now = env.Now.AddMinutes(5);
        var noAck = await d.SendAsync(env.Admin, c3.InvitationId, c3.Token, true);
        check(noAck.Decision == SendDecision.RequiresAcknowledgement && mailer.Calls == callsBefore + 1, "Zeitüberschreitung: keine automatische oder stille Wiederholung ohne ausdrückliche Zusatzbestätigung");
        mailer.Handler = _ => Task.FromResult(new MailSendResult(MailOutcome.Accepted, "accepted"));
        check((await d.SendAsync(env.Admin, c3.InvitationId, c3.Token, true, acknowledgeUnknown: true)).Decision == SendDecision.Sent && await Status(c3.InvitationId) == "submitted"
              && (await EventTypes(c3.InvitationId)).Contains("invitation.send_requested"), "Zeitüberschreitung: mit ausdrücklicher Zusatzbestätigung ist ein erneuter Versand möglich");

        // Mailer wirft → unklar
        var c4 = await env.Invites.CreateAsync(env.Admin, "vier@kunde.test", LearnerProfiles.B2cLearner);
        mailer.Handler = _ => throw new IOException("kaputt");
        check((await d.SendAsync(env.Admin, c4.InvitationId, c4.Token, true)).Decision == SendDecision.Unknown && await Status(c4.InvitationId) == "unknown", "Mailer-Ausnahme: wird als „unklar“ behandelt, nicht als nicht gesendet");

        // Drosselung: Wartezeit bleibt gesperrt
        var c5 = await env.Invites.CreateAsync(env.Admin, "fuenf@kunde.test", LearnerProfiles.B2cLearner);
        mailer.Handler = _ => Task.FromResult(new MailSendResult(MailOutcome.Rejected, "throttled", TimeSpan.FromMinutes(10)));
        await d.SendAsync(env.Admin, c5.InvitationId, c5.Token, true);
        var early = await d.SendAsync(env.Admin, c5.InvitationId, c5.Token, true);
        env.Now = env.Now.AddMinutes(11);
        mailer.Handler = _ => Task.FromResult(new MailSendResult(MailOutcome.Accepted, "accepted"));
        check(early.Decision == SendDecision.InProgress && (await d.SendAsync(env.Admin, c5.InvitationId, c5.Token, true)).Decision == SendDecision.Sent, "Drosselung (Retry-After): vorher kein Versand, danach möglich");

        // Parallele Anfragen: genau ein Versand
        var c6 = await env.Invites.CreateAsync(env.Admin, "sechs@kunde.test", LearnerProfiles.B2cLearner);
        var before = mailer.Calls;
        mailer.Handler = async _ => { await Task.Delay(150); return new MailSendResult(MailOutcome.Accepted, "accepted"); };
        var many = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => d.SendAsync(env.Admin, c6.InvitationId, c6.Token, true)));
        check(many.Count(x => x.Decision == SendDecision.Sent) == 1 && mailer.Calls == before + 1 && many.All(x => x.Decision is SendDecision.Sent or SendDecision.InProgress or SendDecision.RequiresRotation),
            "Doppelversand: acht parallele Anfragen → genau eine Mail");

        // Mehrinstanzbetrieb: zweite Dienstinstanz auf derselben Datenbank
        var c7 = await env.Invites.CreateAsync(env.Admin, "sieben@kunde.test", LearnerProfiles.B2cLearner);
        var ev2 = new InviteEvents(env.Db); var inv2 = InvitationService.Create(env.Db, env.Users, env.Cfg.Invitations, ev2, env.Topics); inv2.UseClock(() => env.Now);
        var mailer2 = new FakeMailer { Handler = async _ => { await Task.Delay(150); return new MailSendResult(MailOutcome.Accepted, "accepted"); } };
        var d2 = new InvitationDeliveryService(env.Db, env.Users, inv2, mailer2, ev2, () => env.Now);
        mailer.Handler = mailer2.Handler; var b2 = mailer.Calls;
        var both = await Task.WhenAll(d.SendAsync(env.Admin, c7.InvitationId, c7.Token, true), d2.SendAsync(env.Admin, c7.InvitationId, c7.Token, true));
        check(both.Count(x => x.Decision == SendDecision.Sent) == 1 && (mailer.Calls - b2) + mailer2.Calls == 1, "Mehrinstanzbetrieb: zwei Dienstinstanzen auf derselben Datenbank versenden zusammen genau eine Mail (Slot und Lease in der Datenbank)");

        // Neuausstellung während des Versands
        var c8 = await env.Invites.CreateAsync(env.Admin, "acht@kunde.test", LearnerProfiles.B2cLearner);
        mailer.Handler = async _ => { await env.Invites.RotateAsync(env.Admin, c8.InvitationId); return new MailSendResult(MailOutcome.Accepted, "accepted"); };
        var sup = await d.SendAsync(env.Admin, c8.InvitationId, c8.Token, true);
        check(sup.Decision == SendDecision.NotSendable && sup.Code == "superseded" && await Status(c8.InvitationId) == "pending", "Neuausstellung während des Versands: das Ergebnis wird nicht dem neuen Link zugeschrieben (Zustand bleibt pending)");

        // Ergebnis nicht speicherbar → bleibt „unknown“, kein stilles Wiederholen
        var c9 = await env.Invites.CreateAsync(env.Admin, "neun@kunde.test", LearnerProfiles.B2cLearner);
        mailer.Handler = _ => { env.Db.FailAt = 0; return Task.FromResult(new MailSendResult(MailOutcome.Accepted, "accepted")); };
        var rec = await d.SendAsync(env.Admin, c9.InvitationId, c9.Token, true);
        env.Now = env.Now.AddMinutes(3);
        mailer.Handler = _ => Task.FromResult(new MailSendResult(MailOutcome.Accepted, "accepted")); var cb = mailer.Calls;
        check(rec.Decision == SendDecision.Unknown && rec.Code == "record_failed" && await Status(c9.InvitationId) == "unknown"
              && (await d.SendAsync(env.Admin, c9.InvitationId, c9.Token, true)).Decision == SendDecision.RequiresAcknowledgement && mailer.Calls == cb,
            "Speicherfehler nach dem Versand: Zustand bleibt unknown und wird nicht still wiederholt");

        // Widerrufene/abgelaufene Einladung; zu viele Versuche
        var c10 = await env.Invites.CreateAsync(env.Admin, "zehn@kunde.test", LearnerProfiles.B2cLearner);
        await env.Invites.RevokeAsync(env.Admin, c10.InvitationId);
        check((await d.SendAsync(env.Admin, c10.InvitationId, c10.Token, true)).Decision == SendDecision.NotSendable, "Versand: eine widerrufene Einladung wird nicht versendet");
        var c11 = await env.Invites.CreateAsync(env.Admin, "elf@kunde.test", LearnerProfiles.B2cLearner);
        await env.Raw.ExecuteAsync("UPDATE invitations SET expires_at=@d WHERE invitation_id=@i", new (string, object?)[] { ("d", env.Now.AddHours(-1)), ("i", c11.InvitationId) });
        check((await d.SendAsync(env.Admin, c11.InvitationId, c11.Token, true)).Decision == SendDecision.NotSendable, "Versand: eine abgelaufene Einladung wird nicht versendet");
        var c12 = await env.Invites.CreateAsync(env.Admin, "zwoelf@kunde.test", LearnerProfiles.B2cLearner);
        mailer.Handler = _ => Task.FromResult(new MailSendResult(MailOutcome.Rejected, "network_x"));
        for (var i = 0; i < 5; i++) await d.SendAsync(env.Admin, c12.InvitationId, c12.Token, true);
        check((await d.SendAsync(env.Admin, c12.InvitationId, c12.Token, true)).Decision == SendDecision.TooManyAttempts, "Versand: nach fünf Fehlversuchen wird gesperrt (Neuausstellung setzt zurück)");

        // Die Einlösung bleibt vom Versand unabhängig und genau einmal möglich
        var c13 = await env.Invites.CreateAsync(env.Admin, "dreizehn@kunde.test", LearnerProfiles.B2cLearner, new[] { mod });
        var red = await env.Invites.RedeemAsync(c13.Token, InvitationUnitTests.Id("dreizehn@kunde.test"));
        var red2 = await env.Invites.RedeemAsync(c13.Token, InvitationUnitTests.Id("dreizehn@kunde.test"));
        check(red.Success && !red2.Success && (await d.SendAsync(env.Admin, c13.InvitationId, c13.Token, true)).Decision == SendDecision.NotSendable, "Einlösung: auch ohne Versand genau einmal möglich; nach der Einlösung nicht mehr versendbar");
    }

    // ─── OTP-Extension ─────────────────────────────────────────────────

    private static async Task OtpAsync(Action<bool, string> check, string dir)
    {
        Console.WriteLine("OnOtpSend-Extension (Entra → Learny → Exchange)");
        const string Auth = "https://t.ciamlogin.com/tid/v2.0", Iss = "https://tid.ciamlogin.com/tid/v2.0", Caller = "99045fe1-7639-4a75-9d4a-577b6ca3810f", Code = "48151623";
        var now = DateTime.UtcNow;
        using var rsa = RSA.Create(2048); using var other = RSA.Create(2048);
        var pub = rsa.ExportParameters(false);
        static string B64u(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        static string Jwt(object payload, RSA k, string alg = "RS256")
        {
            var h = B64u(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg, kid = "k1", typ = "JWT" })));
            var p = B64u(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
            return h + "." + p + "." + B64u(k.SignData(Encoding.ASCII.GetBytes(h + "." + p), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        long Ts(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();
        Dictionary<string, object> Claims() => new() { ["iss"] = Iss, ["aud"] = "api-app", ["azp"] = Caller, ["tid"] = "tid", ["ver"] = "2.0", ["exp"] = Ts(now.AddHours(1)), ["nbf"] = Ts(now.AddMinutes(-1)) };
        string Good() => "Bearer " + Jwt(Claims(), rsa);
        string Body(string email = "invitee@y.test", string code = Code, string type = "microsoft.graph.authenticationEvent.emailOtpSend") =>
            "{\"type\":\"" + type + "\",\"data\":{\"tenantId\":\"tid\",\"otpContext\":{\"identifier\":\"" + email + "\",\"oneTimeCode\":\"" + code + "\"}}}";

        var disc = new StubHandler
        {
            Respond = r => r.RequestUri!.ToString().EndsWith("/keys")
                ? Json(new { keys = new[] { new { kty = "RSA", kid = "k1", n = B64u(pub.Modulus!), e = B64u(pub.Exponent!) } } })
                : Json(new { issuer = Iss, jwks_uri = "https://t.ciamlogin.com/tid/discovery/v2.0/keys" }),
        };
        var env = await InvitationUnitTests.Env.CreateAsync(dir);
        await env.Invites.CreateAsync(env.Admin, "invitee@y.test", LearnerProfiles.B2cLearner);
        var rev = await env.Invites.CreateAsync(env.Admin, "widerrufen@y.test", LearnerProfiles.B2cLearner); await env.Invites.RevokeAsync(env.Admin, rev.InvitationId);
        var exp = await env.Invites.CreateAsync(env.Admin, "abgelaufen@y.test", LearnerProfiles.B2cLearner);
        await env.Raw.ExecuteAsync("UPDATE invitations SET expires_at=@d WHERE invitation_id=@i", new (string, object?)[] { ("d", DateTime.UtcNow.AddHours(-1)), ("i", exp.InvitationId) });
        // bekannter externer Benutzer (aktiv/gesperrt) und Workforce-Benutzer mit vergleichbarer Adresse
        var extInv = await env.Invites.CreateAsync(env.Admin, "extern@y.test", LearnerProfiles.B2bLearner);
        var extId = await env.Invites.RedeemAsync(extInv.Token, InvitationUnitTests.Id("extern@y.test"));
        var gesperrt = await env.Invites.CreateAsync(env.Admin, "gesperrt@y.test", LearnerProfiles.B2cLearner);
        var gesperrtRes = await env.Invites.RedeemAsync(gesperrt.Token, InvitationUnitTests.Id("gesperrt@y.test"));
        await env.Raw.ExecuteAsync("UPDATE users SET status='disabled' WHERE id=@u", new (string, object?)[] { ("u", gesperrtRes.UserId) });
        await env.AddWorkforceUserAsync("Mitarbeiter", "mitarbeiter@y.test");

        var graph = new StubHandler(); int graphStatus = 202;
        graph.Respond = r => r.RequestUri!.Host == "login.microsoftonline.com" ? Token() : new HttpResponseMessage((HttpStatusCode)graphStatus);
        var audit = new List<string>();
        OtpSendHandler NewHandler(int perRecipient = 3, int global = 60) => new(
            new ExtensionTokenValidator(new HttpClient(disc), new OtpExtensionOptions { Authority = Auth, Audiences = new[] { "api-app" }, ExpectedTenantId = "tid" }),
            new ExchangeOnlineMailService(new HttpClient(graph), MailOpt(allowed: new[] { "@nicht-relevant.test" })),   // die Allowlist der Einladungsmails darf den OTP-Versand nicht steuern
            new InvitationOtpRecipientPolicy(env.Invites),
            new OtpExtensionOptions { Authority = Auth, Audiences = new[] { "api-app" }, ExpectedTenantId = "tid", PerRecipientPer10Minutes = perRecipient, GlobalPerMinute = global, Audit = (e, r) => { lock (audit) audit.Add(e + "|" + r); return Task.CompletedTask; } });
        int Sends() => graph.Calls.Count(c => c.Url.Contains("/sendMail"));
        void Reset() { graph.Calls.Clear(); graphStatus = 202; }

        {
            Reset(); var h = NewHandler();
            var r = await h.HandleAsync(Good(), Body(), default);
            var send = graph.Calls.Single(c => c.Url.Contains("/sendMail"));
            check(r.Status == 200 && r.Json!.Contains("continueWithDefaultBehavior"), "OTP: gültiger Aufruf → 200 mit continueWithDefaultBehavior");
            check(send.Url == "https://graph.microsoft.com/v1.0/users/learny%40brors.link/sendMail" && graph.Calls[0].Url.Contains(WorkforceMailTenant), "OTP: Versand über learny@brors.link mit dem Token der Mail-App aus dem Workforce-Tenant");
            check(send.Body.Split(Code).Length == 2, "OTP: der von Entra erzeugte Code wird unverändert und genau einmal in die Mail übernommen");
            check((await h.HandleAsync(Good(), Body(), default)).Status == 200 && Sends() == 1, "OTP: identische Wiederholung nach Erfolg → keine zweite Mail");
        }
        {
            Reset(); var h = NewHandler();
            async Task Denied(string name, string? authz) { var r = await h.HandleAsync(authz, Body(), default); check(r.Status == 401 && r.Json is null && Sends() == 0, "OTP: 401 ohne Mail — " + name); }
            await Denied("kein Bearer-Token (kein Cookie ersetzt die Endpunkt-Authentifizierung)", null);
            await Denied("kein Bearer-Präfix", Jwt(Claims(), rsa));
            await Denied("falscher Issuer", "Bearer " + Jwt(new Dictionary<string, object>(Claims()) { ["iss"] = "https://evil.test/tid/v2.0" }, rsa));
            await Denied("falsche Audience", "Bearer " + Jwt(new Dictionary<string, object>(Claims()) { ["aud"] = "fremde-api" }, rsa));
            await Denied("fehlende App-Berechtigung (falsche aufrufende App)", "Bearer " + Jwt(new Dictionary<string, object>(Claims()) { ["azp"] = "00000000-0000-0000-0000-000000000000" }, rsa));
            await Denied("falscher Tenant", "Bearer " + Jwt(new Dictionary<string, object>(Claims()) { ["tid"] = "anderer" }, rsa));
            await Denied("abgelaufen", "Bearer " + Jwt(new Dictionary<string, object>(Claims()) { ["exp"] = Ts(now.AddHours(-1)) }, rsa));
            await Denied("falsche Signatur", "Bearer " + Jwt(Claims(), other));
            await Denied("HS256", "Bearer " + Jwt(Claims(), rsa, "HS256"));
            await Denied("Müll", "Bearer abc.def.ghi");
        }
        {
            Reset();
            async Task Bad(string name, string body, int expect = 400) { var r = await NewHandler().HandleAsync(Good(), body, default); check(r.Status == expect && Sends() == 0, $"OTP: {expect} — {name}"); }
            await Bad("falscher Ereignistyp", Body(type: "microsoft.graph.authenticationEvent.tokenIssuanceStart"));
            await Bad("kein JSON", "nicht json");
            await Bad("otpContext fehlt", "{\"type\":\"microsoft.graph.authenticationEvent.emailOtpSend\",\"data\":{}}");
            await Bad("ungültige Adresse", Body(email: "kein-email"));
            await Bad("Code mit Markup", Body(code: "12<b>34"));
            await Bad("Body über 8 KB (begrenzte Request-Größe)", Body() + new string(' ', 9000), 413);
        }
        {
            async Task<(int, int)> Try(string email) { Reset(); var r = await NewHandler().HandleAsync(Good(), Body(email: email), default); return (r.Status, Sends()); }
            check(await Try("invitee@y.test") is (200, 1), "OTP-Empfänger: Adresse mit gültiger Einladung erhält den Code");
            check(await Try("INVITEE@Y.TEST") is (200, 1), "OTP-Empfänger: Groß-/Kleinschreibung unerheblich");
            check(await Try("extern@y.test") is (200, 1), "OTP-Empfänger: aktiver externer Benutzer (erneute Anmeldung) erhält den Code");
            check(await Try("fremd@y.test") is (403, 0), "OTP-Empfänger: unbekannte Adresse → 403, keine Mail");
            check(await Try("widerrufen@y.test") is (403, 0) && await Try("abgelaufen@y.test") is (403, 0), "OTP-Empfänger: widerrufene oder abgelaufene Einladung → 403, keine Mail");
            check(await Try("gesperrt@y.test") is (403, 0), "OTP-Empfänger: gesperrter externer Benutzer → 403");
            check(await Try("mitarbeiter@y.test") is (403, 0), "OTP-Empfänger: Workforce-Benutzer sind keine Empfänger (sie melden sich nicht über External ID an) — ihre Anmeldung bleibt davon unberührt");
            check(extId.Success && (await env.Invites.MayReceiveSignInCodeAsync("invitee@y.test")) && !(await env.Invites.MayReceiveSignInCodeAsync("fremd@y.test")),
                "OTP-Empfänger: die Prüfung ist rein lesend und gewährt weder Registrierung noch Zugriff (die Einlösung prüft allein der InvitationService)");
            Reset(); var s0 = await env.Raw.QueryAsync("SELECT status FROM invitations WHERE recipient_email='invitee@y.test'");
            await NewHandler().HandleAsync(Good(), Body(), default);
            check((await env.Raw.QueryAsync("SELECT status FROM invitations WHERE recipient_email='invitee@y.test'"))[0]["status"] == s0[0]["status"] && s0[0]["status"] == "pending", "OTP: der Versand eines Codes verbraucht oder verändert die Einladung nicht");
        }
        {
            Reset(); var h = NewHandler(perRecipient: 2);
            var rs = new List<int>(); for (var i = 0; i < 4; i++) rs.Add((await h.HandleAsync(Good(), Body(code: "1000000" + i), default)).Status);
            check(rs.SequenceEqual(new[] { 200, 200, 429, 429 }) && Sends() == 2, "OTP: Ratenbegrenzung je Empfänger (429, keine weitere Mail)");
            Reset(); var hg = NewHandler(global: 2);
            var gs = new List<int>(); foreach (var m in new[] { "invitee@y.test", "extern@y.test", "invitee@y.test" }) gs.Add((await hg.HandleAsync(Good(), Body(email: m, code: "2" + gs.Count + "345678"), default)).Status);
            check(gs[2] == 429, "OTP: globale Ratenbegrenzung");
        }
        {
            Reset(); graphStatus = 403;
            var r = await NewHandler().HandleAsync(Good(), Body(), default);
            check(r.Status == 502 && r.Json is null, "OTP: unberechtigter Absender (Graph 403) → 502 ohne „continue“ (Entra sendet keinen eigenen Code ohne Rückfall)");
            Reset(); graphStatus = 503;
            var r2 = await NewHandler().HandleAsync(Good(), Body(code: "99887766"), default);
            check(r2.Status == 504 && r2.Json is null, "OTP: unklares Ergebnis → 504 (nicht als Erfolg gemeldet)");
        }
        {
            Reset(); var slow = NewHandler();
            var slowGraph = new StubHandler { Respond = graph.Respond, DelayFor = r => r.RequestUri!.Host == "graph.microsoft.com" ? 3000 : 0 };
            var o = new OtpExtensionOptions { Authority = Auth, Audiences = new[] { "api-app" }, ExpectedTenantId = "tid", SendTimeout = TimeSpan.FromMilliseconds(300) };
            var hs = new OtpSendHandler(new ExtensionTokenValidator(new HttpClient(disc), o), new ExchangeOnlineMailService(new HttpClient(slowGraph), MailOpt()), new InvitationOtpRecipientPolicy(env.Invites), o);
            var sw = System.Diagnostics.Stopwatch.StartNew(); var r = await hs.HandleAsync(Good(), Body(code: "55443322"), default); sw.Stop();
            check(r.Status == 504 && sw.ElapsedMilliseconds < 2500, "OTP: Zeitlimit für den Mailversand wirkt (504 statt Hängenbleiben)");
        }
        // Keine Codes in Datenbank, Audit und Ausgaben
        string[] codes = { Code, "10000000", "10000001", "55443322", "99887766" };
        var dbLeak = new List<string>();
        foreach (var t in new[] { "audit_log", "invitation_events", "invitation_deliveries", "invitations", "invitation_meta", "users" })
            foreach (var row in await env.Raw.QueryAsync($"SELECT * FROM {t}"))
                foreach (var v in row.Values) if (v is not null && codes.Any(v.Contains)) dbLeak.Add(t);
        string auditText; lock (audit) auditText = string.Join("\n", audit);
        check(dbLeak.Count == 0 && !codes.Any(auditText.Contains) && !auditText.Contains("@") && auditText.Contains("otp_send|200:accepted"),
            "OTP: Codes stehen weder in der Datenbank noch im Audit-Hook (nur Ergebniscodes, keine Adressen)");
    }

    // ─── Verwaltung der Lernrechte ─────────────────────────────────────

    private static async Task AdministrationAsync(Action<bool, string> check, string dir)
    {
        Console.WriteLine("Verwaltung: Zuweisungen, Rechteentzug, Deaktivierung, Bestandsschutz");
        var env = await InvitationUnitTests.Env.CreateAsync(dir);
        var modA = await env.AddGlobalModuleAsync("Modul A"); var modB = await env.AddGlobalModuleAsync("Modul B");
        var (topic, topicModule) = await env.AddTopicAsync("Datenschutz", global: true, versions: 2);
        var wf = await env.AddWorkforceUserAsync("Mitarbeiter", "ma@firma.test");
        var inv = await env.Invites.CreateAsync(env.Admin, "kunde@y.test", LearnerProfiles.B2cLearner, new[] { modA });
        var reg = await env.Invites.RedeemAsync(inv.Token, InvitationUnitTests.Id("kunde@y.test"));
        var ext = reg.UserId!;
        async Task<bool> CanRead(string user, string module) => (await env.Repo.ResolveModuleAccessAsync(user, module)) is not null;
        async Task<List<string>> Roles2(string user) => (await env.Raw.QueryAsync("SELECT role FROM user_roles WHERE user_id=@u", new (string, object?)[] { ("u", user) })).Select(r => r["role"]!).OrderBy(x => x).ToList();

        // Workforce: Zuweisung ohne neue Einladung/Identität/Profil
        var idsBefore = await env.CountAsync("SELECT COUNT(*) AS n FROM user_identities WHERE user_id=@u", ("u", wf));
        var rolesBefore = await Roles2(wf);
        var readBefore = await CanRead(wf, modB);
        await env.Learners.AssignModuleAsync(env.Admin, wf, modA);
        var a1 = await env.Learners.AssignTopicAsync(env.Admin, wf, topic, 2, mandatory: true, dueAt: DateTime.UtcNow.AddDays(30));
        var ov = await env.Learners.GetOverviewAsync(env.Admin, wf);
        check(ov is { Profile: null, HasWorkforceIdentity: true, HasExternalIdentity: false } && ov.ModuleGrants.Count == 1 && ov.TopicAssignments.Count == 1 && ov.TopicAssignments[0].Mandatory,
            "Workforce: Module und Pflichtthema ohne neue Einladung, Identität oder Lernprofil zugewiesen");
        check(await env.CountAsync("SELECT COUNT(*) AS n FROM user_identities WHERE user_id=@u", ("u", wf)) == idsBefore && await env.CountAsync("SELECT COUNT(*) AS n FROM user_profiles WHERE user_id=@u", ("u", wf)) == 0
              && (await Roles2(wf)).SequenceEqual(rolesBefore) && await CanRead(wf, modB) == readBefore, "Workforce: Rollen, Identitäten und bisheriger Zugriff bleiben unverändert (Bestandsschutz)");
        check(await env.Learners.RevokeModuleAsync(env.Admin, wf, modA) && await env.Learners.RevokeTopicAssignmentAsync(env.Admin, a1.Id) && await CanRead(wf, modB) == readBefore,
            "Workforce: Entzug nimmt nur die Zuweisung zurück, nicht den bisherigen Zugriff");

        // Extern (B2C): nur zugewiesen
        check(await CanRead(ext, modA) && !await CanRead(ext, modB), "B2C: Modul A (zugewiesen) ja, Modul B (global, nicht zugewiesen) nein");
        await env.Learners.AssignModuleAsync(env.Admin, ext, modB);
        check(await CanRead(ext, modB), "B2C: nach ausdrücklicher Zuweisung ist Modul B erreichbar");
        await env.Learners.RevokeModuleAsync(env.Admin, ext, modB);
        check(!await CanRead(ext, modB) && await CanRead(ext, modA), "B2C: Rechteentzug wirkt sofort (ohne neue Anmeldung), das übrige Recht bleibt");
        var a2 = await env.Learners.AssignTopicAsync(env.Admin, ext, topic, 2, mandatory: false);   // bei globalen Themen ist nur die aktive globale Version zuweisbar
        var mod1 = await env.TopicModuleOfVersionAsync(topic, 1); var mod2 = await env.TopicModuleOfVersionAsync(topic, 2);
        check(await CanRead(ext, mod2) && !await CanRead(ext, mod1), "B2C: Themenzuweisung gilt für die zugewiesene Version, nicht für andere");
        var ovExt = await env.Learners.GetOverviewAsync(env.Admin, ext);
        check(ovExt!.TopicAssignments.Single().Mandatory == false, "Pflicht: eine freiwillige Zuweisung erzeugt keine Pflicht (nur ausdrückliches mandatory)");
        await env.Learners.RevokeTopicAssignmentAsync(env.Admin, a2.Id);
        check(!await CanRead(ext, mod2), "B2C: Entzug der Themenzuweisung wirkt sofort");
        var a3 = await env.Learners.AssignTopicAsync(env.Admin, ext, topic, 2, mandatory: true, dueAt: DateTime.UtcNow.AddDays(10));
        check((await env.Learners.GetOverviewAsync(env.Admin, ext))!.TopicAssignments.Any(t => t.Mandatory && t.TopicVersion == 2) && await CanRead(ext, mod2), "Pflichtzuweisung: nutzt die bestehende mandatory-Logik mit konkreter Version");

        // Deaktivierung
        await env.Users.SetStatusAsync(env.Admin, ext, false);
        var disabled = await env.Users.GetAsync(ext);
        check(disabled is { IsActive: false } && (await env.Users.SignInKnownIdentityAsync("https://ext.example/v2.0", (await env.Raw.QueryAsync("SELECT subject FROM user_identities WHERE user_id=@u", new (string, object?)[] { ("u", ext) }))[0]["subject"]!)) is { IsActive: false },
            "Deaktivierung: der gesperrte Benutzer gilt als nicht aktiv (die Anmeldung wird im Callback abgewiesen)");
        await env.Users.SetStatusAsync(env.Admin, ext, true);

        // Berechtigung der Verwaltung
        async Task<bool> Denied(Func<Task> f) { try { await f(); return false; } catch (UnauthorizedAccessException) { return true; } }
        check(await Denied(() => env.Learners.AssignModuleAsync(env.Auditor, ext, modB)) && await Denied(() => env.Learners.AssignModuleAsync(env.TopicAdmin, ext, modB))
              && await Denied(() => env.Learners.AssignModuleAsync(ext, ext, modB)) && await Denied(() => env.Learners.GetOverviewAsync(ext, ext))
              && await Denied(() => env.Invites.ListAsync(env.Auditor)) && await Denied(() => env.Invites.CreateAsync(env.TopicAdmin, "x@y.test", LearnerProfiles.B2cLearner)),
            "Berechtigung: Modulzuweisung, Übersicht und Einladungen nur für Administratoren (Rolle gegen die Datenbank); externe Benutzer können sich nichts zuweisen");
        check(await Denied(() => env.Users.SetStatusAsync(ext, wf, false)), "Berechtigung: Sperren nur durch Administratoren");

        var roleDenied = false; try { await env.Users.SetRolesAsync(env.Admin, ext, new[] { Roles.Admin }); } catch (InvalidOperationException) { roleDenied = true; }
        check(roleDenied && (await Roles2(ext)).SequenceEqual(new List<string> { "user" }), "Rollen: externe Lernende erhalten keine administrative Rolle (Dienst verweigert, auch durch Administratoren)");
        await env.Users.SetRolesAsync(env.Admin, wf, new[] { Roles.Auditor });
        check((await Roles2(wf)).Contains(Roles.Auditor), "Rollen: Workforce-Benutzer lassen sich unverändert Rollen zuweisen");
        // Profile sind keine Rollen; Identitäten werden nicht verknüpft
        check((await Roles2(ext)).SequenceEqual(new List<string> { "user" }), "Profil B2C vergibt keine Rolle: der externe Benutzer hat nur „user“");
        var dup = false; try { await env.Invites.CreateAsync(env.Admin, "ma@firma.test", LearnerProfiles.B2cLearner); } catch (AccountExistsException) { dup = true; }
        check(dup, "Keine Identitätsverknüpfung über die E-Mail: für ein vorhandenes Workforce-Konto gibt es keine Einladung");

        // Ereignisse sind nachvollziehbar und geheimnisfrei
        var types = (await env.Events.ListAsync(null, ext, 100)).Select(e => e.Type).ToHashSet();
        check(new[] { "user.created", "profile.assigned", "content.assigned", "content.revoked" }.All(types.Contains), "Ereignisse: Benutzer angelegt, Profil, Rechte vergeben und entzogen sind je Benutzer nachvollziehbar");
        check(AdminEventLabels.Label(InviteEventTypes.Submitted).Contains("nicht zwingend zugestellt") && AdminEventLabels.ErrorLabel("timeout").Contains("unklar"), "Anzeige: „angenommen“ wird nie als „zugestellt“ bezeichnet");
    }

    // ─── Oberfläche (Komponenten im Prozess gerendert) ─────────────────

    private static async Task UiAsync(Action<bool, string> check, string dir)
    {
        Console.WriteLine("Administrationsoberfläche (gerenderte Komponenten)");
        var env = await InvitationUnitTests.Env.CreateAsync(dir);
        env.Cfg.ExternalId = new ExternalIdSettings { Enabled = true };
        env.Cfg.Mail = new ExchangeMailSettings { Enabled = true };
        var mod = await env.AddGlobalModuleAsync("Sichtbares Modul");
        var wf = await env.AddWorkforceUserAsync("Mitarbeiterin Muster", "muster@firma.test");
        var created = await env.Invites.CreateAsync(env.Admin, "extern-ui@kunde.test", LearnerProfiles.B2cLearner, new[] { mod });
        var reg = await env.Invites.RedeemAsync((await env.Invites.CreateAsync(env.Admin, "b2c-ui@kunde.test", LearnerProfiles.B2cLearner, new[] { mod })).Token, InvitationUnitTests.Id("b2c-ui@kunde.test"));

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, env.Cfg);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, env.Users);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, env.Topics);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, env.Events);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, env.Invites);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, env.Learners);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, new SharingService(env.Db, env.Repo, env.Users));
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, new InvitationDeliveryService(env.Db, env.Users, env.Invites, new FakeMailer(), env.Events, () => env.Now));
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(services, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);

        async Task<string> Render<T>(string actor, params (string, object?)[] extra) where T : Microsoft.AspNetCore.Components.IComponent
        {
            await using var r = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(sp, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
            var dict = new Dictionary<string, object?> { ["ActorId"] = actor };
            foreach (var (k, v) in extra) dict[k] = v;
            return await r.Dispatcher.InvokeAsync(async () => (await r.RenderComponentAsync<T>(Microsoft.AspNetCore.Components.ParameterView.FromDictionary(dict))).ToHtmlString());
        }

        var inv = await Render<LearnCards.Web.Components.Shared.AdminInvitationsPanel>(env.Admin, ("InitiallyOpen", true));
        check(inv.Contains("Neue Einladung") && inv.Contains("Einladung erstellen (ohne Versand)") && inv.Contains("extern-ui@kunde.test") && inv.Contains("nicht versendet") && inv.Contains("Sichtbares Modul"),
            "UI Einladungen: Formular „ohne Versand“, Einladungsübersicht mit Versandstatus „nicht versendet“ und Auswahl globaler Module");
        check(!inv.Contains(created.Token) && !inv.Contains("Einladung jetzt senden"), "UI Einladungen: kein Token im Markup; ohne Auswahl einer Einladung gibt es keine Versandschaltfläche (kein Versand durch Aufrufen oder Aktualisieren)");
        check(inv.Contains("B2C — einzelne Lernende") && inv.Contains("B2B — Lernende einer Organisation") && inv.Contains("verpflichtend"), "UI Einladungen: Lernprofil B2B/B2C, Gültigkeit und Pflicht/freiwillig wählbar");
        env.Cfg.Mail = new ExchangeMailSettings { Enabled = false };
        var invNoMail = await Render<LearnCards.Web.Components.Shared.AdminInvitationsPanel>(env.Admin);
        check(invNoMail.Contains("Mailversand ist nicht aktiviert"), "UI Einladungen: ohne MAIL_ENABLED weist die Oberfläche darauf hin");
        env.Cfg.ExternalId = new ExternalIdSettings { Enabled = false };
        check((await Render<LearnCards.Web.Components.Shared.AdminInvitationsPanel>(env.Admin)).Contains("Einladungen sind nicht aktiv"), "UI Einladungen: ohne External ID erscheint ein Hinweis statt der Funktionen");
        env.Cfg.ExternalId = new ExternalIdSettings { Enabled = true };

        var users = await env.Users.ListAsync();
        var learners = await Render<LearnCards.Web.Components.Shared.AdminLearnersPanel>(env.Admin, ("UserList", users));
        check(learners.Contains("Mitarbeiterin Muster") && learners.Contains(">Workforce<") && learners.Contains("Extern") && learners.Contains("B2C") && learners.Contains("haben keine Rollen"),
            "UI Lernende: Workforce-Benutzer und externe Lernende (B2C) mit Art, ohne Profil- oder Rollenänderung");
        var wfOpen = await Render<LearnCards.Web.Components.Shared.AdminLearnersPanel>(env.Admin, ("UserList", users), ("InitiallyOpenUserId", wf));
        check(wfOpen.Contains("Zugang und Rollen") && wfOpen.Contains("Themen-Admin") && wfOpen.Contains("aria-pressed") && wfOpen.Contains("Bestandsprofil"), "UI Benutzer: Workforce-Benutzer — Rollen sind je Rolle schaltbar, Bestandsprofil angezeigt");
        var extOpen = await Render<LearnCards.Web.Components.Shared.AdminLearnersPanel>(env.Admin, ("UserList", users), ("InitiallyOpenUserId", reg.UserId!));
        check(extOpen.Contains("Externes Profil: keine Rollen") && !extOpen.Contains("aria-pressed") && extOpen.Contains("Sichtbares Modul") && extOpen.Contains("Pflichtschulungen"), "UI Benutzer: externer Lernender — keine Rollenschalter, Profil B2C, Zuweisungen (Module, Themen/Pflicht)");
        await env.Events.LogAsync(InviteEventTypes.Submitted, created.InvitationId, null, null, env.Admin);
        var evHtml = await Render<LearnCards.Web.Components.Shared.AdminEventsPanel>(env.Admin);
        check(evHtml.Contains("Einladung erstellt") && evHtml.Contains("Versand angenommen") && evHtml.Contains("kein Beweis") && !evHtml.Contains(created.Token), "UI Ereignisprotokoll: Beschriftungen, Hinweis zur Aussagekraft, keine Tokens");

        async Task<bool> Refused<T>(string actor, string secret, params (string, object?)[] extra) where T : Microsoft.AspNetCore.Components.IComponent
        {
            try { var h = await Render<T>(actor, extra); return !h.Contains(secret) && !h.Contains("Einladung erstellen"); }
            catch (UnauthorizedAccessException) { return true; }
        }
        check(await Refused<LearnCards.Web.Components.Shared.AdminEventsPanel>(env.Auditor, "extern-ui@kunde.test") && await Refused<LearnCards.Web.Components.Shared.AdminEventsPanel>(reg.UserId!, "Einladung erstellt")
              && await Refused<LearnCards.Web.Components.Shared.AdminInvitationsPanel>(env.Auditor, "extern-ui@kunde.test") && await Refused<LearnCards.Web.Components.Shared.AdminLearnersPanel>(wf, "Mitarbeiterin Muster", ("UserList", users)),
            "UI: Ereignisse, Einladungen und Lernende nur für Administratoren — die Rolle wird serverseitig geprüft (Auditor, externer und Workforce-Benutzer sehen weder Daten noch Formulare)");
    }

    // ─── Auf der Plattform erzeugtes Mail-Zertifikat (ADR 0050) ────────

    private static async Task CertificateAsync(Action<bool, string> check, string dir)
    {
        Console.WriteLine("Mail-Zertifikat auf der Plattform");
        var env = await InvitationUnitTests.Env.CreateAsync(dir);
        var keyDir = Path.Combine(dir, "mailkeys");
        var master = RandomNumberGenerator.GetBytes(32);
        var certs = new MailCertificateService(env.Users, keyDir, master);

        async Task<bool> Denied(Func<Task> f) { try { await f(); return false; } catch (UnauthorizedAccessException) { return true; } }
        check(await Denied(() => certs.GenerateAsync(env.Auditor)) && await Denied(() => certs.GenerateAsync(env.TopicAdmin)) && await Denied(() => certs.GetInfoAsync(env.Auditor)) && await Denied(() => certs.GetPublicCertificateAsync(null)) && !Directory.Exists(keyDir),
            "Mail-Zertifikat: Erzeugen, Anzeigen und Herunterladen nur für Administratoren; ohne Recht entsteht nichts");
        check(await certs.GetInfoAsync(env.Admin) is null, "Mail-Zertifikat: anfangs kein Zertifikat");

        var info = await certs.GenerateAsync(env.Admin);
        var file = Path.Combine(keyDir, "mail-client.pfx.enc");
        var raw = await File.ReadAllBytesAsync(file);
        bool Parses() { try { using var c = X509CertificateLoader.LoadPkcs12(raw, ""); return true; } catch (CryptographicException) { return false; } }
        check(info.Usable && info.Thumbprint.Length == 40 && info.NotAfter > DateTime.UtcNow.AddMonths(23) && File.Exists(file), "Mail-Zertifikat: erzeugt (Fingerabdruck, 24 Monate), Datei im Schlüsselverzeichnis");
        check(!Parses() && !Encoding.ASCII.GetString(raw).Contains("Learny-Mail-Client") && !Encoding.UTF8.GetString(raw).Contains("PRIVATE KEY"), "Mail-Zertifikat: die gespeicherte Datei ist verschlüsselt (kein lesbares PKCS#12, kein Klartext)");
        var der = await certs.GetPublicCertificateAsync(env.Admin);
        using (var pub = X509CertificateLoader.LoadCertificate(der!))
            check(pub.Thumbprint == info.Thumbprint && !pub.HasPrivateKey, "Mail-Zertifikat: der Download enthält nur das öffentliche Zertifikat (kein privater Schlüssel)");
        var audit = (await env.Raw.QueryAsync("SELECT detail FROM audit_log WHERE action='mail.certificate.create'")).Single()["detail"]!;
        check(audit.Contains(info.Thumbprint) && !audit.Contains(Convert.ToBase64String(master)) && audit.Length < 100, "Mail-Zertifikat: Audit enthält nur den öffentlichen Fingerabdruck");

        var again = false; try { await certs.GenerateAsync(env.Admin); } catch (InvalidOperationException) { again = true; }
        check(again && (await certs.GetInfoAsync(env.Admin))!.Thumbprint == info.Thumbprint, "Mail-Zertifikat: ein vorhandenes wird ohne ausdrückliche Bestätigung nie ersetzt");

        // Versand mit dem Plattformzertifikat
        var graph = new StubHandler { Respond = r => r.RequestUri!.Host == "login.microsoftonline.com" ? Token() : new HttpResponseMessage(HttpStatusCode.Accepted) };
        var settings = ExchangeMailSettings.Load(k => new Dictionary<string, string> { ["MAIL_ENABLED"] = "1", ["MAIL_TENANT_ID"] = WorkforceMailTenant, ["MAIL_CLIENT_ID"] = Guid.NewGuid().ToString(), ["MAIL_SENDER"] = "learny@brors.link", ["MAIL_ALLOWED_RECIPIENTS"] = "*" }.GetValueOrDefault(k, ""), ExternalTenant, false, dir);
        var emptyDir = new MailCertificateService(env.Users, Path.Combine(dir, "leer"), master);
        var mailer0 = new PlatformMailer(settings, emptyDir, new MailRecipientsService(env.Db, env.Users, settings), () => new HttpClient(graph));
        var mail = new InvitationMail("a@kunde.test", "https://learny.test/invite#x", DateTime.UtcNow.AddDays(1));
        var none = await mailer0.SendAsync(mail);
        check(none.Outcome == MailOutcome.Rejected && none.Code == "mail_certificate_missing" && graph.Calls.Count == 0, "Mail-Zertifikat: ohne Zertifikat wird nichts gesendet und nichts abgerufen (fail-closed)");

        string AssertionThumb(string body)
        {
            var form = body.Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
            var head = form["client_assertion"].Split('.')[0].Replace('-', '+').Replace('_', '/'); head = head.PadRight(head.Length + (4 - head.Length % 4) % 4, '=');
            using var d = JsonDocument.Parse(Convert.FromBase64String(head));
            return d.RootElement.GetProperty("x5t#S256").GetString()!;
        }
        static string B64u(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var mailer = new PlatformMailer(settings, certs, new MailRecipientsService(env.Db, env.Users, settings), () => new HttpClient(graph));
        var sent = await mailer.SendAsync(mail);
        using var certA = X509CertificateLoader.LoadCertificate(der!);
        check(sent.Outcome == MailOutcome.Accepted && graph.Calls.Count(c => c.Url.Contains("/sendMail")) == 1 && AssertionThumb(graph.Calls[0].Body) == B64u(certA.GetCertHash(HashAlgorithmName.SHA256)),
            "Mail-Zertifikat: der Versand signiert die Client-Assertion mit dem Plattformzertifikat (x5t#S256 passt zur hinterlegbaren .cer)");

        var info2 = await certs.GenerateAsync(env.Admin, replaceExisting: true);
        graph.Calls.Clear();
        await mailer.SendAsync(mail);
        var der2 = await certs.GetPublicCertificateAsync(env.Admin);
        using var certB = X509CertificateLoader.LoadCertificate(der2!);
        check(info2.Thumbprint != info.Thumbprint && AssertionThumb(graph.Calls[0].Body) == B64u(certB.GetCertHash(HashAlgorithmName.SHA256)), "Mail-Zertifikat: ein ausdrücklich ersetztes Zertifikat wirkt ohne Neustart (neuer Fingerabdruck in der Assertion)");

        // anderer Schlüssel (INVITE_TOKEN_KEY gewechselt/verloren): nicht lesbar, nie still ersetzt
        var wrong = new MailCertificateService(env.Users, keyDir, RandomNumberGenerator.GetBytes(32));
        var wInfo = await wrong.GetInfoAsync(env.Admin);
        var before = await File.ReadAllBytesAsync(file);
        graph.Calls.Clear();
        var wMailer = new PlatformMailer(settings, wrong, new MailRecipientsService(env.Db, env.Users, settings), () => new HttpClient(graph));
        check(wInfo is { Usable: false } && await wrong.GetPublicCertificateAsync(env.Admin) is null && (await wMailer.SendAsync(mail)).Code == "mail_certificate_missing" && graph.Calls.Count == 0 && (await File.ReadAllBytesAsync(file)).SequenceEqual(before),
            "Mail-Zertifikat: mit falschem Schlüssel unbrauchbar (Anzeige „nicht lesbar“, kein Versand) und die Datei bleibt unverändert");
        var noKey = new MailCertificateService(env.Users, Path.Combine(dir, "ohne"), Array.Empty<byte>());
        var nk = false; try { await noKey.GenerateAsync(env.Admin); } catch (InvalidOperationException) { nk = true; }
        check(nk && !Directory.Exists(Path.Combine(dir, "ohne")), "Mail-Zertifikat: ohne INVITE_TOKEN_KEY wird nichts erzeugt");

        // Oberfläche
        env.Cfg.Mail = settings;
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, env.Cfg);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, certs);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, new MailRecipientsService(env.Db, env.Users, settings));
        using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        async Task<string> Render(string actor)
        {
            await using var r = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(sp, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
            return await r.Dispatcher.InvokeAsync(async () => (await r.RenderComponentAsync<LearnCards.Web.Components.Shared.AdminMailPanel>(Microsoft.AspNetCore.Components.ParameterView.FromDictionary(new Dictionary<string, object?> { ["ActorId"] = actor }))).ToHtmlString());
        }
        var html = await Render(env.Admin);
        check(html.Contains(info2.Thumbprint) && html.Contains("/api/admin/mail/certificate.cer") && html.Contains("Zertifikat erzeugen") && html.Contains("learny@brors.link") && !html.Contains("PRIVATE") && !html.Contains(Convert.ToBase64String(master)),
            "UI Mailversand: Fingerabdruck, Download des öffentlichen Zertifikats, Entra-Anleitung — kein privates Material");
        var denied = await Render(env.Auditor);
        check(!denied.Contains(info2.Thumbprint) && !denied.Contains("certificate.cer"), "UI Mailversand: ohne Administratorrolle weder Fingerabdruck noch Download");
    }

    // ─── In der Plattform änderbare Empfängerliste (ADR 0050) ──────────

    private static async Task RecipientsAsync(Action<bool, string> check, string dir)
    {
        Console.WriteLine("Erlaubte Mailempfänger (dauerhaft in der Plattform)");
        var env = await InvitationUnitTests.Env.CreateAsync(dir);
        var now = DateTime.UtcNow;
        var settings = ExchangeMailSettings.Load(k => new Dictionary<string, string> { ["MAIL_ENABLED"] = "1", ["MAIL_TENANT_ID"] = WorkforceMailTenant, ["MAIL_CLIENT_ID"] = Guid.NewGuid().ToString(), ["MAIL_SENDER"] = "learny@brors.link", ["MAIL_ALLOWED_RECIPIENTS"] = "@env.test" }.GetValueOrDefault(k, ""), ExternalTenant, false, dir);
        var rec = new MailRecipientsService(env.Db, env.Users, settings, () => now, TimeSpan.Zero);
        var certs = new MailCertificateService(env.Users, Path.Combine(dir, "k"), RandomNumberGenerator.GetBytes(32));
        await certs.GenerateAsync(env.Admin);
        var graph = new StubHandler { Respond = r => r.RequestUri!.Host == "login.microsoftonline.com" ? Token() : new HttpResponseMessage(HttpStatusCode.Accepted) };
        var mailer = new PlatformMailer(settings, certs, rec, () => new HttpClient(graph));
        async Task<string> Send(string to) => (await mailer.SendAsync(new InvitationMail(to, "https://learny.test/invite#x", DateTime.UtcNow.AddDays(1)))).Code;
        async Task<bool> Denied(Func<Task> f) { try { await f(); return false; } catch (UnauthorizedAccessException) { return true; } }

        var start = await rec.GetAsync(env.Admin);
        check(start.Source == "umgebung" && start.Recipients.SequenceEqual(new[] { "@env.test" }) && await Send("a@env.test") == "accepted" && await Send("a@brors.net") == "recipient_not_allowed",
            "Empfänger: ohne gespeicherten Wert gilt MAIL_ALLOWED_RECIPIENTS aus der Umgebung");
        check(await Denied(() => rec.SaveAsync(env.Auditor, "*")) && await Denied(() => rec.SaveAsync(env.TopicAdmin, "*")) && await Denied(() => rec.ResetAsync(null)) && await Denied(() => rec.GetAsync(env.Auditor)), "Empfänger: Ändern, Zurücksetzen und Anzeigen nur für Administratoren");

        await rec.SaveAsync(env.Admin, "@brors.net, Joerg+10@Kunde.test");
        check(await Send("joerg+10@brors.net") == "accepted" && await Send("joerg+10@kunde.test") == "accepted" && await Send("x@env.test") == "recipient_not_allowed",
            "Empfänger: gespeicherte Liste (Domain und Einzeladresse, Groß-/Kleinschreibung egal) wirkt sofort ohne Neustart und ersetzt den Umgebungswert");
        var again = new MailRecipientsService(env.Db, env.Users, settings, () => now, TimeSpan.Zero);
        var info = await again.GetAsync(env.Admin);
        check(info.Source == "datenbank" && info.Recipients.SequenceEqual(new[] { "@brors.net", "joerg+10@kunde.test" }), "Empfänger: der Wert ist dauerhaft (neue Dienstinstanz, z. B. nach Neustart oder zweite Instanz, liest ihn aus der Datenbank)");

        await rec.SaveAsync(env.Admin, "*");
        check(await Send("irgendwer@egal.test") == "accepted" && (await rec.GetAsync(env.Admin)).Recipients.Contains("*"), "Empfänger: „*“ erlaubt jede Adresse");
        await rec.SaveAsync(env.Admin, "");
        check(await Send("a@brors.net") == "recipient_not_allowed" && (await rec.GetAsync(env.Admin)).Recipients.Count == 0, "Empfänger: ein leerer gespeicherter Wert heißt niemand (fail-closed), nicht „Umgebungswert“");

        var bad = false; try { await rec.SaveAsync(env.Admin, "kein-eintrag, @x"); } catch (ArgumentException) { bad = true; }
        check(bad && (await rec.GetAsync(env.Admin)).Recipients.Count == 0, "Empfänger: ungültige Eingabe wird abgewiesen und ändert nichts");

        await rec.ResetAsync(env.Admin);
        var reset = await rec.GetAsync(env.Admin);
        check(reset.Source == "umgebung" && reset.Recipients.SequenceEqual(new[] { "@env.test" }) && await Send("a@env.test") == "accepted", "Empfänger: Zurücksetzen entfernt den gespeicherten Wert, es gilt wieder die Umgebung");

        await rec.SaveAsync(env.Admin, "geheim.person@privat.test");
        var audits = (await env.Raw.QueryAsync("SELECT action, detail FROM audit_log WHERE action LIKE 'mail.recipients.%'")).Select(r => r["detail"] ?? "").ToList();
        check(audits.Count >= 5 && audits.All(d => !d.Contains("@")) && audits.Any(d => d.Contains("wildcard=1")), "Empfänger: das Audit enthält nur Anzahl und Platzhalter-Flag, keine Adressen");

        env.Cfg.Mail = settings;
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, env.Cfg);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, certs);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, rec);
        using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        await using var r = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(sp, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var html = await r.Dispatcher.InvokeAsync(async () => (await r.RenderComponentAsync<LearnCards.Web.Components.Shared.AdminMailPanel>(Microsoft.AspNetCore.Components.ParameterView.FromDictionary(new Dictionary<string, object?> { ["ActorId"] = env.Admin }))).ToHtmlString());
        check(html.Contains("geheim.person@privat.test") && html.Contains("in der Plattform gespeichert") && html.Contains("Zurücksetzen"), "UI Mailversand: Empfängerliste mit Quelle, Speichern und Zurücksetzen");
    }

    // ─── B2C/B2B: nur Lernen des Zugewiesenen, keine Anlage/Freigabe, kein MCP ────

    private static async Task B2cRestrictionsAsync(Action<bool, string> check, string dir)
    {
        Console.WriteLine("Externe Lernprofile: keine Anlage, keine Freigabe, kein MCP");
        var env = await InvitationUnitTests.Env.CreateAsync(dir);
        env.Cfg.McpTokenKey = RandomNumberGenerator.GetBytes(32);
        env.Cfg.McpPublicUrls = new() { "https://mcp.test" };
        var mcp = new McpLinkService(env.Db, env.Cfg, env.Users, new McpTokenProtector(env.Cfg));
        var sharing = new SharingService(env.Db, env.Repo, env.Users);
        var modA = await env.AddGlobalModuleAsync("Modul A"); var modB = await env.AddGlobalModuleAsync("Modul B");
        var inv = await env.Invites.CreateAsync(env.Admin, "b2c@kunde.test", LearnerProfiles.B2cLearner, new[] { modA });
        var ext = (await env.Invites.RedeemAsync(inv.Token, InvitationUnitTests.Id("b2c@kunde.test"))).UserId!;
        var wf = await env.AddWorkforceUserAsync("Mitarbeiter", "ma@firma.test");
        async Task<bool> Denied(Func<Task> f) { try { await f(); return false; } catch (UnauthorizedAccessException) { return true; } }

        // Lernen: nur Zugewiesenes
        check(await env.Repo.ResolveModuleAccessAsync(ext, modA) is not null && await env.Repo.ResolveModuleAccessAsync(ext, modB) is null, "B2C: sieht nur zugewiesene Module (A ja, B nein)");

        // Anlegen
        check(await Denied(() => env.Repo.CreateModuleAsync(new ModuleCreateRequest { Name = "Mein Modul" }, ext)), "B2C: kann kein Modul anlegen");
        check(await Denied(() => env.Topics.CreateAsync(ext, "Mein Thema", null)), "B2C: kann kein Thema anlegen");
        check(await Denied(() => sharing.GrantShareAsync(ext, modA, wf)) && await Denied(() => sharing.SubmitAsync(ext, modA)), "B2C: kann nichts teilen oder zur globalen Veröffentlichung einreichen");

        // MCP
        check(!await mcp.IsAllowedAsync(ext) && await mcp.IsAllowedAsync(wf), "MCP: für externe Lernprofile nicht erlaubt, für Workforce-Benutzer unverändert");
        check(await Denied(() => mcp.CreateLinkAsync(ext, "Mein Client", "token", TimeSpan.FromDays(30))), "MCP: B2C kann kein persönliches Token erzeugen");
        check(await Denied(() => mcp.CompleteConsentAsync(ext, "https://mcp.test/oauth/callback", "state-1", "Client", approve: true)), "MCP: B2C kann keine OAuth-Verknüpfung bestätigen");
        var denyUrl = await mcp.CompleteConsentAsync(ext, "https://mcp.test/oauth/callback", "state-1", "Client", approve: false);
        check(denyUrl.Contains("error=access_denied") && !denyUrl.Contains("code="), "MCP: Ablehnen bleibt möglich und liefert keinen Code");
        var (wfToken, _) = await mcp.CreateLinkAsync(wf, "Mein Client", "token", TimeSpan.FromDays(30));
        check(await mcp.ValidateAsync(wfToken) is { } v && v.User.Id == wf, "MCP: Workforce-Token funktioniert unverändert (Bestandsschutz)");

        // Bereits ausgestellter Zugang eines später eingeschränkten Benutzers ist wirkungslos
        await env.Raw.ExecuteAsync("INSERT INTO user_profiles (user_id, profile, source_invitation_id, created_at) VALUES (@u,'B2C_LEARNER',NULL,@n)", new (string, object?)[] { ("u", wf), ("n", DateTime.UtcNow) });
        check(await mcp.ValidateAsync(wfToken) is null, "MCP: ein vorhandenes Token eines nachträglich eingeschränkten Benutzers wird nicht mehr akzeptiert");
        await env.Raw.ExecuteAsync("DELETE FROM user_profiles WHERE user_id=@u", new (string, object?)[] { ("u", wf) });
        check(await mcp.ValidateAsync(wfToken) is not null, "MCP: ohne Einschränkung gilt das Token wieder (nichts wurde widerrufen oder verändert)");

        // Zuweisung ändert daran nichts: kein Recht durch Lernzuweisung
        await env.Learners.AssignModuleAsync(env.Admin, ext, modB);
        check(await Denied(() => env.Repo.CreateModuleAsync(new ModuleCreateRequest { Name = "x" }, ext)) && !await mcp.IsAllowedAsync(ext), "B2C: auch mit weiteren Zuweisungen keine Anlage und kein MCP");
    }

    // ─── Favoriten und Listenwerkzeuge ─────────────────────────────────

    private static async Task FavoritesAsync(Action<bool, string> check, string dir)
    {
        Console.WriteLine("Favoriten und Listenwerkzeuge");
        var env = await InvitationUnitTests.Env.CreateAsync(dir);
        var favs = new FavoritesService(env.Db);
        var modA = await env.AddGlobalModuleAsync("Modul A"); var modB = await env.AddGlobalModuleAsync("Modul B");
        var wf = await env.AddWorkforceUserAsync("Mitarbeiter", "ma@firma.test");
        var inv = await env.Invites.CreateAsync(env.Admin, "b2c@kunde.test", LearnerProfiles.B2cLearner, new[] { modA });
        var ext = (await env.Invites.RedeemAsync(inv.Token, InvitationUnitTests.Id("b2c@kunde.test"))).UserId!;
        async Task<bool> Fails<T>(Func<Task> f) where T : Exception { try { await f(); return false; } catch (T) { return true; } }

        check(await favs.ToggleAsync(wf, "module", modA) && (await favs.ListAsync(wf, "module")).Contains(modA), "Favoriten: Stern setzen merkt den Eintrag");
        check(!await favs.ToggleAsync(wf, "module", modA) && (await favs.ListAsync(wf, "module")).Count == 0, "Favoriten: erneutes Umschalten entfernt ihn");
        await favs.ToggleAsync(wf, "module", modA); await favs.ToggleAsync(wf, "topic", modB);
        check((await favs.ListAsync(wf, "module")).SequenceEqual(new[] { modA }) && (await favs.ListAsync(wf, "topic")).SequenceEqual(new[] { modB }), "Favoriten: getrennt nach Art (Modul, Thema, Nachweis)");
        check((await favs.ListAsync(env.Admin, "module")).Count == 0 && (await favs.ListAsync(ext, "module")).Count == 0, "Favoriten: je Benutzer getrennt, keiner sieht fremde Favoriten");
        check(new FavoritesService(env.Db) is { } again && (await again.ListAsync(wf, "module")).Contains(modA), "Favoriten: dauerhaft in der Datenbank (neue Dienstinstanz liest sie)");
        check(await Fails<ArgumentException>(() => favs.ToggleAsync(wf, "unbekannt", modA)) && await Fails<ArgumentException>(() => favs.ToggleAsync(wf, "module", "kein-guid")) && await Fails<UnauthorizedAccessException>(() => favs.ToggleAsync("kein-guid", "module", modA)),
            "Favoriten: ungültige Art, Kennung oder Benutzer werden abgewiesen");
        check(await Fails<ArgumentException>(() => favs.ToggleAsync(wf, "module", "' OR 1=1 --")) && (await env.CountAsync("SELECT COUNT(*) AS n FROM user_favorites")) == 2, "Favoriten: keine Einschleusung über die Kennung (gebundene Parameter, nur GUIDs)");

        // Ein Favorit gewährt nie Zugriff
        await favs.ToggleAsync(ext, "module", modB);
        check((await favs.ListAsync(ext, "module")).Contains(modB) && await env.Repo.ResolveModuleAccessAsync(ext, modB) is null && await env.Repo.ResolveModuleAccessAsync(ext, modA) is not null,
            "Favoriten: ein Stern gibt keinen Zugriff (B2C sieht Modul B trotz Favorit nicht, Modul A bleibt zugewiesen)");

        // Grenze
        var many = await env.AddWorkforceUserAsync("Viele", "viele@firma.test");
        for (var i = 0; i < FavoritesService.MaxPerUser; i++) await env.Raw.ExecuteAsync("INSERT INTO user_favorites (user_id, kind, item_id, created_at) VALUES (@u,'module',@i,@n)", new (string, object?)[] { ("u", many), ("i", Guid.NewGuid().ToString()), ("n", DateTime.UtcNow) });
        check(await Fails<InvalidOperationException>(() => favs.ToggleAsync(many, "module", modA)), "Favoriten: Obergrenze je Benutzer wird durchgesetzt");

        // Werkzeugleiste und Stern rendern
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Microsoft.JSInterop.IJSRuntime>(services, new NullJs());
        using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        await using var r = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(sp, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var bar = await r.Dispatcher.InvokeAsync(async () => (await r.RenderComponentAsync<LearnCards.Web.Components.Shared.ListToolbar>(Microsoft.AspNetCore.Components.ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Placeholder"] = "Module suchen", ["FavoriteCount"] = 2, ["FavoritesOnly"] = true, ["View"] = "list", ["Total"] = 6, ["Shown"] = 2,
            ["SortOptions"] = new (string, string)[] { ("fav", "Favoriten zuerst"), ("name", "Name A–Z") }, ["Sort"] = "name",
        }))).ToHtmlString());
        bar = System.Net.WebUtility.HtmlDecode(bar);
        check(bar.Contains("Module suchen") && bar.Contains("Favoriten (2)") && bar.Contains("Kachelansicht") && bar.Contains("Listenansicht") && bar.Contains("2 von 6 Einträgen") && bar.Contains("Name A–Z")
              && bar.Contains("class=\"is-on\" aria-pressed=\"true\" title=\"Liste\"") && bar.Contains("chip-active"), "UI Listenwerkzeuge: Suche, Favoritenfilter mit Zähler, Sortierung, Kachel-/Listenumschalter, Trefferzahl");
        // Gruppierung: Reihenfolge der Gruppen, Reihenfolge der Einträge bleibt, Monatsgruppen neueste zuerst
        var items = new[] { ("a", 2), ("b", 1), ("c", 2), ("d", 3) };
        var gr = ListGrouping.By(items, x => (x.Item2, "G" + x.Item2));
        check(gr.Select(g => g.Title).SequenceEqual(new[] { "G1", "G2", "G3" }) && gr[1].Items.Select(i => i.Item1).SequenceEqual(new[] { "a", "c" }) && ListGrouping.By(items, null) is { Count: 1 } one && one[0].Title == "" && one[0].Items.Count == 4,
            "Gruppierung: Gruppen in fester Reihenfolge, Einträge behalten ihre Sortierung, ohne Schlüssel eine Gruppe ohne Titel");
        var months = ListGrouping.By(new[] { new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc) }, d => ListGrouping.Month(d));
        check(months.Select(m => m.Title).SequenceEqual(new[] { "November 2026", "März 2026" }) && months[0].Items.Count == 2, "Gruppierung: Monatsgruppen auf Deutsch, neuester Monat zuerst");
        var bar2 = await r.Dispatcher.InvokeAsync(async () => (await r.RenderComponentAsync<LearnCards.Web.Components.Shared.ListToolbar>(Microsoft.AspNetCore.Components.ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["GroupOptions"] = new (string, string)[] { ("", "keine"), ("topic", "Thema"), ("date", "Änderungsmonat") }, ["Group"] = "topic", ["Total"] = 3, ["Shown"] = 3,
        }))).ToHtmlString());
        bar2 = System.Net.WebUtility.HtmlDecode(bar2);
        check(bar2.Contains("Gruppierung") && bar2.Contains("Gruppieren: keine") && bar2.Contains("Gruppiert nach Thema") && bar2.Contains("Gruppiert nach Änderungsmonat") && bar2.Contains("value=\"topic\" selected"), "UI Listenwerkzeuge: Gruppierung wählbar (keine, Thema, Änderungsmonat)");
        var star = await r.Dispatcher.InvokeAsync(async () => (await r.RenderComponentAsync<LearnCards.Web.Components.Shared.FavoriteButton>(Microsoft.AspNetCore.Components.ParameterView.FromDictionary(new Dictionary<string, object?> { ["Active"] = true, ["Label"] = "Modul A" }))).ToHtmlString());
        star = System.Net.WebUtility.HtmlDecode(star);
        check(star.Contains("★") && star.Contains("aria-pressed=\"true\"") && star.Contains("Modul A aus Favoriten entfernen"), "UI Stern: Zustand und zugänglicher Name");
    }

    private sealed class NullJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    // ─── Startziel nach der Anmeldung ──────────────────────────────────

    private static async Task LandingAsync(Action<bool, string> check, string dir)
    {
        Console.WriteLine("Startziel nach der Anmeldung");
        var env = await InvitationUnitTests.Env.CreateAsync(dir);
        var landing = new LandingService(env.Topics, env.Repo, env.Db);
        async Task AddCard(string module, DateTime at) => await env.Raw.ExecuteAsync(
            "INSERT INTO cards (id, module_id, category, term, question, definition, created_at, updated_at) VALUES (@id,@m,'K','T','Q','D',@t,@t)",
            new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("m", module), ("t", at) });

        var wf = await env.AddWorkforceUserAsync("Mitarbeiter", "ma@firma.test");
        check(await landing.ResolveAsync(wf) == "/" && await landing.ResolveAsync(null) == "/" && await landing.ResolveAsync("kein-guid") == "/", "Startziel: ohne Inhalte, ohne Benutzer oder mit ungültiger Kennung → Modulübersicht");

        var old = await env.AddPrivateModuleAsync(wf, "Älteres Modul"); var recent = await env.AddPrivateModuleAsync(wf, "Neueres Modul");
        await AddCard(old, DateTime.UtcNow.AddDays(-30)); await AddCard(recent, DateTime.UtcNow.AddDays(-1));
        check(await landing.ResolveAsync(wf) == "/module/" + recent, "Startziel: ohne zugewiesene Themen → Modul mit den aktuellsten Lernkarten");
        await env.Raw.ExecuteAsync("UPDATE cards SET archived=1 WHERE module_id=@m", new (string, object?)[] { ("m", recent) });
        check(await landing.ResolveAsync(wf) == "/module/" + old, "Startziel: archivierte Karten zählen nicht");

        // zugewiesenes Thema hat Vorrang
        var other = await env.AddWorkforceUserAsync("Anderer", "an@firma.test");
        var (topic, _) = await env.AddTopicAsync("Zugewiesenes Thema", global: true, versions: 1, owner: other);
        var asg = await env.Learners.AssignTopicAsync(env.Admin, wf, topic, 1, mandatory: false);
        check(await landing.ResolveAsync(wf) == "/topics?tab=assigned", "Startziel: zugewiesene, nicht abgeschlossene Themen haben Vorrang → Themen, Bereich „Mir zugewiesen“");
        await env.Learners.RevokeTopicAssignmentAsync(env.Admin, asg.Id);
        check(await landing.ResolveAsync(wf) == "/module/" + old, "Startziel: nach Entzug der Zuweisung wieder das Modul mit den aktuellsten Karten");
        var (ownTopic, _) = await env.AddTopicAsync("Eigenes globales Pflichtthema", global: true, versions: 1, owner: wf);
        var asg2 = await env.Learners.AssignTopicAsync(env.Admin, wf, ownTopic, 1, mandatory: true);
        check(await landing.ResolveAsync(wf) == $"/topics/{ownTopic}?version=1", "Startziel: Pflichtthema ohne Eintrag unter „Mir zugewiesen“ (eigen/global) → direkt das Thema");
        await env.Learners.RevokeTopicAssignmentAsync(env.Admin, asg2.Id);

        // Fremde Module zählen nie; B2C sieht nur Zugewiesenes
        var foreign = await env.AddPrivateModuleAsync(other, "Fremd"); await AddCard(foreign, DateTime.UtcNow);
        check(await landing.ResolveAsync(wf) == "/module/" + old, "Startziel: Karten fremder privater Module bestimmen das Ziel nie");
        var modA = await env.AddGlobalModuleAsync("Zugewiesen"); var modB = await env.AddGlobalModuleAsync("Nicht zugewiesen");
        await AddCard(modA, DateTime.UtcNow.AddDays(-5)); await AddCard(modB, DateTime.UtcNow);
        var inv = await env.Invites.CreateAsync(env.Admin, "b2c@kunde.test", LearnerProfiles.B2cLearner, new[] { modA });
        var ext = (await env.Invites.RedeemAsync(inv.Token, InvitationUnitTests.Id("b2c@kunde.test"))).UserId!;
        check(await landing.ResolveAsync(ext) == "/module/" + modA, "Startziel: externes Lernprofil landet nur bei zugewiesenen Inhalten (Modul B ist neuer, aber nicht zugewiesen)");
        check(AuthEndpointsLanding("/") == "/start" && AuthEndpointsLanding("/profile") == "/profile" && AuthEndpointsLanding("/topics") == "/topics", "Startziel: nur ohne bestimmtes Ziel (/) wird nach der Anmeldung auf /start geleitet");
    }

    private static string AuthEndpointsLanding(string url) => (string)typeof(LearnCards.Web.Auth.AuthEndpoints).GetMethod("AfterLogin", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object[] { url })!;
}
