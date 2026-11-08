using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Services.Mail;

namespace LearnCards.Web.Services;

public static class DeliveryStatus
{
    public const string Pending = "pending";
    public const string Submitted = "submitted";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
}

public enum SendDecision
{
    Sent,                   // Graph hat angenommen (submitted) — keine Zustellgarantie
    NotConfirmed,           // Administratorbestätigung fehlt
    NotSendable,            // Einladung nicht mehr pending/abgelaufen, Link nicht aktuell oder während des Versands erneuert
    InProgress,             // paralleler Versandversuch läuft (Lease) oder Wartezeit (Retry-After) nicht abgelaufen
    RequiresRotation,       // bereits submitted: neuer Versand nur über ausdrückliche Neuausstellung des Links
    RequiresAcknowledgement,// Ergebnis eines früheren Versuchs unklar: erneuter Versand nur mit ausdrücklicher Bestätigung
    TooManyAttempts,
    Failed,                 // eindeutig nicht versendet
    Unknown,                // Ergebnis unklar
}

public sealed record SendResult(SendDecision Decision, string Code = "", TimeSpan? RetryAfter = null);

/// <summary>Versandzustand einer Einladung (getrennt vom Einladungsstatus). Nie ein Link oder Token.</summary>
public sealed record DeliveryInfo(string InvitationId, string Status, int Attempts, string? LastErrorCode, DateTime? LastAttemptAt, DateTime? SubmittedAt, DateTime? LeaseUntil);

/// <summary>
/// Versandzustand je Einladung, getrennt vom Einladungsstatus (ADR 0044/0049). Entscheidet nur, ob gesendet werden darf; der Transport (<see cref="IInvitationMailer"/>) kennt keine
/// Einladungslogik. Ein Versandfehler verändert die Einladung nie (sie wird nicht verbraucht). Ablauf: Administratoraktion mit Bestätigung → atomarer Anspruch auf den Versandslot
/// (Zustand wird <b>vor</b> dem Aufruf von Graph auf <c>unknown</c> gesetzt: bricht der Prozess mitten im Versand ab, bleibt „unklar“ stehen, nie „nicht gesendet“) → Versand → Ergebnis,
/// Ereignis und Audit in einer Transaktion. Mehrinstanzbetrieb: Slot und Lease liegen in der Datenbank. Eine erfolgreiche Graph-Antwort (HTTP 202) heißt nur <c>submitted</c> (angenommen),
/// nicht „zugestellt“. Der Link wird nur aus dem Token im Speicher des Aufrufers gebildet und nie gespeichert, protokolliert oder auditiert.
/// </summary>
public sealed class InvitationDeliveryService
{
    private readonly IDatabase _db;
    private readonly UserService _users;
    private readonly InvitationService _invitations;
    private readonly IInvitationMailer _mailer;
    private readonly InviteEvents _events;
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _lease;
    private readonly int _maxAttempts;

    public InvitationDeliveryService(IDatabase db, UserService users, InvitationService invitations, IInvitationMailer mailer, InviteEvents events,
        Func<DateTime>? clock = null, int leaseSeconds = 60, int maxAttempts = 5)
    {
        _db = db; _users = users; _invitations = invitations; _mailer = mailer; _events = events;
        _clock = clock ?? (() => invitations.Clock());
        _lease = TimeSpan.FromSeconds(leaseSeconds);
        _maxAttempts = maxAttempts;
    }

    public async Task<DeliveryInfo?> GetAsync(string? actorUserId, string invitationId, CancellationToken ct = default)
    {
        await _users.RequireRoleAsync(actorUserId, Roles.Admin);
        return await ReadAsync(invitationId, ct);
    }

    private async Task<DeliveryInfo?> ReadAsync(string invitationId, CancellationToken ct)
    {
        if (!UserService.IsGuid(invitationId)) return null;
        var rows = await _db.QueryAsync("SELECT * FROM invitation_deliveries WHERE invitation_id=@i", new (string, object?)[] { ("i", invitationId) }, ct);
        if (rows.Count == 0) return null;
        var r = rows[0];
        return new DeliveryInfo(invitationId, r["status"]!, DbValue.ToInt(r["attempts"]), r.GetValueOrDefault("last_error_code"),
            r["last_attempt_at"] is null ? null : DbValue.ToDateTime(r["last_attempt_at"]), r["submitted_at"] is null ? null : DbValue.ToDateTime(r["submitted_at"]),
            r["lease_until"] is null ? null : DbValue.ToDateTime(r["lease_until"]));
    }

    /// <param name="token">Klartext-Token, nur aus dem Speicher des Aufrufers (Erstellung oder Neuausstellung); nie aus der Datenbank.</param>
    /// <param name="confirmed">Ausdrückliche Bestätigung der Administratoraktion; ohne sie wird nichts gesendet.</param>
    /// <param name="acknowledgeUnknown">Nur nötig, wenn der letzte Versuch „unklar“ endete: die Mail könnte bereits angenommen worden sein.</param>
    public async Task<SendResult> SendAsync(string? actorUserId, string invitationId, string token, bool confirmed, bool acknowledgeUnknown = false, CancellationToken ct = default)
    {
        var actor = await _users.RequireRoleAsync(actorUserId, Roles.Admin);
        if (!confirmed) return new(SendDecision.NotConfirmed);

        var info = await _invitations.GetInfoAsync(invitationId, ct);
        if (info is null || info.Status != InvitationStatus.Pending || string.IsNullOrEmpty(token) || !await _invitations.IsCurrentTokenAsync(invitationId, token, ct))
            return new(SendDecision.NotSendable);
        var link = _invitations.LinkFor(token);

        // Atomarer Versandslot (nur ein Versuch gleichzeitig, auch über Instanzen hinweg). Der Zustand wird sofort auf „unknown“ gesetzt: ein Abbruch mitten im Versand
        // hinterlässt „unklar“ und nie ein wiederholbares „pending“. submitted wird nie still wiederholt.
        var now = _clock();
        var allowed = acknowledgeUnknown ? "('pending','failed','unknown')" : "('pending','failed')";
        var claim = new DbStatement($"""
            UPDATE invitation_deliveries SET status='unknown', last_error_code='in_progress', lease_until=@lease, attempts=attempts+1, last_attempt_at=@now, updated_at=@now
            WHERE invitation_id=@i AND status IN {allowed} AND attempts<@max AND (lease_until IS NULL OR lease_until<=@now)
              AND EXISTS (SELECT 1 FROM invitations v WHERE v.invitation_id=@i AND v.token_version=@tv AND v.status='pending')
            """, new (string, object?)[] { ("i", invitationId), ("lease", now.Add(_lease)), ("now", now), ("max", _maxAttempts), ("tv", info.TokenVersion) }, 1);
        try
        {
            await _db.ExecuteBatchAsync(new[] { claim, _events.Statement(InviteEventTypes.SendRequested, invitationId, null, acknowledgeUnknown ? "retry_acknowledged" : null, actor.Id) }, ct);
        }
        catch (DbBatchConflictException ex) when (ex.Index == 0)
        {
            var d = await ReadAsync(invitationId, ct);
            if (d is null) return new(SendDecision.NotSendable);
            if (d.Status == DeliveryStatus.Submitted) return new(SendDecision.RequiresRotation);
            if (d.Attempts >= _maxAttempts) return new(SendDecision.TooManyAttempts);
            if (d.LeaseUntil is DateTime until && until > now) return new(SendDecision.InProgress, d.LastErrorCode ?? "", until - now);
            if (d.Status == DeliveryStatus.Unknown && !acknowledgeUnknown) return new(SendDecision.RequiresAcknowledgement, d.LastErrorCode ?? "");
            return new(SendDecision.NotSendable);
        }

        MailSendResult result;
        try { result = await _mailer.SendAsync(new InvitationMail(info.RecipientEmail, link, info.ExpiresAt, info.DisplayName), ct); }
        catch (Exception) { result = new(MailOutcome.Ambiguous, "mailer_exception"); }

        var status = result.Outcome switch
        {
            MailOutcome.Accepted => DeliveryStatus.Submitted,
            MailOutcome.Rejected => DeliveryStatus.Failed,
            _ => DeliveryStatus.Unknown,
        };
        var code = result.Outcome == MailOutcome.Accepted ? null : result.Code;
        // Retry-After (z. B. HTTP 429): Der Slot bleibt bis dahin gesperrt, es gibt keine frühere Wiederholung. Sonst endet das Lease sofort.
        DateTime? until2 = result.RetryAfter is { } ra && result.Outcome != MailOutcome.Accepted ? _clock().Add(ra) : null;
        var done = _clock();
        var stmts = new List<DbStatement>
        {
            // Ergebnis nur schreiben, solange es unser Versuch ist (Token nicht inzwischen erneuert, Zustand noch „unknown“ aus dem Anspruch).
            new("""
                UPDATE invitation_deliveries SET status=@st, lease_until=@until, last_error_code=@code,
                       submitted_at=CASE WHEN @st='submitted' THEN @now ELSE submitted_at END, updated_at=@now
                WHERE invitation_id=@i AND status='unknown' AND last_error_code='in_progress'
                  AND EXISTS (SELECT 1 FROM invitations v WHERE v.invitation_id=@i AND v.token_version=@tv)
                """, new (string, object?)[] { ("i", invitationId), ("st", status), ("until", until2), ("code", code), ("now", done), ("tv", info.TokenVersion) }, 1),
            _events.Statement(result.Outcome == MailOutcome.Accepted ? InviteEventTypes.Submitted : InviteEventTypes.SendFailed, invitationId, null,
                result.Outcome switch { MailOutcome.Accepted => null, MailOutcome.Rejected => result.Code, _ => "unknown:" + result.Code }, actor.Id),
            _invitations.AuditStatement(actor.Id, "invitation.send", invitationId, $"status={status} code={result.Code}"),   // nie der Link
        };
        try { await _db.ExecuteBatchAsync(stmts, CancellationToken.None); }
        catch (DbBatchConflictException) { return new(SendDecision.NotSendable, "superseded"); }   // Link wurde während des Versands erneuert; der Zustand gehört zum neuen Link
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(SendDecision.Unknown, "record_failed"); }   // Zustand bleibt „unknown“: nie ein stilles Wiederholen

        return result.Outcome switch
        {
            MailOutcome.Accepted => new(SendDecision.Sent, result.Code),
            MailOutcome.Rejected => new(SendDecision.Failed, result.Code, result.RetryAfter),
            _ => new(SendDecision.Unknown, result.Code, result.RetryAfter),
        };
    }
}
