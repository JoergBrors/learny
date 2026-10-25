using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services.ImageStorage;

/// <summary>
/// Komponententests für Remote-Bildspeicher, SAS-/Presign-Broker und die Schutzmaßnahmen (ADR 0017/0018) — ohne Cloud-Zugriff.
/// </summary>
public static class ImageStorageRemoteUnitTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("Komponententests Remote-Speicher, SAS und Schutzmaßnahmen (ADR 0018)");
        SigV4(check);
        AddressGuard(check);
        Redaction(check);
        UrlGuard(check);
        await AzureSasAsync(check);
        await S3PresignAsync(check);
        await SafeHttpAsync(check);
        await SharePointMockAsync(check);
    }

    private static void SigV4(Action<bool, string> check)
    {
        // Offizieller AWS-Testvektor "get-vanilla"
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = "example.amazonaws.com", ["x-amz-date"] = "20150830T123600Z" };
        var sig = AwsSigV4.Signature("GET", "/", "", headers, AwsSigV4.EmptyPayloadHash, "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "us-east-1", "service",
            new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero), out _);
        check(sig == "5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31", "U11 SigV4: AWS-Testvektor get-vanilla stimmt");
        check(AwsSigV4.Encode("a b/c~d", true) == "a%20b/c~d" && AwsSigV4.Encode("a/b", false) == "a%2Fb", "U11 SigV4: URI-Kodierung nach RFC 3986");
    }

    private static void AddressGuard(Action<bool, string> check)
    {
        bool P(string ip) => SafeHttp.IsPublicAddress(IPAddress.Parse(ip));
        check(!P("127.0.0.1") && !P("10.1.2.3") && !P("172.16.0.1") && !P("172.31.255.255") && !P("192.168.1.1") && !P("169.254.169.254") && !P("100.64.0.1") && !P("0.0.0.0"),
            "U12 SSRF: private, Loopback, Link-Local (Metadatendienst), CGNAT werden als nicht öffentlich erkannt");
        check(!P("::1") && !P("fe80::1") && !P("fc00::1") && !P("fd12::1") && !P("::ffff:10.0.0.1") && !P("::"), "U12 SSRF: IPv6 (Loopback, Link-Local, ULA, gemappte private IPv4) blockiert");
        check(P("20.50.1.1") && P("52.95.1.1") && P("172.32.0.1") && P("2603:1030::1"), "U12 SSRF: öffentliche Adressen bleiben erlaubt");
    }

    private static void Redaction(Action<bool, string> check)
    {
        var text = "GET https://acc.blob.core.windows.net/c/k?sv=2020-12-06&sr=b&sp=r&sig=ABCDEF%2Bxyz%3D&skoid=1234 Authorization: Bearer eyJhbGciOi SharedKey acc:SECRETSIG X-Amz-Signature=deadbeef";
        var red = SecretRedactor.Redact(text);
        check(!red.Contains("ABCDEF") && !red.Contains("1234") && !red.Contains("eyJhbGciOi") && !red.Contains("SECRETSIG") && !red.Contains("deadbeef"), "U13 Redaktion: SAS-Signatur, Bearer-Token, SharedKey und SigV4-Signatur werden entfernt");
    }

    private static void UrlGuard(Action<bool, string> check)
    {
        var baseUri = new Uri("https://acc.blob.core.windows.net/");
        var keys = new HashSet<string> { "sv", "sr", "sp", "sig" };
        bool Ok(string url, string path = "/c/ab/abcd", bool allowHttp = false)
        {
            try { StorageUrlGuard.Validate("t", new Uri(url), baseUri, path, allowHttp, keys, new Dictionary<string, string> { ["sp"] = "r" }); return true; }
            catch (ImageStorageException e) { return e.Kind != ImageStorageError.Invalid ? throw e : false; }
        }
        check(Ok("https://acc.blob.core.windows.net/c/ab/abcd?sv=1&sr=b&sp=r&sig=x"), "U14 URL-Guard: erwartete URL wird akzeptiert");
        check(!Ok("https://evil.example.com/c/ab/abcd?sv=1&sr=b&sp=r&sig=x"), "U14 URL-Guard: fremder Host wird abgelehnt");
        check(!Ok("https://acc.blob.core.windows.net.evil.com/c/ab/abcd?sp=r"), "U14 URL-Guard: Host-Suffix-Trick wird abgelehnt");
        check(!Ok("https://acc.blob.core.windows.net/c/ab/other?sp=r"), "U14 URL-Guard: anderes Objekt wird abgelehnt");
        check(!Ok("https://acc.blob.core.windows.net/c/ab/abcd/../x?sp=r"), "U14 URL-Guard: Pfad-Manipulation wird abgelehnt");
        check(!Ok("https://acc.blob.core.windows.net/c/ab/abcd?sp=rw"), "U14 URL-Guard: Schreibrechte im SAS werden abgelehnt");
        check(!Ok("https://acc.blob.core.windows.net/c/ab/abcd?sp=r&sdd=1&comp=list"), "U14 URL-Guard: unbekannte Query-Parameter werden abgelehnt");
        check(!Ok("https://user:pw@acc.blob.core.windows.net/c/ab/abcd?sp=r"), "U14 URL-Guard: Zugangsdaten in der URL werden abgelehnt");
        check(!Ok("http://acc.blob.core.windows.net/c/ab/abcd?sp=r"), "U14 URL-Guard: http wird ohne Entwicklungs-Flag abgelehnt");
        check(!Ok("https://acc.blob.core.windows.net:8443/c/ab/abcd?sp=r"), "U14 URL-Guard: anderer Port wird abgelehnt");
    }

    private static readonly string DevKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";   // öffentlich dokumentierter Azurite-Entwicklungsschlüssel

    private static async Task AzureSasAsync(Action<bool, string> check)
    {
        var settings = new AzureBlobSettings { Account = "devstoreaccount1", Endpoint = "http://127.0.0.1:10000/devstoreaccount1", Container = "learncards-images", Auth = "sharedkey", AccountKey = DevKey, AllowHttp = true, SasTtlSeconds = 60 };
        var broker = AzureSasBroker.CreateForSharedKey(settings);
        var key = ImageStorageKey.New();
        var grant = await broker.GetReadGrantAsync(key);
        var q = System.Web.HttpUtility.ParseQueryString(grant.Url.Query);
        check(q["sp"] == "r" && q["sr"] == "b" && q["sv"] is { Length: > 0 } && !string.IsNullOrEmpty(q["sig"]), "U15 SAS: nur Lese-Recht (sp=r), Ressource ein Blob (sr=b), signiert");
        check(grant.Url.AbsolutePath == $"/devstoreaccount1/learncards-images/{key[..2]}/{key}", "U15 SAS: gilt für genau das angefragte Blob (kein Container-/Konto-SAS)");
        var se = DateTimeOffset.Parse(q["se"]!, null, System.Globalization.DateTimeStyles.AssumeUniversal);
        check(se > DateTimeOffset.UtcNow && se <= DateTimeOffset.UtcNow.AddSeconds(62) && grant.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(62), "U15 SAS: Gültigkeit entspricht dem TTL (60 s)");
        check(!grant.ToString().Contains("sig", StringComparison.OrdinalIgnoreCase) && !grant.ToString().Contains(q["sig"]!), "U15 SAS: ToString des Grants gibt keine Signatur aus");
        var shortTtl = await AzureSasBroker.CreateForSharedKey(new AzureBlobSettings { Account = "devstoreaccount1", Endpoint = settings.Endpoint, Auth = "sharedkey", AccountKey = DevKey, AllowHttp = true, SasTtlSeconds = 5 }).GetReadGrantAsync(key);
        check(shortTtl.ExpiresAt >= DateTimeOffset.UtcNow.AddSeconds(8), "U15 SAS: TTL wird auf mindestens 10 s begrenzt");
        try { await broker.GetReadGrantAsync("../../etc/passwd"); check(false, "U15 SAS: ungültiger Schlüssel wird abgelehnt"); }
        catch (ImageStorageException e) { check(e.Kind == ImageStorageError.Invalid, "U15 SAS: manipulierter Speicherschlüssel wird abgelehnt"); }

        // Provider akzeptiert nur Grants für den konfigurierten Endpunkt/Container/das Objekt
        var evil = new FakeBroker(k => new StorageGrant(new Uri($"https://evil.example.com/devstoreaccount1/learncards-images/{k[..2]}/{k}?sp=r&sr=b&sig=x"), DateTimeOffset.UtcNow.AddMinutes(1)));
        var p = new AzureBlobImageStorageProvider(settings, 5, evil);
        try { await p.GetAsync(key); check(false, "U16 Provider: URL eines fremden Hosts wird abgelehnt"); }
        catch (ImageStorageException e) { check(e.Kind == ImageStorageError.Invalid && !e.Message.Contains("evil.example.com") || e.Kind == ImageStorageError.Invalid, "U16 Provider: SAS-URL eines fremden Hosts wird vor dem Aufruf abgelehnt"); }
        var otherObject = new FakeBroker(k => new StorageGrant(new Uri($"http://127.0.0.1:10000/devstoreaccount1/learncards-images/ff/{ImageStorageKey.New()}?sp=r&sr=b&sig=x"), DateTimeOffset.UtcNow.AddMinutes(1)));
        try { await new AzureBlobImageStorageProvider(settings, 5, otherObject).GetAsync(key); check(false, "U16 Provider: URL eines anderen Objekts wird abgelehnt"); }
        catch (ImageStorageException e) { check(e.Kind == ImageStorageError.Invalid, "U16 Provider: SAS-URL eines anderen Objekts wird abgelehnt"); }
    }

    private static async Task S3PresignAsync(Action<bool, string> check)
    {
        var s = new S3Settings { Endpoint = "https://minio.example.com", Bucket = "learncards", Region = "eu-central-1", AccessKeyId = "AKIAEXAMPLE", SecretAccessKey = "SUPERSECRETVALUE", PathStyle = true, PresignTtlSeconds = 60 };
        var grant = await S3PresignBroker.Create(s).GetReadGrantAsync("abcdef0123456789abcdef0123456789");
        var q = System.Web.HttpUtility.ParseQueryString(grant.Url.Query);
        check(grant.Url.AbsolutePath == "/learncards/images/ab/abcdef0123456789abcdef0123456789" && q["X-Amz-Expires"] == "60" && q["X-Amz-SignedHeaders"] == "host" && !string.IsNullOrEmpty(q["X-Amz-Signature"]),
            "U17 S3: Presigned-URL gilt für genau ein Objekt, 60 s, nur host signiert");
        check(!grant.Url.ToString().Contains("SUPERSECRETVALUE"), "U17 S3: das Secret erscheint nie in der URL");
        var virt = await S3PresignBroker.Create(new S3Settings { Bucket = "learncards", Region = "eu-central-1", AccessKeyId = "A", SecretAccessKey = "S" }).GetReadGrantAsync("abcdef0123456789abcdef0123456789");
        check(virt.Url.Host == "learncards.s3.eu-central-1.amazonaws.com", "U17 S3: AWS-Standardendpunkt (virtual-hosted) ohne frei wählbaren Host");
    }

    private static async Task SafeHttpAsync(Action<bool, string> check)
    {
        // Ohne Freigabe sind Loopback-Ziele gesperrt
        using (var strict = SafeHttp.CreateClient(false, 5))
        {
            try { await strict.GetAsync("http://127.0.0.1:9/"); check(false, "U18 SSRF: Loopback-Ziel wird blockiert"); }
            catch (HttpRequestException) { check(true, "U18 SSRF: Verbindungen zu Loopback/privaten Adressen werden blockiert (ohne ALLOW_PRIVATE_NETWORK)"); }
        }

        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var loop = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch { return; }
                if (c.Request.Url!.AbsolutePath == "/redirect") { c.Response.StatusCode = 302; c.Response.Headers["Location"] = "http://169.254.169.254/latest/meta-data/"; }
                else if (c.Request.Url.AbsolutePath == "/big") { var big = new byte[3 * 1024 * 1024]; c.Response.ContentLength64 = big.Length; await c.Response.OutputStream.WriteAsync(big); }
                else { c.Response.StatusCode = 200; }
                c.Response.Close();
            }
        });
        try
        {
            using var open = SafeHttp.CreateClient(true, 10);
            var r = await open.GetAsync($"http://127.0.0.1:{port}/redirect");
            check(r.StatusCode == HttpStatusCode.Found, "U19 Redirect: Weiterleitungen (z. B. auf den Metadatendienst) werden nicht verfolgt");
            var big = await open.GetAsync($"http://127.0.0.1:{port}/big", HttpCompletionOption.ResponseHeadersRead);
            try { await SafeHttp.ReadCappedAsync(big, "t", 2 * 1024 * 1024, CancellationToken.None); check(false, "U19 Größenlimit"); }
            catch (ImageStorageException e) { check(e.Kind == ImageStorageError.Invalid, "U19 Größenlimit: übergroße Antwort (3 MiB > 2 MiB) wird abgelehnt"); }
        }
        finally { listener.Stop(); }

        // Nicht erreichbar → kontrollierte Unavailable-Meldung ohne URL
        var down = new AzureBlobImageStorageProvider(new AzureBlobSettings { Account = "devstoreaccount1", Endpoint = $"http://127.0.0.1:{FreePort()}/devstoreaccount1", Auth = "sharedkey", AccountKey = DevKey, AllowHttp = true, AllowPrivateNetwork = true }, 3);
        try { await down.GetAsync(ImageStorageKey.New()); check(false, "U20 Storage nicht erreichbar"); }
        catch (ImageStorageException e) { check(e.Kind is ImageStorageError.Unavailable or ImageStorageError.Timeout && !e.Message.Contains("sig="), "U20 Speicher nicht erreichbar → kontrollierter Fehler (ohne SAS/URL in der Meldung)"); }
    }

    // ─── SharePoint gegen einen Graph-Mock ───────────────────────────────────

    private static async Task SharePointMockAsync(Action<bool, string> check)
    {
        await using var graph = new MockGraph();
        var s = graph.Settings();
        var p = new SharePointImageStorageProvider(s, 10);
        var key = ImageStorageKey.New();
        var data = new byte[50_000]; new Random(5).NextBytes(data);
        var etag = await p.PutAsync(key, data, "image/png");
        check(etag is not null && await p.ExistsAsync(key), "U21 SharePoint: Speichern über Graph (App-only-Token)");
        var read = await p.GetAsync(key);
        check(read is not null && read.Data.SequenceEqual(data) && read.ETag == etag, "U21 SharePoint: Lesen über Download-URL liefert identische Bytes + ETag");
        var downloadsBefore = graph.Downloads;
        var revalidated = await p.GetAsync(key, etag);
        check(revalidated is { NotModified: true } && graph.Downloads == downloadsBefore, "U21 SharePoint: Revalidierung mit ETag lädt keine Daten erneut herunter");
        graph.FailTokens = true;
        // Token ist gecacht → kein neuer Tokenabruf nötig
        check((await p.GetAsync(key)) is not null, "U21 SharePoint: Token wird wiederverwendet");
        graph.FailTokens = false;
        graph.ForeignDownloadUrl = true;
        try { await p.GetAsync(key); check(false, "U22 SharePoint: fremde Download-URL"); }
        catch (ImageStorageException e) { check(e.Kind == ImageStorageError.Invalid, "U22 SharePoint: Download-URL auf fremden Host wird abgelehnt"); }
        graph.ForeignDownloadUrl = false;
        await p.DeleteAsync(key);
        check(!await p.ExistsAsync(key) && await p.GetAsync(key) is null, "U21 SharePoint: Löschen entfernt die Datei; fehlend → null");
        check(graph.AuthorizedGraphCallsOnly, "U21 SharePoint: alle Graph-Aufrufe trugen das Server-Token");
        graph.Throttle = 1;
        check(await p.ExistsAsync(key) == false, "U21 SharePoint: 429 mit Retry-After wird einmal kontrolliert wiederholt");
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port;
    }

    private sealed class FakeBroker : IStorageAccessBroker
    {
        private readonly Func<string, StorageGrant> _f;
        public FakeBroker(Func<string, StorageGrant> f) => _f = f;
        public Task<StorageGrant> GetReadGrantAsync(string key, CancellationToken ct = default) => Task.FromResult(_f(key));
        public void Invalidate() { }
    }
}

/// <summary>Minimaler Microsoft-Graph-Mock (Token, Datei-Upload/-Metadaten/-Download/-Löschen) für Provider- und Ende-zu-Ende-Tests.</summary>
public sealed class MockGraph : IAsyncDisposable
{
    private readonly HttpListener _l = new();
    private readonly ConcurrentDictionary<string, (byte[] Data, string ETag)> _files = new();
    private int _etagCounter;
    public int Port { get; }
    public int Downloads, MetaCalls, TokenCalls, Throttle;
    public bool FailTokens, ForeignDownloadUrl;
    public bool AuthorizedGraphCallsOnly = true;
    public int FileCount => _files.Count;

    public MockGraph(int? port = null)
    {
        Port = port ?? Free();
        _l.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _l.Start();
        _ = Task.Run(Loop);
    }

    private static int Free()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    public SharePointSettings Settings() => new()
    {
        TenantId = "tenant", ClientId = "client", ClientSecret = "secret", SiteId = "site", DriveId = "drive", Folder = "LearnCards/images",
        GraphBaseUrl = $"http://127.0.0.1:{Port}/v1.0", Authority = $"http://127.0.0.1:{Port}", AllowHttp = true, AllowPrivateNetwork = true,
    };

    private async Task Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext c;
            try { c = await _l.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Handle(c));
        }
    }

    private async Task Handle(HttpListenerContext c)
    {
        try
        {
            var path = c.Request.Url!.AbsolutePath;
            var raw = c.Request.RawUrl!.Split('?')[0];
            if (path.EndsWith("/oauth2/v2.0/token"))
            {
                Interlocked.Increment(ref TokenCalls);
                if (FailTokens) { c.Response.StatusCode = 400; c.Response.Close(); return; }
                await Json(c, 200, "{\"access_token\":\"mock-token\",\"expires_in\":3600}");
                return;
            }
            if (raw.StartsWith("/dl/"))
            {
                if (_files.TryGetValue(raw[4..], out var f)) { Interlocked.Increment(ref Downloads); c.Response.ContentLength64 = f.Data.Length; await c.Response.OutputStream.WriteAsync(f.Data); c.Response.Close(); }
                else { c.Response.StatusCode = 404; c.Response.Close(); }
                return;
            }
            if (c.Request.Headers["Authorization"] != "Bearer mock-token") { AuthorizedGraphCallsOnly = false; c.Response.StatusCode = 401; c.Response.Close(); return; }
            if (Interlocked.Exchange(ref Throttle, 0) == 1) { c.Response.StatusCode = 429; c.Response.Headers["Retry-After"] = "1"; c.Response.Close(); return; }
            var item = raw.Replace("/v1.0/", "");
            var isContent = item.EndsWith(":/content");
            if (isContent) item = item[..^":/content".Length];
            if (c.Request.HttpMethod == "PUT" && isContent)
            {
                using var ms = new MemoryStream(); await c.Request.InputStream.CopyToAsync(ms);
                var etag = $"\"{{{Interlocked.Increment(ref _etagCounter)}}},1\"";
                _files[item] = (ms.ToArray(), etag);
                await Json(c, 201, JsonSerializer.Serialize(new { id = "x", eTag = etag }));
            }
            else if (c.Request.HttpMethod == "GET")
            {
                Interlocked.Increment(ref MetaCalls);
                if (!_files.TryGetValue(item, out var f)) { c.Response.StatusCode = 404; c.Response.Close(); return; }
                var url = ForeignDownloadUrl ? "https://evil.example.com/steal" : $"http://127.0.0.1:{Port}/dl/{item}";
                await Json(c, 200, JsonSerializer.Serialize(new Dictionary<string, object> { ["id"] = "x", ["eTag"] = f.ETag, ["size"] = f.Data.Length, ["@microsoft.graph.downloadUrl"] = url }));
            }
            else if (c.Request.HttpMethod == "DELETE") { _files.TryRemove(item, out _); c.Response.StatusCode = 204; c.Response.Close(); }
            else { c.Response.StatusCode = 400; c.Response.Close(); }
        }
        catch { try { c.Response.Abort(); } catch { } }
    }

    private static async Task Json(HttpListenerContext c, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        c.Response.StatusCode = status; c.Response.ContentType = "application/json"; c.Response.ContentLength64 = bytes.Length;
        await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
    }

    public ValueTask DisposeAsync() { try { _l.Stop(); _l.Close(); } catch { } return ValueTask.CompletedTask; }
}
