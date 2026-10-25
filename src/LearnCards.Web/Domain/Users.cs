using System.Security.Claims;

namespace LearnCards.Web.Domain;

/// <summary>Rollen (ADR 0011). Jeder Benutzer besitzt immer "user"; weitere Rollen vergibt nur ein Administrator.</summary>
public static class Roles
{
    public const string User = "user";
    public const string Admin = "admin";
    /// <summary>Global Learny Auditor: Leserechte auf globale Inhalte/Ergebnisse (ab Freigabe-/Global-Funktion), keine Änderungsrechte.</summary>
    public const string Auditor = "auditor";

    /// <summary>Themen-Admin: verwaltet Themenzuweisungen und sieht den Lernstatus (ADR 0029); keine Einsicht in fremde private Inhalte, keine Veröffentlichung, keine Benutzerverwaltung.</summary>
    public const string TopicAdmin = "themen_admin";

    public static readonly string[] All = { User, Admin, Auditor, TopicAdmin };
}

/// <summary>Anwendungsbenutzer mit stabiler interner ID (unabhängig vom Identity Provider).</summary>
public class AppUser
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    /// <summary>"active" | "disabled"</summary>
    public string Status { get; set; } = "active";
    public List<string> Roles { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime LastLoginAt { get; set; }

    public bool IsActive => Status == "active";
    public bool IsAdmin => Roles.Contains(Domain.Roles.Admin);
    public bool IsAuditor => Roles.Contains(Domain.Roles.Auditor);
    public bool IsTopicAdmin => Roles.Contains(Domain.Roles.TopicAdmin);
}

/// <summary>Zugeordnete externe Identität (z. B. Entra-ID-Subject).</summary>
public class UserIdentityInfo
{
    public string Provider { get; set; } = "";
    public string Subject { get; set; } = "";
}

/// <summary>Claim-Zugriff: die interne Benutzer-ID liegt im Claim "uid" (wird serverseitig gesetzt).</summary>
public static class UserClaims
{
    public const string UserId = "uid";

    public static string? GetUserId(this ClaimsPrincipal? principal) =>
        principal?.FindFirst(UserId)?.Value is { Length: > 0 } v ? v : null;
}

/// <summary>Zusammenfassung des eigenen Bestands für den Profilbereich.</summary>
public class OwnedContentSummary
{
    public int Modules { get; set; }
    public int ActiveCards { get; set; }
    public int ArchivedCards { get; set; }
}

public class AuditEntry
{
    public string Id { get; set; } = "";
    public string? ActorUserId { get; set; }
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
