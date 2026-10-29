using System.Globalization;

namespace LearnCards.Web.Infrastructure;

/// <summary>
/// OpenTimestamps-Einstellungen (ADR 0039/0040). Standard: ausgeschaltet. Der Client ist nativ in der Anwendung (kein <c>ots</c>-Programm, kein Python, kein WSL);
/// Calendar-Server und Bitcoin-Knoten kommen ausschließlich aus der Konfiguration.
/// </summary>
public sealed class OtsSettings
{
    public bool Enabled { get; set; }
    /// <summary>Calendar-Server (https), mindestens zwei unabhängig betriebene; keine Standardwerte.</summary>
    public List<string> Calendars { get; set; } = new();
    /// <summary>Betreiberzuordnung je Calendar-URL (normalisiert: klein, ohne abschließenden Schrägstrich). Ohne Eintrag gilt der Betreiber als unbekannt und zählt <b>nicht</b> als unabhängig.</summary>
    public Dictionary<string, string> Operators { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int TimeoutSeconds { get; set; } = 60;
    /// <summary>Bestätigungstiefe, ab der ein Bitcoin-Einschluss als „confirmed“ gilt.</summary>
    public int Confirmations { get; set; } = 6;
    public string BitcoinRpcUrl { get; set; } = "";
    /// <summary>Optional: Esplora-kompatibler Explorer als Header-Quelle (Zusatzannahme, siehe ADR 0040); nur wenn kein eigener Knoten konfiguriert ist.</summary>
    public string BitcoinExplorerUrl { get; set; } = "";
    public string BitcoinRpcUser { get; set; } = "";
    public string BitcoinRpcPassword { get; set; } = "";

    public static OtsSettings Load()
    {
        string E(string key, string fallback = "") => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v.Trim() : fallback;
        int I(string key, int fallback) => int.TryParse(E(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : fallback;
        return new OtsSettings
        {
            Enabled = E("OTS_ENABLED").Equals("true", StringComparison.OrdinalIgnoreCase) || E("OTS_ENABLED") == "1",
            Calendars = ParseCalendars(E("OTS_CALENDARS"), out var ops),
            Operators = ops,
            TimeoutSeconds = I("OTS_TIMEOUT_SEC", 60),
            Confirmations = I("OTS_CONFIRMATIONS", 6),
            BitcoinRpcUrl = E("OTS_BITCOIN_RPC_URL"), BitcoinExplorerUrl = E("OTS_BITCOIN_EXPLORER_URL").TrimEnd('/'), BitcoinRpcUser = E("OTS_BITCOIN_RPC_USER"), BitcoinRpcPassword = E("OTS_BITCOIN_RPC_PASSWORD"),
        };
    }

    /// <summary>
    /// Liest <c>OTS_CALENDARS</c>: kommagetrennte Einträge <c>https://calendar|Betreiber</c>. Der Betreiber wird bewusst vom Betreiber der Installation benannt (nicht aus dem Hostnamen geraten);
    /// Einträge ohne Betreiber werden als „unbekannt“ geführt und nicht als unabhängig gewertet.
    /// </summary>
    public static List<string> ParseCalendars(string raw, out Dictionary<string, string> operators)
    {
        operators = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var urls = new List<string>();
        foreach (var entry in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('|', 2, StringSplitOptions.TrimEntries);
            var url = parts[0].TrimEnd('/');
            if (url.Length == 0) continue;
            urls.Add(url);
            if (parts.Length == 2 && parts[1].Length > 0) operators[url] = parts[1];
        }
        return urls;
    }

    public string? OperatorOf(string url) => Operators.TryGetValue(url.TrimEnd('/'), out var o) && o.Length > 0 ? o : null;

    /// <summary>Kurzbeschreibung des Konfigurationsproblems (null = betriebsbereit). Weniger als zwei https-Calendars ist ein Fehler, kein stiller Betrieb mit einem Einzelserver.</summary>
    public string? Problem()
    {
        if (!Enabled) return "OpenTimestamps ist nicht aktiviert (OTS_ENABLED).";
        var valid = Calendars.Where(c => Uri.TryCreate(c, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (valid.Count < 2) return "Mindestens zwei https-Calendar-Server (OTS_CALENDARS) sind erforderlich.";
        if (valid.Select(OperatorOf).Where(o => o is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
            return "Mindestens zwei Calendars unterschiedlicher, benannter Betreiber sind erforderlich (OTS_CALENDARS: https://calendar|Betreiber); unbekannte Betreiber gelten nicht als unabhängig.";
        return null;
    }
}
