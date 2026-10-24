# Deployment: OIDC-based GitHub Actions pipeline

This document describes how the `infra/` Terraform module is deployed to AWS
through GitHub Actions using GitHub OIDC federation, with no long-lived AWS
access keys stored anywhere.

- **Target account:** `079184685437`
- **Target region:** `ap-south-1` (Mumbai)
- **Pipeline:** `terraform plan` on pull requests into `main`, `terraform
  apply` on merges (pushes) to `main`.
- **Credentials:** short-lived STS credentials obtained per run by assuming an
  IAM role through OIDC. The only AWS identifier stored in GitHub is the deploy
  role ARN, which is not a secret. No access keys exist.

## How the credential flow works

1. A workflow run on `pull_request` or `push` requests a GitHub-signed OIDC
   token (the workflow grants `id-token: write`).
2. [`aws-actions/configure-aws-credentials@v4`](https://github.com/aws-actions/configure-aws-credentials)
   presents that token to AWS STS and calls `AssumeRoleWithWebIdentity` against
   the deploy role ARN in `vars.AWS_DEPLOY_ROLE_ARN`.
3. The deploy role's trust policy only accepts tokens whose audience is
   `sts.amazonaws.com` and whose subject matches this repository
   (`repo:999blue9999-collab/Cartoon:*`, tightened to `ref:refs/heads/main` and
   the `pull_request` subject). Anything else is rejected by AWS.
4. STS returns temporary credentials valid for one hour. Terraform uses them to
   plan (PRs) or plan and apply (pushes to `main`).

No static `aws_access_key_id` / `aws_secret_access_key` is ever created,
exchanged, or stored.

## One-time bootstrap (run once, by hand, with admin credentials)

There is a chicken-and-egg problem: the deploy role and the S3 state backend
must exist before the pipeline can assume a role or store remote state. The
[`bootstrap/`](../bootstrap/) module solves this. It is applied ONCE by a human
with administrative credentials and is intentionally NOT part of the
plan/apply cycle the pipeline runs. It keeps LOCAL state (no S3 backend of its
own).

`bootstrap/` creates:

- an `aws_iam_openid_connect_provider` for `token.actions.githubusercontent.com`
  (audience `sts.amazonaws.com`);
- an `aws_iam_role` (`golu-prod-github-deploy`) whose trust policy restricts
  `sts:AssumeRoleWithWebIdentity` to this repository's `main` branch and
  pull-request subjects;
- a least-privilege deploy policy sufficient to manage everything in `infra/`;
- the S3 state bucket (`golu-prod-tfstate-079184685437`) and the DynamoDB lock
  table (`golu-prod-tfstate-lock`).

### Steps

```sh
cd bootstrap
cp terraform.tfvars.example terraform.tfvars   # defaults already target this project
terraform init
terraform plan -out tfplan      # review carefully: this creates IAM + state resources
terraform apply tfplan
```

Note the outputs, especially `deploy_role_arn`, `state_bucket_name`,
`state_lock_table_name`, and `backend_config_hint`.

> Keep `bootstrap/`'s local state file (`terraform.tfstate`) safe. It is small
> but records the OIDC provider, deploy role, and state backend. Store it in a
> secure location or a separate backend of your choosing; it is gitignored.

## Set the repository variable

In the GitHub repository, go to **Settings -> Secrets and variables -> Actions
-> Variables** and add a repository variable:

- **Name:** `AWS_DEPLOY_ROLE_ARN`
- **Value:** the `deploy_role_arn` output from `bootstrap/`. Expected shape:

  ```
  arn:aws:iam::079184685437:role/golu-prod-github-deploy
  ```

This is a **variable**, not a secret, because a role ARN is not sensitive. No
AWS access keys are added to the repository.

## Migrate the infra/ state to the S3 backend

`infra/` now has a partial S3 backend block (see
[`infra/backend.tf`](../infra/backend.tf)). The bucket and lock table created by
`bootstrap/` already exist, so initialize `infra/` against them:

```sh
cd infra
terraform init \
  -backend-config="bucket=golu-prod-tfstate-079184685437" \
  -backend-config="key=infra/terraform.tfstate" \
  -backend-config="region=ap-south-1" \
  -backend-config="dynamodb_table=golu-prod-tfstate-lock" \
  -backend-config="encrypt=true"
```

Or use the tracked example file:

```sh
cd infra
cp backend.hcl.example backend.hcl
terraform init -backend-config=backend.hcl
```

If `infra/` already has local state from an earlier apply, Terraform prompts to
copy that existing state into the new S3 backend during `init`; answer `yes`.
On a brand-new deployment there is nothing to migrate.

## Thereafter: PRs plan, merges apply

Once bootstrap is applied and `AWS_DEPLOY_ROLE_ARN` is set, the pipeline in
[`.github/workflows/terraform.yml`](../.github/workflows/terraform.yml) runs
automatically:

- **Open or update a pull request into `main`:** the `plan` job runs
  `terraform fmt -check`, `init` (against the S3 backend), `validate`, and
  `plan`. It never applies.
- **Merge the pull request (push to `main`):** the `apply` job runs `init`,
  `validate`, `plan`, then `apply`. Apply is gated with
  `if: github.event_name == 'push' && github.ref == 'refs/heads/main'`, so it
  can only run on `main`.

The region is pinned to `ap-south-1` and the backend is pinned to the bootstrap
bucket/table through workflow `env` values, so no run has to guess where state
lives or which region to target.

## What is deliberately NOT in the pipeline

- **No access keys.** OIDC only. The role ARN is the single configured value.
- **No environment dumps.** The workflow never runs `printenv`, `env`, `set`,
  or prints `process.env`. Each step references only the specific variables it
  needs.
- **No apply on branches other than `main`.** The apply job is gated to pushes
  on `main`.
- **No bootstrap on every run.** The OIDC provider, deploy role, and state
  backend are created once out of band, not on each merge.

## Security summary

- Trust is restricted to this repository and to `main` + pull-request subjects,
  so no other repository or arbitrary branch can assume the deploy role.
- The deploy policy is scoped to this project's resource name prefix
  (`golu-prod-*`) for IAM and to the specific services `infra/` uses; it is not
  an admin role (no `iam:*`, no `Action: "*"`).
- Remote state lives in a private, versioned, encrypted S3 bucket with DynamoDB
  locking, so concurrent applies cannot corrupt state.
