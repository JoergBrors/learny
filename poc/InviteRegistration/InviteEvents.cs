using System.Text.RegularExpressions;
using LearnCards.Web.Data;

namespace InviteRegistration;

/// <summary>Erlaubte Ereignistypen des Ereignisprotokolls (ADR 0046). Nur technisch beobachtete Ereignisse; nie Codes, Links oder Tokens.</summary>
public static class EventTypes
{
    public const string Created = "invitation.created";
    public const string SendRequested = "invitation.send_requested";
    public const string Submitted = "invitation.submitted";
    public const string SendFailed = "invitation.send_failed";
    public const string Opened = "invitation.opened";            // beobachteter Serveraufruf, kein Beweis fuer persoenliches Oeffnen (Mail-Scanner, Linkvorschau)
    public const string RegistrationStarted = "registration.started";
    public const string AuthenticationCompleted = "authentication.completed";
    public const string RegistrationRejected = "registration.rejected";
    public const string Accepted = "invitation.accepted";
    public const string UserCreated = "user.created";
    public const string ProfileAssigned = "profile.assigned";
    public const string ContentAssigned = "content.assigned";
    public const string ContentRevoked = "content.revoked";
    public const string Revoked = "invitation.revoked";
    public const string Rotated = "invitation.rotated";
    public const string UserDeactivated = "user.deactivated";
    public const string UserReactivated = "user.reactivated";
    public const string UserSignedIn = "user.signed_in";
    public const string AdminSignedIn = "admin.signed_in";
    public const string AdminRejected = "admin.login_rejected";
    public const string MailSettingsChanged = "mail.settings_changed";
    public const string MailCertificateCreated = "mail.certificate_created";
    /// <summary>Aufruf der OTP-Extension durch Entra; Kategorie = HTTP-Status:Ergebniscode (nie Code, Adresse oder Token).</summary>
    public const string OtpSend = "otp.send";
    /// <summary>Aufruf der Token-Anreicherung durch Entra; Kategorie = HTTP-Status:Ergebnis (nie Claims, IDs oder Adressen).</summary>
    public const string TokenClaims = "claims.token";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Created, SendRequested, Submitted, SendFailed, Opened, RegistrationStarted, AuthenticationCompleted, RegistrationRejected, Accepted,
        UserCreated, ProfileAssigned, ContentAssigned, ContentRevoked, Revoked, Rotated, UserDeactivated, UserReactivated, UserSignedIn, AdminSignedIn, AdminRejected, MailSettingsChanged, MailCertificateCreated, OtpSend, TokenClaims,
    };
}

public sealed record InviteEvent(string Id, string? InvitationId, string? UserId, string Type, string? Category, string? ActorUserId, DateTime At);

/// <summary>Ereignisprotokoll in der Datenbank. Erlaubt nur bekannte Typen und eine eng begrenzte Fehlerkategorie (kein Freitext), damit keine Geheimnisse hineingelangen.</summary>
public sealed class InviteEvents
{
    private static readonly Regex CategoryPattern = new("^[a-zA-Z0-9_.:-]{1,64}$", RegexOptions.Compiled);
    private readonly IDatabase _db; private readonly Func<DateTime> _clock;
    public InviteEvents(IDatabase db, Func<DateTime>? clock = null) { _db = db; _clock = clock ?? (() => DateTime.UtcNow); }

    public async Task LogAsync(string type, string? invitationId = null, string? userId = null, string? category = null, string? actorUserId = null, CancellationToken ct = default)
    {
        if (!EventTypes.All.Contains(type)) throw new ArgumentException("Unbekannter Ereignistyp.");
        if (category is not null && !CategoryPattern.IsMatch(category)) category = "invalid_category";   // nie Freitext speichern
        await _db.ExecuteAsync("INSERT INTO invitation_events (id, invitation_id, user_id, event_type, category, actor_user_id, at) VALUES (@id,@i,@u,@t,@c,@a,@now)",
            new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("i", invitationId), ("u", userId), ("t", type), ("c", category), ("a", actorUserId), ("now", _clock()) }, ct);
    }

    public async Task<List<InviteEvent>> ListAsync(string? invitationId = null, string? userId = null, int take = 200, CancellationToken ct = default)
    {
        var where = new List<string>(); var args = new List<(string, object?)>();
        if (invitationId is not null) { where.Add("invitation_id=@i"); args.Add(("i", invitationId)); }
        if (userId is not null) { where.Add("user_id=@u"); args.Add(("u", userId)); }
        var rows = await _db.QueryAsync("SELECT * FROM invitation_events" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY at DESC, id LIMIT " + Math.Clamp(take, 1, 1000), args, ct);
        return rows.Select(r => new InviteEvent(r["id"]!, r["invitation_id"], r["user_id"], r["event_type"]!, r["category"], r["actor_user_id"], DbValue.ToDateTime(r["at"]))).ToList();
    }
}
