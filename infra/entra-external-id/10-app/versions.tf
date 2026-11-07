terraform {
  required_version = ">= 1.9.0, < 2.0.0"

  required_providers {
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.10"
    }
  }

  backend "local" {
    path = "../.state/10-app.tfstate"
  }
}

# Arbeitet ausschliesslich im External Tenant. Anmeldung: az login --tenant <external_tenant_id> --allow-no-subscriptions
provider "azuread" {
  tenant_id = var.external_tenant_id
  use_cli   = true
}
