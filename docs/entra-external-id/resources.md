# Entra External ID — Ressourcen und Supportmatrix

Stand der Recherche: 2026-11-07 (Microsoft Learn). Verwendet wird **Microsoft Entra External ID in einem External Tenant**, nicht Azure AD B2C.

## Supportmatrix

| Ressource | Terraform | Graph API | Manuell | Status |
|---|---|---|---|---|
| Ressourcengruppe | `azapi_resource` (`Microsoft.Resources/resourceGroups`) | – | – | **Umgesetzt** (`00-tenant`) |
| External Tenant | `azapi_resource` `Microsoft.AzureActiveDirectory/ciamDirectories@2023-05-17-preview` | – (ARM) | Portal-Alternative | **Umgesetzt**; API **Preview**; Erstellung laut Q&A nur mit delegiertem Benutzer (Rolle Tenant Creator), nicht mit Dienstprinzipal — vor Apply bestätigen |
| Resource-Provider-Registrierung | bewusst nicht (ändert Subscription-Zustand) | – | `az provider register` | Bootstrap-Schritt |
| App-Registrierung | `azuread_application` | v1.0 | – | **Umgesetzt** (`10-app`) |
| Service Principal, Basis-Scopes | `azuread_service_principal`, `…_delegated_permission_grant` | v1.0 | – | **Umgesetzt** (nur openid, profile, email, offline_access) |
| Client-Secret | `azuread_application_password` (optional, Standard aus) | v1.0 | Portal/Key Vault | Optional |
| Sign-up-/Sign-in-User-Flow | **Nein** (kein Resource in azuread/azapi) | **v1.0** `authenticationEventsFlows` (`externalUsersSelfServiceSignUpEventsFlow`); Recht `EventListener.ReadWrite.All`, Rolle External ID User Flow Administrator | Portal | **Bootstrap-Skript** `create-userflow.ps1` |
| App ↔ User-Flow | Nein | v1.0 (`conditions.applications.includeApplications`) | Portal | im Skript |
| E-Mail-OTP als Flow-IdP | Nein | v1.0 (Flow-IdP-ID `EmailOtpSignup-OAUTH`) | Portal | im Skript — **ID im PoC verifizieren** (Doku-Beispiel zeigt `EmailPassword-OAUTH`) |
| Deutsche Anmeldetexte | Nein | Graph `organizationalBranding` | Portal | **Offen**, Phase 2 |
| Google / Microsoft-Konto / Entra-Föderation | Nein | v1.0 `identityProviders` | Portal | **Nur vorbereitet**, nicht aktiviert |
| Basisattribute | Nein | Teil des Flows | – | `email`, `displayName` |

Ein dritter Provider (`microsoft/msgraph`) könnte User Flows deklarativ machen; er ist bewusst **nicht** aufgenommen (neue ADR nötig).

## Isolierung

Eigene States, eigene Ressourcengruppe, eigene App; keine `import`-Blöcke; keine Lesezugriffe auf bestehende Entra-Objekte außer dem Microsoft-Graph-Dienstprinzipal des External Tenants.

## Secret-Verwaltung

- Bevorzugt: Secret im Portal des External Tenants erzeugen und direkt in Key Vault bzw. die `.env` der PoC-Umgebung legen (`OIDC_CLIENT_SECRET`, Datei nie committen).
- Mit `create_client_secret = true` landet es im State: Remote-Backend mit RBAC und Verschlüsselung verwenden; Laufzeit 90 Tage; es gibt absichtlich keinen Output dafür.
- Secrets/Tokens nie in Outputs, Logs, Plan-Ausgaben oder Tickets.

## Föderation mit Unternehmens-Entra-Tenants (Entwurf, nicht implementiert)

1. Im Quelltenant (Test-Unternehmenstenant) eine App-Registrierung anlegen; Redirect-URI wie im External-Tenant-Portal für den IdP angezeigt (`…ciamlogin.com/…/federation/oauth2`).
2. Im External Tenant den IdP „Microsoft Entra ID (OIDC/Mitarbeiter)“ mit Client-ID/-Secret der Quell-App anlegen, begrenzt auf den Quelltenant.
3. IdP dem User Flow hinzufügen (`onAuthenticationMethodLoadStart.identityProviders`); ein Flow je App.
4. Consent: Admin-Zustimmung im Quelltenant für `openid profile email`; Zuordnung über `tid` + `oid`/`sub`, nie über die E-Mail allein.
5. **Sicherheitsentscheidung vor Aktivierung:** Allowlist je Tenant-ID, kein Wildcard; eigene ADR.

## Offene Punkte

- Issuer im Token (`<tenant-id>.ciamlogin.com`) gegen Learnys `OIDC_ISSUER`-Prüfung testen (Phase 2).
- Rollenmodell (technische App-Rollen vs. fachliches Profil, ADR 0011/0012) und stabile interne Identität je Tenant in Phase 3.
