# Empfehlung zur späteren Learny-Integration

Nur Empfehlung; nichts davon ist in Learny umgesetzt. Voraussetzung ist die Freigabe der ADRs [0042](../adr/0042-einladungspflichtige-external-id-registrierung.md), [0043](../adr/0043-transaktionsmodell-einladungseinloesung.md) und [0044](../adr/0044-exchange-online-einladungsversand-und-versandstatus.md). Gemeinsame Architektur: [poc-architecture.md](poc-architecture.md).

## Reihenfolge der Integration (klein, jeweils mit Tests)

1. **Transaktionsentscheidung** ([ADR 0043](../adr/0043-transaktionsmodell-einladungseinloesung.md)): Soll `IDatabase` eine Batch-/Transaktionsmethode erhalten? Empfohlen; ohne sie gilt das getestete Modell „atomarer Anspruch + Kompensation + fail-closed“.
2. **Schema:** Tabellen aus `InviteSchema` (`invitations`, `invitation_grants`, `invitation_deliveries`, `user_profiles`, `module_grants`, `invitation_audit`) in `Data/Schema.cs`, [database-schema.md](../database-schema.md) pflegen. Prüfen, ob `user_profiles` zur Rollenlogik (ADR 0011/0012, `Roles`) passt.
3. **Dienste:** `InvitationService`, `InvitationDeliveryService` nach `Services/`; Konfiguration (`INVITE_TOKEN_KEY` Pflicht, Startabbruch ohne Schlüssel) in `AppConfig.cs`, `.env.example`, `docker-compose.yml`, `deploy/iis/web.env`.
4. **Auth-Gate:** In der OIDC-Rückleitung (`Auth/OidcCallback.cs`) für den External-ID-Issuer die automatische Benutzeranlage (`UserService`) **abschalten** und `RedeemAsync` aufrufen; `IOidcFlow` wird durch Learnys `OidcService` ersetzt (Signatur, Issuer, Audience, nonce, exp). Das Token reist im `SameSite=None`-Cookie (Pfad `/auth/callback`), nie in URL oder `state`. Bekannte Identitäten melden sich normal an.
5. **Endpunkte:** `GET /invite/{token}` und `/invite-start` (nur Vorprüfung), Admin unter `/api/admin/invitations` (create, send, rotate, revoke, status) – nie in `TokenAllowedPrefixes`, nie per MCP; Administratorrolle im Dienst gegen die DB. Der PoC-Schlüssel `X-Admin-Key` entfällt zugunsten der bestehenden Anmeldung.
6. **Inhalte:** `module_grants` an `ResolveModuleAccessAsync` anbinden (ADR 0015): B2C ohne Standardleserechte.
7. **Versand (nach Schritt 5, getrennt aktivierbar):** `ExchangeOnlineMailService` hinter `IInvitationMailer`; Konfiguration `MAIL_ENABLED` (Standard aus), `MAIL_TENANT_ID` (Workforce, nie der External Tenant), `MAIL_CLIENT_ID`, `MAIL_CERT_THUMBPRINT` (oder Key Vault; `MAIL_CLIENT_SECRET` nur als Ausnahme), `MAIL_SENDER` (`learny@brors.link`), `MAIL_ALLOWED_RECIPIENTS`; neue Variablen in `AppConfig.cs`, `.env.example`, `docker-compose.yml`, `deploy/iis/web.env`. RBAC-Scope und Prüfungen aus [mail-permissions.md](mail-permissions.md); Terraform-Stack `20-mail`. Schulungsbenachrichtigungen (ADR 0029) können `IInvitationMailer` später nutzen.
8. **Mehrere Issuer:** Konfiguration für Workforce und External ID getrennt halten; kein stiller Fallback (ADR 0026); `iss`/`sub`-Paar als Schlüssel, nie die E-Mail.
9. **Tests:** die Szenarien aus [poc-testing.md](poc-testing.md) nach `tests/LearnCards.IntegrationTests` übernehmen (inkl. „ohne Einladung kein `users`-Eintrag“, Versand-Zustände, Rotation gegen Einlösung). Danach `dotnet build LearnCards.slnx` und `dotnet run --project tests/LearnCards.IntegrationTests --no-build`.
10. **Weitere Doku:** README/`docs/`, Help-Seite, AGENTS.md-Tabelle.

## Eigener OTP-Versand und Branding (Integration)

- **Endpunkt:** `POST /extensions/otp-send` in der bestehenden Learny-Anwendung (keine zweite Anwendung, ADR 0003) hinter öffentlichem HTTPS; Handler nur bei vollständiger Konfiguration aktiv. Konfiguration (alle Pflicht, sonst kein Endpunkt): `OTP_EXT_AUTHORITY` (Discovery des External Tenants), `OTP_EXT_AUDIENCES`, `OTP_EXT_TENANT_ID`; neue Variablen in `AppConfig.cs`, `.env.example`, `docker-compose.yml`, `deploy/iis/web.env`.
- **Anbindung:** `IOtpMailer` nutzt denselben `ExchangeOnlineMailService` wie die Einladungen (eine Mail-App, ein Zertifikat, ein Postfach-Scope). Die Empfängerregel (`IsKnownRecipientAsync`) bleibt Pflicht; ohne sie wäre die Anmeldeseite ein offener Mailversand.
- **IIS/Proxy:** Der Endpunkt muss die Entra-Aufrufe unverändert erreichen (Request Filtering, WAF/Proxy: nur POST, Body ≤ 8 KB). Er darf nicht in `TokenAllowedPrefixes` (MCP) aufgenommen werden.
- **Betrieb:** Verfügbarkeit überwachen; ohne Rückfall gibt es bei Ausfall keine Anmeldung. Timeout und Wiederholung von Entra sind nicht dokumentiert und im Livetest zu messen.
- **Branding:** `branding.json` und Skripte bleiben unter `infra/`; Assets nie kopieren, sondern aus `wwwroot/brand` und `tools/brand` ableiten. Änderungen am Learny-Design (`app.css`) sollten die Brandingwerte mitziehen; ein Test prüft die Übereinstimmung.
- **Reihenfolge:** (1) Branding anwenden und prüfen, (2) Endpunkt in Learny und Konfiguration, (3) API-App per Terraform, (4) Extension registrieren (`-Apply`), (5) erst nach Freigabe `-Activate`, (6) Rückfall bleibt aus.

## Portal in die Learny-Produktintegration übernehmen

- **Benutzerverwaltung statt Dev-Login:** Die Administratoranmeldung entspricht der vorhandenen Anmeldung (Entra-Workforce, Rollen aus App Roles, ADR 0012). Im PoC erfolgt die Administratoranmeldung bereits über die Learny-Entra-App und die App-Rolle `Admin.All` (Subject-Bootstrap nur als Zusatz); in Learny gelten `UserService.RequireRoleAsync` und die vorhandene Rollenzuordnung.
- **Modelle übernehmen:** `invitations`, `invitation_deliveries`, `invitation_meta`, `invitation_events`, `user_profiles`, `module_grants` (nullbare Einladungsquelle, `granted_by`); `ContentAccess` wird an `ResolveModuleAccessAsync` angebunden (ADR 0015). Das Profil (B2B/B2C) ist von technischen Rollen getrennt zu halten.
- **UI:** Die Razor-Seiten sind PoC-Vorlagen; in Learny entstehen sie im vorhandenen Layout (`Components/Pages`, `app.css`), Geschäftslogik nur in `Services/` (AGENTS.md).
- **Sitzungen:** Statt der PoC-Cookies gelten Learnys Sitzungs- und Betriebsmodus-Regeln (ADR 0014/0026); gemeinsamer Ablauf `InviteFlow` ist der Ersatz für die Einladungsendpunkte, `prompt=login` bleibt.
- **Offen vor der Integration:** Workforce-App-Registrierung und Live-Test der Administratoranmeldung; PostgreSQL-Tests der neuen Abfragen; Aufbewahrung und Manipulationsschutz des Ereignisprotokolls; Löschkonzept für Benutzer; Regeln für Profiländerungen; Mehrinstanzbetrieb (die Linkübergabe `LinkHandover` ist prozesslokal).

## Verifizierungsstrategie für E-Mail-Identitäten

- Quelle der Wahrheit ist das validierte ID-Token (Signatur über JWKS, Issuer aus der Discovery, `aud`, `exp`, nonce) und `tid` des konfigurierten Tenants.
- Echtes Token (getestet): `email`, `idp = mail`, `sub`, `oid`, `tid`, `ver = 2.0`; `email_verified` fehlt.
- Freigaberegel: bestätigt nur bei `idp = mail` **und** `ver = 2.0` im konfigurierten Tenant. Nicht pauschal auf Google, Microsoft-Konto oder Unternehmensföderation übertragen: diese Anbieter bleiben fail-closed, bis ein separates Vertrauensmodell (ADR) akzeptiert ist.
- Die Identität wird nie über die E-Mail allein, sondern über `iss` + `sub` geführt.

## Zu klären vor der Integration

- **„E-Mail bestätigt“ (am echten Token geklärt):** Das External-ID-Token trägt **kein** `email_verified`, aber `idp = mail` bei der Anmeldung per E-Mail-Einmalcode. Der PoC wertet nur das als bestätigt (`EmailVerificationPolicies.OtpIdpOnly`); jeder andere Anbieter gilt nicht als bestätigt, bis dafür eine eigene Regel entschieden ist (Google, Microsoft-Konto, Föderation).
- **Issuer-Form (geprüft):** `https://<tenant-id>.ciamlogin.com/<tenant-id>/v2.0` (Tenant-ID als Subdomain), nicht die Authority mit `<name>.ciamlogin.com`. Learny muss den Issuer aus der Discovery übernehmen, nicht aus der konfigurierten Adresse ableiten; `OIDC_ISSUER` allein reicht nicht.
- **Löschung/Deaktivierung:** Verhalten verwaister Entra-Konten (siehe unten) und DSGVO-Löschung.
- **Ratenbegrenzung** für `/invite/*` und Audit-Aufbewahrung.
- **Verwaiste Entra-Konten und Mail-Bounces:** keine automatische Auswertung/Löschung im PoC.
- **Mehrinstanzbetrieb:** `LinkHandover` ist prozesslokal; bei mehreren Instanzen ist nach Neustart/Lastverteilung die Rotation der Regelfall.

## Grenzen: Learny-Registrierung vs. Entra-Kontoanlage

- **Variante A (umgesetzt im PoC-Konzept):** Entra erlaubt die Selbstregistrierung, Learny verweigert Benutzer und Zugriff ohne Einladung. Folge: Fremde können ein **Entra-Konto** anlegen (Verzeichnisobjekt, Kosten je aktivem Benutzer), erhalten aber nichts in Learny. Gegenmaßnahmen: Aufräumen verwaister Konten, Alarm bei Häufung, Kosten beobachten.
- **Variante B (Custom Authentication Extension, nicht getestet):** Quelle: [Microsoft Learn](https://learn.microsoft.com/en-us/entra/identity-platform/custom-extension-attribute-collection).
  - **Status/Gültigkeit:** Dokumentation als *Preview* gekennzeichnet; gilt für External Tenants.
  - **Ereignisse:** `OnAttributeCollectionStart` (vor der Attributseite; Aktionen `continueWithDefaultBehavior`, `showBlockPage`, `setPrefillValues`) und `OnAttributeCollectionSubmit` (nach dem Absenden; `continueWithDefaultBehavior`, `showBlockPage`, `modifyAttributeValues`, `showValidationError`).
  - **Authentifizierung des Endpunkts:** Entra ruft die REST-API per `client_credentials` mit Bearer-Token auf (Berechtigung „Receive custom authentication extension HTTP requests“); die API muss Token, Issuer (`…ciamlogin.com/<tenant-id>/v2.0`) und Audience prüfen und darf nur HTTPS bieten.
  - **Einladungsübertragung:** Die Doku zeigt **keinen** dokumentierten Weg, einen beliebigen Query-Parameter des Authorize-Requests an die Extension weiterzureichen. Praktikabler Entwurf: die Einladung als Pflichtattribut „Einladungscode“ auf der Registrierungsseite erfassen und in `OnAttributeCollectionSubmit` prüfen (`showValidationError`/`showBlockPage`). Das verschlechtert die Nutzbarkeit (Code einfügen) und ist **ungetestet**.
  - **Kein Verbrauch in der Extension:** Der Aufruf ist wiederholbar (Fehlversuche, Timeouts, Wiederholung durch Entra); der Token darf dort nur geprüft, nie verbraucht werden. Verbrauch erst im Learny-Callback. Dadurch kann ein gültiger Code zwischen Prüfung und Callback von jemand anderem eingelöst werden – dann entsteht ein Entra-Konto ohne Learny-Zugang (Variante A als Rückfallebene bleibt nötig).
  - **Fehlerverhalten:** Antwortet der Endpunkt nicht oder fehlerhaft, ist das Verhalten laut verfügbarer Doku nicht verbindlich beschrieben (Timeouts/Retries nicht geprüft). Ohne gesicherte Aussage darf die Extension nur als zusätzliche Hürde gelten, nie als alleinige Sicherung.
  - **Kosten/Betrieb:** zusätzlicher gehosteter Endpunkt (z. B. Azure Function) mit eigener App-Registrierung, Verfügbarkeitsanforderung und Terraform/Graph-Konfiguration (derzeit nicht im Stack, ADR 0041 müsste ergänzt werden).
- **Empfehlung:** Variante A als verbindliche Sicherheitsgrenze; Variante B erst nach PoC-Test im echten Tenant als Komfort-/Kostenschutz.

**Abschluss und Planung der Umsetzung:** Stand, Tenant-Objekte, Grenzen und Vorschlag für die Reihenfolge stehen in [poc-abschluss.md](poc-abschluss.md); der Branch bleibt als Referenz unverändert.
