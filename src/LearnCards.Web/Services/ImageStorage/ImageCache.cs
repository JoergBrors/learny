using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LearnCards.Web.Services.ImageStorage;

/// <summary>Eintrag im Bild-Cache. Der Schlüssel enthält Bild-ID und Prüfsumme; <see cref="ETag"/> dient der Revalidierung nach Ablauf der TTL.</summary>
public sealed record CachedImage(byte[] Data, string MimeType, string Checksum, string? ETag, DateTime ExpiresAtUtc)
{
    public bool IsFresh => DateTime.UtcNow < ExpiresAtUtc;
}

public sealed class ImageCacheStats
{
    public long MemoryHits, FileHits, Misses, Evictions, Stores, Revalidations;
    public long MemoryBytes, FileBytes;
}

/// <summary>
/// Cache für Bilder entfernter Speicher (ADR 0017). Er liegt <b>hinter</b> der Berechtigungsprüfung: Zugriff auf einen Cache-Eintrag gibt es nur, nachdem
/// die Karte/das Modul für den Benutzer als lesbar bestätigt wurde. Cache-Daten werden nie direkt ausgeliefert oder öffentlich abgelegt.
/// </summary>
public interface IImageCache
{
    bool Enabled { get; }
    ImageCacheStats Stats { get; }
    /// <summary>Liefert den Eintrag auch nach Ablauf der TTL (zur Revalidierung); der Aufrufer prüft <see cref="CachedImage.IsFresh"/>.</summary>
    Task<CachedImage?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, CachedImage value, CancellationToken ct = default);
    /// <summary>Verlängert die Gültigkeit nach erfolgreicher Revalidierung (304).</summary>
    Task TouchAsync(string key, DateTime newExpiryUtc, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
}

public sealed class NullImageCache : IImageCache
{
    public bool Enabled => false;
    public ImageCacheStats Stats { get; } = new();
    public Task<CachedImage?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<CachedImage?>(null);
    public Task SetAsync(string key, CachedImage value, CancellationToken ct = default) => Task.CompletedTask;
    public Task TouchAsync(string key, DateTime newExpiryUtc, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Arbeitsspeicher-Cache mit Größenlimit und LRU-Eviction (älteste Zugriffe zuerst).</summary>
public sealed class MemoryImageCache
{
    private sealed class Entry { public CachedImage Value = default!; public long LastAccessTicks; }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly long _maxBytes;
    private readonly object _evictLock = new();
    private long _bytes;
    private readonly ImageCacheStats _stats;

    public MemoryImageCache(long maxBytes, ImageCacheStats stats)
    {
        _maxBytes = maxBytes;
        _stats = stats;
    }

    public long Bytes => Interlocked.Read(ref _bytes);
    public int Count => _entries.Count;

    public CachedImage? Get(string key)
    {
        if (!_entries.TryGetValue(key, out var e)) return null;
        e.LastAccessTicks = DateTime.UtcNow.Ticks;
        return e.Value;
    }

    public void Set(string key, CachedImage value)
    {
        if (value.Data.Length > _maxBytes) return;   // größer als das gesamte Limit: nicht cachen
        var entry = new Entry { Value = value, LastAccessTicks = DateTime.UtcNow.Ticks };
        _entries.AddOrUpdate(key,
            _ => { Interlocked.Add(ref _bytes, value.Data.Length); return entry; },
            (_, old) => { Interlocked.Add(ref _bytes, value.Data.Length - old.Value.Data.Length); return entry; });
        EvictIfNeeded();
    }

    public void Touch(string key, DateTime newExpiry)
    {
        if (_entries.TryGetValue(key, out var e)) e.Value = e.Value with { ExpiresAtUtc = newExpiry };
    }

    public void Remove(string key)
    {
        if (_entries.TryRemove(key, out var e)) Interlocked.Add(ref _bytes, -e.Value.Data.Length);
    }

    private void EvictIfNeeded()
    {
        if (Bytes <= _maxBytes) return;
        lock (_evictLock)
        {
            foreach (var kv in _entries.OrderBy(k => k.Value.LastAccessTicks))
            {
                if (Bytes <= _maxBytes) break;
                if (_entries.TryRemove(kv.Key, out var removed))
                {
                    Interlocked.Add(ref _bytes, -removed.Value.Data.Length);
                    Interlocked.Increment(ref _stats.Evictions);
                }
            }
        }
        _stats.MemoryBytes = Bytes;
    }
}

/// <summary>
/// Persistenter Dateicache (überlebt Neustarts). Dateinamen sind SHA-256 des Cache-Schlüssels (kein Benutzereingabe-Anteil); je Eintrag eine
/// Datendatei und eine Metadatendatei. Atomares Schreiben, Größenlimit mit LRU-Eviction über den Zeitstempel des letzten Zugriffs, unter Linux 0600/0700.
/// Das Verzeichnis liegt außerhalb von wwwroot und wird nie ausgeliefert.
/// </summary>
public sealed class FileImageCache
{
    private sealed record Meta(string MimeType, string Checksum, string? ETag, DateTime ExpiresAtUtc);

    private readonly string _dir;
    private readonly long _maxBytes;
    private readonly ImageCacheStats _stats;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private long _bytes = -1;

    public FileImageCache(string dir, long maxBytes, ImageCacheStats stats)
    {
        _dir = Path.GetFullPath(dir);
        _maxBytes = maxBytes;
        _stats = stats;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_dir);
        else Directory.CreateDirectory(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public string Directory_ => _dir;

    private static string Name(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    private string DataPath(string key) => Path.Combine(_dir, Name(key) + ".bin");
    private string MetaPath(string key) => Path.Combine(_dir, Name(key) + ".json");

    public async Task<CachedImage?> GetAsync(string key, CancellationToken ct)
    {
        var data = DataPath(key);
        var meta = MetaPath(key);
        try
        {
            if (!File.Exists(data) || !File.Exists(meta)) return null;
            var m = JsonSerializer.Deserialize<Meta>(await File.ReadAllTextAsync(meta, ct));
            if (m is null) return null;
            var bytes = await File.ReadAllBytesAsync(data, ct);
            // Integrität: der Cache vertraut seinen Dateien nicht blind (manipuliert/abgeschnitten → Eintrag verwerfen)
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), m.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                await RemoveAsync(key, ct);
                return null;
            }
            File.SetLastAccessTimeUtc(data, DateTime.UtcNow);
            return new CachedImage(bytes, m.MimeType, m.Checksum, m.ETag, m.ExpiresAtUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public async Task SetAsync(string key, CachedImage value, CancellationToken ct)
    {
        if (value.Data.Length > _maxBytes) return;
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureSizeKnownAsync();
            var data = DataPath(key);
            var existing = File.Exists(data) ? new FileInfo(data).Length : 0;
            await WriteAtomicAsync(data, value.Data, ct);
            await WriteAtomicAsync(MetaPath(key), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Meta(value.MimeType, value.Checksum, value.ETag, value.ExpiresAtUtc))), ct);
            _bytes += value.Data.Length - existing;
            EvictIfNeeded();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Cache ist best effort */ }
        finally { _lock.Release(); }
    }

    public async Task TouchAsync(string key, DateTime newExpiry, CancellationToken ct)
    {
        var cached = await GetAsync(key, ct);
        if (cached is not null) await SetAsync(key, cached with { ExpiresAtUtc = newExpiry }, ct);
    }

    public async Task RemoveAsync(string key, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var data = DataPath(key);
            if (File.Exists(data)) { _bytes = Math.Max(0, _bytes - new FileInfo(data).Length); }
            TryDelete(data);
            TryDelete(MetaPath(key));
        }
        finally { _lock.Release(); }
    }

    private Task EnsureSizeKnownAsync()
    {
        if (_bytes >= 0) return Task.CompletedTask;
        _bytes = new DirectoryInfo(_dir).EnumerateFiles("*.bin").Sum(f => f.Length);
        return Task.CompletedTask;
    }

    private void EvictIfNeeded()
    {
        if (_bytes <= _maxBytes) { _stats.FileBytes = _bytes; return; }
        foreach (var f in new DirectoryInfo(_dir).EnumerateFiles("*.bin").OrderBy(f => f.LastAccessTimeUtc))
        {
            if (_bytes <= _maxBytes) break;
            _bytes -= f.Length;
            TryDelete(f.FullName);
            TryDelete(Path.ChangeExtension(f.FullName, ".json"));
            Interlocked.Increment(ref _stats.Evictions);
        }
        _stats.FileBytes = _bytes;
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, path, overwrite: true);
        }
        catch { TryDelete(temp); throw; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}

/// <summary>Zweistufiger Cache: Arbeitsspeicher (schnell, flüchtig) vor persistentem Dateicache (überlebt Neustarts, optional).</summary>
public sealed class TwoLevelImageCache : IImageCache
{
    private readonly MemoryImageCache _memory;
    private readonly FileImageCache? _file;

    public TwoLevelImageCache(MemoryImageCache memory, FileImageCache? file, ImageCacheStats stats)
    {
        _memory = memory;
        _file = file;
        Stats = stats;
    }

    public bool Enabled => true;
    public ImageCacheStats Stats { get; }

    public async Task<CachedImage?> GetAsync(string key, CancellationToken ct = default)
    {
        var m = _memory.Get(key);
        if (m is not null) { Interlocked.Increment(ref Stats.MemoryHits); return m; }
        if (_file is not null)
        {
            var f = await _file.GetAsync(key, ct);
            if (f is not null)
            {
                _memory.Set(key, f);   // in den Arbeitsspeicher heben
                Interlocked.Increment(ref Stats.FileHits);
                return f;
            }
        }
        Interlocked.Increment(ref Stats.Misses);
        return null;
    }

    public async Task SetAsync(string key, CachedImage value, CancellationToken ct = default)
    {
        _memory.Set(key, value);
        if (_file is not null) await _file.SetAsync(key, value, ct);
        Interlocked.Increment(ref Stats.Stores);
        Stats.MemoryBytes = _memory.Bytes;
    }

    public async Task TouchAsync(string key, DateTime newExpiryUtc, CancellationToken ct = default)
    {
        _memory.Touch(key, newExpiryUtc);
        if (_file is not null) await _file.TouchAsync(key, newExpiryUtc, ct);
        Interlocked.Increment(ref Stats.Revalidations);
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _memory.Remove(key);
        if (_file is not null) await _file.RemoveAsync(key, ct);
    }
}
