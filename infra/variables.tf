variable "aws_region" {
  description = "AWS region for all resources. Defaults to Mumbai (ap-south-1), appropriate for an India-targeted Hindi series."
  type        = string
  default     = "ap-south-1"
}

variable "environment" {
  description = "Deployment environment name, applied as a default tag and used in derived resource names."
  type        = string
  default     = "prod"
}

variable "project_name" {
  description = "Short project identifier used to build the resource name prefix."
  type        = string
  default     = "golu"
}

variable "name_prefix" {
  description = "Prefix applied to resource names. When null, it is derived from project_name and environment in locals."
  type        = string
  default     = null
}

variable "assets_bucket_name" {
  description = "Name of the S3 bucket holding working assets and project files. When null, a name is derived from name_prefix and the AWS account id."
  type        = string
  default     = null
}

variable "masters_bucket_name" {
  description = "Name of the S3 bucket holding mastered/delivery outputs. When null, a name is derived from name_prefix and the AWS account id."
  type        = string
  default     = null
}

variable "gpu_instance_types" {
  description = "Candidate GPU instance types for the render compute environment, in preference order."
  type        = list(string)
  default     = ["g5.xlarge", "g5.2xlarge", "g5.4xlarge"]
}

variable "render_max_vcpus" {
  description = "Maximum vCPUs the render compute environment may scale to. Sized for parallel rendering toward 52+ episodes per season."
  type        = number
  default     = 1024
}

variable "use_spot" {
  description = "Whether the render compute environment uses EC2 Spot capacity to reduce cost."
  type        = bool
  default     = true
}

variable "monthly_budget_usd" {
  description = "Monthly cost budget in USD used for AWS Budgets alerting. Matches the outline's 2k-6k USD Spot ceiling."
  type        = number
  default     = 6000
}

variable "budget_alert_emails" {
  description = "Email addresses that receive AWS Budgets threshold notifications."
  type        = list(string)
  default     = []
}
