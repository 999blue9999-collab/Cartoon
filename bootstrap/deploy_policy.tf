# -----------------------------------------------------------------------------
# Deploy policy attached to the GitHub Actions OIDC role.
#
# This is a LEAST-PRIVILEGE-ISH policy sufficient to plan/apply everything in
# infra/: S3 buckets, AWS Batch (compute env, queue, job definition), the EC2
# bits the compute env needs (security group, VPC/subnet reads), IAM role and
# policy management for the module's own roles, MediaConvert, CloudFront,
# Polly/Transcribe setup (IAM only), CloudWatch logs/alarms/dashboards, SNS,
# AWS Budgets, and the CloudFormation stacks that wrap the MediaConvert job
# templates. It also grants read/write to the S3 state bucket and the DynamoDB
# lock table so `terraform init`/plan/apply can manage remote state.
#
# Scoping notes:
#   - IAM write actions are scoped to this project's role/policy name prefix
#     (<project>-<environment>-*) and the MediaConvert/CloudFormation stack
#     names, NOT account-wide, so the deploy role cannot mint arbitrary admin
#     roles. iam:PassRole is scoped to the module's own roles.
#   - Many AWS services (Batch, CloudFront, Budgets, MediaConvert, Polly,
#     Transcribe, EC2 describe) do not support resource-level permissions for
#     the create/describe actions Terraform needs, so those statements use
#     resource "*" with a narrow action list, which is documented per block.
#   - This is a deploy role, not an admin role: there is no iam:*, no s3:* on
#     every bucket, and no "Action": "*".
# -----------------------------------------------------------------------------

data "aws_iam_policy_document" "deploy" {

  # ---- Remote state: S3 state bucket + DynamoDB lock table ----
  statement {
    sid    = "TerraformStateBucket"
    effect = "Allow"
    actions = [
      "s3:ListBucket",
      "s3:GetBucketVersioning",
      "s3:GetBucketLocation",
    ]
    resources = [aws_s3_bucket.tfstate.arn]
  }

  statement {
    sid    = "TerraformStateObjects"
    effect = "Allow"
    actions = [
      "s3:GetObject",
      "s3:PutObject",
      "s3:DeleteObject",
    ]
    resources = ["${aws_s3_bucket.tfstate.arn}/*"]
  }

  statement {
    sid    = "TerraformStateLock"
    effect = "Allow"
    actions = [
      "dynamodb:GetItem",
      "dynamodb:PutItem",
      "dynamodb:DeleteItem",
    ]
    resources = [aws_dynamodb_table.tfstate_lock.arn]
  }

  # ---- S3: manage the application assets + masters buckets ----
  # Bucket names are globally unique and not predictable until apply (they
  # embed the account id), so S3 management is granted account-wide here but
  # limited to the specific bucket-management action set Terraform needs. This
  # is the standard trade-off for S3's flat ARN namespace.
  statement {
    sid    = "ManageApplicationBuckets"
    effect = "Allow"
    actions = [
      "s3:CreateBucket",
      "s3:DeleteBucket",
      "s3:PutBucketTagging",
      "s3:GetBucketTagging",
      "s3:PutBucketVersioning",
      "s3:GetBucketVersioning",
      "s3:PutBucketPublicAccessBlock",
      "s3:GetBucketPublicAccessBlock",
      "s3:PutEncryptionConfiguration",
      "s3:GetEncryptionConfiguration",
      "s3:PutLifecycleConfiguration",
      "s3:GetLifecycleConfiguration",
      "s3:PutBucketPolicy",
      "s3:GetBucketPolicy",
      "s3:DeleteBucketPolicy",
      "s3:GetBucketLocation",
      "s3:ListBucket",
      "s3:GetBucketAcl",
      "s3:PutBucketAcl",
    ]
    resources = ["*"]
  }

  # ---- IAM: manage ONLY this project's roles and policies ----
  statement {
    sid    = "ManageProjectRoles"
    effect = "Allow"
    actions = [
      "iam:CreateRole",
      "iam:DeleteRole",
      "iam:GetRole",
      "iam:TagRole",
      "iam:UntagRole",
      "iam:UpdateRole",
      "iam:UpdateAssumeRolePolicy",
      "iam:ListRolePolicies",
      "iam:ListAttachedRolePolicies",
      "iam:AttachRolePolicy",
      "iam:DetachRolePolicy",
      "iam:CreateInstanceProfile",
      "iam:DeleteInstanceProfile",
      "iam:GetInstanceProfile",
      "iam:AddRoleToInstanceProfile",
      "iam:RemoveRoleFromInstanceProfile",
      "iam:TagInstanceProfile",
      "iam:ListInstanceProfilesForRole",
    ]
    resources = [
      "arn:aws:iam::${var.aws_account_id}:role/${local.name_prefix}-*",
      "arn:aws:iam::${var.aws_account_id}:instance-profile/${local.name_prefix}-*",
    ]
  }

  statement {
    sid    = "ManageProjectCustomerPolicies"
    effect = "Allow"
    actions = [
      "iam:CreatePolicy",
      "iam:DeletePolicy",
      "iam:GetPolicy",
      "iam:GetPolicyVersion",
      "iam:ListPolicyVersions",
      "iam:CreatePolicyVersion",
      "iam:DeletePolicyVersion",
      "iam:TagPolicy",
    ]
    resources = [
      "arn:aws:iam::${var.aws_account_id}:policy/${local.name_prefix}-*",
    ]
  }

  # Attaching AWS-managed service-role policies (AWSBatchServiceRole, the ECS
  # instance role, the Spot fleet tagging role) must reference the AWS-managed
  # policy ARNs, which live under aws:policy/. GetPolicy on these is read-only.
  statement {
    sid    = "ReadAwsManagedServicePolicies"
    effect = "Allow"
    actions = [
      "iam:GetPolicy",
      "iam:GetPolicyVersion",
    ]
    resources = [
      "arn:aws:iam::aws:policy/service-role/AWSBatchServiceRole",
      "arn:aws:iam::aws:policy/service-role/AmazonEC2ContainerServiceforEC2Role",
      "arn:aws:iam::aws:policy/service-role/AmazonEC2SpotFleetTaggingRole",
    ]
  }

  # Pass ONLY this project's roles to the services that need them (Batch service
  # role, EC2 instance role, Spot fleet role, render job role, MediaConvert
  # role, etc.). Scoped to the project role name prefix, never "*".
  statement {
    sid    = "PassProjectRoles"
    effect = "Allow"
    actions = [
      "iam:PassRole",
    ]
    resources = [
      "arn:aws:iam::${var.aws_account_id}:role/${local.name_prefix}-*",
    ]
  }

  # Service-linked roles that Batch / Spot / MediaConvert may create on first
  # use. CreateServiceLinkedRole is scoped by the aws:ServiceName condition.
  statement {
    sid    = "CreateServiceLinkedRoles"
    effect = "Allow"
    actions = [
      "iam:CreateServiceLinkedRole",
    ]
    resources = ["*"]
    condition {
      test     = "StringEquals"
      variable = "iam:AWSServiceName"
      values = [
        "batch.amazonaws.com",
        "spot.amazonaws.com",
        "spotfleet.amazonaws.com",
        "ecs.amazonaws.com",
      ]
    }
  }

  # ---- AWS Batch: compute environment, queue, job definition ----
  # Batch does not support resource-level permissions on these create/describe
  # actions, so resource "*" with a narrow action list is required.
  statement {
    sid    = "ManageBatch"
    effect = "Allow"
    actions = [
      "batch:CreateComputeEnvironment",
      "batch:UpdateComputeEnvironment",
      "batch:DeleteComputeEnvironment",
      "batch:CreateJobQueue",
      "batch:UpdateJobQueue",
      "batch:DeleteJobQueue",
      "batch:RegisterJobDefinition",
      "batch:DeregisterJobDefinition",
      "batch:DescribeComputeEnvironments",
      "batch:DescribeJobQueues",
      "batch:DescribeJobDefinitions",
      "batch:ListTagsForResource",
      "batch:TagResource",
      "batch:UntagResource",
    ]
    resources = ["*"]
  }

  # ---- EC2: the networking/security-group bits Batch needs ----
  # The compute env reads the default VPC/subnets and manages one egress-only
  # security group. EC2 describe actions are not resource-scopable.
  statement {
    sid    = "ReadEc2Networking"
    effect = "Allow"
    actions = [
      "ec2:DescribeVpcs",
      "ec2:DescribeSubnets",
      "ec2:DescribeSecurityGroups",
      "ec2:DescribeSecurityGroupRules",
      "ec2:DescribeNetworkInterfaces",
      "ec2:DescribeAccountAttributes",
      "ec2:DescribeAvailabilityZones",
      "ec2:DescribeTags",
    ]
    resources = ["*"]
  }

  statement {
    sid    = "ManageRenderSecurityGroup"
    effect = "Allow"
    actions = [
      "ec2:CreateSecurityGroup",
      "ec2:DeleteSecurityGroup",
      "ec2:AuthorizeSecurityGroupEgress",
      "ec2:RevokeSecurityGroupEgress",
      "ec2:AuthorizeSecurityGroupIngress",
      "ec2:RevokeSecurityGroupIngress",
      "ec2:CreateTags",
      "ec2:DeleteTags",
    ]
    resources = ["*"]
  }

  # ---- MediaConvert: mastering queue (job templates are via CloudFormation) ----
  statement {
    sid    = "ManageMediaConvert"
    effect = "Allow"
    actions = [
      "mediaconvert:CreateQueue",
      "mediaconvert:UpdateQueue",
      "mediaconvert:DeleteQueue",
      "mediaconvert:GetQueue",
      "mediaconvert:ListQueues",
      "mediaconvert:CreateJobTemplate",
      "mediaconvert:UpdateJobTemplate",
      "mediaconvert:DeleteJobTemplate",
      "mediaconvert:GetJobTemplate",
      "mediaconvert:ListJobTemplates",
      "mediaconvert:DescribeEndpoints",
      "mediaconvert:TagResource",
      "mediaconvert:UntagResource",
      "mediaconvert:ListTagsForResource",
    ]
    resources = ["*"]
  }

  # ---- CloudFormation: the stacks wrapping MediaConvert job templates ----
  statement {
    sid    = "ManageMediaConvertStacks"
    effect = "Allow"
    actions = [
      "cloudformation:CreateStack",
      "cloudformation:UpdateStack",
      "cloudformation:DeleteStack",
      "cloudformation:DescribeStacks",
      "cloudformation:DescribeStackEvents",
      "cloudformation:DescribeStackResources",
      "cloudformation:GetTemplate",
      "cloudformation:ListStacks",
      "cloudformation:TagResource",
      "cloudformation:UntagResource",
    ]
    resources = [
      "arn:aws:cloudformation:${var.aws_region}:${var.aws_account_id}:stack/${local.name_prefix}-*/*",
    ]
  }

  # ---- CloudFront: distribution + origin access control ----
  statement {
    sid    = "ManageCloudFront"
    effect = "Allow"
    actions = [
      "cloudfront:CreateDistribution",
      "cloudfront:UpdateDistribution",
      "cloudfront:DeleteDistribution",
      "cloudfront:GetDistribution",
      "cloudfront:GetDistributionConfig",
      "cloudfront:ListDistributions",
      "cloudfront:CreateOriginAccessControl",
      "cloudfront:UpdateOriginAccessControl",
      "cloudfront:DeleteOriginAccessControl",
      "cloudfront:GetOriginAccessControl",
      "cloudfront:ListOriginAccessControls",
      "cloudfront:TagResource",
      "cloudfront:UntagResource",
      "cloudfront:ListTagsForResource",
    ]
    resources = ["*"]
  }

  # ---- CloudWatch Logs: the Batch log group ----
  statement {
    sid    = "ManageLogGroups"
    effect = "Allow"
    actions = [
      "logs:CreateLogGroup",
      "logs:DeleteLogGroup",
      "logs:DescribeLogGroups",
      "logs:PutRetentionPolicy",
      "logs:DeleteRetentionPolicy",
      "logs:ListTagsForResource",
      "logs:TagResource",
      "logs:UntagResource",
    ]
    resources = [
      "arn:aws:logs:${var.aws_region}:${var.aws_account_id}:log-group:/aws/batch/${local.name_prefix}*",
    ]
  }

  # ---- CloudWatch: alarms + dashboards ----
  # Dashboards are not resource-scopable; alarms are scoped to the project name.
  statement {
    sid    = "ManageCloudWatchAlarms"
    effect = "Allow"
    actions = [
      "cloudwatch:PutMetricAlarm",
      "cloudwatch:DeleteAlarms",
      "cloudwatch:DescribeAlarms",
      "cloudwatch:ListTagsForResource",
      "cloudwatch:TagResource",
      "cloudwatch:UntagResource",
    ]
    resources = [
      "arn:aws:cloudwatch:${var.aws_region}:${var.aws_account_id}:alarm:${local.name_prefix}-*",
    ]
  }

  statement {
    sid    = "ManageCloudWatchDashboards"
    effect = "Allow"
    actions = [
      "cloudwatch:PutDashboard",
      "cloudwatch:DeleteDashboards",
      "cloudwatch:GetDashboard",
      "cloudwatch:ListDashboards",
    ]
    resources = ["*"]
  }

  # ---- SNS: alert topic + subscriptions ----
  statement {
    sid    = "ManageAlertsTopic"
    effect = "Allow"
    actions = [
      "sns:CreateTopic",
      "sns:DeleteTopic",
      "sns:GetTopicAttributes",
      "sns:SetTopicAttributes",
      "sns:Subscribe",
      "sns:Unsubscribe",
      "sns:ListSubscriptionsByTopic",
      "sns:ListTagsForResource",
      "sns:TagResource",
      "sns:UntagResource",
    ]
    resources = [
      "arn:aws:sns:${var.aws_region}:${var.aws_account_id}:${local.name_prefix}-*",
    ]
  }

  # SNS subscription listing is account-level (not topic-scopable).
  statement {
    sid    = "ListSnsSubscriptions"
    effect = "Allow"
    actions = [
      "sns:ListSubscriptions",
      "sns:GetSubscriptionAttributes",
    ]
    resources = ["*"]
  }

  # ---- AWS Budgets: the monthly cost budget ----
  # Budgets does not support fine-grained resource ARNs for these actions.
  statement {
    sid    = "ManageBudgets"
    effect = "Allow"
    actions = [
      "budgets:ViewBudget",
      "budgets:ModifyBudget",
      "budgets:DescribeBudget",
    ]
    resources = [
      "arn:aws:budgets::${var.aws_account_id}:budget/*",
    ]
  }

  # ---- Account + identity reads Terraform performs during plan ----
  statement {
    sid    = "ReadAccountContext"
    effect = "Allow"
    actions = [
      "sts:GetCallerIdentity",
      "ec2:DescribeRegions",
    ]
    resources = ["*"]
  }
}

resource "aws_iam_policy" "deploy" {
  name        = "${local.name_prefix}-github-deploy"
  description = "Least-privilege-ish permissions for the GitHub Actions OIDC role to manage the infra/ module."
  policy      = data.aws_iam_policy_document.deploy.json
}
