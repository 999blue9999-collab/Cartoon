"""Full-build orchestration: config -> audio + SRT + frames -> muxed MP4 + notes."""
from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
from dataclasses import dataclass

from . import audio as audio_mod
from . import config as config_mod
from . import ffmpeg
from . import render as render_mod
from . import subtitles
from . import text as text_mod


@dataclass
class BuildResult:
    mp4_path: str
    wav_path: str
    srt_path: str
    notes_path: str
    duration: float
    n_frames: int


def _resolve(assets_dir: str, rel: str) -> str:
    return rel if os.path.isabs(rel) else os.path.join(assets_dir, rel)


def build(
    config_path: str,
    assets_dir: str,
    out_dir: str,
    *,
    font_path: str | None = None,
    burn_subtitles: bool | None = None,
    fps_unique: int | None = None,
    scratch_dir: str | None = None,
    progress=None,
) -> BuildResult:
    """Build a complete episode animatic and return the output paths.

    When burn-in is enabled (CLI ``--burn-subtitles`` or episode
    ``burn_subtitles: true``), the active dialogue event's text is burned into
    each frame using a Devanagari-capable font. The sidecar ``.srt`` is written
    regardless, and remains the authoritative caption track.
    """
    episode = config_mod.load_config(config_path)
    if fps_unique is not None:
        episode.fps_unique = fps_unique
    if font_path is not None:
        episode.font_path = font_path
    if burn_subtitles is not None:
        episode.burn_subtitles = burn_subtitles

    config_mod.validate(episode, assets_dir, probe_durations=True)

    # Resolve the caption font up front (before the slow render) so a missing
    # or non-Devanagari font fails fast with a clear, actionable error.
    caption_font = None
    if episode.burn_subtitles:
        size = max(14, int(episode.height * 0.05))
        caption_font = text_mod.resolve_devanagari_font(
            font_path, episode.font_path, size
        )

    os.makedirs(out_dir, exist_ok=True)
    sr = episode.sample_rate
    total = episode.total_duration or episode.shots_duration

    # --- decode clips, build prepared shots with per-event envelopes + patches
    prepared_shots = []
    placed_events = []  # (global_start, samples) for the mix
    timed_events = []  # (t0, t1, text) for the SRT
    cursor = 0.0
    for shot in episode.shots:
        base = render_mod.cover_path(
            _resolve(assets_dir, shot.image), episode.width, episode.height
        )
        ps = render_mod._PreparedShot(shot, base, cursor)
        for ev in shot.events:
            samples = ffmpeg.decode_mono(_resolve(assets_dir, ev.clip), sr)
            env = audio_mod.make_env(samples, sr, episode.fps_unique)
            patch = render_mod.local_face_color(base, ev.x, ev.y, ev.w, ev.speaker)
            t0 = cursor + ev.start
            t1 = t0 + len(samples) / sr
            ps.events.append(
                {"t0": t0, "t1": t1, "env": env, "patch": patch,
                 "x": ev.x, "y": ev.y, "w": ev.w, "text": ev.text}
            )
            placed_events.append((t0, samples))
            timed_events.append((t0, t1, ev.text))
        prepared_shots.append(ps)
        cursor += shot.duration

    base_name = episode.output_basename
    wav_path = os.path.join(out_dir, f"{base_name}.wav")
    srt_path = os.path.join(out_dir, f"{base_name}.srt")
    mp4_path = os.path.join(out_dir, f"{base_name}.mp4")
    notes_path = os.path.join(out_dir, f"{base_name}-notes.md")

    # --- audio mix + WAV
    mix = audio_mod.build_mix(episode, placed_events, sr)
    audio_mod.write_wav(wav_path, mix, sr)

    # --- SRT sidecar (authoritative captions)
    timed_events.sort(key=lambda e: e[0])
    subtitles.write_srt(srt_path, timed_events)

    # --- frames (streamed to a temp scratch dir, cleaned up after mux)
    frames_root = scratch_dir or tempfile.mkdtemp(prefix="animatic_frames_")
    frames_dir = os.path.join(frames_root, "frames")
    n_frames = 0
    try:
        n_frames = render_mod.render_frames(
            episode, prepared_shots, frames_dir,
            caption_font=caption_font, progress=progress
        )
        _mux(episode, frames_dir, wav_path, mp4_path, total)
    finally:
        shutil.rmtree(frames_root, ignore_errors=True)

    size_mb = os.path.getsize(mp4_path) / 1e6 if os.path.exists(mp4_path) else 0.0
    _write_notes(notes_path, episode, total, n_frames, size_mb)

    return BuildResult(mp4_path, wav_path, srt_path, notes_path, total, n_frames)


def _mux(episode, frames_dir, wav_path, mp4_path, total):
    exe = ffmpeg.ffmpeg_exe()
    if os.path.exists(mp4_path):
        os.remove(mp4_path)
    cmd = [
        exe, "-y",
        "-framerate", str(episode.fps_unique),
        "-i", os.path.join(frames_dir, "f%05d.jpg"),
        "-i", wav_path,
        "-r", str(episode.fps_delivery),
    ]
    if episode.total_duration is not None:
        cmd += ["-t", str(total)]
    cmd += [
        "-c:v", "libx264", "-preset", "fast", "-crf", "24",
        "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "160k",
        "-shortest", "-movflags", "+faststart",
        mp4_path,
    ]
    proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if proc.returncode != 0:
        raise RuntimeError(
            "ffmpeg mux failed:\n" + proc.stderr[-800:].decode(errors="ignore")
        )


def _write_notes(path, episode, total, n_frames, size_mb):
    mm = int(total // 60)
    ss = total - mm * 60
    lines = [
        f"# {episode.title} - Animatic - DRAFT",
        "",
        "## Scope",
        f"A config-driven animatic covering {len(episode.shots)} shot(s), "
        f"generated from an external episode config (no hardcoded shot list).",
        "",
        "## Technical details",
        f"- {episode.width}x{episode.height} H.264 MP4, {episode.fps_delivery}fps "
        f"delivery, {mm:02d}:{ss:05.2f} ({episode.fps_unique} unique drawings/second "
        f"held on twos), {size_mb:.1f} MB, {n_frames} unique drawings",
        "- Reference-guided keyframes held per shot with camera push-ins/pans",
        "- Hindi scratch TTS dialogue; soft synthetic rain/room tone; separate "
        "Devanagari SRT sidecar",
        "- Speech amplitude drives rough mouth-open/close poses; vignette + "
        "warm/teal grade",
        "",
        "## Limitation - not final animation",
        "This is an **animatic/previs**, not a finished animated scene. It does "
        "not contain generated full-body motion or phoneme-level facial "
        "animation; mouth timing is amplitude-driven and needs artist review. No "
        "external neural video/lip-sync model was used. The scratch TTS is not "
        "the final cast dub. The sidecar .srt carries the authoritative captions.",
        "",
        "## Files in this delivery",
        f"- `{episode.output_basename}.mp4` - the animatic preview with audio",
        f"- `{episode.output_basename}.srt` - timed Devanagari captions",
        f"- `{episode.output_basename}.wav` - dialogue and ambience mix",
        "",
    ]
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines))
