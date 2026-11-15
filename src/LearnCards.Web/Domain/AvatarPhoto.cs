namespace LearnCards.Web.Domain;

/// <summary>
/// Prüfung eines hochgeladenen Fotos, das nur als Inspiration für den Comic-Avatar dient (ADR 0055).
/// Maßgeblich ist der tatsächliche Dateiinhalt (Signatur), nie Dateiname oder vom Browser gemeldeter Typ. Das Foto wird nur im Arbeitsspeicher verarbeitet.
/// </summary>
public static class AvatarPhoto
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public const int MinBytes = 256;
    public const string AllowedFormatsText = "JPG, PNG oder WEBP";

    /// <summary>Liefert den Medientyp (image/jpeg, image/png, image/webp) oder null, wenn die Bytes kein zulässiges Bild darstellen.</summary>
    public static string? DetectMime(ReadOnlySpan<byte> b)
    {
        if (b.Length < MinBytes || b.Length > MaxBytes) return null;
        // JPEG: FF D8 FF … und Ende-Marker FF D9 (abgeschnittene Dateien werden abgewiesen)
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return LastNonZero(b) is { } e && e >= 1 && b[e - 1] == 0xFF && b[e] == 0xD9 ? "image/jpeg" : null;
        // PNG: Signatur, erster Chunk IHDR, Dateiende mit IEND
        if (b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) && b[12] == 'I' && b[13] == 'H' && b[14] == 'D' && b[15] == 'R'
            && b[^8] == 'I' && b[^7] == 'E' && b[^6] == 'N' && b[^5] == 'D') return "image/png";
        // WEBP: RIFF <Größe> WEBP, Größenfeld passt zur Dateilänge
        if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P'
            && BitConverter.ToUInt32(b.Slice(4, 4)) + 8 <= (uint)b.Length) return "image/webp";
        return null;
    }

    private static int? LastNonZero(ReadOnlySpan<byte> b) { for (var i = b.Length - 1; i >= 0; i--) if (b[i] != 0) return i; return null; }
}
