namespace LearnCards.Web.Services;

/// <summary>Deutsche Anzeigetexte der Ereignistypen (Administrationsoberfläche). Nur Beschriftung, keine Logik.</summary>
public static class AdminEventLabels
{
    public static string Label(string type) => type switch
    {
        InviteEventTypes.Created => "Einladung erstellt",
        InviteEventTypes.SendRequested => "Versand angefordert",
        InviteEventTypes.Submitted => "Versand angenommen (nicht zwingend zugestellt)",
        InviteEventTypes.SendFailed => "Versand fehlgeschlagen oder unklar",
        InviteEventTypes.Opened => "Link aufgerufen (Serveraufruf)",
        InviteEventTypes.RegistrationStarted => "Registrierung gestartet",
        InviteEventTypes.AuthenticationCompleted => "OIDC-Anmeldung abgeschlossen",
        InviteEventTypes.RegistrationRejected => "Registrierung abgelehnt",
        InviteEventTypes.Accepted => "Einladung eingelöst",
        InviteEventTypes.UserCreated => "Benutzer angelegt",
        InviteEventTypes.ProfileAssigned => "Lernprofil vergeben",
        InviteEventTypes.ContentAssigned => "Lernrechte vergeben",
        InviteEventTypes.ContentRevoked => "Lernrechte entzogen",
        InviteEventTypes.Revoked => "Einladung widerrufen",
        InviteEventTypes.Rotated => "Einladungslink neu ausgestellt",
        _ => type,
    };

    public static string DeliveryLabel(string status) => status switch
    {
        DeliveryStatus.Pending => "nicht versendet",
        DeliveryStatus.Submitted => "angenommen (nicht zwingend zugestellt)",
        DeliveryStatus.Failed => "fehlgeschlagen",
        DeliveryStatus.Unknown => "unklar",
        _ => status,
    };

    public static string InvitationLabel(string status) => status switch
    {
        "pending" => "offen",
        "accepted" => "eingelöst",
        "revoked" => "widerrufen",
        "expired" => "abgelaufen",
        _ => status,
    };

    /// <summary>Verständlicher Text zu einem Versandfehlercode (nie ein Rohtext des Dienstes).</summary>
    public static string ErrorLabel(string? code) => code switch
    {
        null or "" => "",
        "mail_disabled" => "Mailversand ist nicht aktiviert (MAIL_ENABLED).",
        "mail_not_configured" => "Mailversand ist unvollständig konfiguriert.",
        "mail_certificate_missing" => "Für den Mailversand fehlt das Zertifikat (Administration → Mailversand → Zertifikat erzeugen und in Entra hinterlegen).",
        "wrong_tenant" => "Die Mail-App liegt im falschen Tenant.",
        "recipient_not_allowed" => "Die Empfängeradresse ist nach MAIL_ALLOWED_RECIPIENTS nicht erlaubt.",
        "invalid_message" => "Die Nachricht ist ungültig.",
        "auth_failed" or "unauthorized" => "Die Anmeldung der Mail-App bei Microsoft ist fehlgeschlagen.",
        "auth_timeout" or "auth_network" => "Microsoft war für die Anmeldung der Mail-App nicht erreichbar (nichts gesendet).",
        "permission_denied" => "Die Mail-App darf aus diesem Postfach nicht senden (Exchange-RBAC).",
        "mailbox_not_found" => "Das Absenderpostfach wurde nicht gefunden.",
        "throttled" => "Microsoft drosselt den Versand; später erneut versuchen.",
        "timeout" or "network" or "mailer_exception" => "Das Ergebnis ist unklar (Zeitüberschreitung/Netzwerk): die Mail kann trotzdem angenommen worden sein.",
        "in_progress" => "Ein Versandversuch läuft oder wurde unterbrochen; das Ergebnis ist unklar.",
        "record_failed" => "Das Ergebnis konnte nicht gespeichert werden; unklar.",
        var c when c.StartsWith("server_") => "Microsoft meldete einen Serverfehler; die Mail kann trotzdem angenommen worden sein.",
        var c when c.StartsWith("rejected_") => "Microsoft hat die Nachricht abgelehnt (" + c["rejected_".Length..] + ").",
        _ => "Fehler (" + code + ").",
    };
}
