"""Shared fixtures/helpers for pipeline tests (pure logic, no full render)."""
import os

import pytest
from PIL import Image

from pipeline import config as config_mod


@pytest.fixture
def assets_dir(tmp_path):
    """A tiny synthetic assets dir with one keyframe and one short audio clip."""
    img = Image.new("RGB", (64, 48), (120, 90, 70))
    img_path = tmp_path / "frame.png"
    img.save(img_path)

    # A tiny valid WAV so clip_duration can probe it without network/large media.
    import wave

    import numpy as np

    wav_path = tmp_path / "clip.wav"
    with wave.open(str(wav_path), "wb") as fh:
        fh.setnchannels(1)
        fh.setsampwidth(2)
        fh.setframerate(44100)
        samples = (np.zeros(44100, dtype=np.int16))  # 1.0s of silence
        fh.writeframes(samples.tobytes())
    return str(tmp_path)


def make_episode(**overrides):
    """Build a minimal valid EpisodeConfig referencing frame.png + clip.wav."""
    shot = config_mod.Shot(
        name="S1",
        image="frame.png",
        duration=5.0,
        rain=10.0,
        camera=[
            config_mod.CameraKeyframe(0.0, 0.5, 0.5, 1.0),
            config_mod.CameraKeyframe(5.0, 0.4, 0.6, 1.3),
        ],
        events=[
            config_mod.DialogueEvent(
                clip="clip.wav", speaker="g", x=0.4, y=0.6, w=0.03,
                start=1.0, text="वो क्या है?",
            )
        ],
    )
    episode = config_mod.EpisodeConfig(
        title="Test",
        width=64,
        height=48,
        total_duration=5.0,
        shots=[shot],
    )
    for k, v in overrides.items():
        setattr(episode, k, v)
    return episode
