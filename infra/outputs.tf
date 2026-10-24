output "aws_region" {
  description = "The AWS region all resources are deployed into."
  value       = var.aws_region
}

output "name_prefix" {
  description = "The resolved resource name prefix used across the pipeline."
  value       = local.name_prefix
}

output "assets_bucket_name" {
  description = "The resolved S3 bucket name for working assets and project files."
  value       = local.assets_bucket_name
}

output "masters_bucket_name" {
  description = "The resolved S3 bucket name for mastered/delivery outputs."
  value       = local.masters_bucket_name
}
