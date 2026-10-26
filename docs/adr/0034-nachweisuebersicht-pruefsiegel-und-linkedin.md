# 0034 — Nachweisübersicht, Prüfsiegel „Learny Verified“ und LinkedIn-Angaben

- **Status:** Akzeptiert
- **Datum:** 2026-10-26
- **Ergänzt:** [0021](0021-zertifikate-und-open-badges.md), [0032](0032-themenabschluss-clr-und-master-badge.md), [0033](0033-signierte-zertifikats-pdfs.md)

## Kontext

Lernende sollen ihre Abschlüsse zentral sehen, herunterladen, prüfen und — freiwillig — in ihr LinkedIn-Profil übernehmen können. Ein einheitliches Prüfzeichen soll die von Learny durchgeführte Verifikation erkennbar machen, ohne eine Zertifizierung durch Dritte zu suggerieren.

## Entscheidung

1. **„Meine Abschlüsse und Nachweise“** (`/achievements`, nur angemeldet, nur eigene Nachweise): Module und Themen mit Version, Nummer, Ausstellungs- und Abschlussdatum, Ergebnis, nachgewiesenen Kompetenzen, Status (gültig/widerrufen); Aktionen: Modulzertifikat bzw. Themenabschluss als signiertes PDF, CLR (JSON-LD), Open Badge/Master-Badge, „Zertifikat prüfen“ (`/verify/{id}`), „Auf LinkedIn hinzufügen“. Beim Öffnen werden erfüllte, noch nicht ausgestellte Themenabschlüsse nachgezogen (idempotent, ADR 0032). Es gibt keine öffentliche Liste privater Abschlüsse.
2. **Öffentliche Prüfseite** (`/verify/{id}`): zeigt Status, Nummer, Prüfsummen und — als vom Inhaber durch Weitergabe der Adresse freigegebenen Nachweis — Modul-/Themenname, Version, Ergebnis, Kompetenzen (nachgewiesen/nicht nachgewiesen/nicht ausreichend geprüft) und bei Themen die Module mit nachgewiesenen Kompetenzen, aus dem unveränderlichen Snapshot. Keine Lernendennamen, Antworten, Lerninhalte oder Chats.
3. **LinkedIn.** Die Aktion „Auf LinkedIn hinzufügen“ ist eine reine Eintragshilfe: Sie zeigt Name des Zertifikats, Aussteller (Learny), Zertifikats-ID, Ausstellungsmonat/-jahr und die Verifikationsadresse (`GET /api/certificates/{id}/linkedin`, nur Eigentümer) und öffnet erst auf ausdrücklichen Klick LinkedIns „Add to Profile“-Seite (`https://www.linkedin.com/profile/add?startTask=CERTIFICATION_NAME` mit Vorbelegung, `rel="noopener noreferrer"`, neuer Tab). LinkedIn dokumentiert, dass Links nicht mehr zertifikatsspezifisch angepasst werden; die Vorbelegung kann ignoriert werden — deshalb stehen die Werte zum Übernehmen daneben. Es gibt **keine** LinkedIn-API-Anbindung und **keine** automatische Veröffentlichung. Eine neutrale Freigabegrafik (PNG, 1200×627, ohne personenbezogene Daten) liegt bei.
4. **Prüfsiegel „Learny Verified – Digitally Signed Credential“** (`wwwroot/brand/`): SVG-Marke und -Wortmarke mit Hell-/Dunkelmodus (`prefers-color-scheme`) sowie getrennten Dunkelvarianten, PNG-Ausgaben (32/64/180/192/512, Apple-Touch-Icon, Wortmarke hell/dunkel, Freigabegrafik), Favicon, Web-App-Manifest, Siegel im Seitenkopf (Link auf die Prüfseite), in der Nachweisübersicht, auf der Prüfseite und als Vektor in den PDFs. Die PNGs entstehen mit `tools/brand/make_assets.py` aus derselben Geometrie. Das Siegel bestätigt die Learny-Prüfung — **keine** Zertifizierung durch 1EdTech oder LinkedIn; das steht auch in PDF, Hilfe und Freigabegrafik.

## Konsequenzen

- LinkedIn-Verhalten liegt außerhalb unserer Kontrolle; die Eintragshilfe bleibt auch ohne Vorbelegung brauchbar.
- Wer die Verifikationsadresse teilt, gibt Modul-/Themenname und Kompetenzstatus des Abschlusses preis (Entscheidung des Inhabers).
- Das Siegel wird in der Oberfläche statisch ausgeliefert; ein farbiges Siegel in fremden Kontexten (z. B. Social-Media-Vorschau) wird nur über die Freigabegrafik transportiert.

## Leitplanken für Änderungen

- Jeder Benutzer sieht nur seine eigenen Abschlüsse; keine Sammelansicht privater Nachweise.
- Nie automatisch veröffentlichen oder LinkedIn-Funktionen voraussetzen, die es nicht gibt.
- Das Siegel nie als Zertifizierung Dritter darstellen.
- Tests: `AchievementTests.cs`.

## Betroffener Code

- `Components/Pages/Achievements.razor`, `Verify.razor`, `Layout/MainLayout.razor`, `Components/App.razor`, `wwwroot/brand/*`, `wwwroot/manifest.webmanifest`
- `Services/Certificates/CertificateService.Topic.cs` (`LinkedInShare`), `Api/CertificateEndpoints.cs` (`/{id}/linkedin`), `tools/brand/make_assets.py`
