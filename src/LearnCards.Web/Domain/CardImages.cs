namespace LearnCards.Web.Domain;

/// <summary>Optionales Bild einer Lernkarte (ADR 0016): Base64 im Karten-JSON, gespeichert in <c>cards.images_json</c>.</summary>
public class CardImage
{
    /// <summary>
    /// Interne Bild-ID (ADR 0017). Im Import optional: ein Eintrag <b>nur</b> mit <c>image_id</c> (ohne <c>data_base64</c>) behält ein vorhandenes Bild dieser Karte
    /// (z. B. beim Zurückschreiben einer gelesenen Karte). Bei neuen Bildern wird die ID vom Server vergeben.
    /// </summary>
    public string ImageId { get; set; } = "";
    /// <summary>Nur serverseitig: zugehörige Karte (nie aus dem Import übernehmen).</summary>
    public string CardId { get; set; } = "";
    /// <summary>Nur serverseitig: Bildspeicher-Provider. Eingehende Werte werden ignoriert.</summary>
    public string Provider { get; set; } = "";
    /// <summary>Nur serverseitig: Schlüssel im Bildspeicher. Eingehende Werte werden ignoriert (sonst wären fremde Bilder referenzierbar).</summary>
    public string StorageKey { get; set; } = "";
    public int SizeBytes { get; set; }
    /// <summary>SHA-256 (Hex, klein) der Bilddatei; dient der Integritätsprüfung und als Cache-Versionskennung.</summary>
    public string Checksum { get; set; } = "";
    public int SortOrder { get; set; }

    /// <summary>image/png, image/jpeg oder image/webp — muss zur tatsächlichen Bildsignatur passen.</summary>
    public string MimeType { get; set; } = "";
    /// <summary>Standard-Base64 der Bilddatei, ohne "data:"-Präfix. Nur im Import (und auf ausdrücklichen Wunsch beim Auslesen) befüllt.</summary>
    public string DataBase64 { get; set; } = "";
    /// <summary>Alternativtext für Screenreader und wenn das Bild nicht angezeigt werden kann.</summary>
    public string AltText { get; set; } = "";
    /// <summary>Optionale Bildunterschrift.</summary>
    public string Caption { get; set; } = "";

    /// <summary>Größe der dekodierten Bilddatei in Bytes (aus der Base64-Länge berechnet, ohne Dekodierung).</summary>
    public int DecodedSize
    {
        get
        {
            var n = DataBase64.Length;
            if (n == 0) return 0;
            var padding = DataBase64.EndsWith("==", StringComparison.Ordinal) ? 2 : DataBase64.EndsWith('=') ? 1 : 0;
            return n / 4 * 3 - padding;
        }
    }
}

/// <summary>
/// Zentrale, vorläufige Produktlimits für Kartenbilder (ADR 0016). Änderungen hier wirken auf Validierung, Live-Schema
/// (<c>/api/schema/import/cards</c>), MCP-Beschreibungen und Hilfe — die Dokumentation daraus ableiten bzw. mitziehen.
/// </summary>
public static class CardImageLimits
{
    public const int MaxImagesPerCard = 5;
    /// <summary>Maximale Größe der dekodierten Bilddatei: 2 MiB.</summary>
    public const int MaxDecodedBytes = 2 * 1024 * 1024;
    public const int MaxTextLength = 300;
    public static readonly string[] AllowedMimeTypes = { "image/png", "image/jpeg", "image/webp" };

    /// <summary>Maximale Länge des Base64-Texts (vor dem Dekodieren geprüft, damit riesige Eingaben nicht erst allokiert werden).</summary>
    public static int MaxBase64Chars => (MaxDecodedBytes + 2) / 3 * 4;

    public static bool IsAllowedMimeType(string? mime) =>
        mime is not null && AllowedMimeTypes.Contains(mime, StringComparer.Ordinal);
}

/// <summary>Validiert Kartenbilder serverseitig und normalisiert sie (Leerraum im Base64 entfernt, MIME-Typ kleingeschrieben).</summary>
public static class CardImageValidator
{
    /// <summary>
    /// Prüft Anzahl, MIME-Allowlist, Base64-Kodierung, dekodierte Größe und die Dateisignatur. Null/leer = keine Bilder = gültig.
    /// Die Methode ist idempotent und verändert die Einträge in-place (Normalisierung).
    /// </summary>
    public static (bool Ok, string Error) ValidateAndNormalize(List<CardImage>? images)
    {
        if (images is null || images.Count == 0) return (true, "");
        if (images.Count > CardImageLimits.MaxImagesPerCard)
            return (false, $"Höchstens {CardImageLimits.MaxImagesPerCard} Bilder pro Karte erlaubt (übergeben: {images.Count}).");

        for (var i = 0; i < images.Count; i++)
        {
            var label = $"Bild {i + 1}";
            var img = images[i];
            if (img is null) return (false, $"{label}: Eintrag ist leer.");

            img.MimeType = (img.MimeType ?? "").Trim().ToLowerInvariant();
            img.AltText = (img.AltText ?? "").Trim();
            img.Caption = (img.Caption ?? "").Trim();

            // Eintrag ohne Bilddaten, aber mit image_id: vorhandenes Bild behalten (Zuordnung zur Karte prüft der Server beim Speichern)
            if (string.IsNullOrWhiteSpace(img.DataBase64) && !string.IsNullOrWhiteSpace(img.ImageId))
            {
                if (img.AltText.Length > CardImageLimits.MaxTextLength || img.Caption.Length > CardImageLimits.MaxTextLength)
                    return (false, $"{label}: alt_text und caption dürfen höchstens {CardImageLimits.MaxTextLength} Zeichen lang sein.");
                continue;
            }

            if (!CardImageLimits.IsAllowedMimeType(img.MimeType))
                return (false, $"{label}: mime_type '{img.MimeType}' nicht erlaubt (erlaubt: {string.Join(", ", CardImageLimits.AllowedMimeTypes)}).");
            if (img.AltText.Length > CardImageLimits.MaxTextLength || img.Caption.Length > CardImageLimits.MaxTextLength)
                return (false, $"{label}: alt_text und caption dürfen höchstens {CardImageLimits.MaxTextLength} Zeichen lang sein.");

            var raw = img.DataBase64 ?? "";
            if (raw.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return (false, $"{label}: data_base64 muss reines Base64 ohne 'data:'-Präfix sein.");
            var clean = string.Concat(raw.Where(c => !char.IsWhiteSpace(c)));
            if (clean.Length == 0) return (false, $"{label}: data_base64 fehlt.");
            if (clean.Length > CardImageLimits.MaxBase64Chars + 4)
                return (false, $"{label}: Bild zu groß (höchstens {CardImageLimits.MaxDecodedBytes / (1024 * 1024)} MiB dekodiert).");

            var buffer = new byte[clean.Length / 4 * 3 + 3];
            if (!Convert.TryFromBase64String(clean, buffer, out var written) || written == 0)
                return (false, $"{label}: data_base64 ist kein gültiges Standard-Base64.");
            if (written > CardImageLimits.MaxDecodedBytes)
                return (false, $"{label}: Bild zu groß ({written / 1024} KiB; höchstens {CardImageLimits.MaxDecodedBytes / 1024} KiB dekodiert).");

            var actual = DetectMimeType(buffer.AsSpan(0, written));
            if (actual is null)
                return (false, $"{label}: Die Daten sind weder PNG, JPEG noch WebP.");
            if (!string.Equals(actual, img.MimeType, StringComparison.Ordinal))
                return (false, $"{label}: mime_type '{img.MimeType}' passt nicht zu den Bilddaten (erkannt: {actual}).");

            img.DataBase64 = clean;
        }
        return (true, "");
    }

    /// <summary>Erkennt PNG, JPEG und WebP anhand der Dateisignatur; sonst null.</summary>
    public static string? DetectMimeType(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
            return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return "image/jpeg";
        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P')
            return "image/webp";
        return null;
    }
}
