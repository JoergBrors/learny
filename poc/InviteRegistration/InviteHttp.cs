using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnCards.Web.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InviteRegistration;

/// <summary>OIDC-Anbindung (Authorization Code + PKCE, form_post). Im PoC austauschbar; die echte Prüfung (JWKS, Issuer, nonce, exp) nutzt in der Integration Learnys OidcService.</summary>
public interface IOidcFlow
{
    string BuildAuthorizeUrl(string state, string nonce, string codeChallenge);
    /// <summary>Tauscht den Code (mit PKCE-Verifier) und liefert nur eine vollständig validierte Identität, sonst null.</summary>
    Task<VerifiedIdentity?> ExchangeAndValidateAsync(string code, string codeVerifier, string nonce, CancellationToken ct);
}

public sealed class InviteAppOptions
{
    public required string Url { get; init; }
    public required IDatabase Db { get; init; }
    public required InvitationService Invitations { get; init; }
    public required InvitationDeliveryService Delivery { get; init; }
    public required IOidcFlow Oidc { get; init; }
    /// <summary>PoC-Administratorzugang: geheimer Schlüssel → Benutzer-ID eines Administrators (Rolle wird weiterhin gegen die DB geprüft). Nicht für Produktion.</summary>
    public required string AdminKey { get; init; }
    public required string AdminUserId { get; init; }
    public Func<DateTime>? Clock { get; init; }
    /// <summary>Optional: Handler der Custom Authentication Extension (OnOtpSend). Ohne Handler wird der Endpunkt nicht bereitgestellt (404, fail-closed).</summary>
    public OtpSendHandler? OtpHandler { get; init; }
    /// <summary>Optional: Ereignisprotokoll für Öffnen/Start/Authentifizierung.</summary>
    public InviteEvents? Events { get; init; }
}

/// <summary>Flüchtige Übergabe des Klartextlinks an den Versandprozess (nur Speicher, begrenzte Lebensdauer, nie persistiert).</summary>
public sealed class LinkHandover
{
    private readonly ConcurrentDictionary<string, (string Token, string Link, DateTime Until)> _items = new();
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _ttl;
    public LinkHandover(Func<DateTime>? clock = null, TimeSpan? ttl = null) { _clock = clock ?? (() => DateTime.UtcNow); _ttl = ttl ?? TimeSpan.FromMinutes(30); }
    public void Put(string invitationId, string token, string link) => _items[invitationId] = (token, link, _clock().Add(_ttl));
    public (string Token, string Link)? Peek(string invitationId)
    {
        if (!_items.TryGetValue(invitationId, out var v)) return null;
        if (v.Until <= _clock()) { _items.TryRemove(invitationId, out _); return null; }
        return (v.Token, v.Link);
    }
    public void Remove(string invitationId) => _items.TryRemove(invitationId, out _);
}

public static class InviteApp
{
    private const string SessionCookie = "inv.sess";
    private static readonly TimeSpan CookieLife = TimeSpan.FromMinutes(10);

    public static WebApplication Create(InviteAppOptions o)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(o.Url);
        if (o.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) builder.WebHost.UseKestrelHttpsConfiguration();   // Slim-Builder: HTTPS ausdrücklich aktivieren
        builder.Logging.ClearProviders();   // keine Request-URLs (mit Token) in Logs
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        var app = builder.Build();
        var clock = o.Clock ?? (() => DateTime.UtcNow);
        var handover = new LinkHandover(clock);
        var dp = app.Services.GetRequiredService<IDataProtectionProvider>();
        var oidcProtector = dp.CreateProtector("invite.oidc.v1");
        var tokProtector = dp.CreateProtector("invite.token.v1");
        var sessProtector = dp.CreateProtector("invite.session.v1");

        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            await next();
        });

        CookieOptions Cookie(HttpContext c, string path, SameSiteMode same) => new()
        {
            HttpOnly = true, Secure = c.Request.IsHttps || !IsLoopback(c), SameSite = same, Path = path, MaxAge = CookieLife,
        };

        // ── Einladungslink, Registrierung, Anmeldung: gemeinsame Komponente (auch vom Portal verwendet) ──
        InviteFlow.Map(app, new InviteFlowOptions
        {
            Invitations = o.Invitations, Oidc = o.Oidc, DataProtection = dp, Clock = clock, Events = o.Events,
            SignInUserAsync = (c, uid) =>
            {
                c.Response.Cookies.Append(SessionCookie, sessProtector.Protect(uid + "|" + clock().Ticks), Cookie(c, "/", SameSiteMode.Lax));
                return Task.CompletedTask;
            },
        });

        // ── Custom Authentication Extension (OnOtpSend): nur mit gültigem Entra-Bearer-Token, nie frei nutzbar ──
        if (o.OtpHandler is { } otp) MapOtp(app, otp);

        // ── Geschützter Bereich: nur zugewiesene Module ──
        var access = new ContentAccess(o.Db);
        app.MapGet("/dashboard", async (HttpContext c) =>
        {
            var uid = ReadSession(c, sessProtector);
            if (uid is null) return Results.StatusCode(401);
            return Results.Json(new { modules = await access.ListReadableModulesAsync(uid, c.RequestAborted) });
        });
        app.MapGet("/modules/{id}", async (HttpContext c, string id) =>
        {
            var uid = ReadSession(c, sessProtector);
            if (uid is null) return Results.StatusCode(401);
            return await access.CanReadModuleAsync(uid, id, c.RequestAborted) ? Results.Json(new { id }) : Results.StatusCode(403);
        });

        // ── Administration (PoC-Schlüssel; Rolle wird im Dienst gegen die DB geprüft) ──
        bool IsAdmin(HttpContext c) =>
            c.Request.Headers.TryGetValue("X-Admin-Key", out var k) && k.Count == 1
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(k[0]!), Encoding.UTF8.GetBytes(o.AdminKey));

        app.MapPost("/admin/invitations", async (HttpContext c, CreateRequest r) =>
        {
            if (!IsAdmin(c)) return Results.StatusCode(401);
            try
            {
                var inv = await o.Invitations.CreateAsync(o.AdminUserId, r.Email, r.Profile, r.ModuleIds, c.RequestAborted);
                handover.Put(inv.InvitationId, inv.Token, inv.Link);   // nur im Speicher, für den bestätigten Versand
                return Results.Json(new { invitationId = inv.InvitationId, expiresAt = inv.ExpiresAt, deliveryStatus = DeliveryStatus.Pending });   // kein Link/Token in der Antwort
            }
            catch (ArgumentException) { return Results.StatusCode(400); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        });

        app.MapPost("/admin/invitations/{id}/send", async (HttpContext c, string id, SendRequest r) =>
        {
            if (!IsAdmin(c)) return Results.StatusCode(401);
            var held = handover.Peek(id);
            if (held is null) return Results.Json(new { error = "link_unavailable", hint = "rotate" }, statusCode: 409);   // kein Klartext mehr verfügbar → ausdrückliche Rotation
            var res = await o.Delivery.SendAsync(o.AdminUserId, id, held.Value.Token, held.Value.Link, r.Confirm, c.RequestAborted);
            if (res.Decision == SendDecision.Sent) handover.Remove(id);   // Klartext nach erfolgreicher Übergabe verwerfen
            var code = res.Decision switch
            {
                SendDecision.Sent => 200, SendDecision.NotConfirmed => 400, SendDecision.InProgress => 409,
                SendDecision.RequiresRotation => 409, SendDecision.NotSendable => 409, SendDecision.TooManyAttempts => 429,
                SendDecision.Failed => 502, _ => 504,
            };
            return Results.Json(new { decision = res.Decision.ToString(), code = res.Code, deliveryStatus = await o.Delivery.GetStatusAsync(id) }, statusCode: code);
        });

        app.MapPost("/admin/invitations/{id}/rotate", async (HttpContext c, string id) =>
        {
            if (!IsAdmin(c)) return Results.StatusCode(401);
            var inv = await o.Invitations.RotateAsync(o.AdminUserId, id, c.RequestAborted);
            if (inv is null) return Results.StatusCode(409);
            handover.Put(id, inv.Token, inv.Link);
            return Results.Json(new { invitationId = id, deliveryStatus = DeliveryStatus.Pending });
        });

        app.MapPost("/admin/invitations/{id}/revoke", async (HttpContext c, string id) =>
        {
            if (!IsAdmin(c)) return Results.StatusCode(401);
            handover.Remove(id);
            return await o.Invitations.RevokeAsync(o.AdminUserId, id, c.RequestAborted) ? Results.Ok() : Results.StatusCode(409);
        });

        app.MapGet("/admin/invitations/{id}", async (HttpContext c, string id) =>
        {
            if (!IsAdmin(c)) return Results.StatusCode(401);
            var info = await o.Invitations.GetInfoAsync(id, c.RequestAborted);
            return info is null ? Results.StatusCode(404)
                : Results.Json(new { info.InvitationId, info.Status, info.TargetProfile, info.ExpiresAt, deliveryStatus = await o.Delivery.GetStatusAsync(id) });
        });

        return app;
    }

    /// <summary>Der OTP-Endpunkt der Custom Authentication Extension (POST /extensions/otp-send).</summary>
    public static void MapOtp(WebApplication app, OtpSendHandler otp) =>
        app.MapPost("/extensions/otp-send", async (HttpContext c) =>
        {
            const int max = 8 * 1024;
            if (c.Request.ContentLength is > max) return Results.StatusCode(413);
            using var ms = new MemoryStream();
            var buf = new byte[1024]; int n;
            while ((n = await c.Request.Body.ReadAsync(buf, c.RequestAborted)) > 0) { ms.Write(buf, 0, n); if (ms.Length > max) return Results.StatusCode(413); }
            var res = await otp.HandleAsync(c.Request.Headers.Authorization.FirstOrDefault(), Encoding.UTF8.GetString(ms.ToArray()), c.RequestAborted);
            if (res.RetryAfter is { } ra) c.Response.Headers.RetryAfter = ((int)Math.Ceiling(ra.TotalSeconds)).ToString();
            return res.Json is null ? Results.StatusCode(res.Status) : Results.Content(res.Json, "application/json", Encoding.UTF8, res.Status);
        });

    /// <summary>Der Endpunkt der Token-Anreicherung (POST /extensions/token-claims), nur mit gültigem Entra-Bearer-Token.</summary>
    public static void MapTokenClaims(WebApplication app, TokenClaimsHandler claims) =>
        app.MapPost("/extensions/token-claims", async (HttpContext c) =>
        {
            const int max = 16 * 1024;
            if (c.Request.ContentLength is > max) return Results.StatusCode(413);
            using var ms = new MemoryStream();
            var buf = new byte[1024]; int n;
            while ((n = await c.Request.Body.ReadAsync(buf, c.RequestAborted)) > 0) { ms.Write(buf, 0, n); if (ms.Length > max) return Results.StatusCode(413); }
            var res = await claims.HandleAsync(c.Request.Headers.Authorization.FirstOrDefault(), Encoding.UTF8.GetString(ms.ToArray()), c.RequestAborted);
            return res.Json is null ? Results.StatusCode(res.Status) : Results.Content(res.Json, "application/json", Encoding.UTF8, res.Status);
        });

    /// <summary>Schlanker Host, der AUSSCHLIESSLICH den OTP-Endpunkt bereitstellt (für einen öffentlichen Tunnel): keine Einladungs-, Admin- oder Dashboard-Pfade, kein Logging von URLs.</summary>
    public static WebApplication CreateOtpOnly(string url, OtpSendHandler? otp, TokenClaimsHandler? claims = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            await next();
        });
        if (otp is not null) MapOtp(app, otp);
        if (claims is not null) MapTokenClaims(app, claims);
        return app;
    }

    public sealed record CreateRequest(string Email, string Profile, string[]? ModuleIds);
    public sealed record SendRequest(bool Confirm);

    internal static IResult Invalid() => Results.Text("Die Einladung ist ungültig oder nicht einlösbar.", "text/plain", Encoding.UTF8, 400);
    internal static IResult Failed() => Results.Redirect("/invite-failed", false, false);
    private static bool IsLoopback(HttpContext c) => c.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip);
    private static string Rand(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? ReadSession(HttpContext c, IDataProtector p)
    {
        var raw = c.Request.Cookies[SessionCookie];
        if (string.IsNullOrEmpty(raw)) return null;
        try { return p.Unprotect(raw).Split('|')[0]; } catch (Exception) { return null; }
    }
}
