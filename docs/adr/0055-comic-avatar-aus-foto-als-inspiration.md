# ADR 0055: Comic-Avatar mit Foto als Inspiration und erweiterter Avatar-Baukasten

**Status:** Akzeptiert und umgesetzt (ergänzt ADR 0051; ADR 0051 Satz „Keine Fotos“ gilt weiter für das **Ergebnis** — der Avatar ist nie ein Foto —, nicht mehr für die optionale **Eingabe**)

## Kontext
Der Avatar-Generator (ADR 0051) lässt die KI Parameter eines festen SVG-Baukastens wählen (Hautton, Haar, Frisur, Kleidung, Accessoire). Nutzer berichteten, dass unterschiedliche Beschreibungen häufig sehr ähnliche Gesichter ergeben.

**Ursache (im Code belegt):**
1. Der Baukasten hatte **eine einzige feste Gesichtsvorlage** (immer dieselbe Ellipse, Augen, Mund); es variierten nur Farben, fünf Frisuren und fünf Accessoires. Gesichtsform, Bart, Glatze, Stachel-/Kurzhaarfrisuren, Afro oder Scheitel waren nicht ausdrückbar.
2. Der KI-Prompt kannte nur diese wenigen Felder und gab keine Anweisung, sie gezielt und abwechslungsreich zu nutzen; die Standardwerte (`short`, `none`) lagen nahe.
3. Der Offline-Fallback kannte nur wenige Schlüsselwörter und fiel sonst auf einen festen Standardavatar zurück.

Es gibt weder Caching noch feste Zufallsparameter (die Completion läuft mit den Standardparametern des Dienstes); die Vielfalt war durch Baukasten und Prompt begrenzt, nicht durch Zwischenspeicherung.

## Entscheidung
1. **Baukasten erweitert:** `Gesichtsform` (`oval|round|square|long`), `Bart` (`none|stubble|short|full`) und Frisuren `buzz|spiky|sidepart|afro|bald`. Kennung: weiterhin `custom:Haut|Haar|Frisur|Kleidung|Accessoire`, **optional** um `|Gesicht|Bart` ergänzt (7 Teile). Die Standardform (oval, ohne Bart) wird im alten 5-teiligen Format gespeichert; alle bisherigen Kennungen bleiben gültig, es gibt **keine Datenbankänderung** (`user_preferences.avatar` ist Text). Weiterhin nur geprüfte Hexfarben und feste Listen — nie freies SVG oder Bilder.
2. **Foto als optionale Eingabe:** Ein Foto dient nur als Inspiration; das Ergebnis ist eine eigenständige Comicfigur aus dem Baukasten. Verarbeitung nur über den bestehenden `OpenAiClient` (Chat Completions mit Bildteil `image_url` als Data-URL, Detailstufe `low`; Modell aus `OPENAI_MODEL`, Standard `gpt-4o-mini` ist bildfähig). Keine neue Abhängigkeit, kein neuer Dienst, keine neue Konfiguration.
3. **Datenschutz:** Das Foto existiert nur als Byte-Array im Arbeitsspeicher der Profilsitzung und im einen Anfrageinhalt; es wird **nie** gespeichert (weder Datenbank noch Dateisystem noch Cache), nie protokolliert (keine Bild-/Base64-Daten in Logs oder Fehlermeldungen; Fehlerprotokolle nennen nur den Ausnahmetyp) und nach „Übernehmen“, „Verwerfen“ oder „Foto entfernen“ verworfen. Gesendet wird es erst nach **ausdrücklicher Einwilligung** (Kontrollkästchen) und nur zur einmaligen Auswertung beim konfigurierten KI-Dienst.
4. **Validierung:** `AvatarPhoto.DetectMime` prüft den **tatsächlichen Inhalt** (Signatur und Struktur von JPEG/PNG/WEBP, abgeschnittene Dateien, 256 Byte bis 4 MB); Dateiname und vom Browser gemeldeter Typ zählen nicht. Die Prüfung läuft im Dienst (`AvatarGeneratorService`) vor jedem KI-Aufruf — nicht nur in der Oberfläche. Pixeldaten werden nicht dekodiert (keine Bibliothek, ADR 0002); die Prüfung ist eine Struktur-, keine Vollvalidierung. Die Antwort der KI wird wie bisher nur als geprüfte Parameter übernommen.
5. **Keine Identifizierung:** Der Prompt verbietet Benennen/Identifizieren sowie Schätzungen zu Alter, Herkunft, Gesundheit oder Eigenschaften, behandelt Text im Bild und den Zusatzwunsch nie als Anweisung und lässt die Zusammenfassung nur Comic-Merkmale nennen. Es gibt keine Gesichtserkennung und keine eigenen biometrischen Auswertungen.
6. **Fallback (ADR 0008):** Ohne KI-Konfiguration wird der Foto-Upload nicht angeboten; die Text-Generierung bleibt wie bisher (Stichwortauswertung, jetzt mit Bart, Glatze, Gesichtsform, Frisurwörtern). Schlägt der KI-Aufruf mit Foto fehl oder liefert er ungültige Werte, entsteht — wenn eine Beschreibung vorhanden ist — der Vorschlag aus dem Text mit Hinweis „Das Foto konnte nicht ausgewertet werden“; ohne Beschreibung erscheint eine verständliche Meldung.
7. **Prompt:** Das Schema nennt die erweiterten Felder; die Anweisung verlangt, Merkmale gezielt zu wählen und die Bandbreite der Werte zu nutzen. Der Zusatzwunsch hat bei Foto-Widerspruch Vorrang.

## Folgen
- Keine API-/Karten-JSON-Änderung, keine Datenbank- oder Konfigurationsänderung, keine neuen Pakete.
- Die Darstellung der Altkennungen bleibt unverändert (oval, ohne Bart).
- Der Prompt-Teil mit dem Foto ist per Stub-Test abgedeckt (Anfrageinhalt, Fallbacks, Ablehnung); die **Qualität der Merkmalsübertragung** hängt vom KI-Modell ab und wurde mit echten Fotos **nicht** automatisiert geprüft.
- Das Foto verlässt Learny Richtung KI-Dienst (Drittland-/Auftragsverarbeitungsfragen des Betreibers beachten); die Einwilligung im Dialog ersetzt keine Datenschutzdokumentation des Betreibers.

**Nachtrag (ADR 0056):** Die Chat-Merkmalsextraktion mit Foto bleibt nur im SVG-Modus erhalten (wenn keine Bildgenerierung verfügbar ist). Mit Bildgenerierung gelten Einwilligung, Verarbeitung im Arbeitsspeicher und Nicht-Speichern des Fotos unverändert; der externe Dienst unterliegt eigenen Aufbewahrungsbedingungen.

**Nachtrag (ADR 0057):** Auch die Galerie und der Export enthalten nie Referenzfotos; nur ausdrücklich gespeicherte Avatare werden aufbewahrt.
