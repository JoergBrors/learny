using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services.ImageStorage;

/// <summary>AWS Signature Version 4 (ohne SDK). Wird für Header-Signatur (Schreiben/Löschen/Prüfen) und Presigned-URLs (Lesen) verwendet.</summary>
public static class AwsSigV4
{
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    public static string Sha256Hex(byte[] data) => Hex(SHA256.HashData(data));
    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    public static string AmzDate(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>RFC-3986-Kodierung wie von SigV4 verlangt (Pfad: '/' bleibt erhalten).</summary>
    public static string Encode(string value, bool keepSlash)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~' || (keepSlash && c == '/')) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static byte[] SigningKey(string secret, string date, string region, string service) =>
        Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secret), date), region), service), "aws4_request");

    /// <summary>Berechnet die Signatur für eine kanonische Anfrage. <paramref name="headers"/> sind die signierten Header (Name klein, Wert getrimmt).</summary>
    public static string Signature(string method, string canonicalUri, string canonicalQuery, SortedDictionary<string, string> headers, string payloadHash,
        string secret, string region, string service, DateTimeOffset time, out string signedHeaders)
    {
        signedHeaders = string.Join(";", headers.Keys);
        var canonicalRequest = string.Join("\n", method, canonicalUri, canonicalQuery,
            string.Concat(headers.Select(h => h.Key + ":" + h.Value + "\n")), signedHeaders, payloadHash);
        var date = time.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var scope = $"{date}/{region}/{service}/aws4_request";
        var stringToSign = string.Join("\n", "AWS4-HMAC-SHA256", AmzDate(time), scope, Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));
        return Hex(HMACSHA256.HashData(SigningKey(secret, date, region, service), Encoding.UTF8.GetBytes(stringToSign)));
    }

    /// <summary>Authorization-Header für eine Header-signierte Anfrage.</summary>
    public static string AuthorizationHeader(string method, string canonicalUri, string canonicalQuery, SortedDictionary<string, string> headers, string payloadHash,
        string accessKey, string secret, string region, string service, DateTimeOffset time)
    {
        var sig = Signature(method, canonicalUri, canonicalQuery, headers, payloadHash, secret, region, service, time, out var signedHeaders);
        var scope = $"{time.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}/{region}/{service}/aws4_request";
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={sig}";
    }
}

/// <summary>Presigned-GET (Query-Signatur) für genau ein Objekt, kurzlebig, nur Header <c>host</c> signiert.</summary>
public sealed class S3PresignBroker : IStorageAccessBroker
{
    private readonly S3Target _t;
    internal S3PresignBroker(S3Target t) => _t = t;
    public static S3PresignBroker Create(S3Settings s) => new(new S3Target(s));
    public void Invalidate() { }

    public Task<StorageGrant> GetReadGrantAsync(string key, CancellationToken ct = default)
    {
        ImageStorageKey.Require("s3", key);
        var s = _t.Settings;
        var now = DateTimeOffset.UtcNow;
        var ttl = Math.Clamp(s.PresignTtlSeconds, 10, 600);
        var (uri, canonicalUri) = _t.ObjectUri(key);
        var date = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var q = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = $"{s.AccessKeyId}/{date}/{s.Region}/s3/aws4_request",
            ["X-Amz-Date"] = AwsSigV4.AmzDate(now),
            ["X-Amz-Expires"] = ttl.ToString(CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"] = "host",
        };
        if (s.SessionToken.Length > 0) q["X-Amz-Security-Token"] = s.SessionToken;
        var canonicalQuery = string.Join("&", q.Select(kv => AwsSigV4.Encode(kv.Key, false) + "=" + AwsSigV4.Encode(kv.Value, false)));
        var sig = AwsSigV4.Signature("GET", canonicalUri, canonicalQuery, new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = _t.HostHeader },
            "UNSIGNED-PAYLOAD", s.SecretAccessKey, s.Region, "s3", now, out _);
        return Task.FromResult(new StorageGrant(new Uri(uri.GetLeftPart(UriPartial.Path) + "?" + canonicalQuery + "&X-Amz-Signature=" + sig), now.AddSeconds(ttl)));
    }
}

internal sealed class S3Target
{
    public S3Settings Settings { get; }
    public Uri Base { get; }                // Schema + Host(+Port) für Anfragen
    public string HostHeader { get; }

    public S3Target(S3Settings s)
    {
        Settings = s;
        if (s.Endpoint.Length == 0)
        {
            Base = new Uri($"https://{s.Bucket}.s3.{s.Region}.amazonaws.com");
        }
        else
        {
            var e = new Uri(s.Endpoint);
            Base = s.PathStyle ? e : new Uri($"{e.Scheme}://{s.Bucket}.{e.Authority}");
        }
        HostHeader = Base.IsDefaultPort ? Base.IdnHost : $"{Base.IdnHost}:{Base.Port}";
    }

    private bool UsesPathStyle => Settings.Endpoint.Length > 0 && Settings.PathStyle;

    /// <summary>Objektpfad (nicht escaped): [/{bucket}]/images/xx/key.</summary>
    public string ObjectPath(string key) => (UsesPathStyle ? Base.AbsolutePath.TrimEnd('/') + "/" + Settings.Bucket : "") + "/images/" + key[..2] + "/" + key;
    public string BucketPath => UsesPathStyle ? Base.AbsolutePath.TrimEnd('/') + "/" + Settings.Bucket : "/";

    public (Uri Uri, string CanonicalUri) ObjectUri(string key)
    {
        var canonical = AwsSigV4.Encode(ObjectPath(key), keepSlash: true);
        return (new Uri(Base.GetLeftPart(UriPartial.Authority) + canonical), canonical);
    }
}

/// <summary>
/// S3-kompatibler Speicher (AWS S3, MinIO, …) über REST + SigV4 ohne SDK. Der Endpunkt kommt aus der Konfiguration (https; http nur mit ausdrücklichem Flag);
/// Anfrageziele sind fest (Bucket + serverseitiger Schlüssel), keine Hosts aus Daten. Lesen erfolgt über eine kurzlebige Presigned-URL des Brokers.
/// </summary>
public sealed class S3ImageStorageProvider : IImageStorageProvider
{
    private static readonly IReadOnlySet<string> PresignKeys = new HashSet<string>(StringComparer.Ordinal)
        { "X-Amz-Algorithm", "X-Amz-Credential", "X-Amz-Date", "X-Amz-Expires", "X-Amz-SignedHeaders", "X-Amz-Security-Token", "X-Amz-Signature" };

    private readonly S3Target _t;
    private readonly HttpClient _http;
    private readonly IStorageAccessBroker _broker;

    public S3ImageStorageProvider(S3Settings s, int timeoutSeconds, IStorageAccessBroker? brokerOverride = null)
    {
        _t = new S3Target(s);
        _http = SafeHttp.CreateClient(s.AllowPrivateNetwork, timeoutSeconds);
        _broker = brokerOverride ?? new S3PresignBroker(_t);
    }

    public string Name => "s3";
    public bool IsRemote => true;

    public async Task<string?> PutAsync(string key, byte[] data, string mimeType, CancellationToken ct = default)
    {
        ImageStorageKey.Require(Name, key);
        using var response = await SendSignedAsync(HttpMethod.Put, key, data, ct);
        if (!response.IsSuccessStatusCode) throw SafeHttp.StatusError(Name, response.StatusCode, "Bild speichern");
        return response.Headers.ETag?.Tag;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        ImageStorageKey.Require(Name, key);
        using var response = await SendSignedAsync(HttpMethod.Head, key, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode) throw SafeHttp.StatusError(Name, response.StatusCode, "Bild prüfen");
        return true;
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ImageStorageKey.Require(Name, key);
        using var response = await SendSignedAsync(HttpMethod.Delete, key, null, ct);
        if (response.StatusCode != HttpStatusCode.NotFound && !response.IsSuccessStatusCode) throw SafeHttp.StatusError(Name, response.StatusCode, "Bild löschen");
    }

    public async Task<StoredImage?> GetAsync(string key, string? ifNoneMatch = null, CancellationToken ct = default)
    {
        ImageStorageKey.Require(Name, key);
        for (var attempt = 0; ; attempt++)
        {
            var grant = await _broker.GetReadGrantAsync(key, ct);
            StorageUrlGuard.Validate(Name, grant.Url, _t.Base, _t.ObjectUri(key).CanonicalUri, _t.Settings.AllowHttp, PresignKeys);
            using var response = await SafeHttp.SendAsync(_http, Name, () =>
            {
                var r = new HttpRequestMessage(HttpMethod.Get, grant.Url);
                SafeHttp.AddIfNoneMatch(r, ifNoneMatch);
                return r;
            }, ct);
            if (response.StatusCode == HttpStatusCode.NotModified) return new StoredImage(Array.Empty<byte>(), ifNoneMatch, NotModified: true);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized && attempt == 0) { _broker.Invalidate(); continue; }
            if (!response.IsSuccessStatusCode) throw SafeHttp.StatusError(Name, response.StatusCode, "Bild lesen");
            return new StoredImage(await SafeHttp.ReadCappedAsync(response, Name, CardImageLimits.MaxDecodedBytes, ct), response.Headers.ETag?.Tag);
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
            var read = await GetAsync(key, null, ct);
            await DeleteAsync(key, ct);
            return read is not null && read.Data.SequenceEqual(probe)
                ? new ProviderHealth(true, $"Bucket '{_t.Settings.Bucket}' beschreibbar, Presigned-Lesen funktioniert", sw.Elapsed)
                : new ProviderHealth(false, "Lese-/Schreibprobe fehlgeschlagen.", sw.Elapsed);
        }
        catch (Exception e) { return new ProviderHealth(false, SecretRedactor.Redact(e.Message), sw.Elapsed); }
    }

    private Task<HttpResponseMessage> SendSignedAsync(HttpMethod method, string key, byte[]? body, CancellationToken ct)
    {
        var s = _t.Settings;
        var (uri, canonicalUri) = _t.ObjectUri(key);
        var payloadHash = body is null ? AwsSigV4.EmptyPayloadHash : AwsSigV4.Sha256Hex(body);
        return SafeHttp.SendAsync(_http, Name, () =>
        {
            var now = DateTimeOffset.UtcNow;
            var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["host"] = _t.HostHeader, ["x-amz-content-sha256"] = payloadHash, ["x-amz-date"] = AwsSigV4.AmzDate(now),
            };
            if (s.SessionToken.Length > 0) headers["x-amz-security-token"] = s.SessionToken;
            var r = new HttpRequestMessage(method, uri);
            if (body is not null) r.Content = new ByteArrayContent(body);
            foreach (var (k, v) in headers.Where(h => h.Key != "host")) r.Headers.TryAddWithoutValidation(k, v);
            r.Headers.TryAddWithoutValidation("Authorization",
                AwsSigV4.AuthorizationHeader(method.Method, canonicalUri, "", headers, payloadHash, s.AccessKeyId, s.SecretAccessKey, s.Region, "s3", now));
            return r;
        }, ct);
    }
}
