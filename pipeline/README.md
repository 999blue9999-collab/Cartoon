# Episode-animatic pipeline

A reusable, **config-driven episode-animatic renderer**. You describe a scene
as an external config file (an ordered list of shots, each with a keyframe
image, duration, camera moves, rain/ambience, and Hindi dialogue events), and
the tool renders an episode-length animatic: an MP4 with amplitude-driven mouth
poses, a synchronized WAV mix, a Devanagari `.srt` sidecar, and a notes
markdown.

It is the generalized, automatable form of the one-off
`golu_project/build_sc09_full_animatic.py` scene-builder script: the hardcoded
`SHOTS` list and the render techniques (cover-crop, smoothstep camera, RMS mouth
envelope, rain overlay, vignette/grade, on-twos frame muxing) were lifted into a
small library plus a CLI so future scenes are authored as config, not code.

> **Read the [LIMITATIONS](#limitations) section before assuming what this
> does.** This is an **animatic / previs** tool: mouth motion is an amplitude
> approximation, not neural lip-sync, and the Hindi audio is scratch TTS, not a
> final dub. It is a planning preview, not finished animation.

## What it produces

For each build the tool writes four files into the output directory, named from
the config's `output_basename`:

| File | What it is |
|---|---|
| `<basename>.mp4` | The animatic preview (H.264 + AAC, `faststart`) |
| `<basename>.wav` | The dialogue + synthetic rain/room-tone mix |
| `<basename>.srt` | **Authoritative** timed Devanagari captions (correct Unicode) |
| `<basename>-notes.md` | Scope + technical details + honest limitation note |

## Install

The pipeline is a Python 3.11+ CLI with no build step. Create a virtualenv in
the workspace (the sandbox `/root/.local` is read-only and `/tmp` is not
persistent, so the venv must live under the workspace) and install the runtime
dependencies plus pytest:

```sh
python3 -m venv /projects/sandbox/.venv-animatic
/projects/sandbox/.venv-animatic/bin/pip install -r pipeline/requirements.txt pytest
```

`ffmpeg`/`ffprobe` are **not** required on the system `PATH`: they come bundled
with [`imageio-ffmpeg`](https://pypi.org/project/imageio-ffmpeg/) and are
resolved at runtime (override with the `ANIMATIC_FFMPEG` env var if you want a
specific binary).

Run the tests (fast, offline, no full render):

```sh
cd /projects/sandbox/Cartoon
/projects/sandbox/.venv-animatic/bin/python -m pytest pipeline/tests -q
```

## Running the CLI

Everything is driven through `python -m pipeline build` (run with the repo root
as the working directory so `import pipeline` resolves):

```sh
cd /projects/sandbox/Cartoon

# Validate the config and print a summary without rendering:
python -m pipeline build \
  --config pipeline/examples/sc09-five-minute.yaml \
  --assets-dir /projects/sandbox/golu_project \
  --validate-only

# Fast end-to-end smoke render (seconds):
python -m pipeline build \
  --config pipeline/examples/smoke-test.yaml \
  --assets-dir /projects/sandbox/golu_project \
  --out-dir /projects/sandbox/.animatic-out

# Same, but burn the Devanagari dialogue into the frames (needs a font):
python -m pipeline build \
  --config pipeline/examples/smoke-test.yaml \
  --assets-dir /projects/sandbox/golu_project \
  --out-dir /projects/sandbox/.animatic-out \
  --burn-subtitles \
  --font /projects/sandbox/fonts/NotoSansDevanagari-Regular.ttf
```

| Flag | Purpose |
|---|---|
| `--config` | Path to the YAML/JSON episode config (required). |
| `--assets-dir` | Directory that keyframe/audio paths resolve against (required). |
| `--out-dir` | Output directory for the mp4/wav/srt/notes (required unless `--validate-only`). |
| `--validate-only` | Validate the config and print a one-line summary, then exit. |
| `--font` | Path to a Devanagari-capable TrueType font for burn-in. Overrides the episode `font_path`. |
| `--burn-subtitles` / `--no-burn-subtitles` | Enable/disable burned-in captions (overrides the config `burn_subtitles`). |
| `--fps-unique` | Override unique drawings per second (default 12). |

On a validation error the CLI prints a clear message and exits non-zero.

## Config schema

A config is a YAML (or JSON, chosen by file extension) mapping with episode-level
settings and an ordered `shots` list. Asset paths are **relative** and resolved
against `--assets-dir`. See `pipeline/examples/sc09-five-minute.yaml` (the full
8-shot / 300.0s SC09 episode) and `pipeline/examples/smoke-test.yaml` (a 3-shot /
10.0s fast render) for complete, working examples.

### Annotated example

```yaml
title: "SC09 - Full Five-Minute"   # required
width: 1280                         # frame width  (default 1280)
height: 720                         # frame height (default 720)
fps_unique: 12                      # unique drawings per second (on twos)
fps_delivery: 24                    # delivery frame rate of the MP4
sample_rate: 44100                  # audio mix sample rate
total_duration: 300.0               # optional; if set, must equal sum(shot.duration)
output_basename: "SC09-animatic"    # base name for the 4 output files

# Burned-in Devanagari subtitles (optional). The .srt sidecar is always
# written and is authoritative; burn-in is a best-effort visual aid.
burn_subtitles: false               # or pass --burn-subtitles on the CLI
font_path: null                     # path to a Devanagari font; or pass --font

shots:
  - name: "Lane - first look"       # label used in error messages
    image: keyframes/sc09-01.png    # relative to --assets-dir
    duration: 35.0                   # seconds this shot is held
    rain: 25                         # 0 = none; >=10 draws a rain overlay
    camera:                          # optional smoothstep camera keyframes
      # [t_seconds, framing_x (0..1), framing_y (0..1), zoom (>=1.0)]
      - [0,  0.50, 0.50, 1.00]       # times are local to the shot, sorted,
      - [10, 0.41, 0.63, 1.95]       # and must lie within [0, duration]
      - [35, 0.50, 0.50, 1.03]
    events:                          # ordered dialogue events
      - clip: audio/sc09-01.mp3      # audio file, relative to --assets-dir
        who: g                       # speaker id ('who' or 'speaker'); 'a'/'g'
                                     #   have skin masks, others fall back safely
        start: 10.0                  # start offset within the shot (seconds)
        x: 0.405                     # face centre x (0..1 of frame width)
        y: 0.625                     # face centre y (0..1 of frame height)
        w: 0.018                     # face/mouth width as a fraction of width
        text: "वो क्या है?"            # Devanagari caption (SRT + optional burn-in)
```

### Validation rules

`--validate-only` (and every build) enforces, with clear, named errors:

- every keyframe image and audio clip exists under `--assets-dir`;
- each event's `[start, start + clip_duration]` fits inside its shot's
  `duration` (clip length is probed with the bundled ffmpeg);
- camera keyframe times are within `[0, duration]` and sorted;
- `x`, `y` are in `[0, 1]` and `w > 0`;
- if `total_duration` is set, the shot durations sum to it (within `1e-6`).

## The on-twos rendering model

The renderer draws **12 unique frames per second** (`fps_unique`) and the MP4 is
muxed out at **24 fps** (`fps_delivery`), so each drawing is held across two
delivery frames. This "on twos" cadence matches traditional limited animation:
it halves the number of drawings while keeping smooth 24 fps playback, and it is
how the reference SC09 script worked. Per unique frame the tool picks the active
shot, interpolates the authored camera with a smoothstep (plus a gentle
push-in toward the talking character), applies the rain overlay and vignette /
warm-teal grade, draws an amplitude-keyed mouth viseme, and (when enabled) burns
the caption.

## Episode length, memory, and performance

Frames are **streamed to disk** one JPEG at a time into a temporary scratch
directory, then muxed by ffmpeg and the scratch directory is removed. Nothing
holds the whole frame sequence in memory, so memory stays bounded regardless of
episode length: a 5-minute episode at 12 fps is 3,600 drawings, a 30-minute one
is 21,600, and peak memory is roughly one frame either way. A full 5-minute
render is CPU-bound and slow; use `--validate-only` for a quick structural check
and `smoke-test.yaml` for a fast end-to-end render.

## Devanagari font dependency (for burned-in subtitles)

The sidecar `.srt` is always written and carries correct Unicode, so you only
need a font when you ask to **burn** captions into the frames. No Devanagari
font ships with the tool or is assumed present on the system. When
`--burn-subtitles` is requested:

- if no font is supplied (via `--font` or episode `font_path`, or the
  `ANIMATIC_FONT` env var), **or** the supplied font cannot render Devanagari
  (checked against U+0915 KA), the build fails fast with a clear, actionable
  error naming `--font` and the download URL. It never silently renders tofu
  boxes and never falls back to the PIL default bitmap font.

Download Noto Sans Devanagari to a location outside the repo (it must not be
committed; `*.ttf` and `fonts/` are gitignored):

```sh
mkdir -p /projects/sandbox/fonts
curl -sL -o /projects/sandbox/fonts/NotoSansDevanagari-Regular.ttf \
  https://github.com/notofonts/notofonts.github.io/raw/main/fonts/NotoSansDevanagari/hinted/ttf/NotoSansDevanagari-Regular.ttf
```

## Assets and what is not committed

The example configs reference the read-only GOLU reference assets at
`/projects/sandbox/golu_project` (keyframes and Hindi scratch-TTS clips); those
assets are consumed via `--assets-dir` and are **never** copied into this repo.
All generated media (`*.mp4`, `*.wav`, intermediate frame directories), the
virtualenv, and downloaded fonts (`*.ttf`) are intentionally **not committed**
and are gitignored. Render to a path outside the repo (e.g.
`/projects/sandbox/.animatic-out`). Only the Python code, config files, tests,
`requirements.txt`, and this README are tracked.

## LIMITATIONS

This is an **animatic / previs** tool, consistent with the honest framing in the
repo's top-level README and the GOLU output notes. It is a planning preview, not
finished animation:

- **Amplitude-driven mouth, not lip-sync.** Mouth open/close is derived from the
  RMS envelope of the scratch audio and drawn as a simple viseme ellipse over the
  keyframe. It is **not** phoneme-level lip-sync, and it is **not** full-body or
  facial animation. No neural video / lip-sync model is used; poses need artist
  review.
- **Scratch TTS, not the final dub.** The Hindi dialogue clips are scratch
  text-to-speech for timing and previs only, never a final cast performance.
- **PIL complex-script shaping caveat.** Burned-in Devanagari is rendered with
  Pillow's `ImageDraw`, which does **not** perform complex-script (HarfBuzz)
  shaping. Conjuncts and matra (vowel-sign) reordering may be imperfect in the
  burned-in frames. The sidecar **`.srt` carries the correct Unicode text and is
  the authoritative caption track**; treat the burn-in as a convenience preview.
