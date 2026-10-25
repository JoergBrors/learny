using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LearnCards.Web.Api;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using Microsoft.Extensions.Logging;

namespace LearnCards.Web.Services.ImageStorage;

/// <summary>Auslieferbarer Bildinhalt (nach Integritätsprüfung).</summary>
public sealed record ImageContent(byte[] Data, string MimeType, string Checksum, string? ETag, bool FromCache);

/// <summary>Verweis auf ein gespeichertes Bild (zum Aufräumen nach dem Löschen von Karten/Modulen).</summary>
public sealed record ImageRef(string Provider, string StorageKey, string ImageId, string Checksum);

/// <summary>
/// Bildverwaltung provider-unabhängig (ADR 0017): Metadaten in <c>card_images</c> (Datenbank), Binärdaten im primären Bildspeicher,
/// Cache für entfernte Speicher. Berechtigungen prüft der Aufrufer (<c>CardRepository</c>) <b>vor</b> dem Zugriff auf Inhalte.
/// </summary>
public sealed class CardImageService
{
    private const string Columns = "i.id, i.card_id, i.provider, i.storage_key, i.mime_type, i.size_bytes, i.checksum, i.alt_text, i.caption, i.sort_order";

    private readonly IDatabase _db;
    private readonly ImageStorageRegistry _registry;
    private readonly IImageCache _cache;
    private readonly ImageCacheSettings _cacheSettings;
    private readonly ILogger<CardImageService> _log;
    private readonly ConcurrentDictionary<string, Lazy<Task<ImageContent>>> _inflight = new(StringComparer.Ordinal);

    public CardImageService(IDatabase db, ImageStorageRegistry registry, IImageCache cache, AppConfig cfg, ILogger<CardImageService> log)
    {
        _db = db;
        _registry = registry;
        _cache = cache;
        _cacheSettings = cfg.ImageStorage.Cache;
        _log = log;
    }

    public ImageStorageRegistry Registry => _registry;
    public IImageCache Cache => _cache;

    // ─── Metadaten lesen ────────────────────────────────────────────────────

    /// <summary>Lädt die Bildmetadaten (ohne Binärdaten) für Karten und hängt sie an <see cref="Card.Images"/>.</summary>
    public async Task AttachAsync(IReadOnlyCollection<Card> cards)
    {
        foreach (var c in cards) c.Images = new();
        var ids = cards.Select(c => c.Id).Where(UserService.IsGuid).Distinct().ToList();
        if (ids.Count == 0) return;
        var byCard = cards.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in ids.Chunk(200))
        {
            var names = chunk.Select((_, i) => "@c" + i).ToList();
            var args = chunk.Select((id, i) => ("c" + i, (object?)id)).ToList();
            var rows = await _db.QueryAsync($"SELECT {Columns} FROM card_images i WHERE i.card_id IN ({string.Join(",", names)}) ORDER BY i.sort_order, i.created_at", args);
            foreach (var r in rows)
            {
                var img = Map(r);
                if (byCard.TryGetValue(img.CardId, out var card)) card.Images.Add(img);
                // Karten mit gleicher ID mehrfach in der Liste (z. B. Importvorschau) teilen dieselbe Metadatenliste nicht — nicht relevant, IDs sind eindeutig
            }
        }
    }

    public async Task<List<CardImage>> ListForCardAsync(string cardId)
    {
        if (!UserService.IsGuid(cardId)) return new();
        var rows = await _db.QueryAsync($"SELECT {Columns} FROM card_images i WHERE i.card_id = @c ORDER BY i.sort_order, i.created_at", new[] { ("c", (object?)cardId) });
        return rows.Select(Map).ToList();
    }

    public async Task<CardImage?> GetMetaAsync(string imageId)
    {
        if (!UserService.IsGuid(imageId)) return null;
        var rows = await _db.QueryAsync($"SELECT {Columns} FROM card_images i WHERE i.id = @id", new[] { ("id", (object?)imageId) });
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    // ─── Schreiben ──────────────────────────────────────────────────────────

    /// <summary>
    /// Ersetzt die Bilder einer Karte durch die übergebene Liste (bereits validiert, siehe <see cref="CardImageValidator"/>):
    /// Einträge mit <c>data_base64</c> sind neue Bilder (werden im primären Speicher abgelegt; identische Bilder der Karte werden wiederverwendet),
    /// Einträge nur mit <c>image_id</c> behalten ein vorhandenes Bild dieser Karte. Von außen übergebene Provider-/Schlüsselangaben werden <b>ignoriert</b>.
    /// </summary>
    public async Task<List<CardImage>> ReplaceForCardAsync(string cardId, List<CardImage> inbound, CancellationToken ct = default)
    {
        var existing = await ListForCardAsync(cardId);
        var primary = _registry.Primary;
        var written = new List<ImageRef>();
        var result = new List<(CardImage Img, bool IsNew)>();

        try
        {
            for (var i = 0; i < inbound.Count; i++)
            {
                var item = inbound[i];
                if (!string.IsNullOrEmpty(item.DataBase64))
                {
                    var bytes = Convert.FromBase64String(item.DataBase64);
                    var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    var reuse = existing.FirstOrDefault(e => e.Checksum == checksum && e.MimeType == item.MimeType && result.All(r => r.Img.ImageId != e.ImageId));
                    if (reuse is not null)
                    {
                        result.Add((WithText(reuse, item, i), false));
                        continue;
                    }
                    var key = ImageStorageKey.New();
                    await primary.PutAsync(key, bytes, item.MimeType, ct);
                    var imageId = Guid.NewGuid().ToString();
                    written.Add(new ImageRef(primary.Name, key, imageId, checksum));
                    result.Add((new CardImage
                    {
                        ImageId = imageId, CardId = cardId, Provider = primary.Name, StorageKey = key, MimeType = item.MimeType,
                        SizeBytes = bytes.Length, Checksum = checksum, AltText = item.AltText, Caption = item.Caption, SortOrder = i,
                    }, true));
                }
                else
                {
                    var keep = existing.FirstOrDefault(e => string.Equals(e.ImageId, item.ImageId, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"Bild {i + 1}: image_id gehört nicht zu dieser Karte.");
                    if (result.Any(r => r.Img.ImageId == keep.ImageId)) throw new ArgumentException($"Bild {i + 1}: image_id ist doppelt angegeben.");
                    result.Add((WithText(keep, item, i), false));
                }
            }
        }
        catch
        {
            await CleanupAsync(written);   // bereits geschriebene neue Binärdaten wieder entfernen
            throw;
        }

        // Metadaten in der Datenbank: nicht mehr referenzierte Zeilen entfernen, neue anlegen, behaltene aktualisieren
        var keepIds = result.Select(r => r.Img.ImageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = existing.Where(e => !keepIds.Contains(e.ImageId)).ToList();
        try
        {
            foreach (var r in removed)
                await _db.ExecuteAsync("DELETE FROM card_images WHERE id = @id", new[] { ("id", (object?)r.ImageId) });
            foreach (var (img, isNew) in result)
            {
                if (isNew)
                    await _db.ExecuteAsync("""
                        INSERT INTO card_images (id, card_id, provider, storage_key, mime_type, size_bytes, checksum, alt_text, caption, sort_order, created_at)
                        VALUES (@id, @card, @provider, @key, @mime, @size, @sum, @alt, @cap, @ord, @now)
                        """, new (string, object?)[]
                    {
                        ("id", img.ImageId), ("card", cardId), ("provider", img.Provider), ("key", img.StorageKey), ("mime", img.MimeType),
                        ("size", img.SizeBytes), ("sum", img.Checksum), ("alt", img.AltText), ("cap", img.Caption), ("ord", img.SortOrder), ("now", DateTime.UtcNow),
                    });
                else
                    await _db.ExecuteAsync("UPDATE card_images SET alt_text = @alt, caption = @cap, sort_order = @ord WHERE id = @id",
                        new (string, object?)[] { ("alt", img.AltText), ("cap", img.Caption), ("ord", img.SortOrder), ("id", img.ImageId) });
            }
        }
        catch
        {
            await CleanupAsync(written);
            throw;
        }

        await CleanupOrphansAsync(removed.Select(r => new ImageRef(r.Provider, r.StorageKey, r.ImageId, r.Checksum)));
        return result.Select(r => r.Img).ToList();
    }

    /// <summary>Kopiert die Bildverweise einer Karte auf eine andere (globale Kopie/Freigabe): gleiche Binärdaten, neue Bild-IDs, keine Duplikate im Speicher.</summary>
    public async Task CopyForCardAsync(string sourceCardId, string targetCardId)
    {
        foreach (var img in await ListForCardAsync(sourceCardId))
            await _db.ExecuteAsync("""
                INSERT INTO card_images (id, card_id, provider, storage_key, mime_type, size_bytes, checksum, alt_text, caption, sort_order, created_at)
                VALUES (@id, @card, @provider, @key, @mime, @size, @sum, @alt, @cap, @ord, @now)
                """, new (string, object?)[]
            {
                ("id", Guid.NewGuid().ToString()), ("card", targetCardId), ("provider", img.Provider), ("key", img.StorageKey), ("mime", img.MimeType),
                ("size", img.SizeBytes), ("sum", img.Checksum), ("alt", img.AltText), ("cap", img.Caption), ("ord", img.SortOrder), ("now", DateTime.UtcNow),
            });
    }

    // ─── Lesen der Inhalte ──────────────────────────────────────────────────

    /// <summary>
    /// Liefert den Bildinhalt provider-unabhängig. Entfernte Speicher laufen über den Cache (Schlüssel = Provider + Bild-ID + Prüfsumme):
    /// frischer Treffer ohne Providerzugriff, abgelaufener Treffer mit ETag-Revalidierung, Fehlschlag mit Abruf; gleichzeitige Abrufe desselben Bildes werden gebündelt.
    /// Alle Inhalte werden gegen die gespeicherte Prüfsumme verifiziert.
    /// </summary>
    public async Task<ImageContent> GetContentAsync(CardImage meta, CancellationToken ct = default)
    {
        // Je Abruf ein strukturierter Logeintrag (ADR 0018): nie URLs, SAS, Signaturen, Schlüssel oder Header — nur Bild-ID, Provider, Cache, Dauer, Größe, Status.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var content = await GetContentCoreAsync(meta, ct);
            _log.LogInformation("Bildabruf {ImageId} provider={Provider} cache={Cache} dauer_ms={Ms} bytes={Bytes} status=ok",
                meta.ImageId, meta.Provider, content.FromCache ? "hit" : "miss", sw.ElapsedMilliseconds, content.Data.Length);
            return content;
        }
        catch (ImageStorageException e)
        {
            _log.LogWarning("Bildabruf {ImageId} provider={Provider} dauer_ms={Ms} status=fehler kategorie={Kind}", meta.ImageId, meta.Provider, sw.ElapsedMilliseconds, e.Kind);
            throw;
        }
    }

    private async Task<ImageContent> GetContentCoreAsync(CardImage meta, CancellationToken ct)
    {
        var provider = _registry.Get(meta.Provider);
        var cacheable = _cache.Enabled && provider.IsRemote;
        var cacheKey = $"{meta.Provider}:{meta.ImageId}:{meta.Checksum}";

        CachedImage? cached = null;
        if (cacheable)
        {
            cached = await _cache.GetAsync(cacheKey, ct);
            if (cached is { IsFresh: true }) return new ImageContent(cached.Data, cached.MimeType, cached.Checksum, cached.ETag, FromCache: true);
        }

        var lazy = _inflight.GetOrAdd(cacheKey, _ => new Lazy<Task<ImageContent>>(() => LoadAsync(provider, meta, cacheKey, cacheable, cached)));
        return await lazy.Value.WaitAsync(ct);
    }

    private async Task<ImageContent> LoadAsync(IImageStorageProvider provider, CardImage meta, string cacheKey, bool cacheable, CachedImage? stale)
    {
        try
        {
            StoredImage? stored;
            if (stale?.ETag is not null)
            {
                stored = await provider.GetAsync(meta.StorageKey, stale.ETag);
                if (stored is { NotModified: true })
                {
                    await _cache.TouchAsync(cacheKey, DateTime.UtcNow.AddMinutes(_cacheSettings.TtlMinutes));
                    return new ImageContent(stale.Data, stale.MimeType, stale.Checksum, stale.ETag, FromCache: true);
                }
            }
            else
            {
                stored = await provider.GetAsync(meta.StorageKey);
            }

            if (stored is null)
            {
                if (cacheable) await _cache.RemoveAsync(cacheKey);
                throw new ImageStorageException(ImageStorageError.NotFound, provider.Name, "Das Bild wurde im Bildspeicher nicht gefunden.");
            }

            var actual = Convert.ToHexString(SHA256.HashData(stored.Data)).ToLowerInvariant();
            if (!string.IsNullOrEmpty(meta.Checksum) && !string.Equals(actual, meta.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                if (cacheable) await _cache.RemoveAsync(cacheKey);
                _log.LogError("Integritätsfehler bei Bild {ImageId} (Provider {Provider}): Prüfsumme weicht ab.", meta.ImageId, provider.Name);
                throw new ImageStorageException(ImageStorageError.IntegrityFailed, provider.Name, "Die Bilddaten stimmen nicht mit der gespeicherten Prüfsumme überein.");
            }

            if (cacheable)
                await _cache.SetAsync(cacheKey, new CachedImage(stored.Data, meta.MimeType, actual, stored.ETag, DateTime.UtcNow.AddMinutes(_cacheSettings.TtlMinutes)));
            return new ImageContent(stored.Data, meta.MimeType, actual, stored.ETag, FromCache: false);
        }
        finally
        {
            _inflight.TryRemove(cacheKey, out _);
        }
    }

    // ─── Aufräumen ──────────────────────────────────────────────────────────

    /// <summary>Verweise aller Bilder der Karten eines Moduls — vor dem Löschen sammeln (die Zeilen verschwinden per Fremdschlüssel).</summary>
    public async Task<List<ImageRef>> CollectForModuleAsync(string moduleId)
    {
        if (!UserService.IsGuid(moduleId)) return new();
        var rows = await _db.QueryAsync("SELECT i.id, i.provider, i.storage_key, i.checksum FROM card_images i JOIN cards c ON c.id = i.card_id WHERE c.module_id = @m", new[] { ("m", (object?)moduleId) });
        return rows.Select(r => new ImageRef(r["provider"] ?? "", r["storage_key"] ?? "", r["id"] ?? "", r["checksum"] ?? "")).ToList();
    }

    public async Task<List<ImageRef>> CollectForCardAsync(string cardId) =>
        (await ListForCardAsync(cardId)).Select(i => new ImageRef(i.Provider, i.StorageKey, i.ImageId, i.Checksum)).ToList();

    /// <summary>Entfernt Binärdaten, auf die keine Zeile mehr verweist (Referenzzählung; geteilte Binärdaten aus Kopien bleiben erhalten). Best effort, Fehler werden protokolliert.</summary>
    public async Task CleanupOrphansAsync(IEnumerable<ImageRef> refs)
    {
        foreach (var group in refs.GroupBy(r => (r.Provider, r.StorageKey)))
        {
            try
            {
                var still = await _db.QueryAsync("SELECT 1 FROM card_images WHERE provider = @p AND storage_key = @k",
                    new (string, object?)[] { ("p", group.Key.Provider), ("k", group.Key.StorageKey) });
                foreach (var r in group) if (_cache.Enabled) await _cache.RemoveAsync(CacheKeyFor(r));
                if (still.Count > 0 || !_registry.IsConfigured(group.Key.Provider)) continue;
                await _registry.Get(group.Key.Provider).DeleteAsync(group.Key.StorageKey);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Bilddaten {Provider}/{Key} konnten nicht entfernt werden (verwaist).", group.Key.Provider, group.Key.StorageKey);
            }
        }
    }

    private Task CleanupAsync(List<ImageRef> written) => CleanupOrphansAsync(written);

    private static string CacheKeyFor(ImageRef r) => $"{r.Provider}:{r.ImageId}:{r.Checksum}";

    // ─── Statistik / Altdaten ───────────────────────────────────────────────

    public async Task<Dictionary<string, (int Images, long Bytes)>> CountByProviderAsync()
    {
        var rows = await _db.QueryAsync("SELECT provider, COUNT(*) AS n, COALESCE(SUM(size_bytes), 0) AS bytes FROM card_images GROUP BY provider");
        return rows.ToDictionary(r => r["provider"] ?? "", r => (DbValue.ToInt(r["n"]), (long)DbValue.ToDouble(r["bytes"])));
    }

    /// <summary>
    /// Überführt Bilder aus der früheren Speicherung (<c>cards.images_json</c> mit Base64, ADR 0016) einmalig in <c>card_images</c> + Datenbank-Speicher.
    /// Idempotent und ohne Datenverlust: <c>images_json</c> bleibt unverändert erhalten, <c>cards.images_migrated</c> markiert erledigte Karten.
    /// Fehlerhafte Karten bleiben unmarkiert und werden beim nächsten Start erneut versucht.
    /// </summary>
    public async Task<int> MigrateLegacyInlineImagesAsync()
    {
        await _db.ExecuteAsync("UPDATE cards SET images_migrated = 1 WHERE images_migrated = 0 AND (images_json IS NULL OR images_json = '[]' OR images_json = '')");
        var db = _registry.Get(ImageProviderNames.Database);
        var converted = 0;
        var last = Guid.Empty.ToString();
        while (true)
        {
            var batch = await _db.QueryAsync("SELECT id, images_json FROM cards WHERE images_migrated = 0 AND id > @last ORDER BY id LIMIT 25",
                new[] { ("last", (object?)last) });
            if (batch.Count == 0) break;
            foreach (var row in batch)
            {
                var cardId = row["id"] ?? "";
                last = cardId;
                try
                {
                    var legacy = JsonSerializer.Deserialize<List<CardImage>>(row["images_json"] ?? "[]", AppJson.Options) ?? new();
                    // Teilweise Vorläufe bereinigen (für Karten mit images_migrated=0 existieren keine regulären Zeilen)
                    var partial = await ListForCardAsync(cardId);
                    foreach (var p in partial) await _db.ExecuteAsync("DELETE FROM card_images WHERE id = @id", new[] { ("id", (object?)p.ImageId) });
                    await CleanupOrphansAsync(partial.Select(p => new ImageRef(p.Provider, p.StorageKey, p.ImageId, p.Checksum)));

                    var order = 0;
                    foreach (var img in legacy)
                    {
                        var bytes = Convert.FromBase64String(img.DataBase64);
                        var key = ImageStorageKey.New();
                        await db.PutAsync(key, bytes, img.MimeType);
                        await _db.ExecuteAsync("""
                            INSERT INTO card_images (id, card_id, provider, storage_key, mime_type, size_bytes, checksum, alt_text, caption, sort_order, created_at)
                            VALUES (@id, @card, 'database', @key, @mime, @size, @sum, @alt, @cap, @ord, @now)
                            """, new (string, object?)[]
                        {
                            ("id", Guid.NewGuid().ToString()), ("card", cardId), ("key", key), ("mime", img.MimeType), ("size", bytes.Length),
                            ("sum", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()), ("alt", img.AltText ?? ""), ("cap", img.Caption ?? ""),
                            ("ord", order++), ("now", DateTime.UtcNow),
                        });
                    }
                    await _db.ExecuteAsync("UPDATE cards SET images_migrated = 1 WHERE id = @id", new[] { ("id", (object?)cardId) });
                    converted++;
                }
                catch (Exception e)
                {
                    _log.LogError(e, "Altbilder der Karte {CardId} konnten nicht überführt werden; images_json bleibt unverändert, neuer Versuch beim nächsten Start.", cardId);
                }
            }
        }
        return converted;
    }

    // ─── Hilfen ─────────────────────────────────────────────────────────────

    private static CardImage WithText(CardImage source, CardImage text, int order) => new()
    {
        ImageId = source.ImageId, CardId = source.CardId, Provider = source.Provider, StorageKey = source.StorageKey, MimeType = source.MimeType,
        SizeBytes = source.SizeBytes, Checksum = source.Checksum, AltText = text.AltText, Caption = text.Caption, SortOrder = order,
    };

    private static CardImage Map(Dictionary<string, string?> r) => new()
    {
        ImageId = r["id"] ?? "",
        CardId = r.GetValueOrDefault("card_id") ?? "",
        Provider = r.GetValueOrDefault("provider") ?? "",
        StorageKey = r.GetValueOrDefault("storage_key") ?? "",
        MimeType = r.GetValueOrDefault("mime_type") ?? "",
        SizeBytes = DbValue.ToInt(r.GetValueOrDefault("size_bytes")),
        Checksum = r.GetValueOrDefault("checksum") ?? "",
        AltText = r.GetValueOrDefault("alt_text") ?? "",
        Caption = r.GetValueOrDefault("caption") ?? "",
        SortOrder = DbValue.ToInt(r.GetValueOrDefault("sort_order")),
    };
}
