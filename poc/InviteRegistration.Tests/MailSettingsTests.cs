using System.Net;
using System.Security.Cryptography.X509Certificates;
using InviteRegistration;
using InviteRegistration.Portal;
using LearnCards.Web.Data;

// Mail-Einrichtung im Portal: App-Registrierung, Zertifikat, Download nur des öffentlichen Teils, Aktivierung nur mit Voraussetzungen.
static partial class ExtendedTests
{
    static async Task MailSettingsTests(Action<string, bool, string?> check, string tmp)
    {
        Console.WriteLine("Mail-Einrichtung im Portal");
        const string ext = "11111111-1111-1111-1111-111111111111", wf = "22222222-2222-2222-2222-222222222222", app = "33333333-3333-3333-3333-333333333333";
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var p = await StartPortal(tmp, tokenKey: key, mail: (db, keyDir) => new MailSettingsService(db, System.IO.Path.Combine(keyDir, "mail"), key, new MailDefaults(wf, app, "learny@brors.link"), new[] { ext }));
        var admin = await AdminLogin(p);
        var page = await (await admin.Get("/admin/mail")).Content.ReadAsStringAsync();
        check("Seite zeigt vorausgewählte App-Registrierung und Anleitung", page.Contains(app) && page.Contains("learny@brors.link") && page.Contains("Vorausgewählt") && page.Contains("Zertifikat hochladen"), null);
        check("Ohne Anmeldung gesperrt (Seite und Download)", (int)(await NewBrowser(p.Url).Get("/admin/mail")).StatusCode is 302 or 401 or 403 && (int)(await NewBrowser(p.Url).Get("/admin/mail/certificate.cer")).StatusCode is 302 or 401 or 403, null);
        check("Vor der Erzeugung: kein Download", (await admin.Get("/admin/mail/certificate.cer")).StatusCode == HttpStatusCode.NotFound, null);

        async Task<string> Post(string path, params (string, string)[] f)
        {
            var (t, _) = await PageToken(admin, "/admin/mail");
            var res = await admin.Post(path, f.Append(("__RequestVerificationToken", t)).ToArray());
            return res.Headers.Location?.ToString() ?? ((int)res.StatusCode).ToString();
        }
        (string, string)[] S(string tenant, string client, string sender, string recipients, bool enable) =>
            enable ? new[] { ("tenantId", tenant), ("clientId", client), ("sender", sender), ("recipients", recipients), ("enabled", "yes") }
                   : new[] { ("tenantId", tenant), ("clientId", client), ("sender", sender), ("recipients", recipients) };

        check("Aktivieren ohne Zertifikat → abgelehnt", (await Post("/admin/actions/mail/settings", S(wf, app, "learny@brors.link", "a@b.test", true))).EndsWith("mail_invalid"), null);
        check("Zertifikat ohne Bestätigung → nichts erzeugt", (await Post("/admin/actions/mail/certificate")).EndsWith("not_confirmed") && (await admin.Get("/admin/mail/certificate.cer")).StatusCode == HttpStatusCode.NotFound, null);
        check("Zertifikat mit Bestätigung erzeugt", (await Post("/admin/actions/mail/certificate", ("confirm", "yes"))).EndsWith("mail_cert_created"), null);

        var dl = await admin.Get("/admin/mail/certificate.cer"); var der = await dl.Content.ReadAsByteArrayAsync();
        using var cert = X509CertificateLoader.LoadCertificate(der);
        var html = await (await admin.Get("/admin/mail")).Content.ReadAsStringAsync();
        check("Download liefert ein öffentliches Zertifikat ohne privaten Schlüssel, Fingerabdruck passt zur Anzeige", dl.StatusCode == HttpStatusCode.OK && !cert.HasPrivateKey && html.Contains(cert.Thumbprint), null);
        var stored = await File.ReadAllBytesAsync(System.IO.Path.Combine(p.KeyDir, "mail", "mail-client.pfx.enc"));
        var plainPfx = X509ContentType.Unknown;
        try { plainPfx = X509Certificate2.GetCertContentType(stored); } catch (System.Security.Cryptography.CryptographicException) { }   // nicht lesbar = verschlüsselt
        check("Privater Schlüssel liegt nur verschlüsselt im Datenverzeichnis (kein lesbares PFX, kein Zertifikat im Klartext)", plainPfx == X509ContentType.Unknown && stored.AsSpan().IndexOf(der) < 0, plainPfx.ToString());
        var dbDump = string.Join("|", (await p.Db.QueryAsync("SELECT * FROM mail_settings")).SelectMany(r => r.Values));
        check("Datenbank enthält nur Metadaten, keinen Schlüssel", !dbDump.Contains("PRIVATE") && dbDump.Contains(cert.Thumbprint), null);

        check("External-ID-Tenant → abgelehnt", (await Post("/admin/actions/mail/settings", S(ext, app, "learny@brors.link", "a@b.test", false))).EndsWith("mail_invalid"), null);
        check("Ungültige GUID / Absender / Empfänger → abgelehnt", (await Post("/admin/actions/mail/settings", S("x", app, "learny@brors.link", "", false))).EndsWith("mail_invalid")
            && (await Post("/admin/actions/mail/settings", S(wf, app, "kein-mail", "", false))).EndsWith("mail_invalid")
            && (await Post("/admin/actions/mail/settings", S(wf, app, "learny@brors.link", "@", false))).EndsWith("mail_invalid"), null);
        check("Aktivieren ohne erlaubten Empfänger → abgelehnt (fail-closed)", (await Post("/admin/actions/mail/settings", S(wf, app, "learny@brors.link", "", true))).EndsWith("mail_invalid"), null);
        check("Speichern und Aktivieren mit Zertifikat und Empfänger", (await Post("/admin/actions/mail/settings", S(wf, app, "Learny@Brors.link", "a@b.test, @c.test", true))).EndsWith("mail_saved"), null);

        var svc = new MailSettingsService(p.Db, System.IO.Path.Combine(p.KeyDir, "mail"), key, new MailDefaults(), new[] { ext });
        var opt = await svc.BuildOptionsAsync();
        check("Versandoptionen: Zertifikat mit privatem Schlüssel, Absender normalisiert, Empfänger übernommen",
            opt is { Enabled: true } && opt.ClientCertificate!.HasPrivateKey && opt.SenderMailbox == "learny@brors.link" && opt.AllowedRecipients.SequenceEqual(new[] { "a@b.test", "@c.test" }) && opt.ClientId == app, null);
        var other = new MailSettingsService(p.Db, System.IO.Path.Combine(p.KeyDir, "mail"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), new MailDefaults(), null);
        check("Anderer Token-Schlüssel kann das Zertifikat nicht lesen (kein Versand)", await other.BuildOptionsAsync() is null, null);

        var mailer = new ConfigurableMailer(svc);
        var res = await mailer.SendAsync(new InvitationMail("fremd@x.test", "https://poc/invite/x", DateTime.UtcNow.AddDays(1)));
        check("Empfänger außerhalb der Liste → recipient_not_allowed (kein Netzwerkaufruf)", res.Outcome == MailOutcome.Rejected && res.Code == "recipient_not_allowed", res.Code);

        await Post("/admin/actions/mail/certificate", ("confirm", "yes"));
        var after = await mailer.SendAsync(new InvitationMail("a@b.test", "https://poc/invite/x", DateTime.UtcNow.AddDays(1)));
        check("Neues Zertifikat deaktiviert den Versand bis zur erneuten Aktivierung", await svc.BuildOptionsAsync() is null && !(await svc.GetAsync()).Enabled && after.Code == "mail_disabled", after.Code);
        var ev = await p.Db.QueryAsync("SELECT event_type, category FROM invitation_events WHERE event_type LIKE 'mail.%'");
        check("Änderungen sind protokolliert (ohne Schlüssel oder Freitext)", ev.Count >= 3 && ev.All(r => r["category"] is null or "enabled" or "disabled"), ev.Count.ToString());
        await p.App.DisposeAsync();
    }
}
