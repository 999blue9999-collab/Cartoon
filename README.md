# GOLU - Kal Ka Dost: AWS production pipeline (infrastructure as code)

This repository is the Terraform infrastructure as code for the AWS production
pipeline behind **GOLU - Kal Ka Dost**, a Hindi animated series designed to
scale toward 52 or more episodes per season (and to recombine into longer
broadcast chapters and a feature cut). It provisions the AWS building blocks
that a studio needs to store assets, render shots on GPUs at scale, master the
results, generate subtitles, deliver over a CDN, and keep a hard eye on cost.
The architecture follows Section 9 ("Production Sketch") of the project outline
in the reference pack.

> **Read the LIMITATIONS section before anything else.** This infrastructure
> does not itself produce finished 3D animation or neural lipsync. It is the
> AWS orchestration and scaffolding around a render payload that you supply.

## Why Terraform (and not AWS CDK)

Both Terraform and the [AWS CDK](https://aws.amazon.com/cdk/) can describe this
infrastructure. This project uses [Terraform](https://developer.hashicorp.com/terraform)
for a few concrete reasons:

- **Validates and plans without executing application code.** `terraform
  validate` runs the full type and reference check with no AWS credentials and
  no bootstrapped environment, which is exactly how this repo is verified in a
  sandbox. The CDK needs a Node or Python toolchain and a `cdk synth` step that
  runs your program first.
- **Declarative state is the whole artifact.** The `.tf` files plus the state
  file are the single source of truth. There is no synthesis step between the
  code you read and the CloudFormation that ships, which keeps the mapping
  between a resource in code and a resource in AWS direct and auditable.
- **One language for the whole team.** HCL is the only thing a reviewer needs
  to read. The pipeline here is infrastructure, not an application, so a
  general-purpose programming language buys little and costs onboarding time.
- **Provider breadth with an honest escape hatch.** Where the AWS provider has
  no native resource (MediaConvert job templates, see below), Terraform still
  manages the resource through a small CloudFormation stack, so nothing falls
  outside the state file.

## Architecture overview (mapped to outline Section 9)

Every item below corresponds to a real resource in [`infra/`](infra/). Nothing
here is aspirational.

| Outline Section 9 step | AWS service | Terraform file |
|---|---|---|
| Design and assets, versioned in S3 | Two S3 buckets (assets + masters), versioning on, public access blocked, SSE and lifecycle rules | [`infra/storage.tf`](infra/storage.tf) |
| Render heavy shots on EC2 GPU (g5 / Spot), the outline names Thinkbox Deadline | AWS Batch managed compute environment (g5 family, Spot capable), job queue, parameterized GPU job definition | [`infra/render.tf`](infra/render.tf) |
| Least-privilege access for render and orchestration | IAM roles and scoped policies for the render job and the pipeline | [`infra/iam.tf`](infra/iam.tf) |
| Master to H.264 and ProRes at 1080p | MediaConvert queue plus H.264 1080p review and ProRes 1080p mezzanine job templates | [`infra/media.tf`](infra/media.tf) |
| Scratch Hindi voice for animatics only | Amazon Polly IAM role and documented invocation path (Hindi neural voice, scratch only) | [`infra/media.tf`](infra/media.tf) |
| Hindi and English subtitles with Devanagari cleanup | Amazon Transcribe IAM role and documented hi-IN / en subtitle path | [`infra/media.tf`](infra/media.tf) |
| CloudFront self-stream from the masters bucket | CloudFront distribution with Origin Access Control over the private masters bucket | [`infra/delivery.tf`](infra/delivery.tf) |
| Budget alarms on, cost and health monitoring | AWS Budgets monthly cost budget, CloudWatch dashboard and alarm, SNS topic, Batch log group | [`infra/monitoring.tf`](infra/monitoring.tf) |

The render farm uses **AWS Batch** as the modern, infrastructure-as-code
substitute for the Thinkbox Deadline render manager named in the outline. Batch
gives a managed, autoscaling EC2 GPU pool with native Spot support and a
declarative job model, all expressible in Terraform, where Deadline would need
a separately licensed and manually operated farm. This substitution is
documented in the code and in [`docs/architecture.md`](docs/architecture.md).

## Per-episode job flow

```
assets in S3  ->  AWS Batch GPU render  ->  MediaConvert master  ->  Transcribe subtitles  ->  CloudFront deliver
```

1. **Assets in S3.** Characters, backgrounds, gadgets, audio and per-scene
   working files live in the assets bucket under documented key prefixes.
2. **AWS Batch GPU render.** The pipeline submits a parameterized job (episode
   id, input prefix, output prefix, frame range) to the g5 Spot queue. The
   container writes finished frames to `s3://<masters>/renders/<episode-id>/`.
3. **MediaConvert master.** A MediaConvert job template conforms the frames to
   1080p at 24fps and writes two deliverables (a ProRes mezzanine master and an
   H.264 review master) under `/chapters/chN/` or `/feature/`.
4. **Transcribe subtitles.** Amazon Transcribe generates Hindi (hi-IN) and
   English subtitles as SRT and VTT under `/subtitles/`, followed by a
   documented Devanagari cleanup step.
5. **CloudFront deliver.** A CloudFront distribution serves the private masters
   bucket over HTTPS through an Origin Access Control, so the bucket stays
   private while review and feature cuts stream to viewers. Public delivery is
   restricted to the finished `/chapters/chN/` and `/feature/` prefixes; raw
   renders, scratch, and work-in-progress subtitles are not reachable over the
   CDN.

A deeper component-by-component description, diagrams, and the scale math are in
[`docs/architecture.md`](docs/architecture.md).

## Repository layout

```
Cartoon/
  README.md                      this file
  docs/
    architecture.md              AWS pipeline deep dive, diagrams, scale math, limitations
    n8n-cartoon-pipeline.md      n8n cartoon automation: episodes, seasons, consistent characters/voices
    character-bible-template.md  template for the Character Bible (consistent cast across seasons)
  workflows/                     n8n workflow JSON templates (import into n8n)
    n8n-cartoon-season-producer.json   plan -> scenes -> TTS -> image -> video -> lipsync -> assemble -> publish
    n8n-character-onboarding.json      register one consistent character (ref image + voice clone)
  templates/                     Google Sheet templates that drive the n8n pipeline
    episodes.csv                 season/episode plan (status, approval, music, ...)
    scenes.csv                   per-scene breakdown (character, dialogue, action, ...)
    characters.csv               Character Bible starter
  services/episode-assembler/    FastAPI service: stitches scenes, music, subtitles into an episode MP4
  infra/                         Terraform root module (flat layout)
    versions.tf                  Terraform and AWS provider version pins
    providers.tf                 AWS provider, region, default tags
    variables.tf                 all input variables
    main.tf                      shared locals and account lookup
    storage.tf                   S3 assets and masters buckets
    iam.tf                       least-privilege render and pipeline roles
    render.tf                    AWS Batch g5 Spot render farm
    media.tf                     MediaConvert, Polly, Transcribe
    delivery.tf                  CloudFront over the private masters bucket
    monitoring.tf                Budgets, CloudWatch, SNS, log group
    outputs.tf                   bucket, role, queue, distribution outputs
    terraform.tfvars.example     example variable values (no secrets)
  docker-compose.yml             local n8n + episode assembler for development
```

## Configuring variables

Copy [`infra/terraform.tfvars.example`](infra/terraform.tfvars.example) to
`infra/terraform.tfvars` and adjust. The `terraform.tfvars` file is gitignored;
the `.example` file is tracked and contains only non-secret placeholders. The
variables you most often set:

| Variable | Purpose | Default |
|---|---|---|
| `aws_region` | Region for all resources (Mumbai suits an India-targeted series) | `ap-south-1` |
| `environment` | Environment name, used in tags and derived names | `prod` |
| `project_name` | Short identifier used to build the name prefix | `golu` |
| `name_prefix` | Override the derived `project-environment` prefix | `null` (derived) |
| `assets_bucket_name` | Assets bucket name (must be globally unique) | `null` (auto-derived) |
| `masters_bucket_name` | Masters and delivery bucket name (globally unique) | `null` (auto-derived) |
| `gpu_instance_types` | Candidate g5 instance types for the render pool | `["g5.xlarge", "g5.2xlarge", "g5.4xlarge"]` |
| `render_max_vcpus` | Maximum vCPUs the render pool scales to | `1024` |
| `use_spot` | Use EC2 Spot capacity for the render pool | `true` |
| `render_container_image` | Your pluggable render container image (see LIMITATIONS) | placeholder, must be replaced |
| `monthly_budget_usd` | Monthly cost budget for Budgets alerts | `6000` |
| `budget_alert_emails` | Emails that receive budget threshold alerts | `[]` |

When `assets_bucket_name` or `masters_bucket_name` is left unset, the name is
derived as `<name_prefix>-assets-<account_id>` and
`<name_prefix>-masters-<account_id>` so it stays globally unique.

## How to deploy

### In this sandbox (validation only, no AWS credentials)

This environment has no AWS credentials, so only formatting, init and validation
run here. Do not run `terraform plan` or `terraform apply` in the sandbox.
Terraform v1.9.8 is pre-installed at `/projects/sandbox/.tools/bin/terraform`.

```sh
export PATH=/projects/sandbox/.tools/bin:$PATH
cd /projects/sandbox/Cartoon/infra
terraform fmt -check -recursive
terraform init -backend=false
terraform validate
```

`terraform validate` should report `Success! The configuration is valid.`

### Against real AWS (plan and apply)

On a workstation or CI runner that has AWS credentials configured through the
standard [AWS credential chain](https://docs.aws.amazon.com/cli/latest/userguide/cli-chap-authentication.html):

```sh
cd infra
cp terraform.tfvars.example terraform.tfvars   # then edit the values
terraform init
terraform plan -out tfplan
terraform apply tfplan
```

Review the plan before applying. The configuration provisions billable
resources (S3, a Batch GPU compute environment, a CloudFront distribution, and
monitoring), so keep `monthly_budget_usd` and `budget_alert_emails` set.

## LIMITATIONS

Read this before assuming what the pipeline does.

- **It does not generate finished 3D animated video.** This infrastructure does
  not render characters, perform animation, or create neural lipsync by itself.
  No such tool is provided or exists in this environment. The repository is the
  AWS orchestration and scaffolding (storage, GPU scheduling, mastering,
  subtitles, delivery, cost control) around a render payload that you supply.

## Cartoon automation with n8n (creative layer)

This repository also contains n8n workflow templates that automate **Doraemon-style
cartoon production in complete episodes and seasons**, with consistent
characters, voices, voiceovers, and publishing:

- [`workflows/n8n-cartoon-season-producer.json`](workflows/n8n-cartoon-season-producer.json)
  reads a season/episode plan from Google Sheets and, per scene: generates a
  voiceover (ElevenLabs, locked character voice), draws the scene (NanoBanana,
  character reference + style lock), renders video (VEO3 image-to-video), and
  lipsyncs it (Sync Labs). Scenes are then stitched into a mastered episode
  (voice + music + subtitles) by the episode assembler service, sent to Telegram
  for approval, and published via Blotato to YouTube/TikTok/Instagram/Facebook.
- [`workflows/n8n-character-onboarding.json`](workflows/n8n-character-onboarding.json)
  onboards one consistent character: reference image sheet (NanoBanana) + voice
  clone (ElevenLabs), appended to the Character Bible sheet.
- [`docs/n8n-cartoon-pipeline.md`](docs/n8n-cartoon-pipeline.md) — full setup,
  consistency rules, credential list, and limitations.
- [`docs/character-bible-template.md`](docs/character-bible-template.md) — starter
  Character Bible for a Doraemon-style cast.
- [`templates/`](templates/) — Google Sheet templates (Episodes, Scenes,
  Characters) that drive the pipeline.
- [`services/episode-assembler/`](services/episode-assembler/) — FastAPI service
  that masters episode MP4s (concat, subtitles, music mix).
- [`docker-compose.yml`](docker-compose.yml) — local n8n + assembler for
  development.

See `docs/n8n-cartoon-pipeline.md` for the import checklist and provider
credentials (ElevenLabs, fal/NanoBanana, kie.ai VEO3, Sync Labs, Blotato,
Google Drive/Sheets, Telegram).
- **The creative render and lipsync job is a pluggable container.** You provide
  the actual 3D render and compositing (and any lipsync) as a container image
  through the `render_container_image` variable. The AWS Batch job definition
  passes your container the episode id, input prefix, output prefix, and frame
  range, and expects it to read assets from S3 and write rendered frames back to
  S3. The default image value is a placeholder and must be replaced before real
  jobs can run.
- **Amazon Polly voices are scratch only.** Polly Hindi neural voice is for
  scratch and animatic tracks only, never a final deliverable. Final character
  voices and songs are human actors and singers, as the outline requires.
- **Do not treat this as a one-click episode factory.** The pipeline scales and
  automates the AWS side of production. It does not produce finished episodes on
  its own, and the creative quality depends entirely on the render payload and
  the human performances you bring to it.

## Verification

```sh
export PATH=/projects/sandbox/.tools/bin:$PATH
cd /projects/sandbox/Cartoon/infra
terraform fmt -check -recursive && terraform init -backend=false && terraform validate
```
