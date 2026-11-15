using System.Text.Json.Serialization;

namespace LearnCards.Web.Domain;

/// <summary>Grenzen der persönlichen Avatar-Galerie und des Austauschformats (ADR 0057). Serverseitig durchgesetzt, nie nur in der Oberfläche.</summary>
public static class GalleryLimits
{
    public const int MaxEntriesPerUser = 50;
    public const long MaxTotalBytesPerUser = 50L * 1024 * 1024;
    public const int MaxImageBytes = 1_500_000;
    public const int MaxNameLength = 60;
    public const int MaxDescriptionLength = 200;

    // Import (Archiv)
    public const long MaxArchiveBytes = 64L * 1024 * 1024;
    public const long MaxTotalUncompressedBytes = 96L * 1024 * 1024;
    public const int MaxManifestBytes = 1024 * 1024;
    public const int MaxFilesInArchive = 120;
    public const int MaxAvatarsInArchive = 50;
    public const int MaxPathLength = 120;
    /// <summary>Höchstes Verhältnis entpackt/gepackt für Einträge über 64 KB (ZIP-Bomben); Bilder liegen unkomprimiert, das Manifest komprimiert nur mäßig.</summary>
    public const int MaxCompressionRatio = 200;

    public const int MinPasswordLength = 8;
    public const int RecommendedPasswordLength = 12;
    public const int MaxPasswordLength = 256;
}

/// <summary>
/// Die fünf Stimmungen (Kennungen wie <see cref="Avatars.States"/>). Bei KI-Bildern sind es <b>echte, einzeln erzeugte Bilddateien</b> derselben Figur (ADR 0058), bei SVG-Avataren Darstellungsparameter.
/// Ein Eintrag ohne Stimmung (Altbestand) gilt als Neutral.
/// </summary>
public static class AvatarMoods
{
    public const string Neutral = "neutral";
    public static readonly IReadOnlyList<(string Id, string Label, string Slug)> All = new[]
    {
        ("neutral", "Neutral", "neutral"), ("happy", "Freundlich", "freundlich"), ("motivating", "Motivierend", "motivierend"), ("success", "Erfolgreich", "erfolgreich"), ("encouraging", "Ermutigend", "ermutigend"),
    };
    public static bool IsValid(string? id) => All.Any(m => m.Id == id);
    public static string Normalize(string? id) => string.IsNullOrEmpty(id) ? Neutral : id;
    public static string Label(string? id) => All.FirstOrDefault(m => m.Id == Normalize(id)).Label ?? "";
    public static string Slug(string? id) => All.FirstOrDefault(m => m.Id == Normalize(id)).Slug ?? "neutral";

    /// <summary>Beschreibung des Ausdrucks für den Bildauftrag (Mimik, Augenpartie, Mund, Kopfhaltung).</summary>
    public static string PromptText(string mood) => mood switch
    {
        "happy" => "Mood FRIENDLY: a warm, natural smile, friendly open gaze, open and likeable.",
        "motivating" => "Mood MOTIVATING: a confident, energetic expression, alert and attentive eyes, a positive smile, head held slightly up.",
        "success" => "Mood SUCCESSFUL: a proud, satisfied smile, self-assured radiance, upright confident posture.",
        "encouraging" => "Mood ENCOURAGING: an understanding, attentive expression turned toward the viewer, a gentle smile, calm supportive presence, head tilted very slightly.",
        _ => "Mood NEUTRAL: a relaxed, natural expression, calm gaze, subtle or no smile.",
    };
}

/// <summary>Ein gespeicherter Galerieeintrag (Metadaten; die Bilddaten werden getrennt geladen).</summary>
public sealed record GalleryEntry(string Id, string GroupId, string Kind, string Name, string Description, string Style, string Mood,
    string Mime, int ByteSize, string Sha256, string SvgId, bool Favorite, DateTime CreatedAt, string LegacyVersion, string FigureId = "")
{
    /// <summary>Schlüssel der Figur: Einträge mit gleicher <c>FigureId</c> sind Stimmungsvarianten derselben Comicfigur; Altbestand ohne Figur ist eine Figur für sich.</summary>
    public string FigureKey => FigureId.Length > 0 ? FigureId : "e:" + Id;
    public string EffectiveMood => AvatarMoods.Normalize(Mood);
    public const string KindSvg = "svg";
    public const string KindImage = "image";
    public bool IsImage => Kind == KindImage;
}

/// <summary>Neuer Galerieeintrag (noch ohne Kennung): KI-Bild (Bytes) oder SVG-Baukasten (gültige Kennung <c>custom:…</c> bzw. Voreinstellung).</summary>
public sealed record NewGalleryEntry(string Kind, string Name, string Description, string Style, byte[]? Bytes, string? SvgId, bool Favorite = false, DateTime? CreatedAt = null, string? GroupKey = null, string? FigureKey = null, string Mood = "");

/// <summary>Manifest des Austauschformats <c>learny-avatar-gallery</c> (Datei <c>manifest.json</c>, Formatversion 1; siehe ADR 0057). Unbekannte optionale Felder werden beim Lesen ignoriert.</summary>
public sealed class GalleryManifest
{
    public const string FormatId = "learny-avatar-gallery";
    public const int CurrentVersion = 1;
    public const string FileName = "manifest.json";

    [JsonPropertyName("format")] public string Format { get; set; } = FormatId;
    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("exported_at")] public string ExportedAt { get; set; } = "";
    [JsonPropertyName("groups")] public List<ManifestGroup> Groups { get; set; } = new();
    [JsonPropertyName("avatars")] public List<ManifestAvatar> Avatars { get; set; } = new();
}

public sealed class ManifestGroup
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class ManifestAvatar
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    /// <summary><c>image</c> (KI-Bild) oder <c>svg</c> (Baukasten-Parameter).</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("style")] public string? Style { get; set; }
    /// <summary>Stimmung eines KI-Bildes (<c>neutral|happy|motivating|success|encouraging</c>); fehlt bei Altbestand (= neutral) und bei SVG-Avataren (deren Stimmungen sind Darstellungsparameter).</summary>
    [JsonPropertyName("mood")] public string? Mood { get; set; }
    /// <summary>Archivinterne Kennung der Figur (optional, seit ADR 0058): Bilder mit gleicher Figur sind Stimmungsvarianten derselben Comicfigur. Ältere Leser ignorieren das Feld.</summary>
    [JsonPropertyName("figure")] public string? Figure { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("favorite")] public bool Favorite { get; set; }
    [JsonPropertyName("file")] public string? File { get; set; }
    [JsonPropertyName("mime")] public string? Mime { get; set; }
    [JsonPropertyName("bytes")] public int? Bytes { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("svg")] public ManifestSvg? Svg { get; set; }
}

/// <summary>Vollständige Parameter einer SVG-Baukasten-Figur; beim Import wird die Darstellung daraus rekonstruiert (alle Stimmungen sind Darstellungsparameter derselben Figur).</summary>
public sealed class ManifestSvg
{
    [JsonPropertyName("skin")] public string Skin { get; set; } = "";
    [JsonPropertyName("hair")] public string Hair { get; set; } = "";
    [JsonPropertyName("hair_style")] public string HairStyle { get; set; } = "";
    [JsonPropertyName("outfit")] public string Outfit { get; set; } = "";
    [JsonPropertyName("accessory")] public string Accessory { get; set; } = "";
    [JsonPropertyName("face")] public string? Face { get; set; }
    [JsonPropertyName("beard")] public string? Beard { get; set; }
    /// <summary>Darstellbare Stimmungen dieser Figur (reine Darstellungsparameter, keine eigenen Bilddateien).</summary>
    [JsonPropertyName("moods")] public List<string>? Moods { get; set; }
    /// <summary>Kennung einer Baukasten-Voreinstellung (z. B. „luna“), falls die Figur eine solche ist; dann sind die Parameter zusätzlich ausgeschrieben.</summary>
    [JsonPropertyName("preset")] public string? Preset { get; set; }
}
