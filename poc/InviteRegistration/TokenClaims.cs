using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnCards.Web.Data;

namespace InviteRegistration;

public sealed class TokenClaimsOptions
{
    /// <summary>Client-ID der Portal-App, für deren Token Claims geliefert werden. Aufrufe für andere Apps erhalten leere Claims.</summary>
    public required string ExpectedClientAppId { get; init; }
    /// <summary>Mehr als so viele Module werden nicht einzeln aufgeführt (Tokengröße); dann steht nur die Anzahl im Token.</summary>
    public int MaxModules { get; init; } = 20;
    public int MaxBodyBytes { get; init; } = 16 * 1024;
    public Func<string, string, Task>? Audit { get; init; }
}

/// <summary>Das, was aus der Datenbank in den Token übernommen wird.</summary>
public sealed record LearnerClaims(string UserId, string Profile, string Status, IReadOnlyList<string> Modules, string GrantsVersion);

/// <summary>
/// Handler der Custom Authentication Extension „OnTokenIssuanceStart“ (Custom Claims Provider): ergänzt das Token um <c>learny_*</c>-Claims.
/// Die Claims sind nur ein <b>Hinweis</b> (Übersicht/Cache); die Zugriffsentscheidung bleibt bei <see cref="ContentAccess"/> gegen die Datenbank.
/// Nie ein Fehler für die Anmeldung: unbekannter Benutzer, Fehler oder fremde App → leere Claims (200), sonst bräche Entra die Anmeldung ab.
/// Enthält nie Name, E-Mail, Einladungsdaten, Notizen oder Administratorrollen.
/// </summary>
public sealed class TokenClaimsHandler
{
    private const string EventType = "microsoft.graph.authenticationEvent.tokenIssuanceStart";
    private readonly ExtensionTokenValidator _tokens; private readonly IDatabase _db; private readonly ContentAccess _access; private readonly TokenClaimsOptions _o;

    public TokenClaimsHandler(ExtensionTokenValidator tokens, IDatabase db, ContentAccess access, TokenClaimsOptions options)
    { _tokens = tokens; _db = db; _access = access; _o = options; }

    public async Task<OtpHttpResult> HandleAsync(string? authorization, string body, CancellationToken ct)
    {
        if (body.Length > _o.MaxBodyBytes) return await Done(413, "too_large");
        if (!await _tokens.ValidateAsync(authorization, ct)) return await Done(401, "unauthorized");

        string? oid = null, mail = null, appId = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() != EventType) return await Done(400, "bad_schema");
            var ctx = root.GetProperty("data").GetProperty("authenticationContext");
            if (ctx.TryGetProperty("user", out var u))
            {
                oid = Str(u, "id"); mail = Str(u, "mail");
            }
            if (ctx.TryGetProperty("clientServicePrincipal", out var sp)) appId = Str(sp, "appId");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return await Done(400, "bad_schema"); }

        if (!string.Equals(appId, _o.ExpectedClientAppId, StringComparison.OrdinalIgnoreCase)) return await Done(200, "other_app", Claims(null));

        LearnerClaims? learner = null;
        try { learner = await ResolveAsync(oid, mail, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return await Done(200, "lookup_failed", Claims(null)); }
        return await Done(200, learner is null ? "unknown_user" : "claims", Claims(learner));
    }

    /// <summary>Benutzer über die Entra-Objekt-ID (bei Registrierung/Anmeldung festgehalten), sonst über die verifizierte Adresse des Tenants.</summary>
    public async Task<LearnerClaims?> ResolveAsync(string? entraObjectId, string? mail, CancellationToken ct)
    {
        string? uid = null;
        if (Guid.TryParse(entraObjectId, out var g))
        {
            var rows = await _db.QueryAsync("SELECT user_id FROM user_entra_oids WHERE oid=@o", new (string, object?)[] { ("o", g.ToString()) }, ct);
            if (rows.Count > 0) uid = rows[0]["user_id"];
        }
        if (uid is null && InvitationService.NormalizeEmail(mail) is { } m)
        {
            var rows = await _db.QueryAsync("""
                SELECT u.id AS id FROM users u JOIN user_profiles p ON p.user_id=u.id JOIN user_identities i ON i.user_id=u.id
                WHERE LOWER(u.email)=@m GROUP BY u.id
                """, new (string, object?)[] { ("m", m) }, ct);
            if (rows.Count == 1) uid = rows[0]["id"];
        }
        return uid is null ? null : await BuildAsync(uid, ct);
    }

    public async Task<LearnerClaims?> BuildAsync(string userId, CancellationToken ct)
    {
        var rows = await _db.QueryAsync("SELECT u.status AS s, p.profile AS p FROM users u JOIN user_profiles p ON p.user_id=u.id WHERE u.id=@u", new (string, object?)[] { ("u", userId) }, ct);
        if (rows.Count == 0) return null;
        var status = rows[0]["s"]!; var profile = rows[0]["p"]!;
        var modules = status == "active" ? await _access.ListReadableModulesAsync(userId, ct) : new List<string>();
        return new LearnerClaims(userId, profile, status, modules, Version(profile, status, modules));
    }

    /// <summary>Änderungsmarke der Berechtigungen: ändert sich bei jeder Änderung von Profil, Status oder Modulen (Hash, kein Zähler, keine Schemaänderung).</summary>
    public static string Version(string profile, string status, IEnumerable<string> modules) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile + "|" + status + "|" + string.Join(",", modules.OrderBy(x => x, StringComparer.Ordinal)))))[..12].ToLowerInvariant();

    /// <summary>Entra kennt für diese Claims nur Text und Text-Listen (max. 3 KB insgesamt); andere Typen lassen die Tokenausstellung scheitern.</summary>
    private Dictionary<string, object> Claims(LearnerClaims? l)
    {
        var c = new Dictionary<string, object>();
        if (l is null) return c;
        c["learny_uid"] = l.UserId; c["learny_profile"] = l.Profile; c["learny_status"] = l.Status;
        c["learny_modules_n"] = l.Modules.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);   // nur String und String-Array werden unterstützt (keine Zahl, kein Boolean, kein JSON)
        if (l.Modules.Count <= _o.MaxModules) c["learny_modules"] = l.Modules.ToArray();
        c["learny_grants_v"] = l.GrantsVersion;
        return c;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task<OtpHttpResult> Done(int status, string code, Dictionary<string, object>? claims = null)
    {
        if (_o.Audit is not null) await _o.Audit("token_claims", $"{status}:{code}");
        if (claims is null) return new OtpHttpResult(status);
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["data"] = new Dictionary<string, object>
            {
                ["@odata.type"] = "microsoft.graph.onTokenIssuanceStartResponseData",
                ["actions"] = new object[] { new Dictionary<string, object> { ["@odata.type"] = "microsoft.graph.tokenIssuanceStart.provideClaimsForToken", ["claims"] = claims } },
            },
        });
        return new OtpHttpResult(status, json);
    }
}
