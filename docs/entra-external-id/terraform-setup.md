# Entra External ID — Terraform-Setup (PoC)

Isolierte Testumgebung für eine spätere Hybrid-Authentifizierung ([ADR 0041](../adr/0041-entra-external-id-terraform-und-tenant-modell.md)). Der bestehende Workforce-Tenant, der Learny-Code und alle Deployments bleiben unberührt.

## Struktur (`infra/entra-external-id/`)

| Verzeichnis | Tenant/Kontext | Provider | Inhalt |
|---|---|---|---|
| `00-tenant/` | Workforce-Tenant + Subscription (nur Verknüpfungsressource) | `Azure/azapi ~> 2.13` | Ressourcengruppe, External Tenant (`ciamDirectories`) |
| `10-app/` | **External Tenant** | `hashicorp/azuread ~> 3.10` | PoC-App-Registrierung, Service Principal, Basis-Scopes, optional Secret |
| `bootstrap/` | External Tenant | Azure CLI + Graph | User Flow `LearnySignUpSignIn` (einmalig, PowerShell) |

Zwei getrennte Stacks mit **eigenem State** (`infra/entra-external-id/.state/`, git-ignoriert), weil der `azuread`-Provider erst nach Existenz des Tenants gegen diesen konfiguriert werden kann.

## Weitere Stacks und Skripte (Stand 2026-11-08)

| Verzeichnis | Tenant | Inhalt | Status |
|---|---|---|---|
| `20-mail/` | Workforce-/Exchange | Mail-App „Learny Invitation Mailer“, Service Principal, öffentliches Zertifikat (kein Secret im State) | angewendet; Exchange-RBAC über `20-mail/scripts/provision-exchange-rbac.ps1` (nicht Terraform) |
| `10-app/` (Ergänzung) | External | optional API-App der OTP-Extension (`otp_extension_api_host`, Standard leer, vier Ressourcen: App, Identifier-URI, Service Principal, Rollenzuweisung) | **angewendet am 2026-11-08** mit `otp_extension_api_host = learny-otp.brors.date` (4 Ressourcen: API-App „Learny OTP Extension API“, Client-ID `1132ae90-fa71-4a16-b5df-15054b9e1ff0`, Identifier-URI `api://learny-otp.brors.date/1132ae90-…`, Service Principal, Rollenzuweisung). Ohne den Wert keine Änderung |
| `branding/` + `bootstrap/branding.ps1` | External | Branding (Texte, Farben, Bannerlogo, Favicon) aus Bestandsassets | **angewendet am 2026-11-08** (Standard-Branding und `de-DE`: Hintergrundfarbe, deutscher Hinweistext, Eingabehinweis, Bannerlogo, Favicon); erneuter Trockenlauf: keine Differenz |
| `bootstrap/otp-extension.ps1` | External | Extension `OnOtpSend` registrieren (`-Apply`), Listener aktivieren (`-Activate`), Rückfall nur mit `-FallbackToMicrosoft` | **Extension registriert** (2026-11-08, ID `f931a363-bfd2-4670-99ff-8bdd9723837c`, Ziel `https://learny-otp.brors.date/extensions/otp-send`); **kein Listener**, daher keine Wirkung auf die Anmeldung (Microsoft sendet weiter). Listener `e8d1e5fa-…` am 2026-11-08 aktiviert (ohne Rückfall) und live getestet |

Alle Terraform-Stacks: `terraform fmt -check` und `validate` erfolgreich. Branding und Extension-Registrierung sind bewusst keine Terraform-Ressourcen (kein Provider-Support; ADR 0045).

## Ablauf

1. Voraussetzungen und einmaliger Bootstrap: [bootstrap.md](bootstrap.md).
2. `cd infra/entra-external-id/00-tenant`, `terraform.tfvars` aus `.example` anlegen, dann `terraform init`, `terraform validate`, `terraform plan -out tfplan` — **Plan prüfen**: genau 2 neue Ressourcen (Ressourcengruppe, Tenant), nichts anderes.
3. Erst nach ausdrücklicher Freigabe: `terraform apply tfplan` (Tenant-Erstellung dauert bis ca. 30 min).
4. `az login --tenant <external_tenant_id> --allow-no-subscriptions`, dann `10-app`: `terraform.tfvars` füllen, `init`, `validate`, `plan`, nach Freigabe `apply` (4 Ressourcen: App, Service Principal, Consent-Grant; Secret nur optional).
5. User Flow: `bootstrap/create-userflow.ps1` (Trockenlauf, dann `-Apply`).
6. Prüfung: Discovery `metadata_url` per Browser/`curl` (inkl. `jwks_uri`), Testkonto über den Flow anmelden.

## Sicherheitsregeln

- Kein `apply` ohne vorherigen, geprüften Plan; kein `-auto-approve`, keine CI-Automatik.
- `prevent_destroy` auf Tenant, Ressourcengruppe und App; kein `terraform destroy` (siehe Außerbetriebnahme in [bootstrap.md](bootstrap.md)).
- State und Plan-Dateien gelten als geheim und sind git-ignoriert; `.terraform.lock.hcl` wird eingecheckt.
- Client-Secret: Standard **nicht** per Terraform (`create_client_secret = false`); siehe [resources.md](resources.md#secret-verwaltung).
- Keine produktiven Benutzer, keine Rollenmigration; die Learny-`.env` wird nicht angefasst.

## Kosten

External ID rechnet nach monatlich aktiven Benutzern (MAU) ab, ein Kontingent ist inklusive; der Tenant ist an die Subscription gebunden. Preise: <https://aka.ms/ciambilling>. Für wenige Testkonten ist mit minimalen Kosten zu rechnen; ein Budget-Alert auf der Ressourcengruppe wird empfohlen. Verbindliche Zahlen sind **nicht geprüft** und vor dem Apply im Preisrechner zu bestätigen.

## Wiederherstellung

State verloren: Ressourcen nie neu anlegen, sondern mit `terraform import` zurückholen (Tenant: `…/providers/Microsoft.AzureActiveDirectory/ciamDirectories/<name>`; App: `/applications/<object-id>`). Der Tenant selbst ist vom Verlust des States nicht betroffen.
