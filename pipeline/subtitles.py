"""Sidecar SRT writer. The .srt carries the authoritative Unicode captions."""
from __future__ import annotations


def srt_time(sec: float) -> str:
    """Format seconds as an SRT timestamp ``HH:MM:SS,mmm``."""
    ms = int(round(sec * 1000))
    hh = ms // 3600000
    ms %= 3600000
    mm = ms // 60000
    ms %= 60000
    ss = ms // 1000
    ms %= 1000
    return f"{hh:02d}:{mm:02d}:{ss:02d},{ms:03d}"


def write_srt(path: str, timed_events) -> None:
    """Write a sidecar SRT file.

    ``timed_events`` is an iterable of ``(start_sec, end_sec, text)`` tuples in
    chronological order.
    """
    with open(path, "w", encoding="utf-8") as fh:
        for ix, (t0, t1, text) in enumerate(timed_events, 1):
            fh.write(f"{ix}\n{srt_time(t0)} --> {srt_time(t1)}\n{text}\n\n")
