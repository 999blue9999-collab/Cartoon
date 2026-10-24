output "deploy_role_arn" {
  description = "ARN of the GitHub Actions OIDC deploy role. Set this as the repository variable AWS_DEPLOY_ROLE_ARN. This value is NOT a secret."
  value       = aws_iam_role.deploy.arn
}

output "oidc_provider_arn" {
  description = "ARN of the GitHub OIDC identity provider."
  value       = aws_iam_openid_connect_provider.github.arn
}

output "state_bucket_name" {
  description = "Name of the S3 bucket holding Terraform remote state. Use as the backend 'bucket' value for infra/."
  value       = aws_s3_bucket.tfstate.bucket
}

output "state_lock_table_name" {
  description = "Name of the DynamoDB table used for Terraform state locking. Use as the backend 'dynamodb_table' value for infra/."
  value       = aws_dynamodb_table.tfstate_lock.name
}

output "backend_config_hint" {
  description = "Ready-to-use -backend-config flags for `terraform init` in infra/."
  value = join(" ", [
    "-backend-config=\"bucket=${aws_s3_bucket.tfstate.bucket}\"",
    "-backend-config=\"key=infra/terraform.tfstate\"",
    "-backend-config=\"region=${var.aws_region}\"",
    "-backend-config=\"dynamodb_table=${aws_dynamodb_table.tfstate_lock.name}\"",
    "-backend-config=\"encrypt=true\"",
  ])
}
