using LearnCards.Web.Infrastructure;

/// <summary>
/// Betriebsmodus fail closed (ADR 0014/0026): Außerhalb von Development entsteht der Dev-Login nie durch fehlende oder fehlerhafte Konfiguration;
/// eine ausdrücklich gesetzte, aber unlesbare ENV_FILE verhindert den Start.
/// </summary>
public static class AuthModeTests
{
    private static readonly string[] Vars = { "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "AUTH_MODE", "OIDC_ISSUER", "OIDC_CLIENT_ID", "OIDC_CLIENT_SECRET", "ENV_FILE", "MCP_TOKEN_KEY" };

    public static void Run(Action<bool, string> check)
    {
        var saved = Vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            string? Outcome(string environment, string? authMode, bool oidc, bool secret = true)
            {
                foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null);
                Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", environment);
                Environment.SetEnvironmentVariable("MCP_TOKEN_KEY", Convert.ToBase64String(new byte[32]));   // Pflichtvariable (ADR 0025), hier nur Testwert
                if (authMode is not null) Environment.SetEnvironmentVariable("AUTH_MODE", authMode);
                if (oidc)
                {
                    Environment.SetEnvironmentVariable("OIDC_ISSUER", "https://login.example.org/tenant/v2.0");
                    Environment.SetEnvironmentVariable("OIDC_CLIENT_ID", "client-id");
                    if (secret) Environment.SetEnvironmentVariable("OIDC_CLIENT_SECRET", "geheim");
                }
                try { return AppConfig.Load().AuthMode; }
                catch (InvalidOperationException ex) { return "FEHLER:" + ex.Message; }
            }

            check(Outcome("Production", null, oidc: false)!.StartsWith("FEHLER:") && Outcome("Production", "auto", oidc: false)!.StartsWith("FEHLER:"),
                "Betriebsmodus: Production ohne OIDC-Konfiguration und ohne AUTH_MODE → Startabbruch, kein stiller Dev-Login");
            check(Outcome("Production", null, oidc: true) == "oidc" && Outcome("Production", "auto", oidc: true) == "oidc" && Outcome("Production", "oidc", oidc: true) == "oidc",
                "Betriebsmodus: Production mit vollständiger OIDC-Konfiguration → oidc");
            check(Outcome("Production", "odic", oidc: false)!.StartsWith("FEHLER:") && Outcome("Production", "odic", oidc: true)!.StartsWith("FEHLER:"),
                "Betriebsmodus: Tippfehler im Wert von AUTH_MODE → Startabbruch (auch mit OIDC-Angaben)");
            check(Outcome("Production", "oidc", oidc: false)!.StartsWith("FEHLER:") && Outcome("Production", "oidc", oidc: true, secret: false)!.StartsWith("FEHLER:"),
                "Betriebsmodus: AUTH_MODE=oidc mit fehlendem Issuer/Client/Secret → Startabbruch");
            check(Outcome("Production", "dev", oidc: false) == "dev", "Betriebsmodus: ausdrücklich gesetztes AUTH_MODE=dev bleibt möglich (lokaler Test, Warnung beim Start)");
            check(Outcome("Development", null, oidc: false) == "dev" && Outcome("Development", "auto", oidc: false) == "dev", "Betriebsmodus: Development ohne Konfiguration → Dev-Login (lokale Entwicklung)");
            var msg = Outcome("Production", null, oidc: false)!;
            check(!msg.Contains("geheim") && msg.Contains("ENV_FILE"), "Betriebsmodus: Fehlermeldung nennt die Ursache (ENV_FILE), enthält keine Werte");

            // ENV_FILE: ausdrücklich gesetzt, aber nicht lesbar → kein stilles Weiterstarten ohne Konfiguration
            foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null);
            Environment.SetEnvironmentVariable("ENV_FILE", Path.Combine(Path.GetTempPath(), "gibt-es-nicht-" + Guid.NewGuid().ToString("N") + ".env"));
            string? envError = null;
            try { EnvLoader.Load(); } catch (InvalidOperationException ex) { envError = ex.Message; }
            check(envError is not null && envError.Contains("ENV_FILE"), "ENV_FILE zeigt auf eine fehlende/unlesbare Datei → klarer Startfehler statt Betrieb ohne Konfiguration");
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        }
    }
}
