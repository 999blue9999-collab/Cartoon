# Cartoon Studio — n8n Automation Pipeline

Doraemon-style cartoon automation: **complete episodes and seasons with consistent characters, voices, voiceovers, and publishing**, orchestrated in n8n.

This repository already contains the AWS production-infrastructure side of the project (`infra/`, Terraform). This document adds the **creative automation layer** in n8n that produces the actual render payload the AWS pipeline consumes.

## The two workflows

| Workflow | File | Purpose |
|---|---|---|
| **Season Producer** | `workflows/n8n-cartoon-season-producer.json` | Reads a season/episode plan from Google Sheets, produces every scene (voiceover → image → video → lipsync), assembles the episode, gates approval, publishes. |
| **Character Onboarding** | `workflows/n8n-character-onboarding.json` | Registers one consistent character once: reference image sheet (NanoBanana) + voice clone (ElevenLabs), appended to the Characters tab. |

Import either file into n8n (Workflows → Import), then configure the credentials and placeholder values marked in the Config node.

## Data model (one Google Sheet)

Three tabs drive everything. Templates are in `templates/`:

**Episodes** — one row per episode
`RowId, Season, Episode, Title, Logline, MusicUrl, TargetDuration, Status, Approval, EpisodeUrl, PublishedAt`

**Scenes** — one row per scene (one dialogue line per scene; split multi-line scenes into multiple rows)
`RowId, Season, Episode, SceneNo, Character, DialogueLine, Action, ImagePrompt, DurationSeconds, Status, AudioUrl, ImageUrl, VideoUrl, SyncedUrl`

**Characters** (Character Bible) — one row per character
`Name, Role, RefImageUrl, VoiceId, Active, Description`

Status values: empty / `PLANNED` → to do; `DONE` → scene assets ready; `PUBLISHED` / `NEEDS_REVISION` → episode level.

## How consistency is kept (Doraemon style)

1. **Character lock:** every scene looks up its character in `Config.CHARACTER_BIBLE` and reuses the same `refImageUrl` (NanoBanana image reference) and `voiceId` (ElevenLabs) across all episodes and seasons. Onboard a character once with the Character Onboarding workflow.
2. **Style lock:** `STYLE_PROMPT` and `STYLE_REF_IMAGE` are injected into every image and video prompt, so every frame shares the same 2D-anime look.
3. **Voice lock:** one ElevenLabs voice per character, same settings (`stability 0.5 / similarity 0.8`) every time.
4. **Music lock:** one `MusicUrl` per season, mixed under every episode by the assembler.

## Scene pipeline (inside Season Producer)

For each pending scene row:

1. **Build Image Prompt** — scene action + dialogue + character role + style prompt + character reference image.
2. **TTS (ElevenLabs)** — `text-to-speech/{voiceId}`, `eleven_multilingual_v2`, locked voice settings → MP3 binary.
3. **Upload Audio to Drive** — Google Drive upload → public `webContentLink`.
4. **NanoBanana (fal)** — `fal-ai/nano-banana/edit` with `image_urls = [character ref, style ref]` → wait 20s → edited image URL.
5. **VEO3 (kie.ai)** — image-to-video `api.kie.ai/api/v1/veo/generate` → poll `record-info` → scene video URL.
6. **Lipsync (Sync Labs)** — `video/sync` with video + audio URLs → synced video URL; falls back to raw video if lipsync is unavailable.
7. **Update Scene Row** — writes `Status`, `AudioUrl`, `ImageUrl`, `VideoUrl`, `SyncedUrl` back to the Scenes tab.

When the last scene of the episode is done, all scenes are re-read, and the **episode assembler service** stitches them: normalized 1080p24 concat, voiceover + season music mix, burned-in subtitles, MP4 master.

## Approval & publishing

- `AUTO_PUBLISH = true`: episode publishes immediately after assembly.
- Otherwise: a preview is sent to Telegram; the workflow waits, then checks the `Approval` column of the Episodes row. `YES` → publish; anything else → `NEEDS_REVISION`.
- Publishing: upload to **Blotato** → YouTube, TikTok, Instagram, Facebook (modeled on the reference viral-video workflow you shared). Requires Blotato Pro + the Blotato community node configured in n8n. Disable with `PUBLISH_TO_SOCIALS = false`.

## Credentials to create in n8n

| Node | Credential | What to set |
|---|---|---|
| Text-to-Speech (ElevenLabs) | Generic Header Auth | `xi-api-key: <ELEVENLABS_API_KEY>` |
| Generate Scene Image (NanoBanana) | Generic Header Auth | `Authorization: Key <FAL_KEY>` |
| Generate Scene Video (VEO3) | Generic Header Auth | `Authorization: Bearer <KIE_API_KEY>` |
| Lipsync (Sync Labs) | Generic Header Auth | `Authorization: Bearer <SYNCLABS_API_KEY>` |
| Upload Audio to Drive | Google Drive | OAuth2 (folder where scene MP3s land) |
| Update Scene Row / Episode Row | Google Sheets | OAuth2 (the production sheet) |
| Upload to Blotato / social nodes | Blotato | Blotato API key (Pro plan) |
| Send Preview to Telegram | Telegram | Bot token + chat ID |

The `Config` Set node holds the remaining plain-text values (sheet IDs, GIDs, assembler URL, style prompt, character bible JSON). Replace the empty placeholders before running.

## Episode assembler service

`services/episode-assembler/` is a small FastAPI service the workflow calls at `POST /assemble-episode`. It downloads scene videos, normalizes them to 1080p24, concatenates, mixes voiceover with season music, burns subtitles, and returns the mastered episode URL.

```bash
cd services/episode-assembler
pip install -r requirements.txt
# needs ffmpeg (Dockerfile includes it)
uvicorn app:app --host 0.0.0.0 --port 8000
```

Without ffmpeg it writes a manifest JSON instead of a video — useful for dry-run testing of the n8n wiring.

## Local run (docker-compose)

`docker-compose.yml` at the repo root runs n8n plus the assembler:

```bash
docker compose up -d
# open n8n, import the two workflow JSON files, fill in Config values and credentials
```

## Limitations

- This is an automation blueprint, not a rendered-video guarantee: image/video/lipsync quality depends on the providers (fal NanoBanana, kie.ai VEO3, ElevenLabs, Sync Labs) and their API availability/pricing.
- Sync Labs / Blotato endpoints shown are the provider shapes at time of writing; verify exact paths against current provider docs before production use.
- The workflow does not itself draw or animate characters — it orchestrates the AI generators. The AWS infrastructure in `infra/` can later host a custom render container for higher-control 3D/2D production.
- Google Sheets reads use public CSV export URLs; writes use the Google Sheets node (OAuth2). Make the sheet's CSV export accessible or switch reads to the Sheets API.

## Related

- `docs/character-bible-template.md` — example Character Bible for a Doraemon-style cast.
- `infra/` — AWS Terraform pipeline (storage, Batch GPU render, MediaConvert, Transcribe, CloudFront) that can consume/assist the assets this n8n workflow produces.
- Reference workflow shared with this project: "Generate AI viral videos with NanoBanana & VEO3, shared on socials via Blotato" (the social-publish half of this pipeline is modeled on it).
