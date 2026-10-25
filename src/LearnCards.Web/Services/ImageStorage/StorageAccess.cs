using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace LearnCards.Web.Services.ImageStorage;

/// <summary>
/// Kurzlebige, objektbezogene Leseberechtigung für genau ein Bild (ADR 0018). Die <see cref="Url"/> enthält ggf. ein SAS bzw. eine Presigned-Signatur und ist
/// ein <b>Geheimnis des Servers</b>: Sie wird nie persistiert, nie geloggt und nie an Clients ausgegeben.
/// </summary>
public sealed record StorageGrant(Uri Url, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string>? Headers = null)
{
    public bool IsExpired(TimeSpan margin) => DateTimeOffset.UtcNow + margin >= ExpiresAt;
    public override string ToString() => $"StorageGrant(host={Url.Host}, expires={ExpiresAt:O})";   // niemals die URL ausgeben
}

/// <summary>
/// Vertrauensgrenze zwischen Anwendung und Speicherzugriff (ADR 0018). Ein Broker liefert ausschließlich <b>lesende</b>, auf <b>ein Objekt</b>
/// beschränkte, <b>kurzlebige</b> Zugriffe. Die Anwendung entscheidet vorher über die Berechtigung des Benutzers (Karte/Modul/Freigabe/Global); der Broker kennt
/// keine Benutzer. Ein künftiger externer Handler (separater Dienst, der SAS ausstellt) implementiert diese Schnittstelle: Eingabe = Provider-Schlüssel (32 Hex),
/// Ausgabe = <see cref="StorageGrant"/>. Die Anwendung prüft jede zurückgelieferte URL erneut mit <see cref="StorageUrlGuard"/> (fester Endpunkt, fester Container,
/// genau dieses Objekt, nur Lese-Parameter) und fordert nie eine URL aus Daten der Datenbank an.
/// </summary>
public interface IStorageAccessBroker
{
    Task<StorageGrant> GetReadGrantAsync(string key, CancellationToken ct = default);

    /// <summary>Wird aufgerufen, wenn der Speicher ein Grant ablehnt (abgelaufen/widerrufen): zwischengespeicherte Schlüsselmaterialien verwerfen.</summary>
    void Invalidate();
}

/// <summary>Quelle für Entra-ID-Zugriffstoken (Managed Identity oder Client-Credentials). Token werden nur im Arbeitsspeicher gehalten.</summary>
public interface IAccessTokenSource
{
    Task<string> GetTokenAsync(string scope, CancellationToken ct = default);
}

public sealed class EntraTokenSource : IAccessTokenSource
{
    private readonly string _provider;
    private readonly string _tenant, _clientId, _secret, _authority;
    private readonly bool _managedIdentity;
    private readonly HttpClient _http;       // Entra-Endpunkt (öffentlich bzw. konfiguriert)
    private readonly HttpClient _imds;       // Metadatendienst (link-local, fest vorgegebenes Ziel)
    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EntraTokenSource(string provider, bool managedIdentity, string tenant, string clientId, string secret, string authority, bool allowPrivateNetwork, int timeoutSeconds)
    {
        _provider = provider;
        _managedIdentity = managedIdentity;
        _tenant = tenant; _clientId = clientId; _secret = secret; _authority = authority.TrimEnd('/');
        _http = SafeHttp.CreateClient(allowPrivateNetwork, timeoutSeconds);
        _imds = SafeHttp.CreateClient(true, Math.Min(timeoutSeconds, 10));
    }

    public async Task<string> GetTokenAsync(string scope, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(scope, out var c) && c.Expires > DateTimeOffset.UtcNow.AddMinutes(2)) return c.Token;
            var (token, expires) = _managedIdentity ? await FetchManagedIdentityAsync(scope, ct) : await FetchClientCredentialsAsync(scope, ct);
            _cache[scope] = (token, expires);
            return token;
        }
        finally { _gate.Release(); }
    }

    private async Task<(string, DateTimeOffset)> FetchClientCredentialsAsync(string scope, CancellationToken ct)
    {
        using var response = await SafeHttp.SendAsync(_http, _provider, () => new HttpRequestMessage(HttpMethod.Post, $"{_authority}/{Uri.EscapeDataString(_tenant)}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = _clientId, ["client_secret"] = _secret, ["scope"] = scope,
            }),
        }, ct);
        return await ParseAsync(response, ct);
    }

    private async Task<(string, DateTimeOffset)> FetchManagedIdentityAsync(string scope, CancellationToken ct)
    {
        var resource = scope.EndsWith("/.default", StringComparison.Ordinal) ? scope[..^"/.default".Length] : scope;
        var endpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");      // App Service / Container Apps / Functions
        var header = Environment.GetEnvironmentVariable("IDENTITY_HEADER");
        using var response = await SafeHttp.SendAsync(_imds, _provider, () =>
        {
            HttpRequestMessage r;
            if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(header))
            {
                var q = $"{endpoint}?resource={Uri.EscapeDataString(resource)}&api-version=2019-08-01" + (_clientId.Length > 0 ? $"&client_id={Uri.EscapeDataString(_clientId)}" : "");
                r = new HttpRequestMessage(HttpMethod.Get, q);
                r.Headers.TryAddWithoutValidation("X-IDENTITY-HEADER", header);
            }
            else
            {
                var q = $"http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource={Uri.EscapeDataString(resource)}" + (_clientId.Length > 0 ? $"&client_id={Uri.EscapeDataString(_clientId)}" : "");
                r = new HttpRequestMessage(HttpMethod.Get, q);
                r.Headers.TryAddWithoutValidation("Metadata", "true");
            }
            return r;
        }, ct);
        return await ParseAsync(response, ct);
    }

    private async Task<(string, DateTimeOffset)> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new ImageStorageException(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? ImageStorageError.Unauthorized : ImageStorageError.Unavailable,
                _provider, $"Anmeldung bei Entra ID fehlgeschlagen (HTTP {(int)response.StatusCode}). Prüfe Tenant, Client und Berechtigungen.");
        var bytes = await SafeHttp.ReadCappedAsync(response, _provider, 64 * 1024, ct);
        var json = JsonNode.Parse(bytes)!;
        var token = json["access_token"]?.GetValue<string>() ?? throw new ImageStorageException(ImageStorageError.Unauthorized, _provider, "Entra ID lieferte kein Zugriffstoken.");
        DateTimeOffset expires;
        if (json["expires_in"] is { } ei && int.TryParse(ei.ToString(), out var secs)) expires = DateTimeOffset.UtcNow.AddSeconds(secs);
        else if (json["expires_on"] is { } eo && long.TryParse(eo.ToString(), out var epoch)) expires = DateTimeOffset.FromUnixTimeSeconds(epoch);
        else expires = DateTimeOffset.UtcNow.AddMinutes(30);
        return (token, expires);
    }

    public static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);
}
