# -----------------------------------------------------------------------------
# IAM baseline (least privilege) for the production pipeline.
#
# Two roles are defined:
#
#   1. Render-job role (local.render_role)
#      Assumed by the containerized render task running on AWS Batch / ECS
#      (ecs-tasks.amazonaws.com). Scoped to:
#        - READ from the assets bucket   (pull characters/backgrounds/audio)
#        - READ/WRITE scratch on the assets bucket under /scratch/ (reproducible
#          intermediate frames; the assets bucket has the 14-day scratch
#          lifecycle rule, so scratch is cost-controlled, not the masters bucket)
#        - WRITE finished frames to the masters bucket under /renders/
#        - PUT CloudWatch Logs for the job
#      S3 actions are pinned to the two concrete bucket ARNs. There is no
#      account-wide s3:* and no resource "*" where a real ARN exists.
#
#   2. Pipeline / orchestration role
#      Assumed by the orchestration layer (Lambda / Step Functions) that
#      submits Batch jobs and kicks off MediaConvert / Transcribe. Scoped to
#      the specific Batch resources and read-only S3 listing for routing.
# -----------------------------------------------------------------------------

data "aws_partition" "current" {}
data "aws_region" "current" {}

# --------------------------- Render-job role ---------------------------------

data "aws_iam_policy_document" "render_job_assume" {
  statement {
    sid     = "AllowEcsTaskAssume"
    effect  = "Allow"
    actions = ["sts:AssumeRole"]

    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "render_job" {
  name               = "${local.name_prefix}-render-job"
  description        = "Role assumed by the containerized render task on AWS Batch/ECS."
  assume_role_policy = data.aws_iam_policy_document.render_job_assume.json

  tags = {
    Name = "${local.name_prefix}-render-job"
  }
}

data "aws_iam_policy_document" "render_job" {
  # Read working assets (characters, backgrounds, gadgets, audio, scenes).
  statement {
    sid    = "ReadAssets"
    effect = "Allow"
    actions = [
      "s3:GetObject",
      "s3:GetObjectVersion",
    ]
    resources = ["${aws_s3_bucket.assets.arn}/*"]
  }

  # Read/write reproducible render scratch on the ASSETS bucket under
  # /scratch/. Scratch deliberately lives on the assets bucket because that is
  # where the 14-day scratch expiration lifecycle rule lives (storage.tf), so
  # intermediate frames never accumulate on the more expensive masters bucket.
  statement {
    sid    = "ReadWriteAssetsScratch"
    effect = "Allow"
    actions = [
      "s3:GetObject",
      "s3:GetObjectVersion",
      "s3:PutObject",
      "s3:AbortMultipartUpload",
    ]
    resources = [
      "${aws_s3_bucket.assets.arn}/scratch/*",
    ]
  }

  statement {
    sid       = "ListAssetsBucket"
    effect    = "Allow"
    actions   = ["s3:ListBucket"]
    resources = [aws_s3_bucket.assets.arn]
  }

  # Write finished render frames to the masters bucket under /renders/ (and
  # read back for multi-pass compositing). Scoped to the /renders/ prefix of
  # the bucket ARN, not account-wide; scratch is kept off this bucket.
  statement {
    sid    = "WriteRenders"
    effect = "Allow"
    actions = [
      "s3:GetObject",
      "s3:GetObjectVersion",
      "s3:PutObject",
      "s3:AbortMultipartUpload",
    ]
    resources = [
      "${aws_s3_bucket.masters.arn}/renders/*",
    ]
  }

  statement {
    sid       = "ListMastersBucket"
    effect    = "Allow"
    actions   = ["s3:ListBucket"]
    resources = [aws_s3_bucket.masters.arn]

    condition {
      test     = "StringLike"
      variable = "s3:prefix"
      values   = ["renders/*"]
    }
  }

  # CloudWatch Logs for the render job. Log group ARN is namespaced to the
  # project's /aws/batch/ prefix rather than a blanket "*".
  statement {
    sid    = "WriteLogs"
    effect = "Allow"
    actions = [
      "logs:CreateLogStream",
      "logs:PutLogEvents",
      "logs:CreateLogGroup",
    ]
    resources = [
      "arn:${data.aws_partition.current.partition}:logs:${data.aws_region.current.name}:${local.account_id}:log-group:/aws/batch/${local.name_prefix}*",
    ]
  }
}

resource "aws_iam_policy" "render_job" {
  name        = "${local.name_prefix}-render-job"
  description = "Least-privilege S3 + Logs access for render jobs."
  policy      = data.aws_iam_policy_document.render_job.json
}

resource "aws_iam_role_policy_attachment" "render_job" {
  role       = aws_iam_role.render_job.name
  policy_arn = aws_iam_policy.render_job.arn
}

# ----------------------- Pipeline / orchestration role -----------------------

data "aws_iam_policy_document" "pipeline_assume" {
  statement {
    sid     = "AllowPipelineServicesAssume"
    effect  = "Allow"
    actions = ["sts:AssumeRole"]

    principals {
      type = "Service"
      identifiers = [
        "lambda.amazonaws.com",
        "states.amazonaws.com",
      ]
    }
  }
}

resource "aws_iam_role" "pipeline" {
  name               = "${local.name_prefix}-pipeline"
  description        = "Orchestration role that submits Batch render jobs and drives the pipeline."
  assume_role_policy = data.aws_iam_policy_document.pipeline_assume.json

  tags = {
    Name = "${local.name_prefix}-pipeline"
  }
}

data "aws_iam_policy_document" "pipeline" {
  # Submit / describe / terminate render jobs on this project's queue and
  # definition only.
  statement {
    sid    = "ManageRenderJobs"
    effect = "Allow"
    actions = [
      "batch:SubmitJob",
      "batch:TerminateJob",
    ]
    resources = [
      aws_batch_job_queue.render.arn,
      "${aws_batch_job_definition.render.arn}:*",
      aws_batch_job_definition.render.arn,
    ]
  }

  # Describe/list are not resource-scopable in Batch; keep read-only and narrow
  # to just these describe/list actions.
  statement {
    sid    = "ObserveRenderJobs"
    effect = "Allow"
    actions = [
      "batch:DescribeJobs",
      "batch:ListJobs",
      "batch:DescribeJobQueues",
      "batch:DescribeJobDefinitions",
      "batch:DescribeComputeEnvironments",
    ]
    resources = ["*"]
  }

  # Pass the render-job role to Batch when registering/submitting jobs.
  statement {
    sid       = "PassRenderRole"
    effect    = "Allow"
    actions   = ["iam:PassRole"]
    resources = [aws_iam_role.render_job.arn]
  }

  # List both buckets + read assets to decide routing; write delivery masters.
  statement {
    sid    = "InspectBuckets"
    effect = "Allow"
    actions = [
      "s3:ListBucket",
      "s3:GetBucketLocation",
    ]
    resources = [
      aws_s3_bucket.assets.arn,
      aws_s3_bucket.masters.arn,
    ]
  }

  statement {
    sid    = "ReadRendersWriteMasters"
    effect = "Allow"
    actions = [
      "s3:GetObject",
      "s3:PutObject",
    ]
    resources = [
      "${aws_s3_bucket.masters.arn}/renders/*",
      "${aws_s3_bucket.masters.arn}/chapters/*",
      "${aws_s3_bucket.masters.arn}/feature/*",
    ]
  }
}

resource "aws_iam_policy" "pipeline" {
  name        = "${local.name_prefix}-pipeline"
  description = "Least-privilege orchestration permissions for the render pipeline."
  policy      = data.aws_iam_policy_document.pipeline.json
}

resource "aws_iam_role_policy_attachment" "pipeline" {
  role       = aws_iam_role.pipeline.name
  policy_arn = aws_iam_policy.pipeline.arn
}
