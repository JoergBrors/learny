# Architecture Decision Records (ADR)

Jede ADR beschreibt **eine** Entscheidung, die das Verhalten oder die Struktur von LearnCards prägt:
Kontext, Entscheidung, Konsequenzen und **Leitplanken für Änderungen**. Sie sind die verbindliche
Referenz dafür, *wie das System funktioniert und warum* — für Menschen und für KI-Assistenten
(siehe [AGENTS.md](../../AGENTS.md)).

## Index

| Nr. | Titel | Status |
|---|---|---|
| [0001](0001-adr-verwenden.md) | Architekturentscheidungen als ADR dokumentieren | Akzeptiert |
| [0002](0002-keine-nuget-pakete.md) | Keine externen NuGet-Pakete | Akzeptiert |
| [0003](0003-eine-deploybare-einheit.md) | Blazor Server + REST-API in einer Einheit | Akzeptiert |
| [0004](0004-datenbank-abstraktion.md) | `IDatabase`: SQLite lokal, PostgreSQL produktiv | Akzeptiert |
| [0005](0005-authentifizierung.md) | Authentifizierung: OIDC, Dev-Login, `X-MCP-Key` | Akzeptiert |
| [0006](0006-kanonisches-karten-json.md) | Kanonisches Karten-JSON (snake_case) und Live-Schema | Akzeptiert |
| [0007](0007-mcp-server.md) | Eigenständiger MCP-Server als API-Client | Akzeptiert |
| [0008](0008-ki-integration-und-fallback.md) | KI-Integration mit Offline-Fallback und Token-Budget | Akzeptiert |
| [0009](0009-deployment-wege.md) | Deployment: IIS per Tag, Docker, Azure | Akzeptiert |
| [0010](0010-folienmodus-und-karten-quiz.md) | Folienmodus und statische Karten-Quizze | Akzeptiert (Quizlogik teilweise ersetzt durch 0019) |
| [0011](0011-mehrbenutzerarchitektur.md) | Mehrbenutzerarchitektur: Identität, Besitz, Rollen, Migration | Akzeptiert |
| [0012](0012-rollen-aus-entra-app-roles.md) | Rollen aus Entra-ID-App-Rollen (`roles`-Claim) | Akzeptiert |
| [0013](0013-mcp-benutzergebundene-anmeldung.md) | MCP: benutzergebundene Anmeldung per OAuth und persönliche Tokens | Akzeptiert |
| [0014](0014-sitzungs-und-betriebsmodus-haertung.md) | Sitzungs- und Betriebsmodus-Härtung | **Vorgeschlagen** (nicht umgesetzt) |
| [0015](0015-freigaben-globale-lernmodule.md) | Benutzerfreigaben, globale Lernmodule und Ergebniszuordnung | Akzeptiert |
| [0016](0016-bilder-in-lernkarten.md) | Bilder als Bestandteil von Lernkarten (Base64) | Akzeptiert |
| [0017](0017-konfigurierbare-bildspeicher.md) | Konfigurierbare Bildspeicherprovider, Bild-Cache, Migration | Akzeptiert |
| [0018](0018-zentraler-image-proxy-und-storage-zugriff.md) | Zentraler Image-Proxy und Storage-Zugriffsdelegation (SAS-Broker) | Akzeptiert |
| [0019](0019-einheitliches-quizsystem.md) | Einheitliche Quiz-Engine, Quizkonfiguration und persistenter Fragenpool | Akzeptiert |

Prüfberichte, die Anlass für ADRs sind, liegen unter [docs/reviews/](../reviews/) (aktuell: [Mehrbenutzer-Prüfbericht](../reviews/2026-10-24-mehrbenutzer-pruefbericht.md)).

## Neue ADR anlegen

1. Nächste Nummer vergeben, `NNNN-kurzer-titel.md` aus [template.md](template.md) kopieren.
2. Index oben ergänzen.
3. Bestehende ADRs werden **nicht umgeschrieben**: Wird eine Entscheidung geändert, entsteht eine neue ADR,
   die alte erhält den Status `Ersetzt durch NNNN`. Reine Klarstellungen/Tippfehler sind erlaubt.
