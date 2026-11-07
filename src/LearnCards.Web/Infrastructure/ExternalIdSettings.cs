using System.Text.RegularExpressions;

namespace LearnCards.Web.Infrastructure;

/// <summary>
/// Entra External ID als zweiter, getrennter Identitätsanbieter für externe Lernende (ADR 0048).
/// Standard: aus. Der Workforce-Zugang (<c>OIDC_*</c>) bleibt davon unberührt. Ist <c>EXTID_ENABLED</c> gesetzt, müssen alle Pflichtwerte
/// vorhanden und gültig sein — es gibt keinen stillen Rückfall (ADR 0026); Fehler nennen nie ein Geheimnis.
/// </summary>
public sealed class ExternalIdSettings
{
    public bool Enabled { get; init; }
    /// <summary>Discovery-Basis des External Tenants, z. B. <c>https://&lt;tenant-id&gt;.ciamlogin.com/&lt;tenant-id&gt;/v2.0</c> (der Issuer wird aus der Discovery übernommen, nie aus dieser Adresse abgeleitet).</summary>
    public string Authority { get; init; } = "";
    /// <summary>Erwartete Tenant-ID (Claim <c>tid</c>); bindet jede Identität an den konfigurierten External Tenant.</summary>
    public string TenantId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string Scope { get; init; } = "openid profile email";

    private static readonly Regex WorkforceIssuer = new(@"^https://login\.microsoftonline\.com/(?<tid>[0-9a-fA-F-]{36})/v2\.0/?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ExternalIdSettings Load(Func<string, string> env, string workforceIssuer, string workforceClientId, bool isDevelopment)
    {
        var enabled = env("EXTID_ENABLED").Trim() is "1" or "true" or "True" or "TRUE";
        var s = new ExternalIdSettings
        {
            Enabled = enabled,
            Authority = env("EXTID_AUTHORITY").Trim().TrimEnd('/'),
            TenantId = env("EXTID_TENANT_ID").Trim(),
            ClientId = env("EXTID_CLIENT_ID").Trim(),
            ClientSecret = env("EXTID_CLIENT_SECRET"),
            Scope = env("EXTID_SCOPE") is { Length: > 0 } sc ? sc.Trim() : "openid profile email",
        };
        if (enabled) s.EnsureValid(workforceIssuer, workforceClientId, isDevelopment);
        return s;
    }

    /// <summary>Prüft die Konfiguration beim Start. Beide Anbieter müssen eindeutig getrennt sein, sonst droht eine Identitäts- oder Rollenvermischung.</summary>
    public void EnsureValid(string workforceIssuer, string workforceClientId, bool isDevelopment)
    {
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var a)
            || !(a.Scheme == Uri.UriSchemeHttps || (isDevelopment && a.Scheme == Uri.UriSchemeHttp && a.IsLoopback)))
            throw new InvalidOperationException("EXTID_AUTHORITY muss eine HTTPS-Adresse sein (http nur auf Loopback in Development).");
        if (!Guid.TryParse(TenantId, out _))
            throw new InvalidOperationException("EXTID_TENANT_ID muss die Verzeichnis-ID (GUID) des External Tenants sein.");
        if (ClientId.Length == 0 || ClientId.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("EXTID_CLIENT_ID fehlt oder ist nur ein Platzhalter.");
        if (ClientSecret.Length == 0)
            throw new InvalidOperationException("EXTID_CLIENT_SECRET fehlt.");
        if (!Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid"))
            throw new InvalidOperationException("EXTID_SCOPE muss 'openid' enthalten.");

        // Trennung der Anbieter: gleiche Adresse, gleiche App oder gleicher Tenant würden beide Anmeldewege vermischen.
        if (workforceIssuer.Length > 0 && string.Equals(Authority.TrimEnd('/'), workforceIssuer.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("EXTID_AUTHORITY darf nicht dem Workforce-Issuer (OIDC_ISSUER) entsprechen.");
        if (workforceClientId.Length > 0 && string.Equals(ClientId, workforceClientId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("EXTID_CLIENT_ID darf nicht der Workforce-Client (OIDC_CLIENT_ID) sein.");
        var m = WorkforceIssuer.Match(workforceIssuer);
        if (m.Success && string.Equals(m.Groups["tid"].Value, TenantId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("EXTID_TENANT_ID darf nicht der Workforce-Tenant sein (getrennte Tenants, ADR 0048).");
    }
}
