# 0042 — Einladungspflichtige External-ID-Registrierung und einmalige Einlösung

- **Status:** Akzeptiert (PoC-Stand 2026-11-08; die Integration in Learny wird aus diesem Branch geplant, offene Punkte: [poc-abschluss.md](../entra-external-id/poc-abschluss.md)) – im PoC umgesetzt und live Ende-zu-Ende bestätigt (Einladung, Einmalcode, einmalige Einlösung)
- **Datum:** 2026-11-07
- **Bezug:** [0004](0004-datenbank-abstraktion.md), [0005](0005-authentifizierung.md), [0011](0011-mehrbenutzerarchitektur.md), [0012](0012-rollen-aus-entra-app-roles.md), [0015](0015-freigaben-globale-lernmodule.md), [0026](0026-betriebsmodus-fail-closed.md), [0036](0036-oidc-form-post-und-https-callbacks.md), [0041](0041-entra-external-id-terraform-und-tenant-modell.md)

## Kontext

Externe Lernende sollen sich nur mit einer Einladung registrieren können. Entra External ID authentifiziert, Learny entscheidet über Benutzer, Profil und Inhaltsrechte. Ein **Entra-Konto** (Verzeichnisobjekt im External Tenant) ist nicht dasselbe wie ein **Learny-Benutzer** (`users` + `user_identities` + Profil + Rechte).

## Entscheidung

1. **Einladung:** Tabelle `invitations` (Felder siehe PoC), Status `pending|accepted|expired|revoked`, `expired` wird aus `expires_at` abgeleitet. Profil und Inhaltszuweisungen (`invitation_grants`) liegen im Datensatz; nur `B2B_LEARNER` und `B2C_LEARNER`, nie Administrator-, Auditor- oder Themen-Admin-Rollen. Erzeugen und Widerrufen nur durch Administratoren (Rolle gegen die DB).
2. **Token:** 256 Bit aus `RandomNumberGenerator`, im Link `/invite/<token>`; gespeichert wird nur `HMAC-SHA256(token)` mit einem Schlüssel aus der Umgebung (analog ADR 0025). Klartext, Hash-Schlüssel und Token erscheinen nie in Logs/Audit/Antworten. HTTPS ist außer auf localhost Pflicht. Standardgültigkeit 72 h (1–720 h konfigurierbar).
3. **Einlösung erst nach validiertem OIDC-Token:** Issuer + `sub` stammen aus der geprüften ID-Token-Antwort (ADR 0036). Reihenfolge: Einladung prüfen → Identität in `user_identities` vorhanden? (dann ablehnen) → E-Mail bestätigt und gleich der Einladung (Normalisierung, Konstantzeitvergleich) → **atomarer Verbrauch** (`UPDATE … WHERE status='pending' AND expires_at>now`, genau eine Zeile gewinnt) → Benutzer, Identität, Profil, Rechte anlegen. Bei Teilfehler werden die angelegten Zeilen gelöscht und der Verbrauch zurückgenommen; scheitert auch das, bleibt die Einladung verbraucht (fail-closed).
4. **Einheitliche Fehlerantwort:** Alle Ablehnungsgründe liefern nach außen dieselbe Meldung; Gründe stehen nur im Audit (ohne Token).
5. **Verbrauchte Einladungen werden nie wieder aktiv** (auch nicht nach Deaktivierung, Löschung der Identität oder Änderung des Benutzers). Eine gleiche E-Mail allein bestätigt keine Identität und verknüpft kein Konto.
6. **Inhalte:** B2C-Benutzer haben keine Standardleserechte; Zugriff nur über ausdrückliche `module_grants` auf globale/freigegebene Module, serverseitig geprüft; ohne Profil kein Zugriff.
7. **Entra-seitige Sperre (Variante B, Custom Authentication Extension)** bleibt optional und ungetestet; Variante A (Learny verweigert die Freischaltung) ist die Grundlage. Details und Grenzen: `docs/entra-external-id/learny-integration.md`.

## Konsequenzen

- Variante A verhindert Learny-Zugang, **nicht** die Entstehung eines Kontos im External Tenant (verwaiste Entra-Konten möglich; Aufräumen ist ein Betriebsthema, keine automatische Löschung).
- Ohne echte Transaktionen in `IDatabase` (ADR 0004) basiert die Absicherung auf atomarem Verbrauch plus Kompensation; eine Transaktionsunterstützung in der Datenbank-Abstraktion würde das vereinfachen (separate ADR).
- Die automatische Benutzeranlage beim ersten OIDC-Login (`UserService`) muss für den External-ID-Issuer abgeschaltet werden, sonst umgeht sie die Einladung.

## Leitplanken für Änderungen

- Profil/Rechte nur aus der Einladung, nie aus Query, Claims oder Formularfeldern; keine Administrator-Einladungen.
- Kein Klartexttoken speichern oder loggen; Token nicht in Weiterleitungs-URLs (nach dem ersten Aufruf in ein kurzlebiges, geschütztes `HttpOnly`-Cookie überführen).
- Keine automatische Identitätszusammenführung über die E-Mail.
- Änderungen an `UserService.ResolveOrCreate…` benötigen Tests für „ohne Einladung kein Benutzer“.

## Betroffener Code

- `poc/InviteRegistration/*`, `poc/InviteRegistration.Tests/*`, `docs/entra-external-id/poc-*.md`, `learny-integration.md`

## Nachtrag 2026-11-07 (Abstimmung mit Versand)

- Der Anspruch bindet zusätzlich den **aktuellen Token-Hash**; dadurch ist die **Rotation** (neuer Link derselben Einladung, alter sofort ungültig) gegen parallele Einlösung abgesichert ([0043](0043-transaktionsmodell-einladungseinloesung.md)).
- Versandzustände (`pending|submitted|failed|unknown`) sind ein **eigenes** Modell neben dem Einladungsstatus; ein Versandfehler verbraucht nie eine Einladung ([0044](0044-exchange-online-einladungsversand-und-versandstatus.md)).
- Die HTTP-Schicht (Link, Cookie, form_post-Callback) ist im PoC umgesetzt; die Token-Validierung bleibt ein Fake (`IOidcFlow`), in Learny `OidcService`.

## Nachtrag 2026-11-07 (Livetest)

- Gegen den echten External Tenant geprüft: Das ID-Token enthält `email`, `idp = mail`, `sub`, `oid`, `tid`, aber **kein `email_verified`**. Entscheidung: Die E-Mail gilt nur bei `idp = mail` (Anmeldung per E-Mail-Einmalcode) als bestätigt; andere Anbieter gelten nicht als bestätigt, solange keine eigene Regel entschieden ist.
- Der Issuer lautet `https://<tenant-id>.ciamlogin.com/<tenant-id>/v2.0` und stammt aus der Discovery.

## Nachtrag 2026-11-08 (Portal)

Status unverändert. Das Administrationsportal (ADR [0046](0046-administrationsportal-externe-registrierung.md)) nutzt dieselbe Einladungslogik. Ergänzt: Einladungsmetadaten (`invitation_meta`), eigene Gültigkeit je Einladung (1–720 h), Ereignisprotokoll (`invitation_events`), `FindActiveUserByIdentityAsync` für die Anmeldung bereits registrierter Benutzer (unbekannte Identitäten werden ohne Einladung abgewiesen) und `prompt=login` im Registrierungsablauf. Die Einmaligkeit und die Profilzuweisung aus dem Einladungsdatensatz bleiben unverändert.

## Nachtrag 2026-11-08 (lokale Konten, Kontoprüfung)

Status unverändert. Neben der Anmeldung per E-Mail-Einmalcode (`idp = mail`) gilt als „E-Mail bestätigt“ auch ein **lokales Konto des External Tenants** (`EmailVerificationPolicies.OtpOrLocalAccount`): Der Token trägt dann keinen `idp`-Claim (am echten Token beobachtet: `idp` fehlt, `ver = 2.0`, kein `email_verified`, Adresse in `preferred_username`/`emails`). Annahme (Microsoft-Dokumentation, am Tenant nicht geprüft): Entra bestätigt die Adresse bei der Registrierung eines lokalen Kontos per Einmalcode. Föderierte Anbieter (immer mit `idp`) bleiben gesperrt; Tenant (`tid`), exakte Adressbindung und Identitätsausschluss gelten unverändert. Die Einladung wird außerdem nicht mehr erstellt, wenn zur Adresse bereits ein Portalkonto existiert (`AccountExistsException`). Anzeigename: Quelle ist die Einladung, nicht der Token. Ablehnungsgründe stehen lesbar im Ereignisprotokoll; bei `EmailNotVerified` mit Kurzform von `idp`/`ver`/E-Mail/`email_verified` (keine Namen, Adressen oder Kennungen).
