# 0017 — Konfigurierbare Bildspeicherprovider, Bild-Cache und explizite Migration

- **Status:** Akzeptiert
- **Datum:** 2026-10-25
- **Ersetzt teilweise:** [0016](0016-bilder-in-lernkarten.md) — nur die Speicherung (Base64 inline in `cards.images_json`). Das Import-/API-Format `images[]`, die Grenzen (5 Bilder, 2 MiB, 300 Zeichen Text) und die Validierung aus 0016 gelten unverändert weiter.
- **Ergänzt durch:** [0018](0018-zentraler-image-proxy-und-storage-zugriff.md) (Auslieferung, SAS/Presign, SSRF-Schutz)
- **Ergänzt:** [0004](0004-datenbank-abstraktion.md) (Schema), [0011](0011-mehrbenutzerarchitektur.md)/[0015](0015-freigaben-globale-lernmodule.md) (Berechtigungen)

## Kontext

ADR 0016 speicherte Bilder als Base64-Text in der Kartenzeile. Das vergrößert jede Kartenabfrage, verhindert Browser-Caching und bindet die Bilder an die Datenbank. Betriebsumgebungen (IIS, Docker, Azure) brauchen
alternative Ablagen (Dateisystem, Azure Blob, S3-kompatibel, SharePoint). Gleichzeitig dürfen Berechtigungen (privat/freigegeben/global), das Import-JSON und die MCP-Tools nicht davon abhängen, wo die Bytes liegen.

## Entscheidung

1. **Provider-Schnittstelle** `IImageStorageProvider` (binär, `CancellationToken`): `PutAsync`, `GetAsync` (mit optionalem ETag), `ExistsAsync`, `DeleteAsync`, `CheckHealthAsync`.
   Speicherschlüssel sind **serverseitig** erzeugte GUIDs (32 Hex, `ImageStorageKey`) und werden von jedem Provider erneut validiert (keine Pfad-/URL-/Header-Manipulation).
2. **Konfiguration** über Umgebungsvariablen (`IMAGE_STORAGE_PROVIDER=database|local|azureblob|s3|sharepoint`, Standard `database`). Secrets nur aus der Umgebung/einer Secret-Verwaltung, nie im Quellcode. Die Konfiguration wird beim Start vollständig geprüft
   (`ImageStorageSettings.Validate`); ungültige Werte verhindern den Start mit einer lesbaren Fehlerliste. Pro Installation nimmt genau ein **primärer** Provider neue Bilder auf; weitere konfigurierte Provider bleiben lesbar.
3. **Metadaten in der Datenbank:** Tabelle `card_images` (`id` = `image_id`, `card_id`, `provider`, `storage_key`, `mime_type`, `size_bytes`, `checksum` SHA-256, `alt_text`, `caption`, `sort_order`).
   Der Database-Provider legt die Bytes in `image_blobs` (Base64-**Text**, weil das Datenbankprotokoll textbasiert ist — `IDatabase`, ADR 0004; die Provider-Schnittstelle bleibt dennoch binär).
   Bytes mit gleicher Prüfsumme innerhalb einer Karte werden dedupliziert; das Löschen erfolgt über Referenzzählung (`CleanupOrphansAsync`).
4. **Altbestand:** Beim Start werden Karten mit `images_json` einmalig in `card_images` überführt (`cards.images_migrated`). `images_json` bleibt **unverändert** erhalten (kein Datenverlust, Rückfall möglich); Fehler einer Karte blockieren den Start nicht.
5. **Provider:**
   - *database* — Standard, kompatibel zu bestehenden Installationen.
   - *local* — `{IMAGE_LOCAL_PATH}/{key[0..2]}/{key}.img`, atomare Schreibvorgänge (temporäre Datei + Umbenennen mit Wiederholung unter Windows), Linux/macOS 0600/0700, im Container ein persistentes Volume.
   - *azureblob* — REST über `HttpClient` (kein SDK). Daten-Ebene (Schreiben/Löschen/Prüfen) mit Entra-Token (Managed Identity bevorzugt, Client-Secret) oder SharedKey (nur Entwicklung/Azurite); **Lesen nur über Lese-SAS** (ADR 0018).
   - *s3* — S3-kompatibel (AWS, MinIO …), eigene SigV4-Implementierung (gegen den offiziellen AWS-Testvektor geprüft), optional Path-Style, fester Endpunkt aus der Konfiguration; Lesen über Presigned-URL (ADR 0018).
   - *sharepoint* — Microsoft Graph, **App-only** (Client-Credentials). Die Anmelde-Tokens der Benutzer werden nie für Graph verwendet; Berechtigungen prüft allein LearnCards. Minimal nötig: Anwendungsberechtigung `Sites.Selected` (mit Schreibzugriff auf genau die Site/Bibliothek) — nicht `Sites.ReadWrite.All`.
6. **Bild-Cache** `IImageCache` (Arbeitsspeicher-LRU + optional persistenter Dateicache, `TwoLevelImageCache`): gilt nur für **entfernte** Provider. Schlüssel = Provider + Bild-ID + Prüfsumme (Inhaltsänderung ⇒ neuer Eintrag). Ein Treffer löst **keinen** Providerzugriff aus;
   gleichzeitige Abrufe desselben Bildes werden zusammengefasst (single-flight); Größenbegrenzung mit Verdrängung; nach Ablauf der TTL wird per ETag revalidiert (Azure/S3: bedingte Anfrage, SharePoint: Metadaten-ETag ohne Download). Dateien liegen unter SHA-256-Namen, Inhalt wird beim Lesen gegen die Prüfsumme geprüft.
   Temporäre Download-URLs (Graph `downloadUrl`, SAS) werden **nie** gespeichert. Der Cache liegt **hinter** der Berechtigungsprüfung und umgeht sie nie. Standard-Dateicache nur bei SharePoint (`data/image-cache`), sonst per `IMAGE_CACHE_PATH`.
   Einstellungen: `IMAGE_CACHE_ENABLED` (true), `IMAGE_CACHE_MAX_SIZE_MB` (512), `IMAGE_CACHE_TTL_MINUTES` (1440), `IMAGE_CACHE_MEMORY_MB` (64), `IMAGE_CACHE_PATH`.
7. **Migration** (`ImageMigrationService`, Admin-Endpunkte `GET /api/admin/images/storage`, `POST /api/admin/images/migrate`): nur **ausdrücklich** gestartet, nie automatisch beim Providerwechsel. Je Objekt: Quelle lesen → Prüfsumme prüfen → Ziel schreiben →
   zurücklesen und Prüfsumme prüfen → erst dann `card_images.provider` umstellen. **Die Quelle wird nie gelöscht.** Abschnittsweise (`limit`, `cursor`) und damit wiederaufnehmbar/idempotent; Trockenlauf (`dry_run`); Audit-Eintrag `images.migrate`; nur Rolle `admin`, nur Browser-Login.
8. **Import/Export:** `images[]` mit `data_base64` bleibt der einzige Eingabeweg (REST, MCP, Datei). Einträge mit nur `image_id` behalten ein vorhandenes Bild. `GET /api/cards/{id}` liefert Metadaten + URL; Bilddaten als Base64 nur mit `?include_image_data=true` (Export/Sicherung).

## Verworfene Alternativen

- *Binärspalte (`bytea`/BLOB)*: `IDatabase` überträgt Werte als Text; ein zweiter Binärpfad wäre ein Eingriff in beide Datenbanktreiber ohne Mehrwert für die Zielgröße (≤ 2 MiB, ≤ 5 Bilder je Karte).
- *Cloud-SDKs*: widerspricht ADR 0002 (keine NuGet-Pakete); REST + eigene Signierung ist überschaubar und testbar.
- *Automatische Migration beim Providerwechsel*: riskant und nicht rückholbar; stattdessen explizit, prüfbar, ohne Löschen.

## Folgen

- Karten-Abfragen sind klein (Metadaten statt Base64); Browser cachen Bilder (`ETag`, `Cache-Control: private`).
- Ein Providerwechsel allein ändert vorhandene Bilder nicht: sie bleiben beim alten Provider lesbar, solange dieser konfiguriert ist (`NotConfigured` → 503 mit Hinweis), bis eine Migration läuft.
- Zusätzliche Betriebsaufgabe: Berechtigungen/Rollen des gewählten Speichers verwalten (siehe Betriebsdokumente).

## Messwerte (Testlauf vom 2026-10-25; lokale Dienste, SQLite, eine Maschine)

Speichern von 3 Bildern (361 KB) · Abruf eines 300-KB-Bildes, Median / P95 über 20 Abrufe (Anwendung → Proxy → Speicher bzw. Cache):

| Provider | Speichern | Abruf (Median / P95) |
|---|---|---|
| database | 93 ms | 4 / 22 ms |
| local | 100 ms | 2 / 3 ms |
| azureblob (Azurite, Cache an) | 133 ms | 1 / 1 ms |
| sharepoint (Graph-Mock, Cache an) | 92 ms | 0 / 1 ms |

Die Werte bilden nur den Anwendungs-Overhead mit lokalen Diensten ab (kein Netzwerk, Wiederholungsabrufe treffen bei Remote-Providern den Cache). Echte Cloud-Latenzen wurden nicht gemessen; sie sind für den ersten Abruf bzw. nach Cache-Ablauf maßgeblich.
