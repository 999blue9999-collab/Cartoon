# -----------------------------------------------------------------------------
# Media / AI service layer: AWS Elemental MediaConvert (mastering), Amazon Polly
# (scratch Hindi TTS) and Amazon Transcribe (Hindi/English subtitles).
#
# Pipeline shape (per golu-kal-ka-dost-film-outline.md Section 9 + the
# golu-3x44-release-plan.md delivery layout):
#
#   1. The render farm writes finished frames to  s3://<masters>/renders/<ep>/.
#   2. MediaConvert masters those into two deliverables per chapter/feature:
#        - a ProRes 1080p MEZZANINE master (archival / high-bitrate)
#        - an H.264  1080p REVIEW master
#      both landing under the delivery prefixes:
#        s3://<masters>/chapters/chN/   (one per 44:00 broadcast chapter)
#        s3://<masters>/feature/        (the 132:00 feature cut)
#   3. Amazon Transcribe generates Hindi (hi-IN) + English subtitle SRT/VTT,
#      written under  s3://<masters>/subtitles/.
#   4. Amazon Polly produces SCRATCH/animatic Hindi voice only, written under
#      s3://<assets>/scratch-audio/  (never a final deliverable).
#
# SOURCE CADENCE NOTE: the creative is finished "12fps-on-twos" (each drawing
# held for two frames) and conformed to a 24fps timeline. MediaConvert outputs
# are therefore authored at 24fps progressive (see framerate settings below);
# no retiming/interpolation is performed here, the frames arrive at 24fps.
#
# These services are API-driven (no servers to provision). This file creates
# the MediaConvert queue + job templates and the IAM roles each service
# assumes. Jobs themselves are submitted by the orchestration layer (the
# pipeline role from iam.tf) at run time; see the invocation notes below and
# docs for the exact submit calls.
# -----------------------------------------------------------------------------

# ============================ MediaConvert ===================================

resource "aws_media_convert_queue" "masters" {
  name        = "${local.name_prefix}-masters"
  description = "Mastering queue for GOLU chapter and feature deliverables."
  status      = "ACTIVE"

  tags = {
    Name = "${local.name_prefix}-masters"
  }
}

# IAM role MediaConvert assumes to read render frames and write masters.
data "aws_iam_policy_document" "mediaconvert_assume" {
  statement {
    sid     = "AllowMediaConvertAssume"
    effect  = "Allow"
    actions = ["sts:AssumeRole"]

    principals {
      type        = "Service"
      identifiers = ["mediaconvert.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "mediaconvert" {
  name               = "${local.name_prefix}-mediaconvert"
  description        = "Role MediaConvert assumes to read renders/assets and write mastered deliverables."
  assume_role_policy = data.aws_iam_policy_document.mediaconvert_assume.json

  tags = {
    Name = "${local.name_prefix}-mediaconvert"
  }
}

data "aws_iam_policy_document" "mediaconvert" {
  # Read finished render frames and any source assets used as inputs.
  statement {
    sid    = "ReadSources"
    effect = "Allow"
    actions = [
      "s3:GetObject",
      "s3:GetObjectVersion",
    ]
    resources = [
      "${aws_s3_bucket.masters.arn}/renders/*",
      "${aws_s3_bucket.assets.arn}/*",
    ]
  }

  statement {
    sid    = "ListSourceBuckets"
    effect = "Allow"
    actions = [
      "s3:ListBucket",
      "s3:GetBucketLocation",
    ]
    resources = [
      aws_s3_bucket.masters.arn,
      aws_s3_bucket.assets.arn,
    ]
  }

  # Write mastered chapter/feature deliverables.
  statement {
    sid    = "WriteMasters"
    effect = "Allow"
    actions = [
      "s3:PutObject",
    ]
    resources = [
      "${aws_s3_bucket.masters.arn}/chapters/*",
      "${aws_s3_bucket.masters.arn}/feature/*",
    ]
  }
}

resource "aws_iam_policy" "mediaconvert" {
  name        = "${local.name_prefix}-mediaconvert"
  description = "Least-privilege S3 access for MediaConvert mastering jobs."
  policy      = data.aws_iam_policy_document.mediaconvert.json
}

resource "aws_iam_role_policy_attachment" "mediaconvert" {
  role       = aws_iam_role.mediaconvert.name
  policy_arn = aws_iam_policy.mediaconvert.arn
}

# --------------------------- Job templates -----------------------------------
#
# Two reusable job templates master the SAME source render at 1080p/24fps:
#
#   * <prefix>-h264-1080p-review  -> single-rate H.264 .mp4 review master
#   * <prefix>-prores-1080p-mezz  -> ProRes .mov mezzanine (archival) master
#
# HOW THE EPISODE/CHAPTER IS PASSED AT SUBMIT TIME:
#   These templates intentionally fix only the CODEC/mastering settings, not
#   the input/output paths. The orchestration layer (pipeline role) submits a
#   CreateJob call that references the template by name and OVERRIDES:
#     - Settings.Inputs[0].FileInput
#         = s3://<masters>/renders/<episode-id>/<conformed-source>.mov
#     - Settings.OutputGroups[0].OutputGroupSettings
#         .FileGroupSettings.Destination
#         = s3://<masters>/chapters/ch<N>/   (per-chapter deliverable)
#           or s3://<masters>/feature/       (the feature cut)
#   i.e. the chapter (ch1..chN) or feature target is chosen per submission by
#   setting the output Destination; the template supplies everything else.
#
# The Destination values below are PLACEHOLDERS pointing at /chapters/ and
# /feature/ so the layout is self-documenting; real jobs override them.
#
# PROVIDER NOTE: hashicorp/aws (v5) has NO native aws_media_convert_job_template
# resource (only aws_media_convert_queue). MediaConvert JobTemplates are managed
# here via CloudFormation (AWS::MediaConvert::JobTemplate) wrapped in
# aws_cloudformation_stack, the standard Terraform path for AWS resource types
# the provider does not expose. They remain real, named, managed resources.

# H.264 1080p review master template.
resource "aws_cloudformation_stack" "mediaconvert_h264_1080p_review" {
  name = "${local.name_prefix}-h264-1080p-review"

  template_body = jsonencode({
    Resources = {
      JobTemplate = {
        Type = "AWS::MediaConvert::JobTemplate"
        Properties = {
          Name        = "${local.name_prefix}-h264-1080p-review"
          Description = "H.264 1080p24 review master (.mp4) for a GOLU chapter or feature cut."
          Queue       = aws_media_convert_queue.masters.arn
          SettingsJson = {
            TimecodeConfig = { Source = "ZEROBASED" }
            OutputGroups = [
              {
                Name = "H264 Review Master"
                OutputGroupSettings = {
                  Type = "FILE_GROUP_SETTINGS"
                  FileGroupSettings = {
                    # Overridden per submission to /chapters/chN/ or /feature/.
                    Destination = "s3://${local.masters_bucket_name}/chapters/ch1/review/"
                  }
                }
                Outputs = [
                  {
                    NameModifier      = "_h264_1080p_review"
                    ContainerSettings = { Container = "MP4" }
                    VideoDescription = {
                      Width  = 1920
                      Height = 1080
                      CodecSettings = {
                        Codec = "H_264"
                        H264Settings = {
                          RateControlMode = "QVBR"
                          QvbrSettings    = { QvbrQualityLevel = 8 }
                          MaxBitrate      = 12000000
                          # 24fps progressive matches the conformed 24fps timeline.
                          FramerateControl     = "SPECIFIED"
                          FramerateNumerator   = 24
                          FramerateDenominator = 1
                          GopSizeUnits         = "FRAMES"
                          GopSize              = 48
                          CodecProfile         = "HIGH"
                          CodecLevel           = "AUTO"
                        }
                      }
                    }
                    AudioDescriptions = [
                      {
                        CodecSettings = {
                          Codec = "AAC"
                          AacSettings = {
                            Bitrate    = 192000
                            CodingMode = "CODING_MODE_2_0"
                            SampleRate = 48000
                          }
                        }
                      }
                    ]
                  }
                ]
              }
            ]
          }
        }
      }
    }
    Outputs = {
      JobTemplateName = { Value = { Ref = "JobTemplate" } }
    }
  })

  tags = {
    Name = "${local.name_prefix}-h264-1080p-review"
  }
}

# ProRes 1080p mezzanine (archival) master template.
resource "aws_cloudformation_stack" "mediaconvert_prores_1080p_mezz" {
  name = "${local.name_prefix}-prores-1080p-mezz"

  template_body = jsonencode({
    Resources = {
      JobTemplate = {
        Type = "AWS::MediaConvert::JobTemplate"
        Properties = {
          Name        = "${local.name_prefix}-prores-1080p-mezz"
          Description = "Apple ProRes 1080p24 mezzanine master (.mov) for a GOLU chapter or feature cut."
          Queue       = aws_media_convert_queue.masters.arn
          SettingsJson = {
            TimecodeConfig = { Source = "ZEROBASED" }
            OutputGroups = [
              {
                Name = "ProRes Mezzanine Master"
                OutputGroupSettings = {
                  Type = "FILE_GROUP_SETTINGS"
                  FileGroupSettings = {
                    # Overridden per submission to /chapters/chN/ or /feature/.
                    Destination = "s3://${local.masters_bucket_name}/chapters/ch1/mezzanine/"
                  }
                }
                Outputs = [
                  {
                    NameModifier      = "_prores_1080p_mezz"
                    ContainerSettings = { Container = "MOV" }
                    VideoDescription = {
                      Width  = 1920
                      Height = 1080
                      CodecSettings = {
                        Codec = "PRORES"
                        ProresSettings = {
                          CodecProfile = "APPLE_PRORES_422_HQ"
                          # 24fps progressive matches the conformed 24fps timeline.
                          FramerateControl     = "SPECIFIED"
                          FramerateNumerator   = 24
                          FramerateDenominator = 1
                        }
                      }
                    }
                    AudioDescriptions = [
                      {
                        CodecSettings = {
                          # PCM audio for an archival mezzanine.
                          Codec = "WAV"
                          WavSettings = {
                            BitDepth   = 24
                            Channels   = 2
                            SampleRate = 48000
                          }
                        }
                      }
                    ]
                  }
                ]
              }
            ]
          }
        }
      }
    }
    Outputs = {
      JobTemplateName = { Value = { Ref = "JobTemplate" } }
    }
  })

  tags = {
    Name = "${local.name_prefix}-prores-1080p-mezz"
  }
}

# =============================== Amazon Polly ================================
#
# POLLY IS SCRATCH-ONLY. Amazon Polly Hindi NEURAL voice (e.g. "Kajal", with
# "Aditi" as a standard-voice fallback where neural is unavailable) is used
# ONLY to generate SCRATCH / animatic voice tracks while the real voice cast is
# being recorded. It is NEVER used for a final deliverable. Final character
# voice-over is human-recorded for consistent, performed delivery.
#
# Polly is API-driven; there is no server to provision. The orchestration layer
# assumes the role below and calls one of:
#
#   # Short lines -> synchronous synthesis (bytes returned inline):
#   aws polly synthesize-speech \
#     --engine neural --voice-id Kajal --language-code hi-IN \
#     --output-format mp3 --text "Namaste, main Golu hoon." \
#     s3-is-not-used-here.mp3
#
#   # Longer passages -> async task writing straight to S3 scratch-audio:
#   aws polly start-speech-synthesis-task \
#     --engine neural --voice-id Kajal --language-code hi-IN \
#     --output-format mp3 \
#     --output-s3-bucket-name <assets-bucket> \
#     --output-s3-key-prefix scratch-audio/ \
#     --text file://line.txt
#
# The role is therefore scoped to polly synth actions plus S3 writes limited to
# the assets bucket's scratch-audio/ prefix.

data "aws_iam_policy_document" "polly_assume" {
  statement {
    sid     = "AllowPollyPipelineAssume"
    effect  = "Allow"
    actions = ["sts:AssumeRole"]

    principals {
      # Assumed by the orchestration layer (Lambda / Step Functions) that
      # drives scratch-track generation.
      type = "Service"
      identifiers = [
        "lambda.amazonaws.com",
        "states.amazonaws.com",
      ]
    }
  }
}

resource "aws_iam_role" "polly" {
  name               = "${local.name_prefix}-polly-scratch"
  description        = "Role for generating SCRATCH/animatic Hindi TTS via Amazon Polly (never final)."
  assume_role_policy = data.aws_iam_policy_document.polly_assume.json

  tags = {
    Name = "${local.name_prefix}-polly-scratch"
  }
}

data "aws_iam_policy_document" "polly" {
  # Synchronous + asynchronous neural TTS. These Polly actions are not
  # resource-scopable, so they use resource "*" (documented).
  statement {
    sid    = "SynthesizeScratchSpeech"
    effect = "Allow"
    actions = [
      "polly:SynthesizeSpeech",
      "polly:StartSpeechSynthesisTask",
      "polly:GetSpeechSynthesisTask",
      "polly:ListSpeechSynthesisTasks",
    ]
    resources = ["*"]
  }

  # Async tasks write their audio to S3; limit writes to the scratch-audio
  # prefix of the assets bucket only (never masters, never final).
  statement {
    sid    = "WriteScratchAudio"
    effect = "Allow"
    actions = [
      "s3:PutObject",
    ]
    resources = [
      "${aws_s3_bucket.assets.arn}/scratch-audio/*",
    ]
  }
}

resource "aws_iam_policy" "polly" {
  name        = "${local.name_prefix}-polly-scratch"
  description = "Polly neural synthesis + scratch-audio S3 write (scratch tracks only)."
  policy      = data.aws_iam_policy_document.polly.json
}

resource "aws_iam_role_policy_attachment" "polly" {
  role       = aws_iam_role.polly.name
  policy_arn = aws_iam_policy.polly.arn
}

# ============================ Amazon Transcribe ==============================
#
# SUBTITLE PATH. Amazon Transcribe generates subtitles from the mastered /
# conformed audio for TWO language tracks, aligning with the per-chapter
# Hindi/English subtitle deliverables in golu-3x44-release-plan.md:
#
#   - Hindi   : language-code hi-IN
#   - English : language-code en-IN (fallback en-US)
#
# Transcribe can emit SRT and/or VTT directly (SubtitleFormats = ["srt","vtt"]).
# Output lands under the masters bucket subtitle prefix:
#
#   s3://<masters>/subtitles/<episode-id>/<lang>.srt   (and .vtt)
#
# and the per-chapter review/mezzanine deliverables then reference these SRTs.
#
# DEVANAGARI CLEANUP POST-STEP (documented, automatable): raw Hindi Transcribe
# output needs a light cleanup pass before it ships, typically:
#   1. Normalize Devanagari Unicode (NFC) and strip stray combining marks.
#   2. Fix spacing around danda (।) / double-danda (॥) and punctuation.
#   3. Correct common proper-noun / brand spellings (character names, "jalebi",
#      "Defender", etc.) against a project glossary.
#   4. Re-flow line breaks to <= 2 lines / ~42 chars for broadcast captions.
# This runs as a small post-processing Lambda (or manual pass) that reads the
# raw .srt from s3://<masters>/subtitles/.../raw/ and writes the cleaned .srt
# alongside it. The role below permits reading the audio input and writing both
# the raw and cleaned subtitle objects under the subtitles/ prefix.

data "aws_iam_policy_document" "transcribe_assume" {
  statement {
    sid     = "AllowTranscribePipelineAssume"
    effect  = "Allow"
    actions = ["sts:AssumeRole"]

    principals {
      type = "Service"
      identifiers = [
        "transcribe.amazonaws.com",
        "lambda.amazonaws.com",
        "states.amazonaws.com",
      ]
    }
  }
}

resource "aws_iam_role" "transcribe" {
  name               = "${local.name_prefix}-transcribe"
  description        = "Role for generating Hindi/English subtitles (SRT/VTT) via Amazon Transcribe."
  assume_role_policy = data.aws_iam_policy_document.transcribe_assume.json

  tags = {
    Name = "${local.name_prefix}-transcribe"
  }
}

data "aws_iam_policy_document" "transcribe" {
  # StartTranscriptionJob / GetTranscriptionJob are not resource-scopable;
  # resource "*" is required (documented).
  statement {
    sid    = "RunTranscriptionJobs"
    effect = "Allow"
    actions = [
      "transcribe:StartTranscriptionJob",
      "transcribe:GetTranscriptionJob",
      "transcribe:ListTranscriptionJobs",
    ]
    resources = ["*"]
  }

  # Read the audio input (mastered/conformed audio lives in the masters bucket;
  # scratch audio inputs may live in the assets bucket).
  statement {
    sid    = "ReadAudioInput"
    effect = "Allow"
    actions = [
      "s3:GetObject",
      "s3:GetObjectVersion",
    ]
    resources = [
      "${aws_s3_bucket.masters.arn}/renders/*",
      "${aws_s3_bucket.masters.arn}/chapters/*",
      "${aws_s3_bucket.masters.arn}/feature/*",
      "${aws_s3_bucket.assets.arn}/audio/*",
    ]
  }

  statement {
    sid       = "ListSubtitleBucket"
    effect    = "Allow"
    actions   = ["s3:ListBucket", "s3:GetBucketLocation"]
    resources = [aws_s3_bucket.masters.arn]
  }

  # Write SRT/VTT (raw + cleaned) under the masters subtitle prefix.
  statement {
    sid    = "WriteSubtitles"
    effect = "Allow"
    actions = [
      "s3:GetObject",
      "s3:PutObject",
    ]
    resources = [
      "${aws_s3_bucket.masters.arn}/subtitles/*",
    ]
  }
}

resource "aws_iam_policy" "transcribe" {
  name        = "${local.name_prefix}-transcribe"
  description = "Transcribe job actions + S3 read audio / write subtitles."
  policy      = data.aws_iam_policy_document.transcribe.json
}

resource "aws_iam_role_policy_attachment" "transcribe" {
  role       = aws_iam_role.transcribe.name
  policy_arn = aws_iam_policy.transcribe.arn
}
