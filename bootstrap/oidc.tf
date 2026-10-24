# -----------------------------------------------------------------------------
# GitHub OIDC federation: identity provider + deploy role trust policy.
#
# This replaces long-lived AWS access keys in GitHub with short-lived STS
# credentials. GitHub Actions presents a signed OIDC token to AWS STS; the
# trust policy below restricts which repository (and which refs) may exchange
# that token for the deploy role, and requires the audience to be
# sts.amazonaws.com.
#
# NO access keys are ever created or stored. The only value GitHub needs is the
# deploy role ARN (output below), which is not a secret.
# -----------------------------------------------------------------------------

# GitHub's OIDC identity provider for the account.
#
# THUMBPRINT NOTE: AWS now validates the GitHub OIDC endpoint's TLS certificate
# against its own trusted certificate authority store, so the thumbprint is no
# longer security-critical for provider creation. The historical GitHub
# intermediate CA thumbprint is supplied to satisfy the API, which still
# requires a non-empty thumbprint_list.
resource "aws_iam_openid_connect_provider" "github" {
  url             = "https://token.actions.githubusercontent.com"
  client_id_list  = ["sts.amazonaws.com"]
  thumbprint_list = ["6938fd4d98bab03faadb97b34396831e3780aea1"]

  tags = {
    Name = "github-actions-oidc"
  }
}

locals {
  # OIDC subjects (`sub` claim) that may assume the deploy role:
  #   - one per allowed branch push:  repo:<owner>/<repo>:ref:refs/heads/<ref>
  #   - optionally pull_request runs:  repo:<owner>/<repo>:pull_request
  repo_prefix = "repo:${var.github_owner}/${var.github_repo}"

  branch_subjects = [
    for ref in var.allowed_git_refs : "${local.repo_prefix}:ref:refs/heads/${ref}"
  ]

  pull_request_subjects = var.allow_pull_request_oidc ? ["${local.repo_prefix}:pull_request"] : []

  allowed_oidc_subjects = concat(local.branch_subjects, local.pull_request_subjects)
}

data "aws_iam_policy_document" "deploy_assume" {
  statement {
    sid     = "GitHubActionsOIDC"
    effect  = "Allow"
    actions = ["sts:AssumeRoleWithWebIdentity"]

    principals {
      type        = "Federated"
      identifiers = [aws_iam_openid_connect_provider.github.arn]
    }

    # Audience must be sts.amazonaws.com (set by configure-aws-credentials@v4).
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }

    # Restrict to this repository and the specific refs / pull_request subject,
    # so no other repo (and no arbitrary branch) can assume the deploy role.
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:sub"
      values   = local.allowed_oidc_subjects
    }
  }
}

resource "aws_iam_role" "deploy" {
  name                 = "${local.name_prefix}-github-deploy"
  description          = "Role assumed by GitHub Actions via OIDC to plan/apply the infra/ module."
  assume_role_policy   = data.aws_iam_policy_document.deploy_assume.json
  max_session_duration = 3600

  tags = {
    Name = "${local.name_prefix}-github-deploy"
  }
}

resource "aws_iam_role_policy_attachment" "deploy" {
  role       = aws_iam_role.deploy.name
  policy_arn = aws_iam_policy.deploy.arn
}
