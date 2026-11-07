using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using System.Threading.RateLimiting;
using InviteRegistration;
using LearnCards.Web.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InviteRegistration.Portal;

public sealed class PortalOptions
{
    public required string Url { get; init; }
    /// <summary>Öffentliche Basisadresse für Einladungslinks (HTTPS außerhalb von Development).</summary>
    public required string PublicBaseUrl { get; init; }
    public required IDatabase Db { get; init; }
    /// <summary>Verzeichnis der Data-Protection-Schlüssel (Sitzungen überstehen Neustarts).</summary>
    public required string KeyDir { get; init; }
    /// <summary>HMAC-Schlüssel der Einladungstokens (≥ 32 Byte, bleibt über Neustarts gleich, sonst werden gespeicherte Einladungen ungültig).</summary>
    public required byte[] TokenKey { get; init; }
    public required IOidcFlow UserOidc { get; init; }
    public required IInvitationMailer Mailer { get; init; }
    /// <summary>Nur Development: zeigt Lernenden ihr eigenes ID-Token auf der Profilseite.</summary>
    public IdTokenStore? TokenViewer { get; init; }
    /// <summary>Claims der Anmeldung (learny_*) je Subject; die Sitzung übernimmt sie einmalig (ADR 0047). Ohne Wert gibt es keine Claims-Übersicht.</summary>
    public LearnerClaimsStore? ClaimsStore { get; init; }
    /// <summary>Mail-Einrichtung im Portal (App-Registrierung, Zertifikat). Ohne Wert ist die Seite „Mail-Einrichtung“ nur ein Hinweis.</summary>
    public MailSettingsService? MailSettings { get; init; }
    /// <summary>Workforce-OIDC für Administratoren (getrennt vom External-ID-Issuer). Ohne Wert gibt es keinen OIDC-Login für Administratoren.</summary>
    public IOidcFlow? AdminOidc { get; init; }
    public AdminBootstrap? AdminBootstrap { get; init; }
    /// <summary>Nur Development und nur Loopback: lokaler Administrator-Login mit Schlüssel (ersetzt nie den OIDC-Login außerhalb von Development).</summary>
    public string? DevAdminKey { get; init; }
    public bool Development { get; init; }
    public OtpSendHandler? OtpHandler { get; init; }
    /// <summary>Token-Anreicherung (OnTokenIssuanceStart) auf demselben schlanken Extension-Host wie der OTP-Endpunkt.</summary>
    public TokenClaimsHandler? ClaimsHandler { get; init; }
    public string OtpUrl { get; init; } = "http://127.0.0.1:5080";
    public Func<DateTime>? Clock { get; init; }
    public int ValidityHours { get; init; } = 72;
    public int SensitivePerMinute { get; init; } = 30;
    public bool InitializeDatabase { get; init; } = true;
    public bool SeedDemoData { get; init; }
}

/// <summary>Dienste für die Razor-Komponenten.</summary>
public sealed record PortalServices(IDatabase Db, InvitationService Invitations, InvitationDeliveryService Delivery, ContentAccess Access, AdminService Admin, InviteEvents Events, LinkHandover Handover, PortalOptions Options, LearnerOverview Overview);

public sealed class PortalApp : IAsyncDisposable
{
    internal PortalApp(WebApplication main, WebApplication? otp, Func<Task> init) { Main = main; Otp = otp; _init = init; }
    private readonly Func<Task> _init;
    public WebApplication Main { get; }
    public WebApplication? Otp { get; }
    public async Task StartAsync() { await _init(); await Main.StartAsync(); if (Otp is not null) await Otp.StartAsync(); }
    public async Task StopAsync() { if (Otp is not null) await Otp.StopAsync(); await Main.StopAsync(); }
    public async ValueTask DisposeAsync() { if (Otp is not null) await Otp.DisposeAsync(); await Main.DisposeAsync(); }
}

public static class PortalHost
{
    public const string AdminScheme = "admin";
    public const string UserScheme = "user";
    private static readonly string[] SensitivePrefixes = { "/invite", "/invite-start", "/login", "/auth/callback", "/admin/login", "/admin/callback", "/admin/dev-login" };

    public static PortalApp Build(PortalOptions o)
    {
        var clock = o.Clock ?? (() => DateTime.UtcNow);
        var https = o.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!o.Development && !https) throw new InvalidOperationException("Außerhalb von Development ist HTTPS Pflicht (kein Rückfall, ADR 0026).");
        if (!o.Development && o.DevAdminKey is not null) throw new InvalidOperationException("Der lokale Administrator-Login ist nur in Development erlaubt.");
        if (!o.Development && o.AdminOidc is null) throw new InvalidOperationException("Ohne Workforce-OIDC gibt es außerhalb von Development keinen Administratorzugang (fail-closed).");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(PortalHost).Assembly.GetName().Name, ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production" });
        builder.WebHost.UseUrls(o.Url);
        if (https) builder.WebHost.UseKestrelHttpsConfiguration();
        builder.Logging.ClearProviders();   // keine Request-URLs (mit Token) in Logs

        var events = new InviteEvents(o.Db, clock);
        if (o.MailSettings is not null) o.MailSettings.Events = events;
        var inv = new InvitationService(o.Db, new InvitationOptions { BaseUrl = o.PublicBaseUrl, TokenKey = o.TokenKey, ValidityHours = o.ValidityHours, Clock = clock }) { Events = events };
        var delivery = new InvitationDeliveryService(o.Db, inv, o.Mailer, clock) { Events = events };
        var svc = new PortalServices(o.Db, inv, delivery, new ContentAccess(o.Db), new AdminService(o.Db, inv, events, clock), events, new LinkHandover(clock), o, new LearnerOverview(o.Db, new ContentAccess(o.Db)));

        builder.Services.AddSingleton(svc);
        // UTF-8-Seiten: Umlaute und typografische Zeichen bleiben lesbar; <, >, & und Anführungszeichen werden weiterhin kodiert.
        builder.Services.AddWebEncoders(w => w.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
        builder.Services.AddRazorComponents();
        Directory.CreateDirectory(o.KeyDir);
        builder.Services.AddDataProtection().SetApplicationName("LearnyPortal").PersistKeysToFileSystem(new DirectoryInfo(o.KeyDir));
        var secure = https ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        builder.Services.AddAntiforgery(a => { a.Cookie.Name = "lp.csrf"; a.Cookie.HttpOnly = true; a.Cookie.SameSite = SameSiteMode.Strict; a.Cookie.SecurePolicy = secure; });

        builder.Services.AddAuthentication()
            .AddCookie(AdminScheme, c =>
            {
                c.Cookie.Name = "lp.admin"; c.Cookie.HttpOnly = true; c.Cookie.SameSite = SameSiteMode.Lax; c.Cookie.SecurePolicy = secure;
                c.LoginPath = "/admin/signin"; c.AccessDeniedPath = "/access-denied"; c.ExpireTimeSpan = TimeSpan.FromHours(8); c.SlidingExpiration = false;
                // Die Rolle wird bei jedem Aufruf gegen die Datenbank geprüft (nicht aus dem Cookie vertraut).
                c.Events.OnValidatePrincipal = async ctx =>
                {
                    var uid = ctx.Principal?.FindFirst("uid")?.Value;
                    if (uid is null || !await IsActiveAdmin(o.Db, uid)) { ctx.RejectPrincipal(); await ctx.HttpContext.SignOutAsync(AdminScheme); }
                };
            })
            .AddCookie(UserScheme, c =>
            {
                c.Cookie.Name = "lp.user"; c.Cookie.HttpOnly = true; c.Cookie.SameSite = SameSiteMode.Lax; c.Cookie.SecurePolicy = secure;
                c.LoginPath = "/login"; c.AccessDeniedPath = "/access-denied"; c.ExpireTimeSpan = TimeSpan.FromHours(8); c.SlidingExpiration = false;
                // Deaktivierte oder profillose Benutzer sind sofort ausgesperrt.
                c.Events.OnValidatePrincipal = async ctx =>
                {
                    var uid = ctx.Principal?.FindFirst("uid")?.Value;
                    var ok = uid is not null && (await o.Db.QueryAsync("SELECT 1 AS x FROM users u JOIN user_profiles p ON p.user_id=u.id WHERE u.id=@u AND u.status='active'", new (string, object?)[] { ("u", uid) })).Count > 0;
                    if (!ok) { ctx.RejectPrincipal(); await ctx.HttpContext.SignOutAsync(UserScheme); }
                };
            });
        builder.Services.AddAuthorization(a =>
        {
            a.AddPolicy("AdminOnly", p => p.AddAuthenticationSchemes(AdminScheme).RequireAuthenticatedUser().RequireClaim("role", "admin"));
            a.AddPolicy("UserOnly", p => p.AddAuthenticationSchemes(UserScheme).RequireAuthenticatedUser().RequireClaim("role", "learner"));
        });
        builder.Services.AddRateLimiter(r =>
        {
            r.RejectionStatusCode = 429;
            r.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                var path = ctx.Request.Path.Value ?? "";
                if (!SensitivePrefixes.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))) return RateLimitPartition.GetNoLimiter("none");
                return RateLimitPartition.GetFixedWindowLimiter("ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "?"), _ => new FixedWindowRateLimiterOptions { PermitLimit = o.SensitivePerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
        });

        var app = builder.Build();
        var dp = app.Services.GetRequiredService<IDataProtectionProvider>();

        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
            await next();
        });
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        // ── Statisches: Stil und Learny-Assets aus dem Bestand (keine Kopien) ──
        app.MapGet("/portal.css", () => Results.Text(PortalCss.Text, "text/css", Encoding.UTF8));
        var assets = FindAssets();
        app.MapGet("/brand/{name}", (string name) =>
        {
            if (!Regex.IsMatch(name, @"^[a-z0-9\-]{1,64}\.(png|svg)$") || assets is null) return Results.NotFound();
            var file = name == "learny-banner.png" ? assets.Value.Banner : Path.Combine(assets.Value.BrandDir, name);
            return File.Exists(file) ? Results.File(file, name.EndsWith(".svg") ? "image/svg+xml" : "image/png") : Results.NotFound();
        });

        // ── Öffentliches Zertifikat der Mail-App zum Hochladen in Entra (nur Public Key, nur Administratoren) ──
        app.MapGet("/admin/mail/certificate.cer", async (HttpContext c) =>
        {
            var der = o.MailSettings is null ? null : await o.MailSettings.GetPublicCertificateAsync(c.RequestAborted);
            return der is null ? Results.NotFound() : Results.File(der, "application/x-x509-ca-cert", "learny-poc-mail.cer");
        }).RequireAuthorization("AdminOnly");

        // ── Einladung, Registrierung und Anmeldung der Lernenden (gemeinsame Komponente) ──
        InviteFlow.Map(app, new InviteFlowOptions
        {
            Invitations = inv, Oidc = o.UserOidc, DataProtection = dp, Clock = clock, Events = events,
            InvalidPage = () => Results.Content(PortalHtml.Page("Einladung ungültig", "<h1>Einladung ungültig</h1><p>Diese Einladung ist ungültig, abgelaufen oder wurde bereits verwendet.</p><p><a href=\"/\">Zur Startseite</a></p>"), "text/html; charset=utf-8", Encoding.UTF8, 400),
            SignInUserAsync = async (c, uid) =>
            {
                await o.Db.ExecuteAsync("UPDATE users SET last_login_at=@now WHERE id=@u", new (string, object?)[] { ("u", uid), ("now", clock()) });
                // Claims der Anmeldung (nur Hinweis): nur übernehmen, wenn sie zum selben Benutzer gehören.
                var extra = new List<Claim>();
                if (o.ClaimsStore is { } store)
                    foreach (var r in await o.Db.QueryAsync("SELECT subject FROM user_identities WHERE user_id=@u", new (string, object?)[] { ("u", uid) }))
                        if (r["subject"] is { } sub && store.Take(sub) is { } hint && hint.Uid == uid)
                        {
                            if (hint.GrantsVersion is not null) extra.Add(new Claim("lv", hint.GrantsVersion));
                            if (hint.Modules is not null) extra.Add(new Claim("lm", string.Join(",", hint.Modules)));
                            break;
                        }
                await SignIn(c, UserScheme, uid, "learner", clock, extra);
            },
        });

        // ── Administratoranmeldung: getrennter, ausdrücklich vertrauter Workforce-Issuer; Rolle nur über die Bootstrap-Konfiguration ──
        if (o.AdminOidc is { } adminOidc && o.AdminBootstrap is { } bootstrap)
        {
            var adminProt = dp.CreateProtector("portal.admin.oidc.v1");
            app.MapGet("/admin/login", (HttpContext c) => Results.Redirect(OidcCookieFlow.Begin(c, adminOidc, adminProt, "adm.oidc", "/admin/callback", "admin", clock), false, false));
            app.MapPost("/admin/callback", async (HttpContext c) =>
            {
                var (identity, _) = await OidcCookieFlow.CompleteAsync(c, adminOidc, adminProt, "adm.oidc", "/admin/callback", clock);
                var uid = identity is null ? null : await bootstrap.TryEnsureAdminAsync(identity);
                if (uid is null) { await events.LogAsync(EventTypes.AdminRejected, null, null, identity is null ? "oidc_failed" : "not_allowed"); return Results.Redirect("/admin/denied"); }
                await events.LogAsync(EventTypes.AdminSignedIn, null, uid);
                await SignIn(c, AdminScheme, uid, "admin", clock);
                return Results.Redirect("/admin");
            });
        }
        if (o.Development && o.DevAdminKey is { } devKey)
        {
            var dev = new AdminBootstrap(o.Db, "dev-local", new[] { "local-admin" }, Array.Empty<string>(), clock);
            app.MapPost("/admin/dev-login", async (HttpContext c) =>
            {
                if (!IsLoopback(c)) return Results.NotFound();
                if (!await Csrf(c)) return Results.BadRequest();
                var form = await c.Request.ReadFormAsync();
                if (!form.TryGetValue("key", out var k) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(k.ToString()), Encoding.UTF8.GetBytes(devKey))) return Results.Redirect("/admin/denied");
                var uid = (await dev.TryEnsureAdminAsync(new VerifiedIdentity("dev-local", "local-admin", "admin@localhost", true)))!;
                await events.LogAsync(EventTypes.AdminSignedIn, null, uid, "dev");
                await SignIn(c, AdminScheme, uid, "admin", clock);
                return Results.Redirect("/admin");
            });
        }

        app.MapPost("/logout", async (HttpContext c) => { if (!await Csrf(c)) return Results.BadRequest(); await c.SignOutAsync(UserScheme); return Results.Redirect("/"); });
        app.MapPost("/admin/logout", async (HttpContext c) => { if (!await Csrf(c)) return Results.BadRequest(); await c.SignOutAsync(AdminScheme); return Results.Redirect("/"); });

        // ── Lernbereich: nur zugewiesene Module (zentrale Autorisierung: ContentAccess), auch bei direktem Aufruf ──
        app.MapGet("/modules/{id}", async (HttpContext c, string id) =>
        {
            var uid = c.User.FindFirst("uid")?.Value!;
            if (!await svc.Access.CanReadModuleAsync(uid, id, c.RequestAborted)) return Results.Content(PortalHtml.Page("Kein Zugriff", "<h1>Kein Zugriff</h1><p>Dieses Lernmodul ist Ihnen nicht zugewiesen.</p><p><a href=\"/dashboard\">Zu meinen Lernmodulen</a></p>"), "text/html; charset=utf-8", Encoding.UTF8, 403);
            var name = (await o.Db.QueryAsync("SELECT name FROM poc_modules WHERE id=@m", new (string, object?)[] { ("m", id) })).FirstOrDefault()?["name"] ?? "Lernmodul";
            return Results.Content(PortalHtml.Page(name, $"<h1>{System.Net.WebUtility.HtmlEncode(name)}</h1><p>Lerninhalt des Moduls (PoC-Platzhalter).</p><p><a href=\"/dashboard\">Zu meinen Lernmodulen</a></p>"), "text/html; charset=utf-8", Encoding.UTF8, 200);
        }).RequireAuthorization("UserOnly");

        MapAdminActions(app, svc, clock);

        app.MapRazorComponents<Components.App>();

        WebApplication? otpApp = o.OtpHandler is not null || o.ClaimsHandler is not null ? InviteApp.CreateOtpOnly(o.OtpUrl, o.OtpHandler, o.ClaimsHandler) : null;

        async Task Init()
        {
            if (o.InitializeDatabase) await o.Db.InitAsync();
            await inv.InitAsync();
            if (o.MailSettings is not null) await o.MailSettings.InitAsync();
            if (o.SeedDemoData) await PortalSeed.DemoAsync(o.Db);
        }
        return new PortalApp(app, otpApp, Init);
    }

    // ── Verwaltungsaktionen: nur Administratoren, nur POST, CSRF-geprüft, Bestätigung für Versand/Widerruf/Neuausstellung ──
    private static void MapAdminActions(WebApplication app, PortalServices svc, Func<DateTime> clock)
    {
        var g = app.MapGroup("/admin/actions").RequireAuthorization("AdminOnly");

        async Task<(IFormCollection? Form, string Actor, IResult? Error)> Read(HttpContext c)
        {
            if (!await Csrf(c)) return (null, "", Results.BadRequest());
            return (await c.Request.ReadFormAsync(), c.User.FindFirst("uid")!.Value, null);
        }
        static bool Confirmed(IFormCollection f) => f.TryGetValue("confirm", out var v) && v == "yes";
        static IResult Back(string path, string m) => Results.Redirect(path + (path.Contains('?') ? "&" : "?") + "m=" + m, false, false);

        g.MapPost("/invitations", async (HttpContext c) =>
        {
            var (f, actor, err) = await Read(c); if (err is not null) return err;
            try
            {
                var validity = int.TryParse(f!["validityHours"], out var h) ? h : 72;
                var modules = f["moduleIds"].Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
                var meta = new InvitationMeta(Trim(f["displayName"]), Trim(f["description"]), Trim(f["note"]), validity);
                var created = await svc.Invitations.CreateAsync(actor, f["email"].ToString(), f["profile"].ToString(), modules, meta, c.RequestAborted);
                svc.Handover.Put(created.InvitationId, created.Token, created.Link);   // nur im Speicher, für den bestätigten Versand
                return Back("/admin/invitations/" + created.InvitationId, "created");
            }
            catch (AccountExistsException) { return Back("/admin/invitations/new", "account_exists"); }
            catch (ArgumentException) { return Back("/admin/invitations/new", "invalid"); }
        });

        g.MapPost("/invitations/{id}/send", async (HttpContext c, string id) =>
        {
            var (f, actor, err) = await Read(c); if (err is not null) return err;
            if (!Confirmed(f!)) return Back("/admin/invitations/" + id, "not_confirmed");
            var held = svc.Handover.Peek(id);
            if (held is null) return Back("/admin/invitations/" + id, "link_unavailable");   // kein Klartext mehr → ausdrückliche Neuausstellung
            var res = await svc.Delivery.SendAsync(actor, id, held.Value.Token, held.Value.Link, true, c.RequestAborted);
            if (res.Decision == SendDecision.Sent) svc.Handover.Remove(id);
            var m = res.Decision switch
            {
                SendDecision.Sent => "sent", SendDecision.Failed => "send_failed", SendDecision.Unknown => "send_unknown", SendDecision.InProgress => "in_progress",
                SendDecision.RequiresRotation => "requires_rotation", SendDecision.TooManyAttempts => "too_many", _ => "not_sendable",
            };
            return Back("/admin/invitations/" + id, m);
        });

        g.MapPost("/invitations/{id}/rotate", async (HttpContext c, string id) =>
        {
            var (f, actor, err) = await Read(c); if (err is not null) return err;
            if (!Confirmed(f!)) return Back("/admin/invitations/" + id, "not_confirmed");
            var rot = await svc.Invitations.RotateAsync(actor, id, c.RequestAborted);
            if (rot is null) return Back("/admin/invitations/" + id, "not_rotatable");
            svc.Handover.Put(id, rot.Token, rot.Link);
            return Back("/admin/invitations/" + id, "rotated");
        });

        g.MapPost("/invitations/{id}/revoke", async (HttpContext c, string id) =>
        {
            var (f, actor, err) = await Read(c); if (err is not null) return err;
            if (!Confirmed(f!)) return Back("/admin/invitations/" + id, "not_confirmed");
            svc.Handover.Remove(id);
            return Back("/admin/invitations/" + id, await svc.Invitations.RevokeAsync(actor, id, c.RequestAborted) ? "revoked" : "not_revocable");
        });

        g.MapPost("/mail/settings", async (HttpContext c) =>
        {
            var (f, actor, err) = await Read(c); if (err is not null) return err;
            if (svc.Options.MailSettings is not { } ms) return Back("/admin/mail", "mail_unavailable");
            var ok = await ms.SaveAsync(actor, f!["tenantId"].ToString(), f["clientId"].ToString(), f["sender"].ToString(), f["recipients"].ToString(), f["enabled"] == "yes", c.RequestAborted);
            return Back("/admin/mail", ok ? "mail_saved" : "mail_invalid");
        });
        g.MapPost("/mail/certificate", async (HttpContext c) =>
        {
            var (f, actor, err) = await Read(c); if (err is not null) return err;
            if (svc.Options.MailSettings is not { } ms) return Back("/admin/mail", "mail_unavailable");
            if (!Confirmed(f!)) return Back("/admin/mail", "not_confirmed");
            await ms.GenerateCertificateAsync(actor, c.RequestAborted);
            return Back("/admin/mail", "mail_cert_created");
        });

        g.MapPost("/users/{id}/assign", async (HttpContext c, string id) =>
        {
            var (f, actor, err) = await Read(c); if (err is not null) return err;
            try { await svc.Admin.AssignModuleAsync(actor, id, f!["moduleId"].ToString(), c.RequestAborted); svc.Overview.Invalidate(id); return Back("/admin/users/" + id, "assigned"); }
            catch (ArgumentException) { return Back("/admin/users/" + id, "invalid"); }
        });
        g.MapPost("/users/{id}/revoke", async (HttpContext c, string id) =>
        {
            var (f, actor, err) = await Read(c); if (err is not null) return err;
            try { await svc.Admin.RevokeModuleAsync(actor, id, f!["moduleId"].ToString(), c.RequestAborted); svc.Overview.Invalidate(id); return Back("/admin/users/" + id, "revoked_content"); }
            catch (ArgumentException) { return Back("/admin/users/" + id, "invalid"); }
        });
        g.MapPost("/users/{id}/deactivate", async (HttpContext c, string id) =>
        {
            var (f, actor, err) = await Read(c); if (err is not null) return err;
            if (!Confirmed(f!)) return Back("/admin/users/" + id, "not_confirmed");
            try { await svc.Admin.SetUserActiveAsync(actor, id, false, c.RequestAborted); svc.Overview.Invalidate(id); return Back("/admin/users/" + id, "deactivated"); }
            catch (ArgumentException) { return Back("/admin/users/" + id, "invalid"); }
        });
        g.MapPost("/users/{id}/reactivate", async (HttpContext c, string id) =>
        {
            var (f, actor, err) = await Read(c); if (err is not null) return err;
            try { await svc.Admin.SetUserActiveAsync(actor, id, true, c.RequestAborted); svc.Overview.Invalidate(id); return Back("/admin/users/" + id, "reactivated"); }
            catch (ArgumentException) { return Back("/admin/users/" + id, "invalid"); }
        });
    }

    private static string? Trim(Microsoft.Extensions.Primitives.StringValues v) => string.IsNullOrWhiteSpace(v.ToString()) ? null : v.ToString().Trim();

    private static async Task<bool> Csrf(HttpContext c)
    {
        try { await c.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(c); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    private static async Task SignIn(HttpContext c, string scheme, string uid, string role, Func<DateTime> clock, IEnumerable<Claim>? extra = null)
    {
        var identity = new ClaimsIdentity(new[] { new Claim("uid", uid), new Claim("role", role) }.Concat(extra ?? Array.Empty<Claim>()), scheme);
        await c.SignInAsync(scheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false, ExpiresUtc = new DateTimeOffset(clock()).AddHours(8) });
    }

    private static async Task<bool> IsActiveAdmin(IDatabase db, string uid) =>
        (await db.QueryAsync("SELECT 1 AS x FROM user_roles r JOIN users u ON u.id=r.user_id WHERE r.user_id=@u AND r.role='admin' AND u.status='active'", new (string, object?)[] { ("u", uid) })).Count > 0;

    private static bool IsLoopback(HttpContext c) => c.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip);

    private static (string BrandDir, string Banner)? FindAssets()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "LearnCards.slnx"))) d = d.Parent;
        if (d is null) return null;
        var brand = Path.Combine(d.FullName, "src", "LearnCards.Web", "wwwroot", "brand");
        var banner = Path.Combine(d.FullName, "infra", "entra-external-id", "branding", "banner-logo.png");
        return Directory.Exists(brand) ? (brand, banner) : null;
    }

}

public static class PortalSeed
{
    /// <summary>Testmodule des PoC: A (global), B (global), C (freigegeben). Nur wenn noch keine Module existieren.</summary>
    public static async Task DemoAsync(IDatabase db)
    {
        if (DbValue.ToInt((await db.QueryAsync("SELECT COUNT(*) AS n FROM poc_modules"))[0]["n"]) > 0) return;
        foreach (var (name, scope) in new[] { ("Modul A – Grundlagen", "global"), ("Modul B – Aufbau", "global"), ("Modul C – Praxis", "shared") })
            await db.ExecuteAsync("INSERT INTO poc_modules (id, name, scope) VALUES (@id,@n,@s)", new (string, object?)[] { ("id", Guid.NewGuid().ToString()), ("n", name), ("s", scope) });
    }
}

public static class PortalHtml
{
    /// <summary>Kleine gebrandete Seite für Fehler- und Hinweisseiten außerhalb der Razor-Komponenten.</summary>
    public static string Page(string title, string bodyHtml) =>
        "<!DOCTYPE html><html lang=\"de\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>" + System.Net.WebUtility.HtmlEncode(title) + " – Learny</title><link rel=\"stylesheet\" href=\"/portal.css\"></head>" +
        "<body><header class=\"top\"><a class=\"brand\" href=\"/\"><img src=\"/brand/learny-banner.png\" alt=\"Learny\" height=\"28\"></a></header><main><section class=\"card narrow\">" + bodyHtml + "</section></main></body></html>";
}
