using System.Text.RegularExpressions;

namespace LearnCards.Web.Services.ImageStorage;

/// <summary>Fehlerklassen der Bildspeicher; die API bildet sie einheitlich auf HTTP-Antworten ab (provider-unabhängig).</summary>
public enum ImageStorageError
{
    /// <summary>Das Bild existiert im Speicher nicht (mehr).</summary>
    NotFound,
    /// <summary>Der Speicher ist nicht erreichbar oder antwortet mit einem Serverfehler/Throttling.</summary>
    Unavailable,
    Timeout,
    /// <summary>Zugangsdaten abgelehnt oder fehlende Berechtigung (z. B. fehlende Rolle der Managed Identity).</summary>
    Unauthorized,
    /// <summary>Der Provider ist nicht (vollständig) konfiguriert.</summary>
    NotConfigured,
    /// <summary>Die gelesenen Daten passen nicht zur gespeicherten Prüfsumme.</summary>
    IntegrityFailed,
    Invalid,
}

public sealed class ImageStorageException : Exception
{
    public ImageStorageError Kind { get; }
    public string Provider { get; }

    public ImageStorageException(ImageStorageError kind, string provider, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Provider = provider;
    }
}

/// <summary>Ergebnis eines Leseversuchs. <see cref="NotModified"/> = der übergebene ETag ist noch aktuell (keine Daten übertragen).</summary>
public sealed record StoredImage(byte[] Data, string? ETag, bool NotModified = false);

public sealed record ProviderHealth(bool Ok, string Message, TimeSpan Latency);

/// <summary>
/// Gemeinsame Schnittstelle aller Bildspeicher (ADR 0017). Arbeitet ausschließlich mit Binärdaten (keine Base64-Strings) und
/// <b>serverseitig erzeugten</b> Schlüsseln (<see cref="ImageStorageKey"/>); Metadaten und Berechtigungen liegen in der LearnCards-Datenbank.
/// </summary>
public interface IImageStorageProvider
{
    /// <summary>"database", "local", "azureblob", "s3" oder "sharepoint".</summary>
    string Name { get; }

    /// <summary>Entfernter Speicher (Netzwerkzugriff): Kandidat für den Bild-Cache. Database/Local lesen direkt.</summary>
    bool IsRemote { get; }

    /// <summary>Speichert das Bild unter dem Schlüssel (überschreibt idempotent). Liefert den ETag, sofern der Speicher einen liefert.</summary>
    Task<string?> PutAsync(string key, byte[] data, string mimeType, CancellationToken ct = default);

    /// <summary>Liest das Bild; <c>null</c> = nicht vorhanden. Mit <paramref name="ifNoneMatch"/> Revalidierung (NotModified), wo der Speicher ETags kennt.</summary>
    Task<StoredImage?> GetAsync(string key, string? ifNoneMatch = null, CancellationToken ct = default);

    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>Entfernt das Bild (nicht vorhanden = kein Fehler).</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>Prüft Erreichbarkeit und Berechtigung (z. B. Schreib-/Lese-Probe); wirft nicht, sondern berichtet.</summary>
    Task<ProviderHealth> CheckHealthAsync(CancellationToken ct = default);
}

/// <summary>
/// Speicherschlüssel: 32 Kleinbuchstaben/Ziffern (GUID in "N"-Form), ausschließlich vom Server erzeugt. Jeder Provider
/// validiert den Schlüssel erneut — Pfad-, Header- oder URL-Manipulation über Schlüssel ist dadurch ausgeschlossen.
/// </summary>
public static partial class ImageStorageKey
{
    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static string New() => Guid.NewGuid().ToString("N");

    public static bool IsValid(string? key) => key is not null && Pattern().IsMatch(key);

    public static string Require(string provider, string? key) =>
        IsValid(key) ? key! : throw new ImageStorageException(ImageStorageError.Invalid, provider, "Ungültiger Speicherschlüssel.");
}
