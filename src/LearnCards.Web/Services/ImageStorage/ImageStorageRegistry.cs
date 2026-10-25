using LearnCards.Web.Data;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services.ImageStorage;

/// <summary>
/// Hält die konfigurierten Bildspeicher (ADR 0017). Der <b>primäre</b> Provider nimmt neue Bilder auf; weitere konfigurierte Provider bleiben
/// lesbar (vorhandene Bilder tragen ihren Provider in den Metadaten) und sind Quelle/Ziel einer ausdrücklich gestarteten Migration.
/// Der Database-Provider ist immer verfügbar, damit bestehende Installationen und Altdaten lesbar bleiben.
/// </summary>
public sealed class ImageStorageRegistry
{
    private readonly Dictionary<string, IImageStorageProvider> _providers = new(StringComparer.Ordinal);

    public ImageStorageRegistry(AppConfig cfg, IDatabase db)
    {
        var s = cfg.ImageStorage;
        PrimaryName = s.Provider;
        _providers[ImageProviderNames.Database] = new DatabaseImageStorageProvider(db);
        if (s.IsConfigured(ImageProviderNames.Local))
            _providers[ImageProviderNames.Local] = new LocalImageStorageProvider(s.LocalPath);
        if (s.IsConfigured(ImageProviderNames.AzureBlob))
            _providers[ImageProviderNames.AzureBlob] = new AzureBlobImageStorageProvider(s.AzureBlob, s.HttpTimeoutSeconds);
        if (s.IsConfigured(ImageProviderNames.S3))
            _providers[ImageProviderNames.S3] = new S3ImageStorageProvider(s.S3, s.HttpTimeoutSeconds);
        if (s.IsConfigured(ImageProviderNames.SharePoint))
            _providers[ImageProviderNames.SharePoint] = new SharePointImageStorageProvider(s.SharePoint, s.HttpTimeoutSeconds);
    }

    public string PrimaryName { get; }
    public IImageStorageProvider Primary => _providers[PrimaryName];
    public IReadOnlyCollection<string> ConfiguredNames => _providers.Keys;
    public bool IsConfigured(string name) => _providers.ContainsKey(name);

    public IImageStorageProvider Get(string name) =>
        _providers.TryGetValue(name, out var p)
            ? p
            : throw new ImageStorageException(ImageStorageError.NotConfigured, name,
                $"Der Bildspeicher '{name}' ist nicht konfiguriert, dieses Bild liegt aber dort. Konfiguriere den Provider (IMAGE_* Umgebungsvariablen) oder migriere die Bilder.");
}

/// <summary>Baut den Bild-Cache aus der Konfiguration (ADR 0017).</summary>
public static class ImageCacheFactory
{
    public static IImageCache Create(ImageStorageSettings s, out FileImageCache? fileCache)
    {
        fileCache = null;
        if (!s.Cache.Enabled) return new NullImageCache();

        var stats = new ImageCacheStats();
        var maxBytes = (long)s.Cache.MaxSizeMb * 1024 * 1024;
        var memBytes = Math.Min((long)s.Cache.MemoryMb * 1024 * 1024, maxBytes);
        var memory = new MemoryImageCache(memBytes, stats);

        // Persistenter Cache: ausdrücklich per IMAGE_CACHE_PATH, bei SharePoint standardmäßig (Graph-Zugriffe sind langsam und gedrosselt)
        var path = s.Cache.Path;
        if (path.Length == 0 && s.IsConfigured(ImageProviderNames.SharePoint)) path = "data/image-cache";
        if (path.Length > 0) fileCache = new FileImageCache(path, maxBytes, stats);
        return new TwoLevelImageCache(memory, fileCache, stats);
    }
}
