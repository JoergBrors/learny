# UI-Design (ADR 0051)

- **Tokens:** `wwwroot/app.css` (Radien `--radius-*`, Abstände `--space-*`, Farben); keine Inline-Farben.
- **Shell:** Icon-Seitenleiste (mobil: untere Leiste mit „Mehr“), Kopfzeile mit Suche, Darstellung (Auto/Hell/Dunkel) und Profilmenü. Bedienung per `app.js` (`data-shell`), da das Layout statisch gerendert wird.
- **Dashboard:** `/dashboard` (Startseite nach Anmeldung): Weiterlernen, Fortschritt, zugewiesene Themen, Abschlüsse, KI-Assistent.
- **Avatar:** Profil → „Mein Avatar“: Auswahl, Generator mit Beispielbeschreibungen (alle Stimmungen in der Vorschau), ausblenden, zurücksetzen.
- **Lernkarten:** Tippen = umdrehen, Wischen links/rechts = nächste/vorherige Karte, Pfeiltasten, Leertaste; Schaltflächen als Alternative; mobil „⛶ Vollbild“ mit einblendbarer Steuerung.
- **Tests:** `node tests/browser/cards-gesture-unit.js` (Gestenauswertung), Integrationstests. Geräte-/Touch-Test auf echten Telefonen steht aus.
