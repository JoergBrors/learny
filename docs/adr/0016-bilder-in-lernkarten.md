# 0016 — Bilder als Bestandteil von Lernkarten

- **Status:** Akzeptiert — Speicherung teilweise ersetzt durch [0017](0017-konfigurierbare-bildspeicher.md)/[0018](0018-zentraler-image-proxy-und-storage-zugriff.md); Format und Grenzen gelten weiter
- **Datum:** 2026-10-24
- **Hinweis zur Nummer:** Der Änderungsauftrag nannte „ADR 0011“; diese Nummer ist bereits für die Mehrbenutzerarchitektur vergeben (0011–0015), daher 0016.
- **Ergänzt:** [0006](0006-kanonisches-karten-json.md) (Karten-JSON), [0004](0004-datenbank-abstraktion.md) (Schema), [0007](0007-mcp-server.md) (MCP-Tools)

## Kontext

Lernkarten bestanden nur aus Text. Diagramme und Screenshots sollen als optionale Bestandteile einer Karte importiert (REST, MCP, Datei-Import) und angezeigt werden können — ohne externen Dateispeicher,
ohne neue Abhängigkeiten (ADR 0002) und ohne bestehende Karten, Importdateien oder den Offline-Betrieb zu beeinträchtigen. Multimodale LLMs sollen das Feld anhand des Live-Schemas befüllen können, die Anwendung setzt aber nie voraus,
dass ein LLM Bilder erzeugt.

## Entscheidung

### Kartenformat (rückwärtskompatible Erweiterung von ADR 0006)
Neues **optionales** Feld `images[]` am Karten-JSON; jedes Element:

| Feld | Pflicht | Bedeutung |
|---|:-:|---|
| `mime_type` | ja | `image/png`, `image/jpeg` oder `image/webp` — muss zur tatsächlichen Datei passen |
| `data_base64` | ja | reines Standard-Base64 der Bilddatei, **ohne** `data:`-Präfix |
| `alt_text` | nein (empfohlen) | Alternativtext, höchstens 300 Zeichen |
| `caption` | nein | Bildunterschrift, höchstens 300 Zeichen |

- **Vorläufige Produktlimits** (zentral in `Domain/CardImages.cs`, `CardImageLimits`): höchstens **5 Bilder** pro Karte, höchstens **2 MiB** je Bild (dekodiert). Live-Schema, MCP-Texte und Hilfe leiten sich daraus ab bzw. werden mitgezogen.
- **Semantik bei Fehlen:** fehlendes oder leeres `images` = keine Bilder. Beim **Überschreiben** (`overwrite_existing`, `PUT`) behält eine Karte ohne `images`-Feld ihre Bilder; `"images": []` entfernt sie bewusst
  (`CardJson.Images` ist deshalb nullable: `null` = Feld fehlt).

### Validierung (serverseitig, `CardImageValidator`)
In dieser Reihenfolge, mit nummerierten, verständlichen Fehlermeldungen („Bild 2: …“):
Anzahl → MIME-Allowlist → Textlängen → kein `data:`-Präfix → Länge des Base64-Texts (**vor** dem Dekodieren, damit riesige Eingaben nicht allokiert werden) → gültiges Standard-Base64 →
dekodierte Größe → **Dateisignatur** (PNG, JPEG, WebP) muss zum `mime_type` passen. Zeilenumbrüche/Leerraum im Base64 werden toleriert und entfernt (normalisierte Speicherung).
Die Prüfung läuft in `CardJson.Validate()` (Import/POST) **und** als letzte Verteidigungslinie in `CardRepository.CreateCardAsync/UpdateCardAsync` (deckt auch `PUT` und MCP ab). Bilder werden nie dekodiert, skaliert oder neu kodiert (keine Bildbibliothek nötig).

### Speicherung
`cards.images_json TEXT NOT NULL DEFAULT '[]'` (JSON-Array der Bilder inkl. Base64), idempotent migriert in `PostgresDatabase.MigrateCardContentColumnsAsync` bzw. `SqliteDatabase.InitAsync`.
Bestehende Karten erhalten `'[]'`. Parameter werden ausschließlich gebunden. Kopien (globale Veröffentlichung, ADR 0015) übernehmen die Bilder; Freigaben lesen dieselben Datensätze.

### API und Schema
- `GET /api/schema/import/cards` beschreibt `images[]` samt Unterfeldern, Limits und der Regel „nie Base64 erfinden“; das Beispiel im Schema enthält ein winziges, gültiges PNG und ist selbst importierbar.
- **Einzelabruf** `GET /api/cards/{id}` liefert die vollständigen Bilder; **Listen** und Schreibantworten liefern nur **Metadaten** (`mime_type`, `size_bytes`, `alt_text`, `caption`) — große Base64-Daten sollen Listenaufrufe (z. B. MCP `list_cards`) nicht aufblähen.
- Die Importantwort enthält zusätzlich `errors[]` (höchstens 20 abgelehnte Karten mit Grund). Einzelne fehlerhafte Karten verhindern den Import der übrigen nicht (wie bisher: `skipped`).

### MCP
Der MCP-Server enthält **keine Bildlogik**: `add_card`/`import_cards` reichen `images` unverändert an die API durch; Validierung und Fehlertexte kommen von dort. Tool-Beschreibungen und das Eingabeschema von `add_card` erklären Base64 (kein Präfix, echte Dateien, Limits)
und weisen LLMs an, **keine Base64-Daten zu erfinden**; `get_import_schema` liefert unverändert das Live-Schema. `import_cards` gibt abgelehnte Karten mit Grund zurück.

### Darstellung
`FlashCardView` rendert die Bilder direkt aus den gespeicherten Kartendaten als `data:`-URI (`<figure><img><figcaption>`), ohne zusätzliche Abrufe: auf der **Vorderseite** unter der Frage, auf der **Rückseite** nur im umgedrehten Zustand
(keine doppelten Bilddaten im Ruhezustand). Beim Rendern wird die MIME-Allowlist erneut geprüft; externe Bild-URLs werden nie geladen. Bilder sind responsiv (`max-width: 100%`, begrenzte Höhe) und liegen auf hellem Untergrund,
damit transparente Diagramme auch im Dark Mode lesbar bleiben. Folienmodus, Quiz, Freigaben und globale Module nutzen dieselbe Komponente und zeigen Bilder daher ebenfalls. Bilder werden **nie** in LLM-Prompts (Chat, Quiz) übernommen — der Offline-Fallback bleibt unberührt.

## Konsequenzen

- **Größe/Performance:** Eine Karte kann bis zu etwa 13 MB Base64 enthalten (5 × 2 MiB × 4/3). Die Anwendung lädt Karten eines Moduls derzeit samt Bildern in den Circuit-Speicher; bei vielen großen Bildern ist das spürbar.
  Folgearbeit bei Bedarf: Bilder erst für die aktuell angezeigte Karte nachladen oder in eine eigene Tabelle/Objektspeicher auslagern (neue ADR).
- **Importdateien und Anfragegrößen:** Das ASP.NET-Core-Standardlimit für Anfragekörper beträgt rund 30 MB (28,6 MiB); IIS (`maxAllowedContentLength`) hat standardmäßig denselben Wert. Der Datei-Import der Oberfläche akzeptiert bis 28 MiB.
  Das gilt für die Web-App und den HTTP-Transport des MCP-Servers gleichermaßen (nicht gemessen, aus den Framework-Standards abgeleitet). Beim stdio-Transport begrenzt der Server die Zeilenlänge nicht; Grenzen des MCP-Clients und des Modells (Kontext, Ausgabelänge) sind **nicht verifiziert**
  und liegen praktisch weit unter den Serverlimits — daher die Empfehlung: kleine Bilder (< 200 KiB), wenige pro Karte, große Importe in kleinen Stapeln.
- Sicherheitsabwägung: Base64 aus nicht vertrauenswürdigen Quellen wird nur als `data:`-URI mit geprüftem Typ in einem `<img>` verwendet (keine SVG, keine Skripte, kein HTML). SVG ist bewusst **nicht** erlaubt.
- Datenbankgröße wächst mit Bildern; Backups/Exports werden größer. Es gibt keine Bildverkleinerung und keine Deduplizierung.

## Leitplanken für Änderungen

- Limits und erlaubte Typen **nur** in `CardImageLimits` ändern; danach Live-Schema, MCP-Texte, Hilfe, diese ADR und die Tests prüfen. Neue Typen (z. B. GIF, SVG) erfordern eine Sicherheitsbewertung und eine neue ADR.
- **Jeder** neue Schreibpfad für Karten muss `CardImageValidator` durchlaufen (am besten über das Repository); Bilder nie ungeprüft speichern oder ausliefern.
- Listen-/Übersichtsendpunkte liefern keine Base64-Daten; vollständige Bilder nur im Einzelabruf. Keine Bilddaten in Logs, Audit-Einträge oder LLM-Prompts schreiben.
- Das Verhalten „fehlendes `images` behält bestehende Bilder“ nicht ändern (sonst verlieren Importe ohne Bildfeld Bilder).
- Keine externen Bild-URLs rendern, kein ungeprüftes HTML/SVG aus Kartendaten ausgeben.

## Betroffener Code

- `Domain/CardImages.cs` (Modell, Limits, Validator), `Domain/Models.cs` (`Card.Images`, `CardJson.Images`)
- `Data/Schema.cs`, `PostgresDatabase.cs`, `SqliteDatabase.cs` (`images_json`)
- `Services/CardRepository.cs` (Speichern/Lesen/Import mit `ImportResult.Errors`), `CardRepository.Sharing.cs` (Kopie)
- `Api/ApiEndpoints.cs` (DTOs: Metadaten vs. Vollabruf, Fehlerbehandlung), `Api/CardImportSchema.cs` (Live-Schema)
- `LearnCards.McpServer/Program.cs` (Tool-Beschreibungen, `add_card`-Schema, Importfehler)
- `Components/Shared/FlashCardView.razor`, `wwwroot/app.css`, `Components/Pages/Help.razor`, `wwwroot/templates/full-card-template.json`
