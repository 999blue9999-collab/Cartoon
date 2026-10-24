# -----------------------------------------------------------------------------
# Storage layer: S3 buckets for the "GOLU - Kal Ka Dost" production pipeline.
#
# Two buckets are created:
#
#   1. Assets bucket  (local.assets_bucket_name)
#      Working assets and project files. Key-prefix conventions:
#        /characters/   consistent character model/turnaround/rig references
#        /backgrounds/  layout and background plates
#        /gadgets/      props and gadget assets
#        /audio/        source VO, music, SFX stems (and scratch Polly TTS)
#        /scenes/       per-scene working files / keyframe PNGs
#      Render scratch (intermediate frames) is written under /scratch/ and is
#      aged out quickly by lifecycle rules below.
#
#   2. Masters / renders bucket  (local.masters_bucket_name)
#      Mastered + delivery outputs. Key-prefix conventions mirror the
#      delivery layout in golu-3x44-release-plan.md:
#        /chapters/ch1/ ... /chapters/chN/   one prefix per broadcast chapter
#        /feature/                           the feature-length cut
#      Each of those prefixes holds, per the release plan:
#        - a mezzanine master (ProRes, archival/high-bitrate)
#        - a 1080p H.264 review master
#        - Hindi + English subtitle files (.srt)
#      Finished render frames land under /renders/<episode-id>/ before being
#      mastered by MediaConvert into the /chapters|feature prefixes.
#
# Both buckets have versioning ENABLED (the outline insists S3 versioning be on
# so masters and project files are recoverable), all public access blocked, and
# server-side encryption. Lifecycle rules expire old noncurrent versions and
# aggressively clean render scratch to control cost.
# -----------------------------------------------------------------------------

# ---------------------------- Assets bucket ----------------------------------

resource "aws_s3_bucket" "assets" {
  bucket = local.assets_bucket_name

  tags = {
    Name    = local.assets_bucket_name
    Purpose = "working-assets-and-project-files"
  }
}

resource "aws_s3_bucket_versioning" "assets" {
  bucket = aws_s3_bucket.assets.id

  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_public_access_block" "assets" {
  bucket = aws_s3_bucket.assets.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_server_side_encryption_configuration" "assets" {
  bucket = aws_s3_bucket.assets.id

  rule {
    apply_server_side_encryption_by_default {
      # AES256 (SSE-S3) keeps the module deployable without provisioning a KMS
      # key. Swap to "aws:kms" with a CMK for stricter key management.
      sse_algorithm = "AES256"
    }
    bucket_key_enabled = true
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "assets" {
  bucket = aws_s3_bucket.assets.id

  # Depend on versioning so the noncurrent-version rules are valid at apply.
  depends_on = [aws_s3_bucket_versioning.assets]

  # Expire noncurrent versions of working assets after 90 days to bound cost
  # while keeping a reasonable recovery window.
  rule {
    id     = "expire-noncurrent-asset-versions"
    status = "Enabled"

    filter {}

    noncurrent_version_transition {
      noncurrent_days = 30
      storage_class   = "STANDARD_IA"
    }

    noncurrent_version_expiration {
      noncurrent_days = 90
    }

    abort_incomplete_multipart_upload {
      days_after_initiation = 7
    }
  }

  # Render scratch (intermediate frames) is reproducible: delete after 14 days.
  rule {
    id     = "expire-render-scratch"
    status = "Enabled"

    filter {
      prefix = "scratch/"
    }

    expiration {
      days = 14
    }

    noncurrent_version_expiration {
      noncurrent_days = 7
    }
  }
}

# ------------------------- Masters / renders bucket --------------------------

resource "aws_s3_bucket" "masters" {
  bucket = local.masters_bucket_name

  tags = {
    Name    = local.masters_bucket_name
    Purpose = "mastered-and-delivery-outputs"
  }
}

resource "aws_s3_bucket_versioning" "masters" {
  bucket = aws_s3_bucket.masters.id

  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_public_access_block" "masters" {
  bucket = aws_s3_bucket.masters.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_server_side_encryption_configuration" "masters" {
  bucket = aws_s3_bucket.masters.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
    bucket_key_enabled = true
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "masters" {
  bucket = aws_s3_bucket.masters.id

  depends_on = [aws_s3_bucket_versioning.masters]

  # Masters are long-lived deliverables: keep a long noncurrent window but move
  # superseded versions to cheaper storage over time.
  rule {
    id     = "archive-noncurrent-master-versions"
    status = "Enabled"

    filter {}

    noncurrent_version_transition {
      noncurrent_days = 30
      storage_class   = "STANDARD_IA"
    }

    noncurrent_version_transition {
      noncurrent_days = 180
      storage_class   = "GLACIER"
    }

    noncurrent_version_expiration {
      noncurrent_days = 365
    }

    abort_incomplete_multipart_upload {
      days_after_initiation = 7
    }
  }

  # Raw render output under /renders/ is scratch once mastered: transition to
  # cheaper storage quickly and expire after 30 days.
  rule {
    id     = "expire-raw-renders"
    status = "Enabled"

    filter {
      prefix = "renders/"
    }

    transition {
      days          = 7
      storage_class = "STANDARD_IA"
    }

    expiration {
      days = 30
    }

    noncurrent_version_expiration {
      noncurrent_days = 7
    }
  }
}
