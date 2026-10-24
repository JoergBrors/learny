# 0003 — Blazor Server + REST-API in einer deploybaren Einheit

- **Status:** Akzeptiert
- **Datum:** 2026-10-24
- **Ergänzt durch:** [0011](0011-mehrbenutzerarchitektur.md) (Benutzerkontext in Komponenten und API), [Prüfbericht](../reviews/2026-10-24-mehrbenutzer-pruefbericht.md)

## Kontext

Die ursprüngliche Lösung bestand aus FastAPI-Backend, React-Frontend und Reverse Proxy (CORS, SSE-Sonderfälle).

## Entscheidung

`LearnCards.Web` vereint Blazor Web App (interaktive Server-Komponenten), Minimal-API unter `/api`
und OIDC-Endpunkte unter `/auth/*` in **einem** Prozess. Dienste sind Singletons
(`Program.cs`): `IDatabase`, `CardRepository`, `OpenAiClient`, `QuizService`, `ChatService`, `OidcService`.

## Konsequenzen

- Kein CORS, ein Deployment-Artefakt, einfache lokale Entwicklung.
- Blazor Server hält pro Benutzer eine SignalR-Verbindung; Skalierung nach außen braucht Sticky Sessions.
- Die REST-API existiert für externe Clients (MCP, Automation) und bleibt zum Python-Original kompatibel.

## Leitplanken für Änderungen

- Geschäftslogik gehört in `Services/` bzw. `CardRepository`, **nicht** in `.razor`-Dateien oder Endpunkt-Lambdas.
- Neue Funktion → Service-Methode, dann (falls extern nötig) Endpunkt in `Api/ApiEndpoints.cs`, UI darauf aufsetzen.
- Neue Services als Singleton in `Program.cs` registrieren; **keine Zustände pro Benutzer in Singletons, Caches oder statischen Feldern halten** (Prüfbericht: aktuell erfüllt). Benutzerbezug immer über Parameter (`ownerId`/`userSub`), nie über Felder.
- Routen und JSON-Felder der API sind ein Vertrag (siehe [0006](0006-kanonisches-karten-json.md)) — nicht ohne Not umbenennen.

## Betroffener Code

- `src/LearnCards.Web/Program.cs`, `Api/ApiEndpoints.cs`, `Components/**`, `Services/**`
