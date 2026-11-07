# Microsoft Graph (nur Standard-OIDC-Scopes, keine Anwendungsberechtigungen, keine Administratorrechte)
data "azuread_service_principal" "graph" {
  client_id = "00000003-0000-0000-c000-000000000000"
}

locals {
  graph_scopes = ["openid", "profile", "email", "offline_access"]
}

resource "azuread_application" "poc" {
  display_name     = var.app_display_name
  sign_in_audience = "AzureADMyOrg"
  # Keine Eigentuemer-/Rollenvergabe hier; keine App Roles (technische Rollen: ADR 0012, spaeter).

  web {
    redirect_uris = var.redirect_uris
    logout_url    = null

    implicit_grant {
      access_token_issuance_enabled = false
      id_token_issuance_enabled     = false
    }
  }

  # Vertraulicher Webclient (Authorization Code + PKCE); kein oeffentlicher Client.
  fallback_public_client_enabled = false

  # External Tenants akzeptieren nur v2-Tokens (sonst InvalidAccessTokenVersion).
  api {
    requested_access_token_version = 2
  }

  required_resource_access {
    resource_app_id = data.azuread_service_principal.graph.client_id

    dynamic "resource_access" {
      for_each = local.graph_scopes
      content {
        id   = data.azuread_service_principal.graph.oauth2_permission_scope_ids[resource_access.value]
        type = "Scope"
      }
    }
  }

  lifecycle {
    prevent_destroy = true
  }
}

resource "azuread_service_principal" "poc" {
  client_id = azuread_application.poc.client_id
}

# Zustimmung nur fuer die vier Standard-Scopes, damit Endnutzer keinen Consent-Dialog sehen.
resource "azuread_service_principal_delegated_permission_grant" "graph_basic" {
  service_principal_object_id          = azuread_service_principal.poc.object_id
  resource_service_principal_object_id = data.azuread_service_principal.graph.object_id
  claim_values                         = local.graph_scopes
}

resource "azuread_application_password" "poc" {
  count          = var.create_client_secret ? 1 : 0
  application_id = azuread_application.poc.id
  display_name   = "poc-terraform"
  end_date       = timeadd(plantimestamp(), "${var.client_secret_validity_hours}h")

  lifecycle {
    ignore_changes = [end_date]
  }
}

# ── API-Identitaet der Custom Authentication Extension OnOtpSend (ADR 0045) ─────────────────────────
# Nur die App-Registrierung, deren Audience die Extension-API pruefen muss. Die Extension selbst und der Event-Listener werden
# NICHT hier, sondern ueber bootstrap/otp-extension.ps1 registriert (Graph beta, gestuft und nur nach Freigabe).
# Standardmaessig aus (otp_extension_api_host = "").
locals {
  otp_api_enabled = var.otp_extension_api_host != ""
  # Graph-App-Rolle "Receive custom authentication extension HTTP requests"
  otp_receive_payload_role_id = "214e810f-fda8-4fd7-a475-29461495eb00"
}

resource "azuread_application" "otp_api" {
  count            = local.otp_api_enabled ? 1 : 0
  display_name     = "Learny OTP Extension API"
  sign_in_audience = "AzureADMyOrg"

  api {
    requested_access_token_version = 2
  }

  required_resource_access {
    resource_app_id = data.azuread_service_principal.graph.client_id

    resource_access {
      id   = local.otp_receive_payload_role_id
      type = "Role"
    }
  }

  lifecycle {
    prevent_destroy = true
    # Die Identifier-URI wird von azuread_application_identifier_uri verwaltet (sie enthaelt die eigene Client-ID); sonst wuerde die App sie wieder entfernen.
    ignore_changes = [identifier_uris]
  }
}

resource "azuread_application_identifier_uri" "otp_api" {
  count          = local.otp_api_enabled ? 1 : 0
  application_id = azuread_application.otp_api[0].id
  identifier_uri = "api://${var.otp_extension_api_host}/${azuread_application.otp_api[0].client_id}"
}

resource "azuread_service_principal" "otp_api" {
  count     = local.otp_api_enabled ? 1 : 0
  client_id = azuread_application.otp_api[0].client_id
}

# Zustimmung fuer genau diese eine Rolle (kein Mail-Recht, kein Verzeichniszugriff).
resource "azuread_app_role_assignment" "otp_api_receive_payload" {
  count               = local.otp_api_enabled ? 1 : 0
  app_role_id         = local.otp_receive_payload_role_id
  principal_object_id = azuread_service_principal.otp_api[0].object_id
  resource_object_id  = data.azuread_service_principal.graph.object_id
}
