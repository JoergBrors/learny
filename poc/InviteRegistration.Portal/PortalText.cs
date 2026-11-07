namespace InviteRegistration.Portal;

/// <summary>Deutsche Meldungen und Bezeichnungen der Oberfläche. Meldungen werden über feste Codes (nie Freitext aus der URL) ausgewählt.</summary>
public static class PortalText
{
    public static (string Text, bool Error)? Flash(string? code) => code switch
    {
        "created" => ("Einladung erstellt. Sie ist noch nicht versendet: Bitte den Versand unten ausdrücklich bestätigen.", false),
        "sent" => ("Versandauftrag wurde von Exchange Online angenommen. Das ist noch keine Bestätigung der Zustellung.", false),
        "send_failed" => ("Versand fehlgeschlagen. Die Einladung ist unverändert gültig; der Versand kann erneut bestätigt werden.", true),
        "send_unknown" => ("Das Versandergebnis ist unklar (Zeitüberschreitung oder Serverfehler). Die Einladung ist unverändert gültig.", true),
        "in_progress" => ("Ein Versandversuch läuft bereits oder wartet auf die vom Dienst geforderte Wartezeit.", true),
        "requires_rotation" => ("Der Link wurde bereits versendet. Ein erneuter Versand braucht eine Neuausstellung (der alte Link wird ungültig).", true),
        "too_many" => ("Zu viele Versandversuche für diesen Link. Bitte den Link neu ausstellen.", true),
        "not_sendable" => ("Diese Einladung kann nicht versendet werden (abgelaufen, widerrufen, eingelöst oder Link nicht mehr aktuell).", true),
        "link_unavailable" => ("Der Einladungslink ist nicht mehr im Speicher (Neustart oder Zeitablauf). Bitte die Neuausstellung bestätigen; der alte Link wird dabei ungültig.", true),
        "rotated" => ("Link neu ausgestellt. Der alte Link ist ungültig. Bitte den Versand bestätigen.", false),
        "not_rotatable" => ("Neuausstellung nicht möglich (Einladung nicht mehr offen).", true),
        "revoked" => ("Einladung widerrufen. Der Link ist ungültig.", false),
        "not_revocable" => ("Widerruf nicht möglich (bereits eingelöst oder widerrufen).", true),
        "not_confirmed" => ("Bitte die Aktion ausdrücklich bestätigen.", true),
        "invalid" => ("Eingabe ungültig. Bitte E-Mail-Adresse, Profil, Gültigkeit (1–720 Stunden) und Module prüfen.", true),
        "account_exists" => ("Für diese E-Mail-Adresse existiert bereits ein Konto. Eine Einladung ist nicht nötig; die Person meldet sich direkt an. Unter „Benutzer“ lassen sich Rechte ändern oder das Konto deaktivieren.", true),
        "assigned" => ("Lernberechtigung zugewiesen.", false),
        "revoked_content" => ("Lernberechtigung entzogen. Der Zugriff ist sofort gesperrt.", false),
        "deactivated" => ("Benutzer deaktiviert. Er ist sofort ausgesperrt.", false),
        "reactivated" => ("Benutzer reaktiviert.", false),
        "mail_saved" => ("Mail-Einrichtung gespeichert.", false),
        "mail_invalid" => ("Eingabe ungültig: Tenant-ID und Client-ID müssen GUIDs sein (nicht der External-ID-Tenant), Absender und Empfänger gültige Adressen. Aktivieren geht nur mit Zertifikat und mindestens einem erlaubten Empfänger.", true),
        "mail_cert_created" => ("Neues Zertifikat erzeugt. Der Versand ist deaktiviert, bis das Zertifikat in Entra hinterlegt und die Einrichtung erneut aktiviert wurde.", false),
        "mail_unavailable" => ("Die Mail-Einrichtung ist in dieser Instanz nicht verfügbar.", true),
        _ => null,
    };

    public static string Profile(string p) => p switch { "B2C_LEARNER" => "B2C-Lernende", "B2B_LEARNER" => "B2B-Lernende", _ => p };

    public static string InvitationStatus(string s) => s switch { "pending" => "offen", "accepted" => "eingelöst", "expired" => "abgelaufen", "revoked" => "widerrufen", _ => s };
    public static string DeliveryStatus(string s) => s switch { "pending" => "nicht versendet", "submitted" => "angenommen", "failed" => "fehlgeschlagen", "unknown" => "unklar", _ => s };
    public static string UserStatus(string s) => s == "active" ? "aktiv" : "deaktiviert";
    public static string Scope(string s) => s switch { "global" => "global", "shared" => "freigegeben", _ => s };

    public static string Badge(string kind, string value) => (kind, value) switch
    {
        ("inv", "accepted") or ("del", "submitted") or ("usr", "active") => "badge ok",
        ("inv", "expired") or ("inv", "revoked") or ("del", "failed") or ("del", "unknown") or ("usr", "disabled") => "badge bad",
        ("inv", "pending") => "badge warn",
        _ => "badge",
    };

    /// <summary>Lesbare Erklärung der technischen Kategorie eines Ereignisses (Rohwert bleibt in Klammern sichtbar).</summary>
    public static string Category(string? c) => c switch
    {
        null => "",
        { } o when o.StartsWith("oidc:") => "Anmeldung bei Entra konnte nicht geprüft werden: " + o["oidc:".Length..] switch
        {
            "iss" => "Aussteller des Tokens passt nicht (iss) – bei Claims-Mapping-Policy ändert sich oft der Aussteller bzw. der Signaturschlüssel",
            "kid_unknown" => "Signaturschlüssel des Tokens ist in den veröffentlichten Schlüsseln nicht enthalten (kid_unknown)",
            "signature" => "Signatur des Tokens ungültig (signature)",
            "aud" => "Zielgruppe des Tokens passt nicht (aud)",
            "nonce" => "Nonce passt nicht (nonce)",
            "tid" => "Tenant passt nicht (tid)",
            var x when x.StartsWith("token_http_400_aadsts501461") => "Entra lehnt das Token ab, weil bei aktiver Claims-Mapping-Policy die Zielgruppe die App selbst sein muss (AADSTS501461) – Scope <client-id>/.default",
            var x when x.StartsWith("token_http_") => "Token-Endpunkt von Entra lehnte den Code ab (" + x + ")",
            var x => x,
        },
        { } h when h.StartsWith("EmailNotVerified:") => PortalText.Category("EmailNotVerified") + " – Token: " + h["EmailNotVerified:".Length..],
        "IdentityAlreadyRegistered" => "Für diese Anmeldung existiert bereits ein Konto – Registrierung nicht nötig, bitte direkt anmelden (IdentityAlreadyRegistered)",
        "EmailMismatch" => "Die bei Entra verwendete E-Mail-Adresse passt nicht zur eingeladenen (EmailMismatch)",
        "EmailNotVerified" => "E-Mail-Adresse gilt nicht als bestätigt: die Anmeldung kam nicht über den E-Mail-Einmalcode, z. B. bei einem direkt bei Entra angelegten Konto – bitte das Konto in Entra prüfen (EmailNotVerified)",
        "AlreadyUsed" => "Einladung wurde bereits eingelöst (AlreadyUsed)",
        "Expired" => "Einladung abgelaufen (Expired)",
        "Revoked" => "Einladung widerrufen (Revoked)",
        "UnknownToken" => "Einladungslink unbekannt oder durch Neuausstellung ungültig (UnknownToken)",
        "MissingToken" => "Einladungslink fehlt (MissingToken)",
        "InvalidIdentity" => "Anmeldeergebnis unvollständig (InvalidIdentity)",
        "ProvisioningFailed" => "Konto konnte nicht angelegt werden (ProvisioningFailed)",
        "oidc_failed" => "Technischer Fehler bei der Anmeldung (oidc_failed)",
        "not_allowed" => "Keine Administratorberechtigung (not_allowed)",
        "enabled" => "Versand aktiviert", "disabled" => "Versand deaktiviert",
        "mail_disabled" => "Mailversand nicht aktiviert (mail_disabled)",
        "recipient_not_allowed" => "Empfänger nicht erlaubt (recipient_not_allowed)",
        "permission_denied" => "Exchange-RBAC für das Postfach fehlt (permission_denied)",
        "auth_failed" => "Anmeldung der Mail-App fehlgeschlagen (auth_failed)",
        _ => c,
    };

    public static string Event(string t) => t switch
    {
        EventTypes.Created => "Einladung erstellt",
        EventTypes.SendRequested => "Versandauftrag erteilt",
        EventTypes.Submitted => "Versandauftrag angenommen",
        EventTypes.SendFailed => "Versand fehlgeschlagen",
        EventTypes.Opened => "Einladungslink aufgerufen (beobachteter Serveraufruf)",
        EventTypes.RegistrationStarted => "Registrierung gestartet (Weiterleitung zu Entra)",
        EventTypes.AuthenticationCompleted => "OIDC-Anmeldung abgeschlossen",
        EventTypes.RegistrationRejected => "Registrierung abgewiesen",
        EventTypes.Accepted => "Einladung eingelöst",
        EventTypes.UserCreated => "Benutzer angelegt",
        EventTypes.ProfileAssigned => "Lernprofil zugewiesen",
        EventTypes.ContentAssigned => "Lernberechtigung zugewiesen",
        EventTypes.ContentRevoked => "Lernberechtigung entzogen",
        EventTypes.Revoked => "Einladung widerrufen",
        EventTypes.Rotated => "Link neu ausgestellt",
        EventTypes.UserDeactivated => "Benutzer deaktiviert",
        EventTypes.UserReactivated => "Benutzer reaktiviert",
        EventTypes.UserSignedIn => "Benutzer angemeldet",
        EventTypes.AdminSignedIn => "Administrator angemeldet",
        EventTypes.AdminRejected => "Administratoranmeldung abgewiesen",
        EventTypes.MailSettingsChanged => "Mail-Einrichtung geändert",
        EventTypes.MailCertificateCreated => "Mail-Zertifikat erzeugt",
        EventTypes.TokenClaims => "Token-Anreicherung durch Entra (Claims)",
        EventTypes.OtpSend => "Anmeldecode-Anfrage von Entra (OTP-Extension)",
        _ => t,
    };
}
