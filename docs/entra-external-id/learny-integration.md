

## Stand der Integration in Learny (Branch `feature/entra-integration`)

- **Phase A umgesetzt (2026-11-08, [ADR 0048](../adr/0048-multi-issuer-authentifizierung-und-bestandsschutz.md)):** `OidcService` ist parametrisierbar (Workforce unverändert), `ExternalOidcService` und `/auth/external/*` als zweiter Anbieter (nur bekannte Identitäten, Anlage erst mit Einladung in Phase B), `ExternalIdSettings` (`EXTID_*`, fail-closed), `OidcStateStore`/`oidc_consumed_states`, Bestandsschutz-Tests. Offen: PostgreSQL-Prüfung (Phase F), Workflow-Secrets `DEPLOY_EXTID_*` (Phase C).
- Phasen B bis G: siehe Auftrag und [poc-abschluss.md](poc-abschluss.md), Abschnitt 7.

- **Phase B umgesetzt (2026-11-08, [ADR 0049](../adr/0049-einladungs-und-lernberechtigungsmodell.md), [ADR 0048](../adr/0048-multi-issuer-authentifizierung-und-bestandsschutz.md) akzeptiert):** Schema (`user_profiles`, `invitations*`, `invitation_events`, `module_grants`, `user_identities.kind`), `IDatabase.ExecuteBatchAsync` (atomare Transaktion für SQLite und PostgreSQL), `InvitationService` (erstellen, widerrufen, rotieren, auflisten, atomare Einlösung), `InviteEvents`, `LearnerAdminService`, Lernprofile im Zugriffsentscheid, `GET /invite/{token}` und Einlösung im External-Callback. Getestet auf SQLite (94 neue Prüfungen, 1634 insgesamt). **PostgreSQL nicht live geprüft (offen).** Noch keine Oberfläche, kein Versand, keine OTP-Extension (Phase C).

## Schnittstellen für Phase C (Admin-Oberfläche, Versand, OTP, Timeline)

Phase C baut ausschließlich auf diesen Diensten auf (keine Logik aus dem PoC kopieren):

| Aufgabe | Dienst und Methoden |
|---|---|
| Einladung erstellen/verwalten | `InvitationService.CreateAsync(actor, email, profile, moduleIds, topicGrants, meta)`, `RevokeAsync`, `RotateAsync`, `ListAsync(actor, InvitationFilter)`, `GetInfoAsync`; Ausnahmen `UnauthorizedAccessException`, `ArgumentException`, `AccountExistsException` |
| Einladungsversand (Mail) | `InvitationService.RotateAsync`/`CreateAsync` liefern den Link einmalig (nie speichern); Versandstatus in `invitation_deliveries` (Tabelle vorhanden, Zustandsautomat nach ADR 0044 folgt in Phase C: `InvitationDeliveryService` aus dem PoC auf `ExecuteBatchAsync` portieren, Mailer hinter `IInvitationMailer`) |
| Benutzer-/Lernendenverwaltung | `LearnerAdminService.ListExternalLearnersAsync`, `GetOverviewAsync(actor, userId)`; Benutzerstatus/Rollen weiter über `UserService` (`SetStatusAsync`) |
| Modulzuweisungen | `LearnerAdminService.AssignModuleAsync`, `RevokeModuleAsync`, `ListModuleGrantsAsync` |
| Themen/Pflichtschulungen | `LearnerAdminService.AssignTopicAsync(actor, userId, topicId, version, mandatory, dueAt)`, `RevokeTopicAssignmentAsync`; Quelle der Wahrheit `TopicService` (ADR 0029) |
| Ereignis-Timeline | `InviteEvents.ListAsync(invitationId?, userId?, take)` (Typkatalog `InviteEventTypes`; Versandereignisse `SendRequested`/`Submitted`/`SendFailed` sind angelegt, aber noch nicht geschrieben) |
| OTP-Extension | `InvitationService`-Abfrage „bekannter Empfänger“ (gültige Einladung oder aktiver Benutzer) ist noch zu ergänzen; Mailer `IOtpMailer`; Endpunkt `POST /extensions/otp-send` (ADR 0045) |

Offene Voraussetzungen für Phase C: Admin-UI in `Admin.razor` (keine zweite Admin-App), Workflow-Secrets `DEPLOY_EXTID_*`/`DEPLOY_INVITE_TOKEN_KEY` für `deploy/iis/web.env`, Entscheidung zur Aufbewahrung des Ereignisprotokolls, Live-Prüfung auf PostgreSQL.
