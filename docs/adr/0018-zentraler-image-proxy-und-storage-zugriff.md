# 0018 — Zentraler Image-Proxy und Storage-Zugriffsdelegation (SAS-Broker)

- **Status:** Akzeptiert
- **Datum:** 2026-10-25
- **Ergänzt:** [0017](0017-konfigurierbare-bildspeicher.md) (Provider, Cache, Migration), [0016](0016-bilder-in-lernkarten.md) (Bildformat, unverändert)
- **Bezug:** [0011](0011-mehrbenutzerarchitektur.md), [0015](0015-freigaben-globale-lernmodule.md) (Berechtigungen), [0014](0014-sitzungs-und-betriebsmodus-haertung.md) (Härtung)

## Kontext

Bilder liegen je nach Provider in privaten Cloud-Containern. Clients (Browser, MCP) dürfen dort weder direkt zugreifen noch Zugangsdaten sehen: weder Kontoschlüssel noch SAS-Token noch Graph-/Presigned-URLs. Gleichzeitig soll der Server
den Speicher mit minimalen, kurzlebigen Rechten ansprechen und später ein separater Dienst die Ausstellung von SAS übernehmen können.

## Entscheidung

1. **Einziger Weg zum Client ist `GET /api/images/{imageId}`.** Ablauf: Authentifizierung → Berechtigung über `CardRepository.GetReadableImageAsync` (eigene/freigegebene/globale Karte, ADR 0011/0015; fremde ⇒ 404) → interner Provider-Lookup aus den Metadaten
   (Provider + Schlüssel stammen aus der Datenbank, nie vom Client) → Cache → Speicher → Bytes mit korrektem `Content-Type`, `X-Content-Type-Options: nosniff`, `ETag` (SHA-256), `Cache-Control: private`.
   **Keine Weiterleitungen** auf Cloud-URLs, keine Zugangsdaten in Antworten. Karten-JSON enthält nur `url = /api/images/{id}` (kein Provider, kein Schlüssel, keine Cloud-URL). Die UI verwendet ausschließlich diese URL (`loading="lazy"`, `alt`/Beschriftung).
   Der Proxy kann mit `IMAGE_PROXY_ENABLED=false` als Notschalter abgeschaltet werden (503, Code `proxy_disabled`).
2. **Trennung Provider ↔ Zugriffsbroker.** `IStorageAccessBroker.GetReadGrantAsync(key)` liefert ein `StorageGrant` (URL, Ablaufzeit): **ausschließlich lesend, auf ein Objekt beschränkt, kurzlebig**. Der Broker kennt keine Benutzer; die Anwendung entscheidet vorher über die Berechtigung.
   Der Provider schreibt/löscht/prüft mit eigener serverseitiger Identität und liest nur über das Grant.
3. **Azure Blob:**
   - Produktion: Entra ID (Managed Identity bevorzugt, sonst Client-Secret) und **User-Delegation-SAS** (Schlüssel per „Get User Delegation Key“, nur im Arbeitsspeicher; benötigt Rolle *Storage Blob Delegator* + Datenrolle, z. B. *Storage Blob Data Contributor*).
   - SAS-Eigenschaften: Berechtigung nur `r`, Ressource `sr=b` (**ein Blob**, nie Container-/Konto-SAS), Version 2020-12-06, Start −2 min (Uhrenabweichung), Ablauf `IMAGE_AZURE_SAS_TTL_SECONDS` (Standard **60 s**, erlaubt 10–600), `spr=https`.
   - Entwicklung/Test (Azurite): `IMAGE_AZUREBLOB_AUTH=sharedkey` ⇒ Service-SAS mit Kontoschlüssel (gleiche Form, ebenfalls blob-bezogen, `sp=r`). Nicht für Produktion; Azurites OAuth/User-Delegation ist ohne HTTPS nicht testbar und wurde nicht geprüft.
   - Ein SAS wird **nie in der Datenbank gespeichert, nie geloggt, nie an Clients ausgegeben**. Lehnt der Speicher ein Grant ab (abgelaufen/widerrufen), wird der Broker zurückgesetzt und **genau einmal** ein neues Grant geholt; danach kontrollierter Fehler.
4. **S3-kompatibel:** Presigned-GET (SigV4 per Query, nur `host` signiert, `IMAGE_S3_PRESIGN_TTL_SECONDS` Standard 60 s) für genau ein Objekt; Schreiben/Löschen/Prüfen mit Header-Signatur.
5. **SharePoint:** Graph mit Server-Token (App-only); die kurzlebige `downloadUrl` wird nur für den einen Abruf im Arbeitsspeicher benutzt und nur akzeptiert, wenn Host auf `*.sharepoint.com|.us|.de|.cn` bzw. `*.files.1drv.com` endet und das Schema https ist.
6. **Database/Local:** interner Zugriff ohne Broker.
7. **Zukünftiger externer SAS-Handler (Vertrag, nicht implementiert):** Ein separater Dienst implementiert `IStorageAccessBroker`: Eingabe = Provider-Schlüssel (32 Hex), Ausgabe = `StorageGrant`. Vertrauensgrenze: Die Anwendung vertraut dem Handler nur für die *Ausstellung*; sie
   prüft jede zurückgelieferte URL **erneut** (`StorageUrlGuard`) und ruft nur fest konfigurierte Endpunkte/Container ab. Der Handler authentifiziert die Anwendung (Managed Identity/mTLS, nicht über Benutzer-Tokens), liefert nur Leserechte, kurze TTL, und protokolliert Ausstellungen ohne SAS-Inhalt.
8. **Sicherer Cloud-Abruf (`SafeHttp`, `StorageUrlGuard`):**
   - Ziele nur aus der Konfiguration (Endpunkt, Container/Bucket) + serverseitigem Schlüssel; **nie** Hosts/Pfade aus Daten. Broker-URLs werden vor Benutzung geprüft: gleiches Schema/Host/Port, genau der erwartete Pfad, keine Benutzerinfo/Fragmente, nur erlaubte Query-Parameter, `sp=r`/`sr=b`.
   - https verpflichtend; http nur mit Entwicklungs-Flag (`…_ALLOW_HTTP`).
   - **Keine Weiterleitungen** (`AllowAutoRedirect=false`), kein Proxy. DNS wird aufgelöst und die Adresse **vor dem Verbinden** geprüft (SSRF/DNS-Rebinding): Loopback, private, Link-Local (Metadatendienst 169.254.169.254), CGNAT, Multicast, IPv6-ULA werden blockiert, außer
     ausdrücklich per `IMAGE_AZUREBLOB_/IMAGE_S3_/IMAGE_SHAREPOINT_ALLOW_PRIVATE_NETWORK=true` (Private Endpoints, Azurite).
   - Zeitlimits (`IMAGE_HTTP_TIMEOUT_SECONDS`), Abbruch über `CancellationToken`, harte Größenbegrenzung der Antwort (2 MiB) beim Streamen, eine kontrollierte Wiederholung bei 429/503.
   - Fehler werden auf einheitliche Kategorien abgebildet (404 / 503 / 504 / 502 / 400); Meldungen enthalten nie URL, Header oder Signatur (`SecretRedactor`).
9. **Protokollierung je Abruf:** Bild-ID, Provider, Cache hit/miss, Dauer, Bytes, Status bzw. Fehlerkategorie. Nie SAS, Signatur, Kontoschlüssel, signierte URL oder Authorization.
10. **Cache vor Speicher, Berechtigung vor Cache** (ADR 0017): Ein Treffer löst keinen Brokeraufruf und keinen Speicherzugriff aus.

## Folgen

- Alle Remote-Zugriffe laufen durch den Server; Clients sehen nie Cloud-Adressen. Preis: Bildverkehr läuft über die Anwendung (durch Cache und Browser-Caching abgefedert).
- Neue Betriebsrollen: *Storage Blob Delegator* (+ Datenrolle) für die Identität der Anwendung; ggf. Private-Endpoint-Freigabe.
- Offene Punkte: User-Delegation-SAS und echte Entra-Anmeldung wurden nicht gegen ein reales Azure-Konto geprüft; S3 nur per Testvektor/Unit-Test, SharePoint nur gegen einen Graph-Mock.
