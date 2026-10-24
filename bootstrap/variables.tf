variable "aws_region" {
  description = "AWS region for the state backend. Must match the infra/ region (Mumbai, ap-south-1)."
  type        = string
  default     = "ap-south-1"
}

variable "aws_account_id" {
  description = "Target AWS account id. Used to scope the OIDC trust and to name the state bucket. This is the GOLU account."
  type        = string
  default     = "079184685437"
}

variable "project_name" {
  description = "Short project identifier used to build resource names. Matches infra/ project_name."
  type        = string
  default     = "golu"
}

variable "environment" {
  description = "Deployment environment name. Matches infra/ environment."
  type        = string
  default     = "prod"
}

variable "github_owner" {
  description = "GitHub organization or user that owns the repository."
  type        = string
  default     = "999blue9999-collab"
}

variable "github_repo" {
  description = "GitHub repository name the deploy role trusts for OIDC federation."
  type        = string
  default     = "Cartoon"
}

variable "allowed_git_refs" {
  description = <<-EOT
    Git refs (branches) whose GitHub Actions runs may assume the deploy role on
    a `push` event. Each entry maps to an OIDC subject
    `repo:<owner>/<repo>:ref:refs/heads/<ref>`. Defaults to the main branch
    only so only merges to main can apply.
  EOT
  type        = list(string)
  default     = ["main"]
}

variable "allow_pull_request_oidc" {
  description = <<-EOT
    When true, pull_request-triggered workflow runs may also assume the deploy
    role (OIDC subject `repo:<owner>/<repo>:pull_request`). This is what lets
    PRs run `terraform plan`. The deploy policy is the same, but PR runs only
    plan (never apply) because the workflow gates apply to pushes on main.
  EOT
  type        = bool
  default     = true
}

variable "state_bucket_name" {
  description = "Override for the Terraform state S3 bucket name. When null, derived as <project>-<environment>-tfstate-<account_id>."
  type        = string
  default     = null
}

variable "state_lock_table_name" {
  description = "Override for the Terraform state DynamoDB lock table name. When null, derived as <project>-<environment>-tfstate-lock."
  type        = string
  default     = null
}
