# 0001 — Architekturentscheidungen als ADR dokumentieren

- **Status:** Akzeptiert
- **Datum:** 2026-10-24

## Kontext

LearnCards wird zunehmend mit KI-Assistenten weiterentwickelt. Ohne explizit festgehaltene
Entscheidungen „reparieren“ Assistenten (und neue Teammitglieder) gern bewusst gewählte Eigenheiten
— etwa das Fehlen von NuGet-Paketen — oder brechen unsichtbare Verträge wie das Karten-JSON-Format.

## Entscheidung

Wir halten jede strukturprägende Entscheidung als ADR unter `docs/adr/` fest. Jede ADR enthält einen
Abschnitt **Leitplanken für Änderungen**, der direkt als Arbeitsanweisung für Änderungen dient.
[AGENTS.md](../../AGENTS.md) verweist KI-Assistenten auf diese ADRs.

## Konsequenzen

- Änderungen, die eine ADR berühren, aktualisieren oder ersetzen sie im selben Commit/PR.
- Geringer Zusatzaufwand, dafür nachvollziehbare Historie und zielgerichtetere KI-Änderungen.

## Leitplanken für Änderungen

- ADRs sind kurz (1 Seite), eine Entscheidung pro Datei.
- Alte ADRs nicht löschen; per Status „Ersetzt durch“ verketten.
