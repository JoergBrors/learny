# 0054 — Lern- und Prüfungsvorschau für Content-Co-Autoren

- **Status:** Angenommen und umgesetzt (2026-11-14)
- **Datum:** 2026-11-14
- **Ergänzt:** [0052](0052-themenbezogene-co-author-berechtigungen.md), [0053](0053-modulbezogene-co-author-berechtigungen-und-vererbung.md). Berührt, aber nicht gelockert: [0015](0015-freigaben-globale-lernmodule.md), [0029](0029-themenzugriff-pflichtzuweisungen-und-themenadministration.md), [0019](0019-einheitliches-quizsystem.md), [0027](0027-themen-lernpfade-versionierung-und-abschlusspruefungen.md).

## Kontext

Eigentümer und Co-Autoren (ADR 0052/0053) bearbeiten Entwürfe, haben aber bewusst **kein Lernrecht** (ADR 0015/0029): die Lernseite `/module/{id}` und das reguläre Quiz prüfen
`ResolveModuleAccessAsync` (Eigentum, Lernfreigabe, globale Veröffentlichung, eingefrorene Themenkopie). Das Ergebnis: Co-Autoren konnten ihren Entwurf nicht ausprobieren, und die Themenansicht des
Entwurfs bot einen „Lernen“-Link an, der für sie in „Modul nicht gefunden“ endete. Gewünscht ist ein **Testmodus**, der das Lernerlebnis abbildet, ohne Lernrechte zu verleihen und ohne etwas zu speichern.

## Entscheidung

1. **Der Vorschaumodus ist ein eigener, begrenzter Zugriffspfad — kein Lernrecht.** Er ändert weder `ResolveModuleAccessAsync` noch `CanLearnTopicModuleAsync`, verleiht keine Freigabe, Zuweisung oder
   Pflicht und gilt nur für den **Entwurfsstand** (Module: aktueller Stand der Karten und des Fragenpools; Themen: Themenentwurf). Veröffentlichte Versionen und eingefrorene Kopien bleiben unberührt.
2. **Berechtigung** — ausschließlich serverseitig, bei jedem Zugriff gegen die Datenbank, im `TopicService` (`TopicService.Preview.cs`):
   - **Modulvorschau** = Bearbeitungsrecht am Modul (`GetModuleEditAccessAsync` aus ADR 0053): Eigentümer, direkter Modul-Co-Author oder aus einem Themen-Co-Author abgeleitet — einschließlich der
     **Schutzregel bei Mehrfachzuordnung** (Modul auch in einem Thema ohne Recht → keine Vorschau durch Themenrecht), des Ausschlusses eingeschränkter Lernprofile und des Entzugs.
   - **Themenvorschau** = Eigentümer oder Co-Author **dieses** Themas (`RoleForAsync`), Thema nicht archiviert. Darin sind nur die Module testbar, an denen die Modulvorschau zulässig ist.
     Ein **direktes Modulrecht gewährt keine Themenvorschau**; ein Themenrecht gewährt keine Vorschau fremder Module oder Themen.
3. **Gleiche Engine, keine Persistenz.** Start und Bewertung laufen durch **dieselbe** Quiz-Engine (`QuizService.StartSessionCoreAsync`, `GradeSessionAsync`; ADR 0019: keine zweite Engine), im Testmodus aber:
   - Sitzungen leben nur im **Arbeitsspeicher** (`QuizService.Preview.cs`: an den Handelnden gebunden, 2 Stunden, höchstens 20 je Benutzer, einmalig abgebbar) — kein `quiz_sessions`-Eintrag;
   - die Bewertung gibt das Ergebnis zurück und **speichert nichts**: kein `quiz_results`, keine Statistik, kein Prüfungsversuch (`topic_exam_attempts`), kein Prüfungssnapshot, kein Zertifikat, kein Themenabschluss, kein `user_card_states`;
   - KI-generierte Fragen sind flüchtig (`persist: false`); der Pool wird nie verändert; es gelten weder die Wiederholungsgrenze noch die „gecheckten Karten“ (alle aktiven Karten), die Frageanzahl, die Typen,
     die Bestehensgrenze und die Anzeige der Lösungen folgen der tatsächlichen Konfiguration; der Offline-Fallback (ADR 0008) bleibt.
   - Karten-Markierungen („Check“, „Wiederholen“) der Lernvorschau bestehen nur im Zustand der geöffneten Seite. Der KI-Chat ist in der Vorschau nicht verfügbar (er speichert einen Verlauf).
4. **Prüfungsstruktur des Themenentwurfs** (`StartPreviewModuleExamAsync`, `StartPreviewFinalExamAsync`): Modul- und Abschlussprüfung werden mit der **Entwurfskonfiguration** aus den Entwurfsfragenpools der
   zulässigen Module (und den eigenen Themenfragen) zusammengestellt — ohne Freischaltungslogik, Versuchsbindung und Fortschritt. Module mit Mehrfachzuordnung ohne Recht werden aus der Abschlussprüfung
   **ausgeschlossen** (mit Hinweis, ohne Inhalte oder Namen fremder Themen offenzulegen).
5. **Entzug wirkt sofort, auch in offenen Vorschauen.** Jede Aktion (Laden, Weiterblättern, Quizstart, Abgabe) prüft das Recht neu; bei Entzug erscheint „Dein Zugriff auf diese Vorschau wurde entzogen.“, die
   Karten werden nicht mehr angezeigt, die Sitzung wird verworfen, es gibt keine Lösungen und kein Ergebnis. Die **Abgabe** prüft Modul- bzw. Themenrecht **und** Zugehörigkeit des Moduls zum Entwurf erneut
   (`SubmitPreviewAsync`). Es gibt keine Zugriffstokens und keine gemerkten Rollen; die Sitzungs-ID ist an den angemeldeten Benutzer gebunden und autorisiert allein nichts. Direkte Modulrechte bleiben nach einem
   Themenentzug bestehen (ADR 0053). *Restfenster:* wie in ADR 0053 Punkt 6 (Auflösung → einzelne Operation).
6. **Keine neuen Endpunkte.** Die Vorschau ist eine Blazor-Funktion (`/module/{id}/preview`, `/topics/{id}/preview`); es gibt **keine REST- und keine MCP-Schnittstelle**, die regulären Lern- und Quiz-Endpunkte werden
   nicht für Co-Autoren geöffnet, MCP-Tokens erhalten keinen Zugriff, und der Benutzer stammt nie aus Request-Daten. Fehlermeldungen offenbaren keine fremden Inhalte (einheitlich „nicht gefunden“/„entzogen“).
7. **Oberfläche:** Themenentwurf (Editor und Themenseite): „🧪 Vorschau testen“; im Entwurf zeigt die Themenseite für Co-Autoren statt des funktionslosen „Lernen“-Links „🧪 Testen“ (der Eigentümer behält „📖 Lernen“
   und kann zusätzlich testen). Modul-Editor: „🧪 Als Lernender testen“. Reiter „Gemeinsam bearbeitet“: „🧪 Testen“. Die Vorschau trägt überall das Banner/Abzeichen „Testmodus“ und führt Bearbeiten, Testmodus und
   reguläres Lernen sichtbar getrennt.
8. **Keine Schemaänderung.**

## Konsequenzen

- Co-Autoren und Eigentümer können Inhalte und Prüfungen vor der Veröffentlichung ausprobieren; fehlerhafte Fragen und Lösungen fallen vorab auf.
- Weil Vorschau und reguläres Lernen dieselbe Bewertungslogik nutzen, bleibt das Verhalten vergleichbar; dafür trägt `StartSessionCoreAsync`/`GradeSessionAsync` zwei zusätzliche Verzweigungen (Testmodus), die Tests absichern.
- Sitzungen im Arbeitsspeicher gehen beim Neustart des Dienstes verloren (die Vorschau beginnt dann neu) und sind nicht über mehrere Instanzen geteilt — bewusst, die Vorschau ist flüchtig.
- **Bekannte Grenzen:** nur Karteikarten-Modus (kein Folienmodus), kein KI-Chat; Bilder in Karten werden über den Bild-Proxy (`/api/images/{id}`, ADR 0018) mit dem regulären Kartenrecht geladen und erscheinen Co-Autoren daher unter Umständen
  nicht — das gilt unverändert auch im Modul-Editor und ist keine Erweiterung dieses ADR.

## Leitplanken für Änderungen

- Die Vorschau darf **nie** `SaveSessionAsync`, `SaveQuizResultAsync`, `SaveUserCardStateAsync`, Versuchs-/Zertifikats-/Snapshot-Pfade oder Poolschreibungen aufrufen; neue Lernfunktionen im Testmodus brauchen ausdrücklich keine Persistenz.
- Berechtigung nur über `GetModuleEditAccessAsync`/`RoleForAsync` im `TopicService`; nie aus Request-Daten, Tokens oder gemerkten Rollen; jeder sensible Zugriff und die Abgabe prüfen neu.
- Kein neuer REST-/MCP-Pfad ohne neue ADR; reguläre Lernendpunkte und `ResolveModuleAccessAsync` bleiben unverändert.
- Mitziehen: `ModulePreview.razor`, `TopicPreview.razor`, `TopicService.Preview.cs`, `QuizService.Preview.cs`, `PreviewTests.cs`, Hilfe, README, `docs/testing.md`, `tests/browser/`.

## Betroffener Code

- `Services/TopicService.Preview.cs`, `Services/QuizService.Preview.cs`, `Services/QuizService.cs` (`StartSessionCoreAsync`, `GradeSessionAsync` mit Testmodus)
- `Components/Pages/ModulePreview.razor`, `TopicPreview.razor`, `TopicPage.razor`, `TopicEditor.razor`, `ModuleEditor.razor`, `Home.razor`; `Components/Shared/QuizRunner.razor`, `FlashCardView.razor`
- `tests/LearnCards.IntegrationTests/PreviewTests.cs`, `PostgresReadinessTests.cs`, `tests/browser/preview.mjs`
