# bootstrap/

One-time, apply-by-hand Terraform that enables the OIDC deploy pipeline for the
`infra/` module. This is deliberately a SEPARATE root module from `infra/` so it
is applied once with administrative credentials and is NOT part of the
plan/apply cycle the GitHub Actions pipeline runs.

## What it creates

- `aws_iam_openid_connect_provider.github` for
  `token.actions.githubusercontent.com` (audience `sts.amazonaws.com`).
- `aws_iam_role.deploy` (`golu-prod-github-deploy`) with a trust policy that
  allows `sts:AssumeRoleWithWebIdentity` only from this repository
  (`repo:999blue9999-collab/Cartoon:*`), tightened to the `main` branch and the
  pull-request subject, and only for audience `sts.amazonaws.com`.
- A least-privilege-ish deploy policy sufficient to manage everything in
  `infra/` (S3, Batch, the EC2 bits the compute environment needs, IAM role and
  policy management scoped to the project name prefix, MediaConvert, the
  CloudFormation stacks wrapping the MediaConvert job templates, CloudFront,
  Polly/Transcribe IAM, CloudWatch logs/alarms/dashboards, SNS, AWS Budgets),
  plus read/write to the state backend.
- The Terraform remote state backend: a private, versioned, encrypted S3 bucket
  (`golu-prod-tfstate-079184685437`) and a DynamoDB lock table
  (`golu-prod-tfstate-lock`).

## Why it is separate (chicken-and-egg)

The deploy role and the state backend must exist before the pipeline can assume
a role or store remote state. `bootstrap/` therefore keeps LOCAL state (no
backend block here) and is applied once, out of band, by an administrator.

## Usage

```sh
cd bootstrap
cp terraform.tfvars.example terraform.tfvars   # defaults already target account 079184685437 / ap-south-1
terraform init
terraform plan -out tfplan
terraform apply tfplan
```

Then set the repository variable `AWS_DEPLOY_ROLE_ARN` to the `deploy_role_arn`
output. See [`../docs/deployment.md`](../docs/deployment.md) for the full
end-to-end procedure.

## Account and region

Account `079184685437`, region `ap-south-1` (Mumbai). The provider is pinned to
`allowed_account_ids = [var.aws_account_id]` so an accidental apply against the
wrong account fails fast.

## Sandbox note

This repository is validated in a credential-free sandbox, so only
`terraform fmt -check -recursive`, `terraform init -backend=false`, and
`terraform validate` run here. Never run `plan`/`apply` in the sandbox; this
module is applied on a workstation with administrative AWS credentials.
