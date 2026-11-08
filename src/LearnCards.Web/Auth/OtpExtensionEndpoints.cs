using System.Text;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;
using LearnCards.Web.Services.Mail;

namespace LearnCards.Web.Auth;

/// <summary>
/// Entra-Custom-Authentication-Extension „OnOtpSend“ (ADR 0045): <c>POST /extensions/otp-send</c>. Entra erzeugt und validiert den Einmalcode; dieser Endpunkt versendet ihn nur über das
/// Learny-Postfach. Eigene Endpunkt-Authentifizierung (Bearer-Token der Extension: Signatur, Issuer aus der Discovery, Audience, aufrufende App, Tenant, Ablauf) — nicht das Benutzer-Cookie.
/// Begrenzte Größe, Rate Limits je Instanz, Zeitlimit, nie Codes in Logs oder Datenbank; Audit enthält nur Ergebniscodes (keine Adresse, kein Code, keine Tokens), und unauthentifizierte
/// Aufrufe (401/413) werden nicht protokolliert, damit sie den Speicher nicht füllen können. Fachlich getrennt von der Einladungsverwaltung: hier gibt es keine Einladungslogik außer
/// der lesenden Empfängerprüfung (<see cref="InvitationService.MayReceiveSignInCodeAsync"/>). Ist <c>EXTID_OTP_ENABLED</c> aus, existiert der Pfad nicht.
/// </summary>
public static class OtpExtensionEndpoints
{
    public const string Path = "/extensions/otp-send";

    public static void MapOtpExtensionEndpoints(this WebApplication app)
    {
        var cfg = app.Services.GetRequiredService<AppConfig>();
        if (!cfg.OtpExtension.Enabled) return;

        var o = new OtpExtensionOptions
        {
            Authority = cfg.OtpExtension.Authority,
            Audiences = cfg.OtpExtension.Audiences,
            AllowLoopbackHttp = cfg.OtpExtension.AllowLoopbackHttp,
            ExpectedTenantId = cfg.ExternalId.TenantId,
            GlobalPerMinute = cfg.OtpExtension.GlobalPerMinute,
            PerRecipientPer10Minutes = cfg.OtpExtension.PerRecipientPer10Minutes,
            Audit = async (ev, res) =>
            {
                // Betreiber-Log für jede Ablehnung: nur Status und Ergebniscode (nie Adresse, Code oder Token)
                if (!res.StartsWith("200:")) app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LearnCards.Web.Auth.OtpExtension").LogWarning("OTP-Extension abgewiesen: {Result}", res);
                if (res.StartsWith("401:") || res.StartsWith("413:")) return;
                await app.Services.GetRequiredService<UserService>().WriteAuditAsync(null, ev, "", res);
            },
        };
        var handler = new OtpSendHandler(
            new ExtensionTokenValidator(app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(), o),
            app.Services.GetRequiredService<IOtpMailer>(),
            new InvitationOtpRecipientPolicy(app.Services.GetRequiredService<InvitationService>()), o);

        app.MapPost(Path, async (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            // Maschinenschnittstelle: Statuscodes dürfen nicht durch die Fehlerseiten der Anwendung (Re-Execute) ersetzt werden.
            if (http.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
            var sizeLimit = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (sizeLimit is { IsReadOnly: false }) sizeLimit.MaxRequestBodySize = o.MaxBodyBytes + 1024;
            string body;
            try
            {
                using var reader = new StreamReader(http.Request.Body, Encoding.UTF8, false, 4096, true);
                var buf = new char[o.MaxBodyBytes + 1];
                var n = await reader.ReadBlockAsync(buf, 0, buf.Length);
                body = new string(buf, 0, n);
            }
            catch (Exception ex) when (ex is BadHttpRequestException or IOException or InvalidOperationException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }

            var r = await handler.HandleAsync(http.Request.Headers.Authorization.ToString(), body, http.RequestAborted);
            if (r.RetryAfter is { } ra) http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(ra.TotalSeconds)).ToString();
            return r.Json is null ? Results.StatusCode(r.Status) : Results.Content(r.Json, "application/json", Encoding.UTF8, r.Status);
        }).AllowAnonymous().DisableAntiforgery();
    }
}
