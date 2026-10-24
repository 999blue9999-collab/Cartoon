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

output "assets_bucket_arn" {
  description = "ARN of the S3 bucket holding working assets and project files."
  value       = aws_s3_bucket.assets.arn
}

output "masters_bucket_arn" {
  description = "ARN of the S3 bucket holding mastered/delivery outputs."
  value       = aws_s3_bucket.masters.arn
}

output "render_job_role_arn" {
  description = "ARN of the least-privilege role assumed by render jobs on Batch/ECS."
  value       = aws_iam_role.render_job.arn
}

output "pipeline_role_arn" {
  description = "ARN of the orchestration role that submits render jobs and drives the pipeline."
  value       = aws_iam_role.pipeline.arn
}

output "render_job_queue_name" {
  description = "Name of the AWS Batch job queue render jobs are submitted to."
  value       = aws_batch_job_queue.render.name
}

output "render_job_queue_arn" {
  description = "ARN of the AWS Batch render job queue."
  value       = aws_batch_job_queue.render.arn
}

output "render_job_definition_name" {
  description = "Name of the parameterized AWS Batch render job definition."
  value       = aws_batch_job_definition.render.name
}

output "render_job_definition_arn" {
  description = "ARN of the parameterized AWS Batch render job definition."
  value       = aws_batch_job_definition.render.arn
}
