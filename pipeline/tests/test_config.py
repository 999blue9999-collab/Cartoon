"""Config loading + validation tests."""
import pytest

from pipeline import config as config_mod
from pipeline.config import ConfigError

from .conftest import make_episode


def test_valid_config_validates(assets_dir):
    episode = make_episode()
    # clip is 1.0s starting at 1.0 within a 5.0s shot -> fits.
    config_mod.validate(episode, assets_dir)


def test_missing_keyframe_raises(assets_dir):
    episode = make_episode()
    episode.shots[0].image = "does-not-exist.png"
    with pytest.raises(ConfigError, match="keyframe image not found"):
        config_mod.validate(episode, assets_dir)


def test_missing_audio_clip_raises(assets_dir):
    episode = make_episode()
    episode.shots[0].events[0].clip = "nope.wav"
    with pytest.raises(ConfigError, match="audio clip not found"):
        config_mod.validate(episode, assets_dir)


def test_dialogue_overruns_shot_raises(assets_dir):
    episode = make_episode()
    # 1.0s clip starting at 4.5 in a 5.0s shot -> ends at 5.5 > 5.0.
    episode.shots[0].events[0].start = 4.5
    with pytest.raises(ConfigError, match="dialogue exceeds shot"):
        config_mod.validate(episode, assets_dir)


def test_durations_not_summing_raises(assets_dir):
    episode = make_episode(total_duration=10.0)  # shot sums to 5.0
    with pytest.raises(ConfigError, match="total_duration"):
        config_mod.validate(episode, assets_dir, probe_durations=False)


def test_out_of_range_x_raises(assets_dir):
    episode = make_episode()
    episode.shots[0].events[0].x = 1.4
    with pytest.raises(ConfigError, match="x=.* out of range"):
        config_mod.validate(episode, assets_dir, probe_durations=False)


def test_out_of_range_y_raises(assets_dir):
    episode = make_episode()
    episode.shots[0].events[0].y = -0.1
    with pytest.raises(ConfigError, match="y=.* out of range"):
        config_mod.validate(episode, assets_dir, probe_durations=False)


def test_non_positive_w_raises(assets_dir):
    episode = make_episode()
    episode.shots[0].events[0].w = 0.0
    with pytest.raises(ConfigError, match="w=.* must be > 0"):
        config_mod.validate(episode, assets_dir, probe_durations=False)


def test_camera_out_of_range_raises(assets_dir):
    episode = make_episode()
    episode.shots[0].camera[1].t = 9.0  # shot is 5.0s
    with pytest.raises(ConfigError, match="camera time"):
        config_mod.validate(episode, assets_dir, probe_durations=False)


def test_camera_unsorted_raises(assets_dir):
    episode = make_episode()
    episode.shots[0].camera[0].t = 4.0
    episode.shots[0].camera[1].t = 1.0
    with pytest.raises(ConfigError, match="sorted by time"):
        config_mod.validate(episode, assets_dir, probe_durations=False)


def test_load_yaml_roundtrip(tmp_path):
    cfg = tmp_path / "ep.yaml"
    cfg.write_text(
        "title: X\n"
        "total_duration: 3.0\n"
        "shots:\n"
        "  - name: A\n"
        "    image: a.png\n"
        "    duration: 3.0\n"
        "    rain: 5\n"
        "    camera:\n"
        "      - [0, 0.5, 0.5, 1.0]\n"
        "    events:\n"
        "      - clip: c.mp3\n"
        "        who: a\n"
        "        x: 0.5\n"
        "        y: 0.5\n"
        "        w: 0.03\n"
        "        start: 0.0\n"
        "        text: hi\n",
        encoding="utf-8",
    )
    ep = config_mod.load_config(str(cfg))
    assert ep.title == "X"
    assert ep.fps_unique == 12  # default
    assert len(ep.shots) == 1
    ev = ep.shots[0].events[0]
    assert ev.speaker == "a"  # 'who' accepted as alias
    assert ep.shots[0].camera[0].as_tuple() == (0.0, 0.5, 0.5, 1.0)


def test_load_json_roundtrip(tmp_path):
    cfg = tmp_path / "ep.json"
    cfg.write_text(
        '{"title":"J","shots":[{"name":"A","image":"a.png","duration":2.0}]}',
        encoding="utf-8",
    )
    ep = config_mod.load_config(str(cfg))
    assert ep.title == "J"
    assert ep.shots[0].duration == 2.0


def test_unsupported_extension_raises(tmp_path):
    cfg = tmp_path / "ep.txt"
    cfg.write_text("title: X", encoding="utf-8")
    with pytest.raises(ConfigError, match="Unsupported config extension"):
        config_mod.load_config(str(cfg))
