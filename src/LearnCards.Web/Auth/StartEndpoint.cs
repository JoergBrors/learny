using LearnCards.Web.Domain;
using LearnCards.Web.Services;

namespace LearnCards.Web.Auth;

/// <summary>
/// <c>GET /start</c>: Startziel nach der Anmeldung (siehe <see cref="LandingService"/>). Nur für angemeldete Benutzer; leitet immer auf eine interne Adresse weiter, ohne Zwischenspeicherung.
/// Der Aufruf von <c>/</c> (Navigation „Module“) bleibt unverändert die Modulübersicht.
/// </summary>
public static class StartEndpoint
{
    public static void MapStartEndpoint(this WebApplication app)
    {
        app.MapGet("/start", async (HttpContext http, LandingService landing) =>
            AuthEndpoints.NoStoreRedirect(http, "/dashboard")).RequireAuthorization();   // Einstieg: persönliches Dashboard (Weiterlernen = LandingService)
    }
}
