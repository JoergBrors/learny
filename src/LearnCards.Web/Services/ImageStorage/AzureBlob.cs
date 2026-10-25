using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services.ImageStorage;

/// <summary>Gemeinsame Hilfen für Azure-Blob-REST (Adressierung, Zeitformate).</summary>
internal sealed class AzureBlobTarget
{
    public const string ApiVersion = "2020-12-06";
    public AzureBlobSettings Settings { get; }
    public Uri Base { get; }          // Endpunkt inkl. evtl. Konto-Pfad (Azurite)

    public AzureBlobTarget(AzureBlobSettings s)
    {
        Settings = s;
        Base = new Uri((s.Endpoint.Length > 0 ? s.Endpoint : $"https://{s.Account}.blob.core.windows.net").TrimEnd('/') + "/");
    }

    /// <summary>Pfad (absolut, unescaped) des Blobs: {basis}/{container}/{key[..2]}/{key}.</summary>
    public string BlobPath(string key) => Base.AbsolutePath + Settings.Container + "/" + key[..2] + "/" + key;
    public string ContainerPath => Base.AbsolutePath + Settings.Container;
    public Uri BlobUri(string key) => new(Base, BlobPath(key));
    public static string IsoTime(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
/// Stellt kurzlebige, blob-bezogene <b>Lese-SAS</b> aus (ADR 0018). Standard (Managed Identity / Client-Secret): <b>User-Delegation-SAS</b> — der Schlüssel wird mit
/// einem Entra-Token beim Speicherkonto angefordert, kein Kontoschlüssel im Betrieb. <c>sharedkey</c> (nur Entwicklung/Azurite): Service-SAS, signiert mit dem Kontoschlüssel.
/// Berechtigung ausschließlich <c>r</c>, Ressource <c>b</c> (ein Blob), Gültigkeit <c>IMAGE_AZURE_SAS_TTL_SECONDS</c> (Standard 60 s), Protokoll https (http nur bei Entwicklungs-Flag).
/// </summary>
public sealed class AzureSasBroker : IStorageAccessBroker
{
    private readonly AzureBlobTarget _t;
    private readonly IAccessTokenSource? _tokens;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UserDelegationKey? _key;

    private sealed record UserDelegationKey(string Oid, string Tid, string Start, string Expiry, string Service, string Version, string Value, DateTimeOffset ExpiresAt);

    internal AzureSasBroker(AzureBlobTarget target, IAccessTokenSource? tokens, HttpClient http)
    {
        _t = target; _tokens = tokens; _http = http;
    }

    public void Invalidate() { _key = null; }

    /// <summary>Broker für Service-SAS mit Kontoschlüssel (Entwicklung/Test, Azurite) — ohne Netzwerkzugriff beim Ausstellen.</summary>
    public static AzureSasBroker CreateForSharedKey(AzureBlobSettings s) => new(new AzureBlobTarget(s), null, new HttpClient());

    public async Task<StorageGrant> GetReadGrantAsync(string key, CancellationToken ct = default)
    {
        ImageStorageKey.Require("azureblob", key);
        var s = _t.Settings;
        var ttl = Math.Clamp(s.SasTtlSeconds, 10, 600);
        var start = DateTimeOffset.UtcNow.AddMinutes(-2);            // Toleranz für Uhrenabweichung
        var expiry = DateTimeOffset.UtcNow.AddSeconds(ttl);
        var canonical = "/blob/" + (s.Account.Length > 0 ? s.Account : "unknown") + "/" + s.Container + "/" + key[..2] + "/" + key;
        var protocol = _t.Base.Scheme == Uri.UriSchemeHttp ? "https,http" : "https";

        var q = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["sv"] = AzureBlobTarget.ApiVersion, ["sr"] = "b", ["sp"] = "r",
            ["st"] = AzureBlobTarget.IsoTime(start), ["se"] = AzureBlobTarget.IsoTime(expiry), ["spr"] = protocol,
        };

        string signature;
        if (s.Auth == "sharedkey")
        {
            // Service-SAS: sp, st, se, resource, si, sip, spr, sv, sr, snapshot, encryptionscope, rscc, rscd, rsce, rscl, rsct
            var sts = string.Join("\n", "r", q["st"], q["se"], canonical, "", "", protocol, AzureBlobTarget.ApiVersion, "b", "", "", "", "", "", "", "");
            signature = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(s.AccountKey), Encoding.UTF8.GetBytes(sts)));
        }
        else
        {
            var udk = await GetUserDelegationKeyAsync(expiry, ct);
            q["skoid"] = udk.Oid; q["sktid"] = udk.Tid; q["skt"] = udk.Start; q["ske"] = udk.Expiry; q["sks"] = udk.Service; q["skv"] = udk.Version;
            // User-Delegation-SAS: sp, st, se, resource, skoid, sktid, skt, ske, sks, skv, saoid, suoid, scid, sip, spr, sv, sr, snapshot, encryptionscope, rscc..rsct
            var sts = string.Join("\n", "r", q["st"], q["se"], canonical, udk.Oid, udk.Tid, udk.Start, udk.Expiry, udk.Service, udk.Version, "", "", "", "", protocol,
                AzureBlobTarget.ApiVersion, "b", "", "", "", "", "", "", "");
            signature = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(udk.Value), Encoding.UTF8.GetBytes(sts)));
        }
        q["sig"] = signature;

        var query = string.Join("&", q.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
        return new StorageGrant(new Uri(_t.BlobUri(key).GetLeftPart(UriPartial.Path) + "?" + query), expiry);
    }

    private async Task<UserDelegationKey> GetUserDelegationKeyAsync(DateTimeOffset needUntil, CancellationToken ct)
    {
        if (_key is { } k && k.ExpiresAt > needUntil.AddMinutes(1)) return k;
        await _gate.WaitAsync(ct);
        try
        {
            if (_key is { } k2 && k2.ExpiresAt > needUntil.AddMinutes(1)) return k2;
            if (_tokens is null) throw new ImageStorageException(ImageStorageError.NotConfigured, "azureblob", "Für User-Delegation-SAS fehlt die Entra-Anmeldung.");
            var token = await _tokens.GetTokenAsync("https://storage.azure.com/.default", ct);
            var start = DateTimeOffset.UtcNow.AddMinutes(-2);
            var end = DateTimeOffset.UtcNow.AddHours(1);
            var body = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><KeyInfo><Start>{AzureBlobTarget.IsoTime(start)}</Start><Expiry>{AzureBlobTarget.IsoTime(end)}</Expiry></KeyInfo>";
            using var response = await SafeHttp.SendAsync(_http, "azureblob", () =>
            {
                var r = new HttpRequestMessage(HttpMethod.Post, new Uri(_t.Base.GetLeftPart(UriPartial.Authority) + _t.Base.AbsolutePath + "?restype=service&comp=userdelegationkey"))
                { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                r.Headers.TryAddWithoutValidation("x-ms-version", AzureBlobTarget.ApiVersion);
                return r;
            }, ct);
            if (!response.IsSuccessStatusCode)
                throw SafeHttp.StatusError("azureblob", response.StatusCode, "User-Delegation-Schlüssel (Rolle 'Storage Blob Delegator' fehlt?)");
            var xml = XDocument.Parse(Encoding.UTF8.GetString(await SafeHttp.ReadCappedAsync(response, "azureblob", 64 * 1024, ct))).Root!;
            string V(string n) => xml.Element(n)?.Value ?? throw new ImageStorageException(ImageStorageError.Unavailable, "azureblob", "Unerwartete Antwort bei User-Delegation-Schlüssel.");
            var expires = DateTimeOffset.Parse(V("SignedExpiry"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            return _key = new UserDelegationKey(V("SignedOid"), V("SignedTid"), V("SignedStart"), V("SignedExpiry"), V("SignedService"), V("SignedVersion"), V("Value"), expires);
        }
        finally { _gate.Release(); }
    }
}

/// <summary>
/// Azure-Blob-Speicher über REST (kein SDK). Schreiben/Löschen/Prüfen mit Entra-Token (Managed Identity oder Client-Secret) bzw. SharedKey (nur Entwicklung/Azurite).
/// <b>Lesen erfolgt ausschließlich über eine Lese-SAS</b> des <see cref="IStorageAccessBroker"/>: URL wird geprüft (fester Endpunkt, Container, Objekt, nur SAS-Parameter),
/// es werden keine Weiterleitungen verfolgt, die Antwortgröße ist begrenzt; bei Ablauf wird kontrolliert genau einmal ein neues Grant geholt.
/// </summary>
public sealed class AzureBlobImageStorageProvider : IImageStorageProvider
{
    private static readonly IReadOnlySet<string> SasQueryKeys = new HashSet<string>(StringComparer.Ordinal)
        { "sv", "sr", "sp", "st", "se", "spr", "sig", "skoid", "sktid", "skt", "ske", "sks", "skv" };

    private readonly AzureBlobTarget _t;
    private readonly HttpClient _http;
    private readonly IAccessTokenSource? _tokens;
    private readonly IStorageAccessBroker _broker;
    private volatile bool _containerChecked;

    public AzureBlobImageStorageProvider(AzureBlobSettings s, int timeoutSeconds, IStorageAccessBroker? brokerOverride = null)
    {
        _t = new AzureBlobTarget(s);
        _http = SafeHttp.CreateClient(s.AllowPrivateNetwork, timeoutSeconds);
        if (s.Auth != "sharedkey")
            _tokens = new EntraTokenSource("azureblob", s.Auth == "managedidentity", s.TenantId, s.ClientId, s.ClientSecret, s.Authority, s.AllowPrivateNetwork, timeoutSeconds);
        _broker = brokerOverride ?? new AzureSasBroker(_t, _tokens, _http);
    }

    public string Name => "azureblob";
    public bool IsRemote => true;
    internal IStorageAccessBroker Broker => _broker;

    // ─── Schreiben / Löschen / Prüfen (Daten-Ebene, serverseitig authentifiziert) ─────────────

    public async Task<string?> PutAsync(string key, byte[] data, string mimeType, CancellationToken ct = default)
    {
        ImageStorageKey.Require(Name, key);
        await EnsureContainerAsync(ct);
        using var response = await SendAuthedAsync(HttpMethod.Put, _t.BlobPath(key), "", () =>
        {
            var c = new ByteArrayContent(data);
            c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return c;
        }, data.Length, new() { ["x-ms-blob-type"] = "BlockBlob" }, ct);
        if (!response.IsSuccessStatusCode) throw SafeHttp.StatusError(Name, response.StatusCode, "Bild speichern");
        return response.Headers.ETag?.Tag;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        ImageStorageKey.Require(Name, key);
        using var response = await SendAuthedAsync(HttpMethod.Head, _t.BlobPath(key), "", null, 0, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode) throw SafeHttp.StatusError(Name, response.StatusCode, "Bild prüfen");
        return true;
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ImageStorageKey.Require(Name, key);
        using var response = await SendAuthedAsync(HttpMethod.Delete, _t.BlobPath(key), "", null, 0, null, ct);
        if (response.StatusCode != HttpStatusCode.NotFound && !response.IsSuccessStatusCode) throw SafeHttp.StatusError(Name, response.StatusCode, "Bild löschen");
    }

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_containerChecked) return;
        using var response = await SendAuthedAsync(HttpMethod.Put, _t.ContainerPath, "\nrestype:container", null, 0, null, ct, "?restype=container");
        // 201 = angelegt, 409 = existiert bereits; 403 = keine Berechtigung zum Anlegen (Container muss dann vorhanden sein)
        if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict or HttpStatusCode.Forbidden) { _containerChecked = true; return; }
        throw SafeHttp.StatusError(Name, response.StatusCode, "Container prüfen");
    }

    // ─── Lesen: ausschließlich über das Grant des Brokers ────────────────────────────────

    public async Task<StoredImage?> GetAsync(string key, string? ifNoneMatch = null, CancellationToken ct = default)
    {
        ImageStorageKey.Require(Name, key);
        for (var attempt = 0; ; attempt++)
        {
            var grant = await _broker.GetReadGrantAsync(key, ct);
            StorageUrlGuard.Validate(Name, grant.Url, _t.Base, _t.BlobPath(key), _t.Settings.AllowHttp, SasQueryKeys,
                new Dictionary<string, string> { ["sp"] = "r", ["sr"] = "b" });   // nur Lesen, nur ein Blob
            using var response = await SafeHttp.SendAsync(_http, Name, () =>
            {
                var r = new HttpRequestMessage(HttpMethod.Get, grant.Url);
                SafeHttp.AddIfNoneMatch(r, ifNoneMatch);
                r.Headers.TryAddWithoutValidation("x-ms-version", AzureBlobTarget.ApiVersion);
                return r;
            }, ct);
            if (response.StatusCode == HttpStatusCode.NotModified) return new StoredImage(Array.Empty<byte>(), ifNoneMatch, NotModified: true);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized && attempt == 0)
            {
                _broker.Invalidate();           // Grant abgelaufen/widerrufen → genau ein kontrollierter Neuversuch
                continue;
            }
            if (!response.IsSuccessStatusCode) throw SafeHttp.StatusError(Name, response.StatusCode, "Bild lesen");
            var data = await SafeHttp.ReadCappedAsync(response, Name, CardImageLimits.MaxDecodedBytes, ct);
            return new StoredImage(data, response.Headers.ETag?.Tag);
        }
    }

    public async Task<ProviderHealth> CheckHealthAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var key = ImageStorageKey.New();
        try
        {
            var probe = Encoding.ASCII.GetBytes("learncards-health");
            await PutAsync(key, probe, "application/octet-stream", ct);
            var read = await GetAsync(key, null, ct);     // prüft zugleich die SAS-Ausstellung
            await DeleteAsync(key, ct);
            return read is not null && read.Data.SequenceEqual(probe)
                ? new ProviderHealth(true, $"Container '{_t.Settings.Container}' beschreibbar, Lese-SAS funktioniert", sw.Elapsed)
                : new ProviderHealth(false, "Lese-/Schreibprobe fehlgeschlagen.", sw.Elapsed);
        }
        catch (Exception e) { return new ProviderHealth(false, SecretRedactor.Redact(e.Message), sw.Elapsed); }
    }

    // ─── Authentifizierte Anfragen ───────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAuthedAsync(HttpMethod method, string path, string canonicalQuery, Func<HttpContent>? content, int length,
        Dictionary<string, string>? extra, CancellationToken ct, string query = "")
    {
        var s = _t.Settings;
        var token = _tokens is null ? null : await _tokens.GetTokenAsync("https://storage.azure.com/.default", ct);
        return await SafeHttp.SendAsync(_http, Name, () =>
        {
            var uri = new Uri(_t.Base.GetLeftPart(UriPartial.Authority) + "/" + EscapePath(path) + query);
            var r = new HttpRequestMessage(method, uri);
            if (content is not null) r.Content = content();
            var date = DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture);
            r.Headers.TryAddWithoutValidation("x-ms-date", date);
            r.Headers.TryAddWithoutValidation("x-ms-version", AzureBlobTarget.ApiVersion);
            if (extra is not null) foreach (var (k, v) in extra) r.Headers.TryAddWithoutValidation(k, v);
            if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            else
            {
                var xms = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["x-ms-date"] = date, ["x-ms-version"] = AzureBlobTarget.ApiVersion };
                if (extra is not null) foreach (var (k, v) in extra) xms[k.ToLowerInvariant()] = v;
                var canonHeaders = string.Concat(xms.Select(kv => kv.Key + ":" + kv.Value + "\n"));
                var contentType = content is not null ? "application/octet-stream" : "";
                var sts = string.Join("\n", method.Method, "", "", length > 0 ? length.ToString(CultureInfo.InvariantCulture) : "", "", contentType, "", "", "", "", "", "")
                          + "\n" + canonHeaders + "/" + s.Account + path + canonicalQuery;
                var sig = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(s.AccountKey), Encoding.UTF8.GetBytes(sts)));
                r.Headers.TryAddWithoutValidation("Authorization", $"SharedKey {s.Account}:{sig}");
            }
            return r;
        }, ct);
    }

    private static string EscapePath(string absolutePath) => string.Join("/", absolutePath.TrimStart('/').Split('/').Select(Uri.EscapeDataString));
}
