using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;

namespace LearnCards.Web.Auth;

/// <summary>
/// /auth/login, POST /auth/callback (form_post, ADR 0036), /auth/logout — Cookie-Session + OIDC Code Flow.
/// Im Dev-Modus (AUTH_MODE=dev oder keine OIDC-Konfiguration) erfolgt ein lokaler Login ohne Provider.
/// </summary>
public static class AuthEndpoints
{
    private const string StateCookie = "lc.oidc.state";

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var cfg = app.Services.GetRequiredService<AppConfig>();

        app.MapGet("/auth/login", async (HttpContext http, OidcService oidc, UserService users, IDataProtectionProvider dp, string? returnUrl, string? devUser, string? devRoles) =>
        {
            returnUrl = SafeReturnUrl(returnUrl);

            if (cfg.AuthMode == "dev")
            {
                // Dev-Modus: Standardbenutzer "local-dev-user" (Admin). Mit ?devUser=<name> lassen sich weitere
                // Testbenutzer anmelden (für Mehrbenutzer-Tests); sie erhalten keine Sonderrechte.
                var name = Environment.GetEnvironmentVariable("DEV_USER_NAME") ?? "Lokale Entwicklung";
                var email = Environment.GetEnvironmentVariable("DEV_USER_EMAIL") ?? "dev@localhost";
                var subject = UserService.DevDefaultSubject;
                if (!string.IsNullOrEmpty(devUser) && System.Text.RegularExpressions.Regex.IsMatch(devUser, "^[a-z0-9_-]{1,32}$"))
                {
                    subject = "dev-" + devUser;
                    name = devUser;
                    email = devUser + "@localhost";
                }
                // ?devRoles=Admin.All,... simuliert den "roles"-Claim von Entra ID (nur Dev-Modus; durchläuft dieselbe Abbildung/Synchronisierung)
                var simulatedRoles = string.IsNullOrWhiteSpace(devRoles) ? null
                    : devRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var devAppUser = await users.SignInAsync(UserService.DevProvider, subject, name, email, simulatedRoles);
                if (!devAppUser.IsActive) return Results.Redirect("/login?error=Zugang+gesperrt");
                await SignInAsync(http, devAppUser, subject);
                return Results.Redirect(returnUrl);
            }

            var redirectUri = BuildRedirectUri(http, cfg);
            if (redirectUri is null) return Results.Redirect("/login?error=HTTPS+f%C3%BCr+die+Anmeldung+erforderlich");

            var state = OidcService.CreateState(returnUrl);
            var protector = dp.CreateProtector("oidc-state").ToTimeLimitedDataProtector();
            http.Response.Cookies.Append(StateCookie, protector.Protect(JsonSerializer.Serialize(state), OidcCallback.StateLifetime),
                OidcCallback.StateCookieOptions());   // SameSite=None: Rücksprung per Cross-Site-POST (form_post, ADR 0036)

            var url = await oidc.BuildAuthorizeUrlAsync(state, redirectUri);
            return Results.Redirect(url);
        }).AllowAnonymous();

        // Alte GET-Rücksprünge (response_mode=query) werden nicht mehr verarbeitet; ein Code in der URL wird nie ausgewertet.
        app.MapGet(OidcCallback.CallbackPath, (HttpContext http) =>
            NoStoreRedirect(http, "/login?error=Anmeldung+bitte+erneut+starten")).AllowAnonymous();

        // Rücksprung des Identity Providers (response_mode=form_post): Cross-Site-POST ohne Antiforgery-Token.
        // Schutz vor Login-CSRF ist die Korrelation: state im Formular = state im verschlüsselten, einmaligen Cookie.
        app.MapPost(OidcCallback.CallbackPath, async (HttpContext http, OidcService oidc, UserService users, IDataProtectionProvider dp) =>
        {
            var sizeLimit = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (sizeLimit is { IsReadOnly: false }) sizeLimit.MaxRequestBodySize = OidcCallback.MaxBodyBytes;

            // Korrelationscookie bei jedem Ausgang entfernen (einmalige Nutzung)
            var raw = http.Request.Cookies[StateCookie];
            http.Response.Cookies.Delete(StateCookie, OidcCallback.StateCookieOptions());

            if (!http.Request.HasFormContentType)
                return NoStoreRedirect(http, "/login?error=Antwort+des+Identity-Providers+ung%C3%BCltig");

            OidcCallback.Form? form;
            try { form = OidcCallback.ParseForm(await http.Request.ReadFormAsync()).Form; }
            catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException or IOException) { form = null; }
            if (form is null) return NoStoreRedirect(http, "/login?error=Antwort+des+Identity-Providers+ung%C3%BCltig");

            if (form.Error is not null)
                return NoStoreRedirect(http, $"/login?error={Uri.EscapeDataString(OidcCallback.SafeErrorText(form.Error))}");
            if (raw is null) return NoStoreRedirect(http, "/login?error=Login-Sitzung+abgelaufen");

            OidcService.AuthState authState;
            try
            {
                var protector = dp.CreateProtector("oidc-state").ToTimeLimitedDataProtector();
                authState = JsonSerializer.Deserialize<OidcService.AuthState>(protector.Unprotect(raw))!;
            }
            catch { return NoStoreRedirect(http, "/login?error=State-Cookie+ung%C3%BCltig"); }

            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(authState.State), System.Text.Encoding.UTF8.GetBytes(form.State!)))
                return NoStoreRedirect(http, "/login?error=State-Parameter+stimmt+nicht");
            if (!OidcCallback.TryConsume(authState.State))
                return NoStoreRedirect(http, "/login?error=Login-Sitzung+bereits+verwendet");

            try
            {
                var redirectUri = BuildRedirectUri(http, cfg);
                if (redirectUri is null) return NoStoreRedirect(http, "/login?error=HTTPS+f%C3%BCr+die+Anmeldung+erforderlich");
                var (idToken, _) = await oidc.ExchangeCodeAsync(form.Code!, authState.CodeVerifier, redirectUri);
                var claims = await oidc.ValidateIdTokenAsync(idToken, authState.Nonce);

                string? Get(string key) => claims.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

                var sub = Get("sub");
                if (string.IsNullOrWhiteSpace(sub))
                    return NoStoreRedirect(http, "/login?error=ID-Token+enth%C3%A4lt+kein+Subject");

                var appUser = await users.SignInAsync(cfg.OidcIssuer, sub,
                    Get("name") ?? Get("preferred_username") ?? "Unbekannt",
                    Get("email") ?? Get("preferred_username") ?? "",
                    idpRoles: GetRoles(claims));
                if (!appUser.IsActive)
                    return NoStoreRedirect(http, "/login?error=Zugang+gesperrt");

                await SignInAsync(http, appUser, sub);
                return NoStoreRedirect(http, authState.ReturnUrl);
            }
            catch (Exception ex)
            {
                return NoStoreRedirect(http, $"/login?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).AllowAnonymous().DisableAntiforgery();

        app.MapGet("/auth/logout", async (HttpContext http, OidcService oidc) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (cfg.AuthMode == "oidc")
            {
                try
                {
                    var disco = await oidc.GetDiscoveryAsync();
                    if (disco.EndSessionEndpoint is not null)
                    {
                        var postLogout = $"{http.Request.Scheme}://{http.Request.Host}/login";
                        return Results.Redirect($"{disco.EndSessionEndpoint}?post_logout_redirect_uri={Uri.EscapeDataString(postLogout)}");
                    }
                }
                catch { /* Discovery nicht erreichbar → nur lokal abmelden */ }
            }
            return Results.Redirect("/login");
        }).AllowAnonymous();
    }

    /// <summary>Setzt die Cookie-Session. "uid" (interne Benutzer-ID) wird ausschließlich hier serverseitig gesetzt;
    /// Rollen kommen nicht aus dem Cookie, sondern werden bei jeder Anfrage frisch aus der Datenbank geladen (siehe Program.cs).</summary>
    private static async Task SignInAsync(HttpContext http, AppUser user, string externalSubject)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(UserClaims.UserId, user.Id),
            new("sub", externalSubject),
            new(ClaimTypes.Name, user.DisplayName),
        };
        if (user.Email.Length > 0) claims.Add(new Claim(ClaimTypes.Email, user.Email));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(10),
                AllowRefresh = true,
            });
    }

    /// <summary>Liest die App-Rollen aus dem ID-Token ("roles", Array von Strings). Fehlender Claim = keine Rollen.</summary>
    private static List<string> GetRoles(IReadOnlyDictionary<string, JsonElement> claims)
    {
        var result = new List<string>();
        if (claims.TryGetValue("roles", out var v))
        {
            if (v.ValueKind == JsonValueKind.Array)
                result.AddRange(v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
            else if (v.ValueKind == JsonValueKind.String)
                result.Add(v.GetString()!);
        }
        return result;
    }

    /// <summary>Öffentliche Callback-Adresse (PUBLIC_BASE_URL hat Vorrang); außerhalb der Entwicklung nur HTTPS, sonst null.</summary>
    private static string? BuildRedirectUri(HttpContext http, AppConfig cfg) =>
        OidcCallback.ResolveRedirectUri(cfg.Blockchain.PublicBaseUrl, http.Request.Scheme, http.Request.Host,
            allowHttp: http.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment());

    /// <summary>303 See Other nach dem POST, ohne Zwischenspeicherung; die Ziel-URL enthält nie Codes.</summary>
    private static IResult NoStoreRedirect(HttpContext http, string url)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.StatusCode = StatusCodes.Status303SeeOther;
        http.Response.Headers.Location = url;
        return Results.Empty;
    }

    private static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") ? url : "/";
}
