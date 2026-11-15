using System.Globalization;
using System.Security.Cryptography;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services.AvatarGallery;

/// <summary>Wie mit Duplikaten (gleiches Bild bzw. gleiche Baukasten-Figur) beim Import umgegangen wird.</summary>
public enum ImportDuplicates { Skip, ImportAsNew }

public sealed record ImportResult(int Imported, int SkippedDuplicates);

/// <summary>
/// Persönliche Avatar-Galerie (ADR 0057): dauerhaft gespeicherte KI-Bildavatare und Baukasten-Avatare mit Varianten-Gruppen, Favoriten, Export/Import. Jede Methode bekommt die Benutzerkennung
/// des authentifizierten Kontexts und filtert in SQL danach (ADR 0011); fremde Kennungen liefern „nicht gefunden“. Originalfotos werden nie gespeichert.
/// </summary>
public sealed class AvatarGalleryService
{
    private readonly IDatabase _db;
    private readonly CardRepository _repo;
    private readonly AvatarImageService _images;
    public AvatarGalleryService(IDatabase db, CardRepository repo, AvatarImageService images) { _db = db; _repo = repo; _images = images; }

    private const string Cols = "id, group_id, kind, name, description, style, mood, mime, byte_size, sha256, svg_id, favorite, created_at, legacy_version, figure_id";
    private static readonly System.Text.RegularExpressions.Regex IdRx = new("^[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    public static bool IsValidId(string? id) => id is not null && IdRx.IsMatch(id);
    private static string NewId() => Guid.NewGuid().ToString("N");

    private static GalleryEntry Map(Dictionary<string, string?> r) => new(
        r["id"] ?? "", r["group_id"] ?? "", r["kind"] ?? "", r["name"] ?? "", r["description"] ?? "", r["style"] ?? "", r["mood"] ?? "", r["mime"] ?? "",
        DbValue.ToInt(r["byte_size"]), r["sha256"] ?? "", r["svg_id"] ?? "", DbValue.ToInt(r["favorite"]) == 1,
        DateTime.TryParse(r["created_at"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.UtcNow, r["legacy_version"] ?? "", r["figure_id"] ?? "");

    // ─── Lesen ──────────────────────────────────────────────────────────

    /// <summary>Alle Einträge des Benutzers (neueste zuerst). Übernimmt dabei einmalig ein vorhandenes älteres Profilbild (<c>avatar_images</c>) in die Galerie.</summary>
    public async Task<List<GalleryEntry>> ListAsync(string userSub)
    {
        await MigrateLegacyAsync(userSub);
        var rows = await _db.QueryAsync($"SELECT {Cols} FROM avatar_gallery WHERE user_sub = @u ORDER BY created_at DESC, id", new[] { ("u", (object?)userSub) });
        return rows.Select(Map).ToList();
    }

    public async Task<GalleryEntry?> GetAsync(string userSub, string id)
    {
        if (!IsValidId(id)) return null;
        var rows = await _db.QueryAsync($"SELECT {Cols} FROM avatar_gallery WHERE user_sub = @u AND id = @id", new[] { ("u", (object?)userSub), ("id", id) });
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    /// <summary>Bilddaten eines eigenen Bildeintrags (Besitzprüfung in SQL), sonst null.</summary>
    public async Task<(byte[] Bytes, string Mime)?> GetImageAsync(string userSub, string id)
    {
        if (!IsValidId(id)) return null;
        var rows = await _db.QueryAsync("SELECT mime, data_b64 FROM avatar_gallery WHERE user_sub = @u AND id = @id AND kind = 'image'", new[] { ("u", (object?)userSub), ("id", id) });
        return rows.Count == 0 ? null : (Convert.FromBase64String(rows[0]["data_b64"] ?? ""), rows[0]["mime"] ?? "image/webp");
    }

    /// <summary>Bild des aktiven Profilavatars: <c>gal:&lt;id&gt;</c> aus der Galerie, sonst ein älteres <c>img:</c>-Bild aus <c>avatar_images</c>. Nur das eigene.</summary>
    /// <summary>Kennung des Galerieeintrags hinter dem aktiven Bildavatar (<c>gal:</c> direkt, älteres <c>img:</c> über die einmalige Übernahme in die Galerie), sonst null.</summary>
    private async Task<string?> ActiveEntryIdAsync(string userSub, string preset)
    {
        if (Avatars.IsGalleryId(preset)) return Avatars.GalleryEntryId(preset);
        if (!Avatars.IsImageId(preset)) return null;
        await MigrateLegacyAsync(userSub);
        var rows = await _db.QueryAsync("SELECT id FROM avatar_gallery WHERE user_sub = @u AND legacy_version = @v", new (string, object?)[] { ("u", userSub), ("v", Avatars.ImageVersionOf(preset)) });
        return rows.Count == 0 ? null : rows[0]["id"];
    }

    public async Task<(byte[] Bytes, string Mime)?> GetActiveImageAsync(string userSub, string? mood = null)
    {
        var (preset, _) = await _repo.GetAvatarAsync(userSub);
        if (await ActiveEntryIdAsync(userSub, preset) is { } activeId)
        {
            var id = activeId;
            if (!string.IsNullOrEmpty(mood) && AvatarMoods.IsValid(mood))
            {
                var sibling = await FindMoodEntryAsync(userSub, id, mood);   // echtes Bild dieser Stimmung, sonst die Hauptvariante (nie ein Bild einer anderen Stimmung)
                if (sibling is not null) id = sibling.Id;
            }
            var img = await GetImageAsync(userSub, id);
            if (img is not null) return img;
        }
        if (Avatars.IsImageId(preset)) { var legacy = await _repo.GetAvatarImageAsync(userSub); return legacy is null ? null : (legacy.Value.Bytes, legacy.Value.Mime); }
        return null;
    }

    /// <summary>Stimmungsvariante derselben Figur wie <paramref name="entryId"/> (nur eigene Einträge), sonst null. Neutral = Eintrag mit Stimmung neutral bzw. ohne Stimmung (Altbestand).</summary>
    private async Task<GalleryEntry?> FindMoodEntryAsync(string userSub, string entryId, string mood)
    {
        var e = await GetAsync(userSub, entryId);
        if (e is null || !e.IsImage) return null;
        if (e.EffectiveMood == mood) return e;
        if (e.FigureId.Length == 0) return null;
        var rows = await _db.QueryAsync($"SELECT {Cols} FROM avatar_gallery WHERE user_sub = @u AND figure_id = @f AND kind = 'image'", new (string, object?)[] { ("u", userSub), ("f", e.FigureId) });
        return rows.Select(Map).FirstOrDefault(x => x.EffectiveMood == mood);
    }

    /// <summary>Stimmungen, für die die aktive Figur ein <b>eigenes Bild</b> besitzt (leer bei SVG-Avataren und Altbestand ohne Varianten außer der Hauptvariante).</summary>
    public async Task<HashSet<string>> ActiveMoodsAsync(string userSub)
    {
        var (preset, _) = await _repo.GetAvatarAsync(userSub);
        var set = new HashSet<string>();
        var activeId = await ActiveEntryIdAsync(userSub, preset);
        if (activeId is null) return set;
        var e = await GetAsync(userSub, activeId);
        if (e is null || !e.IsImage) return set;
        set.Add(e.EffectiveMood);
        if (e.FigureId.Length > 0)
            foreach (var r in await _db.QueryAsync("SELECT mood FROM avatar_gallery WHERE user_sub = @u AND figure_id = @f AND kind = 'image'", new (string, object?)[] { ("u", userSub), ("f", e.FigureId) }))
                set.Add(AvatarMoods.Normalize(r["mood"]));
        return set;
    }

    /// <summary>Gibt zurück, ob ein Eintrag der aktive Profilavatar ist (berechnet aus der gespeicherten Auswahl; nie doppelt gespeichert).</summary>
    public static bool IsActive(GalleryEntry e, string activePreset) =>
        e.IsImage ? (Avatars.IsGalleryId(activePreset) && Avatars.GalleryEntryId(activePreset) == e.Id) || (e.LegacyVersion.Length > 0 && Avatars.IsImageId(activePreset) && Avatars.ImageVersionOf(activePreset) == e.LegacyVersion)
                  : e.SvgId.Length > 0 && e.SvgId == activePreset;

    public async Task<string> ActivePresetAsync(string userSub) => (await _repo.GetAvatarAsync(userSub)).Preset;

    // ─── Schreiben ──────────────────────────────────────────────────────

    /// <summary>Speichert Einträge (alles oder nichts) unter einer gemeinsamen Gruppe; ersetzt nie vorhandene Einträge. Grenzen: Anzahl, Gesamtgröße, Bildgröße und -inhalt.</summary>
    public async Task<List<GalleryEntry>> AddAsync(string userSub, IReadOnlyList<NewGalleryEntry> items, string? groupId = null)
    {
        if (items.Count == 0) return new();
        await MigrateLegacyAsync(userSub);
        var (count, bytes) = await UsageAsync(userSub);
        var newBytes = items.Sum(i => (long)(i.Bytes?.Length ?? 0));
        if (count + items.Count > GalleryLimits.MaxEntriesPerUser) throw new ArgumentException($"Deine Galerie ist voll (höchstens {GalleryLimits.MaxEntriesPerUser} Einträge). Lösche zuerst Einträge.");
        if (bytes + newBytes > GalleryLimits.MaxTotalBytesPerUser) throw new ArgumentException($"Der Speicher deiner Galerie ist voll (höchstens {GalleryLimits.MaxTotalBytesPerUser / (1024 * 1024)} MB). Lösche zuerst Einträge.");
        var groups = new Dictionary<string, string>(); var figures = new Dictionary<string, string>();
        var batch = new List<DbStatement>(); var added = new List<GalleryEntry>();
        var now = DateTime.UtcNow;
        var fixedGroup = groupId is not null && IsValidId(groupId) ? groupId : null;
        foreach (var it in items)
        {
            string mime = "", svg = "", sha = "", b64 = ""; var size = 0;
            if (it.Kind == GalleryEntry.KindImage)
            {
                var b = it.Bytes ?? throw new ArgumentException("Das Avatarbild fehlt.");
                mime = AvatarPhoto.DetectMime(b) ?? throw new ArgumentException("Das Avatarbild ist ungültig (erlaubt: WebP, PNG, JPEG).");
                if (b.Length > GalleryLimits.MaxImageBytes) throw new ArgumentException("Das Avatarbild ist zu groß.");
                size = b.Length; sha = Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant(); b64 = Convert.ToBase64String(b);
            }
            else if (it.Kind == GalleryEntry.KindSvg)
            {
                if (!Avatars.IsValid(it.SvgId) || Avatars.IsImageId(it.SvgId) || Avatars.IsGalleryId(it.SvgId)) throw new ArgumentException("Der Baukasten-Avatar ist ungültig.");
                svg = it.SvgId!;
            }
            else throw new ArgumentException("Unbekannter Avatar-Typ.");
            var gk = it.GroupKey ?? "";
            var mood = it.Kind == GalleryEntry.KindImage ? (string.IsNullOrEmpty(it.Mood) ? AvatarMoods.Neutral : AvatarMoods.IsValid(it.Mood) ? it.Mood : throw new ArgumentException("Unbekannte Stimmung.")) : "";
            var fk = it.FigureKey ?? "";
            var figure = it.Kind == GalleryEntry.KindImage ? (fk.Length > 0 ? (figures.TryGetValue(fk, out var f) ? f : figures[fk] = NewId()) : NewId()) : "";
            var gid = fixedGroup ?? (gk.Length > 0 ? (groups.TryGetValue(gk, out var g) ? g : groups[gk] = NewId()) : NewId());
            var id = NewId();
            var created = it.CreatedAt ?? now;
            var name = AvatarArchive.CleanText(it.Name, GalleryLimits.MaxNameLength); if (name.Length == 0) name = it.Kind == GalleryEntry.KindImage ? "KI-Avatar" : "Baukasten-Avatar";
            var desc = AvatarArchive.CleanText(it.Description, GalleryLimits.MaxDescriptionLength);
            var style = it.Style.Length > 20 ? it.Style[..20] : it.Style;
            batch.Add(new DbStatement($"INSERT INTO avatar_gallery (id, user_sub, group_id, kind, name, description, style, mood, mime, byte_size, sha256, svg_id, data_b64, favorite, created_at, legacy_version, figure_id) VALUES (@id, @u, @g, @k, @n, @d, @s, @mood, @m, @sz, @sha, @svg, @b, @f, @c, '', @fig)",
                new (string, object?)[] { ("id", id), ("u", userSub), ("g", gid), ("k", it.Kind), ("n", name), ("d", desc), ("s", style), ("mood", mood), ("m", mime), ("sz", size), ("sha", sha), ("svg", svg), ("b", b64), ("f", it.Favorite ? 1 : 0), ("c", created), ("fig", figure) }, 1));
            added.Add(new GalleryEntry(id, gid, it.Kind, name, desc, style, mood, mime, size, sha, svg, it.Favorite, created, "", figure));
        }
        await _db.ExecuteBatchAsync(batch);
        return added;
    }

    public async Task RenameAsync(string userSub, string id, string name)
    {
        var n = AvatarArchive.CleanText(name, GalleryLimits.MaxNameLength);
        if (n.Length == 0) throw new ArgumentException("Bitte gib einen Namen ein.");
        if (!IsValidId(id) || await _db.ExecuteAsync("UPDATE avatar_gallery SET name = @n WHERE user_sub = @u AND id = @id", new (string, object?)[] { ("n", n), ("u", userSub), ("id", id) }) == 0)
            throw new KeyNotFoundException("Avatar nicht gefunden.");
    }

    public async Task SetFavoriteAsync(string userSub, string id, bool favorite)
    {
        if (!IsValidId(id) || await _db.ExecuteAsync("UPDATE avatar_gallery SET favorite = @f WHERE user_sub = @u AND id = @id", new (string, object?)[] { ("f", favorite ? 1 : 0), ("u", userSub), ("id", id) }) == 0)
            throw new KeyNotFoundException("Avatar nicht gefunden.");
    }

    /// <summary>Macht einen eigenen Eintrag zum Profilavatar (ausdrückliche Benutzeraktion). Andere Einträge bleiben unverändert.</summary>
    public async Task<string> ActivateAsync(string userSub, string id)
    {
        var e = await GetAsync(userSub, id) ?? throw new KeyNotFoundException("Avatar nicht gefunden.");
        var preset = e.IsImage ? Avatars.GalleryId(e.Id) : e.SvgId;
        await _repo.SaveAvatarAsync(userSub, preset, false);
        return preset;
    }

    /// <summary>Löscht eigene Einträge nach Bestätigung durch den Aufrufer. Der aktive Profilavatar wird nie gelöscht (zuerst einen anderen wählen); alles oder nichts.</summary>
    public async Task<int> DeleteAsync(string userSub, IReadOnlyCollection<string> ids)
    {
        var list = ids.Where(IsValidId).Distinct().ToList();
        if (list.Count == 0) return 0;
        var active = await ActivePresetAsync(userSub);
        var entries = new List<GalleryEntry>();
        foreach (var id in list) entries.Add(await GetAsync(userSub, id) ?? throw new KeyNotFoundException("Avatar nicht gefunden."));
        if (entries.Any(e => IsActive(e, active))) throw new InvalidOperationException("Der aktive Profilavatar kann nicht gelöscht werden. Wähle zuerst einen anderen Avatar.");
        await _db.ExecuteBatchAsync(list.Select(id => new DbStatement("DELETE FROM avatar_gallery WHERE user_sub = @u AND id = @id", new (string, object?)[] { ("u", userSub), ("id", id) }, 1)).ToList());
        return list.Count;
    }

    /// <summary>Löscht eine ganze Variantengruppe des Benutzers (gleiche Regeln wie <see cref="DeleteAsync"/>).</summary>
    public async Task<int> DeleteGroupAsync(string userSub, string groupId)
    {
        if (!IsValidId(groupId)) return 0;
        var ids = (await ListAsync(userSub)).Where(e => e.GroupId == groupId).Select(e => e.Id).ToList();
        return await DeleteAsync(userSub, ids);
    }

    /// <summary>Entfernt die komplette Galerie eines Benutzers (für ein künftiges Löschkonzept bei Kontolöschung); berührt nur dessen Zeilen, auch das ältere <c>avatar_images</c>-Bild.</summary>
    public async Task DeleteAllForUserAsync(string userSub)
    {
        await _db.ExecuteBatchAsync(new[]
        {
            new DbStatement("DELETE FROM avatar_gallery WHERE user_sub = @u", new (string, object?)[] { ("u", userSub) }),
            new DbStatement("DELETE FROM avatar_images WHERE user_sub = @u", new (string, object?)[] { ("u", userSub) }),
        });
    }

    private async Task<(int Count, long Bytes)> UsageAsync(string userSub)
    {
        var r = await _db.QueryAsync("SELECT COUNT(*) AS c, COALESCE(SUM(byte_size), 0) AS b FROM avatar_gallery WHERE user_sub = @u", new[] { ("u", (object?)userSub) });
        return (DbValue.ToInt(r[0]["c"]), long.Parse(r[0]["b"] ?? "0", CultureInfo.InvariantCulture));
    }

    // ─── Migration des älteren Profilbilds ──────────────────────────────

    /// <summary>
    /// Übernimmt ein vorhandenes <c>avatar_images</c>-Bild (ADR 0056) einmalig als Galerieeintrag, ohne die alte Zeile zu verändern oder zu löschen. Idempotent (Marke <c>legacy_version</c>);
    /// das Profilbild bleibt über seine bisherige Kennung <c>img:…</c> aktiv und wird in der Galerie als aktiv gekennzeichnet.
    /// </summary>
    public async Task MigrateLegacyAsync(string userSub)
    {
        var rows = await _db.QueryAsync("SELECT version, mime, byte_size, data_b64, style, created_at FROM avatar_images WHERE user_sub = @u", new[] { ("u", (object?)userSub) });
        if (rows.Count == 0) return;
        var r = rows[0]; var version = r["version"] ?? "";
        if (version.Length == 0) return;
        var exists = await _db.QueryAsync("SELECT 1 AS x FROM avatar_gallery WHERE user_sub = @u AND legacy_version = @v", new (string, object?)[] { ("u", userSub), ("v", version) });
        if (exists.Count > 0) return;
        var created = DateTime.TryParse(r["created_at"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.UtcNow;
        var b64 = r["data_b64"] ?? ""; byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); } catch (FormatException) { return; }
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await _db.ExecuteAsync("INSERT INTO avatar_gallery (id, user_sub, group_id, kind, name, description, style, mood, mime, byte_size, sha256, svg_id, data_b64, favorite, created_at, legacy_version) VALUES (@id, @u, @g, 'image', @n, '', @s, '', @m, @sz, @sha, '', @b, 0, @c, @v)",
            new (string, object?)[] { ("id", NewId()), ("u", userSub), ("g", NewId()), ("n", "Bisheriges Profilbild"), ("s", r["style"] ?? ""), ("m", r["mime"] ?? "image/webp"), ("sz", bytes.Length), ("sha", sha), ("b", b64), ("c", created), ("v", version) });
    }

    // ─── Export ─────────────────────────────────────────────────────────

    /// <summary>Exportiert eigene Einträge (<paramref name="ids"/> = null: gesamte Galerie, sonst die Auswahl) als ZIP, optional AES-256-verschlüsselt. Verursacht keine KI-Aufrufe; fremde Kennungen → nicht gefunden.</summary>
    public async Task<byte[]> ExportAsync(string userSub, IReadOnlyCollection<string>? ids, string? password)
    {
        if (password is not null && password.Length > 0) ValidatePassword(password);
        var all = await ListAsync(userSub);
        List<GalleryEntry> selected;
        if (ids is null) selected = all;
        else
        {
            var set = ids.ToHashSet(StringComparer.Ordinal);
            selected = all.Where(e => set.Contains(e.Id)).ToList();
            if (selected.Count != set.Count) throw new KeyNotFoundException("Avatar nicht gefunden.");
        }
        if (selected.Count == 0) throw new ArgumentException("Es gibt nichts zu exportieren.");
        selected = selected.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        var images = new Dictionary<string, byte[]>();
        foreach (var e in selected.Where(e => e.IsImage)) images[e.Id] = (await GetImageAsync(userSub, e.Id))?.Bytes ?? throw new KeyNotFoundException("Avatar nicht gefunden.");
        return AvatarArchive.Build(selected, e => images.GetValueOrDefault(e.Id), string.IsNullOrEmpty(password) ? null : password);
    }

    /// <summary>Passwortregeln des Exports: mindestens 8 Zeichen (hart); Empfehlung ab 12 Zeichen mit Vielfalt wird in der Oberfläche angezeigt.</summary>
    public static void ValidatePassword(string password)
    {
        if (password.Length < GalleryLimits.MinPasswordLength) throw new ArgumentException($"Das Passwort ist zu kurz (mindestens {GalleryLimits.MinPasswordLength} Zeichen).");
        if (password.Length > GalleryLimits.MaxPasswordLength) throw new ArgumentException("Das Passwort ist zu lang.");
    }

    /// <summary>Qualitätshinweis für die Oberfläche (leer = ausreichend). Keine harte Regel, kein Speichern des Passworts.</summary>
    public static string? PasswordHint(string password)
    {
        if (password.Length < GalleryLimits.RecommendedPasswordLength) return $"Schwach: Nutze mindestens {GalleryLimits.RecommendedPasswordLength} Zeichen.";
        var kinds = new[] { password.Any(char.IsLower), password.Any(char.IsUpper), password.Any(char.IsDigit), password.Any(c => !char.IsLetterOrDigit(c)) }.Count(x => x);
        return kinds < 3 ? "Mäßig: Mische Groß-/Kleinbuchstaben, Ziffern und Sonderzeichen oder nutze eine längere Passphrase." : null;
    }

    // ─── Import ─────────────────────────────────────────────────────────

    /// <summary>Prüft ein hochgeladenes Archiv vollständig (nur Arbeitsspeicher, nichts wird gespeichert). Falsches Passwort oder jede Beschädigung → Ausnahme, keine Teilergebnisse.</summary>
    public ParsedArchive PreviewImport(byte[] archive, string? password) => AvatarArchive.Parse(archive, string.IsNullOrEmpty(password) ? null : password);

    /// <summary>Importiert die gewählten Avatare (Archiv-Kennungen) als <b>neue</b> Einträge mit neuen internen Kennungen (nie fremde IDs), erhält Gruppen, überschreibt nichts und ändert den Profilavatar nicht.</summary>
    public async Task<ImportResult> CommitImportAsync(string userSub, ParsedArchive parsed, IReadOnlyCollection<string> selectedArchiveIds, ImportDuplicates duplicates)
    {
        var sel = selectedArchiveIds.ToHashSet(StringComparer.Ordinal);
        var chosen = parsed.Avatars.Where(a => sel.Contains(a.ArchiveId)).ToList();
        if (chosen.Count == 0) throw new ArgumentException("Es ist nichts zum Import ausgewählt.");
        var existing = await ListAsync(userSub);
        var seenSha = existing.Where(e => e.IsImage).Select(e => e.Sha256).ToHashSet(StringComparer.Ordinal);
        var seenSvg = existing.Where(e => !e.IsImage).Select(e => e.SvgId).ToHashSet(StringComparer.Ordinal);
        var items = new List<NewGalleryEntry>(); var skipped = 0;
        foreach (var a in chosen)
        {
            var dup = a.Kind == GalleryEntry.KindImage ? seenSha.Contains(a.Sha256) : seenSvg.Contains(a.SvgId ?? "");
            if (dup && duplicates == ImportDuplicates.Skip) { skipped++; continue; }
            if (a.Kind == GalleryEntry.KindImage) seenSha.Add(a.Sha256); else seenSvg.Add(a.SvgId ?? "");
            items.Add(new NewGalleryEntry(a.Kind, a.Name, a.Description, a.Style, a.Bytes, a.SvgId, a.Favorite, a.CreatedAt, a.GroupKey, a.FigureKey, a.Mood));
        }
        if (items.Count > 0) await AddAsync(userSub, items);   // eine Transaktion; Grenzen werden vorher geprüft → bei Fehler wird nichts übernommen
        return new ImportResult(items.Count, skipped);
    }

    // ─── Stimmungsvarianten (ADR 0058) ─────────────────────────────────

    /// <summary>Ergebnis einer Stimmungsserie: je Stimmung Erfolg oder Fehlermeldung; erfolgreiche Bilder sind bereits gespeichert.</summary>
    public sealed record MoodRunResult(IReadOnlyList<(string Mood, bool Ok, string? Error)> Items)
    {
        public int Created => Items.Count(i => i.Ok);
        public int Failed => Items.Count(i => !i.Ok);
    }

    /// <summary>Alle Einträge der Figur (gleiche Figur wie <paramref name="entryId"/>), nur eigene; sortiert nach der Reihenfolge der Stimmungen.</summary>
    public async Task<List<GalleryEntry>> FigureEntriesAsync(string userSub, string entryId)
    {
        var e = await GetAsync(userSub, entryId) ?? throw new KeyNotFoundException("Avatar nicht gefunden.");
        if (!e.IsImage) return new() { e };
        if (e.FigureId.Length == 0) return new() { e };
        var rows = await _db.QueryAsync($"SELECT {Cols} FROM avatar_gallery WHERE user_sub = @u AND figure_id = @f AND kind = 'image'", new (string, object?)[] { ("u", userSub), ("f", e.FigureId) });
        return rows.Select(Map).OrderBy(x => AvatarMoods.All.ToList().FindIndex(m => m.Id == x.EffectiveMood)).ToList();
    }

    /// <summary>Fehlende Stimmungen einer Figur (ohne KI-Aufruf).</summary>
    public async Task<List<string>> MissingMoodsAsync(string userSub, string entryId)
    {
        var have = (await FigureEntriesAsync(userSub, entryId)).Select(x => x.EffectiveMood).ToHashSet();
        return AvatarMoods.All.Select(m => m.Id).Where(m => !have.Contains(m)).ToList();
    }

    /// <summary>
    /// Erzeugt Stimmungsbilder <b>nur nach ausdrücklicher Aufforderung</b> (kostenpflichtig): Referenz ist das neutrale Hauptbild der Figur. Vorhandene Stimmungen werden nie überschrieben
    /// (außer über <see cref="RegenerateMoodAsync"/>); die Serie ist sequenziell, Teilerfolge bleiben gespeichert, ein Fehler stoppt die Serie nur bei Limit/Auslastung/fehlender Freischaltung.
    /// </summary>
    public async Task<MoodRunResult> CreateMoodsAsync(string userSub, string entryId, IReadOnlyList<string> moods, CancellationToken ct = default)
    {
        var entry = await GetAsync(userSub, entryId) ?? throw new KeyNotFoundException("Avatar nicht gefunden.");
        if (!entry.IsImage) throw new ArgumentException("Stimmungsbilder gibt es nur für KI-Bildavatare (Baukasten-Avatare haben Darstellungsvarianten).");
        var wanted = moods.Distinct().Where(m => AvatarMoods.IsValid(m) && m != AvatarMoods.Neutral).ToList();
        if (wanted.Count == 0) throw new ArgumentException("Es ist keine Stimmung ausgewählt.");
        var figure = await EnsureFigureAsync(userSub, entry);
        var items = new List<(string, bool, string?)>();
        foreach (var mood in wanted)
        {
            var current = await FigureEntriesAsync(userSub, figure.Id);
            if (current.Any(x => x.EffectiveMood == mood)) continue;   // vorhandene Varianten werden nie überschrieben
            var reference = current.FirstOrDefault(x => x.EffectiveMood == AvatarMoods.Neutral) ?? figure;
            var refBytes = (await GetImageAsync(userSub, reference.Id))?.Bytes ?? throw new KeyNotFoundException("Das Referenzbild fehlt.");
            try
            {
                var r = await _images.GenerateMoodAsync(userSub, StyleOf(figure), refBytes, mood, ct);
                await AddMoodEntryAsync(userSub, figure, mood, r);
                items.Add((mood, true, null));
            }
            catch (AvatarImageException ex)
            {
                items.Add((mood, false, ex.Message));
                if (ex.StopSeries) break;   // Limit/Auslastung/fehlende Freischaltung: keine weiteren (kostenpflichtigen) Aufrufe
            }
        }
        return new MoodRunResult(items);
    }

    /// <summary>Erzeugt <b>eine</b> Stimmung neu und ersetzt das vorhandene Bild dieser Stimmung erst bei Erfolg (Fehler lassen das alte Bild unverändert). Nur auf ausdrückliche Aktion.</summary>
    public async Task RegenerateMoodAsync(string userSub, string entryId, CancellationToken ct = default)
    {
        var entry = await GetAsync(userSub, entryId) ?? throw new KeyNotFoundException("Avatar nicht gefunden.");
        if (!entry.IsImage) throw new ArgumentException("Nur KI-Bilder lassen sich neu erzeugen.");
        var mood = entry.EffectiveMood;
        var figure = await EnsureFigureAsync(userSub, entry);
        var current = await FigureEntriesAsync(userSub, figure.Id);
        var reference = mood == AvatarMoods.Neutral ? null : current.FirstOrDefault(x => x.EffectiveMood == AvatarMoods.Neutral) ?? current.First(x => x.Id != entry.Id);
        if (reference is null) throw new ArgumentException("Das neutrale Hauptbild wird nicht neu erzeugt: Erstelle bei Bedarf einen neuen Avatar im Generator.");
        var refBytes = (await GetImageAsync(userSub, reference.Id))?.Bytes ?? throw new KeyNotFoundException("Das Referenzbild fehlt.");
        var r = await _images.GenerateMoodAsync(userSub, StyleOf(figure), refBytes, mood, ct);   // bei Fehler: Ausnahme, altes Bild bleibt
        var sha = Convert.ToHexString(SHA256.HashData(r.Bytes)).ToLowerInvariant();
        await _db.ExecuteAsync("UPDATE avatar_gallery SET data_b64 = @b, mime = @m, byte_size = @sz, sha256 = @sha, legacy_version = '' WHERE user_sub = @u AND id = @id",
            new (string, object?)[] { ("b", Convert.ToBase64String(r.Bytes)), ("m", r.Mime), ("sz", r.Bytes.Length), ("sha", sha), ("u", userSub), ("id", entry.Id) });
    }

    private static string StyleOf(GalleryEntry e) => AvatarImageStyles.IsValid(e.Style) ? e.Style : AvatarImageStyles.Realistic;

    /// <summary>Stellt sicher, dass der Eintrag zu einer Figur gehört (Altbestand ohne Figur wird zur neutralen Hauptvariante einer neuen Figur).</summary>
    private async Task<GalleryEntry> EnsureFigureAsync(string userSub, GalleryEntry e)
    {
        if (e.FigureId.Length > 0) return e;
        var fig = NewId();
        var mood = e.Mood.Length > 0 ? e.Mood : AvatarMoods.Neutral;
        await _db.ExecuteAsync("UPDATE avatar_gallery SET figure_id = @f, mood = @m WHERE user_sub = @u AND id = @id AND figure_id = ''", new (string, object?)[] { ("f", fig), ("m", mood), ("u", userSub), ("id", e.Id) });
        return await GetAsync(userSub, e.Id) ?? e;
    }

    private async Task AddMoodEntryAsync(string userSub, GalleryEntry figure, string mood, AvatarImageResult r)
    {
        var (count, bytes) = await UsageAsync(userSub);
        if (count + 1 > GalleryLimits.MaxEntriesPerUser) throw new AvatarImageException($"Deine Galerie ist voll (höchstens {GalleryLimits.MaxEntriesPerUser} Einträge). Lösche zuerst Einträge.", true);
        if (bytes + r.Bytes.Length > GalleryLimits.MaxTotalBytesPerUser) throw new AvatarImageException("Der Speicher deiner Galerie ist voll. Lösche zuerst Einträge.", true);
        var baseName = figure.Name.Contains(" · ") ? figure.Name[..figure.Name.IndexOf(" · ", StringComparison.Ordinal)] : figure.Name;
        var name = AvatarArchive.CleanText(baseName + " · " + AvatarMoods.Label(mood), GalleryLimits.MaxNameLength);
        var sha = Convert.ToHexString(SHA256.HashData(r.Bytes)).ToLowerInvariant();
        await _db.ExecuteAsync("INSERT INTO avatar_gallery (id, user_sub, group_id, kind, name, description, style, mood, mime, byte_size, sha256, svg_id, data_b64, favorite, created_at, legacy_version, figure_id) VALUES (@id, @u, @g, 'image', @n, '', @s, @mood, @m, @sz, @sha, '', @b, 0, @c, '', @fig)",
            new (string, object?)[] { ("id", NewId()), ("u", userSub), ("g", figure.GroupId), ("n", name), ("s", figure.Style), ("mood", mood), ("m", r.Mime), ("sz", r.Bytes.Length), ("sha", sha), ("b", Convert.ToBase64String(r.Bytes)), ("c", DateTime.UtcNow), ("fig", figure.FigureId) });
    }
}
