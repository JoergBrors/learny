locals {
  host = "${var.tenant_subdomain}.ciamlogin.com"
}

output "external_tenant_id" {
  value = var.external_tenant_id
}

output "tenant_domain" {
  value = "${var.tenant_subdomain}.onmicrosoft.com"
}

output "oidc_authority" {
  description = "Authority fuer OIDC-Clients (Issuer im Token kann <tenant-id>.ciamlogin.com lauten - im PoC pruefen)."
  value       = "https://${local.host}/${var.external_tenant_id}/v2.0"
}

output "metadata_url" {
  value = "https://${local.host}/${var.external_tenant_id}/v2.0/.well-known/openid-configuration"
}

output "poc_client_id" {
  value = azuread_application.poc.client_id
}

output "expected_redirect_uris" {
  value = var.redirect_uris
}

output "user_flow_name" {
  value = var.user_flow_name
}

# Absichtlich kein Output fuer Secrets.

output "otp_api_client_id" {
  description = "Client-ID der Extension-API (Audience, die der OTP-Endpunkt pruefen muss). Leer, solange otp_extension_api_host nicht gesetzt ist."
  value       = local.otp_api_enabled ? azuread_application.otp_api[0].client_id : ""
}

output "otp_api_identifier_uri" {
  value = local.otp_api_enabled ? azuread_application_identifier_uri.otp_api[0].identifier_uri : ""
}
