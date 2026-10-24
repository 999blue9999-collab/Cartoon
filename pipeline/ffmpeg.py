"""ffmpeg/ffprobe discovery and low-level media helpers.

The binary is discovered dynamically so the pipeline runs anywhere:

1. ``$ANIMATIC_FFMPEG`` if set (explicit override),
2. the binary bundled with ``imageio-ffmpeg`` (no system install needed),
3. a plain ``ffmpeg`` on ``PATH`` as a last resort.

There is intentionally NO hardcoded versioned path (the reference scripts
baked in a python3.13 site-packages path; that is not portable).
"""
from __future__ import annotations

import os
import re
import shutil
import subprocess

import numpy as np

_FFMPEG_CACHE: str | None = None


def ffmpeg_exe() -> str:
    """Return the path to an ffmpeg binary, discovered dynamically.

    Raises ``RuntimeError`` if no ffmpeg can be located.
    """
    global _FFMPEG_CACHE
    if _FFMPEG_CACHE:
        return _FFMPEG_CACHE

    override = os.environ.get("ANIMATIC_FFMPEG")
    if override:
        if not os.path.isfile(override):
            raise RuntimeError(
                f"ANIMATIC_FFMPEG points at a non-existent file: {override}"
            )
        _FFMPEG_CACHE = override
        return _FFMPEG_CACHE

    try:
        import imageio_ffmpeg

        exe = imageio_ffmpeg.get_ffmpeg_exe()
        if exe and os.path.isfile(exe):
            _FFMPEG_CACHE = exe
            return _FFMPEG_CACHE
    except Exception:
        pass

    on_path = shutil.which("ffmpeg")
    if on_path:
        _FFMPEG_CACHE = on_path
        return _FFMPEG_CACHE

    raise RuntimeError(
        "No ffmpeg binary found. Set $ANIMATIC_FFMPEG, install imageio-ffmpeg, "
        "or put ffmpeg on PATH."
    )


def ffprobe_exe() -> str | None:
    """Return an ffprobe path if one sits next to ffmpeg or on PATH, else None.

    The imageio-ffmpeg bundle usually ships only ffmpeg, so callers must be
    prepared for ``None`` and fall back to parsing ``ffmpeg -i`` stderr.
    """
    exe = ffmpeg_exe()
    guess = exe.replace("ffmpeg", "ffprobe")
    if guess != exe and os.path.isfile(guess):
        return guess
    on_path = shutil.which("ffprobe")
    return on_path


def decode_mono(path: str, sr: int) -> np.ndarray:
    """Decode any audio file to mono float32 PCM in [-1, 1] at ``sr`` Hz."""
    exe = ffmpeg_exe()
    proc = subprocess.run(
        [exe, "-i", path, "-f", "s16le", "-ac", "1", "-ar", str(sr), "-"],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if not proc.stdout:
        tail = proc.stderr[-300:].decode(errors="ignore")
        raise RuntimeError(f"No audio decoded from {path}: {tail}")
    return np.frombuffer(proc.stdout, dtype=np.int16).astype(np.float32) / 32768.0


_DURATION_RE = re.compile(r"Duration:\s*(\d+):(\d+):(\d+\.\d+)")


def clip_duration(path: str) -> float:
    """Return a clip's duration in seconds.

    Uses ffprobe when available, otherwise parses the ``Duration:`` line from
    ``ffmpeg -i`` stderr (as the reference ``build_sc09.py`` does).
    """
    probe = ffprobe_exe()
    if probe:
        proc = subprocess.run(
            [
                probe,
                "-v",
                "error",
                "-show_entries",
                "format=duration",
                "-of",
                "default=noprint_wrappers=1:nokey=1",
                path,
            ],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        out = proc.stdout.strip()
        if out:
            try:
                return float(out)
            except ValueError:
                pass

    exe = ffmpeg_exe()
    proc = subprocess.run([exe, "-i", path], capture_output=True, text=True)
    match = _DURATION_RE.search(proc.stderr)
    if not match:
        raise RuntimeError(f"Could not determine duration of {path}")
    hh, mm, ss = int(match.group(1)), int(match.group(2)), float(match.group(3))
    return hh * 3600 + mm * 60 + ss
