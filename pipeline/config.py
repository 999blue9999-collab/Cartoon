"""Episode config schema, loader and validation.

An episode is described entirely by an external YAML (or JSON) file: there
is no hardcoded SHOTS list in code. The schema mirrors the reference
``build_sc09_full_animatic.py`` data, generalized into dataclasses:

    EpisodeConfig
      -> shots: list[Shot]
           -> camera: list[CameraKeyframe]
           -> events: list[DialogueEvent]

Image and audio-clip paths are resolved relative to a configurable
``assets_dir`` at validation time.
"""
from __future__ import annotations

import json
import os
from dataclasses import dataclass, field

from . import ffmpeg


class ConfigError(ValueError):
    """Raised for any invalid or inconsistent episode config."""


@dataclass
class CameraKeyframe:
    """A camera mark: time (s, local to the shot), framing centre and zoom."""

    t: float
    fx: float
    fy: float
    zoom: float

    def as_tuple(self) -> tuple[float, float, float, float]:
        return (self.t, self.fx, self.fy, self.zoom)


@dataclass
class DialogueEvent:
    """A spoken line: audio clip, speaker id, face location, start and text."""

    clip: str
    speaker: str
    x: float
    y: float
    w: float
    start: float
    text: str


@dataclass
class Shot:
    """A single storyboard shot: a held keyframe with camera moves + dialogue."""

    name: str
    image: str
    duration: float
    rain: float = 0.0
    camera: list[CameraKeyframe] = field(default_factory=list)
    events: list[DialogueEvent] = field(default_factory=list)


@dataclass
class EpisodeConfig:
    """Top-level episode settings plus an ordered list of shots."""

    title: str
    width: int = 1280
    height: int = 720
    fps_unique: int = 12
    fps_delivery: int = 24
    sample_rate: int = 44100
    total_duration: float | None = None
    font_path: str | None = None
    burn_subtitles: bool = False
    ambience_seed: int = 29
    rain_divisor: float = 1000.0
    output_basename: str = "episode-animatic"
    shots: list[Shot] = field(default_factory=list)

    @property
    def shots_duration(self) -> float:
        return sum(s.duration for s in self.shots)


# ---------------------------------------------------------------------------
# Loading
# ---------------------------------------------------------------------------

def _load_raw(path: str) -> dict:
    ext = os.path.splitext(path)[1].lower()
    with open(path, "r", encoding="utf-8") as fh:
        text = fh.read()
    if ext in (".yaml", ".yml"):
        try:
            import yaml
        except ImportError as exc:  # pragma: no cover - exercised via requirements
            raise ConfigError(
                "PyYAML is required to load YAML configs; install it or use JSON."
            ) from exc
        data = yaml.safe_load(text)
    elif ext == ".json":
        data = json.loads(text)
    else:
        raise ConfigError(
            f"Unsupported config extension {ext!r}; use .yaml, .yml or .json."
        )
    if not isinstance(data, dict):
        raise ConfigError("Config root must be a mapping/object.")
    return data


def _event_from(raw: dict, shot_name: str) -> DialogueEvent:
    # Accept either 'speaker' or the reference's 'who' key.
    speaker = raw.get("speaker", raw.get("who"))
    missing = [
        k for k in ("clip", "x", "y", "w", "start", "text")
        if k not in raw
    ]
    if speaker is None:
        missing.append("speaker/who")
    if missing:
        raise ConfigError(
            f"Shot {shot_name!r}: dialogue event missing keys: {', '.join(missing)}"
        )
    return DialogueEvent(
        clip=str(raw["clip"]),
        speaker=str(speaker),
        x=float(raw["x"]),
        y=float(raw["y"]),
        w=float(raw["w"]),
        start=float(raw["start"]),
        text=str(raw["text"]),
    )


def _camera_from(raw, shot_name: str) -> CameraKeyframe:
    # Accept a 4-tuple/list (t, fx, fy, zoom) or a mapping.
    if isinstance(raw, (list, tuple)):
        if len(raw) != 4:
            raise ConfigError(
                f"Shot {shot_name!r}: camera keyframe must have 4 values "
                f"(t, fx, fy, zoom), got {len(raw)}."
            )
        t, fx, fy, zoom = raw
    elif isinstance(raw, dict):
        try:
            t, fx, fy, zoom = raw["t"], raw["fx"], raw["fy"], raw["zoom"]
        except KeyError as exc:
            raise ConfigError(
                f"Shot {shot_name!r}: camera keyframe missing key {exc}."
            ) from exc
    else:
        raise ConfigError(f"Shot {shot_name!r}: invalid camera keyframe {raw!r}.")
    return CameraKeyframe(float(t), float(fx), float(fy), float(zoom))


def _shot_from(raw: dict) -> Shot:
    name = str(raw.get("name", raw.get("image", "<unnamed>")))
    for key in ("image", "duration"):
        if key not in raw:
            raise ConfigError(f"Shot {name!r}: missing required key {key!r}.")
    camera = [_camera_from(c, name) for c in (raw.get("camera") or [])]
    events = [_event_from(e, name) for e in (raw.get("events") or [])]
    return Shot(
        name=name,
        image=str(raw["image"]),
        duration=float(raw["duration"]),
        rain=float(raw.get("rain", 0.0)),
        camera=camera,
        events=events,
    )


def load_config(path: str) -> EpisodeConfig:
    """Load an :class:`EpisodeConfig` from a YAML or JSON file."""
    data = _load_raw(path)
    if "title" not in data:
        raise ConfigError("Episode config must set a 'title'.")
    shots_raw = data.get("shots")
    if not shots_raw:
        raise ConfigError("Episode config must list at least one shot.")

    episode = EpisodeConfig(
        title=str(data["title"]),
        width=int(data.get("width", 1280)),
        height=int(data.get("height", 720)),
        fps_unique=int(data.get("fps_unique", 12)),
        fps_delivery=int(data.get("fps_delivery", 24)),
        sample_rate=int(data.get("sample_rate", 44100)),
        total_duration=(
            float(data["total_duration"]) if data.get("total_duration") is not None else None
        ),
        font_path=data.get("font_path"),
        burn_subtitles=bool(data.get("burn_subtitles", False)),
        ambience_seed=int(data.get("ambience_seed", 29)),
        rain_divisor=float(data.get("rain_divisor", 1000.0)),
        output_basename=str(data.get("output_basename", "episode-animatic")),
        shots=[_shot_from(s) for s in shots_raw],
    )
    return episode


# ---------------------------------------------------------------------------
# Validation
# ---------------------------------------------------------------------------

def _resolve(assets_dir: str, rel: str) -> str:
    return rel if os.path.isabs(rel) else os.path.join(assets_dir, rel)


def validate(
    episode: EpisodeConfig,
    assets_dir: str,
    *,
    probe_durations: bool = True,
) -> None:
    """Validate an episode config, raising :class:`ConfigError` on any problem.

    When ``probe_durations`` is True (the default), every dialogue clip's
    duration is probed via the bundled ffmpeg so that
    ``[start, start + clip_duration]`` can be checked against its shot. Tests
    may pass ``probe_durations=False`` to skip the ffmpeg probe.
    """
    if episode.width <= 0 or episode.height <= 0:
        raise ConfigError("Episode width and height must be positive.")
    if episode.fps_unique <= 0 or episode.fps_delivery <= 0:
        raise ConfigError("fps_unique and fps_delivery must be positive.")

    for shot in episode.shots:
        if shot.duration <= 0:
            raise ConfigError(f"Shot {shot.name!r}: duration must be positive.")

        img_path = _resolve(assets_dir, shot.image)
        if not os.path.isfile(img_path):
            raise ConfigError(
                f"Shot {shot.name!r}: keyframe image not found: {img_path}"
            )

        # Camera keyframes must be in [0, duration] and sorted by time.
        last_t = None
        for cam in shot.camera:
            if not (0.0 <= cam.t <= shot.duration + 1e-6):
                raise ConfigError(
                    f"Shot {shot.name!r}: camera time {cam.t} outside "
                    f"[0, {shot.duration}]."
                )
            if last_t is not None and cam.t < last_t - 1e-9:
                raise ConfigError(
                    f"Shot {shot.name!r}: camera keyframes must be sorted by time."
                )
            last_t = cam.t

        for ev in shot.events:
            if not (0.0 <= ev.x <= 1.0):
                raise ConfigError(
                    f"Shot {shot.name!r}: event x={ev.x} out of range [0, 1]."
                )
            if not (0.0 <= ev.y <= 1.0):
                raise ConfigError(
                    f"Shot {shot.name!r}: event y={ev.y} out of range [0, 1]."
                )
            if ev.w <= 0.0:
                raise ConfigError(
                    f"Shot {shot.name!r}: event w={ev.w} must be > 0."
                )
            if ev.start < 0.0:
                raise ConfigError(
                    f"Shot {shot.name!r}: event start={ev.start} must be >= 0."
                )

            clip_path = _resolve(assets_dir, ev.clip)
            if not os.path.isfile(clip_path):
                raise ConfigError(
                    f"Shot {shot.name!r}: audio clip not found: {clip_path}"
                )

            if probe_durations:
                dur = ffmpeg.clip_duration(clip_path)
                end = ev.start + dur
                if end > shot.duration + 1e-6:
                    raise ConfigError(
                        f"Shot {shot.name!r}: dialogue exceeds shot "
                        f"(event '{ev.text[:24]}...' ends at {end:.3f}s > "
                        f"shot duration {shot.duration}s)."
                    )

    if episode.total_duration is not None:
        if abs(episode.shots_duration - episode.total_duration) > 1e-6:
            raise ConfigError(
                f"Shot durations sum to {episode.shots_duration} but "
                f"total_duration is {episode.total_duration}."
            )
