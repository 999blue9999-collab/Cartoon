# -----------------------------------------------------------------------------
# Content delivery: CloudFront in front of the PRIVATE masters/delivery bucket.
#
# Per the outline (Section 9), masters are self-streamed/distributed via
# CloudFront. The masters bucket stays fully PRIVATE (public access block is
# on, see storage.tf); CloudFront reaches it through an Origin Access Control
# (OAC, SigV4) and an S3 bucket policy that grants read ONLY to this specific
# distribution. Viewers are forced to HTTPS (redirect-to-https).
#
# NOTE: the default cache behavior serves the whole bucket so both the
# /chapters/chN/ review masters and the /feature/ cut are reachable; restrict
# or add path-specific behaviors (e.g. signed URLs for review masters) in a
# hardened deployment.
# -----------------------------------------------------------------------------

resource "aws_cloudfront_origin_access_control" "masters" {
  name                              = "${local.name_prefix}-masters-oac"
  description                       = "OAC letting CloudFront read the private GOLU masters bucket."
  origin_access_control_origin_type = "s3"
  signing_behavior                  = "always"
  signing_protocol                  = "sigv4"
}

locals {
  masters_origin_id = "${local.name_prefix}-masters-s3"
}

resource "aws_cloudfront_distribution" "masters" {
  enabled         = true
  comment         = "GOLU masters/delivery distribution (private S3 via OAC)."
  is_ipv6_enabled = true

  origin {
    domain_name              = aws_s3_bucket.masters.bucket_regional_domain_name
    origin_id                = local.masters_origin_id
    origin_access_control_id = aws_cloudfront_origin_access_control.masters.id
  }

  default_cache_behavior {
    target_origin_id       = local.masters_origin_id
    viewer_protocol_policy = "redirect-to-https"
    allowed_methods        = ["GET", "HEAD", "OPTIONS"]
    cached_methods         = ["GET", "HEAD"]
    compress               = true

    # AWS managed "CachingOptimized" cache policy (recommended for S3 origins).
    cache_policy_id = "658327ea-f89d-4fab-a63d-7e88639e58f6"
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  viewer_certificate {
    # Default *.cloudfront.net certificate. Attach an ACM cert + aliases for a
    # custom domain in a hardened deployment.
    cloudfront_default_certificate = true
  }

  # PriceClass_200 covers Asia (incl. India) + Europe + North America at lower
  # cost than All; adjust if a different audience geography is targeted.
  price_class = "PriceClass_200"

  tags = {
    Name = "${local.name_prefix}-masters"
  }
}

# Bucket policy: allow ONLY this CloudFront distribution (via OAC, matched by
# the AWS:SourceArn condition) to read objects. The bucket itself stays private.
data "aws_iam_policy_document" "masters_cdn" {
  statement {
    sid       = "AllowCloudFrontOACRead"
    effect    = "Allow"
    actions   = ["s3:GetObject"]
    resources = ["${aws_s3_bucket.masters.arn}/*"]

    principals {
      type        = "Service"
      identifiers = ["cloudfront.amazonaws.com"]
    }

    condition {
      test     = "StringEquals"
      variable = "AWS:SourceArn"
      values   = [aws_cloudfront_distribution.masters.arn]
    }
  }
}

resource "aws_s3_bucket_policy" "masters_cdn" {
  bucket = aws_s3_bucket.masters.id
  policy = data.aws_iam_policy_document.masters_cdn.json

  # The public access block must not reject this policy: it does not grant
  # public access (principal is the CloudFront service with a SourceArn
  # condition), so it is compatible with block_public_policy = true.
  depends_on = [aws_s3_bucket_public_access_block.masters]
}
