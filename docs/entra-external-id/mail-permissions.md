# Exchange Online: Versandrechte auf ein Postfach begrenzen

Zu [ADR 0044](../adr/0044-exchange-online-einladungsversand-und-versandstatus.md). **Stand 2026-11-08: Zertifikat, Mail-App und Exchange-RBAC (Schritt 1 bis 3) sind eingerichtet und mit `verify-exchange-rbac.ps1` geprüft. Livetests A bis D sind ausgeführt und bestanden (siehe unten).** Quelle: [RBAC for Applications in Exchange Online](https://learn.microsoft.com/en-us/exchange/permissions-exo/application-rbac).

## Zuordnung der Tenants

| | External-ID-Tenant | Workforce-/Exchange-Tenant |
|---|---|---|
| Tenant-ID | `5fce2b3c-8e95-49b5-9cfd-fc223b7e8f63` (`learnyextidpoc.onmicrosoft.com`) | `db88f9b6-c89a-41a3-a21d-b61458e234ff` („Brors.NET“) |
| Zweck | Anmeldung der Lernenden (OIDC, App „Learny External Auth PoC“) | Mailversand (App „Learny Invitation Mailer“), Absenderpostfach |
| Terraform | `00-tenant`, `10-app` | `20-mail` (eigener State) |

Die Mail-App liegt **nie** im External-ID-Tenant: Terraform (`precondition`) und der Mailservice (`ForbiddenTenantIds`) verhindern die Verwechslung.

## Absenderpostfach (lesend ermittelt, 2026-11-08)

- Verzeichnisobjekt „Learny-Lernplattform“, UPN `learny@brors.xyz`, **primäre SMTP-Adresse `learny@brors.link`**, Aliase `learny@brors.xyz`, `learny@brorsxyz.onmicrosoft.com`, Konto **deaktiviert** (typisch für ein Shared Mailbox). Kein neues Postfach wird angelegt.
- **Nicht lesend bestätigt** (erfordert eine interaktive Exchange-Anmeldung): Postfachtyp `SharedMailbox`, bestehende Postfachberechtigungen, vorhandene RBAC-Objekte. `provision-exchange-rbac.ps1` liest und prüft das vor jeder Änderung.
- Sendeweg wie vorgegeben: `POST https://graph.microsoft.com/v1.0/users/learny@brors.link/sendMail`. Ob Graph hier die primäre SMTP-Adresse statt des UPN akzeptiert, zeigt erst Test A. Fallback ohne Änderung des RBAC-Scopes: UPN `learny@brors.xyz` oder Objekt-ID `728f5a5c-a974-41d0-974e-4262bc5bbca3` im Pfad.

## Befund: bestehende Mail-Berechtigungen (lesend)

Apps im Workforce-Tenant mit **tenantweitem** Graph-Mail-Anwendungsrecht: *AvePoint Online Services Administration for Microsoft Azure* (Mail.Send), *SendAs* (Mail.Send), *AdvancedHuntingRead* (Mail.Send), *FileUpload-App-Prod* und *FileUpload-App-Dev* (Mail.Send), *POWERSHELL GRAPH* (Mail.Read). Diese Apps können **auch über `learny@brors.link` senden** (Entra- und Exchange-Berechtigungen addieren sich). Der RBAC-Scope der Mail-App schützt davor nicht. Empfehlung: im Workforce-Tenant prüfen und gegebenenfalls Exchange-Application-Access-Policies/RBAC für diese Apps einführen. Das ist **nicht** Teil dieses Auftrags und wurde nicht verändert.

## Konfiguration (Terraform `20-mail`)

| Ressource | Eigenschaft |
|---|---|
| `azuread_application` „Learny Invitation Mailer“ | nur Workforce-Tenant, `AzureADMyOrg`, keine Redirect-URIs, keine Plattform, keine `required_resource_access`, `prevent_destroy` |
| `azuread_service_principal` | gleiche App |
| `azuread_application_certificate` (optional) | nur öffentliches Zertifikat; privater Schlüssel nie in Terraform; **kein Client-Secret im State** |

Plan (2026-11-08): **2 hinzufügen, 0 ändern, 0 löschen** (App, Service Principal); keine Änderung an den Stacks `00-tenant`/`10-app`.

## Authentifizierung der Mail-App

Client-Credentials mit **Zertifikat** (Client-Assertion, RS256, `x5t#S256`); `ClientSecret` ist nur eine Alternative, genau eines von beiden. Token vom Endpunkt `https://login.microsoftonline.com/db88f9b6-…/oauth2/v2.0/token`, Scope `https://graph.microsoft.com/.default`. Der Absender kommt ausschließlich aus der Konfiguration; Empfänger nur aus der Allowlist (leer = niemand).

## Schritte (alle nur nach Freigabe)

1. **Zertifikat:** `scripts/new-mail-certificate.ps1` (Trockenlauf, dann `-Apply`): privater Schlüssel nicht exportierbar im Speicher, öffentlicher Teil als PEM (`.gitignore`d).
2. **Terraform:** `20-mail/terraform.tfvars` mit `mail_certificate_path = "mail-app-public.pem"`, `terraform plan`, nach Freigabe `apply` (Anmeldung `az login --tenant db88f9b6-…`). **Ausgeführt am 2026-11-08:** 3 Ressourcen angelegt (App, Service Principal, öffentliches Zertifikat); Client-ID `f3365162-0eda-48b3-99dd-b96d3633c40c`, Service-Principal-Objekt-ID `d93e53e9-813f-4fdd-83ad-11cbd7ce0ab4`, Zertifikat-Thumbprint `E9A0BAC76ABA6FC701392344A7118BCF1A841A68`.
3. **RBAC (ausgeführt 2026-11-08):** `scripts/provision-exchange-rbac.ps1 -AppId <client-id> -ServicePrincipalObjectId <sp-oid>` (Trockenlauf), dann `-Apply`. Legt nur Fehlendes an: `New-ServicePrincipal`, `New-ManagementScope` mit Filter `PrimarySmtpAddress -eq 'learny@brors.link'`, `New-ManagementRoleAssignment -Role 'Application Mail.Send' -CustomResourceScope`. Bricht ab bei weiteren Rollen oder abweichendem Scope.
4. Warten (Cache 30 min bis 2 h), dann **`scripts/verify-exchange-rbac.ps1 -ServicePrincipalObjectId <sp-oid> -OtherMailbox <anderes>`** (nur lesend; Exit-Code 0 nur bei Erfolg).
5. **Live-Tests** (siehe unten).

## Live-Testplan (vorbereitet, nicht ausgeführt)

Programm: `poc/InviteRegistration.MailLiveTest` (Umgebung `MAIL_*`, ohne `--send` nur Trockenlauf).

| Test | Aktion | Erwartung |
|---|---|---|
| **A** erlaubtes Postfach | Versand über `learny@brors.link` an den freigegebenen Testempfänger | Graph 202 (`Accepted`); Mail kommt an |
| **B** anderes Postfach | derselbe Service Principal sendet über ein anderes Postfach | HTTP 403 (`permission_denied`); bei Erfolg **Sicherheitsfehler**, sofort Rolle entfernen |
| **C** Registrierung | Testempfänger öffnet den Link, meldet sich per External ID an | nur zugewiesene Inhalte (Modul A, nicht B) |
| **D** Wiederverwendung | eingelösten Link erneut öffnen | „ungültig“, keine zweite Registrierung |

**Ergebnis 2026-11-08 (sofort nach der RBAC-Einrichtung, ohne Wartezeit):**

- **Test A:** Absender `learny@brors.link` (Pfad `/users/learny@brors.link/sendMail`, primäre SMTP-Adresse akzeptiert), Graph 202 `Accepted`, Zertifikatsauthentifizierung funktioniert. Ob die Mail im Postfach des Empfängers ankommt, ist durch das Postfach zu bestätigen (202 heißt nur „angenommen").
- **Test B:** Versand über `joerg@brors.net` mit demselben Service Principal: HTTP 403 `permission_denied` (verweigert).

- **Test C (Ende-zu-Ende):** Die Einladung wurde über `ExchangeOnlineMailService` (Zertifikat, `learny@brors.link`) an `joerg@brors.net` gesendet (`Sent/accepted`, Versandstatus `submitted`, Einladung `pending`). Der Link aus der Mail führte über External ID (E-Mail-Einmalcode) zur Registrierung; Einladung `accepted` (`redeemed ok`), Dashboard zeigte genau ein Modul (A).
- **Test D:** Der eingelöste Link wurde erneut geöffnet: „Die Einladung ist ungültig oder nicht einlösbar.“, kein zweiter Benutzer.
- Das dafür angelegte Client-Secret der External-ID-App wurde nach dem Test gelöscht (0 Secrets).

Test C und D laufen mit `poc/InviteRegistration.LiveProbe` bzw. dem späteren Learny-Callback (Ablauf wie am 2026-11-07 bestätigt).

## Versand der Entra-Einmalcodes (OTP)

Der OTP-Versand nutzt **dieselbe** Mail-App „Learny Invitation Mailer“ im Workforce-Tenant (Zertifikat, Token von `db88f9b6-…`), denselben Absender `learny@brors.link` und denselben RBAC-Scope (`Application Mail.Send` nur für dieses Postfach). Es gibt keine zweite Mail-App und keine zusätzlichen Mail-Rechte. Der Absender kommt nur aus der Konfiguration. Die statische Empfänger-Allowlist gilt für Einladungsmails; OTP-Empfänger werden im Handler gegen gültige Einladungen und aktive Benutzer geprüft (ADR [0045](../adr/0045-external-id-branding-und-eigener-otp-mailprovider.md)).

Getrennte Identitäten: Die **Extension-API** der Anmeldeseite (App „Learny OTP Extension API“ im External Tenant, nur Audience, kein Mail-Recht) und die **Mail-App** (Workforce-Tenant, nur `Application Mail.Send` per Exchange-RBAC) sind verschiedene Apps in verschiedenen Tenants. Die Extension-API-App erhält ausschließlich die Graph-App-Rolle „Receive custom authentication extension HTTP requests“.

Mailvorlage: Betreff „Ihr Learny-Anmeldecode“, HTML (Graph kennt nur einen Body-Typ, keine Textalternative), ohne Bilder, Links und Tracking.

## Einladungsmail aus dem Portal

Das Portal versendet die Einladung über denselben `ExchangeOnlineMailService` (Absender `learny@brors.link`, Betreff „Einladung zu Learny“) und nur nach ausdrücklicher Administratorbestätigung. Inhalt: gebrandetes HTML mit persönlicher Begrüßung (falls Anzeigename), Link als Schaltfläche und sichtbar, Gültigkeit, Hinweis auf einmalige Nutzung, Sicherheitshinweis; keine Bilder, kein Tracking. Graph kennt nur einen Body-Typ je Nachricht (keine Textalternative). Mail-App, Zertifikat und Exchange-RBAC bleiben unverändert; es gibt keine neuen Rechte.

## Statische Prüfung (Stand heute)

| Prüfung | Ergebnis |
|---|---|
| Mail-App ohne Graph-Anwendungsberechtigung geplant | ja (Plan: keine `required_resource_access`) |
| keine tenantweite Mail.Send-Zuweisung für die Mail-App vorgesehen | ja |
| Terraform `fmt -check` und `validate` | erfolgreich (alle drei Stacks) |
| Scope = genau ein Postfach (Filter) | **angewendet und geprüft** (Scope `Learny-Invite-Sender`, Rolle `Application Mail.Send`, `learny@brors.link` InScope = True, `joerg@brors.net` InScope = False) |
| Postfachtyp | `SharedMailbox` bestätigt (Trockenlauf des Provisionierungsskripts); Geräte-Code-Anmeldung `-DeviceLogin` ergänzt |
| andere Apps mit tenantweitem Mail.Send | **Risiko** (siehe oben) |

## Grenzen

- Graph 202 heißt „angenommen“, nicht „zugestellt“; Bounces landen im Postfach des Absenders und werden nicht ausgewertet.
- SPF/DKIM/DMARC der Domain `brors.link` sind nicht geprüft.
- Wirksamkeit von RBAC-Änderungen: 30 Minuten bis 2 Stunden.
- Zertifikatsrotation und Ablauf sind nicht automatisiert.
