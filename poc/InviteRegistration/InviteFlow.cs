using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace InviteRegistration;

public sealed record OidcCtx(string State, string Nonce, string Verifier, string Token, long Ticks);

/// <summary>
/// OIDC-Hin- und Rückweg mit Korrelationscookie (state, nonce, PKCE-Verifier; SameSite=None wegen form_post, ADR 0036). Wird für die Registrierung/Anmeldung der Lernenden
/// und für die Administratoranmeldung verwendet. Das Cookie ist einmalig (wird beim Rücksprung gelöscht) und 10 Minuten gültig.
/// </summary>
public static class OidcCookieFlow
{
    public static readonly TimeSpan Life = TimeSpan.FromMinutes(10);

    public static string Begin(HttpContext c, IOidcFlow flow, IDataProtector p, string cookieName, string cookiePath, string extra, Func<DateTime> clock)
    {
        var state = Rand(); var nonce = Rand(); var verifier = Rand(48);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var payload = JsonSerializer.Serialize(new OidcCtx(state, nonce, verifier, extra, clock().Ticks));
        c.Response.Cookies.Append(cookieName, p.Protect(payload), new CookieOptions
        { HttpOnly = true, Secure = true, SameSite = SameSiteMode.None, Path = cookiePath, MaxAge = Life });   // SameSite=None verlangt Secure; Browser erlauben es auch auf http://localhost
        return flow.BuildAuthorizeUrl(state, nonce, challenge);
    }

    /// <returns>Validierte Identität und der beim Start mitgegebene Zusatzwert; bei jedem Fehler (Identity = null) ohne Unterscheidung.</returns>
    public static async Task<(VerifiedIdentity? Identity, string? Extra)> CompleteAsync(HttpContext c, IOidcFlow flow, IDataProtector p, string cookieName, string cookiePath, Func<DateTime> clock)
    {
        var raw = c.Request.Cookies[cookieName];
        c.Response.Cookies.Delete(cookieName, new CookieOptions { Path = cookiePath, Secure = true, SameSite = SameSiteMode.None });   // einmalig
        if (!c.Request.HasFormContentType || c.Request.ContentLength is null or > 32 * 1024) return (null, null);
        var form = await c.Request.ReadFormAsync(c.RequestAborted);
        if (string.IsNullOrEmpty(raw) || form["code"].Count != 1 || form["state"].Count != 1 || form.ContainsKey("error")) return (null, null);
        OidcCtx? oc;
        try { oc = JsonSerializer.Deserialize<OidcCtx>(p.Unprotect(raw)); } catch (Exception) { return (null, null); }
        if (oc is null || clock() - new DateTime(oc.Ticks, DateTimeKind.Utc) > Life) return (null, null);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(form["state"]!), Encoding.UTF8.GetBytes(oc.State))) return (null, null);
        var identity = await flow.ExchangeAndValidateAsync(form["code"]!, oc.Verifier, oc.Nonce, c.RequestAborted);
        return (identity, oc.Token);
    }

    private static string Rand(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool IsLoopback(HttpContext c) => c.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip);
}

public sealed class InviteFlowOptions
{
    public required InvitationService Invitations { get; init; }
    public required IOidcFlow Oidc { get; init; }
    public required IDataProtectionProvider DataProtection { get; init; }
    /// <summary>Wird nach erfolgreicher Einlösung/Anmeldung mit der internen Benutzer-ID aufgerufen (setzt die Sitzung des Hosts).</summary>
    public required Func<HttpContext, string, Task> SignInUserAsync { get; init; }
    public Func<DateTime>? Clock { get; init; }
    public InviteEvents? Events { get; init; }
    public string SuccessPath { get; init; } = "/dashboard";
    /// <summary>Optional: eigene (z. B. gebrandete) Fehlerseite statt des Klartexts. Muss für alle Fehlerfälle gleich bleiben (keine Unterscheidung nach außen).</summary>
    public Func<IResult>? InvalidPage { get; init; }
}

/// <summary>
/// Einladungslink, Registrierung und Anmeldung der Lernenden (ADR 0042/0036): <c>GET /invite/{token}</c> prüft nur (verbraucht nie), <c>GET /invite-start</c> leitet zu Entra,
/// <c>GET /login</c> startet die Anmeldung bereits registrierter Benutzer, <c>POST /auth/callback</c> (form_post) löst ein oder meldet an. Jeder Fehler endet einheitlich auf <c>/invite-failed</c>.
/// </summary>
public static class InviteFlow
{
    private const string OidcCookie = "inv.oidc";
    private const string TokenCookie = "inv.tok";

    public static void Map(WebApplication app, InviteFlowOptions o)
    {
        var clock = o.Clock ?? (() => DateTime.UtcNow);
        var oidcProtector = o.DataProtection.CreateProtector("invite.oidc.v1");
        var tokProtector = o.DataProtection.CreateProtector("invite.token.v1");
        IResult Invalid() => o.InvalidPage?.Invoke() ?? InviteApp.Invalid();
        Task Ev(string type, string? inv = null, string? user = null, string? cat = null) => o.Events is null ? Task.CompletedTask : o.Events.LogAsync(type, inv, user, cat);

        CookieOptions Cookie(HttpContext c, string path, SameSiteMode same) => new()
        { HttpOnly = true, Secure = c.Request.IsHttps || !IsLoopback(c), SameSite = same, Path = path, MaxAge = OidcCookieFlow.Life };

        // Einladungslink: nur prüfen, nie senden, nie verbrauchen. "Geöffnet" ist nur ein beobachteter Serveraufruf (Mail-Scanner/Linkvorschau möglich).
        app.MapGet("/invite/{token}", async (HttpContext c, string token) =>
        {
            if (!await o.Invitations.IsRedeemableAsync(token, c.RequestAborted)) return Invalid();
            await Ev(EventTypes.Opened, await o.Invitations.GetInvitationIdAsync(token, c.RequestAborted));
            // Token verlässt die URL sofort: geschütztes Cookie, dann Weiterleitung auf eine token-lose Adresse.
            c.Response.Cookies.Append(TokenCookie, tokProtector.Protect(token + "|" + clock().Ticks), Cookie(c, "/", SameSiteMode.Lax));
            return Results.Redirect("/invite-start", false, false);
        });

        app.MapGet("/invite-start", async (HttpContext c) =>
        {
            var token = ReadToken(c, tokProtector, clock);
            c.Response.Cookies.Delete(TokenCookie, new CookieOptions { Path = "/" });
            if (token is null || !await o.Invitations.IsRedeemableAsync(token, c.RequestAborted)) return Invalid();
            await Ev(EventTypes.RegistrationStarted, await o.Invitations.GetInvitationIdAsync(token, c.RequestAborted));
            // Das Token reist im SameSite=None-Cookie bis zum Callback (Cross-Site-POST von Entra); nie im state oder in URLs.
            return Results.Redirect(OidcCookieFlow.Begin(c, o.Oidc, oidcProtector, OidcCookie, "/auth/callback", token, clock), false, false);
        });

        // Anmeldung bereits registrierter Benutzer (ohne Einladung; unbekannte Identitäten werden abgewiesen).
        app.MapGet("/login", (HttpContext c) =>
            Results.Redirect(OidcCookieFlow.Begin(c, o.Oidc, oidcProtector, OidcCookie, "/auth/callback", "", clock), false, false));

        app.MapPost("/auth/callback", async (HttpContext c) =>
        {
            var (identity, token) = await OidcCookieFlow.CompleteAsync(c, o.Oidc, oidcProtector, OidcCookie, "/auth/callback", clock);
            if (identity is null) return InviteApp.Failed();

            if (string.IsNullOrEmpty(token))
            {
                var uid = await o.Invitations.FindActiveUserByIdentityAsync(identity.Issuer, identity.Subject, c.RequestAborted);
                if (uid is null) { await Ev(EventTypes.RegistrationRejected, null, null, "unknown_identity"); return InviteApp.Failed(); }
                if (identity.ObjectId is not null) await o.Invitations.RecordObjectIdAsync(uid, identity.ObjectId, c.RequestAborted);
                await Ev(EventTypes.UserSignedIn, null, uid);
                await o.SignInUserAsync(c, uid);
                return Results.Redirect(o.SuccessPath, false, false);
            }

            var invitationId = await o.Invitations.GetInvitationIdAsync(token, c.RequestAborted);
            await Ev(EventTypes.AuthenticationCompleted, invitationId);
            var result = await o.Invitations.RedeemAsync(token, identity, c.RequestAborted);
            if (!result.Success) return InviteApp.Failed();   // keine Differenzierung nach außen
            await o.SignInUserAsync(c, result.UserId!);
            return Results.Redirect(o.SuccessPath, false, false);
        });

        app.MapGet("/invite-failed", () => Invalid());
    }

    private static string? ReadToken(HttpContext c, IDataProtector p, Func<DateTime> clock)
    {
        var raw = c.Request.Cookies[TokenCookie];
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var parts = p.Unprotect(raw).Split('|');
            return parts.Length == 2 && clock() - new DateTime(long.Parse(parts[1]), DateTimeKind.Utc) <= OidcCookieFlow.Life ? parts[0] : null;
        }
        catch (Exception) { return null; }
    }

    private static bool IsLoopback(HttpContext c) => c.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip);
}
