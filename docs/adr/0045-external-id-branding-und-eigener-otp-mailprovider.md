# 0045 — External-ID-Branding und eigener OTP-Mailprovider

- **Status:** Akzeptiert (PoC-Stand 2026-11-08; die Integration in Learny wird aus diesem Branch geplant, offene Punkte: [poc-abschluss.md](../entra-external-id/poc-abschluss.md)) – im PoC umgesetzt, Branding und OTP-Extension live am External Tenant bestätigt (eigener OTP-Versand über learny@brors.link)
- **Datum:** 2026-11-08
- **Bezug:** [0005](0005-authentifizierung.md), [0026](0026-betriebsmodus-fail-closed.md), [0036](0036-oidc-form-post-und-https-callbacks.md), [0041](0041-entra-external-id-terraform-und-tenant-modell.md), [0042](0042-einladungspflichtige-external-id-registrierung.md), [0043](0043-transaktionsmodell-einladungseinloesung.md), [0044](0044-exchange-online-einladungsversand-und-versandstatus.md)

## Kontext

Die External-ID-Anmeldeseiten tragen Microsoft-Standarddarstellung, und die Einmalcodes (OTP) werden von Microsoft versendet („Learny External ID PoC (über Microsoft)“). Gewünscht: Learny-Branding und Codes von `learny@brors.link`. Der OTP-Ablauf (Code-Erzeugung und -Validierung) muss bei Microsoft Entra bleiben; die Registrierung mit Einladungslink, PKCE und einmaliger Einlösung bleibt unverändert.

## Entscheidung

1. **Branding** wird als Konfiguration im Repository gehalten (`infra/entra-external-id/branding/branding.json`) und ausschließlich über `bootstrap/branding.ps1` angewendet (Graph v1.0 `organizationalBranding`/`localizations`; Trockenlauf mit Differenz als Standard, schreiben nur mit `-Apply`). Terraform (`azuread`) bietet dafür keine Ressource; ein weiterer Provider wird nicht eingeführt. Verwendet werden **nur Bestandsassets**: Favicon `wwwroot/brand/favicon-32.png`, ein Bannerlogo, das mit `tools/brand/make_entra_banner.py` aus dem Karten-Symbol der App und den Farben aus `app.css` erzeugt wird (kein neues Markendesign), Farben `--bg`, `--ink`, `--brand-700`. Deutsche Texte als Lokalisierung `de-DE` und als Standard.
2. **Tenant-Anzeigename** („Learny External ID PoC“) wird nicht automatisch geändert; Zielname „Learny“ ist vorgemerkt und braucht eine eigene Freigabe.
3. **Eigener OTP-Mailversand** über die dokumentierte Custom Authentication Extension (Ereignis `emailOtpSend`/`OnOtpSend`, Preview, nur External Tenants). Der Endpunkt `POST /extensions/otp-send` liegt im isolierten PoC (`OtpSendHandler`), ist nur mit konfiguriertem Handler vorhanden und versendet den von Entra gelieferten Code über den **bestehenden** `ExchangeOnlineMailService` (`IOtpMailer`): gleiche Mail-App, gleiches Zertifikat, gleicher Postfachscope, fester Absender `learny@brors.link`. Keine neue Mail-App, keine zusätzlichen Mail-Rechte.
4. **Authentifizierung der Extension:** Bearer-Token von Entra: RS256 über JWKS, Issuer aus der Discovery, Audience = API-App der Extension, aufrufende App (`azp`/`appid`) = `99045fe1-7639-4a75-9d4a-577b6ca3810f`, Tenant = External Tenant, `exp`/`nbf`. Zusätzlich Schema-Prüfung, Body ≤ 8 KB, Zeitlimit beim Versand (10 s), Rate Limit (global und je Empfänger) und Duplikatschutz.
5. **Empfängerregel statt offenem Mailversand:** Einen Code erhält nur eine Adresse mit gültiger Einladung (pending, nicht abgelaufen, nicht widerrufen) oder mit aktivem Learny-Benutzer. Alle anderen werden abgelehnt (Entra sendet dann keinen Code). Damit erzeugt die Anmeldeseite keinen Mailversand an beliebige Adressen.
6. **Code-Behandlung:** Der Code wird nie erzeugt, verändert, gespeichert oder protokolliert; er steht nur in der Graph-Anfrage. Für den Duplikatschutz wird nur ein HMAC aus Adresse und Code mit einem Prozessschlüssel für wenige Minuten im Speicher gehalten.
7. **Fehler und Ausfall:** Bei jedem Fehler antwortet der Endpunkt nicht mit `continueWithDefaultBehavior` (401/400/403/413/429/502/504), damit Entra keine erfolgreiche Zustellung annimmt. **Ohne konfigurierten Rückfall sendet Entra bei Extension-Fehlern keinen Code** (laut Microsoft-Dokumentation Standard); damit ist ein Ausfall fail-closed (keine Anmeldung ohne Code). Der Rückfall auf den Microsoft-Versand (`fallbackToMicrosoftProviderOnError`) ist **aus** und nur ausdrücklich über `-FallbackToMicrosoft` einschaltbar.
8. **Aktivierung gestuft und nur mit Freigabe:** Terraform legt die API-App der Extension nur bei gesetztem `otp_extension_api_host` an (Standard leer). `bootstrap/otp-extension.ps1` registriert die Extension (`-Apply`) und aktiviert den Listener für die PoC-App erst mit `-Activate`. **Solange kein Listener existiert, sendet weiterhin Microsoft.** Rückfallstrategie auf Microsoft-OTP: Listener löschen.
9. **Vertrauensregel unverändert:** `email_verified` fehlt im Token; die E-Mail gilt nur bei `idp = mail`, `ver = 2.0` im konfigurierten Tenant als bestätigt. Google, Microsoft-Konto und Föderation bleiben gesperrt.

## Konsequenzen

- Die Extension braucht eine **öffentlich erreichbare HTTPS-Adresse**; im Entra-Aufruf gibt es einen Zeit- und Verfügbarkeitsbezug, der nicht dokumentiert ist (Timeout und Wiederholungsverhalten sind unbekannt, daher der Duplikatschutz). Hosting ist offen und eine Freigabeentscheidung (Live-Test).
- Die Microsoft-Dokumentation nennt den Feldnamen des Codes uneinheitlich (`oneTimeCode` und `onetimecode`); beide werden akzeptiert. Das echte Schema ist im Livetest zu bestätigen.
- Graph `sendMail` kennt nur einen Body-Typ je Nachricht: Die Mail wird als HTML versendet, eine reine Textalternative gibt es nicht.
- Die Gültigkeit des Codes (30 Minuten in der Microsoft-Mail) ist für den Versand über die Extension nicht verbindlich belegt und wird konfigurierbar angegeben.
- Branding setzt eine passende Entra-Lizenz (Graph-Dokumentation: P1/P2) voraus; die Verfügbarkeit im External Tenant ist vor dem Apply zu prüfen. Seitentexte über `contentCustomization` sind nicht vorbereitet.
- Zusätzliche Betriebsaufgaben: Verfügbarkeit des Endpunkts, Überwachung der Zustellung (Graph 202 ≠ zugestellt), Zertifikatsrotation der Mail-App.

## Leitplanken für Änderungen

- Kein Code in Logs, Audit, Datenbank, Antworten oder Fehlermeldungen; keine eigene Code-Erzeugung oder -Validierung.
- Keine Extension ohne Token-, Audience- und Aufruferprüfung; kein Endpunkt ohne Empfängerregel.
- Kein Rückfall auf den Microsoft-Versand als Standard; Aktivierung nur nach Freigabe.
- Keine neue Mail-App und keine breiteren Mail-Rechte (ADR 0044); Absender nie aus Anfragen.
- Branding nur aus Bestandsassets; Konfiguration und Skripte gemeinsam pflegen.

## Betroffener Code

- `poc/InviteRegistration/OtpExtension.cs`, `Mail.cs` (`IOtpMailer`, `SendOtpAsync`), `InviteHttp.cs` (`/extensions/otp-send`), `InvitationService.cs` (`IsKnownRecipientAsync`)
- `infra/entra-external-id/branding/*`, `bootstrap/branding.ps1`, `bootstrap/otp-extension.ps1`, `10-app` (gated API-App), `tools/brand/make_entra_banner.py`
- `poc/InviteRegistration.Tests/OtpExtensionTests.cs`, `BrandingTests.cs`, `docs/entra-external-id/*`

## Nachtrag 2026-11-08 (Branding angewendet)

Status unverändert („Vorgeschlagen“). Das Branding wurde mit `bootstrap/branding.ps1 -Apply` angewendet; die OTP-Extension ist weiterhin **nicht** registriert und nicht aktiv. Beobachtungen: Bilder des Standard-Brandings nur über `/branding/localizations/0/{bild}` (der direkte Pfad ist für PUT nicht erlaubt); Anlegen per `PATCH /branding` funktioniert; die Lizenzfrage (P1/P2) stellte sich im External Tenant nicht als Hindernis.

## Nachtrag 2026-11-08 (Extension registriert, nicht aktiviert)

Status unverändert („Vorgeschlagen“). Die API-App der Extension wurde per Terraform angelegt, die Extension mit `bootstrap/otp-extension.ps1 -DeviceLogin -Apply` registriert (Ziel `https://learny-otp.brors.date/extensions/otp-send` über den vorhandenen Cloudflare-Tunnel). Es existiert **kein Listener**; Microsoft sendet die Codes weiterhin. Das Token der Azure CLI enthält das Recht `CustomAuthenticationExtension.ReadWrite.All` nicht; das Skript fordert es deshalb per Gerätecode an (`-DeviceLogin`). Die Aktivierung (`-Activate`, Rückfall aus) braucht eine eigene Freigabe.

## Nachtrag 2026-11-08 (Livetest bestanden)

Status unverändert („Vorgeschlagen“, Zustimmung steht aus). Der eigene OTP-Versand wurde live getestet: Listener aktiv (ohne Rückfall), Entra rief `POST /extensions/otp-send` mit gültigem Token auf, die Learny-OTP-Mail kam von `learny@brors.link` (genau eine, keine Microsoft-Mail), Entra validierte den Code selbst, die Einladung wurde einmalig eingelöst. Ausfalltest (H) und Rückfall auf Microsoft (I) sind noch offen. Betrieb: Der Host muss laufen, sonst gibt es für die PoC-App keine Anmeldung (fail-closed).

## Nachtrag 2026-11-08 (Portal)

Status unverändert (Zustimmung steht aus). Das Portal betreibt den OTP-Endpunkt (falls aktiviert) auf einem eigenen Port ohne Zugriff auf die Verwaltung. Der Registrierungsablauf sendet `prompt=login`, damit eine bestehende Entra-Sitzung nicht als frische OTP-Verifikation gilt; die Wirkung bei External ID ist live zu bestätigen. Der Zustand des Listeners im Tenant ist unverändert (Listener aktiv, Test I und Rückbau stehen aus).

## Nachtrag 2026-11-08 (Phase C)

Der OTP-Handler ist als `POST /extensions/otp-send` in Learny integriert (`EXTID_OTP_ENABLED`, `EXTID_OTP_AUDIENCES`); Empfänger sind Adressen mit gültiger Einladung oder aktive externe Benutzer mit Profil, Workforce-Benutzer nicht. Codes werden weder gespeichert noch protokolliert. Einzelheiten und Grenzen (Ratenbegrenzung je Instanz): [ADR 0050](0050-administration-einladungsversand-und-otp-in-learny.md).
