using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services.ImageStorage;

/// <summary>
/// Bildspeicher in SharePoint/OneDrive-Dokumentbibliotheken über Microsoft Graph (ADR 0017/0018). <b>App-only</b>: ein eigener Server-Token (Client-Credentials) —
/// die Anmelde-Tokens der Benutzer werden nie für Graph verwendet; Benutzerrechte werden ausschließlich von LearnCards geprüft. Dateien liegen unter
/// <c>{Folder}/{key[0..2]}/{key}.img</c> (nur serverseitige Schlüssel). Die kurzlebige <c>@microsoft.graph.downloadUrl</c> wird nur im Arbeitsspeicher für den
/// einen Abruf verwendet (nie gespeichert, nie geloggt, nie ausgegeben) und streng geprüft (https, SharePoint-Host, kein Redirect). Mit dem ETag aus den
/// Metadaten wird ein zwischengespeichertes Bild ohne erneuten Download revalidiert.
/// </summary>
public sealed class SharePointImageStorageProvider : IImageStorageProvider
{
    private static readonly string[] AllowedDownloadHostSuffixes = { ".sharepoint.com", ".sharepoint.us", ".sharepoint.de", ".sharepoint.cn", ".files.1drv.com" };

    private readonly SharePointSettings _s;
    private readonly HttpClient _http;
    private readonly IAccessTokenSource _tokens;
    private readonly Uri _graph;

    public SharePointImageStorageProvider(SharePointSettings s, int timeoutSeconds)
    {
        _s = s;
        _graph = new Uri(s.GraphBaseUrl.TrimEnd('/') + "/");
        _http = SafeHttp.CreateClient(s.AllowPrivateNetwork || s.AllowHttp, timeoutSeconds);
        _tokens = new EntraTokenSource("sharepoint", managedIdentity: false, s.TenantId, s.ClientId, s.ClientSecret, s.Authority, s.AllowPrivateNetwork || s.AllowHttp, timeoutSeconds);
    }

    public string Name => "sharepoint";
    public bool IsRemote => true;

    private string ItemPath(string key)
    {
        ImageStorageKey.Require(Name, key);
        var path = (_s.Folder.Length > 0 ? _s.Folder + "/" : "") + key[..2] + "/" + key + ".img";
        var escaped = string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return $"sites/{Uri.EscapeDataString(_s.SiteId)}/drives/{Uri.EscapeDataString(_s.DriveId)}/root:/{escaped}";
    }

    private async Task<HttpResponseMessage> GraphAsync(HttpMethod method, string relative, Func<HttpContent>? content, CancellationToken ct)
    {
        var token = await _tokens.GetTokenAsync("https://graph.microsoft.com/.default", ct);
        return await SafeHttp.SendAsync(_http, Name, () =>
        {
            var r = new HttpRequestMessage(method, new Uri(_graph, relative));
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (content is not null) r.Content = content();
            return r;
        }, ct);
    }

    public async Task<string?> PutAsync(string key, byte[] data, string mimeType, CancellationToken ct = default)
    {
        using var response = await GraphAsync(HttpMethod.Put, ItemPath(key) + ":/content", () =>
        {
            var c = new ByteArrayContent(data);
            c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return c;
        }, ct);
        if (!response.IsSuccessStatusCode) throw SafeHttp.StatusError(Name, response.StatusCode, "Bild speichern");
        var json = JsonNode.Parse(await SafeHttp.ReadCappedAsync(response, Name, 256 * 1024, ct));
        return json?["eTag"]?.GetValue<string>();
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        using var response = await GraphAsync(HttpMethod.Get, ItemPath(key) + "?$select=id", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode) throw SafeHttp.StatusError(Name, response.StatusCode, "Bild prüfen");
        return true;
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        using var response = await GraphAsync(HttpMethod.Delete, ItemPath(key), null, ct);
        if (response.StatusCode != HttpStatusCode.NotFound && !response.IsSuccessStatusCode) throw SafeHttp.StatusError(Name, response.StatusCode, "Bild löschen");
    }

    public async Task<StoredImage?> GetAsync(string key, string? ifNoneMatch = null, CancellationToken ct = default)
    {
        // 1) Metadaten: ETag + kurzlebige Download-URL (ein Graph-Aufruf)
        JsonNode? meta;
        using (var response = await GraphAsync(HttpMethod.Get, ItemPath(key) + "?$select=id,eTag,size,@microsoft.graph.downloadUrl", null, ct))
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) throw SafeHttp.StatusError(Name, response.StatusCode, "Bild lesen");
            meta = JsonNode.Parse(await SafeHttp.ReadCappedAsync(response, Name, 256 * 1024, ct));
        }
        var etag = meta?["eTag"]?.GetValue<string>();
        if (ifNoneMatch is not null && etag is not null && string.Equals(etag, ifNoneMatch, StringComparison.Ordinal))
            return new StoredImage(Array.Empty<byte>(), etag, NotModified: true);      // kein Download nötig
        if (meta?["size"]?.GetValue<long>() is { } size && size > CardImageLimits.MaxDecodedBytes)
            throw new ImageStorageException(ImageStorageError.Invalid, Name, "Das Bild im Speicher überschreitet die zulässige Größe.");
        var downloadUrl = meta?["@microsoft.graph.downloadUrl"]?.GetValue<string>();
        if (string.IsNullOrEmpty(downloadUrl)) return null;

        // 2) Download über die vorab authentifizierte URL (ohne Authorization-Header, ohne Weiterleitungen)
        var url = ValidateDownloadUrl(downloadUrl);
        using var download = await SafeHttp.SendAsync(_http, Name, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (download.StatusCode == HttpStatusCode.NotFound) return null;
        if (!download.IsSuccessStatusCode) throw SafeHttp.StatusError(Name, download.StatusCode, "Bild herunterladen");
        return new StoredImage(await SafeHttp.ReadCappedAsync(download, Name, CardImageLimits.MaxDecodedBytes, ct), etag);
    }

    private Uri ValidateDownloadUrl(string value)
    {
        void Fail(string why) => throw new ImageStorageException(ImageStorageError.Invalid, Name, "Unerlaubte Download-URL von Graph: " + why);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url)) { Fail("ungültig"); return null!; }
        if (!string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment)) Fail("Zugangsdaten/Fragment");
        var sameAsGraph = _s.AllowHttp && string.Equals(url.Authority, _graph.Authority, StringComparison.OrdinalIgnoreCase);   // nur Testmock
        if (!sameAsGraph)
        {
            if (url.Scheme != Uri.UriSchemeHttps) Fail("kein https");
            var host = url.IdnHost.ToLowerInvariant();
            if (!AllowedDownloadHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal))) Fail("Host ist kein SharePoint-Speicher");
        }
        return url;
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
                ? new ProviderHealth(true, "Dokumentbibliothek beschreibbar", sw.Elapsed)
                : new ProviderHealth(false, "Lese-/Schreibprobe fehlgeschlagen.", sw.Elapsed);
        }
        catch (Exception e) { return new ProviderHealth(false, SecretRedactor.Redact(e.Message), sw.Elapsed); }
    }
}
