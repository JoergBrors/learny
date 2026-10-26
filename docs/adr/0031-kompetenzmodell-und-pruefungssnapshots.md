# 0031 — Kompetenzmodell, Prüfungssnapshots und kompetenzbasierte Modulzertifikate

- **Status:** Akzeptiert
- **Datum:** 2026-10-26
- **Ergänzt:** [0021](0021-zertifikate-und-open-badges.md) (Modulzertifikat), [0019](0019-einheitliches-quizsystem.md) (Bewertung bleibt im `QuizService`), [0030](0030-einheitliche-modul-und-themenversionierung.md) (Modulversionen)

## Kontext

Das Modulzertifikat (ADR 0021) bescheinigte nur „Modul bestanden“. Für Open Badges 3.0 und das CLR (ADR 0032) sollen konkrete Kompetenzen nachvollziehbar belegt werden — aber nur, wenn sie tatsächlich ausreichend geprüft wurden.

## Entscheidung

1. **Modell.** Ein Modul besitzt optional *Lernziele* (`id`, `text`) und *Kompetenzen* (`id`, `objective_id`, `name`, `description`, `pass_percent`, `min_questions`). Sie liegen in `module_competencies.definition_json` und sind Teil des Moduls und damit der Modulversion (Teil des Inhaltshashes; eingefrorene Kopien erhalten eine Kopie). Karten ordnen sich über `competencies[]` (Kompetenz-IDs) zu; im Karten-JSON optional (**fehlend = behalten, `[]` = entfernen**, wie `images`). Kette: Modul → Lernziel → Kompetenz → Lernkarte → Quizfrage (über die Karte) → Bewertungskriterium (`pass_percent`, `min_questions`, Methode `module_quiz`) → verifiziertes Prüfungsergebnis. Fragen ohne Karte zählen für keine Kompetenz. Bearbeiten: Moduleditor, `PUT /api/modules/{id}/competencies`, Modul-JSON (`learning_objectives`, `competencies`), nur der Eigentümer privater Module (Entwurf).
2. **Nachweis.** `CompetencyService.EvaluateAsync` ordnet die vom `QuizService` bewerteten Fragen (Karte, Punkte) den Kompetenzen zu — es bewertet nie selbst. Status je Kompetenz: `demonstrated` (≥ `min_questions` geprüft und ≥ `pass_percent`), `not_demonstrated`, `insufficiently_tested` (zu wenige Fragen). „Modul bestanden“ setzt nie Kompetenzen auf nachgewiesen. Module ohne Kompetenzmodell funktionieren unverändert.
3. **Prüfungssnapshot.** Bei jedem verifizierten, bestandenen Modulabschluss entsteht genau ein unveränderlicher Snapshot (`exam_snapshots`, UNIQUE je Ergebnis): kanonisches JSON `learny-exam-snapshot` mit Modul (Name, Version, Scope, Inhaltshash), Prüfung (Methode, Bestehensgrenze, Fragenanzahl, `criteria_version` = SHA-256 des Kompetenzmodells, Abschlusszeitpunkt), Ergebnis, Lernzielen und Kompetenznachweisen; Hash = SHA-256 über genau diese Zeichenfolge. **Keine** Lernendendaten, Antworten oder Fragetexte. Es gibt keinen Änderungspfad.
4. **Modulversion beim Abschluss.** Schließt der Eigentümer sein privates Modul ab, wird der aktuelle Stand bei Bedarf als Modulversion eingefroren (ADR 0030); Zertifikat und Snapshot tragen diese Versionsnummer (global: die globale Version). Bestehende Zertifikate bleiben unverändert.
5. **Open Badge 3.0.** `CertificateService.BuildCredential` ergänzt bei vorhandenem Snapshot die Standardstrukturen: `achievement.version`, `criteria` (id, narrative mit Bewertungskriterien), `alignment` (`targetType: ceasn:Competency`, `targetCode`, `targetUrl`), `resultDescription` (`resultType: Percent`, `requiredValue`, `valueMin/Max`), `credentialSubject.result` (je Kompetenz mit `resultDescription`) und `evidence` (Typ `Evidence`, URL `/api/public/certificates/{id}/evidence`, Snapshot-Hash). **Bescheinigt werden nur nachgewiesene Kompetenzen**; die übrigen stehen nur im Snapshot. Signatur und Widerruf wie ADR 0021; `snapshot_id`/`snapshot_hash` am Zertifikat.
6. **Öffentlicher Nachweis.** `GET /api/public/certificates/{id}/evidence` liefert den unveränderten Snapshot (Zertifikats-ID ist die Freigabe durch den Inhaber; enthält Modul- und Kompetenznamen, nichts Weiteres).

## Konsequenzen

- Aussagekraft hängt von der Modellierung ab (Karten ↔ Kompetenzen, genug Fragen); fehlende Abdeckung führt zu „nicht ausreichend geprüft“.
- Kartengebundene Fragen des Quiz zählen; reine Pool-Fragen ohne Karte sind nicht zuordenbar.
- Konformität: Die Strukturen wurden gegen die Pflichtfelder und Enums der offiziellen 1EdTech-JSON-Schemas (Open Badges 3.0.3) geprüft; es gibt keine offizielle 1EdTech-Zertifizierung (siehe ADR 0032).

## Leitplanken für Änderungen

- Bewertung nur im `QuizService`; Kompetenzen nur aus dessen verifiziertem Ergebnis.
- Snapshots und ausgestellte Credentials nie ändern; Snapshot ohne personenbezogene Daten und Antworten.
- Nur nachgewiesene Kompetenzen im Credential. Neue Felder im Karten-JSON optional (ADR 0006).
- Beispiele/Schema/Hilfe/MCP-Texte mitziehen (`CardImportSchema`, `TransferSchema`, `Help.razor`).
- Tests: `CompetencyTests.cs`.

## Betroffener Code

- `Domain/Competencies.cs`, `Services/CompetencyService.cs`, `Services/Certificates/CertificateService.cs`, `Services/QuizService.cs`
- `Services/ModuleTransferService.cs` (Export/Import/Klonen), `Services/CardRepository*.cs` (Kartenfeld, Kopien, Inhaltshash)
- `Api/TopicEndpoints.cs`, `Api/CertificateEndpoints.cs`, `Components/Pages/ModuleEditor.razor`
