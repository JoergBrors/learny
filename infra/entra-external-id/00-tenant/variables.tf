variable "subscription_id" {
  description = "Azure-Subscription, mit der der External Tenant verknuepft wird."
  type        = string
  validation {
    condition     = can(regex("^[0-9a-fA-F-]{36}$", var.subscription_id))
    error_message = "subscription_id muss eine GUID sein."
  }
}

variable "tenant_resource_name" {
  description = "Ressourcenname = Subdomain von <name>.onmicrosoft.com (nur Buchstaben/Ziffern, max. 26). Muss global frei sein."
  type        = string
  validation {
    condition     = can(regex("^[a-zA-Z0-9]{1,26}$", var.tenant_resource_name))
    error_message = "Nur Buchstaben und Ziffern, 1-26 Zeichen."
  }
}

variable "display_name" {
  description = "Anzeigename des External Tenants."
  type        = string
  default     = "Learny External ID PoC"
}

variable "country_code" {
  description = "Laendercode der Datenresidenz (nicht aenderbar). Siehe https://aka.ms/ciam-data-location"
  type        = string
  default     = "DE"
}

variable "data_location" {
  description = "Datenregion der Kundendaten: 'Europe', 'United States', 'Asia Pacific' oder 'Australia'."
  type        = string
  default     = "Europe"
  validation {
    condition     = contains(["Europe", "United States", "Asia Pacific", "Australia"], var.data_location)
    error_message = "Ungueltige Datenregion."
  }
}

variable "resource_group_name" {
  description = "Ressourcengruppe fuer die Verknuepfungsressource des Tenants."
  type        = string
  default     = "rg-learny-extid-poc"
}

variable "resource_group_location" {
  description = "Azure-Region der Ressourcengruppe (nur Metadaten, nicht die Kundendatenregion)."
  type        = string
  default     = "westeurope"
}

variable "tags" {
  type = map(string)
  default = {
    project     = "learny"
    environment = "poc"
    purpose     = "entra-external-id"
  }
}
