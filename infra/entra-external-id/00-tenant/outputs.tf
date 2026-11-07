output "external_tenant_id" {
  description = "Tenant-ID des External Tenants (Eingabe fuer 10-app)."
  value       = azapi_resource.external_tenant.output.properties.tenantId
}

output "tenant_domain" {
  description = "Primaere Tenant-Domain."
  value       = "${var.tenant_resource_name}.onmicrosoft.com"
}

output "tenant_subdomain" {
  description = "Subdomain fuer ciamlogin.com (Eingabe fuer 10-app)."
  value       = var.tenant_resource_name
}
