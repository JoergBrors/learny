# 0043 — Transaktionsmodell der Einladungseinlösung

- **Status:** Akzeptiert (PoC-Stand 2026-11-08; die Integration in Learny wird aus diesem Branch geplant, offene Punkte: [poc-abschluss.md](../entra-external-id/poc-abschluss.md)) – im PoC umgesetzt, per Test und live bestätigt (Einlösung genau einmal)
- **Datum:** 2026-11-07
- **Bezug:** [0004](0004-datenbank-abstraktion.md), [0026](0026-betriebsmodus-fail-closed.md), [0042](0042-einladungspflichtige-external-id-registrierung.md)

## Kontext

Die Einlösung legt Benutzer, Identität, Profil und Inhaltsrechte an und verbraucht die Einladung. `IDatabase` (ADR 0004) bietet keine Transaktion über mehrere Anweisungen; jede Anweisung wird einzeln und serialisiert ausgeführt. Mehrere Prozesse (oder Verbindungen) können dieselbe Datenbank nutzen.

## Entscheidung

1. **Keine neue Transaktionsschnittstelle im PoC.** Stattdessen gilt das Modell „atomarer Anspruch + Kompensation + fail-closed“:
   1. Der **Anspruch** auf die Einladung ist eine einzelne bedingte Anweisung (`UPDATE … WHERE invitation_id AND token_hash AND status='pending' AND revoked_at IS NULL AND expires_at>now`). Genau eine Zeile = Gewinner. Dieselbe Bedingung schützt gegen **Rotation** (`token_hash` ändert sich) und **Widerruf** (`status`).
   2. Danach werden Benutzer, Identität (`PRIMARY KEY (provider, subject)` verhindert Doppelanlage), Profil und Rechte (`INSERT … SELECT` in einer Anweisung) angelegt.
   3. Bei einem Fehler: Löschen des Benutzers (Kaskade entfernt Identität, Profil, Rechte), danach Zurücksetzen der Einladung auf `pending` nur, wenn `accepted_user_id` noch dem eigenen Anspruch entspricht.
   4. Scheitert die Kompensation oder bricht der Prozess ab, bleibt die Einladung **verbraucht** (`accepted`). Ein Benutzer ohne `user_profiles`-Zeile hat keinen Zugriff (`ContentAccess` prüft das Profil). Der Administrator stellt neu aus; Bereinigung ist eine Betriebsaufgabe.
2. **Für die Learny-Integration wird eine Transaktions-/Batch-Methode in `IDatabase` empfohlen** (z. B. `ExecuteBatchAsync` für SQLite und PostgreSQL, alles-oder-nichts). Sie ersetzt Schritt 3–4, **nicht** den bedingten Anspruch in Schritt 1. Das ist eine Änderung an ADR 0004 und braucht eine eigene Entscheidung; sie wurde bewusst **nicht** umgesetzt.

## Konsequenzen

- Kein Zustand, in dem eine Einladung zweimal erfolgreich ist; ein Teilfehler kann nur zu „zu streng“ führen (Einladung verbraucht, kein Zugriff), nie zu „zu offen“.
- Verwaiste Benutzerzeilen ohne Profil sind möglich, wenn ein Prozess zwischen Schritt 2 und 3 stirbt; sie sind wirkungslos und bereinigbar (`users` ohne `user_profiles` und ohne Rolle).
- Hohe Parallelität ist über die serialisierende Datenbankschicht getestet, nicht über echte Mehrfach-Prozess-Last auf PostgreSQL.

## Leitplanken für Änderungen

- Der Anspruch bleibt die erste schreibende Anweisung der Einlösung und trägt alle Gültigkeitsbedingungen.
- Kein „erst anlegen, dann verbrauchen“; kein Zurücksetzen verbrauchter Einladungen außer der Kompensation desselben Anspruchs.
- Rotation und Widerruf nur mit bedingtem `UPDATE` auf `status='pending'`.

## Betroffener Code

- `poc/InviteRegistration/InvitationService.cs` (`RedeemAsync`, `CompensateAsync`, `RotateAsync`, `RevokeAsync`), `poc/InviteRegistration.Tests/*`

## Nachtrag 2026-11-08 (Prüfung)

Die Entscheidung wurde erneut geprüft und ist **unverändert**. Der Livetest gegen den External Tenant (2026-11-07) bestätigte die einmalige Einlösung mit echtem OIDC-Ablauf; die Mail-Härtung ändert den Einlösepfad nicht. Weiterhin offen: eine Transaktions-/Batch-Methode in `IDatabase` für die Learny-Integration.

## Nachtrag 2026-11-08 (Portal)

Entscheidung unverändert. `module_grants.source_invitation_id` ist jetzt nullbar (administrative Zuweisungen haben keine Einladung; zusätzlich `granted_by`). Der atomare Anspruch, die Kompensation und der fail-closed-Zustand sind unberührt; neu getestet: zwei Portalinstanzen auf derselben Datenbankdatei, 12 parallele Einlösungen → genau ein Erfolg.
