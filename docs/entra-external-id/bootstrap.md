# Entra External ID — Bootstrap und Außerbetriebnahme

## Voraussetzungen und Berechtigungen

| Schritt | Identität | Berechtigung |
|---|---|---|
| Tenant erstellen (`00-tenant`) | angemeldeter Benutzer (`az login`) | Rolle **Tenant Creator** (Entra) und **Contributor** auf Subscription/Ressourcengruppe |
| Provider-Registrierung | Subscription-Besitzer | `az provider register --namespace Microsoft.AzureActiveDirectory` (einmalig) |
| App anlegen (`10-app`) | Benutzer im External Tenant | **Application Administrator** plus eine Rolle für den Consent-Grant (z. B. Privileged Role Administrator) |
| User Flow | Benutzer im External Tenant | **External ID User Flow Administrator** |

Werkzeuge: Terraform ≥ 1.9, Azure CLI, PowerShell 7.

## Einmaliger Bootstrap

1. `az login` (Workforce-Tenant), `az account set --subscription <id>`.
2. Provider registrieren; Status `Registered` abwarten.
3. Tenant-Name auf Verfügbarkeit prüfen (`<name>.onmicrosoft.com`; max. 26 Zeichen, nur Buchstaben/Ziffern). Datenregion und Land sind **nicht nachträglich änderbar**.
4. `00-tenant` planen und nach Freigabe anwenden ([terraform-setup.md](terraform-setup.md)). Fehlt eine Berechtigung, schlägt der Lauf ab; ein erneuter Lauf ist gefahrlos.
5. `az login --tenant <external_tenant_id> --allow-no-subscriptions`; `10-app` anwenden.
6. `bootstrap/create-userflow.ps1` (erst Trockenlauf, dann `-Apply`).
7. Testkonto: Flow über die Authorize-URL der App durchlaufen (E-Mail-Einmalcode), Discovery und `jwks_uri` abrufen.

Scheitert `ciamDirectories` per Terraform (Rechte, Preview-Änderung), den Tenant im Entra Admin Center erstellen („Manage tenants → Create → External → Azure subscription“) und mit `terraform import` in `00-tenant` übernehmen.

## Kontrollierte Außerbetriebnahme (nur auf ausdrücklichen Auftrag)

1. Nutzung in `.env`/Deployments entfernen.
2. App und User Flow im External Tenant entfernen.
3. Tenant im Entra Admin Center löschen (vorher Abhängigkeiten entfernen), danach `terraform state rm` für die Tenant-Ressource und die Ressourcengruppe prüfen/entfernen.
4. Kein automatisches `terraform destroy`: `prevent_destroy` schützt die Ressourcen; das Entfernen ist eine bewusste, dokumentierte Handlung.
