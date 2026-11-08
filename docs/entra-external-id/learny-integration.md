

## Stand der Integration in Learny (Branch `feature/entra-integration`)

- **Phase A umgesetzt (2026-11-08, [ADR 0048](../adr/0048-multi-issuer-authentifizierung-und-bestandsschutz.md)):** `OidcService` ist parametrisierbar (Workforce unverändert), `ExternalOidcService` und `/auth/external/*` als zweiter Anbieter (nur bekannte Identitäten, Anlage erst mit Einladung in Phase B), `ExternalIdSettings` (`EXTID_*`, fail-closed), `OidcStateStore`/`oidc_consumed_states`, Bestandsschutz-Tests. Offen: PostgreSQL-Prüfung (Phase F), Workflow-Secrets `DEPLOY_EXTID_*` (Phase C).
- Phasen B bis G: siehe Auftrag und [poc-abschluss.md](poc-abschluss.md), Abschnitt 7.

- **Phase B umgesetzt (2026-11-08, [ADR 0049](../adr/0049-einladungs-und-lernberechtigungsmodell.md), [ADR 0048](../adr/0048-multi-issuer-authentifizierung-und-bestandsschutz.md) akzeptiert):** Schema (`user_profiles`, `invitations*`, `invitation_events`, `module_grants`, `user_identities.kind`), `IDatabase.ExecuteBatchAsync` (atomare Transaktion für SQLite und PostgreSQL), `InvitationService` (erstellen, widerrufen, rotieren, auflisten, atomare Einlösung), `InviteEvents`, `LearnerAdminService`, Lernprofile im Zugriffsentscheid, `GET /invite/{token}` und Einlösung im External-Callback. Getestet auf SQLite (94 neue Prüfungen, 1634 insgesamt). **PostgreSQL nicht live geprüft (offen).** Noch keine Oberfläche, kein Versand, keine OTP-Extension (Phase C).

## Schnittstellen für Phase C (Admin-Oberfläche, Versand, OTP, Timeline)

Phase C baut ausschließlich auf diesen Diensten auf (keine Logik aus dem PoC kopieren):

| Aufgabe | Dienst und Methoden |
|---|---|
| Einladung erstellen/verwalten | `InvitationService.CreateAsync(actor, email, profile, moduleIds, topicGrants, meta)`, `RevokeAsync`, `RotateAsync`, `ListAsync(actor, InvitationFilter)`, `GetInfoAsync`; Ausnahmen `UnauthorizedAccessException`, `ArgumentException`, `AccountExistsException` |
| Einladungsversand (Mail) | `InvitationService.RotateAsync`/`CreateAsync` liefern den Link einmalig (nie speichern); Versandstatus in `invitation_deliveries` (Tabelle vorhanden, Zustandsautomat seit Phase C: `InvitationDeliveryService` mit `ExecuteBatchAsync`, Mailer hinter `IInvitationMailer`, ADR 0050) |
| Benutzer-/Lernendenverwaltung | `LearnerAdminService.ListExternalLearnersAsync`, `GetOverviewAsync(actor, userId)`; Benutzerstatus/Rollen weiter über `UserService` (`SetStatusAsync`) |
| Modulzuweisungen | `LearnerAdminService.AssignModuleAsync`, `RevokeModuleAsync`, `ListModuleGrantsAsync` |
| Themen/Pflichtschulungen | `LearnerAdminService.AssignTopicAsync(actor, userId, topicId, version, mandatory, dueAt)`, `RevokeTopicAssignmentAsync`; Quelle der Wahrheit `TopicService` (ADR 0029) |
| Ereignis-Timeline | `InviteEvents.ListAsync(invitationId?, userId?, take)` (Typkatalog `InviteEventTypes`; Versandereignisse `SendRequested`/`Submitted`/`SendFailed` sind angelegt, aber noch nicht geschrieben) |
| OTP-Extension | `InvitationService`-Abfrage „bekannter Empfänger“ (gültige Einladung oder aktiver Benutzer) ist noch zu ergänzen; Mailer `IOtpMailer`; Endpunkt `POST /extensions/otp-send` (ADR 0045) |

Offene Voraussetzungen für Phase C: Admin-UI in `Admin.razor` (keine zweite Admin-App), Workflow-Secrets `DEPLOY_EXTID_*`/`DEPLOY_INVITE_TOKEN_KEY` für `deploy/iis/web.env`, Entscheidung zur Aufbewahrung des Ereignisprotokolls, Live-Prüfung auf PostgreSQL.

## Phase C: Administration, Einladungsversand und OTP (Stand 2026-11-08, [ADR 0050](../adr/0050-administration-einladungsversand-und-otp-in-learny.md), vorgeschlagen)

**Umgesetzt (SQLite getestet, kein Live-Test):**

| Bereich | Umsetzung |
|---|---|
| Administration | `/admin`, Bereiche **Benutzer** (Zugang, Profil, Rollen, Zuweisungen), **Einladungen**, **Inhalte**, **Mailversand**, **Protokoll** (nur Administratoren; Komponenten in `Components/Shared`, Logik in den Diensten). Einladung erstellen (E-Mail, Profil B2B/B2C, Gültigkeit, Module, Themen, Pflicht/freiwillig, Frist) **ohne** Versand; Versand nur nach ausdrücklicher Bestätigung; Link neu ausstellen; widerrufen; Verlauf je Einladung. |
| Lernende | Workforce-Benutzer und externe Lernende: Module und Themen zuweisen/entziehen, Pflicht nur über „verpflichtend“, Benutzer sperren/entsperren. Workforce: keine neue Einladung, Identität oder Profil; bisheriger Zugriff bleibt. Extern: nur Zugewiesenes, Entzug wirkt sofort. |
| Mailversand | `ExchangeOnlineMailService` (Graph `sendMail`, `learny@brors.link`), Versandzustand `pending/submitted/failed/unknown` getrennt vom Einladungsstatus (`InvitationDeliveryService`): Anspruch setzt `unknown` vor dem Graph-Aufruf, Ergebnis + Ereignis + Audit atomar, kein Doppelversand (auch über Instanzen), `unknown` nur mit Zusatzbestätigung, kein Link in Datenbank/Audit. Zertifikat der Mail-App wird **auf der Plattform erzeugt** (Administration → Mailversand: privater Schlüssel verschlüsselt in `MAIL_KEY_DIR`, nur die öffentliche `.cer` zum Hochladen in Entra; jede Umgebung eigenes Zertifikat). Vorlagen: Einladung, zugewiesene Schulung, Pflichtschulungserinnerung (die beiden Schulungsvorlagen sind **nicht angebunden** — keine automatischen Mails). |
| OTP | `POST /extensions/otp-send` mit eigener Bearer-Prüfung der Extension (Issuer, Audience, aufrufende App, Tenant), 8 KB, Ratenbegrenzung, Zeitlimit; Codes nie gespeichert/protokolliert. Empfänger: gültige Einladung oder aktiver externer Benutzer mit Profil; Workforce-Benutzer sind keine Empfänger. |
| Linkschutz | Link `…/invite#<token>` (Fragment verlässt den Browser nie) → tokenlose Seite → POST-Body → Cookie; kein Token in IIS-/Proxy-/Cloudflare-Protokollen oder Referrer. |

**Konfiguration (alle Standard aus, fail-closed):** `MAIL_ENABLED`, `MAIL_TENANT_ID`, `MAIL_CLIENT_ID`, `MAIL_SENDER`, `MAIL_CLIENT_CERT_PATH` + `MAIL_CLIENT_CERT_PASSWORD` (oder in Development `MAIL_CLIENT_SECRET`), `MAIL_ALLOWED_RECIPIENTS`; `EXTID_OTP_ENABLED`, `EXTID_OTP_AUDIENCES` (optional `EXTID_OTP_AUTHORITY`, `EXTID_OTP_GLOBAL_PER_MINUTE`, `EXTID_OTP_PER_RECIPIENT_10MIN`). Dazu aus Phase A/B: `EXTID_*`, `INVITE_TOKEN_KEY`, `PUBLIC_BASE_URL`. Siehe `.env.example`, [docs/iis-deployment.md](../iis-deployment.md) und [mail-permissions.md](mail-permissions.md).

**Offene Punkte / Risiken:** PostgreSQL nicht live geprüft (`PostgresReadinessTests` vorbereitet, nicht ausgeführt); der IIS-Workflow rendert `EXTID_*`, `INVITE_TOKEN_KEY` und `MAIL_*` noch nicht; Ratenbegrenzung der OTP-Extension je Instanz; Mail-App-Zertifikat noch nicht aus der Learny-PKI (Phase D); kein Live-Test mit Exchange Online und Entra.

### Manueller Ende-zu-Ende-Test (nach separater Freigabe, **noch nicht ausgeführt**)

Voraussetzungen: Testumgebung mit `EXTID_*`, `INVITE_TOKEN_KEY`, `MAIL_*` (Zertifikat), `EXTID_OTP_*`; Cloudflare/Proxy leitet **nur** die nötigen Pfade öffentlich weiter, darunter `/extensions/otp-send`; die Custom Authentication Extension zeigt auf den Learny-Endpunkt; der Zustand des OTP-Listeners wird vorher erneut geprüft (Test I im PoC ist offen); ein Testpostfach ist als Empfänger in `MAIL_ALLOWED_RECIPIENTS` eingetragen.

| Schritt | Aktion | Erwartung |
|---|---|---|
| 0 | Administration → Mailversand: „Zertifikat erzeugen“, `.cer` herunterladen, in Entra bei der Mail-App hochladen (Debug und Plattform je eigenes). | Fingerabdruck wird angezeigt; nach wenigen Minuten wirksam. |
| A | Workforce-Administrator meldet sich an. | Anmeldung wie bisher. |
| B | Administration → Einladungen: Einladung erstellen (B2C, Modul A). | Status offen, Versand „nicht versendet“, **keine** Mail. |
| C | B2C-Profil und Modul A sind zugewiesen (Anzeige). | Nur Modul A in der Einladung. |
| D | „Versand bestätigen“, „Einladung jetzt senden“. | Versand „angenommen (nicht zwingend zugestellt)“. |
| E | Posteingang prüfen. | Absender `learny@brors.link`, Betreff „Einladung zu Learny“. |
| F | Link öffnen (Zugriffsprotokolle prüfen). | Weiterleitung zu Entra; **kein** Token in IIS-/Proxy-Log; Ereignisse „Link aufgerufen“, „Registrierung gestartet“. |
| G | Registrierung bei Entra (E-Mail-Einmalcode). | Entra fragt nach dem Code. |
| H | OTP-Mail prüfen. | Absender `learny@brors.link`, Betreff „Ihr Learny-Anmeldecode“. |
| I | Code eingeben. | Entra validiert, Rücksprung zu Learny. |
| J | — | Einladung eingelöst (einmalig), Benutzer + Profil + Zuweisung entstehen atomar. |
| K | Dashboard des neuen Benutzers. | Nur Modul A. |
| L | Modul B direkt aufrufen (UI und API). | Nicht erreichbar. |
| M | Administration → Ereignisprotokoll. | Timeline: erstellt, Versand angefordert/angenommen, Link aufgerufen, Registrierung gestartet, OIDC abgeschlossen, eingelöst, Profil, Lernrechte. |
| N | Modul A entziehen. | Zugriff sofort weg (ohne neue Anmeldung). |
| O | Link erneut öffnen. | Abgewiesen, keine zweite Registrierung. |

Nach dem Test werden Testbenutzer und -einladungen nur nach ausdrücklichem Auftrag entfernt.

### Voraussetzungen für Phase D

Ablösung des selbstsignierten Plattformzertifikats durch eine Ausstellung aus der Learny-PKI (Entra akzeptiert in der App-Registrierung nur öffentliche Zertifikate; der private Schlüssel bleibt in Learny), Rotation ohne Mailausfall (zwei Zertifikate nebeneinander), Ablaufüberwachung, Ablage des Schlüssels außerhalb des Webroots, Erweiterung des IIS-Workflows um die Variablen, PostgreSQL-Liveprüfung.

### Umgebungen, Entra-Umstellung und Cloudflare (Hilfsskript `infra/entra-external-id/bootstrap/learny-targets.ps1`)

Debug läuft auf `http://localhost:5000`, die Plattform auf `https://learny.brors.date`. Das Skript (Trockenlauf ohne `-Apply`, nichts wird gelöscht) trägt die Redirect-URIs `…/auth/external/callback` für **beide** Adressen zusätzlich an der PoC-App ein und setzt die Ziel-URL der OTP-Extension: Standard `-OtpTarget Debug` = `https://learny-otp.brors.date/extensions/otp-send` (der Cloudflare-Tunnel zeigt auf die lokale Debug-Instanz, der OTP-Test bleibt lokal); `-OtpTarget Platform` = `https://learny.brors.date/extensions/otp-send`. Bezeichner der API-App (Audience) und Listener bleiben unverändert. `-ShowCloudflare` zeigt die Cloudflare-Angaben ohne Anmeldung:

| Cloudflare (Zero Trust → Tunnels → Public Hostname) | Wert |
|---|---|
| Hostname | `learny-otp.brors.date` |
| Path | `extensions/otp-send` |
| Service | HTTP `localhost:5000` (bisher `localhost:5080` = PoC-Portal) |
| zweite Regel, gleicher Host ohne Pfad | `HTTP Status 404` (Catch-all) |
| Access-Richtlinie | keine auf diesem Host |

