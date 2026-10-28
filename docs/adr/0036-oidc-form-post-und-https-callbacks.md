# 0036 — OIDC form_post und sichere HTTPS-Callbacks

- **Status:** Akzeptiert
- **Datum:** 2026-10-28
- **Ergänzt:** [0005](0005-authentifizierung.md), [0009](0009-deployment-wege.md), [0014](0014-sitzungs-und-betriebsmodus-haertung.md)

> **Architekturaussage.** Der OIDC-Authorization-Code-Flow mit PKCE bleibt; die Rückleitung von Entra ID erfolgt per `response_mode=form_post` als `POST /auth/callback`. Der Autorisierungscode erscheint nie in einer URL.

## Kontext

Unter IIS lieferte der Rücksprung `GET /auth/callback?code=…` HTTP 404.15 (Request Filtering, „Query String Too Long“, Standardlimit `maxQueryString` 2048): Entra-Codes sind länger. Ein Anheben globaler URL-Limits ist unerwünscht. Außerdem wurde die Redirect-URI bisher aus dem Request-Host gebildet (im Fehlerfall `http://`).

## Entscheidung

1. `response_mode=form_post`; `response_type=code`, PKCE S256, `state`, `nonce`, Scopes, Token-Austausch und ID-Token-Prüfung unverändert. Kein Implicit Flow.
2. `POST /auth/callback` (`application/x-www-form-urlencoded`, Body ≤ 32 KB): `code`, `state`, `error`, `error_description` – doppelte, überlange oder fehlende Felder werden abgewiesen. `GET /auth/callback` wertet nie einen Code aus. Antworten: `303`, `Cache-Control: no-store`, Ziel-URL ohne Codes.
3. Antiforgery ist nur für diesen Endpunkt abgeschaltet (Cross-Site-POST des Providers). Der Login-CSRF-Schutz ist die Korrelation: `state` im Formular muss (Konstantzeitvergleich) dem `state` im Data-Protection-geschützten, zeitlich begrenzten (10 min) Cookie entsprechen; jeder `state` ist nur einmal verwendbar (Cookie wird gelöscht, zusätzlich Wiederholungssperre im Speicher).
4. Das Korrelationscookie `lc.oidc.state` ist `SameSite=None; Secure; HttpOnly; Path=/auth/callback; Max-Age=10 min`, weil Lax-Cookies bei Cross-Site-POSTs nicht gesendet werden. Das Sitzungscookie `lc.auth` bleibt unverändert (Lax).
5. Redirect-URI: `PUBLIC_BASE_URL` + `/auth/callback` hat Vorrang vor dem Host-Header; außerhalb von `Development` ist HTTPS Pflicht (sonst Abbruch mit Meldung, kein Fallback). Fehlertexte des Providers werden nicht angezeigt, nur ein unkritischer Fehlercode.
6. IIS Request Filtering bleibt aktiv; `maxQueryString` wird nicht pauschal erhöht. Ein vorhandener manueller Workaround (4096) kann nach erfolgreichem Test zurückgenommen werden.

## Konsequenzen

- Die Entra-Redirect-URI (Typ „Web“) muss exakt `https://<öffentliche Adresse>/auth/callback` lauten; HTTPS ist jetzt zwingend.
- Laufende Logins beim Update schlagen einmalig fehl (altes Cookieformat/-pfad) und müssen neu gestartet werden.
- Die Wiederholungssperre ist prozesslokal (eine Instanz je Deployment, siehe [0003](0003-eine-deploybare-einheit.md)).
- Forwarded Headers vertrauen weiterhin allen Quellen (bestehend); die Redirect-URI ist durch `PUBLIC_BASE_URL` davon entkoppelt. Beschränkung auf bekannte Proxys bleibt offener Punkt aus ADR 0014.

## Leitplanken für Änderungen

- Keine Codes, Tokens oder PKCE-Verifier in URLs, Logs oder Fehlermeldungen; `/oauth/callback` des MCP-Servers ist ein getrennter Ablauf.
- Cookie-Einstellung nicht auf `lc.auth` ausdehnen; kein `SameSite=None` ohne `Secure`.
- Rollback: vorherige Version bereitstellen (nutzt GET/`query`) und `maxQueryString` wieder auf 4096 setzen.

## Betroffener Code

- `Auth/OidcCallback.cs`, `Auth/AuthEndpoints.cs`, `Auth/OidcService.cs` (`BuildAuthorizeUrl`), `tests/LearnCards.IntegrationTests/OidcCallbackTests.cs`

## Nachtrag 2026-10-28 (Abnahmeprüfung)

- Die Abnahme mit einem lokalen Test-Identity-Provider (`OidcFlowTests`) deckte auf, dass ein ID-Token **ohne `exp`** akzeptiert wurde. `exp` ist jetzt Pflicht (`OidcService.ValidateIdTokenAsync`). Der Token-Austausch gibt zudem keinen Antwortkörper des Providers mehr weiter.
- **Forwarded Headers** vertrauen weiterhin jeder Quelle; das ist **nicht behoben** (siehe [Sicherheitsdokument](../security-update-cve-2026-111497.md), Abschnitt 8, mit ADR-Vorschlag „Vertrauenswürdige Proxys“).
- **Wiederholungssperre des state:** prozesslokal, nur bei Einzelinstanz vollständig wirksam; für mehrere Instanzen wäre gemeinsamer Zustand und eine neue ADR nötig.
- Docker: automatischer Mindestversions-Check der .NET-Laufzeit vor der Veröffentlichung statt fester Image-Digests (`tools/docker/RuntimeCheck.cs`, `publish-docker.yml`; ergänzt [0024](0024-docker-hub-veroeffentlichung.md)).
