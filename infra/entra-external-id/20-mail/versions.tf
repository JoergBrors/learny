terraform {
  required_version = ">= 1.9.0, < 2.0.0"

  required_providers {
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.10"
    }
  }

  # Eigener State (ADR 0041/0044), getrennt von 00-tenant und 10-app.
  backend "local" {
    path = "../.state/20-mail.tfstate"
  }
}

# Arbeitet ausschliesslich im Workforce-/Exchange-Tenant (NICHT im External Tenant).
# Anmeldung: az login --tenant <workforce_tenant_id>
provider "azuread" {
  tenant_id = var.workforce_tenant_id
  use_cli   = true
}
