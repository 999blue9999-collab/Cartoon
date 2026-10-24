# -----------------------------------------------------------------------------
# Remote state backend: S3 for state storage + DynamoDB for state locking.
#
# This is a PARTIAL backend configuration on purpose: the bucket, key, region
# and lock table are supplied at `terraform init` time via -backend-config so
# the same configuration works for the sandbox (init -backend=false) and for
# real deploys without hardcoding environment-specific names here.
#
# The state bucket and the DynamoDB lock table are created ONCE by the
# bootstrap/ module (see bootstrap/README.md), BEFORE this module is ever
# init'd against the backend. That ordering resolves the chicken-and-egg
# problem of needing a state store before you can store state.
#
# Supply the values one of two ways:
#
#   1. A backend config file (see backend.hcl.example):
#        terraform init -backend-config=backend.hcl
#
#   2. Individual flags (this is what the GitHub Actions workflow does):
#        terraform init \
#          -backend-config="bucket=golu-prod-tfstate-079184685437" \
#          -backend-config="key=infra/terraform.tfstate" \
#          -backend-config="region=ap-south-1" \
#          -backend-config="dynamodb_table=golu-prod-tfstate-lock" \
#          -backend-config="encrypt=true"
#
# In the credential-free sandbox, skip the backend entirely:
#        terraform init -backend=false
# -----------------------------------------------------------------------------

terraform {
  backend "s3" {
    # Intentionally empty. All attributes (bucket, key, region,
    # dynamodb_table, encrypt) are provided at init time via -backend-config.
  }
}
