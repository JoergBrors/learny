using System.Diagnostics;
using System.Text;
using LearnCards.Web.Data;

namespace LearnCards.Web.Services.ImageStorage;

/// <summary>
/// Bildspeicher in der LearnCards-Datenbank (PostgreSQL/SQLite) — Standard und kompatibel zu den bisherigen Installationen.
/// <see cref="IDatabase"/> überträgt Werte als Text; die Binärdaten liegen deshalb als Base64-Text in <c>image_blobs.data_base64</c>
/// (Entscheidung ADR 0017: kein <c>bytea</c>/BLOB, weil das Datenbankprotokoll bewusst textbasiert ist). Die Provider-Schnittstelle bleibt binär.
/// </summary>
public sealed class DatabaseImageStorageProvider : IImageStorageProvider
{
    private readonly IDatabase _db;
    public DatabaseImageStorageProvider(IDatabase db) => _db = db;

    public string Name => "database";
    public bool IsRemote => false;

    public async Task<string?> PutAsync(string key, byte[] data, string mimeType, CancellationToken ct = default)
    {
        ImageStorageKey.Require(Name, key);
        var exists = await _db.QueryAsync("SELECT 1 FROM image_blobs WHERE storage_key = @k", new[] { ("k", (object?)key) }, ct);
        var args = new (string, object?)[] { ("k", key), ("d", Convert.ToBase64String(data)), ("n", data.Length), ("now", DateTime.UtcNow) };
        if (exists.Count > 0)
            await _db.ExecuteAsync("UPDATE image_blobs SET data_base64 = @d, size_bytes = @n WHERE storage_key = @k", args, ct);
        else
            await _db.ExecuteAsync("INSERT INTO image_blobs (storage_key, data_base64, size_bytes, created_at) VALUES (@k, @d, @n, @now)", args, ct);
        return null;
    }

    public async Task<StoredImage?> GetAsync(string key, string? ifNoneMatch = null, CancellationToken ct = default)
    {
        ImageStorageKey.Require(Name, key);
        var rows = await _db.QueryAsync("SELECT data_base64 FROM image_blobs WHERE storage_key = @k", new[] { ("k", (object?)key) }, ct);
        if (rows.Count == 0) return null;
        return new StoredImage(Convert.FromBase64String(rows[0]["data_base64"] ?? ""), null);
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        ImageStorageKey.Require(Name, key);
        return (await _db.QueryAsync("SELECT 1 FROM image_blobs WHERE storage_key = @k", new[] { ("k", (object?)key) }, ct)).Count > 0;
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ImageStorageKey.Require(Name, key);
        await _db.ExecuteAsync("DELETE FROM image_blobs WHERE storage_key = @k", new[] { ("k", (object?)key) }, ct);
    }

    public async Task<ProviderHealth> CheckHealthAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await _db.QueryAsync("SELECT COUNT(*) AS n FROM image_blobs", null, ct);
            return new ProviderHealth(true, "Datenbank erreichbar", sw.Elapsed);
        }
        catch (Exception e) { return new ProviderHealth(false, "Datenbank nicht erreichbar: " + e.Message, sw.Elapsed); }
    }
}

/// <summary>
/// Bildspeicher im lokalen Dateisystem. Dateinamen werden aus dem validierten Server-Schlüssel abgeleitet
/// (<c>{root}/{key[0..2]}/{key}.img</c>) — kein Benutzereingabe-Anteil, daher keine Path-Traversal-Möglichkeit; zusätzlich wird der aufgelöste
/// Pfad gegen das Wurzelverzeichnis geprüft. Schreiben ist atomar (temporäre Datei im Zielverzeichnis, dann Verschieben). Unter Linux/macOS
/// erhalten Verzeichnisse 0700 und Dateien 0600. Im Containerbetrieb muss das Verzeichnis ein persistentes Volume sein.
/// </summary>
public sealed class LocalImageStorageProvider : IImageStorageProvider
{
    private readonly string _root;

    public LocalImageStorageProvider(string root)
    {
        _root = Path.GetFullPath(root);
        EnsureDirectory(_root);
    }

    public string Name => "local";
    public bool IsRemote => false;
    public string Root => _root;

    public async Task<string?> PutAsync(string key, byte[] data, string mimeType, CancellationToken ct = default)
    {
        var path = PathFor(key);
        EnsureDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await fs.WriteAsync(data, ct);
                await fs.FlushAsync(ct);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await MoveWithRetryAsync(temp, path, ct);   // atomar im selben Verzeichnis
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new ImageStorageException(e is UnauthorizedAccessException ? ImageStorageError.Unauthorized : ImageStorageError.Unavailable, Name,
                "Lokaler Bildspeicher nicht beschreibbar: " + e.Message, e);
        }
        catch { TryDelete(temp); throw; }
        return null;
    }

    public async Task<StoredImage?> GetAsync(string key, string? ifNoneMatch = null, CancellationToken ct = default)
    {
        var path = PathFor(key);
        try
        {
            // FileShare.Delete: ein gleichzeitiges Ersetzen der Datei (atomares Umbenennen) darf nicht an einem laufenden Lesezugriff scheitern
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[fs.Length];
            await fs.ReadExactlyAsync(buffer, ct);
            return new StoredImage(buffer, null);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ImageStorageException(ImageStorageError.Unavailable, Name, "Lokaler Bildspeicher nicht lesbar: " + e.Message, e);
        }
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(File.Exists(PathFor(key)));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        TryDelete(PathFor(key));
        return Task.CompletedTask;
    }

    public async Task<ProviderHealth> CheckHealthAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var key = ImageStorageKey.New();
        try
        {
            var probe = Encoding.ASCII.GetBytes("learncards-health");
            await PutAsync(key, probe, "application/octet-stream", ct);
            var read = await GetAsync(key, null, ct);
            await DeleteAsync(key, ct);
            return read is not null && read.Data.SequenceEqual(probe)
                ? new ProviderHealth(true, $"Verzeichnis beschreibbar ({_root})", sw.Elapsed)
                : new ProviderHealth(false, "Lese-/Schreibprobe fehlgeschlagen.", sw.Elapsed);
        }
        catch (Exception e) { return new ProviderHealth(false, e.Message, sw.Elapsed); }
    }

    /// <summary>Pfad für einen validierten Schlüssel; wirft bei ungültigem Schlüssel oder wenn der Pfad das Wurzelverzeichnis verlassen würde.</summary>
    public string PathFor(string key)
    {
        ImageStorageKey.Require(Name, key);
        var full = Path.GetFullPath(Path.Combine(_root, key[..2], key + ".img"));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ImageStorageException(ImageStorageError.Invalid, Name, "Ungültiger Speicherpfad.");
        return full;
    }

    /// <summary>Unter Windows scheitert das Umbenennen kurzzeitig, wenn die Zieldatei gerade gelesen oder von einem anderen Schreiber ersetzt wird — wenige kurze Wiederholungen.</summary>
    private static async Task MoveWithRetryAsync(string temp, string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temp, path, overwrite: true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 8)
            {
                await Task.Delay(5 + attempt * 10, ct);
            }
        }
    }

    private static void EnsureDirectory(string dir)
    {
        if (Directory.Exists(dir)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
