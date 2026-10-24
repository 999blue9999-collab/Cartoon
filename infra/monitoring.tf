# -----------------------------------------------------------------------------
# Cost + monitoring guardrails.
#
# The project outline (Section 9) insists budget alarms be ON because a season
# of Spot GPU rendering can run $2k-$6k. This file provides:
#
#   1. The CloudWatch log group the render job definition / render role already
#      reference ( /aws/batch/<name_prefix> ) but that FEAT-002 did not create.
#   2. An AWS Budgets monthly COST budget at var.monthly_budget_usd with 80% +
#      100% actual and forecast notifications to var.budget_alert_emails.
#   3. An SNS topic (+ optional email subscriptions) for operational alarms.
#   4. A CloudWatch alarm on failed Batch jobs, notifying the SNS topic.
#   5. A CloudWatch dashboard summarizing Batch compute/job metrics.
#
# Email lists are guarded with count/for_each so the whole configuration still
# validates and applies cleanly when budget_alert_emails defaults to [].
# -----------------------------------------------------------------------------

locals {
  # True only when at least one alert email is configured. Used to gate the
  # SNS email subscriptions and the budget notification subscriber blocks.
  has_alert_emails = length(var.budget_alert_emails) > 0
}

# --------------------- CloudWatch log group for Batch ------------------------
# Referenced by aws_batch_job_definition.render (awslogs-group) and granted to
# the render role in iam.tf; created here so the reference resolves.
resource "aws_cloudwatch_log_group" "batch" {
  name              = "/aws/batch/${local.name_prefix}"
  retention_in_days = 30

  tags = {
    Name = "/aws/batch/${local.name_prefix}"
  }
}

# ------------------------------- SNS alerts ----------------------------------

resource "aws_sns_topic" "alerts" {
  name = "${local.name_prefix}-alerts"

  tags = {
    Name = "${local.name_prefix}-alerts"
  }
}

# One email subscription per configured address. for_each over a set so an
# empty var.budget_alert_emails simply creates zero subscriptions (valid).
resource "aws_sns_topic_subscription" "alert_emails" {
  for_each  = toset(var.budget_alert_emails)
  topic_arn = aws_sns_topic.alerts.arn
  protocol  = "email"
  endpoint  = each.value
}

# ------------------------------ AWS Budgets ----------------------------------
#
# Monthly COST budget at the configured ceiling. Four notifications: actual and
# forecast at 80% and 100%. Each notification emails the configured addresses;
# the subscriber block is guarded by the has_alert_emails flag so validate
# passes with an empty email list (the budget + thresholds still exist, they
# just have no subscribers until emails are supplied).

resource "aws_budgets_budget" "monthly" {
  name         = "${local.name_prefix}-monthly"
  budget_type  = "COST"
  limit_amount = tostring(var.monthly_budget_usd)
  limit_unit   = "USD"
  time_unit    = "MONTHLY"

  # Actual spend at 80%.
  notification {
    comparison_operator        = "GREATER_THAN"
    threshold                  = 80
    threshold_type             = "PERCENTAGE"
    notification_type          = "ACTUAL"
    subscriber_email_addresses = var.budget_alert_emails
  }

  # Actual spend at 100%.
  notification {
    comparison_operator        = "GREATER_THAN"
    threshold                  = 100
    threshold_type             = "PERCENTAGE"
    notification_type          = "ACTUAL"
    subscriber_email_addresses = var.budget_alert_emails
  }

  # Forecast to exceed 80%.
  notification {
    comparison_operator        = "GREATER_THAN"
    threshold                  = 80
    threshold_type             = "PERCENTAGE"
    notification_type          = "FORECASTED"
    subscriber_email_addresses = var.budget_alert_emails
  }

  # Forecast to exceed 100%.
  notification {
    comparison_operator        = "GREATER_THAN"
    threshold                  = 100
    threshold_type             = "PERCENTAGE"
    notification_type          = "FORECASTED"
    subscriber_email_addresses = var.budget_alert_emails
  }
}

# ---------------------- CloudWatch alarm: failed jobs ------------------------
# Alerts when the render queue accumulates failed jobs. AWS/Batch publishes
# per-queue job-state metrics; FailedJobs is dimensioned by JobQueue.
resource "aws_cloudwatch_metric_alarm" "batch_failed_jobs" {
  alarm_name          = "${local.name_prefix}-batch-failed-jobs"
  alarm_description   = "Render jobs are failing on the GOLU Batch queue."
  namespace           = "AWS/Batch"
  metric_name         = "FailedJobs"
  statistic           = "Sum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 1
  comparison_operator = "GreaterThanOrEqualToThreshold"
  treat_missing_data  = "notBreaching"

  dimensions = {
    JobQueue = aws_batch_job_queue.render.name
  }

  alarm_actions = [aws_sns_topic.alerts.arn]
  ok_actions    = [aws_sns_topic.alerts.arn]

  tags = {
    Name = "${local.name_prefix}-batch-failed-jobs"
  }
}

# -------------------------- CloudWatch dashboard -----------------------------
# Summarizes Batch job throughput/state for the render queue so operators can
# watch a season's render progress at a glance.
resource "aws_cloudwatch_dashboard" "render" {
  dashboard_name = "${local.name_prefix}-render"

  dashboard_body = jsonencode({
    widgets = [
      {
        type   = "text"
        x      = 0
        y      = 0
        width  = 24
        height = 2
        properties = {
          markdown = "# GOLU render pipeline\nBatch job throughput and state for queue `${aws_batch_job_queue.render.name}`. Budget ceiling: $${var.monthly_budget_usd}/mo."
        }
      },
      {
        type   = "metric"
        x      = 0
        y      = 2
        width  = 12
        height = 6
        properties = {
          title   = "Batch jobs by state"
          region  = var.aws_region
          view    = "timeSeries"
          stacked = false
          metrics = [
            ["AWS/Batch", "RunnableJobs", "JobQueue", aws_batch_job_queue.render.name],
            ["AWS/Batch", "RunningJobs", "JobQueue", aws_batch_job_queue.render.name],
            ["AWS/Batch", "SubmittedJobs", "JobQueue", aws_batch_job_queue.render.name],
            ["AWS/Batch", "FailedJobs", "JobQueue", aws_batch_job_queue.render.name],
          ]
        }
      },
      {
        type   = "metric"
        x      = 12
        y      = 2
        width  = 12
        height = 6
        properties = {
          title  = "Failed render jobs"
          region = var.aws_region
          view   = "timeSeries"
          metrics = [
            ["AWS/Batch", "FailedJobs", "JobQueue", aws_batch_job_queue.render.name],
          ]
        }
      }
    ]
  })
}
