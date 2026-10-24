# Root module for the "GOLU - Kal Ka Dost" AWS production pipeline.
#
# This file holds shared locals and lookups that later features anchor
# resources to (storage, render batch, media, delivery, monitoring).
# It intentionally defines no billable resources yet; it establishes a
# valid baseline that terraform fmt/init/validate all pass.

data "aws_caller_identity" "current" {}

locals {
  # Resource name prefix, e.g. "golu-prod". Overridable via var.name_prefix.
  name_prefix = coalesce(var.name_prefix, "${var.project_name}-${var.environment}")

  account_id = data.aws_caller_identity.current.account_id

  # Derived bucket names fall back to a globally-unique pattern that includes
  # the AWS account id when explicit names are not supplied.
  assets_bucket_name  = coalesce(var.assets_bucket_name, "${local.name_prefix}-assets-${local.account_id}")
  masters_bucket_name = coalesce(var.masters_bucket_name, "${local.name_prefix}-masters-${local.account_id}")
}
