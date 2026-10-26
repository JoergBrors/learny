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
| [0007](0007-mcp-server.md) | Eigenständiger MCP-Server als API-Client (Nachtrag 2026-10-26: `get_card`, `update_card`, `export_cards`) | Akzeptiert |
| [0008](0008-ki-integration-und-fallback.md) | KI-Integration mit Offline-Fallback und Token-Budget | Akzeptiert |
| [0009](0009-deployment-wege.md) | Deployment: IIS per Tag, Docker, Azure | Akzeptiert |
| [0010](0010-folienmodus-und-karten-quiz.md) | Folienmodus und statische Karten-Quizze | Akzeptiert (Quizlogik teilweise ersetzt durch 0019) |
| [0011](0011-mehrbenutzerarchitektur.md) | Mehrbenutzerarchitektur: Identität, Besitz, Rollen, Migration | Akzeptiert, ergänzt durch 0029 |
| [0012](0012-rollen-aus-entra-app-roles.md) | Rollen aus Entra-ID-App-Rollen (`roles`-Claim) | Akzeptiert, ergänzt durch 0029 |
| [0013](0013-mcp-benutzergebundene-anmeldung.md) | MCP: benutzergebundene Anmeldung per OAuth und persönliche Tokens | Akzeptiert |
| [0014](0014-sitzungs-und-betriebsmodus-haertung.md) | Sitzungs- und Betriebsmodus-Härtung | **Vorgeschlagen** (nicht umgesetzt) |
| [0015](0015-freigaben-globale-lernmodule.md) | Benutzerfreigaben, globale Lernmodule und Ergebniszuordnung | Akzeptiert, ergänzt durch 0029 |
| [0016](0016-bilder-in-lernkarten.md) | Bilder als Bestandteil von Lernkarten (Base64) | Akzeptiert |
| [0017](0017-konfigurierbare-bildspeicher.md) | Konfigurierbare Bildspeicherprovider, Bild-Cache, Migration | Akzeptiert |
| [0018](0018-zentraler-image-proxy-und-storage-zugriff.md) | Zentraler Image-Proxy und Storage-Zugriffsdelegation (SAS-Broker) | Akzeptiert |
| [0019](0019-einheitliches-quizsystem.md) | Einheitliche Quiz-Engine, Quizkonfiguration und persistenter Fragenpool | Akzeptiert |
| [0020](0020-sichere-visuelle-bearbeitung-formatierter-lerninhalte.md) | Sichere visuelle Bearbeitung formatierter Lerninhalte (Rich-Text, Karteneditor) | Akzeptiert |
| [0021](0021-zertifikate-und-open-badges.md) | Zertifikate und signierte Open Badges 3.0 | Akzeptiert (Oberfläche teilweise offen) |
| [0022](0022-polygon-verankerung-und-kostenschranke.md) | Polygon-Verankerung, Merkle-Bündelung und harte Kostenschranke | Akzeptiert (Signierlösung offen) |
| [0023](0023-secrets-und-badge-schluessel-unter-iis.md) | Geheimnisse und Badge-Schlüssel unter IIS (außerhalb des Webroots, Marker, fail-closed) | Akzeptiert (IIS-Test offen) |
| [0024](0024-docker-hub-veroeffentlichung.md) | Veröffentlichung als Docker-Hub-Images mit Compose-Installationspaket | Akzeptiert (Erstveröffentlichung offen) |
| [0026](0026-betriebsmodus-fail-closed.md) | Betriebsmodus fail closed: kein stiller Dev-Login, ENV_FILE verbindlich | Akzeptiert |
| [0027](0027-themen-lernpfade-versionierung-und-abschlusspruefungen.md) | Themen, Lernpfade, Versionierung und Abschlussprüfungen | Teilweise ersetzt durch 0028, 0029, 0032, ergänzt durch 0030 |
| [0028](0028-themenmodule-json-und-mcp-verwaltung.md) | Eigene Themenmodule, portable JSON-Definitionen und MCP-Verwaltung von Themenentwürfen | Akzeptiert |
| [0029](0029-themenzugriff-pflichtzuweisungen-und-themenadministration.md) | Themenbezogene Modulzugriffe, Pflichtzuweisungen und delegierte Themenadministration (Rolle `themen_admin`) | Akzeptiert |
| [0030](0030-einheitliche-modul-und-themenversionierung.md) | Einheitliche Modul- und Themenversionierung mit festgelegten Modulversionen | Akzeptiert |
| [0031](0031-kompetenzmodell-und-pruefungssnapshots.md) | Kompetenzmodell, Prüfungssnapshots und kompetenzbasierte Modulzertifikate | Akzeptiert |
| [0032](0032-themenabschluss-clr-und-master-badge.md) | Themenabschluss mit CLR 2.0 und Master-Open-Badge | Akzeptiert |
| [0033](0033-signierte-zertifikats-pdfs.md) | Signierte Zertifikats-PDFs mit eingebetteten Credentials und Upload-Prüfung | Akzeptiert |
| [0034](0034-nachweisuebersicht-pruefsiegel-und-linkedin.md) | Nachweisübersicht, Prüfsiegel „Learny Verified“ und LinkedIn-Angaben | Akzeptiert |
| [0025](0025-sichere-speicherung-von-mcp-tokens.md) | Sichere Speicherung von MCP-Tokens, Codes und OAuth-Clients (Schutz bei Datenbankzugriff) | Akzeptiert und umgesetzt |

Prüfberichte, die Anlass für ADRs sind, liegen unter [docs/reviews/](../reviews/) (aktuell: [Mehrbenutzer-Prüfbericht](../reviews/2026-10-24-mehrbenutzer-pruefbericht.md)).

## Neue ADR anlegen

1. Nächste Nummer vergeben, `NNNN-kurzer-titel.md` aus [template.md](template.md) kopieren.
2. Index oben ergänzen.
3. Bestehende ADRs werden **nicht umgeschrieben**: Wird eine Entscheidung geändert, entsteht eine neue ADR,
   die alte erhält den Status `Ersetzt durch NNNN`. Reine Klarstellungen/Tippfehler sind erlaubt.
