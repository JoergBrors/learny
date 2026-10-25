# Entwickeln mit KI-Assistenten

Dieses Repository ist darauf vorbereitet, dass Änderungen gezielt von KI-Assistenten (Claude Code, Copilot, Codex u. a.)
umgesetzt werden. Der Rahmen besteht aus drei Ebenen:

| Ebene | Datei | Zweck |
|---|---|---|
| Einstieg | [AGENTS.md](../AGENTS.md) (`CLAUDE.md` verweist darauf) | Regeln, „Wo ändere ich was?“, Prüfschritte |
| Entscheidungen | [docs/adr/](adr/README.md) | *Warum* das System so ist, inkl. Leitplanken für Änderungen |
| Überblick | [architecture.md](architecture.md), [database-schema.md](database-schema.md), [local-development.md](local-development.md), [testing.md](testing.md) | Struktur, Datenmodell, Betrieb, Tests |
| Fachentscheidungen | [ADR 0015](adr/0015-freigaben-globale-lernmodule.md) | Freigaben, globale Module, Ergebniszuordnung |
| Prüfungen | [docs/reviews/](reviews/) | Prüfberichte (z. B. [Mehrbenutzer](reviews/2026-10-24-mehrbenutzer-pruefbericht.md)) |

Für einen vorgeschalteten Assistenten (ChatGPT-Projekt), der aus Wünschen fertige Aufträge für Codex/Claude Code erzeugt,
siehe [gpt-project-prompt.md](gpt-project-prompt.md).

## Einen guten Änderungsauftrag formulieren

```text
Ziel:        Was soll sich für Anwender/Systeme ändern?
Bereich:     Betroffene Komponente (z. B. Karten-JSON, MCP-Tool, Quiz)
ADRs:        Relevante ADRs (z. B. 0006, 0010) – oder „bitte selbst anhand AGENTS.md bestimmen“
Abnahme:     Woran erkenne ich, dass es funktioniert? (Build, manueller Ablauf, Beispiel-JSON)
Nicht ändern: Explizite Grenzen (z. B. „kein Schema-Bruch“)
```

Beispiel: *„Karten sollen ein optionales Feld `difficulty` (easy|medium|hard) bekommen. Beachte ADR 0006 und 0004.
Abnahme: Build ok, Import von `demoodule/demo.json` weiterhin möglich, Feld im Schema unter `/api/schema/import/cards` sichtbar.“*

## Erwarteter Ablauf des Assistenten

1. Relevante ADR(s) und betroffene Dateien lesen.
2. Kurz den Plan nennen, falls mehrere Dateien/Schichten betroffen sind.
3. Änderung umsetzen, `dotnet build LearnCards.slnx` ausführen und die Integrationstests starten (`dotnet run --project tests/LearnCards.IntegrationTests --no-build`, siehe [testing.md](testing.md)).
4. Dokumentation und ggf. ADR aktualisieren (neue Entscheidung → neue ADR, Index in [adr/README.md](adr/README.md) ergänzen).
5. Ergebnis berichten: geänderte Dateien, was geprüft wurde, was **nicht** geprüft werden konnte.

## Wann ist eine neue ADR nötig?

- Neue Abhängigkeit, neuer Persistenz-/Auth-/Transport-Mechanismus
- Änderung des Karten-JSON-Formats, der API-Verträge oder des Datenmodells (dann auch Live-Importschema, MCP-Beschreibungen, `wwwroot/templates/*.json`, `demoodule/*.json` und `Help.razor` im selben Auftrag aktualisieren; der Test „Beispiele, Templates, Schema, MCP, Hilfe“ schützt die Übereinstimmung; neue Textformatierungen zusätzlich in `RichText`, `rte.js` und `rich_text_rules`)
- Neue Deployment-Variante oder Änderung am Release-Prozess
- Abweichung von einer bestehenden Leitplanke

Reine Fehlerbehebungen, UI-Anpassungen und Texte brauchen keine ADR.

## Pflege

- ADRs sind unveränderliche Historie; Änderungen als neue ADR mit Verweis (siehe [adr/README.md](adr/README.md)).
- Ändert sich die Verzeichnisstruktur oder kommen Tests hinzu, [AGENTS.md](../AGENTS.md) im selben PR anpassen.
- Tests: paketfreie Integrationstests unter `tests/` ([testing.md](testing.md)). Neue Funktionen mit Benutzerbezug erhalten dort Prüfungen;
  ein Testframework als NuGet-Paket erfordert eine neue ADR ([ADR 0002](adr/0002-keine-nuget-pakete.md)).
