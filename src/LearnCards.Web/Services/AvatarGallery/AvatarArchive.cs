using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services.AvatarGallery;

/// <summary>Ein aus dem Archiv gelesener, vollständig geprüfter Avatar (noch nicht importiert; Archiv-IDs sind nie interne IDs).</summary>
public sealed record ParsedAvatar(string ArchiveId, string GroupKey, string GroupName, string Kind, string Name, string Description, string Style, bool Favorite,
    DateTime? CreatedAt, byte[]? Bytes, string Mime, string Sha256, string? SvgId, string? FigureKey = null, string Mood = "");

/// <summary>Ergebnis des Prüfens eines Archivs: nur gültige Avatare; <see cref="IgnoredFiles"/> = nicht referenzierte Dateien (werden nicht übernommen).</summary>
public sealed record ParsedArchive(int Version, DateTime? ExportedAt, IReadOnlyList<ParsedAvatar> Avatars, int IgnoredFiles);

/// <summary>
/// Austauschformat <c>learny-avatar-gallery</c> Version 1 (ADR 0057): ZIP mit <c>manifest.json</c> und <c>avatars/avatar-NNN.webp|png|jpg</c>. Dateinamen entstehen nur aus einer Zählung,
/// nie aus Benutzereingaben. Das Manifest enthält keine Benutzerkennungen, Kontodaten, Schlüssel, Passwörter oder Originalfotos.
/// </summary>
public static class AvatarArchive
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Baut das Archiv aus Galerieeinträgen (Bilder über <paramref name="imageOf"/>). Verursacht keine KI-Aufrufe; Passwort optional (AES-256).</summary>
    public static byte[] Build(IReadOnlyList<GalleryEntry> entries, Func<GalleryEntry, byte[]?> imageOf, string? password, DateTime? now = null)
    {
        var t = now ?? DateTime.UtcNow;
        var files = new List<ZipFileData>();
        var manifest = new GalleryManifest { ExportedAt = t.ToString("o", CultureInfo.InvariantCulture) };
        var groupIds = new Dictionary<string, string>(); var figureIds = new Dictionary<string, string>();
        var n = 0;
        foreach (var e in entries)
        {
            if (!groupIds.TryGetValue(e.GroupId, out var gid)) { gid = "g" + (groupIds.Count + 1).ToString("D3", CultureInfo.InvariantCulture); groupIds[e.GroupId] = gid; manifest.Groups.Add(new ManifestGroup { Id = gid, Name = e.Name }); }
            n++;
            var a = new ManifestAvatar
            {
                Id = "a" + n.ToString("D3", CultureInfo.InvariantCulture), Group = gid, Type = e.Kind, Name = e.Name, Description = e.Description.Length > 0 ? e.Description : null,
                Style = e.Style.Length > 0 ? e.Style : null, Mood = e.IsImage && e.Mood.Length > 0 ? e.Mood : null, CreatedAt = e.CreatedAt.ToString("o", CultureInfo.InvariantCulture), Favorite = e.Favorite,
            };
            if (e.IsImage)
            {
                if (e.FigureId.Length > 0) { if (!figureIds.TryGetValue(e.FigureId, out var fid)) { fid = "f" + (figureIds.Count + 1).ToString("D3", CultureInfo.InvariantCulture); figureIds[e.FigureId] = fid; } a.Figure = fid; }
                var bytes = imageOf(e) ?? throw new ArchiveException("Ein Avatarbild fehlt und kann nicht exportiert werden.");
                var ext = e.Mime switch { "image/webp" => "webp", "image/png" => "png", "image/jpeg" => "jpg", _ => throw new ArchiveException("Unbekanntes Bildformat.") };
                var file = $"avatars/avatar-{n.ToString("D3", CultureInfo.InvariantCulture)}" + (e.Mood.Length > 0 ? "-" + AvatarMoods.Slug(e.Mood) : "") + $".{ext}";
                files.Add(new ZipFileData(file, bytes));
                a.File = file; a.Mime = e.Mime; a.Bytes = bytes.Length; a.Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            }
            else
            {
                var p = Avatars.Find(e.SvgId) ?? throw new ArchiveException("Ein Baukasten-Avatar ist ungültig und kann nicht exportiert werden.");
                a.Svg = new ManifestSvg
                {
                    Skin = p.Skin, Hair = p.Hair, HairStyle = p.HairStyle, Outfit = p.Outfit, Accessory = p.Accessory, Face = p.Face, Beard = p.Beard,
                    Moods = Avatars.States.OrderBy(x => x, StringComparer.Ordinal).ToList(),   // Darstellungsparameter, keine eigenen Bilder
                    Preset = Avatars.All.Any(x => x.Id == e.SvgId) ? e.SvgId : null,
                };
            }
            manifest.Avatars.Add(a);
        }
        files.Insert(0, new ZipFileData(GalleryManifest.FileName, JsonSerializer.SerializeToUtf8Bytes(manifest, Json)));
        return SecureZip.Build(files, password, t);
    }

    /// <summary>Liest und prüft ein Archiv vollständig im Speicher. Jede Abweichung ergibt <see cref="ArchiveException"/>; es gibt keine Teilergebnisse.</summary>
    public static ParsedArchive Parse(byte[] archive, string? password)
    {
        var files = SecureZip.Read(archive, password);
        var byName = files.ToDictionary(f => f.Name, StringComparer.Ordinal);
        if (!byName.TryGetValue(GalleryManifest.FileName, out var mf)) throw new ArchiveException("Das ist kein Learny-Avatar-Archiv (manifest.json fehlt).");
        if (mf.Data.Length > GalleryLimits.MaxManifestBytes) throw new ArchiveException("Das Manifest ist zu groß.");
        GalleryManifest m;
        try { m = JsonSerializer.Deserialize<GalleryManifest>(mf.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = false }) ?? throw new JsonException(); }
        catch (JsonException) { throw new ArchiveException("Das Manifest ist kein gültiges JSON im Format „learny-avatar-gallery“."); }
        if (m.Format != GalleryManifest.FormatId) throw new ArchiveException("Das ist kein Learny-Avatar-Archiv (unbekanntes Format).");
        if (m.Version < 1 || m.Version > GalleryManifest.CurrentVersion)
            throw new ArchiveException($"Das Archiv hat die Formatversion {m.Version}. Diese Learny-Version unterstützt Version {GalleryManifest.CurrentVersion}. Bitte aktualisiere Learny oder exportiere erneut.");
        if (m.Avatars is null || m.Avatars.Count == 0) throw new ArchiveException("Das Archiv enthält keine Avatare.");
        if (m.Avatars.Count > GalleryLimits.MaxAvatarsInArchive) throw new ArchiveException($"Das Archiv enthält zu viele Avatare (höchstens {GalleryLimits.MaxAvatarsInArchive}).");

        var groups = (m.Groups ?? new()).Where(g => !string.IsNullOrEmpty(g.Id)).GroupBy(g => g.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Name ?? "", StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal); var used = new HashSet<string>(StringComparer.Ordinal); var result = new List<ParsedAvatar>();
        foreach (var a in m.Avatars)
        {
            if (string.IsNullOrWhiteSpace(a.Id) || a.Id.Length > 40 || !ids.Add(a.Id)) throw new ArchiveException("Das Manifest enthält fehlende oder doppelte Avatar-Kennungen.");
            var name = CleanText(a.Name, GalleryLimits.MaxNameLength); if (name.Length == 0) name = "Importierter Avatar";
            var desc = CleanText(a.Description, GalleryLimits.MaxDescriptionLength);
            var gkey = string.IsNullOrEmpty(a.Group) || a.Group.Length > 40 ? "g-" + a.Id : a.Group;
            var gname = groups.TryGetValue(gkey, out var gn) ? CleanText(gn, GalleryLimits.MaxNameLength) : "";
            DateTime? created = DateTime.TryParse(a.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var c) && c <= DateTime.UtcNow.AddMinutes(5) && c.Year >= 2000 ? c : null;
            if (a.Type == GalleryEntry.KindImage)
            {
                if (string.IsNullOrEmpty(a.File)) throw new ArchiveException("Ein Bildavatar im Manifest verweist auf keine Datei.");
                try { SecureZip.ValidateName(Encoding.UTF8.GetBytes(a.File), true); } catch (ArchiveException) { throw new ArchiveException("Das Manifest enthält unzulässige Dateiverweise (nur Dateien im Archiv, keine Adressen oder Pfadtricks)."); }
                if (!a.File.StartsWith("avatars/", StringComparison.Ordinal) || !byName.TryGetValue(a.File, out var f)) throw new ArchiveException("Das Manifest verweist auf eine Bilddatei, die im Archiv fehlt.");
                if (f.Data.Length > GalleryLimits.MaxImageBytes) throw new ArchiveException("Ein Bild im Archiv ist zu groß.");
                var mime = AvatarPhoto.DetectMime(f.Data) ?? throw new ArchiveException("Eine Datei im Archiv ist kein gültiges Bild (erlaubt: WebP, PNG, JPEG).");
                if (a.Mime is not null && a.Mime != mime) throw new ArchiveException("Der angegebene Bildtyp passt nicht zum Inhalt der Datei.");
                var sha = Convert.ToHexString(SHA256.HashData(f.Data)).ToLowerInvariant();
                if (!string.Equals(a.Sha256, sha, StringComparison.OrdinalIgnoreCase)) throw new ArchiveException("Die Prüfsumme eines Bildes stimmt nicht: Das Archiv wurde verändert oder ist beschädigt.");
                if (!used.Add(a.File)) throw new ArchiveException("Mehrere Avatare verweisen auf dieselbe Datei.");
                var style = AvatarImageStyles.IsValid(a.Style) ? a.Style! : "";
                var mood = AvatarMoods.IsValid(a.Mood) ? a.Mood! : "";   // unbekannte/fehlende Stimmung (ältere Archive) = Altbestand ohne Stimmung
                var fkey = string.IsNullOrEmpty(a.Figure) || a.Figure.Length > 40 ? null : a.Figure;
                result.Add(new ParsedAvatar(a.Id, gkey, gname, GalleryEntry.KindImage, name, desc, style, a.Favorite, created, f.Data, mime, sha, null, fkey, mood));
            }
            else if (a.Type == GalleryEntry.KindSvg)
            {
                var s = a.Svg ?? throw new ArchiveException("Ein Baukasten-Avatar im Manifest hat keine Parameter.");
                var p = Avatars.Custom(s.Skin ?? "", s.Hair ?? "", s.HairStyle ?? "", s.Outfit ?? "", s.Accessory ?? "", string.IsNullOrEmpty(s.Face) ? "oval" : s.Face, string.IsNullOrEmpty(s.Beard) ? "none" : s.Beard)
                    ?? throw new ArchiveException("Ein Baukasten-Avatar hat ungültige Parameter (nur geprüfte Farben und feste Auswahllisten sind erlaubt).");
                var svgId = !string.IsNullOrEmpty(s.Preset) && Avatars.All.Any(x => x.Id == s.Preset && x.Skin == p.Skin && x.Hair == p.Hair && x.HairStyle == p.HairStyle) ? s.Preset! : p.Id;
                result.Add(new ParsedAvatar(a.Id, gkey, gname, GalleryEntry.KindSvg, name, desc, "svg", a.Favorite, created, null, "", "", svgId));
            }
            else throw new ArchiveException("Das Manifest enthält einen unbekannten Avatar-Typ.");
        }
        DateTime? exported = DateTime.TryParse(m.ExportedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ex) ? ex : null;
        return new ParsedArchive(m.Version, exported, result, files.Count(f => f.Name != GalleryManifest.FileName && !used.Contains(f.Name)));
    }

    /// <summary>Entfernt Steuerzeichen und kürzt; Ausgabe in der Oberfläche erfolgt ohnehin kodiert.</summary>
    public static string CleanText(string? s, int max)
    {
        var t = new string((s ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return t.Length > max ? t[..max].Trim() : t;
    }
}
