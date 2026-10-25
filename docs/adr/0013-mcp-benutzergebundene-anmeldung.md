# 0013 — MCP: benutzergebundene Anmeldung per OAuth und persönliche Tokens

- **Status:** Akzeptiert
- **Datum:** 2026-10-24
- **Ergänzt durch:** [0015](0015-freigaben-globale-lernmodule.md) (`list_modules` mit `scope`, lesender Zugriff auf freigegebene/globale Module; Freigaben/Veröffentlichung bleiben für Tokens gesperrt)
- **Ersetzt teilweise:** [0011](0011-mehrbenutzerarchitektur.md) (Abschnitt „API und MCP-Schlüssel“: `MCP_LINKED_USER_ID` entfällt), [0007](0007-mcp-server.md) (Authentifizierung des MCP-Servers)

## Kontext

Jeder Benutzer soll sich über den MCP-Server mit **seinem eigenen** Konto anmelden und nur **seinen** Bereich sehen. Zuvor arbeitete der MCP-Server mit einem
gemeinsamen Dienstschlüssel (`X-MCP-Key`); sein OAuth-Teil stellte Tokens aus, die keinem Benutzer zugeordnet waren, und die Zustimmungsseite konnte ohne
Anmeldung bestätigt werden. Ein gemeinsamer Schlüssel mit einem fest verknüpften Benutzer (`MCP_LINKED_USER_ID`, Übergangslösung aus ADR 0011) ist
für mehrere Benutzer ungeeignet.

## Entscheidung

### Learny ist die Identitätsquelle, der MCP-Server hält keinen Benutzerzustand
- Ein **MCP-Token** (`lcm_…`, 256 Bit zufällig) identifiziert genau einen Learny-Benutzer. Gespeichert wird nur sein SHA-256-Hash (`mcp_links`), samt Name,
  Art (`oauth` | `token`), Ablauf, letzter Nutzung und Widerruf. Die API prüft bei **jedem** Aufruf: Token bekannt, nicht widerrufen/abgelaufen, Benutzer aktiv.
- Die API akzeptiert `Authorization: Bearer lcm_…` und handelt dann **ausschließlich als dieser Benutzer** (alle Besitzregeln aus ADR 0011 gelten).
  Erlaubt sind nur Daten-Routen: `/api/modules`, `/api/cards`, `/api/import`, `/api/schema`, `/api/mcp/whoami`. Profil, Quiz-/Ergebnis-API, Administration und
  die Verwaltung weiterer MCP-Zugänge sind für Tokens gesperrt (403).
- Der gemeinsame **Dienstschlüssel `X-MCP-Key` trägt keinen Benutzer mehr** und gewährt keinen Datenzugriff. Er dient nur dem MCP-Server für den Code-Tausch
  (`POST /api/mcp/link/exchange`) und das öffentliche Schema. `MCP_LINKED_USER_ID` ist entfernt.

### OAuth-Ablauf (HTTP-Transport, z. B. VS Code, MCP Inspector, Claude)
1. Der Client registriert sich dynamisch (`/oauth/register`; Redirect-URIs: https, Loopback-http oder eigenes Schema — nie `javascript:`/`data:`/`file:`) und
   ruft `/oauth/authorize` (Authorization Code, **PKCE S256 Pflicht**, kein `plain`, kein `client_credentials`) auf.
2. Der MCP-Server prüft die Anfrage, merkt sie sich (`pending`, 10 min), setzt ein HttpOnly-Cookie `lc_mcp_pending` und leitet den Browser zu
   `{Learny}/mcp/connect?return=…&state=<pendingId>&client=…`.
3. Der Benutzer meldet sich bei Learny an (Entra ID) und entscheidet auf der Zustimmungsseite. Learny erzeugt einen **einmaligen Code** (5 min, nur als Hash gespeichert)
   und leitet an `{MCP_PUBLIC_URL}/oauth/callback?code=…&state=…` zurück. Erlaubt sind **ausschließlich** Rückleitungen auf `{MCP_PUBLIC_URL}/oauth/callback`
   (kein offener Redirect).
4. Der MCP-Server prüft Cookie = `state` (Schutz vor Login-CSRF: ein Angreifer kann seine Autorisierung nicht mit der Zustimmung eines Opfers abschließen),
   tauscht den Code serverseitig mit dem Dienstschlüssel gegen ein benutzergebundenes Token und gibt dem Client einen eigenen, einmaligen Autorisierungscode.
5. Am Token-Endpunkt (PKCE-Prüfung) erhält der Client das **Learny-Token als Access Token** (Laufzeit `MCP_TOKEN_DAYS`, Standard 30 Tage, kein Refresh Token).
6. Vor jedem `/mcp`-Aufruf prüft der MCP-Server das Token per `GET /api/mcp/whoami`. Widerrufene, abgelaufene oder gesperrte Zugänge führen sofort zu
   HTTP 401 mit `WWW-Authenticate` (der Client meldet sich neu an). Ist Learny nicht erreichbar, wird der Zugriff verweigert (fail closed).
7. Tool-Aufrufe reichen das Token der Anfrage an die API durch (`McpUserContext`, per `AsyncLocal` an die Anfrage gebunden).

### Persönliches Token (stdio, Skripte)
Im Profil unter `/profile` erzeugt der Benutzer ein Token (30/90/365 Tage); es wird **nur einmal** angezeigt. Der stdio-MCP-Server liest es aus
`LEARNCARDS_MCP_TOKEN`; HTTP-Clients senden es als Bearer-Header. Zugänge lassen sich im Profil einzeln widerrufen; ein Token kann keine weiteren Zugänge erzeugen.

### Konfiguration
- Web: `MCP_PUBLIC_URL` (öffentliche Basis-URL(en) des MCP-Servers, Allowlist für Rückleitungen), `MCP_TOKEN_DAYS`, `MCP_API_KEY` (Dienstschlüssel).
  Dev-Modus: Standard `http://127.0.0.1:8787` / `http://localhost:8787`.
- MCP-Server: `upstreamApi.baseUrl`/`apiKey`, `LEARNCARDS_WEB_PUBLIC_URL` bzw. `upstreamApi.webPublicUrl` (öffentliche Learny-URL für die Browser-Weiterleitung;
  Standard: `baseUrl` ohne `/api`), `oauth.enabled`, `oauth.issuer` (öffentliche MCP-URL), `LEARNCARDS_MCP_TOKEN` (nur stdio). `oauth.signingKey` und
  `oauth.tokenLifetimeMinutes` werden nicht mehr verwendet.

## Konsequenzen

- **Breaking Change für bestehende MCP-Anbindungen:** Der gemeinsame Schlüssel allein liefert keine Daten mehr. HTTP-Clients melden sich per OAuth an, stdio-Clients
  brauchen ein persönliches Token. Bereits registrierte Clients (in-memory) müssen sich nach einem Neustart des MCP-Servers erneut registrieren (dynamische Registrierung).
- Die Zustimmung nutzt die Learny-Sitzung (Entra ID): Wer sich nicht anmelden kann (gesperrt), kann keinen MCP-Zugang erhalten.
- Es gibt keine Refresh Tokens; nach Ablauf ist eine neue Anmeldung nötig. Ein gestohlenes Token gilt bis zum Widerruf/Ablauf — daher Widerruf im Profil und nur Daten-Routen.
- Pending-Autorisierungen und MCP-Autorisierungscodes liegen im Speicher des MCP-Servers; ein Neustart während einer Anmeldung bricht diese ab.
- Skalierung des MCP-Servers auf mehrere Instanzen würde einen gemeinsamen Zustand für Registrierung/Pending/Codes erfordern.

## Leitplanken für Änderungen

- Der MCP-Server bleibt ein **reiner API-Client** ohne Datenbank (ADR 0007); Benutzeridentität ausschließlich über das durchgereichte Learny-Token.
- Neue MCP-Tools nutzen `LearnCardsApiClient` (Token-Weitergabe) — niemals den Dienstschlüssel für Daten. Die API-Route muss in `TokenAllowedPrefixes` stehen
  und die Besitzregeln aus ADR 0011 einhalten; administrative Funktionen werden **nicht** für Tokens freigeschaltet (eigene Sicherheitsentscheidung/ADR).
- Der Dienstschlüssel darf nur serverseitig vom MCP-Server genutzt werden; nie an Clients oder in Tokens weitergeben, nie loggen.
- Rückleitungs-URLs nur über die Allowlist `MCP_PUBLIC_URL`; Tokens/Codes nie im Klartext speichern oder protokollieren.
- Änderungen am Ablauf mit den Tests in `tests/LearnCards.IntegrationTests` absichern (Abschnitte „MCP-Zugänge“ und „MCP-Server: OAuth-Anmeldung“).

## Betroffener Code

- Web: `Services/McpLinkService.cs`, `Api/ApiEndpoints.cs` (Auth-Filter, `/mcp/*`), `Components/Pages/McpConnect.razor`, `Profile.razor`, `Data/Schema.cs` (`mcp_links`, `mcp_link_codes`)
- MCP-Server: `ProprietaryOAuthService.cs`, `Program.cs` (OAuth-Endpunkte, `/mcp`), `LearnCardsApiClient.cs` (`McpUserContext`), `McpSettings.cs`
