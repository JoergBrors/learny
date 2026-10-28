using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace LearnCards.Web.Auth;

/// <summary>
/// Hilfsfunktionen für den OIDC-Rücksprung per <c>response_mode=form_post</c> (ADR 0036): Formular-Prüfung, Cookie-Optionen,
/// Redirect-URI und Einmaligkeit des <c>state</c>. Reine Logik ohne Netzwerkzugriff, damit sie einzeln testbar ist.
/// </summary>
public static class OidcCallback
{
    public const string StateCookie = "lc.oidc.state";
    public const string CallbackPath = "/auth/callback";
    /// <summary>Obergrenze des Formular-Bodys (Entra-Codes sind ~2 KB lang).</summary>
    public const int MaxBodyBytes = 32 * 1024;
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    public const int MaxCode = 8192, MaxState = 256, MaxError = 256, MaxErrorDescription = 2048;

    public record Form(string? Code, string? State, string? Error, string? ErrorDescription);

    /// <summary>Prüft die Formularfelder. Doppelte oder überlange bekannte Felder und unvollständige Antworten werden abgewiesen; unbekannte Felder (z. B. session_state) werden ignoriert.</summary>
    public static (Form? Form, string? Problem) ParseForm(IFormCollection form)
    {
        string? Single(string key, int max, out string? problem)
        {
            problem = null;
            if (!form.TryGetValue(key, out var v) || v.Count == 0) return null;
            if (v.Count > 1) { problem = $"Parameter {key} mehrfach"; return null; }
            var s = v[0];
            if (string.IsNullOrEmpty(s)) return null;
            if (s.Length > max) { problem = $"Parameter {key} zu lang"; return null; }
            return s;
        }

        var code = Single("code", MaxCode, out var p1);
        var state = Single("state", MaxState, out var p2);
        var error = Single("error", MaxError, out var p3);
        var desc = Single("error_description", MaxErrorDescription, out var p4);
        var problem = p1 ?? p2 ?? p3 ?? p4;
        if (problem is not null) return (null, problem);
        if (error is null && (code is null || state is null)) return (null, "Antwort unvollständig");
        return (new Form(code, state, error, desc), null);
    }

    /// <summary>Temporäres Korrelationscookie. SameSite=None ist nötig, weil der Identity Provider per Cross-Site-POST zurückleitet; deshalb Secure, HttpOnly, kurze Lebensdauer und enger Pfad.</summary>
    public static CookieOptions StateCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.None,
        MaxAge = StateLifetime,
        Path = CallbackPath,
    };

    /// <summary>Redirect-URI: PUBLIC_BASE_URL hat Vorrang (nicht per Host-Header manipulierbar). Außerhalb der Entwicklung ist HTTPS Pflicht (null = abgelehnt).</summary>
    public static string? ResolveRedirectUri(string? publicBaseUrl, string scheme, HostString host, bool allowHttp)
    {
        var uri = !string.IsNullOrWhiteSpace(publicBaseUrl)
            ? publicBaseUrl.TrimEnd('/') + CallbackPath
            : $"{scheme}://{host}{CallbackPath}";
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return null;
        if (parsed.Scheme != Uri.UriSchemeHttps && !(allowHttp && parsed.Scheme == Uri.UriSchemeHttp)) return null;
        return uri;
    }

    private static readonly ConcurrentDictionary<string, DateTime> Consumed = new();

    /// <summary>Markiert einen state als verbraucht. false = schon verwendet (Wiederholung).</summary>
    public static bool TryConsume(string state, DateTime? now = null)
    {
        var t = now ?? DateTime.UtcNow;
        foreach (var kv in Consumed)
            if (kv.Value < t) Consumed.TryRemove(kv.Key, out _);
        return Consumed.TryAdd(state, t + StateLifetime + TimeSpan.FromMinutes(1));
    }

    /// <summary>Fehlertext für die Anmeldeseite: nur ein unkritischer Fehlercode des Providers, nie dessen freier Text.</summary>
    public static string SafeErrorText(string error) =>
        System.Text.RegularExpressions.Regex.IsMatch(error, "^[a-z_]{1,64}$") ? $"Anmeldung abgelehnt ({error})" : "Anmeldung abgelehnt";
}
