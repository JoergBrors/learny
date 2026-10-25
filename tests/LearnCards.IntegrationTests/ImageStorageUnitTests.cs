using System.Security.Cryptography;
using LearnCards.Web.Data;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services.ImageStorage;

/// <summary>
/// Komponententests der Bildspeicher-Bausteine (ADR 0017) ohne laufenden Server: Pfadsicherheit, Atomarität, Konfigurationsprüfung, Cache.
/// Weitere Abschnitte (Signierung, Remote-Provider) kommen mit den jeweiligen Etappen hinzu.
/// </summary>
public static class ImageStorageUnitTests
{
    public static async Task RunAsync(Action<bool, string> check, string tmp)
    {
        Console.WriteLine("Komponententests Bildspeicher (ADR 0017)");
        await LocalProviderAsync(check, tmp);
        await DatabaseProviderAsync(check, tmp);
        ConfigValidation(check);
        await CacheAsync(check, tmp);
    }

    private static byte[] Bytes(int n, int seed = 1)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    // ─── Local ──────────────────────────────────────────────────────────────

    private static async Task LocalProviderAsync(Action<bool, string> check, string tmp)
    {
        var root = Path.Combine(tmp, "unit-local");
        var p = new LocalImageStorageProvider(root);
        var key = ImageStorageKey.New();
        var data = Bytes(5000);

        await p.PutAsync(key, data, "image/png");
        var read = await p.GetAsync(key);
        check(read is not null && read.Data.SequenceEqual(data), "U1 Local: Schreiben und Lesen liefern identische Bytes");
        check(await p.ExistsAsync(key) && !await p.ExistsAsync(ImageStorageKey.New()), "U1 Local: ExistsAsync");
        check(await p.GetAsync(ImageStorageKey.New()) is null, "U1 Local: unbekannter Schlüssel → null (nicht vorhanden)");
        await p.DeleteAsync(key);
        check(!await p.ExistsAsync(key) && await p.GetAsync(key) is null, "U1 Local: DeleteAsync entfernt die Datei");
        await p.DeleteAsync(key);
        check(true, "U1 Local: erneutes Löschen ist kein Fehler");

        // Path Traversal: nur serverseitig erzeugte 32-stellige Hex-Schlüssel sind zulässig
        var bad = new[] { "../../etc/passwd", "..\\..\\windows\\system32", "a/b", "abc", new string('a', 31), new string('a', 33), new string('A', 32), "../" + new string('a', 29), "", " ", new string('g', 32), "C:\\temp\\x" };
        var allRejected = true;
        foreach (var k in bad)
        {
            try { await p.PutAsync(k, data, "image/png"); allRejected = false; }
            catch (ImageStorageException e) when (e.Kind == ImageStorageError.Invalid) { }
            try { await p.GetAsync(k); allRejected = false; }
            catch (ImageStorageException e) when (e.Kind == ImageStorageError.Invalid) { }
            try { await p.DeleteAsync(k); allRejected = false; }
            catch (ImageStorageException e) when (e.Kind == ImageStorageError.Invalid) { }
        }
        check(allRejected, $"U2 Local: {bad.Length} manipulierte Schlüssel (Traversal, Trenner, Länge, Großschreibung) werden bei Put/Get/Delete abgelehnt");
        check(!Directory.EnumerateFileSystemEntries(tmp, "passwd", SearchOption.AllDirectories).Any() && Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == 0,
            "U2 Local: außerhalb des Wurzelverzeichnisses wurde nichts angelegt");
        check(p.PathFor(ImageStorageKey.New()).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar), "U2 Local: berechnete Pfade liegen immer unter dem Wurzelverzeichnis");

        // Atomarität: parallele Schreiber desselben Schlüssels hinterlassen immer eine vollständige Variante und keine temporären Dateien
        var k2 = ImageStorageKey.New();
        var variants = Enumerable.Range(1, 12).Select(i => Bytes(20000 + i * 1000, i)).ToList();
        await Task.WhenAll(variants.Select(v => p.PutAsync(k2, v, "image/png")));
        var final = (await p.GetAsync(k2))!.Data;
        check(variants.Any(v => v.SequenceEqual(final)), "U3 Local: parallele Schreibvorgänge → vollständiger Inhalt einer Variante (atomar, nie gemischt/abgeschnitten)");
        check(!Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "U3 Local: keine temporären Dateien zurückgeblieben");
        var concurrent = new List<Task<bool>>();
        for (var i = 0; i < 20; i++)
            concurrent.Add(Task.Run(async () => { var r = await p.GetAsync(k2); return r is null || variants.Any(v => v.SequenceEqual(r.Data)); }));
        var writers = Task.WhenAll(variants.Select(v => p.PutAsync(k2, v, "image/png")));
        check((await Task.WhenAll(concurrent)).All(x => x), "U3 Local: Leser sehen während des Schreibens nie halb geschriebene Dateien");
        await writers;

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(p.PathFor(k2));
            check(mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "U4 Local: Dateien erhalten 0600 (nur Besitzer)");
        }
        else Console.WriteLine("  INFO U4 Local: Dateiberechtigungen 0600 nur unter Linux/macOS prüfbar (Windows: ACL des Verzeichnisses)");

        var health = await p.CheckHealthAsync();
        check(health.Ok, "U5 Local: Health-Check (Schreib-/Leseprobe)");
        var ro = new LocalImageStorageProvider(Path.Combine(tmp, "unit-local-2"));
        check((await ro.CheckHealthAsync()).Ok, "U5 Local: Verzeichnis wird bei Bedarf angelegt");
    }

    // ─── Database ───────────────────────────────────────────────────────────

    private static async Task DatabaseProviderAsync(Action<bool, string> check, string tmp)
    {
        await using var db = new SqliteDatabase(Path.Combine(tmp, "unit-db.db"));
        await db.InitAsync();
        var p = new DatabaseImageStorageProvider(db);
        var key = ImageStorageKey.New();
        var data = Bytes(70000, 7);   // enthält alle Byte-Werte → Base64-Text-Speicherung muss verlustfrei sein
        await p.PutAsync(key, data, "image/png");
        check((await p.GetAsync(key))!.Data.SequenceEqual(data), "U6 Database: Binärdaten bleiben über die Textspeicherung (Base64) verlustfrei");
        await p.PutAsync(key, Bytes(10), "image/png");
        check((await p.GetAsync(key))!.Data.Length == 10, "U6 Database: erneutes Schreiben überschreibt");
        check(await p.ExistsAsync(key), "U6 Database: ExistsAsync");
        await p.DeleteAsync(key);
        check(!await p.ExistsAsync(key) && await p.GetAsync(key) is null, "U6 Database: DeleteAsync");
        var injected = false;
        try { await p.GetAsync("' OR '1'='1"); injected = true; } catch (ImageStorageException e) when (e.Kind == ImageStorageError.Invalid) { }
        check(!injected, "U6 Database: ungültige Schlüssel (SQL-Fragment) werden vor der Abfrage abgelehnt");
        check((await p.CheckHealthAsync()).Ok, "U6 Database: Health-Check");
    }

    // ─── Konfiguration ──────────────────────────────────────────────────────

    private static ImageStorageSettings Cfg(params (string Key, string? Value)[] env)
    {
        var all = new[]
        {
            "IMAGE_STORAGE_PROVIDER", "IMAGE_LOCAL_PATH", "IMAGE_AZUREBLOB_ACCOUNT", "IMAGE_AZUREBLOB_ENDPOINT", "IMAGE_AZUREBLOB_CONTAINER", "IMAGE_AZUREBLOB_AUTH",
            "IMAGE_AZUREBLOB_TENANT_ID", "IMAGE_AZUREBLOB_CLIENT_ID", "IMAGE_AZUREBLOB_CLIENT_SECRET", "IMAGE_AZUREBLOB_ACCOUNT_KEY", "IMAGE_AZUREBLOB_ALLOW_HTTP",
            "IMAGE_S3_ENDPOINT", "IMAGE_S3_REGION", "IMAGE_S3_BUCKET", "IMAGE_S3_ACCESS_KEY_ID", "IMAGE_S3_SECRET_ACCESS_KEY", "IMAGE_S3_PATH_STYLE", "IMAGE_S3_ALLOW_HTTP",
            "IMAGE_SHAREPOINT_TENANT_ID", "IMAGE_SHAREPOINT_CLIENT_ID", "IMAGE_SHAREPOINT_CLIENT_SECRET", "IMAGE_SHAREPOINT_SITE_ID", "IMAGE_SHAREPOINT_DRIVE_ID",
            "IMAGE_SHAREPOINT_FOLDER", "IMAGE_SHAREPOINT_GRAPH_BASE_URL", "IMAGE_SHAREPOINT_AUTHORITY", "IMAGE_SHAREPOINT_ALLOW_HTTP",
            "IMAGE_CACHE_ENABLED", "IMAGE_CACHE_MAX_SIZE_MB", "IMAGE_CACHE_TTL_MINUTES", "IMAGE_CACHE_MEMORY_MB", "IMAGE_CACHE_PATH", "IMAGE_HTTP_TIMEOUT_SECONDS",
        };
        foreach (var k in all) Environment.SetEnvironmentVariable(k, null);
        foreach (var (k, v) in env) Environment.SetEnvironmentVariable(k, v);
        try { return ImageStorageSettings.Load(); }
        finally { foreach (var k in all) Environment.SetEnvironmentVariable(k, null); }
    }

    private static void ConfigValidation(Action<bool, string> check)
    {
        check(Cfg().Validate().Count == 0 && Cfg().Provider == "database", "U7 Config: ohne Angaben gilt Provider 'database' und die Konfiguration ist gültig (kompatibel zu bestehenden Installationen)");
        check(Cfg(("IMAGE_STORAGE_PROVIDER", "ftp")).Validate().Any(e => e.Contains("ungültig")), "U7 Config: unbekannter Provider wird abgelehnt");
        check(Cfg(("IMAGE_STORAGE_PROVIDER", "local")).Validate().Count == 0, "U7 Config: local mit Standardpfad ist gültig");

        var s3 = Cfg(("IMAGE_STORAGE_PROVIDER", "s3")).Validate();
        check(s3.Any(e => e.Contains("IMAGE_S3_BUCKET")) && s3.Any(e => e.Contains("ACCESS_KEY")), "U7 Config: s3 ohne Bucket/Zugangsdaten → verständliche Fehler (alle auf einmal)");
        var okS3 = ("IMAGE_S3_BUCKET", "learncards"); var k1 = ("IMAGE_S3_ACCESS_KEY_ID", "AKIA"); var k2 = ("IMAGE_S3_SECRET_ACCESS_KEY", "secret");
        check(Cfg(("IMAGE_STORAGE_PROVIDER", "s3"), okS3, k1, k2, ("IMAGE_S3_REGION", "eu-central-1")).Validate().Count == 0, "U7 Config: vollständige S3-Konfiguration (AWS-Standardendpunkt) ist gültig");
        check(Cfg(("IMAGE_STORAGE_PROVIDER", "s3"), okS3, k1, k2, ("IMAGE_S3_ENDPOINT", "http://minio.local:9000")).Validate().Any(e => e.Contains("https")), "U7 Config: http-Endpunkt ohne ausdrückliche Freigabe wird abgelehnt");
        check(Cfg(("IMAGE_STORAGE_PROVIDER", "s3"), okS3, k1, k2, ("IMAGE_S3_ENDPOINT", "http://127.0.0.1:9000"), ("IMAGE_S3_ALLOW_HTTP", "true")).Validate().Count == 0, "U7 Config: http nur mit IMAGE_S3_ALLOW_HTTP=true");
        check(Cfg(("IMAGE_STORAGE_PROVIDER", "s3"), okS3, k1, k2, ("IMAGE_S3_ENDPOINT", "https://user:pw@evil.example/")).Validate().Any(e => e.Contains("Zugangsdaten")), "U7 Config: Endpunkt mit Zugangsdaten in der URL wird abgelehnt");
        check(Cfg(("IMAGE_STORAGE_PROVIDER", "s3"), okS3, k1, k2, ("IMAGE_S3_ENDPOINT", "https://s3.example.com/?x=1")).Validate().Any(e => e.Contains("Query")), "U7 Config: Endpunkt mit Query wird abgelehnt");
        check(Cfg(("IMAGE_STORAGE_PROVIDER", "s3"), okS3, k1, k2, ("IMAGE_S3_ENDPOINT", "ftp://x")).Validate().Any(), "U7 Config: nicht-http(s)-Endpunkt wird abgelehnt");
        check(Cfg(("IMAGE_STORAGE_PROVIDER", "s3"), ("IMAGE_S3_BUCKET", "Ungültiger Name!"), k1, k2).Validate().Any(e => e.Contains("Bucket")), "U7 Config: ungültiger Bucketname wird abgelehnt");

        var az = Cfg(("IMAGE_STORAGE_PROVIDER", "azureblob")).Validate();
        check(az.Any(e => e.Contains("IMAGE_AZUREBLOB_ACCOUNT")), "U7 Config: azureblob ohne Konto → Fehler");
        check(Cfg(("IMAGE_STORAGE_PROVIDER", "azureblob"), ("IMAGE_AZUREBLOB_ACCOUNT", "learncardsstore")).Validate().Count == 0, "U7 Config: azureblob mit Managed Identity (Standard) braucht nur Konto und Container");
        check(Cfg(("IMAGE_STORAGE_PROVIDER", "azureblob"), ("IMAGE_AZUREBLOB_ACCOUNT", "learncardsstore"), ("IMAGE_AZUREBLOB_AUTH", "sharedkey")).Validate().Any(e => e.Contains("ACCOUNT_KEY")), "U7 Config: sharedkey ohne Schlüssel → Fehler");
        check(Cfg(("IMAGE_STORAGE_PROVIDER", "azureblob"), ("IMAGE_AZUREBLOB_ACCOUNT", "learncardsstore"), ("IMAGE_AZUREBLOB_AUTH", "clientsecret")).Validate().Any(e => e.Contains("TENANT_ID")), "U7 Config: clientsecret ohne Tenant/Client/Secret → Fehler");
        check(Cfg(("IMAGE_STORAGE_PROVIDER", "azureblob"), ("IMAGE_AZUREBLOB_ACCOUNT", "learncardsstore"), ("IMAGE_AZUREBLOB_CONTAINER", "BAD_NAME")).Validate().Any(e => e.Contains("Containername")), "U7 Config: ungültiger Containername");

        var sp = Cfg(("IMAGE_STORAGE_PROVIDER", "sharepoint")).Validate();
        check(sp.Any(e => e.Contains("SITE_ID")) && sp.Any(e => e.Contains("DRIVE_ID")) && sp.Any(e => e.Contains("CLIENT_SECRET")), "U7 Config: sharepoint ohne Site/Drive/App-Zugang → Fehler");
        check(Cfg(("IMAGE_STORAGE_PROVIDER", "sharepoint"), ("IMAGE_SHAREPOINT_TENANT_ID", "t"), ("IMAGE_SHAREPOINT_CLIENT_ID", "c"), ("IMAGE_SHAREPOINT_CLIENT_SECRET", "s"),
            ("IMAGE_SHAREPOINT_SITE_ID", "site"), ("IMAGE_SHAREPOINT_DRIVE_ID", "drive")).Validate().Count == 0, "U7 Config: vollständige SharePoint-Konfiguration ist gültig");
        check(Cfg(("IMAGE_STORAGE_PROVIDER", "sharepoint"), ("IMAGE_SHAREPOINT_TENANT_ID", "t"), ("IMAGE_SHAREPOINT_CLIENT_ID", "c"), ("IMAGE_SHAREPOINT_CLIENT_SECRET", "s"),
            ("IMAGE_SHAREPOINT_SITE_ID", "site"), ("IMAGE_SHAREPOINT_DRIVE_ID", "drive"), ("IMAGE_SHAREPOINT_FOLDER", "a/../../b")).Validate().Any(e => e.Contains("Zeichen")), "U7 Config: Zielordner mit '..' wird abgelehnt");
        check(Cfg(("IMAGE_STORAGE_PROVIDER", "database"), ("IMAGE_S3_BUCKET", "learncards")).Validate().Any(e => e.Contains("ACCESS_KEY")), "U7 Config: auch ein nicht-primärer, begonnener Provider wird geprüft (kein halb konfigurierter Zustand)");
        check(Cfg(("IMAGE_CACHE_MAX_SIZE_MB", "0")).Validate().Any() && Cfg(("IMAGE_CACHE_TTL_MINUTES", "-5")).Validate().Any(), "U7 Config: Cache-Limits werden geprüft");
        var ex = Assert(() => { var c = Cfg(("IMAGE_STORAGE_PROVIDER", "s3")); c.EnsureValid(); });
        check(ex is InvalidOperationException && ex.Message.Contains("Ungültige Bildspeicher-Konfiguration") && ex.Message.Contains("IMAGE_S3_BUCKET"), "U7 Config: EnsureValid wirft beim Start mit allen Fehlern");
    }

    private static Exception? Assert(Action a)
    {
        try { a(); return null; } catch (Exception e) { return e; }
    }

    // ─── Cache ──────────────────────────────────────────────────────────────

    private static CachedImage Img(byte[] data, double ttlMinutes = 10, string? etag = "\"e1\"") =>
        new(data, "image/png", Convert.ToHexString(SHA256.HashData(data)), etag, DateTime.UtcNow.AddMinutes(ttlMinutes));

    private static async Task CacheAsync(Action<bool, string> check, string tmp)
    {
        // Arbeitsspeicher: Limit und LRU-Eviction
        var stats = new ImageCacheStats();
        var mem = new MemoryImageCache(1_000_000, stats);
        var a = Img(Bytes(400_000, 1)); var b = Img(Bytes(400_000, 2)); var c = Img(Bytes(400_000, 3));
        mem.Set("a", a); mem.Set("b", b);
        _ = mem.Get("a");   // a ist zuletzt benutzt → b wird zuerst verdrängt
        await Task.Delay(5);
        mem.Set("c", c);
        check(mem.Bytes <= 1_000_000 && mem.Get("b") is null && mem.Get("a") is not null && mem.Get("c") is not null && stats.Evictions >= 1, "U8 Cache: Größenlimit wird eingehalten, älteste Zugriffe werden zuerst verdrängt (LRU)");
        mem.Set("big", Img(Bytes(2_000_000)));
        check(mem.Get("big") is null, "U8 Cache: Einträge größer als das Gesamtlimit werden nicht gespeichert");
        mem.Remove("a");
        check(mem.Get("a") is null, "U8 Cache: Entfernen");
        check(!Img(Bytes(10), ttlMinutes: -1).IsFresh && Img(Bytes(10)).IsFresh, "U8 Cache: TTL-Ablauf (IsFresh)");

        // Dateicache: Persistenz, Integrität, Limit
        var dir = Path.Combine(tmp, "unit-cache");
        var fstats = new ImageCacheStats();
        var file = new FileImageCache(dir, 700_000, fstats);
        var f1 = Img(Bytes(300_000, 11), etag: "\"x\"");
        await file.SetAsync("k1", f1, default);
        var again = new FileImageCache(dir, 700_000, new ImageCacheStats());   // neue Instanz = Neustart
        var loaded = await again.GetAsync("k1", default);
        check(loaded is not null && loaded.Data.SequenceEqual(f1.Data) && loaded.ETag == "\"x\"" && loaded.Checksum == f1.Checksum, "U9 Cache: persistenter Dateicache überlebt einen Neustart (inkl. ETag/Prüfsumme)");
        var files = Directory.GetFiles(dir);
        check(files.All(f => Path.GetFileName(f).Length >= 64) && files.All(f => !Path.GetFileName(f).Contains("k1")), "U9 Cache: Dateinamen sind Hashes des Schlüssels (keine Benutzereingabe im Pfad)");
        // Manipulation: Datei verändern → Eintrag wird verworfen
        var bin = Directory.GetFiles(dir, "*.bin").First();
        var tampered = File.ReadAllBytes(bin); tampered[0] ^= 0xFF; File.WriteAllBytes(bin, tampered);
        check(await again.GetAsync("k1", default) is null && !File.Exists(bin), "U9 Cache: manipulierte Cache-Datei wird erkannt und verworfen");
        await file.SetAsync("k2", Img(Bytes(300_000, 12)), default); await Task.Delay(20);
        await file.SetAsync("k3", Img(Bytes(300_000, 13)), default); await Task.Delay(20);
        await file.SetAsync("k4", Img(Bytes(300_000, 14)), default);
        var total = new DirectoryInfo(dir).GetFiles("*.bin").Sum(f => f.Length);
        check(total <= 700_000 && fstats.Evictions >= 1, "U9 Cache: Dateicache hält das Größenlimit durch Eviction");
        check(!Directory.GetFiles(dir, "*.tmp").Any(), "U9 Cache: keine temporären Dateien");

        // Zweistufig: Treffer aus Arbeitsspeicher, dann Datei, Promotion, Invalidierung
        var ts = new ImageCacheStats();
        var two = new TwoLevelImageCache(new MemoryImageCache(5_000_000, ts), new FileImageCache(Path.Combine(tmp, "unit-cache2"), 5_000_000, ts), ts);
        var v = Img(Bytes(1000, 21));
        check(await two.GetAsync("x", default) is null && ts.Misses == 1, "U10 Cache: Fehlschlag wird gezählt");
        await two.SetAsync("x", v, default);
        check(await two.GetAsync("x", default) is not null && ts.MemoryHits == 1, "U10 Cache: Treffer aus dem Arbeitsspeicher");
        var cold = new TwoLevelImageCache(new MemoryImageCache(5_000_000, ts), new FileImageCache(Path.Combine(tmp, "unit-cache2"), 5_000_000, ts), ts);   // „Neustart“
        check(await cold.GetAsync("x", default) is not null && ts.FileHits == 1 && await cold.GetAsync("x", default) is not null && ts.MemoryHits == 2, "U10 Cache: nach Neustart Treffer aus der Datei, danach im Arbeitsspeicher (Promotion)");
        await cold.RemoveAsync("x", default);
        check(await cold.GetAsync("x", default) is null, "U10 Cache: Invalidierung entfernt beide Stufen");
        var gone = new TwoLevelImageCache(new MemoryImageCache(5_000_000, new ImageCacheStats()), new FileImageCache(Path.Combine(tmp, "unit-cache2"), 5_000_000, new ImageCacheStats()), new ImageCacheStats());
        check(await gone.GetAsync("x", default) is null, "U10 Cache: nach Invalidierung auch in der Datei nicht mehr vorhanden");
    }
}
