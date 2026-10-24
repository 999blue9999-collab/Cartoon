"""Smoke test confirming the pipeline package and its deps import cleanly.

This placeholder establishes a green pytest baseline for the scaffolding
commit. Real unit tests (config validation, SRT formatting, RMS envelope
math, cover-crop geometry, camera smoothstep) land in later features.
"""


def test_package_imports():
    import pipeline

    assert pipeline.__version__


def test_runtime_dependencies_import():
    import imageio_ffmpeg
    import numpy  # noqa: F401
    import PIL  # noqa: F401

    # The bundled ffmpeg binary must be resolvable without a system install.
    assert imageio_ffmpeg.get_ffmpeg_exe()
