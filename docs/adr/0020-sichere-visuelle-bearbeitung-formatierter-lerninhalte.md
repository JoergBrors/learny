# 0020 — Sichere visuelle Bearbeitung formatierter Lerninhalte (Rich-Text und Karteneditor)

- **Status:** Akzeptiert
- **Datum:** 2026-10-26
- **Ergänzt:** [0006](0006-kanonisches-karten-json.md) (Textfelder erhalten eine optionale Formatsyntax; keine neuen Felder), [0016](0016-bilder-in-lernkarten.md)/[0017](0017-konfigurierbare-bildspeicher.md)/[0018](0018-zentraler-image-proxy-und-storage-zugriff.md) (Bilder), [0019](0019-einheitliches-quizsystem.md) (Quiz-Engine und Fragenpool werden wiederverwendet)
- **Bezug:** [0002](0002-keine-nuget-pakete.md) (keine Editor-Bibliothek), [0003](0003-eine-deploybare-einheit.md), [0011](0011-mehrbenutzerarchitektur.md), [0015](0015-freigaben-globale-lernmodule.md)

## Kontext

Karten ließen sich nur per JSON-Import/-REST anlegen; die Felder `definition`, `how_it_works`, `context`, `key_fact`, `reference_answer` waren Klartext. Benutzer sollen Karten, Texte (formatiert), Bilder und Quizfragen im Browser bearbeiten können — ohne
neue Abhängigkeiten, ohne zweite Persistenz und so, dass formatierte Texte keine Angriffsfläche (XSS) öffnen. Browser-Editoren auf Basis von `contenteditable` liefern HTML; dieses darf weder gespeichert noch ungeprüft ausgegeben werden.

## Entscheidung

1. **Eigenes, begrenztes Textformat statt HTML.** Formatierte Texte werden als normaler Text mit einer kleinen Markdown-Teilmenge in den **bestehenden Feldern** gespeichert (`RichText`, `Domain/RichText.cs`): Absätze (Leerzeile), Zeilenumbruch, Überschriften `# ## ###`, `**fett**`, `*kursiv*`/`_kursiv_`, `++unterstrichen++`,
   Listen `- ` und `1. `, Links `[Text](https://…)` (nur http/https), Maskierung mit `\`. **Keine neuen Datenbankfelder, keine neuen JSON-Felder**; bestehender Klartext bleibt gültig (Schutz gegen Fehlinterpretation: `C++/C++`, `2 * 3 * 4`, `snake_case` werden nicht formatiert).
2. **Ausgabe nur serverseitig und nur aus dem Markup:** `RichText.ToHtml` kodiert zuerst den gesamten Text (HTML-Encode) und erzeugt danach ausschließlich `p, br, strong, em, u, h4–h6, ul, ol, li, a`. Links nur für absolute http(s)-Adressen ohne Zugangsdaten, Anführungszeichen, Leerraum oder Steuerzeichen; `rel="noopener noreferrer nofollow"`, `target="_blank"`;
   URL-Inhalte werden vor der Hervorhebung herausgelöst (Markup in URLs verändert Adressen nicht). Skripte, Event-Handler, `javascript:`/`data:`-URLs, SVG, eingebettete Ressourcen, iframes und Styles sind damit **nicht darstellbar**; sie erscheinen als sichtbarer Text. Interne Platzhalterzeichen (Private-Use) werden aus Eingaben entfernt.
   Die Komponente `RichTextView` gibt das Ergebnis aus; `FlashCardView` und die Editor-Vorschau nutzen dieselbe Darstellung (Klasse `rt`).
3. **Eingabegrenzen:** je Feld höchstens 20 000 Zeichen, keine Steuerzeichen außer Zeilenumbruch/Tab (`RichText.Validate`, in `CardJson.Validate` — gilt für POST, PUT und Import). Links ≤ 2000 Zeichen.
4. **Visueller Editor ohne Bibliothek:** `RichTextEditor` (Blazor) + `wwwroot/rte.js` (`contenteditable`, `execCommand` für Absatzformat, fett, kursiv, unterstrichen, Listen, Link, Format entfernen). Der Editor **serialisiert beim Tippen in das Markup** (nicht in HTML); die Anwendung rendert das Markup serverseitig neu.
   Einfügen aus der Zwischenablage und Drag & Drop werden als **Klartext** behandelt, fremdes HTML gelangt nie in den Editor. Die Serialisierung übernimmt nur bekannte Formatierungen (auch aus verschachtelten Elementen) und verwirft alles andere. Links werden im Browser **und** serverseitig auf http(s) geprüft.
5. **Karteneditor** (`/module/{id}/edit`, `ModuleEditor.razor`, nur Eigentümer): Karten anlegen, bearbeiten, duplizieren, archivieren/wiederherstellen, sortieren; alle Kartenfelder; Quellen; Folienmodus; Bilder (Upload, Vorschau, Alternativtext, Unterschrift, Reihenfolge, bewusstes Entfernen mit Bestätigung); Live-Vorschau als verschiebbares, nicht modales Overlay (Position/Größe in `localStorage`, Zurücksetzen mit ⟲, auf schmalen Bildschirmen unteres Fenster); Verwerfen; Quiz-Einstellungen
   (`QuizConfigPanel` wiederverwendet). Keine neue Persistenz: Speichern über `CardRepository.CreateCardAsync/UpdateCardAsync`, Bilder über `CardImageService` (Validierung wie beim Import), Fragen über `QuizService`/`QuizPoolService`.
   Neu am Repository: `DuplicateCardAsync`, `ReorderCardsAsync`, `ToCardJson`, optionale Konflikterkennung in `UpdateCardAsync`; REST: `POST /api/cards/{id}/duplicate`, `PUT /api/modules/{id}/cards/order`, Kopfzeile `X-Card-Updated-At` bei `PUT /api/cards/{id}`; `PUT /api/cards/{id}` validiert jetzt wie `POST` (`CardJson.Validate`).
6. **Bilder:** Editor sendet beim Speichern stets die vollständige Liste (vorhandene per `image_id`, neue mit Daten) — dadurch bleiben Bilder erhalten, Texte und Reihenfolge ändern sich. Vorhandene Bilder erscheinen ausschließlich über `GET /api/images/{id}` (ADR 0018); nur *noch nicht gespeicherte* Uploads zeigt die Vorschau lokal als `data:`-URL des gerade gewählten Bildes.
7. **Quizfragen — Quelle der Wahrheit bleibt unverändert (ADR 0019):** Fragen aus dem Karten-`quiz[]` (`source=card_json`) werden im Karteneditor bearbeitet und **mit der Karte** gespeichert (Synchronisation über den vorhandenen Schreibpfad). Fragen mit `source=ai` oder `editor` liegen im Fragenpool und werden **sofort einzeln**
   über `QuizService` gespeichert (anlegen, bearbeiten, Karte zuordnen, aktivieren/deaktivieren). Die Oberfläche trennt beide Bereiche sichtbar und beschriftet, wo gespeichert wird; dieselbe Frage ist nie an zwei Stellen bearbeitbar. Keine neue Bewertung, keine neue Generierung.
8. **Ungespeicherte Änderungen und Konflikte:** Der Editor vergleicht laufend einen Schnappschuss des Entwurfs mit dem geladenen Stand (Anzeige „ungespeicherte Änderungen“). Rückfrage bei Kartenwechsel, internen Link-Klicks (Capture-Listener in `rte.js`, unabhängig von Blazors Navigationsabfangen), Blazor-Navigation (`NavigationLock`) und native Browserwarnung beim Schließen/Neuladen.
   Gleichzeitige Bearbeitung: `UpdateCardAsync` vergleicht `updated_at` des Ladens mit dem aktuellen Stand → `CardConflictException` (REST: 409 `card_conflict`); der Editor meldet den Konflikt, überschreibt nichts und bietet „Neu laden“ an. Ohne Kopfzeile gilt unverändert „letzter Schreibzugriff gewinnt“ (Import, bestehende Clients).
   Grenze: Prüfung und Update sind nicht eine atomare SQL-Anweisung; zwischen beiden Schritten ist ein sehr kleines Zeitfenster.
9. **Rechte:** Nur Eigentümer privater Module (SQL-Filter auf `owner_user_id`; Seite zeigt sonst eine Sperrmeldung). Freigabeempfänger, Fremde, Administratoren (für fremde private Module) und globale Versionen erhalten 404 bzw. 403; globale Versionen bleiben unveränderlich.

## Verworfene Alternativen

- *HTML speichern und beim Ausgeben bereinigen (Allowlist-Sanitizer)*: ohne Bibliothek fehleranfällig; ein Parserfehler wäre ein XSS. Das eigene Markup wird dagegen nie als HTML interpretiert.
- *Externe Editor-Bibliothek (z. B. Quill, TipTap)*: ADR 0002 und zusätzlicher Angriffs-/Wartungsaufwand.
- *Neues Feld `definition_html`*: Bruch mit Karten-JSON-Vertrag und doppelte Pflege.
- *Eigenes Rich-Text-Feld je Frage*: Quizfragen bleiben Klartext (Prüfungsfragen sollen eindeutig sein).

## Folgen

- Bestehende Karten ändern ihr Aussehen nur, wenn ihr Text zufällig Markup enthält (Listen mit „- “ am Zeilenanfang, `**…**`); einzelne Zeilenumbrüche bleiben jetzt sichtbar erhalten.
- KI-Prompts und Bewertung sehen das Markup (lesbarer Text mit wenigen Zeichen); `RichText.ToPlainText` steht für eine Klartext-Sicht bereit.
- Der Editor ist an Blazor-Interaktivität gebunden (JavaScript für `contenteditable`); ohne JavaScript ist nur die Anzeige verfügbar.

## Leitplanken für Änderungen

- Neue Formatierungen: `RichText` (Regeln + Ausgabeliste), `rte.js` (Serialisierung), `RichTextEditor`, CSS `.rt`, `CardImportSchema.rich_text_rules`, MCP-Texte, Help-Seite und Tests **gemeinsam** ändern. Es darf nie HTML aus Benutzereingaben ungeprüft in eine Seite gelangen (`MarkupString` nur mit `RichText.ToHtml`).
- Jeder Schreibpfad ruft `CardJson.Validate` (inkl. `RichText.Validate`).
- Keine zweite Karten-/Quizpersistenz im Editor.

## Offene Punkte

- Sehr schwache Konfliktatomizität (siehe oben); bei Bedarf bedingtes `UPDATE … WHERE updated_at = …`.
- Der Editor ist im Browser mit Chrome (headless) geprüft; Safari/Firefox und echte Touchgeräte nicht.
- `document.execCommand` ist veraltet, aber in allen großen Browsern verfügbar; eine Ablösung wäre auf `rte.js` begrenzt.
- Lösungen bleiben im Karten-JSON sichtbar (ADR 0019) — für Zertifikate (Auftrag 3) ist eine eigene Integritätsentscheidung nötig; der Editor ändert daran nichts.

## Betroffener Code

`Domain/RichText.cs`, `Domain/Models.cs` (`CardJson.Validate`), `Services/CardRepository.cs`, `Services/QuizPoolService.cs` (`CopyCardQuestionsAsync`), `Api/ApiEndpoints.cs`, `Api/CardImportSchema.cs`, `LearnCards.McpServer/Program.cs`, `Components/Pages/ModuleEditor.razor`, `Components/Shared/RichTextEditor.razor`, `RichTextView.razor`, `QuizQuestionForm.razor`,
`FlashCardView.razor`, `ModulePage.razor`, `wwwroot/rte.js`, `wwwroot/app.css`, `wwwroot/templates/full-card-template.json`, `Components/Pages/Help.razor`, `tests/browser/editor-browser-check.js`.
