using LearnCards.Web.Data;

namespace InviteRegistration;

public static class DeliveryStatus
{
    public const string Pending = "pending";
    public const string Submitted = "submitted";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
}

public enum SendDecision
{
    Sent,            // Graph hat angenommen (submitted)
    NotConfirmed,    // Administratorbestätigung fehlt
    NotSendable,     // Einladung nicht mehr pending/abgelaufen oder Link nicht aktuell
    InProgress,      // paralleler Versandversuch läuft (Lease)
    RequiresRotation,// bereits submitted: neuer Versand nur über ausdrückliche Rotation
    TooManyAttempts,
    Failed,          // eindeutig nicht versendet
    Unknown,         // Ergebnis unklar
}

public sealed record SendResult(SendDecision Decision, string Code = "", TimeSpan? RetryAfter = null);

/// <summary>
/// Versandzustand je Einladung, getrennt vom Einladungsstatus (ADR 0044). Entscheidet nur, ob gesendet werden darf;
/// der Transport (<see cref="IInvitationMailer"/>) kennt keine Einladungslogik. Ein Versandfehler verändert die Einladung nie.
/// </summary>
public sealed class InvitationDeliveryService
{
    private readonly IDatabase _db;
    private readonly InvitationService _invitations;
    private readonly IInvitationMailer _mailer;
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _lease;
    private readonly int _maxAttempts;

    /// <summary>Optionales Ereignisprotokoll (ADR 0046).</summary>
    public InviteEvents? Events { get; set; }
    private Task Ev(string type, string invitationId, string? category = null, string? actor = null) =>
        Events is null ? Task.CompletedTask : Events.LogAsync(type, invitationId, null, category, actor);

    public InvitationDeliveryService(IDatabase db, InvitationService invitations, IInvitationMailer mailer,
        Func<DateTime>? clock = null, int leaseSeconds = 60, int maxAttempts = 5)
    {
        _db = db; _invitations = invitations; _mailer = mailer;
        _clock = clock ?? (() => DateTime.UtcNow);
        _lease = TimeSpan.FromSeconds(leaseSeconds);
        _maxAttempts = maxAttempts;
    }

    public async Task<string?> GetStatusAsync(string invitationId, CancellationToken ct = default)
    {
        var rows = await _db.QueryAsync("SELECT status FROM invitation_deliveries WHERE invitation_id=@i", new (string, object?)[] { ("i", invitationId) }, ct);
        return rows.Count == 0 ? null : rows[0]["status"];
    }

    /// <param name="link">Klartextlink, nur aus dem Speicher des Versandprozesses (nie aus der Datenbank).</param>
    public async Task<SendResult> SendAsync(string actorUserId, string invitationId, string token, string link, bool confirmed, CancellationToken ct = default)
    {
        await _invitations.RequireAdminAsync(actorUserId, ct);
        if (!confirmed) return new(SendDecision.NotConfirmed);

        var info = await _invitations.GetInfoAsync(invitationId, ct);
        if (info is null || info.Status != InvitationStatus.Pending || !await _invitations.IsCurrentTokenAsync(invitationId, token, ct))
            return new(SendDecision.NotSendable);

        // Atomarer Versandslot: nur ein Versuch gleichzeitig; submitted/unklar-Altlasten werden nicht still wiederholt.
        var now = _clock();
        var claimed = await _db.ExecuteAsync("""
            UPDATE invitation_deliveries SET lease_until=@lease, attempts=attempts+1, last_attempt_at=@now, updated_at=@now
            WHERE invitation_id=@i AND status IN ('pending','failed','unknown') AND attempts<@max AND (lease_until IS NULL OR lease_until<=@now)
            """, new (string, object?)[] { ("i", invitationId), ("lease", now.Add(_lease)), ("now", now), ("max", _maxAttempts) }, ct);
        if (claimed != 1)
        {
            var d = (await _db.QueryAsync("SELECT status, attempts, lease_until FROM invitation_deliveries WHERE invitation_id=@i", new (string, object?)[] { ("i", invitationId) }, ct)).FirstOrDefault();
            if (d is null) return new(SendDecision.NotSendable);
            if (d["status"] == DeliveryStatus.Submitted) return new(SendDecision.RequiresRotation);
            if (DbValue.ToInt(d["attempts"]) >= _maxAttempts) return new(SendDecision.TooManyAttempts);
            return new(SendDecision.InProgress);
        }

        await Ev(EventTypes.SendRequested, invitationId, null, actorUserId);
        MailSendResult result;
        try { result = await _mailer.SendAsync(new InvitationMail(info.RecipientEmail, link, info.ExpiresAt, info.DisplayName), ct); }
        catch (Exception) { result = new(MailOutcome.Ambiguous, "mailer_exception"); }

        var status = result.Outcome switch
        {
            MailOutcome.Accepted => DeliveryStatus.Submitted,
            MailOutcome.Rejected => DeliveryStatus.Failed,
            _ => DeliveryStatus.Unknown,
        };
        // Retry-After (z. B. HTTP 429): Der Versandslot bleibt bis dahin gesperrt, es gibt keine frühere Wiederholung.
        DateTime? until = result.RetryAfter is { } ra && result.Outcome != MailOutcome.Accepted ? _clock().Add(ra) : null;
        await _db.ExecuteAsync("""
            UPDATE invitation_deliveries SET status=@st, lease_until=@until, last_error_code=@code,
                   submitted_at=CASE WHEN @st='submitted' THEN @now ELSE submitted_at END, updated_at=@now
            WHERE invitation_id=@i
            """, new (string, object?)[] { ("i", invitationId), ("st", status), ("code", result.Outcome == MailOutcome.Accepted ? null : result.Code), ("now", _clock()), ("until", until) }, ct);
        await _db.ExecuteAsync("INSERT INTO invitation_audit (id, invitation_id, event, reason, at) VALUES (@id,@i,'mail_send',@r,@now)",
            new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("i", invitationId), ("r", status + ":" + result.Code), ("now", _clock()) }, ct);

        await Ev(result.Outcome == MailOutcome.Accepted ? EventTypes.Submitted : EventTypes.SendFailed, invitationId, result.Outcome == MailOutcome.Accepted ? null : result.Code, actorUserId);

        return result.Outcome switch
        {
            MailOutcome.Accepted => new(SendDecision.Sent, result.Code),
            MailOutcome.Rejected => new(SendDecision.Failed, result.Code, result.RetryAfter),
            _ => new(SendDecision.Unknown, result.Code, result.RetryAfter),
        };
    }
}
