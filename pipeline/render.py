"""Rendering: cover-crop, camera smoothstep, mouth viseme, grade, frame loop.

All drawing techniques are ported from the reference
``build_sc09_full_animatic.py`` and generalized. Frames are streamed to disk
(JPEG) one at a time so memory stays bounded for episode-length (15-30 min)
runtimes; the caller is responsible for cleaning up the scratch directory.
"""
from __future__ import annotations

import os

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

# Default speaker -> skin-mask heuristics. 'a' (Aarav) warm skin, 'g' (Golu)
# greenish. Any other speaker id falls back to an above-mouth colour sample.
_SPEAKER_MASKS = {"a", "g"}


def cover(image: Image.Image, width: int, height: int) -> Image.Image:
    """Aspect-fill center-crop ``image`` to exactly ``width`` x ``height``."""
    im = image.convert("RGB")
    w, h = im.size
    target = width / height
    if w / h > target:
        nw = int(h * target)
        im = im.crop(((w - nw) // 2, 0, (w - nw) // 2 + nw, h))
    else:
        nh = int(w / target)
        im = im.crop((0, (h - nh) // 2, w, (h - nh) // 2 + nh))
    return im.resize((width, height), Image.Resampling.LANCZOS)


def cover_path(path: str, width: int, height: int) -> Image.Image:
    return cover(Image.open(path), width, height)


def camera_at(marks, t):
    """Smoothstep-interpolate (fx, fy, zoom) across camera ``marks`` at time ``t``.

    ``marks`` is a list of (t, fx, fy, zoom) tuples sorted by time. Values are
    clamped to the first/last mark outside the authored range.
    """
    if not marks:
        return (0.5, 0.5, 1.0)
    if t <= marks[0][0]:
        return tuple(marks[0][1:])
    if t >= marks[-1][0]:
        return tuple(marks[-1][1:])
    for a, b in zip(marks, marks[1:]):
        if a[0] <= t <= b[0]:
            u = (t - a[0]) / max(0.001, b[0] - a[0])
            u = u * u * (3 - 2 * u)  # smoothstep
            return tuple(a[j] + (b[j] - a[j]) * u for j in range(1, 4))
    return tuple(marks[-1][1:])


def local_face_color(im: Image.Image, x: float, y: float, wfrac: float, speaker: str):
    """Sample a representative skin colour around the face location.

    Uses a speaker-keyed skin mask for known speakers; for unknown speakers (or
    when too few masked pixels are found) it falls back to a small sample just
    above the mouth. Never crashes on an unexpected speaker id.
    """
    W, H = im.size
    a = np.asarray(im).astype(np.int16)
    cx, cy = int(x * W), int(y * H)
    rx = max(9, int(wfrac * W * 0.9))
    ry = max(8, int(wfrac * W * 0.75))
    q = a[max(0, cy - ry): min(H, cy + ry + 1), max(0, cx - rx): min(W, cx + rx + 1)]

    pp = None
    if speaker in _SPEAKER_MASKS and q.size:
        r, g, b = q[..., 0], q[..., 1], q[..., 2]
        if speaker == "a":
            mask = (r > g + 12) & (g > b + 4) & (r > 90) & (g > 48) & (b < 220)
        else:  # 'g'
            mask = (g > r) & (g > b - 5) & (g > 80) & (r > 55) & (b > 65)
        masked = q[mask]
        if len(masked) >= 8:
            pp = masked

    if pp is None:
        # Fallback: a small patch just above the mouth.
        sy = max(0, cy - int(wfrac * W * 0.45))
        pp = a[max(0, sy - 3): min(H, sy + 4), max(0, cx - 4): min(W, cx + 5)].reshape(-1, 3)
        if len(pp) == 0:
            return (180, 150, 130)

    return tuple(int(np.clip(v, 0, 255)) for v in np.median(pp, axis=0))


def draw_mouth(frame: Image.Image, x: float, y: float, wfrac: float, openness: float, patch):
    """Patch over the still mouth then draw an openness-keyed viseme ellipse."""
    if openness < 0.20:
        return
    W, H = frame.size
    cx, cy = x * W, y * H
    bw = wfrac * W
    bh = bw * 0.72

    pw, ph = bw * 1.03, bh * 1.08
    lay = Image.new("RGBA", frame.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(lay)
    d.ellipse((cx - pw / 2, cy - ph / 2, cx + pw / 2, cy + ph / 2), fill=tuple(patch) + (255,))
    lay = lay.filter(ImageFilter.GaussianBlur(max(1.0, bw * 0.022)))
    frame.paste(Image.alpha_composite(frame.convert("RGBA"), lay).convert("RGB"), (0, 0))

    d = ImageDraw.Draw(frame)
    mw = bw * (0.78 + 0.20 * openness)
    mh = bh * (0.18 + 0.92 * openness)
    d.ellipse((cx - mw / 2, cy - mh / 2, cx + mw / 2, cy + mh / 2), fill=(48, 22, 24))
    if bw > 36 and openness > 0.57:
        d.rounded_rectangle(
            (cx - mw * 0.30, cy - mh * 0.44, cx + mw * 0.30, cy - mh * 0.44 + max(2, mh * 0.13)),
            radius=max(1, int(mh * 0.04)),
            fill=(246, 239, 220),
        )


def _vignette(width: int, height: int) -> np.ndarray:
    yy, xx = np.mgrid[0:height, 0:width]
    return (
        1 - 0.09 * (((xx - width / 2) / (width / 2)) ** 2 + ((yy - height / 2) / (height / 2)) ** 2)
    ).astype(np.float32)


class _PreparedShot:
    __slots__ = ("shot", "base", "start", "end", "camera_marks", "events")

    def __init__(self, shot, base, start):
        self.shot = shot
        self.base = base
        self.start = start
        self.end = start + shot.duration
        self.camera_marks = [c.as_tuple() for c in shot.camera]
        self.events = []  # list of dicts: t0, t1, env, patch, x, y, w


def render_frames(
    episode,
    prepared_shots,
    frames_dir: str,
    *,
    rain_threshold: float = 10.0,
    progress=None,
) -> int:
    """Render all unique frames as JPEGs into ``frames_dir``.

    ``prepared_shots`` are :class:`_PreparedShot` instances (built by the
    builder) with their events carrying ``t0/t1/env/patch``. Returns the number
    of frames written.
    """
    os.makedirs(frames_dir, exist_ok=True)
    W, H = episode.width, episode.height
    fps = episode.fps_unique
    total = episode.total_duration or episode.shots_duration
    vig = _vignette(W, H)
    ends = np.array([p.end for p in prepared_shots])

    n = int(total * fps)
    for fi in range(n):
        t = fi / fps
        si = min(int(np.searchsorted(ends, t, side="right")), len(prepared_shots) - 1)
        ps = prepared_shots[si]
        local = t - ps.start
        fx, fy, zoom = camera_at(ps.camera_marks, local)

        active = None
        for ev in ps.events:
            if ev["t0"] <= t < ev["t1"]:
                active = ev
                break

        if active:
            tx, ty = active["x"], active["y"]
            tz = max(float(zoom), 1.15)
            u = min(1, max(0, (t - active["t0"]) / 0.45))
            u = u * u * (3 - 2 * u)
            fx = fx + (tx - fx) * u
            fy = fy + (ty - fy) * u
            zoom = zoom + (tz - zoom) * u

        zoom = max(1.0, float(zoom))
        cw, ch = W / zoom, H / zoom
        ox = float(np.clip(fx * W - cw / 2, 0, W - cw))
        oy = float(np.clip(fy * H - ch / 2, 0, H - ch))
        frame = ps.base.crop((int(ox), int(oy), int(ox + cw), int(oy + ch))).resize(
            (W, H), Image.Resampling.LANCZOS
        )

        if ps.shot.rain >= rain_threshold:
            overlay = Image.new("RGBA", (W, H), (0, 0, 0, 0))
            rd = ImageDraw.Draw(overlay)
            count = 34 if ps.shot.rain >= 20 else 18
            phase = fi * 7
            alpha = int(min(255, max(0, ps.shot.rain)))
            width_px = 1 if ps.shot.rain < 20 else 2
            for j in range(count):
                rx = (j * 83 + phase + (j % 5) * 19) % W
                ry = (j * 137 + fi * 23) % H
                ln = 8 + (j % 9) * 2
                rd.line((rx, ry, rx - 5, ry + ln), fill=(190, 224, 246, alpha), width=width_px)
            frame = Image.alpha_composite(frame.convert("RGBA"), overlay).convert("RGB")

        if active:
            ei = max(0, int((t - active["t0"]) * fps))
            env = active["env"]
            op = float(env[min(ei, len(env) - 1)]) if len(env) else 0.0
            sx = (active["x"] * W - ox) * W / cw
            sy = (active["y"] * H - oy) * H / ch
            sw = active["w"] * W / cw
            draw_mouth(frame, sx / W, sy / H, sw, op, active["patch"])

        arr = np.asarray(frame).astype(np.float32)
        arr[..., 0] *= 1.012
        arr[..., 2] *= 1.008
        arr *= vig[..., None]
        Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8)).save(
            os.path.join(frames_dir, f"f{fi:05d}.jpg"), quality=87
        )
        if progress and fi % 600 == 0:
            progress(fi, n)

    return n
