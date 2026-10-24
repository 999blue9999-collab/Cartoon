# -----------------------------------------------------------------------------
# Remote Terraform state backend: S3 bucket (state storage) + DynamoDB table
# (state locking). These are created by bootstrap/ so they EXIST before the
# infra/ module is ever `terraform init`'d against the S3 backend.
#
# CHICKEN-AND-EGG NOTE: bootstrap/ itself keeps LOCAL state (no backend block
# in this directory). It is applied once by a human with admin credentials, and
# its small state file (bootstrap.tfstate) can be committed to a secure store
# or kept locally. infra/ is the module that uses the S3 backend created here.
#
# The bucket name must be globally unique; it embeds the account id. The lock
# table uses the standard "LockID" hash key Terraform's S3 backend expects.
# -----------------------------------------------------------------------------

locals {
  name_prefix           = "${var.project_name}-${var.environment}"
  state_bucket_name     = coalesce(var.state_bucket_name, "${local.name_prefix}-tfstate-${var.aws_account_id}")
  state_lock_table_name = coalesce(var.state_lock_table_name, "${local.name_prefix}-tfstate-lock")
}

resource "aws_s3_bucket" "tfstate" {
  bucket = local.state_bucket_name

  tags = {
    Name    = local.state_bucket_name
    Purpose = "terraform-remote-state"
  }
}

# Versioning lets you recover a previous state if an apply goes wrong.
resource "aws_s3_bucket_versioning" "tfstate" {
  bucket = aws_s3_bucket.tfstate.id

  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "tfstate" {
  bucket = aws_s3_bucket.tfstate.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
    bucket_key_enabled = true
  }
}

# State can contain sensitive values, so the bucket is fully private.
resource "aws_s3_bucket_public_access_block" "tfstate" {
  bucket = aws_s3_bucket.tfstate.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

# DynamoDB table for state locking. Terraform's S3 backend expects a table with
# a single "LockID" string hash key. PAY_PER_REQUEST avoids provisioned cost
# for a table that sees only occasional lock writes.
resource "aws_dynamodb_table" "tfstate_lock" {
  name         = local.state_lock_table_name
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "LockID"

  attribute {
    name = "LockID"
    type = "S"
  }

  tags = {
    Name    = local.state_lock_table_name
    Purpose = "terraform-state-lock"
  }
}
