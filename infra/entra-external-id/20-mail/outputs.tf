output "workforce_tenant_id" {
  value = var.workforce_tenant_id
}

output "mail_app_client_id" {
  value = azuread_application.mailer.client_id
}

output "mail_service_principal_object_id" {
  description = "Objekt-ID des Service Principals (Eingabe fuer New-ServicePrincipal in Exchange)."
  value       = azuread_service_principal.mailer.object_id
}

output "sender_mailbox" {
  value = var.sender_mailbox
}

output "token_endpoint" {
  value = "https://login.microsoftonline.com/${var.workforce_tenant_id}/oauth2/v2.0/token"
}

# Absichtlich keine Secrets und keine Zertifikatsinhalte.
