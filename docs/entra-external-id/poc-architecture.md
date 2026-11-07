# PoC-Architektur: einladungspflichtige Registrierung und Versand

Umsetzung: `poc/InviteRegistration/` (Bibliothek mit HTTP-Schicht, keine NuGet-Pakete, nicht Teil von `LearnCards.slnx`). Entscheidungen: [ADR 0042](../adr/0042-einladungspflichtige-external-id-registrierung.md) (Einlösung), [0043](../adr/0043-transaktionsmodell-einladungseinloesung.md) (Transaktionsmodell), [0044](../adr/0044-exchange-online-einladungsversand-und-versandstatus.md) (Versand).

## Abgrenzung

| | Entra-Konto | Learny-Benutzer |
|---|---|---|
| Wo | External Tenant (Verzeichnisobjekt) | Learny-Datenbank (`users`, `user_identities`) |
| Entsteht durch | Registrierung im User Flow | erfolgreiche Einladungseinlösung |
| Bedeutet | „Person hat sich authentifiziert“ | „Person darf Learny nutzen“, mit Profil und Rechten |

Ein Entra-Konto ohne Learny-Benutzer hat keinen Zugang (fail-closed, ADR 0026). Die Authentifizierung der Lernenden (OIDC, External Tenant) und der Mailversand (Client-Credentials, Workforce-Tenant) sind getrennte Identitäten und Apps (ADR 0005).

## Verantwortlichkeiten

| Baustein | Aufgabe | Kennt nicht |
|---|---|---|
| `InviteApp` (HTTP) | Endpunkte, Cookies, OIDC-Ablauf, Administrator-Authentifizierung | Datenbankdetails, Mailtransport |
| `InvitationService` | Erstellen, Prüfen, **Einlösen**, Widerrufen, **Rotieren**, Profil, Inhaltsrechte (einzige Einladungslogik) | Mail, HTTP |
| `InvitationDeliveryService` | Versandzustand, Administratorbestätigung, Versandslot, Wiederholungsregeln | Token-Erzeugung, Einlösung |
| `IInvitationMailer` / `ExchangeOnlineMailService` | Nachricht bauen, Graph-Token holen (Zertifikat bevorzugt), senden, Fehler klassifizieren; feste Absender- und Empfängerregeln, Tenant-Schutz, Timeouts, Retry-After | Einladungsstatus, Datenbank |
| `LinkHandover` | flüchtige Übergabe des Klartextlinks (nur Speicher, 30 min) | Persistenz |
| `ContentAccess` | serverseitige Inhaltsprüfung (nur zugewiesene Module) | – |
| `IOidcFlow` / `EntraOidcFlow` | Autorisierungs-URL, Code-Tausch (PKCE), Token-Validierung über JWKS. Im PoC `EntraOidcFlow` (live gegen den External Tenant geprüft); in den HTTP-Tests ein Fake; in Learny `OidcService` | Einladungen |

Wiederverwendet (unverändert): `IDatabase`/`SqliteDatabase`, `users`, `user_identities`, `user_roles`. Tabellen des PoC: `invitations`, `invitation_grants`, `invitation_deliveries`, `user_profiles`, `module_grants`, `poc_modules`, `invitation_audit`.

## Zustandsmodelle (getrennt)

```
Einladung:  pending ──Einlösung──▶ accepted        (dauerhaft)
               │ ──Widerruf────▶ revoked
               └─ expires_at ──▶ expired            (abgeleitet, keine Fortschreibung nötig)

Versand:    pending ──Graph 202──▶ submitted        (angenommen, nicht „zugestellt“)
               │ ──eindeutig nicht gesendet──▶ failed ──erneut (derselbe Link)──▶ …
               └─ ──unklar──▶ unknown ──erneut (derselbe Link)──▶ …
            submitted ──ausdrückliche Rotation──▶ pending (neuer Link, alter ungültig)
```

Ein Versandzustand ändert nie den Einladungsstatus. Ein fehlgeschlagener Versand verbraucht nichts.

## Token-Lebenszyklus

| Phase | Klartext | Gespeichert |
|---|---|---|
| Erstellung/Rotation | im Rückgabewert (Speicher) | nur `HMAC-SHA256(token)`, Version |
| Übergabe an den Versand | `LinkHandover` (Speicher, 30 min), nie in der HTTP-Antwort | – |
| Mail | Mailinhalt (Graph, Postfach des Empfängers) | `saveToSentItems=false` |
| Öffnen des Links | URL → sofort in geschütztes `HttpOnly`-Cookie, Weiterleitung auf `/invite-start` | – |
| Weg zu Entra | im `SameSite=None`-Cookie `inv.oidc` (Pfad `/auth/callback`), nicht in `state`/URL | – |
| Einlösung | Cookie → `RedeemAsync` | `accepted`, Token-Hash bleibt (Einmaligkeit) |

Nie in Datenbank, Audit, Logs (Logging der Requests abgeschaltet), Statusantworten oder Fehlercodes.

## Ablauf

```
Admin: POST /admin/invitations            → Einladung + Versand "pending"  (kein Link in der Antwort, keine Mail)
Admin: POST /admin/invitations/{id}/send  → confirm=true, Mailer sendet über Exchange Online → "submitted"/"failed"/"unknown"
Benutzer: GET /invite/<token>             → prüfen, Cookie, 303 /invite-start         (kein Versand, kein Verbrauch)
          GET /invite-start               → state/nonce/PKCE, Cookie, 303 → Entra
Entra:    POST /auth/callback (form_post) → state prüfen (einmalig), Code tauschen, ID-Token validieren
          → RedeemAsync: Einladung gültig? → Identität bekannt? (ablehnen) → E-Mail bestätigt+gleich?
            → atomarer Anspruch → Benutzer/Identität/Profil/Rechte (Teilfehler: Rückbau)
          → 303 /dashboard (Sitzung)  |  jeder Fehler: 303 /invite-failed (einheitlich)
```

## Sicherheitsmodell

- 256-Bit-Zufallstoken, HMAC-gespeichert (Schlüssel ≥ 32 Byte aus der Umgebung); Bindung an E-Mail und Zielprofil; E-Mail muss bestätigt sein; gleiche E-Mail verknüpft nie Konten.
- Einmalige Nutzung durch **einen** bedingten `UPDATE` (Status, Ablauf, Widerruf **und** aktueller Token-Hash), daher gegen parallele Einlösung, Rotation und Widerruf abgesichert.
- Profil/Rechte nur aus dem Einladungsdatensatz; nie Rollen; Administrator-Endpunkte prüfen zusätzlich die Rolle gegen die DB.
- Einheitliche Fehlerantwort; Gründe nur im Audit (ohne Token).
- HTTPS-Basisadresse Pflicht (außer localhost); `Cache-Control: no-store`, `Referrer-Policy: no-referrer`.

## Fehlerbehandlung

| Fall | Ergebnis | Einladung | Versand |
|---|---|---|---|
| Mail abgelehnt (401/403/404/429/4xx, deaktiviert, Konfiguration) | HTTP 502 | pending | failed |
| Mail unklar (Timeout, Netzwerk, 5xx, Ausnahme) | HTTP 504 | pending | unknown |
| Paralleler Versand | 409 `InProgress` | pending | unverändert |
| Versand nach submitted | 409 `RequiresRotation` | pending | submitted |
| Klartext nicht mehr im Speicher | 409 `link_unavailable` → Rotation | pending | unverändert |
| Mehr als 5 Versuche | 429 | pending | failed/unknown |
| Abgelaufen/widerrufen/eingelöst | nicht sendbar, Link ungültig | expired/revoked/accepted | – |
| Einlösung: Teilfehler | Rückbau, abgelehnt | pending (sonst fail-closed accepted) | – |
| Callback wiederholt / Identität bekannt / falsche E-Mail | `/invite-failed` | accepted bzw. pending | – |

## Eigener OTP-Versand (Custom Authentication Extension) und Branding

Umsetzung und Entscheidungen: [ADR 0045](../adr/0045-external-id-branding-und-eigener-otp-mailprovider.md). **Im PoC implementiert und simuliert getestet; am Tenant ist nichts aktiviert** (Microsoft sendet die Codes weiter selbst).

```
Entra erzeugt den Einmalcode  ->  Extension-Aufruf POST /extensions/otp-send  (Bearer-Token von Entra)
  -> ExtensionTokenValidator: RS256/JWKS, Issuer (Discovery), Audience, azp = 99045fe1-..., tid, exp/nbf
  -> Schema (type emailOtpSend, identifier, oneTimeCode), Body <= 8 KB
  -> Rate Limit (global 60/min, je Adresse 3 pro 10 min)
  -> Empfaengerregel: gueltige Einladung oder aktiver Learny-Benutzer (sonst 403, kein Code)
  -> Duplikatschutz (HMAC aus Adresse+Code, wenige Minuten, nur Speicher)
  -> ExchangeOnlineMailService.SendOtpAsync  (learny@brors.link, Zertifikat, RBAC-Scope)
  -> 200 + continueWithDefaultBehavior   |   Fehler: 401/400/403/413/429/502/504 ohne "continue"
Entra validiert den Code selbst, wenn der Benutzer ihn eingibt.
```

| Baustein | Aufgabe |
|---|---|
| `OtpSendHandler` | Ablauf oben; kein Code in Logs, Audit, Datenbank, Antworten |
| `ExtensionTokenValidator` | Prüfung des Entra-Bearer-Tokens (nur Aufrufe von Microsofts Authentication-Events-Dienst) |
| `IOtpRecipientPolicy` / `InvitationOtpRecipientPolicy` | verhindert offenen Mailversand: nur Einladung oder Benutzer; nutzt `InvitationService.IsKnownRecipientAsync` |
| `IOtpMailer` / `ExchangeOnlineMailService` | derselbe Dienst wie für Einladungen; `OtpMailTemplate` (HTML, deutsch, ohne Bilder/Links) |
| `/extensions/otp-send` | nur mit konfiguriertem Handler vorhanden (sonst 404) |

**Ausfallverhalten:** Bei Fehlern der Extension sendet Entra ohne konfigurierten Rückfall **keinen** Code (Microsoft-Dokumentation); es gibt keine Anmeldung ohne Code (fail-closed). Der Rückfall auf den Microsoft-Versand ist ausgeschaltet und nur mit `-FallbackToMicrosoft` einschaltbar. Rückfallstrategie bei Problemen: Listener löschen, dann sendet wieder Microsoft.

**Wirkung auf den Einladungsablauf:** Eine Adresse ohne gültige Einladung erhält bei aktivierter Extension gar keinen Code mehr; damit entsteht im External Tenant auch kein unnötiges Konto durch Fremde (Variante B aus [learny-integration.md](learny-integration.md), allerdings über den Mailversand statt über die Attributprüfung).

**Branding:** `infra/entra-external-id/branding/branding.json` (Bestandsassets und Farben aus `app.css`), angewendet über `bootstrap/branding.ps1` (Trockenlauf, `-Apply`). Stand 2026-11-08: Branding ist **angewendet** (Standard und `de-DE`, Trockenlauf ohne Differenz). Anzeigename des Tenants weiter „Learny External ID PoC“ (Zielname „Learny“ nicht angewendet; er erscheint z. B. in der Microsoft-Standardmail). Die OTP-Extension ist **nicht** aktiviert.

## Portal (Administration und Lernende)

Ein eigenständiges Portal (`poc/InviteRegistration.Portal`, ADR [0046](../adr/0046-administrationsportal-externe-registrierung.md)) macht den PoC bedienbar: `/admin` für Administratoren (Einladungen, Versand, Widerruf, Benutzer, Lernzuweisungen, Ereignisprotokoll) und `/dashboard` für Lernende. Es nutzt die vorhandenen Bausteine weiter; der Einladungs-/OIDC-Ablauf ist dafür in `InviteFlow`/`OidcCookieFlow` ausgelagert. Neu in der Bibliothek: `AdminService` (Verwaltung, Rolle je Aufruf gegen die Datenbank), `AdminBootstrap` (einzige Stelle für die Rolle `admin`, Workforce-Issuer ≠ External-ID-Issuer), `InviteEvents` (Ereignisprotokoll), `InvitationMailTemplate`. Details, Routen, Statusmodell, Start und Live-Test: [poc-portal.md](poc-portal.md).

## Mailversand (Vorbereitung)

Tenants getrennt: Lernende authentifizieren sich im External Tenant (`5fce2b3c-…`), der Mailversand läuft über die App „Learny Invitation Mailer“ im Workforce-/Exchange-Tenant (`db88f9b6-…`) mit Application Mail.Send, per Exchange-RBAC auf `learny@brors.link` begrenzt. Details, Terraform, Skripte und Livetests: [mail-permissions.md](mail-permissions.md). Der Mailservice kennt weder Einladungen noch Token-Lebenszyklus; der Versandzustand bleibt in `invitation_deliveries` (Retry-After sperrt den Versandslot).

## E-Mail-Vertrauen

Gebunden an den getesteten Kontext: `EntraOidcFlow` akzeptiert nur Tokens mit Issuer aus der Discovery, `aud` = Client-ID, `tid` = konfigurierter External Tenant (`ExpectedTenantId`); `EmailVerificationPolicies.OtpIdpOnly` wertet die E-Mail nur bei `idp = mail` und `ver = 2.0` als bestätigt. Google, Microsoft-Konto und Föderation bleiben fail-closed, bis ein eigenes Vertrauensmodell akzeptiert ist.

## Offene Punkte

- Die Token-Validierung ist live gegen Entra geprüft; der Graph-Versand über Exchange Online ist nicht getestet.
- PostgreSQL ungetestet; Ratenbegrenzung für `/invite/*` fehlt.
- „E-Mail bestätigt“ gilt nur bei `idp = mail`; andere Anbieter sind offen ([learny-integration.md](learny-integration.md)).
