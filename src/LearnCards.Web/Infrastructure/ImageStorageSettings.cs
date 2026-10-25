using System.Globalization;

namespace LearnCards.Web.Infrastructure;

/// <summary>Namen der Bildspeicher-Provider (ADR 0017).</summary>
public static class ImageProviderNames
{
    public const string Database = "database";
    public const string Local = "local";
    public const string AzureBlob = "azureblob";
    public const string S3 = "s3";
    public const string SharePoint = "sharepoint";

    public static readonly string[] All = { Database, Local, AzureBlob, S3, SharePoint };
}

public class AzureBlobSettings
{
    public string Account { get; set; } = "";
    /// <summary>Leer = https://{Account}.blob.core.windows.net; sonst z. B. ein Azurite-Endpunkt (http://127.0.0.1:10000/devstoreaccount1).</summary>
    public string Endpoint { get; set; } = "";
    public string Container { get; set; } = "learncards-images";
    /// <summary>managedidentity (Standard) | clientsecret | sharedkey (nur Entwicklung/Test)</summary>
    public string Auth { get; set; } = "managedidentity";
    public string TenantId { get; set; } = "";
    /// <summary>Bei managedidentity: optionale Client-ID einer benutzerzugewiesenen Identität.</summary>
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string AccountKey { get; set; } = "";
    public bool AllowHttp { get; set; }
    public string Authority { get; set; } = "https://login.microsoftonline.com";
    /// <summary>Lebensdauer der Lese-SAS in Sekunden (Standard 60, erlaubt 10–600).</summary>
    public int SasTtlSeconds { get; set; } = 60;
    /// <summary>Private/Loopback-Ziele erlauben (Private Endpoint, Azurite). Standard: nur öffentliche Adressen.</summary>
    public bool AllowPrivateNetwork { get; set; }
}

public class S3Settings
{
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string SessionToken { get; set; } = "";
    public bool PathStyle { get; set; }
    public bool AllowHttp { get; set; }
    public int PresignTtlSeconds { get; set; } = 60;
    public bool AllowPrivateNetwork { get; set; }
}

public class SharePointSettings
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string SiteId { get; set; } = "";
    public string DriveId { get; set; } = "";
    public string Folder { get; set; } = "LearnCards/images";
    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";
    public string Authority { get; set; } = "https://login.microsoftonline.com";
    /// <summary>Nur für Tests gegen einen lokalen Graph-Mock; in Produktion nie setzen.</summary>
    public bool AllowHttp { get; set; }
    public bool AllowPrivateNetwork { get; set; }
}

public class ImageCacheSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxSizeMb { get; set; } = 512;
    public double TtlMinutes { get; set; } = 1440;
    public int MemoryMb { get; set; } = 64;
    /// <summary>Verzeichnis des persistenten Dateicaches; leer = nur Arbeitsspeicher (bei SharePoint: data/image-cache).</summary>
    public string Path { get; set; } = "";
}

/// <summary>
/// Konfiguration der Bildspeicherung (ADR 0017). Pro Installation ist genau ein <b>primärer</b> Provider aktiv (nimmt neue Bilder auf);
/// weitere Provider können konfiguriert bleiben, damit vorhandene Bilder lesbar sind und eine ausdrücklich gestartete Migration möglich ist.
/// Secrets stammen ausschließlich aus Umgebungsvariablen.
/// </summary>
public class ImageStorageSettings
{
    public string Provider { get; set; } = ImageProviderNames.Database;
    public string LocalPath { get; set; } = "data/images";
    public bool LocalPathExplicit { get; set; }
    public AzureBlobSettings AzureBlob { get; set; } = new();
    public S3Settings S3 { get; set; } = new();
    public SharePointSettings SharePoint { get; set; } = new();
    public ImageCacheSettings Cache { get; set; } = new();
    public int HttpTimeoutSeconds { get; set; } = 30;
    /// <summary>Notausschalter für den zentralen Bild-Proxy (<c>GET /api/images/{id}</c>); Standard an (ADR 0018).</summary>
    public bool ProxyEnabled { get; set; } = true;

    public static ImageStorageSettings Load()
    {
        string E(string key, string fallback = "") => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v.Trim() : fallback;
        bool B(string key, bool fallback = false) => E(key) is { Length: > 0 } v ? v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" : fallback;
        int I(string key, int fallback) => int.TryParse(E(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        double D(string key, double fallback) => double.TryParse(E(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;

        var s = new ImageStorageSettings
        {
            Provider = E("IMAGE_STORAGE_PROVIDER", ImageProviderNames.Database).ToLowerInvariant(),
            LocalPath = E("IMAGE_LOCAL_PATH", "data/images"),
            LocalPathExplicit = E("IMAGE_LOCAL_PATH").Length > 0,
            HttpTimeoutSeconds = I("IMAGE_HTTP_TIMEOUT_SECONDS", 30),
            ProxyEnabled = B("IMAGE_PROXY_ENABLED", true),
            AzureBlob = new AzureBlobSettings
            {
                Account = E("IMAGE_AZUREBLOB_ACCOUNT"),
                Endpoint = E("IMAGE_AZUREBLOB_ENDPOINT").TrimEnd('/'),
                Container = E("IMAGE_AZUREBLOB_CONTAINER", "learncards-images"),
                Auth = E("IMAGE_AZUREBLOB_AUTH", "managedidentity").ToLowerInvariant(),
                TenantId = E("IMAGE_AZUREBLOB_TENANT_ID"),
                ClientId = E("IMAGE_AZUREBLOB_CLIENT_ID"),
                ClientSecret = E("IMAGE_AZUREBLOB_CLIENT_SECRET"),
                AccountKey = E("IMAGE_AZUREBLOB_ACCOUNT_KEY"),
                AllowHttp = B("IMAGE_AZUREBLOB_ALLOW_HTTP"),
                Authority = E("IMAGE_AZUREBLOB_AUTHORITY", "https://login.microsoftonline.com").TrimEnd('/'),
                SasTtlSeconds = I("IMAGE_AZURE_SAS_TTL_SECONDS", 60),
                AllowPrivateNetwork = B("IMAGE_AZUREBLOB_ALLOW_PRIVATE_NETWORK"),
            },
            S3 = new S3Settings
            {
                Endpoint = E("IMAGE_S3_ENDPOINT").TrimEnd('/'),
                Region = E("IMAGE_S3_REGION", "us-east-1"),
                Bucket = E("IMAGE_S3_BUCKET"),
                AccessKeyId = E("IMAGE_S3_ACCESS_KEY_ID"),
                SecretAccessKey = E("IMAGE_S3_SECRET_ACCESS_KEY"),
                SessionToken = E("IMAGE_S3_SESSION_TOKEN"),
                PathStyle = B("IMAGE_S3_PATH_STYLE"),
                AllowHttp = B("IMAGE_S3_ALLOW_HTTP"),
                PresignTtlSeconds = I("IMAGE_S3_PRESIGN_TTL_SECONDS", 60),
                AllowPrivateNetwork = B("IMAGE_S3_ALLOW_PRIVATE_NETWORK"),
            },
            SharePoint = new SharePointSettings
            {
                TenantId = E("IMAGE_SHAREPOINT_TENANT_ID"),
                ClientId = E("IMAGE_SHAREPOINT_CLIENT_ID"),
                ClientSecret = E("IMAGE_SHAREPOINT_CLIENT_SECRET"),
                SiteId = E("IMAGE_SHAREPOINT_SITE_ID"),
                DriveId = E("IMAGE_SHAREPOINT_DRIVE_ID"),
                Folder = E("IMAGE_SHAREPOINT_FOLDER", "LearnCards/images").Trim('/'),
                GraphBaseUrl = E("IMAGE_SHAREPOINT_GRAPH_BASE_URL", "https://graph.microsoft.com/v1.0").TrimEnd('/'),
                Authority = E("IMAGE_SHAREPOINT_AUTHORITY", "https://login.microsoftonline.com").TrimEnd('/'),
                AllowHttp = B("IMAGE_SHAREPOINT_ALLOW_HTTP"),
                AllowPrivateNetwork = B("IMAGE_SHAREPOINT_ALLOW_PRIVATE_NETWORK"),
            },
            Cache = new ImageCacheSettings
            {
                Enabled = B("IMAGE_CACHE_ENABLED", true),
                MaxSizeMb = I("IMAGE_CACHE_MAX_SIZE_MB", 512),
                TtlMinutes = D("IMAGE_CACHE_TTL_MINUTES", 1440),
                MemoryMb = I("IMAGE_CACHE_MEMORY_MB", 64),
                Path = E("IMAGE_CACHE_PATH"),
            },
        };
        return s;
    }

    /// <summary>Provider, die vollständig genug konfiguriert sind, um genutzt zu werden (der primäre immer; weitere nur bei gesetzten Kernwerten).</summary>
    public bool IsConfigured(string provider) => provider switch
    {
        ImageProviderNames.Database => true,
        ImageProviderNames.Local => Provider == ImageProviderNames.Local || LocalPathExplicit,
        ImageProviderNames.AzureBlob => Provider == ImageProviderNames.AzureBlob || AzureBlob.Account.Length > 0,
        ImageProviderNames.S3 => Provider == ImageProviderNames.S3 || S3.Bucket.Length > 0,
        ImageProviderNames.SharePoint => Provider == ImageProviderNames.SharePoint || SharePoint.SiteId.Length > 0 || SharePoint.DriveId.Length > 0,
        _ => false,
    };

    /// <summary>Alle Konfigurationsfehler mit verständlichen Hinweisen (leer = gültig). Wird beim Start geprüft.</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (!ImageProviderNames.All.Contains(Provider))
            errors.Add($"IMAGE_STORAGE_PROVIDER='{Provider}' ist ungültig. Erlaubt: {string.Join(", ", ImageProviderNames.All)}.");
        if (HttpTimeoutSeconds is < 1 or > 600) errors.Add("IMAGE_HTTP_TIMEOUT_SECONDS muss zwischen 1 und 600 liegen.");
        if (Cache.MaxSizeMb is < 1 or > 1_048_576) errors.Add("IMAGE_CACHE_MAX_SIZE_MB muss zwischen 1 und 1048576 liegen.");
        if (Cache.TtlMinutes <= 0) errors.Add("IMAGE_CACHE_TTL_MINUTES muss größer als 0 sein.");
        if (Cache.MemoryMb < 1) errors.Add("IMAGE_CACHE_MEMORY_MB muss mindestens 1 sein.");

        if (IsConfigured(ImageProviderNames.Local) && string.IsNullOrWhiteSpace(LocalPath))
            errors.Add("IMAGE_LOCAL_PATH darf nicht leer sein (Provider 'local').");

        if (IsConfigured(ImageProviderNames.AzureBlob))
        {
            var a = AzureBlob;
            var p = "Provider 'azureblob': ";
            if (a.Account.Length == 0) errors.Add(p + "IMAGE_AZUREBLOB_ACCOUNT fehlt.");
            else if (!System.Text.RegularExpressions.Regex.IsMatch(a.Account, "^[a-z0-9]{3,24}$") && a.Endpoint.Length == 0)
                errors.Add(p + "IMAGE_AZUREBLOB_ACCOUNT muss 3–24 Kleinbuchstaben/Ziffern enthalten.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(a.Container, "^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){2,62}$"))
                errors.Add(p + "IMAGE_AZUREBLOB_CONTAINER ist kein gültiger Containername (3–63 Kleinbuchstaben, Ziffern, Bindestriche).");
            if (a.Auth is not ("managedidentity" or "clientsecret" or "sharedkey"))
                errors.Add(p + "IMAGE_AZUREBLOB_AUTH muss managedidentity, clientsecret oder sharedkey sein.");
            if (a.Auth == "clientsecret" && (a.TenantId.Length == 0 || a.ClientId.Length == 0 || a.ClientSecret.Length == 0))
                errors.Add(p + "clientsecret benötigt IMAGE_AZUREBLOB_TENANT_ID, _CLIENT_ID und _CLIENT_SECRET.");
            if (a.Auth == "sharedkey" && a.AccountKey.Length == 0)
                errors.Add(p + "sharedkey benötigt IMAGE_AZUREBLOB_ACCOUNT_KEY (nur für Entwicklung/Test; in Produktion Managed Identity verwenden).");
            if (a.SasTtlSeconds is < 10 or > 600) errors.Add(p + "IMAGE_AZURE_SAS_TTL_SECONDS muss zwischen 10 und 600 liegen (Standard 60).");
            if (a.Endpoint.Length > 0) ValidateEndpoint(errors, p + "IMAGE_AZUREBLOB_ENDPOINT", a.Endpoint, a.AllowHttp, "IMAGE_AZUREBLOB_ALLOW_HTTP");
        }

        if (IsConfigured(ImageProviderNames.S3))
        {
            var s = S3;
            var p = "Provider 's3': ";
            if (s.Bucket.Length == 0) errors.Add(p + "IMAGE_S3_BUCKET fehlt.");
            else if (!System.Text.RegularExpressions.Regex.IsMatch(s.Bucket, "^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$"))
                errors.Add(p + "IMAGE_S3_BUCKET ist kein gültiger Bucketname.");
            if (s.AccessKeyId.Length == 0 || s.SecretAccessKey.Length == 0)
                errors.Add(p + "IMAGE_S3_ACCESS_KEY_ID und IMAGE_S3_SECRET_ACCESS_KEY fehlen (nur über Umgebungsvariablen/Secret-Verwaltung setzen).");
            if (!System.Text.RegularExpressions.Regex.IsMatch(s.Region, "^[a-z0-9-]{2,30}$")) errors.Add(p + "IMAGE_S3_REGION ist ungültig.");
            if (s.PresignTtlSeconds is < 10 or > 600) errors.Add(p + "IMAGE_S3_PRESIGN_TTL_SECONDS muss zwischen 10 und 600 liegen (Standard 60).");
            if (s.Endpoint.Length > 0) ValidateEndpoint(errors, p + "IMAGE_S3_ENDPOINT", s.Endpoint, s.AllowHttp, "IMAGE_S3_ALLOW_HTTP");
            else if (s.AllowHttp) errors.Add(p + "IMAGE_S3_ALLOW_HTTP ist nur zusammen mit IMAGE_S3_ENDPOINT sinnvoll.");
        }

        if (IsConfigured(ImageProviderNames.SharePoint))
        {
            var sp = SharePoint;
            var p = "Provider 'sharepoint': ";
            if (sp.TenantId.Length == 0 || sp.ClientId.Length == 0 || sp.ClientSecret.Length == 0)
                errors.Add(p + "IMAGE_SHAREPOINT_TENANT_ID, _CLIENT_ID und _CLIENT_SECRET fehlen (App-only-Zugriff; die Anmelde-Tokens der Benutzer werden nie für Graph verwendet).");
            if (sp.SiteId.Length == 0) errors.Add(p + "IMAGE_SHAREPOINT_SITE_ID fehlt.");
            if (sp.DriveId.Length == 0) errors.Add(p + "IMAGE_SHAREPOINT_DRIVE_ID fehlt.");
            if (sp.Folder.Contains("..") || sp.Folder.Contains('\\') || sp.Folder.Any(char.IsControl))
                errors.Add(p + "IMAGE_SHAREPOINT_FOLDER enthält unzulässige Zeichen ('..', '\\', Steuerzeichen).");
            ValidateEndpoint(errors, p + "IMAGE_SHAREPOINT_GRAPH_BASE_URL", sp.GraphBaseUrl, sp.AllowHttp, "IMAGE_SHAREPOINT_ALLOW_HTTP");
            ValidateEndpoint(errors, p + "IMAGE_SHAREPOINT_AUTHORITY", sp.Authority, sp.AllowHttp, "IMAGE_SHAREPOINT_ALLOW_HTTP");
        }
        return errors;
    }

    /// <summary>Endpunkte: absolute https-URL (http nur mit ausdrücklichem Flag), ohne Benutzerinfo, Query oder Fragment.</summary>
    private static void ValidateEndpoint(List<string> errors, string name, string value, bool allowHttp, string flag)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) { errors.Add($"{name} ist keine gültige absolute URL."); return; }
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && allowHttp))
            errors.Add($"{name} muss https verwenden (http nur mit {flag}=true, ausschließlich für lokale Tests).");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            errors.Add($"{name} darf keine Zugangsdaten, Query oder Fragment enthalten.");
    }

    /// <summary>Wirft eine <see cref="InvalidOperationException"/> mit allen Fehlern, wenn die Konfiguration ungültig ist.</summary>
    public void EnsureValid()
    {
        var errors = Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException("Ungültige Bildspeicher-Konfiguration:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", errors));
    }
}
