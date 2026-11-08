# ADR 0051: Immersives Dashboard, Avatar-System/-Generator und Touch-Bedienung der Lernkarten

**Status:** Vorgeschlagen

## Kontext
Neues GUI (Violett-Palette, schmale Icon-Seitenleiste, Dashboard als Startseite), ein dekorativer Avatar als Lernbegleiter sowie mobile Bedienung der Lernkarten (Wischen, Vollbild).

## Entscheidung
1. **Design-Tokens** (Radien, Abstände, Farben) nur als CSS-Variablen in `wwwroot/app.css`; Hell/Dunkel/System über `data-theme`/`data-theme-mode`.
2. **Shell ohne Blazor-Ereignisse:** Das Layout wird statisch gerendert (nur Seiten sind `InteractiveServer`). „Mehr“, Profilmenü und Darstellungsumschalter laufen daher per JavaScript (`app.js`, `data-shell`). Die Darstellung wird im Layout nur lokal im Browser gemerkt; die im Profil gespeicherte Wahl gilt weiter beim Speichern dort.
3. **Dashboard** (`DashboardService`) zeigt nur berechenbare Daten aus bestehenden Diensten; Berechtigungen wie sonst (`ResolveModuleAccessAsync`, eingeschränkte Lernende nur zugewiesene Inhalte). Keine erfundenen Werte.
4. **Avatar:** rein dekorativ, selbst gezeichnetes SVG, gespeichert in `user_preferences.avatar`/`avatar_hidden`. Eigene Avatare (`custom:…`) bestehen nur aus geprüften Hexfarben und festen Listen (Frisur, Accessoire) — nie freies SVG/Bilder. Der **Generator** (`AvatarGeneratorService`) lässt die KI nur diese Parameter wählen (Beschreibung wird nicht gespeichert, nur als Beschreibung behandelt); ohne KI greift eine Stichwortauswertung (ADR 0008). Keine Fotos, keine externen Avatardienste. Alle Stimmungen ergeben sich aus denselben Parametern.
5. **Touch:** `wwwroot/cards.js` (Pointer Events, keine Bibliothek). Vertikales Scrollen hat Vorrang; Wechsel nur bei eindeutig horizontaler Bewegung auf der Kartenfläche; jede Geste hat Schaltflächen- und Tastaturalternative; Vollbild blendet Shell/Steuerung aus und lässt sie einblenden.

## Folgen
Keine API-/Karten-JSON-Änderung, keine neuen Pakete. Echte Geräte-Tests stehen aus (nur Logik-Tests der Gestenauswertung und Integrationstests).
