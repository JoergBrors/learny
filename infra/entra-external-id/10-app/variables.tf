variable "external_tenant_id" {
  description = "Tenant-ID des External Tenants (Output external_tenant_id aus 00-tenant)."
  type        = string
  validation {
    condition     = can(regex("^[0-9a-fA-F-]{36}$", var.external_tenant_id))
    error_message = "external_tenant_id muss eine GUID sein."
  }
}

variable "tenant_subdomain" {
  description = "Subdomain des External Tenants (Output tenant_subdomain aus 00-tenant)."
  type        = string
}

variable "app_display_name" {
  type    = string
  default = "Learny External Auth PoC"
}

variable "redirect_uris" {
  description = "Web-Redirect-URIs (POST /auth/callback, ADR 0036). Nur https oder http://localhost."
  type        = list(string)
  default     = ["https://localhost:5001/auth/callback"]
  validation {
    condition = alltrue([
      for u in var.redirect_uris :
      can(regex("^(https://[^/]+|http://localhost(:[0-9]+)?)/auth/callback$", u))
    ])
    error_message = "Redirect-URIs muessen https (oder http://localhost) sein und auf /auth/callback enden."
  }
}

variable "post_logout_redirect_uris" {
  description = "Optionale Abmelde-Rueckleitungen (gleiche Regeln)."
  type        = list(string)
  default     = []
}

variable "user_flow_name" {
  description = "Name des Sign-up-/Sign-in-User-Flows (angelegt per bootstrap/create-userflow.ps1)."
  type        = string
  default     = "LearnySignUpSignIn"
}

variable "create_client_secret" {
  description = "Clientgeheimnis erzeugen? Default nein: Es landet sonst im State. Besser per Portal/Key Vault (secrets.md)."
  type        = bool
  default     = false
}

variable "client_secret_validity_hours" {
  type    = number
  default = 2160 # 90 Tage
}

variable "otp_extension_api_host" {
  description = "Oeffentlicher Hostname (HTTPS) der Custom-Authentication-Extension fuer OnOtpSend, z. B. otp.example.org. Leer = die API-App wird nicht angelegt (Standard). Erst nach Freigabe des Live-Tests setzen."
  type        = string
  default     = ""
}
