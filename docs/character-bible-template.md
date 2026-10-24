# Character Bible Template — Doraemon-Style Cast

Use this to fill `Config.CHARACTER_BIBLE` in the Season Producer workflow, or to register characters with the **Character Onboarding** workflow. The bible is the single source of truth for consistency: every scene reuses the same `refImageUrl` and `voiceId` per character, across every episode and season.

## Example cast (generic archetypes — replace with your own)

| Name | Role | Voice settings | Visual notes (for ref image prompt) |
|---|---|---|---|
| the boy | protagonist | ElevenLabs, warm, curious, stability 0.5 | Schoolboy, shorts, t-shirt, big hopeful eyes |
| robot cat | mentor | ElevenLabs, calm, wise, stability 0.4 | Blue-white robot cat, red nose, collar with bell |
| the girl | friend | ElevenLabs, kind, bright | Pink dress, smart, practical |
| bully | antagonist | ElevenLabs, loud, brash | Taller, green shirt, confident grin |
| genius friend | friend | ElevenLabs, precise | Glasses, yellow shirt, always with a gadget |

## Config JSON example

Paste into the **Config** Set node as `CHARACTER_BIBLE`:

```json
{
  "the boy": {
    "role": "protagonist",
    "voiceId": "ELEVENLABS_VOICE_ID_1",
    "refImageUrl": "https://drive.google.com/.../character-boy-ref.png",
    "colorScheme": "blue shirt, skin tone"
  },
  "robot cat": {
    "role": "mentor",
    "voiceId": "ELEVENLABS_VOICE_ID_2",
    "refImageUrl": "https://drive.google.com/.../character-robotcat-ref.png",
    "colorScheme": "blue-white body, red nose"
  }
}
```

## Onboarding a new character (Character Onboarding workflow)

1. Run the workflow manually with: name, role, description, voice-sample URL.
2. NanoBanana generates a turnaround reference sheet (front/side/back), style-locked to `STYLE_PROMPT`.
3. ElevenLabs clones the voice from the sample → returns `voice_id`.
4. A row is appended to the `Characters` tab; Telegram confirms.

**Next:** copy the new `refImageUrl` and `voiceId` into `Config.CHARACTER_BIBLE` of the Season Producer. From then on, every scene featuring that character keeps the same look and voice.

## Style lock (season level)

Set once in `Config`:

- `STYLE_PROMPT`: e.g. `Doraemon-style 2D anime, bright flat colors, thick clean black outlines, simple geometric shapes, kawaii, sunny suburban Japan, 16:9`
- `STYLE_REF_IMAGE`: public URL of one style-reference image injected into every generation.

## Voice notes

- Use `eleven_multilingual_v2` for Hindi/English mixed dialogue (the series is Hindi-targeted).
- Keep the same `stability` / `similarity_boost` per character across seasons so voices don't drift.
- Record final character voices with human actors for broadcast deliverables; cloned voices are fine for animatics and social-first episodes.
