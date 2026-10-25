using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace LearnCards.Web.Services.ImageStorage;

/// <summary>
/// Sichere ausgehende HTTP-Zugriffe auf Bildspeicher (ADR 0018): keine Redirects, kein Proxy-Umweg, Verbindungsziel wird <b>nach</b> der DNS-Auflösung
/// geprüft und dann direkt auf die geprüfte Adresse verbunden (schützt vor DNS-Rebinding). Öffentliche Cloud-Endpunkte lösen auf öffentliche Adressen auf;
/// interne Ziele (Private Endpoints, lokale Testdienste) müssen ausdrücklich per <c>…_ALLOW_PRIVATE_NETWORK=true</c> freigegeben werden.
/// </summary>
public static class SafeHttp
{
    /// <summary>Wahr für öffentlich routbare Adressen; falsch für Loopback, Link-Local (inkl. Cloud-Metadatendienst 169.254.169.254), private, CGNAT, Multicast, nicht spezifizierte Adressen.</summary>
    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)                // Link-Local inkl. Metadatendienst
                || (b[0] == 100 && b[1] is >= 64 and <= 127)   // CGNAT
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)     // IETF-Protokollzuweisungen
                || b[0] >= 224);                               // Multicast/reserviert
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC);   // fe80::/10, fec0::/10, ff00::/8, fc00::/7
        }
        return false;
    }

    /// <summary>Erzeugt einen HttpClient für Speicherzugriffe: ohne Redirects, ohne Proxy, mit Verbindungsprüfung und Zeitlimit.</summary>
    public static HttpClient CreateClient(bool allowPrivateNetwork, int timeoutSeconds)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,               // nie ungeprüft Weiterleitungen folgen
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(timeoutSeconds, 15)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),   // DNS-Änderungen werden regelmäßig neu aufgelöst und geprüft
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = IPAddress.TryParse(host, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(host, ct);
                var allowed = allowPrivateNetwork ? addresses : addresses.Where(IsPublicAddress).ToArray();
                if (allowed.Length == 0)
                    throw new HttpRequestException($"Verbindung zu '{host}' blockiert: die Adresse liegt nicht im öffentlichen Netz (IMAGE_*_ALLOW_PRIVATE_NETWORK nicht gesetzt).");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(allowed[0], context.DnsEndPoint.Port), ct);   // direkt auf die geprüfte Adresse
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }

    /// <summary>Liest den Antwortkörper höchstens bis <paramref name="maxBytes"/> (Größenbegrenzung gegen übergroße oder bösartige Antworten).</summary>
    public static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, string provider, long maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            throw new ImageStorageException(ImageStorageError.Invalid, provider, "Das Bild im Speicher überschreitet die zulässige Größe.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > maxBytes)
                throw new ImageStorageException(ImageStorageError.Invalid, provider, "Das Bild im Speicher überschreitet die zulässige Größe.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Führt eine Anfrage aus und bildet Netzwerkfehler auf <see cref="ImageStorageException"/> ab. Die Meldungen enthalten nie URLs, Header oder Token;
    /// bei 429/503 erfolgt höchstens eine Wiederholung (Retry-After bis 5 s).
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, string provider, Func<HttpRequestMessage> build, CancellationToken ct, bool retryThrottled = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = build();
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (retryThrottled && attempt == 0 && response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                    response.Dispose();
                    await Task.Delay(wait > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : wait, ct);
                    continue;
                }
                return response;
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                throw new ImageStorageException(ImageStorageError.Timeout, provider, "Zeitüberschreitung beim Bildspeicher.", e);
            }
            catch (HttpRequestException e)
            {
                throw new ImageStorageException(ImageStorageError.Unavailable, provider, SecretRedactor.Redact("Bildspeicher nicht erreichbar: " + e.Message), e);
            }
        }
    }

    /// <summary>Bildet einen Fehlerstatus (nach Prüfung auf 404/304) auf eine einheitliche Ausnahme ab; liest nie den Antworttext in die Meldung.</summary>
    public static ImageStorageException StatusError(string provider, HttpStatusCode status, string operation) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new ImageStorageException(ImageStorageError.Unauthorized, provider, $"{operation}: Zugriff vom Bildspeicher abgelehnt (HTTP {(int)status})."),
        HttpStatusCode.NotFound => new ImageStorageException(ImageStorageError.NotFound, provider, $"{operation}: nicht gefunden."),
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => new ImageStorageException(ImageStorageError.Timeout, provider, $"{operation}: Zeitüberschreitung beim Bildspeicher."),
        _ => new ImageStorageException(ImageStorageError.Unavailable, provider, $"{operation}: Bildspeicher antwortet mit HTTP {(int)status}."),
    };

    public static void AddIfNoneMatch(HttpRequestMessage request, string? etag)
    {
        if (!string.IsNullOrEmpty(etag)) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
    }
}

/// <summary>Entfernt Zugriffsberechtigungen aus Texten (Logs, Fehlermeldungen): SAS-Signaturen, SigV4-Signaturen, Bearer-/SharedKey-Header, Schlüssel.</summary>
public static partial class SecretRedactor
{
    [GeneratedRegex(@"(?i)(sig|signature|x-amz-signature|x-amz-credential|x-amz-security-token|skoid|sktid|skt|ske|skv|access_token|client_secret|accountkey)=[^&\s""']+")]
    private static partial Regex QueryPattern();

    [GeneratedRegex(@"(?i)(Bearer|SharedKey|AWS4-HMAC-SHA256)\s+[^\s""',;]+")]
    private static partial Regex HeaderPattern();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = QueryPattern().Replace(text, m => m.Groups[1].Value + "=***");
        return HeaderPattern().Replace(s, m => m.Groups[1].Value + " ***");
    }
}

/// <summary>
/// Prüft jede URL, die ein <see cref="IStorageAccessBroker"/> liefert, bevor sie als HttpClient-Ziel dient (ADR 0018): Es sind <b>nur</b> Aufrufe an den konfigurierten
/// Endpunkt, den konfigurierten Container/Bucket und genau das angefragte Objekt erlaubt — nie ein beliebiger Host oder Pfad, der aus Daten stammt.
/// </summary>
public static class StorageUrlGuard
{
    public static void Validate(string provider, Uri url, Uri expectedBase, string expectedPath, bool allowHttp, IReadOnlySet<string> allowedQueryKeys,
        IReadOnlyDictionary<string, string>? requiredQueryValues = null)
    {
        void Fail(string why) => throw new ImageStorageException(ImageStorageError.Invalid, provider, "Unerlaubte Speicher-URL: " + why);

        if (!url.IsAbsoluteUri) Fail("nicht absolut");
        if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && allowHttp)) Fail("Schema nicht erlaubt");
        if (!string.Equals(url.Scheme, expectedBase.Scheme, StringComparison.Ordinal)) Fail("Schema weicht vom konfigurierten Endpunkt ab");
        if (!string.Equals(url.IdnHost, expectedBase.IdnHost, StringComparison.OrdinalIgnoreCase) || url.Port != expectedBase.Port) Fail("Host/Port weichen vom konfigurierten Endpunkt ab");
        if (!string.IsNullOrEmpty(url.UserInfo)) Fail("Zugangsdaten in der URL");
        if (!string.IsNullOrEmpty(url.Fragment)) Fail("Fragment");
        if (!string.Equals(url.AbsolutePath, expectedPath, StringComparison.Ordinal)) Fail("Pfad entspricht nicht dem angefragten Objekt");

        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        foreach (var key in query.AllKeys)
            if (key is null || !allowedQueryKeys.Contains(key)) Fail("unerwarteter Query-Parameter");
        if (requiredQueryValues is not null)
            foreach (var (k, v) in requiredQueryValues)
                if (query[k] != v) Fail($"Parameter {k} nicht wie erwartet");
    }
}
