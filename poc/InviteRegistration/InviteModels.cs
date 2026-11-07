namespace InviteRegistration;

/// <summary>Erlaubte Zielprofile. Administrator-, Auditor- und Themen-Admin-Rollen sind nie über Einladungen vergebbar.</summary>
public static class TargetProfiles
{
    public const string B2bLearner = "B2B_LEARNER";
    public const string B2cLearner = "B2C_LEARNER";
    public static readonly string[] All = { B2bLearner, B2cLearner };
}

public static class InvitationStatus
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
}

/// <summary>Aus einem bereits validierten ID-Token abgeleitete Identität (Signatur, Issuer, Audience, nonce, exp wurden vorher geprüft).</summary>
/// <param name="ClaimHint">Nur Diagnose: Anbieter-/Versionsangabe des Tokens in sicherer Kurzform (kein Name, keine Adresse, keine Kennung).</param>
/// <param name="ObjectId">Entra-Objekt-ID (Claim <c>oid</c>, nur wenn GUID); verknüpft später die Token-Anreicherung mit dem Portalbenutzer.</param>
/// <param name="Roles">App-Rollen aus dem Claim <c>roles</c> des validierten ID-Tokens (z. B. "Admin.All" der Learny-App im Workforce-Tenant, ADR 0012). Leer, wenn der Claim fehlt.</param>
public sealed record VerifiedIdentity(string Issuer, string Subject, string? Email, bool EmailVerified, IReadOnlyList<string>? Roles = null, string? ClaimHint = null, string? ObjectId = null);

/// <summary>Interner Grund; nach außen wird immer dieselbe Meldung gezeigt (<see cref="RedeemResult.PublicMessage"/>).</summary>
/// <summary>Für diese E-Mail-Adresse gibt es bereits ein Konto: keine neue Einladung.</summary>
public sealed class AccountExistsException : InvalidOperationException
{
    public AccountExistsException() : base("Für diese E-Mail-Adresse existiert bereits ein Konto.") { }
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

public sealed record CreatedInvitation(string InvitationId, string Link, DateTime ExpiresAt, string Token);

public sealed class InvitationOptions
{
    /// <summary>Öffentliche Basisadresse; außerhalb von localhost ist HTTPS Pflicht (ADR 0036).</summary>
    public string BaseUrl { get; init; } = "";
    /// <summary>Gültigkeit in Stunden (Standard 72, erlaubt 1–720).</summary>
    public int ValidityHours { get; init; } = 72;
    /// <summary>Schlüssel für den HMAC des Tokens (≥ 32 Byte, aus der Umgebung, nie in der Datenbank).</summary>
    public byte[] TokenKey { get; init; } = Array.Empty<byte>();
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;
}

public sealed record InvitationInfo(string InvitationId, string RecipientEmail, string TargetProfile, string Status, DateTime ExpiresAt, int TokenVersion, string? DisplayName = null);

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
