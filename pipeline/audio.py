"""Audio: RMS envelope (mouth openness), dialogue+ambience mix, WAV writer.

Ports the reference ``make_env``/mix/``write_wav`` techniques cleanly:

* ``make_env`` turns a clip into one openness value per *unique* frame using a
  percentile-normalized RMS envelope with ``**`` easing and attack/release
  smoothing.
* ``build_mix`` lays each decoded clip at its global start into a zeroed
  track and adds a gentle filtered-noise rain/room bed scaled per shot, with
  head/tail fades, clipped to +/-0.98.
"""
from __future__ import annotations

import math
import wave

import numpy as np


def make_env(samples: np.ndarray, sr: int, fps_unique: int) -> np.ndarray:
    """Return a per-unique-frame mouth-openness envelope in [0, 1].

    Length is ``round(len(samples) / sr * fps_unique)`` (at least 1).
    """
    n = max(1, int(round(len(samples) / sr * fps_unique)))
    out = np.zeros(n, dtype=np.float32)
    if len(samples) == 0:
        return out
    per = max(1, sr // fps_unique)
    peak = max(float(np.percentile(np.abs(samples), 95)), 0.014)
    for i in range(n):
        lo = i * per
        hi = min((i + 1) * per, len(samples))
        if hi > lo:
            out[i] = math.sqrt(float(np.mean(samples[lo:hi] ** 2))) / peak
    out = np.clip(out * 1.7, 0, 1) ** 0.72
    # Attack / release smoothing: decay forward then backward.
    for i in range(1, n):
        out[i] = max(out[i], 0.48 * out[i - 1])
    for i in range(n - 2, -1, -1):
        out[i] = max(out[i], 0.32 * out[i + 1])
    return out


def _ambience_bed(length: int, seed: int) -> np.ndarray:
    """A gentle filtered-noise bed (white blended with a low-passed copy)."""
    rng = np.random.default_rng(seed)
    white = rng.normal(0, 1, length).astype(np.float32)
    low = np.empty_like(white)
    if length:
        low[0] = white[0]
        for i in range(1, length):
            low[i] = 0.994 * low[i - 1] + 0.006 * white[i]
    return 0.45 * white + 0.55 * low


def build_mix(
    episode,
    placed_events,
    sr: int,
) -> np.ndarray:
    """Build the full mono mix track.

    ``placed_events`` is an iterable of ``(global_start_sec, samples)`` for each
    dialogue clip. The track length is derived from the episode's total duration
    (or the sum of shot durations when no explicit total is set).
    """
    total = episode.total_duration or episode.shots_duration
    length = int(total * sr)
    audio = np.zeros(length, dtype=np.float32)

    for start, samples in placed_events:
        i0 = int(start * sr)
        i1 = min(length, i0 + len(samples))
        if i1 > i0:
            audio[i0:i1] += samples[: i1 - i0]

    amb = _ambience_bed(length, episode.ambience_seed)

    # Per-shot rain/room level along the timeline.
    levels = np.zeros(length, dtype=np.float32)
    cursor = 0.0
    for shot in episode.shots:
        lo = int(cursor * sr)
        hi = min(length, int((cursor + shot.duration) * sr))
        levels[lo:hi] = shot.rain / episode.rain_divisor
        cursor += shot.duration

    # Gentle broad fades at the very head and tail.
    fade = min(int(1.2 * sr), length // 2)
    if fade > 0:
        levels[:fade] *= np.linspace(0, 1, fade)
        levels[-fade:] *= np.linspace(1, 0, fade)

    return np.clip(audio + amb * levels, -0.98, 0.98)


def write_wav(path: str, samples: np.ndarray, sr: int) -> None:
    """Write a mono 16-bit PCM WAV file."""
    with wave.open(path, "wb") as fh:
        fh.setnchannels(1)
        fh.setsampwidth(2)
        fh.setframerate(sr)
        fh.writeframes((np.clip(samples, -1, 1) * 32767).astype(np.int16).tobytes())
