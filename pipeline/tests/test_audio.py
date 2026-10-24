"""make_env envelope math tests (no ffmpeg / media needed)."""
import numpy as np

from pipeline.audio import make_env


SR = 44100
FPS = 12


def test_env_shape_matches_frame_count():
    n_samples = SR * 2  # 2.0s
    env = make_env(np.zeros(n_samples, dtype=np.float32), SR, FPS)
    assert len(env) == round(n_samples / SR * FPS) == 24


def test_silence_is_near_zero():
    env = make_env(np.zeros(SR, dtype=np.float32), SR, FPS)
    assert float(env.max()) < 1e-6


def test_loud_is_higher_than_quiet():
    loud = (np.ones(SR, dtype=np.float32) * 0.9)
    quiet = (np.ones(SR, dtype=np.float32) * 0.02)
    # Percentile normalization means a constant tone saturates; give loud a
    # transient so RMS clearly exceeds a near-silent clip's.
    rng = np.random.default_rng(0)
    loud_sig = (rng.normal(0, 0.5, SR)).astype(np.float32)
    quiet_sig = (rng.normal(0, 0.005, SR)).astype(np.float32)
    assert float(make_env(loud_sig, SR, FPS).mean()) > float(
        make_env(quiet_sig, SR, FPS).mean()
    )


def test_values_in_unit_range():
    rng = np.random.default_rng(1)
    sig = rng.normal(0, 0.3, SR).astype(np.float32)
    env = make_env(sig, SR, FPS)
    assert float(env.min()) >= 0.0
    assert float(env.max()) <= 1.0


def test_release_tail_decays_not_to_zero():
    # A single loud burst at the start should leave a decaying release tail
    # (each frame >= 0.48 * previous forward, 0.32 * next backward).
    sig = np.zeros(SR, dtype=np.float32)
    sig[: SR // FPS] = 0.9  # loud only in the first frame window
    env = make_env(sig, SR, FPS)
    assert env[0] > 0.3
    # forward attack smoothing keeps subsequent frames above a decayed floor
    assert env[1] >= 0.48 * env[0] - 1e-6
