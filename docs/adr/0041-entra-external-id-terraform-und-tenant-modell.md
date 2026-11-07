# 0041 — Entra External ID: Terraform- und Tenant-Modell (PoC)

- **Status:** Akzeptiert (PoC-Stand 2026-11-08; die Integration in Learny wird aus diesem Branch geplant, offene Punkte: [poc-abschluss.md](../entra-external-id/poc-abschluss.md)) – Tenant, App, User Flow und Mail-App live am isolierten External Tenant bzw. Workforce-Tenant angelegt
- **Datum:** 2026-11-07
- **Bezug:** [0002](0002-keine-nuget-pakete.md), [0005](0005-authentifizierung.md), [0009](0009-deployment-wege.md), [0026](0026-betriebsmodus-fail-closed.md), [0036](0036-oidc-form-post-und-https-callbacks.md)

## Kontext

Externe Lernende sollen sich später über Microsoft Entra External ID anmelden können (Hybrid neben dem Workforce-Tenant). Ein isolierter PoC prüft zuerst, was sich deklarativ verwalten lässt. Im Repository gab es bisher keine Infrastruktur-als-Code.

## Entscheidung

1. Ein **separater External Tenant** (`ciamDirectories`, Region Europa), getrennt vom Workforce-Tenant; kein Azure AD B2C.
2. Terraform unter `infra/entra-external-id/` in zwei Stacks mit eigenem State: `00-tenant` (Provider `Azure/azapi ~> 2.13`) und `10-app` (Provider `hashicorp/azuread ~> 3.10`). Nur diese beiden Provider sind freigegeben; ADR 0002 gilt weiter nur für .NET-NuGet-Pakete.
3. **User Flows** werden nicht über Terraform, sondern über ein einmaliges, idempotentes Graph-Skript (`bootstrap/create-userflow.ps1`, v1.0 `authenticationEventsFlows`) angelegt. Ein dritter Provider (z. B. `microsoft/msgraph`) braucht eine eigene ADR.
4. Kein `apply` ohne geprüften Plan, keine CI-Automatik, `prevent_destroy` auf Tenant/Gruppe/App, State git-ignoriert und als geheim behandelt; Client-Secrets standardmäßig nicht in Terraform.
5. Keine Änderung am Learny-Code, an `OIDC_*`-Werten oder am Workforce-Tenant. Die spätere Integration muss ADR 0036 (form_post, HTTPS-Redirect `/auth/callback`) und ADR 0026 (kein stilles Zurückfallen) einhalten.

## Konsequenzen

- Die Tenant-Erstellung ist ein Bootstrap mit delegiertem Benutzer; die API ist Preview und kann sich ändern.
- User Flows sind nicht deklarativ; Drift wird nicht erkannt.
- Zusätzliche Betriebsaufgaben: State-Schutz, Secret-Rotation, Kosten je aktivem Benutzer.

## Leitplanken für Änderungen

- Keine Imports/Ersetzungen bestehender Ressourcen; keine zusätzlichen Graph-Berechtigungen ohne Begründung.
- Föderation mit Unternehmenstenants nur mit Allowlist (eigene ADR).
- Doku in `docs/entra-external-id/` mitpflegen.

## Betroffener Code

- `infra/entra-external-id/**`, `docs/entra-external-id/*`

## Nachtrag 2026-11-07

- Die Exchange-Mail-App (ADR [0044](0044-exchange-online-einladungsversand-und-versandstatus.md)) liegt im **Workforce-/Exchange-Tenant**, nicht im External Tenant, und ist **nicht** Teil dieser Terraform-Stacks (Exchange-RBAC ist über die verwendeten Provider nicht abbildbar; Schritte in `docs/entra-external-id/mail-permissions.md`).
- Die Einladungsverwaltung (ADR [0042](0042-einladungspflichtige-external-id-registrierung.md)) ist Anwendungslogik; die Identity-Infrastruktur bleibt getrennt.

## Nachtrag 2026-11-08 (Branding und OTP-Extension)

- Branding und Extension-Registrierung laufen über Graph-Bootstrap-Skripte mit Trockenlauf (`bootstrap/branding.ps1`, `bootstrap/otp-extension.ps1`), weil `azuread` dafür keine Ressourcen bietet (ADR [0045](0045-external-id-branding-und-eigener-otp-mailprovider.md)).
- Der Stack `10-app` enthält die API-App der OTP-Extension optional (`otp_extension_api_host`, Standard leer): Ohne den Wert ändert sich die Infrastruktur nicht.

## Nachtrag 2026-11-08 (Portal)

Keine Änderung an den Terraform-Stacks. Für die Administratoranmeldung des Portals wird eine Workforce-App-Registrierung benötigt (Redirect `https://<host>/admin/callback`); sie ist **nicht** angelegt und nicht Teil dieses Auftrags.
