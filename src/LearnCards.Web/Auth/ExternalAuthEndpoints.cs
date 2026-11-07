using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;

namespace LearnCards.Web.Auth;

/// <summary>Vertrauensregel „E-Mail bestätigt“ für Entra External ID (ADR 0048). Gilt nur im ausdrücklich getesteten E-Mail-Einmalcode-Kontext des konfigurierten Tenants (der Tenant <c>tid</c>, die Audience und der Issuer sind vorher geprüft).</summary>
public static class ExternalIdentityPolicy
{
    private static string? Str(IReadOnlyDictionary<string, JsonElement> c, string name) => c.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary><c>idp = mail</c> (Anmeldung per Einmalcode), Tokenversion 2.0 und eine E-Mail-Adresse. <c>idp = mail</c> allein ist kein allgemeiner Nachweis; lokale Konten und föderierte Anbieter gelten nie als bestätigt.</summary>
    public static bool IsEmailTrusted(IReadOnlyDictionary<string, JsonElement> claims) =>
        Str(claims, "idp") == "mail" && Str(claims, "ver") == "2.0" && !string.IsNullOrWhiteSpace(Str(claims, "email"));

    public static string? Email(IReadOnlyDictionary<string, JsonElement> claims) => Str(claims, "email");
}

/// <summary>
/// Anmeldung und Registrierung über Entra External ID (ADR 0048/0049): <c>/auth/external/login</c>, <c>GET /invite/{token}</c> und <c>POST /auth/external/callback</c> (form_post, ADR 0036).
/// Eigener Pfad, eigenes Korrelationscookie, eigene Discovery und Client-ID; der Workforce-Ablauf (<c>/auth/login</c>) bleibt unverändert.
/// Regeln: Bekannte externe Identitäten melden sich an (mit gültigem externen Profil, sonst fail-closed). Neue Benutzer entstehen <b>ausschließlich</b> durch die atomare
/// Einlösung einer gültigen Einladung; der Einladungstoken reist nur im verschlüsselten, einmaligen Korrelationscookie (nie in URL oder state-Parameter), die Einlösung erfolgt
/// erst nach vollständig geprüftem ID-Token. Rollen aus dem Token werden nie gelesen; es gibt keine Verknüpfung über die E-Mail-Adresse; Fehlertexte nennen keine Einzelheiten.
/// Ist <c>EXTID_ENABLED</c> aus, existieren die Pfade nicht.
/// </summary>
public static class ExternalAuthEndpoints
{
    private static readonly Regex TokenShape = new("^[A-Za-z0-9_-]{20,128}$", RegexOptions.Compiled);

    public static void MapExternalAuthEndpoints(this WebApplication app)
    {
        var cfg = app.Services.GetRequiredService<AppConfig>();
        if (!cfg.ExternalId.Enabled) return;

        // Gemeinsamer Start: state/nonce/PKCE in das verschlüsselte, einmalige Korrelationscookie; der Einladungstoken (falls vorhanden) liegt nur dort.
        static async Task<IResult> StartAsync(HttpContext http, AppConfig cfg, ExternalOidcService oidc, IDataProtectionProvider dp, string returnUrl, string? inviteToken)
        {
            var redirectUri = AuthEndpoints.BuildRedirectUri(http, cfg, OidcCallback.ExternalCallbackPath);
            if (redirectUri is null) return Results.Redirect("/login?error=HTTPS+f%C3%BCr+die+Anmeldung+erforderlich");
            try
            {
                var state = OidcService.CreateState(returnUrl) with { InviteToken = inviteToken };
                var protector = dp.CreateProtector("oidc-state-external").ToTimeLimitedDataProtector();
                http.Response.Cookies.Append(OidcCallback.ExternalStateCookie, protector.Protect(JsonSerializer.Serialize(state), OidcCallback.StateLifetime),
                    OidcCallback.StateCookieOptions(OidcCallback.ExternalCallbackPath));
                // prompt=login: eine bestehende Entra-Sitzung ersetzt nie die frische Anmeldung (ADR 0042/0046).
                return Results.Redirect(await oidc.BuildAuthorizeUrlAsync(state, redirectUri) + "&prompt=login");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
            {
                return Results.Redirect("/login?error=Anmeldung+derzeit+nicht+verf%C3%BCgbar");
            }
        }

        // Anmeldung bereits registrierter externer Lernender (ohne Einladung)
        app.MapGet("/auth/external/login", (HttpContext http, ExternalOidcService oidc, IDataProtectionProvider dp, string? returnUrl) =>
            StartAsync(http, cfg, oidc, dp, AuthEndpoints.SafeReturnUrl(returnUrl), null)).AllowAnonymous();

        // Einladungslink: nur Vorprüfung (verbraucht nichts), dann Weiterleitung zu Entra. Der Token steht nur in diesem Pfad und im verschlüsselten Cookie.
        app.MapGet("/invite/{token}", async (HttpContext http, string token, ExternalOidcService oidc, IDataProtectionProvider dp, InvitationService invites, InviteEvents events) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            const string invalid = "/login?error=Die+Einladung+ist+ung%C3%BCltig%2C+abgelaufen+oder+wurde+bereits+verwendet";
            if (!TokenShape.IsMatch(token)) return Results.Redirect(invalid);
            var invitationId = await invites.GetInvitationIdAsync(token);
            if (!await invites.IsRedeemableAsync(token))
            {
                await events.LogAsync(InviteEventTypes.RegistrationRejected, invitationId, null, await invites.PreflightFailureAsync(token));
                return Results.Redirect(invalid);
            }
            await events.LogAsync(InviteEventTypes.Opened, invitationId);   // beobachteter Serveraufruf (Scanner/Linkvorschau möglich)
            await events.LogAsync(InviteEventTypes.RegistrationStarted, invitationId);
            return await StartAsync(http, cfg, oidc, dp, "/", token);
        }).AllowAnonymous();

        app.MapGet(OidcCallback.ExternalCallbackPath, (HttpContext http) =>
            AuthEndpoints.NoStoreRedirect(http, "/login?error=Anmeldung+bitte+erneut+starten")).AllowAnonymous();

        app.MapPost(OidcCallback.ExternalCallbackPath, async (HttpContext http, ExternalOidcService oidc, UserService users, CardRepository repo, InvitationService invites, InviteEvents events,
            OidcStateStore states, IDataProtectionProvider dp) =>
        {
            var sizeLimit = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (sizeLimit is { IsReadOnly: false }) sizeLimit.MaxRequestBodySize = OidcCallback.MaxBodyBytes;

            static IResult Fail(HttpContext h, string text) => AuthEndpoints.NoStoreRedirect(h, "/login?error=" + Uri.EscapeDataString(text));
            const string inviteInvalid = "Die Einladung ist ungültig oder nicht einlösbar.";

            // Korrelationscookie bei jedem Ausgang entfernen (einmalige Nutzung)
            var raw = http.Request.Cookies[OidcCallback.ExternalStateCookie];
            http.Response.Cookies.Delete(OidcCallback.ExternalStateCookie, OidcCallback.StateCookieOptions(OidcCallback.ExternalCallbackPath));

            if (!http.Request.HasFormContentType) return Fail(http, "Antwort des Identity-Providers ungültig");
            OidcCallback.Form? form;
            try { form = OidcCallback.ParseForm(await http.Request.ReadFormAsync()).Form; }
            catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException or IOException) { form = null; }
            if (form is null) return Fail(http, "Antwort des Identity-Providers ungültig");
            if (form.Error is not null) return Fail(http, OidcCallback.SafeErrorText(form.Error));
            if (raw is null) return Fail(http, "Login-Sitzung abgelaufen");

            OidcService.AuthState authState;
            try
            {
                var protector = dp.CreateProtector("oidc-state-external").ToTimeLimitedDataProtector();
                authState = JsonSerializer.Deserialize<OidcService.AuthState>(protector.Unprotect(raw))!;
            }
            catch { return Fail(http, "Login-Sitzung ungültig"); }

            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(authState.State), Encoding.UTF8.GetBytes(form.State!)))
                return Fail(http, "State-Parameter stimmt nicht");
            // Einmaligkeit über alle Instanzen (Datenbank), nicht nur im Prozess
            if (!await states.TryConsumeAsync(authState.State)) return Fail(http, "Login-Sitzung bereits verwendet");

            try
            {
                var redirectUri = AuthEndpoints.BuildRedirectUri(http, cfg, OidcCallback.ExternalCallbackPath);
                if (redirectUri is null) return Fail(http, "HTTPS für die Anmeldung erforderlich");
                var (idToken, _) = await oidc.ExchangeCodeAsync(form.Code!, authState.CodeVerifier, redirectUri);
                var claims = await oidc.ValidateIdTokenAsync(idToken, authState.Nonce);   // Signatur, Issuer (Discovery), Audience, tid, exp, nbf, nonce
                var sub = claims.TryGetValue("sub", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                if (string.IsNullOrWhiteSpace(sub)) return Fail(http, "Anmeldung nicht möglich");

                // Identität = (Issuer aus der Discovery, sub); nie die E-Mail. Rollen des Tokens werden bewusst nicht gelesen.
                var issuer = (await oidc.GetDiscoveryAsync()).Issuer;
                var inviteToken = authState.InviteToken;
                var invitationId = inviteToken is null ? null : await invites.GetInvitationIdAsync(inviteToken);
                if (inviteToken is not null) await events.LogAsync(InviteEventTypes.AuthenticationCompleted, invitationId);   // OIDC-Anmeldung abgeschlossen (beobachtet)

                AppUser? user;
                if (await users.HasIdentityAsync(issuer, sub))
                {
                    if (inviteToken is not null)   // bereits registriert: eine Einladung ist nie eine zweite Registrierung
                    {
                        await events.LogAsync(InviteEventTypes.RegistrationRejected, invitationId, null, RedeemOutcome.IdentityAlreadyRegistered.ToString());
                        return Fail(http, inviteInvalid);
                    }
                    user = await users.SignInKnownIdentityAsync(issuer, sub);
                    if (user is null) return Fail(http, "Anmeldung nicht möglich");
                    if (!user.IsActive) return Fail(http, "Zugang gesperrt");
                    // fail-closed: nur Identitäten der Art „external“ mit gültigem externen Profil melden sich hier an
                    if (await users.GetIdentityKindAsync(issuer, sub) != IdentityKinds.External || !LearnerProfiles.IsExternal(await repo.GetLearnerProfileAsync(user.Id)))
                    {
                        await users.WriteAuditAsync(null, "login.external.rejected", "", "reason=no_valid_profile");
                        return Fail(http, "Das Konto ist nicht vollständig eingerichtet. Bitte an den Administrator wenden.");
                    }
                }
                else
                {
                    if (inviteToken is null)
                    {
                        await users.WriteAuditAsync(null, "login.external.rejected", "", "reason=unknown_identity");   // ohne Subject, E-Mail oder Token
                        return Fail(http, "Für dieses Konto liegt keine Einladung vor. Die Registrierung ist nur mit gültiger Einladung möglich.");
                    }
                    var identity = new VerifiedExternalIdentity(issuer, sub, ExternalIdentityPolicy.Email(claims), ExternalIdentityPolicy.IsEmailTrusted(claims));
                    var result = await invites.RedeemAsync(inviteToken, identity);   // atomar: Anspruch, Benutzer, Identität, Profil, Zuweisungen
                    if (!result.Success) return Fail(http, result.PublicMessage);
                    user = await users.GetAsync(result.UserId);
                    if (user is null || !user.IsActive) return Fail(http, "Anmeldung nicht möglich");
                }

                await AuthEndpoints.SignInAsync(http, user, sub);
                return AuthEndpoints.NoStoreRedirect(http, authState.ReturnUrl);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException or KeyNotFoundException or FormatException or TaskCanceledException)
            {
                return Fail(http, "Anmeldung nicht möglich");   // keine Details des Providers oder der Prüfung nach außen
            }
        }).AllowAnonymous().DisableAntiforgery();
    }
}
