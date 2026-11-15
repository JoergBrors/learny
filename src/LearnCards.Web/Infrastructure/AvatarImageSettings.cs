using System.Globalization;

namespace LearnCards.Web.Infrastructure;

/// <summary>
/// Einstellungen der KI-Bildgenerierung für Profilavatare (ADR 0056). Eigene Modelle, unabhängig von <c>OPENAI_MODEL</c> (Chat/Quiz bleiben unverändert).
/// Nutzt denselben Schlüssel und dieselbe Basisadresse wie <c>OPENAI_*</c>. Betreiber können die Funktion mit <c>AVATAR_IMAGE_ENABLED=false</c> abschalten
/// und über die Limits begrenzen; ohne KI-Schlüssel gibt es nur den einfachen SVG-Baukasten.
/// </summary>
public sealed class AvatarImageSettings
{
    /// <summary>Hauptschalter; Standard an (wirksam nur mit konfiguriertem KI-Schlüssel).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Modell ohne Referenzfoto (Endpunkt /images/generations).</summary>
    public string Model { get; set; } = "gpt-image-2.5-flare";
    /// <summary>Modell mit Referenzfoto (Endpunkt /images/edits); laut Dokumentation für Bearbeitungspräzision empfohlen.</summary>
    public string EditModel { get; set; } = "gpt-image-2.5-sunburst";
    /// <summary>low | medium | high | auto (weitere Stufen des Anbieters sind absichtlich nicht freigegeben: Kosten).</summary>
    public string Quality { get; set; } = "medium";
    /// <summary>Breite x Höhe; Vielfaches von 16, mindestens 655.360 Pixel (Anbietergrenze). Standard quadratisch 1024x1024.</summary>
    public string Size { get; set; } = "1024x1024";
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>Höchstzahl der Generierungen je Benutzer in 24 Stunden (je Instanz, Zähler im Arbeitsspeicher).</summary>
    public int DailyLimitPerUser { get; set; } = 10;
    /// <summary>Höchstzahl gleichzeitiger Generierungen insgesamt.</summary>
    public int MaxConcurrent { get; set; } = 4;
    /// <summary>Obergrenze des gespeicherten Bildes (Bytes).</summary>
    public int MaxResultBytes { get; set; } = 1_500_000;

    public static readonly string[] AllowedQualities = { "low", "medium", "high", "auto" };

    public static AvatarImageSettings Load()
    {
        string E(string key, string fallback = "") => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v.Trim() : fallback;
        int I(string key, int fallback, int min, int max) => int.TryParse(E(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, min, max) : fallback;
        var s = new AvatarImageSettings
        {
            Enabled = !E("AVATAR_IMAGE_ENABLED", "true").Equals("false", StringComparison.OrdinalIgnoreCase) && E("AVATAR_IMAGE_ENABLED") != "0",
            Model = E("AVATAR_IMAGE_MODEL", "gpt-image-2.5-flare"),
            EditModel = E("AVATAR_IMAGE_EDIT_MODEL", "gpt-image-2.5-sunburst"),
            Quality = E("AVATAR_IMAGE_QUALITY", "medium").ToLowerInvariant(),
            Size = E("AVATAR_IMAGE_SIZE", "1024x1024").ToLowerInvariant(),
            TimeoutSeconds = I("AVATAR_IMAGE_TIMEOUT_SECONDS", 120, 10, 300),
            DailyLimitPerUser = I("AVATAR_IMAGE_DAILY_LIMIT", 10, 0, 200),
            MaxConcurrent = I("AVATAR_IMAGE_MAX_CONCURRENT", 4, 1, 32),
        };
        if (!AllowedQualities.Contains(s.Quality)) s.Quality = "medium";
        if (!IsValidSize(s.Size)) s.Size = "1024x1024";
        return s;
    }

    /// <summary>Anbieterregeln: WIDTHxHEIGHT, Vielfache von 16, Seitenverhältnis höchstens 3:1, Gesamtpixel 655.360 bis 8.294.400; hier zusätzlich höchstens 2048 je Kante (Kosten, Speicher).</summary>
    public static bool IsValidSize(string size)
    {
        var parts = size.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h)) return false;
        if (w < 16 || h < 16 || w % 16 != 0 || h % 16 != 0 || w > 2048 || h > 2048) return false;
        var px = (long)w * h;
        return px >= 655_360 && px <= 8_294_400 && Math.Max(w, h) <= 3 * Math.Min(w, h);
    }
}
