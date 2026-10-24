"""cover() geometry + camera_at() smoothstep tests (tiny synthetic images)."""
from PIL import Image

from pipeline.render import camera_at, cover


def test_cover_landscape_returns_target_size():
    im = Image.new("RGB", (200, 50), (10, 20, 30))  # very wide
    out = cover(im, 128, 72)
    assert out.size == (128, 72)


def test_cover_portrait_returns_target_size():
    im = Image.new("RGB", (50, 200), (10, 20, 30))  # very tall
    out = cover(im, 128, 72)
    assert out.size == (128, 72)


def test_cover_square_to_landscape():
    im = Image.new("RGB", (100, 100), (0, 0, 0))
    out = cover(im, 160, 90)
    assert out.size == (160, 90)


def test_camera_at_before_first_mark():
    marks = [(0.0, 0.5, 0.5, 1.0), (10.0, 0.2, 0.8, 2.0)]
    assert camera_at(marks, -5.0) == (0.5, 0.5, 1.0)


def test_camera_at_after_last_mark():
    marks = [(0.0, 0.5, 0.5, 1.0), (10.0, 0.2, 0.8, 2.0)]
    assert camera_at(marks, 99.0) == (0.2, 0.8, 2.0)


def test_camera_at_midpoint_is_smoothstep():
    marks = [(0.0, 0.0, 0.0, 1.0), (10.0, 1.0, 1.0, 2.0)]
    fx, fy, zoom = camera_at(marks, 5.0)
    # smoothstep(0.5) = 0.5, so halfway linearly too, but verify easing shape:
    # at t=2.5 -> u=0.25 -> smoothstep = 0.15625
    fx2, _, _ = camera_at(marks, 2.5)
    assert abs(fx2 - 0.15625) < 1e-6
    # midpoint exactly 0.5 for all three channels
    assert abs(fx - 0.5) < 1e-9
    assert abs(fy - 0.5) < 1e-9
    assert abs(zoom - 1.5) < 1e-9


def test_camera_at_empty_marks_defaults():
    assert camera_at([], 3.0) == (0.5, 0.5, 1.0)
