# Eigene App-Registrierung nur fuer den Mailversand (ADR 0044). Keine Redirect-URIs, kein interaktiver Login,
# keine API-Berechtigungen: "Application Mail.Send" wird ausschliesslich ueber Exchange Online RBAC for Applications
# auf das Absenderpostfach vergeben (scripts/provision-exchange-rbac.ps1), NICHT als tenantweite Graph-Berechtigung.
resource "azuread_application" "mailer" {
  display_name     = var.app_display_name
  sign_in_audience = "AzureADMyOrg"

  # App-only: weder web- noch SPA- noch Public-Client-Plattform.
  fallback_public_client_enabled = false

  api {
    requested_access_token_version = 2
  }

  lifecycle {
    prevent_destroy = true

    precondition {
      condition     = !contains(var.forbidden_tenant_ids, var.workforce_tenant_id)
      error_message = "Die Mail-App gehoert in den Workforce-/Exchange-Tenant, nicht in den External-ID-Tenant."
    }
  }
}

resource "azuread_service_principal" "mailer" {
  client_id = azuread_application.mailer.client_id
}

locals {
  mail_certificate_pem = var.mail_certificate_path == "" ? "" : file(var.mail_certificate_path)
}

# Zertifikat statt Client-Secret: nur der oeffentliche Teil steht in Entra und im State.
resource "azuread_application_certificate" "mailer" {
  count          = local.mail_certificate_pem == "" ? 0 : 1
  application_id = azuread_application.mailer.id
  type           = "AsymmetricX509Cert"
  value          = local.mail_certificate_pem

  lifecycle {
    precondition {
      condition     = !can(regex("PRIVATE KEY", local.mail_certificate_pem))
      error_message = "Die Zertifikatsdatei darf keinen privaten Schluessel enthalten."
    }
  }
}
