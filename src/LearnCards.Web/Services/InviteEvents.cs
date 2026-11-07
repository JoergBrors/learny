using System.Text.RegularExpressions;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>Erlaubte Ereignistypen des Ereignisprotokolls (ADR 0046/0049). Nur technisch beobachtete Ereignisse; nie Codes, Links, Tokens oder Freitext.</summary>
public static class InviteEventTypes
{
    public const string Created = "invitation.created";
    public const string SendRequested = "invitation.send_requested";
    public const string Submitted = "invitation.submitted";
    public const string SendFailed = "invitation.send_failed";
    /// <summary>Beobachteter Serveraufruf des Links — kein Beweis für persönliches Öffnen (Mail-Scanner, Linkvorschau).</summary>
    public const string Opened = "invitation.opened";
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

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Created, SendRequested, Submitted, SendFailed, Opened, RegistrationStarted, AuthenticationCompleted, RegistrationRejected, Accepted,
        UserCreated, ProfileAssigned, ContentAssigned, ContentRevoked, Revoked, Rotated,
    };
}

/// <summary>
/// Ereignisprotokoll der Einladungen und Zuweisungen (Tabelle <c>invitation_events</c>). Erlaubt nur bekannte Typen und eine eng begrenzte Kategorie
/// (kein Freitext), damit keine Geheimnisse hineingelangen. Ereignisse unbeobachteter Schritte im Entra-User-Flow werden nie erfunden.
/// </summary>
public sealed class InviteEvents
{
    private static readonly Regex CategoryPattern = new("^[a-zA-Z0-9_.:-]{1,64}$", RegexOptions.Compiled);
    private readonly IDatabase _db;
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public InviteEvents(IDatabase db) => _db = db;

    private static string? Safe(string? category) => category is not null && !CategoryPattern.IsMatch(category) ? "invalid_category" : category;

    /// <summary>Anweisung zum Schreiben eines Ereignisses — für atomare Gruppen (<see cref="IDatabase.ExecuteBatchAsync"/>).</summary>
    public DbStatement Statement(string type, string? invitationId = null, string? userId = null, string? category = null, string? actorUserId = null)
    {
        if (!InviteEventTypes.All.Contains(type)) throw new ArgumentException("Unbekannter Ereignistyp.");
        return new DbStatement("INSERT INTO invitation_events (id, invitation_id, user_id, event_type, category, actor_user_id, at) VALUES (@id,@i,@u,@t,@c,@a,@now)",
            new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("i", invitationId), ("u", userId), ("t", type), ("c", Safe(category)), ("a", actorUserId), ("now", Clock()) });
    }

    public async Task LogAsync(string type, string? invitationId = null, string? userId = null, string? category = null, string? actorUserId = null, CancellationToken ct = default)
    {
        var s = Statement(type, invitationId, userId, category, actorUserId);
        await _db.ExecuteAsync(s.Sql, s.Args, ct);
    }

    public async Task<List<InvitationEventRow>> ListAsync(string? invitationId = null, string? userId = null, int take = 200, CancellationToken ct = default)
    {
        var where = new List<string>(); var args = new List<(string, object?)>();
        if (invitationId is not null) { where.Add("invitation_id=@i"); args.Add(("i", invitationId)); }
        if (userId is not null) { where.Add("user_id=@u"); args.Add(("u", userId)); }
        var rows = await _db.QueryAsync("SELECT * FROM invitation_events" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY at DESC, id LIMIT " + Math.Clamp(take, 1, 1000), args, ct);
        return rows.Select(r => new InvitationEventRow(r["id"]!, r.GetValueOrDefault("invitation_id"), r.GetValueOrDefault("user_id"), r["event_type"]!, r.GetValueOrDefault("category"),
            r.GetValueOrDefault("actor_user_id"), DbValue.ToDateTime(r["at"]))).ToList();
    }
}
