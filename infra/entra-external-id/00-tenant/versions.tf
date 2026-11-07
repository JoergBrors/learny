terraform {
  required_version = ">= 1.9.0, < 2.0.0"

  required_providers {
    azapi = {
      source  = "Azure/azapi"
      version = "~> 2.13"
    }
  }

  # Eigener State (ADR 0041). Lokal als Standard; fuer Teamarbeit backend.hcl (siehe backend.hcl.example).
  backend "local" {
    path = "../.state/00-tenant.tfstate"
  }
}

provider "azapi" {
  subscription_id = var.subscription_id
  # Tenant-Erstellung ist ein Bootstrap mit angemeldetem Benutzer (az login), kein Dienstprinzipal.
  use_cli = true
}
