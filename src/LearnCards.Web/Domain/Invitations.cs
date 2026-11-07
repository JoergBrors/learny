namespace LearnCards.Web.Domain;

/// <summary>
/// Lernprofile (ADR 0049). Ein Profil ist <b>keine</b> technische Rolle (<c>user_roles</c>) und kann nie administrative Rechte verleihen.
/// Kein Profileintrag = Bestandsprofil: die bisherige Learny-Berechtigungslogik (nur für Benutzer des Workforce-Anbieters).
/// </summary>
public static class LearnerProfiles
{
    public const string B2bLearner = "B2B_LEARNER";
    public const string B2cLearner = "B2C_LEARNER";
    /// <summary>Externe Lernprofile: Zugriff nur auf ausdrücklich zugewiesene Inhalte.</summary>
    public static readonly string[] External = { B2bLearner, B2cLearner };

    public static bool IsExternal(string? profile) => profile is B2bLearner or B2cLearner;

    public static string Label(string? profile) => profile switch
    {
        B2bLearner => "B2B-Lernende",
        B2cLearner => "B2C-Lernende",
        _ => "Bestandsprofil",
    };
}

/// <summary>Art einer Identität in <c>user_identities.kind</c>. Altbestand und Dev-Login sind „workforce“.</summary>
public static class IdentityKinds
{
    public const string Workforce = "workforce";
    public const string External = "external";
}

public static class InvitationStatus
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
}

public enum RedeemOutcome
{
    Success,
    MissingToken,
    UnknownToken,
    Expired,
    Revoked,
    AlreadyUsed,
    IdentityAlreadyRegistered,
    EmailNotVerified,
    EmailMismatch,
    InvalidIdentity,
    ProvisioningFailed,
}

public sealed record RedeemResult(RedeemOutcome Outcome, string? UserId = null, string? Profile = null)
{
    public bool Success => Outcome == RedeemOutcome.Success;
    /// <summary>Einheitliche Meldung für alle Fehlerfälle (keine Unterscheidung nach außen).</summary>
    public string PublicMessage => Success ? "Registrierung abgeschlossen." : "Die Einladung ist ungültig oder nicht einlösbar.";
}

/// <summary>Aus einem geprüften External-ID-Token abgeleitete Identität. <paramref name="EmailVerified"/> folgt der Vertrauensregel aus ADR 0048 (nie aus Anfragedaten).</summary>
public sealed record VerifiedExternalIdentity(string Issuer, string Subject, string? Email, bool EmailVerified);

public sealed record CreatedInvitation(string InvitationId, string Link, DateTime ExpiresAt, string Token);

public sealed record InvitationInfo(string InvitationId, string RecipientEmail, string TargetProfile, string Status, DateTime ExpiresAt, int TokenVersion, string? DisplayName = null);

/// <summary>Themenzuweisung einer Einladung (konkrete Version; Pflicht nur bei ausdrücklichem <see cref="Mandatory"/>).</summary>
public sealed record InvitationTopicGrant(string TopicId, int? Version = null, bool Mandatory = false, DateTime? DueAt = null);

/// <summary>Verwaltungsangaben und Gültigkeit einer Einladung (nur Administratoren).</summary>
public sealed record InvitationMeta(string? DisplayName = null, string? Description = null, string? Note = null, int? ValidityHours = null)
{
    public bool HasText => !string.IsNullOrWhiteSpace(DisplayName) || !string.IsNullOrWhiteSpace(Description) || !string.IsNullOrWhiteSpace(Note);

    public void Validate()
    {
        if (ValidityHours is < 1 or > 720) throw new ArgumentException("Gültigkeit muss zwischen 1 und 720 Stunden liegen.");
        Check(DisplayName, 80, "Anzeigename"); Check(Description, 300, "Beschreibung"); Check(Note, 500, "Notiz");
    }

    private static void Check(string? v, int max, string name)
    {
        if (v is null) return;
        if (v.Length > max || v.Any(c => char.IsControl(c) && c is not ('\n' or '\r'))) throw new ArgumentException($"{name} ist ungültig.");
    }
}

/// <summary>Für diese E-Mail-Adresse gibt es bereits ein Konto: keine neue Einladung.</summary>
public sealed class AccountExistsException : InvalidOperationException
{
    public AccountExistsException() : base("Für diese E-Mail-Adresse existiert bereits ein Konto.") { }
}

public sealed record InvitationFilter(string? Status = null, string? Query = null);

/// <summary>Zeile der Einladungsübersicht (Verwaltung). Nie Hash oder Token.</summary>
public sealed record InvitationRow(string InvitationId, string RecipientEmail, string TargetProfile, string Status, string DeliveryStatus, DateTime CreatedAt, DateTime ExpiresAt,
    DateTime? AcceptedAt, string? AcceptedUserId, string? DisplayName, int ModuleCount, int TopicCount);

public sealed record InvitationEventRow(string Id, string? InvitationId, string? UserId, string Type, string? Category, string? ActorUserId, DateTime At);

/// <summary>Ausdrückliche Modulzuweisung eines Benutzers (Verwaltung).</summary>
public sealed record ModuleGrantInfo(string ModuleId, string ModuleName, string Scope, DateTime GrantedAt, bool Active, DateTime? RevokedAt, string? SourceInvitationId);

/// <summary>Zusammenfassung eines Benutzers für die Lernverwaltung: Profil, Identitätsarten, Zuweisungen. Keine Rollen anderer, keine Inhalte, keine Antworten.</summary>
public sealed record LearnerOverviewInfo(string UserId, string DisplayName, string Email, string Status, string? Profile, bool HasExternalIdentity, bool HasWorkforceIdentity,
    IReadOnlyList<ModuleGrantInfo> ModuleGrants, IReadOnlyList<LearnerTopicAssignment> TopicAssignments);

public sealed record LearnerTopicAssignment(string AssignmentId, string TopicId, string TopicName, int? TopicVersion, bool Mandatory, DateTime? DueAt, string Status);
