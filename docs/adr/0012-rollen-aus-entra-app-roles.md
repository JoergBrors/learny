# 0012 — Rollen aus Entra-ID-App-Rollen

- **Status:** Akzeptiert (ergänzt durch [0029](0029-themenzugriff-pflichtzuweisungen-und-themenadministration.md): `themen_admin` ist über `OIDC_ROLE_MAP` abbildbar)
- **Datum:** 2026-10-24
- **Ergänzt:** [0011](0011-mehrbenutzerarchitektur.md) (Rollen), [0005](0005-authentifizierung.md) (OIDC)

## Kontext

Rollen sollen nicht nur in der Anwendung (`/admin`), sondern zentral im Identity Provider gepflegt werden können. In der Entra-ID-App-Registrierung
„learnpath“ existieren die App roles **`Admin.All`** (Anzeigename „Admin“) und **`Users`**. Entra liefert die dem Benutzer zugewiesenen Rollen als Array im
ID-Token-Claim `roles`.

## Entscheidung

- Beim OIDC-Login liest `AuthEndpoints` den Claim `roles` aus dem **validierten** ID-Token (Signatur-, `iss`-, `aud`-, `nonce`-Prüfung wie bisher).
- Die Rollenwerte werden über `OIDC_ROLE_MAP` (`AppConfig.RoleMap`) auf interne Rollen abgebildet. Standard: **`Admin.All=admin`**. Weitere Beispiele:
  `Admin.All=admin,Auditor=auditor` (setzt eine App role „Auditor“ in Entra voraus). Nur `admin` und `auditor` sind abbildbar; `user` hat jeder Benutzer ohnehin
  (die App role `Users` ist daher nur für die Zuweisung in Entra relevant, z. B. mit „Assignment required“).
- Abgleich bei **jedem** Login (`UserService.SyncIdpRolesAsync`):
  - abgebildete Rolle im Token, intern nicht vorhanden → vergeben mit `granted_by = 'idp'`, Audit `role.grant.idp`
  - abgebildete Rolle nicht (mehr) im Token und intern mit `granted_by = 'idp'` → entzogen, Audit `role.revoke.idp`
  - manuell (Admin-UI, `granted_by = <Benutzer-ID>`) oder per Bootstrap (`ADMIN_SUBJECTS`) vergebene Rollen werden **nicht** angetastet
  - nicht abgebildete Rollenwerte werden ignoriert
- Die Wirkung tritt mit dem nächsten Login ein; bestehende Sitzungen behalten ihre Rollen bis dahin (Rollenänderungen in der App wirken dagegen sofort).
- Dev-Modus: `/auth/login?devUser=<name>&devRoles=Admin.All` simuliert den Claim und durchläuft dieselbe Abbildung (nur `AUTH_MODE=dev`, für Tests).

## Konsequenzen

- Entzieht man in Entra `Admin.All`, verliert der Benutzer die Adminrechte beim nächsten Login; in der App allein lässt sich eine IdP-Rolle nicht dauerhaft entfernen
  (sie würde beim nächsten Login wieder gesetzt) — dafür Entra verwenden.
- Der letzte-Admin-Schutz aus ADR 0011 gilt nur für manuelle Änderungen; der IdP gilt als maßgeblich. `ADMIN_SUBJECTS` bleibt als Notzugang bestehen.
- Das Token enthält `roles` nur, wenn dem Benutzer (oder einer seiner Gruppen) die App role in der **Enterprise Application → Users and groups** zugewiesen ist.

## Leitplanken für Änderungen

- Rollen aus dem Token nie ungeprüft übernehmen: nur den Claim des validierten ID-Tokens verwenden und nur über `RoleMap` abbilden.
- Neue vom IdP gesteuerte Rollen: `RoleMap`-Eintrag, `Roles.All` in `Domain/Users.cs`, Test in `tests/LearnCards.IntegrationTests`, [database-schema.md](../database-schema.md) und diese ADR ergänzen.
- `granted_by = 'idp'` ist die Markierung für IdP-gesteuerte Rollen und darf nicht für andere Zwecke verwendet werden.

## Einrichtung in Entra ID

1. App registration → **App roles**: Rolle mit Value `Admin.All` anlegen (vorhanden); optional `Auditor`.
2. **Enterprise applications → learnpath → Users and groups**: Benutzer/Gruppen der Rolle zuweisen.
3. Anwendung: `OIDC_ROLE_MAP` nur setzen, wenn vom Standard (`Admin.All=admin`) abgewichen wird.

## Betroffener Code

- `Auth/AuthEndpoints.cs` (`GetRoles`, Dev-Simulation), `Services/UserService.cs` (`SyncIdpRolesAsync`), `Infrastructure/AppConfig.cs` (`RoleMap`)
