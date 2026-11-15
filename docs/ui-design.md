# UI-Design (ADR 0051)

- **Tokens:** `wwwroot/app.css` (Radien `--radius-*`, Abstände `--space-*`, Farben); keine Inline-Farben.
- **Shell:** Icon-Seitenleiste (mobil: untere Leiste mit „Mehr“), Kopfzeile mit Suche, Darstellung (Auto/Hell/Dunkel) und Profilmenü. Bedienung per `app.js` (`data-shell`), da das Layout statisch gerendert wird.
- **Dashboard:** `/dashboard` (Startseite nach Anmeldung): Weiterlernen, Fortschritt, zugewiesene Themen, Abschlüsse, KI-Assistent.
- **Stimmungsbilder (KI):** Jede KI-Figur zeigt in der Galerie fünf Stimmungskacheln (fehlende als Platzhalter, „unvollständig“-Kennzeichnung); die Stimmungsknöpfe im Profil und die Dashboard-Stimmung zeigen das passende Bild bzw. die Hauptvariante mit Hinweis (ADR 0058).
- **Avatar-Galerie:** Profil → „Meine Avatar-Galerie“ (ADR 0057): Kacheln mit Gruppen, Verwenden, Favorit, Umbenennen, Löschen (mit Bestätigung), Filter/Sortierung, Auswahl, Export (ZIP, optional AES-256), Import mit Vorschau; Vorschläge des Generators lassen sich einzeln oder gesammelt speichern.
- **Avatar:** Profil → „Mein Avatar“: **KI-Bildavatar (echtes Bild)** mit Stil (Comic realistisch/3D-Cartoon/Klassischer Comic), Quelle (Foto/Text/Foto und Text), mehreren Vorschlägen, Übernehmen/Verwerfen (ADR 0056); daneben der einfache SVG-Baukasten: Auswahl, Generator mit Beispielbeschreibungen (alle Stimmungen in der Vorschau), optional ein Foto als Inspiration (nur bei konfigurierter KI, JPG/PNG/WEBP bis 4 MB, Vorschau, entfernbar, nicht gespeichert, ADR 0055), ausblenden, zurücksetzen.
- **Lernkarten:** Tippen = umdrehen, Wischen links/rechts = nächste/vorherige Karte, Pfeiltasten, Leertaste; Schaltflächen als Alternative; mobil „⛶ Vollbild“ mit einblendbarer Steuerung.
- **Tests:** `node tests/browser/cards-gesture-unit.js` (Gestenauswertung), Integrationstests. Geräte-/Touch-Test auf echten Telefonen steht aus.
