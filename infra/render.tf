# -----------------------------------------------------------------------------
# GPU render farm: AWS Batch managed compute environment + job queue + job def.
#
# AWS Batch is used here as the modern, infrastructure-as-code substitute for
# Thinkbox Deadline named in the project outline (Section 9 "Production
# Sketch"). Batch gives us a managed, autoscaling EC2 GPU pool with native Spot
# support and a declarative job model, all expressible in Terraform, whereas
# Deadline would require a separately licensed, manually operated render farm.
#
# Networking: to keep this module deployable WITHOUT first building a VPC, the
# compute environment attaches to the account's DEFAULT VPC and its subnets via
# data sources, plus one minimal egress-only security group. For a hardened
# production deployment, replace these data sources with a dedicated VPC module
# (private subnets + NAT) and pass its subnet/SG ids in.
#
# Scaling: min/desired vCPUs are 0 (scale to zero when idle, no idle cost) and
# max vCPUs is var.render_max_vcpus, sized for parallel rendering toward 52+
# episodes per season. Spot capacity is used when var.use_spot is true.
# -----------------------------------------------------------------------------

# ----------------------------- Networking ------------------------------------

data "aws_vpc" "default" {
  default = true
}

data "aws_subnets" "default" {
  filter {
    name   = "vpc-id"
    values = [data.aws_vpc.default.id]
  }
}

# Minimal security group: no inbound, all outbound (render nodes need to reach
# S3, ECR and CloudWatch). Lock egress down further in a hardened deployment.
resource "aws_security_group" "render" {
  name        = "${local.name_prefix}-render"
  description = "Render farm compute environment egress-only security group."
  vpc_id      = data.aws_vpc.default.id

  egress {
    description = "Allow all outbound (S3, ECR, CloudWatch, package mirrors)."
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = {
    Name = "${local.name_prefix}-render"
  }
}

# --------------------------- Batch IAM roles ---------------------------------

# Batch service role (control-plane role Batch itself assumes).
data "aws_iam_policy_document" "batch_service_assume" {
  statement {
    effect  = "Allow"
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["batch.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "batch_service" {
  name               = "${local.name_prefix}-batch-service"
  description        = "Service role AWS Batch assumes to manage the compute environment."
  assume_role_policy = data.aws_iam_policy_document.batch_service_assume.json

  tags = {
    Name = "${local.name_prefix}-batch-service"
  }
}

resource "aws_iam_role_policy_attachment" "batch_service" {
  role       = aws_iam_role.batch_service.name
  policy_arn = "arn:${data.aws_partition.current.partition}:iam::aws:policy/service-role/AWSBatchServiceRole"
}

# EC2 instance role + profile for the ECS container instances Batch launches.
data "aws_iam_policy_document" "batch_instance_assume" {
  statement {
    effect  = "Allow"
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ec2.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "batch_instance" {
  name               = "${local.name_prefix}-batch-instance"
  description        = "EC2 instance role for the Batch GPU container instances."
  assume_role_policy = data.aws_iam_policy_document.batch_instance_assume.json

  tags = {
    Name = "${local.name_prefix}-batch-instance"
  }
}

resource "aws_iam_role_policy_attachment" "batch_instance_ecs" {
  role       = aws_iam_role.batch_instance.name
  policy_arn = "arn:${data.aws_partition.current.partition}:iam::aws:policy/service-role/AmazonEC2ContainerServiceforEC2Role"
}

resource "aws_iam_instance_profile" "batch_instance" {
  name = "${local.name_prefix}-batch-instance"
  role = aws_iam_role.batch_instance.name
}

# Spot fleet role: required only when provisioning Spot capacity.
data "aws_iam_policy_document" "spot_fleet_assume" {
  count = var.use_spot ? 1 : 0

  statement {
    effect  = "Allow"
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["spotfleet.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "spot_fleet" {
  count              = var.use_spot ? 1 : 0
  name               = "${local.name_prefix}-spot-fleet"
  description        = "Role allowing Batch to request EC2 Spot capacity for the render farm."
  assume_role_policy = data.aws_iam_policy_document.spot_fleet_assume[0].json

  tags = {
    Name = "${local.name_prefix}-spot-fleet"
  }
}

resource "aws_iam_role_policy_attachment" "spot_fleet" {
  count      = var.use_spot ? 1 : 0
  role       = aws_iam_role.spot_fleet[0].name
  policy_arn = "arn:${data.aws_partition.current.partition}:iam::aws:policy/service-role/AmazonEC2SpotFleetTaggingRole"
}

# ----------------------- Batch compute environment ---------------------------

resource "aws_batch_compute_environment" "render" {
  # A fresh name on every change avoids the "cannot update a compute
  # environment in-place" class of errors when instance settings change.
  compute_environment_name_prefix = "${local.name_prefix}-render-"
  type                            = "MANAGED"
  state                           = "ENABLED"
  service_role                    = aws_iam_role.batch_service.arn

  compute_resources {
    # EC2 Spot when var.use_spot (big cost saving per the outline's Spot
    # ceiling), otherwise on-demand EC2.
    type                = var.use_spot ? "SPOT" : "EC2"
    allocation_strategy = var.use_spot ? "SPOT_CAPACITY_OPTIMIZED" : "BEST_FIT_PROGRESSIVE"

    # For Spot, cap the bid at 100% of on-demand (never pay more than on-demand).
    bid_percentage      = var.use_spot ? 100 : null
    spot_iam_fleet_role = var.use_spot ? aws_iam_role.spot_fleet[0].arn : null

    # GPU instance types (g5 family) from the input variable.
    instance_type = var.gpu_instance_types

    # Scale to zero when idle; scale up to the configured ceiling.
    min_vcpus     = 0
    desired_vcpus = 0
    max_vcpus     = var.render_max_vcpus

    instance_role      = aws_iam_instance_profile.batch_instance.arn
    security_group_ids = [aws_security_group.render.id]
    subnets            = data.aws_subnets.default.ids

    tags = {
      Name = "${local.name_prefix}-render-node"
    }
  }

  # Batch recreates the environment before destroying the old one on changes.
  lifecycle {
    create_before_destroy = true
  }

  depends_on = [aws_iam_role_policy_attachment.batch_service]
}

# ------------------------------- Job queue -----------------------------------

resource "aws_batch_job_queue" "render" {
  name     = "${local.name_prefix}-render"
  state    = "ENABLED"
  priority = 1

  compute_environment_order {
    order               = 1
    compute_environment = aws_batch_compute_environment.render.arn
  }

  tags = {
    Name = "${local.name_prefix}-render"
  }
}

# ---------------------------- Job definition ---------------------------------

# Parameterized render job. Parameters (episode id, input/output prefixes,
# frame range) are supplied at submit time and substituted into the container
# command via Batch's Ref:: syntax. The container IMAGE is pluggable and
# user-supplied via var.render_container_image; the creative 3D render /
# compositing payload is intentionally NOT provided by this IaC.
resource "aws_batch_job_definition" "render" {
  name = "${local.name_prefix}-render"
  type = "container"

  # Default parameter values; override per submission.
  parameters = {
    episode_id    = "ep000"
    input_prefix  = "s3://${local.assets_bucket_name}/scenes/ep000/"
    output_prefix = "s3://${local.masters_bucket_name}/renders/ep000/"
    frame_start   = "1"
    frame_end     = "1"
  }

  # Automatically retry transient Spot interruptions.
  retry_strategy {
    attempts = 2
  }

  # A render job should not run forever; 6h ceiling per attempt.
  timeout {
    attempt_duration_seconds = 21600
  }

  container_properties = jsonencode({
    image      = var.render_container_image
    jobRoleArn = aws_iam_role.render_job.arn

    # The command demonstrates how the parameters flow to the render payload.
    # Replace with your render container's real entrypoint arguments.
    command = [
      "--episode", "Ref::episode_id",
      "--input", "Ref::input_prefix",
      "--output", "Ref::output_prefix",
      "--frame-start", "Ref::frame_start",
      "--frame-end", "Ref::frame_end",
    ]

    # Resource requirements: vCPU, memory and GPUs. g5.xlarge has 1 A10G GPU,
    # 4 vCPU, 16 GiB; request conservatively so the job fits the smallest
    # candidate instance type.
    resourceRequirements = [
      { type = "VCPU", value = "4" },
      { type = "MEMORY", value = "15000" },
      { type = "GPU", value = "1" },
    ]

    environment = [
      { name = "ASSETS_BUCKET", value = local.assets_bucket_name },
      { name = "MASTERS_BUCKET", value = local.masters_bucket_name },
    ]

    logConfiguration = {
      logDriver = "awslogs"
      options = {
        "awslogs-group"         = "/aws/batch/${local.name_prefix}"
        "awslogs-region"        = var.aws_region
        "awslogs-stream-prefix" = "render"
      }
    }
  })

  tags = {
    Name = "${local.name_prefix}-render"
  }
}
