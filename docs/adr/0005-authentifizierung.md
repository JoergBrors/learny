# 0005 — Authentifizierung: OIDC, Dev-Login, `X-MCP-Key`

- **Status:** Akzeptiert
- **Datum:** 2026-10-24
- **Ergänzt durch:** [0011](0011-mehrbenutzerarchitektur.md) (interne Benutzer-ID, Rollen, Besitz), [0012](0012-rollen-aus-entra-app-roles.md) (Rollen aus Entra-App-Rollen), [0013](0013-mcp-benutzergebundene-anmeldung.md) (MCP-Tokens; `X-MCP-Key` ist nur noch Dienstschlüssel ohne Datenzugriff)
- **Vorschlag zur Härtung:** [0014](0014-sitzungs-und-betriebsmodus-haertung.md) (nicht umgesetzt)

## Kontext

Produktiv Entra ID, lokal soll ohne Identity Provider gearbeitet werden; Automationen (MCP) brauchen
einen Nicht-Browser-Zugang.

## Entscheidung

> **Stand 2026-10-24:** Die Aussagen zu `user_sub` und zum vollen API-Zugriff per `X-MCP-Key` sind durch ADR 0011 und 0013 überholt (interne Benutzer-ID `uid`; der Schlüssel gewährt keinen Datenzugriff). Bekannte Abweichungen im Betrieb: Prüfbericht F1–F3 (Dev-Login-Rückfall bei `AUTH_MODE=auto`, Cookie nach Logout, offene Circuits).

- `AUTH_MODE=auto`: `oidc`, sobald `OIDC_ISSUER` und `OIDC_CLIENT_ID` gesetzt sind, sonst `dev`.
- OIDC: Authorization-Code-Flow mit PKCE, ID-Token-Validierung gegen JWKS (`iss`, `aud`, `exp`, `nonce`),
  danach Cookie-Session `lc.auth` (HttpOnly, SameSite=Lax, 10 h gleitend).
- `/api/*` akzeptiert Cookie-Login **oder** den Header `X-MCP-Key` (= `MCP_API_KEY`). Ohne beides: HTTP 401 (kein Redirect).
- Benutzerbezogene Daten (Quiz-Ergebnisse, Chat, Karten-Status, Theme) sind über `user_sub` getrennt.
- Data-Protection-Schlüssel werden unter `data/keys` bzw. `DATA_PROTECTION_DIR` persistiert.

## Konsequenzen

- Der `dev`-Modus ist **nicht für Produktion**.
- Der MCP-Key gewährt vollen API-Zugriff; er ist ein Geheimnis.

## Leitplanken für Änderungen

- Neue `/api`-Endpunkte erben den Auth-Filter der Gruppe `api` — keine Endpunkte außerhalb der Gruppe ohne Begründung.
- Endpunkte mit Benutzerbezug müssen mit `X-MCP-Key` umgehen können (kein Benutzer-`sub`) — Muster in `ApiEndpoints.cs` prüfen.
- Keine Secrets in Code, Beispieldateien oder Logs; Konfiguration nur über Umgebung/`.env` (siehe `.env.example`).
- Neue Konfigurationswerte in `AppConfig.cs`, `.env.example`, `deploy/iis/web.env` und Dokumentation ergänzen.

## Betroffener Code

- `src/LearnCards.Web/Auth/*`, `Api/ApiEndpoints.cs` (Filter), `Infrastructure/AppConfig.cs`, `Program.cs`

## Nachtrag (ADR 0036)

Der OIDC-Rücksprung erfolgt per `response_mode=form_post` (`POST /auth/callback`); siehe [0036](0036-oidc-form-post-und-https-callbacks.md).
