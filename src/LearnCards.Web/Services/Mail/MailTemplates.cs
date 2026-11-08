using System.Net;

namespace LearnCards.Web.Services.Mail;

/// <summary>Learny-E-Mailvorlage für Entra-Einmalcodes. Keine externen Bilder, keine Links, kein Tracking; Markenfarben aus wwwroot/app.css (Brand-700 mit weißer Schrift, Kontrast ≥ 4,5:1).</summary>
public static class OtpMailTemplate
{
    public const string Subject = "Ihr Learny-Anmeldecode";
    public const string BrandColor = "#196F67";   // --brand-700
    public const string PaperColor = "#F7F5F0";   // --bg
    public const string InkColor = "#29323C";     // --ink

    /// <summary>Entra erzeugt numerische Codes (live beobachtet: 8 Ziffern); erlaubt sind 4–16 alphanumerische Zeichen, damit nie Markup oder Steuerzeichen in die Mail gelangen.</summary>
    public static bool IsValidCode(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length is >= 4 and <= 16 && code.All(char.IsAsciiLetterOrDigit);

    public static string BuildText(string code, int validityMinutes = 30) =>
        "Hallo,\n\nSie haben einen Anmeldecode für Learny angefordert.\n\nIhr Anmeldecode lautet:\n\n" + code +
        $"\n\nGeben Sie diesen Code auf der Learny-Anmeldeseite ein. Der Code ist {validityMinutes} Minuten gültig.\n\n" +
        "Wenn Sie keinen Code angefordert haben, können Sie diese Nachricht ignorieren.\n\nIhr Learny-Team";

    public static string BuildHtml(string code, int validityMinutes = 30)
    {
        var c = WebUtility.HtmlEncode(code);
        return $"""
            <!DOCTYPE html>
            <html lang="de"><head><meta charset="utf-8"><title>{Subject}</title></head>
            <body style="margin:0;padding:0;background:{PaperColor};">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:{PaperColor};"><tr><td align="center" style="padding:24px 12px;">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:480px;background:#FFFFFF;border:1px solid #E4E0D6;border-radius:12px;">
                <tr><td style="background:{BrandColor};color:#FFFFFF;padding:16px 24px;font-family:'Segoe UI',Arial,sans-serif;font-size:20px;font-weight:700;border-radius:12px 12px 0 0;">Learny</td></tr>
                <tr><td style="padding:24px;font-family:'Segoe UI',Arial,sans-serif;font-size:16px;line-height:1.5;color:{InkColor};">
                  <p style="margin:0 0 12px;">Hallo,</p>
                  <p style="margin:0 0 16px;">Sie haben einen Anmeldecode für Learny angefordert.</p>
                  <p style="margin:0 0 8px;">Ihr Anmeldecode lautet:</p>
                  <p style="margin:0 0 16px;padding:12px 16px;background:{PaperColor};border-radius:8px;font-family:Consolas,'Courier New',monospace;font-size:28px;font-weight:700;letter-spacing:4px;color:{InkColor};text-align:center;">{c}</p>
                  <p style="margin:0 0 16px;">Geben Sie diesen Code auf der Learny-Anmeldeseite ein. Der Code ist {validityMinutes} Minuten gültig.</p>
                  <p style="margin:0 0 16px;">Wenn Sie keinen Code angefordert haben, können Sie diese Nachricht ignorieren.</p>
                  <p style="margin:0;">Ihr Learny-Team</p>
                </td></tr>
              </table>
            </td></tr></table>
            </body></html>
            """;
    }
}

/// <summary>
/// Learny-Einladungsmail (HTML; Graph kennt nur einen Body-Typ je Nachricht). Enthält den persönlichen Einmal-Link, die Gültigkeit und einen Sicherheitshinweis; keine Bilder, kein Tracking.
/// Der Link ist die einzige Verknüpfung und wird nur hier im Klartext verwendet (nie gespeichert).
/// </summary>
public static class InvitationMailTemplate
{
    public const string Subject = "Einladung zu Learny";

    public static string BuildHtml(InvitationMail m)
    {
        var name = string.IsNullOrWhiteSpace(m.DisplayName) ? null : WebUtility.HtmlEncode(m.DisplayName.Trim());
        var link = WebUtility.HtmlEncode(m.Link);
        var until = m.ExpiresAt.ToUniversalTime().ToString("dd.MM.yyyy HH:mm") + " UTC";
        return $"""
            <!DOCTYPE html>
            <html lang="de"><head><meta charset="utf-8"><title>{Subject}</title></head>
            <body style="margin:0;padding:0;background:{OtpMailTemplate.PaperColor};">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:{OtpMailTemplate.PaperColor};"><tr><td align="center" style="padding:24px 12px;">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:520px;background:#FFFFFF;border:1px solid #E4E0D6;border-radius:12px;">
                <tr><td style="background:{OtpMailTemplate.BrandColor};color:#FFFFFF;padding:16px 24px;font-family:'Segoe UI',Arial,sans-serif;font-size:20px;font-weight:700;border-radius:12px 12px 0 0;">Learny</td></tr>
                <tr><td style="padding:24px;font-family:'Segoe UI',Arial,sans-serif;font-size:16px;line-height:1.5;color:{OtpMailTemplate.InkColor};">
                  <p style="margin:0 0 12px;">{(name is null ? "Hallo," : "Hallo " + name + ",")}</p>
                  <p style="margin:0 0 16px;">Sie wurden zu Learny eingeladen. Über Ihren persönlichen Link können Sie sich einmalig registrieren.</p>
                  <p style="margin:0 0 16px;"><a href="{link}" style="display:inline-block;background:{OtpMailTemplate.BrandColor};color:#FFFFFF;text-decoration:none;padding:10px 18px;border-radius:8px;font-weight:600;">Jetzt registrieren</a></p>
                  <p style="margin:0 0 16px;font-size:14px;">Falls die Schaltfläche nicht funktioniert, kopieren Sie diese Adresse in Ihren Browser:<br><span style="word-break:break-all;">{link}</span></p>
                  <p style="margin:0 0 16px;">Der Link ist bis {until} gültig und kann nur einmal verwendet werden.</p>
                  <p style="margin:0 0 16px;font-size:14px;"><b>Sicherheitshinweis:</b> Geben Sie den Link nicht weiter. Learny fragt Sie nie per E-Mail nach Passwörtern oder Anmeldecodes. Wenn Sie diese Einladung nicht erwartet haben, ignorieren Sie diese Nachricht.</p>
                  <p style="margin:0;">Ihr Learny-Team</p>
                </td></tr>
              </table>
            </td></tr></table>
            </body></html>
            """;
    }
}


/// <summary>Learny-Mail zu einer zugewiesenen Schulung bzw. Erinnerung an eine Pflichtschulung (HTML; Graph kennt nur einen Body-Typ je Nachricht). Phase C: keine automatische Anbindung.</summary>
public static class TrainingMailTemplate
{
    public static string Subject(TrainingMail m) => m.Reminder ? "Erinnerung: Pflichtschulung in Learny" : "Neue Schulung in Learny";

    public static string BuildHtml(TrainingMail m)
    {
        var name = string.IsNullOrWhiteSpace(m.DisplayName) ? null : WebUtility.HtmlEncode(m.DisplayName.Trim());
        var title = WebUtility.HtmlEncode(m.Title);
        var url = WebUtility.HtmlEncode(m.AppUrl);
        var due = m.DueAt is { } d ? "<p style=\"margin:0 0 16px;\">Fällig bis " + d.ToUniversalTime().ToString("dd.MM.yyyy") + ".</p>" : "";
        var lead = m.Reminder
            ? "Die folgende Pflichtschulung ist noch nicht abgeschlossen:"
            : m.Mandatory ? "Ihnen wurde eine Pflichtschulung zugewiesen:" : "Ihnen wurde eine Schulung zugewiesen:";
        return $"""
            <!DOCTYPE html>
            <html lang="de"><head><meta charset="utf-8"><title>{WebUtility.HtmlEncode(Subject(m))}</title></head>
            <body style="margin:0;padding:0;background:{OtpMailTemplate.PaperColor};">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:{OtpMailTemplate.PaperColor};"><tr><td align="center" style="padding:24px 12px;">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:520px;background:#FFFFFF;border:1px solid #E4E0D6;border-radius:12px;">
                <tr><td style="background:{OtpMailTemplate.BrandColor};color:#FFFFFF;padding:16px 24px;font-family:'Segoe UI',Arial,sans-serif;font-size:20px;font-weight:700;border-radius:12px 12px 0 0;">Learny</td></tr>
                <tr><td style="padding:24px;font-family:'Segoe UI',Arial,sans-serif;font-size:16px;line-height:1.5;color:{OtpMailTemplate.InkColor};">
                  <p style="margin:0 0 12px;">{(name is null ? "Hallo," : "Hallo " + name + ",")}</p>
                  <p style="margin:0 0 8px;">{lead}</p>
                  <p style="margin:0 0 16px;font-weight:700;">{title}</p>
                  {due}
                  <p style="margin:0 0 16px;"><a href="{url}" style="display:inline-block;background:{OtpMailTemplate.BrandColor};color:#FFFFFF;text-decoration:none;padding:10px 18px;border-radius:8px;font-weight:600;">Zu Learny</a></p>
                  <p style="margin:0;">Ihr Learny-Team</p>
                </td></tr>
              </table>
            </td></tr></table>
            </body></html>
            """;
    }
}
