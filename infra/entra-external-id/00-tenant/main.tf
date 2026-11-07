locals {
  subscription_scope = "/subscriptions/${var.subscription_id}"
}

# Eigene Ressourcengruppe: bestehende Ressourcen werden weder importiert noch veraendert.
resource "azapi_resource" "rg" {
  type      = "Microsoft.Resources/resourceGroups@2024-03-01"
  name      = var.resource_group_name
  parent_id = local.subscription_scope
  location  = var.resource_group_location
  tags      = var.tags

  lifecycle {
    prevent_destroy = true
  }
}

# External Tenant (Entra External ID, ciamDirectories) - nicht Azure AD B2C.
# Voraussetzung: Resource Provider Microsoft.AzureActiveDirectory registriert (siehe bootstrap.md).
# Die Erstellung verlangt laut Dokumentation ein delegiertes Benutzertoken (Tenant Creator).
resource "azapi_resource" "external_tenant" {
  type      = "Microsoft.AzureActiveDirectory/ciamDirectories@2023-05-17-preview"
  name      = var.tenant_resource_name
  parent_id = azapi_resource.rg.id
  location  = var.data_location
  tags      = var.tags

  body = {
    properties = {
      createTenantProperties = {
        displayName = var.display_name
        countryCode = var.country_code
      }
    }
    sku = {
      name = "Standard"
      tier = "A0"
    }
  }

  response_export_values = ["properties.tenantId", "properties.domainName"]

  # Landes-/Anzeigeangaben der Erstellung sind nach dem Anlegen nicht Teil der Ressource.
  ignore_missing_property = true

  lifecycle {
    prevent_destroy = true # Loeschen nur kontrolliert (docs/entra-external-id/bootstrap.md, Aussenbetriebnahme)
    ignore_changes  = [body]
  }
}
