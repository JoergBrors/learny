variable "workforce_tenant_id" {
  description = "Tenant-ID des Workforce-/Exchange-Tenants (Microsoft 365 mit dem Absenderpostfach)."
  type        = string
  validation {
    condition     = can(regex("^[0-9a-fA-F-]{36}$", var.workforce_tenant_id))
    error_message = "workforce_tenant_id muss eine GUID sein."
  }
}

variable "forbidden_tenant_ids" {
  description = "Tenants, in denen die Mail-App nie angelegt werden darf (External-ID-Tenant)."
  type        = list(string)
  default     = ["5fce2b3c-8e95-49b5-9cfd-fc223b7e8f63"]
}

variable "app_display_name" {
  type    = string
  default = "Learny Invitation Mailer"
}

variable "sender_mailbox" {
  description = "Das eine erlaubte Absenderpostfach (primaere SMTP-Adresse; Shared Mailbox). Wird fuer den RBAC-Scope und die Ausgaben verwendet."
  type        = string
  default     = "learny@brors.link"
  validation {
    condition     = can(regex("^[^@ ]+@[^@ ]+[.][^@ ]+$", var.sender_mailbox))
    error_message = "sender_mailbox muss eine E-Mail-Adresse sein."
  }
}

variable "mail_certificate_path" {
  description = "Pfad zur Datei mit dem oeffentlichen Zertifikat (PEM) der Mail-App. Nur der oeffentliche Teil; der private Schluessel bleibt ausserhalb von Terraform (Zertifikatsspeicher/Key Vault). Leer = noch keine Anmeldedaten."
  type        = string
  default     = ""
}
