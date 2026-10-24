provider "aws" {
  region = var.aws_region

  # Credentials are resolved via the standard AWS credential chain. The
  # bootstrap module is applied ONCE by a human with administrative credentials
  # (it creates the OIDC provider, the deploy role and the state backend, all
  # of which require elevated permissions). Do NOT hardcode credentials here.

  # Guard rail: bootstrap must run against the intended account only.
  allowed_account_ids = [var.aws_account_id]

  default_tags {
    tags = {
      Project   = "golu-kal-ka-dost"
      ManagedBy = "terraform"
      Component = "bootstrap"
    }
  }
}
