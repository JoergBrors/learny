# 0023 — Geheimnisse und Badge-Schlüssel unter IIS: Ablage außerhalb des Webroots, Initialisierungsmarker, Fail-closed

- **Status:** Akzeptiert (IIS-Praxistest steht aus)
- **Datum:** 2026-10-26
- **Ergänzt:** [0009](0009-deployment-wege.md), [0021](0021-zertifikate-und-open-badges.md), [0014 (Vorschlag)](../reviews/2026-10-24-mehrbenutzer-pruefbericht.md)
- **Bezug:** [0002](0002-keine-nuget-pakete.md), [0005](0005-authentifizierung.md)

## Kontext

Das IIS-Deployment legte die gerenderte `.env` im Webroot ab und spiegelte das Webroot mit `robocopy /MIR` — dabei wären `data\` (Datenbank, Data-Protection-Schlüssel, Badge-Schlüssel) bei jedem Deployment gelöscht worden. Ein fehlender Badge-Schlüssel führte zur stillen Neuerzeugung und machte alle ausgestellten Badges unprüfbar. Zusätzlich scheiterte der PEM-Import unter IIS (HTTP 500.30, CNG).

## Entscheidung

1. `.env` und Badge-Schlüssel liegen unter `IIS_CONFIG_ROOT` (Standard `%ProgramData%\LearnCards\<Site>`) in `config\` bzw. `keys\`, nie im Webroot; `ENV_FILE` (App-Pool-Umgebungsvariable) zeigt auf die `.env`, `BADGE_SIGNING_KEY_FILE` steht in der `.env`.
2. NTFS: Vererbung entfernt; SYSTEM, Administratoren, Deploy-Konto Vollzugriff; App-Pool nur Lesen (Schlüsselverzeichnis schreibbar nur bis zur Erstinitialisierung). Webroot: App-Pool nur Lesen/Ausführen, Schreiben nur `data`, `logs`.
3. `robocopy /MIR /XD data logs`; Backups schließen `.env`/`*.pem` aus.
4. **Initialisierungsmarker** `badge-signing-key.pem.initialized` (Schlüssel-ID, kein Geheimnis): Ein fehlender Schlüssel wird nur ohne Marker (Neuinstallation) erzeugt — atomar (temporäre Datei, Move ohne Überschreiben), danach neu geladen und per Signatur-Selbstprüfung getestet. Mit Marker, bei abweichender Schlüssel-ID oder wenn bereits Zertifikate in der Datenbank liegen, bricht der Start ab (**fail-closed**, keine stille Rotation).
5. Schlüsselimport über `ECParameters` (kein NCrypt-Schlüsselspeicher), „Benutzerprofil laden“ = True; Request Filtering sperrt `.env`, `.pem`, `.key`, `.initialized`, `.db` (`.config` sperrt IIS bereits standardmäßig), `data`, `logs`, `keys`.
6. Das Deployment prüft Rechte vor dem Start, danach HTTP (sensible Pfade) und die Schlüssel-ID gegen den Marker; Befunde lassen das Deployment fehlschlagen.

## Folgen

- Bestehende Schlüssel werden kopiert, nie gelöscht oder rotiert; ausgestellte Badges bleiben gültig. Rotation bleibt ein bewusster, manueller Vorgang.
- Wer den Marker (nicht den Schlüssel) verliert, kann den Schlüssel nur mit Datenbankprüfung ersetzen — der Marker ist daher zu sichern.
- Ein echter Test unter IIS-App-Pool-Identität ist erst nach dem ersten Deployment möglich.
